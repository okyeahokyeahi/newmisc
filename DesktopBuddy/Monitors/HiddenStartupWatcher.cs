using System.Text.Json;
using System.Xml.Linq;
using DesktopBuddy.Native;
using Microsoft.Win32;

namespace DesktopBuddy.Monitors;

/// <summary>Where a new auto-start entry was found, so the alert can open the right Windows tool.</summary>
public enum StartupPlace { ScheduledTask, Service, StartupFolder, Winlogon }

public sealed record HiddenStartupAlert(StartupPlace Place, string Title, string Message);

/// <summary>
/// Info-stealers mostly hide in scheduled tasks and services now, not the Run keys. This watches
/// scheduled tasks, services, the Startup folders and the Winlogon shell. Entries are remembered on
/// disk, so ones added while Desktop Buddy was closed are caught on the next start.
///
/// Noise control: Chrome/Edge/NVIDIA updaters create tasks all the time, so a new entry only alerts when
/// it runs an unsigned program outside Program Files/Windows, or a script tool (PowerShell, cmd, mshta…)
/// pointed at a user folder or the internet. Everything else is just logged.
/// </summary>
public sealed class HiddenStartupWatcher
{
    private static readonly string BaselinePath = Path.Combine(Settings.Folder, "startup-baseline.json");
    private static readonly string TasksRoot = Path.Combine(FileTrust.WindowsDir, "System32", "Tasks");
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly string[] ScriptHosts =
        ["powershell", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "bitsadmin", "certutil", "curl"];

    private static readonly string[] SuspiciousArgumentHints =
        ["\\appdata\\", "\\temp\\", "\\downloads\\", "\\users\\public\\", "http:", "https:", "-enc", "-encodedcommand", "frombase64", "-w hidden", "-windowstyle hidden"];

    private Dictionary<string, string>? _known; // key -> command
    private bool _winlogonWarned;

    public event Action<HiddenStartupAlert>? Alert;

    public void Tick()
    {
        Dictionary<string, string> current = ReadAll();
        bool firstEverRun = false;
        if (_known == null)
        {
            _known = LoadBaseline();
            if (_known == null)
            {
                firstEverRun = true; // just learn what's there, don't flood the user
                _known = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
            }
        }

        if (!firstEverRun)
        {
            foreach (var (key, command) in current)
            {
                if (_known.TryGetValue(key, out string? old) && old == command) continue;
                Log.Info($"New/changed auto-start entry {key} -> {command}");
                Evaluate(key, command);
            }
        }

        bool changed = current.Count != _known.Count ||
                       current.Any(kv => !_known.TryGetValue(kv.Key, out string? v) || v != kv.Value);
        if (firstEverRun || changed)
        {
            _known = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
            SaveBaseline(_known);
        }

        CheckWinlogon();
    }

    private void Evaluate(string key, string command)
    {
        int sep = key.IndexOf('|');
        string kind = key[..sep], name = key[(sep + 1)..];
        StartupPlace place = kind switch
        {
            "task" => StartupPlace.ScheduledTask,
            "service" => StartupPlace.Service,
            _ => StartupPlace.StartupFolder,
        };

        string what = place switch
        {
            StartupPlace.ScheduledTask => $"A new scheduled task \"{name}\"",
            StartupPlace.Service => $"A new Windows service \"{name}\"",
            _ => $"A new Startup-folder item \"{name}\"",
        };

        // Startup folder: rare enough to always mention.
        if (place == StartupPlace.StartupFolder)
        {
            Alert?.Invoke(new HiddenStartupAlert(place, "New startup item",
                $"{what} will start with Windows: {Short(command)}. If you didn't just install something, click to look."));
            return;
        }

        var (exe, args) = SplitCommand(command);
        if (exe == null) return;
        string exeName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();

        string? reason = null;
        if (ScriptHosts.Contains(exeName) &&
            SuspiciousArgumentHints.Any(h => args.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"it uses {exeName} to run something from a user folder or the internet";
        }
        else if (File.Exists(exe) && !FileTrust.IsInProtectedFolder(exe) && !FileTrust.IsSigned(exe))
        {
            reason = $"it runs an unsigned program from {FileTrust.DescribeLocation(exe)}";
        }

        if (reason == null) return; // looks like a normal updater; logged above

        Alert?.Invoke(new HiddenStartupAlert(place, "Something set itself to auto-start",
            $"{what} was created and {reason}: {Short(command)}. Info-stealers hide like this. " +
            "If you didn't just install something, click to review it."));
    }

    private void CheckWinlogon()
    {
        if (_winlogonWarned) return;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            string shell = key?.GetValue("Shell")?.ToString()?.Trim() ?? "explorer.exe";
            string userinit = key?.GetValue("Userinit")?.ToString()?.Trim().TrimEnd(',') ?? "";

            bool shellOk = shell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
            bool userinitOk = userinit.Length == 0 ||
                Path.GetFileName(userinit).Equals("userinit.exe", StringComparison.OrdinalIgnoreCase) && !userinit.Contains(',');
            if (shellOk && userinitOk) return;

            _winlogonWarned = true;
            Alert?.Invoke(new HiddenStartupAlert(StartupPlace.Winlogon, "Windows login was modified",
                $"The Windows login settings were changed to also run: {(shellOk ? userinit : shell)}. Malware does this to start " +
                "before everything else. Run a full Defender scan (or Defender Offline scan) now."));
        }
        catch (Exception ex)
        {
            Log.Error("Reading Winlogon failed", ex);
        }
    }

    // ---------- Reading ----------
    private static Dictionary<string, string> ReadAll()
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadTasks(all);
        ReadServices(all);
        ReadStartupFolders(all);
        return all;
    }

    private static void ReadTasks(Dictionary<string, string> into)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(TasksRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
        }
        catch (Exception ex)
        {
            Log.Error("Listing scheduled tasks failed", ex);
            return;
        }

        foreach (string file in files)
        {
            try
            {
                XElement? exec = XDocument.Load(file).Descendants(TaskNs + "Exec").FirstOrDefault();
                if (exec == null) continue; // COM-handler tasks: Windows internals
                string command = exec.Element(TaskNs + "Command")?.Value.Trim() ?? "";
                string args = exec.Element(TaskNs + "Arguments")?.Value.Trim() ?? "";
                if (command.Length == 0) continue;
                string commandLine = command.Contains(' ') && !command.StartsWith('"') ? $"\"{command}\" {args}" : $"{command} {args}";
                into[$"task|{Path.GetRelativePath(TasksRoot, file)}"] = commandLine.Trim();
            }
            catch
            {
                // unreadable or not a task file
            }
        }
    }

    private static void ReadServices(Dictionary<string, string> into)
    {
        try
        {
            using RegistryKey? services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null) return;
            foreach (string name in services.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? svc = services.OpenSubKey(name);
                    if (svc?.GetValue("Type") is not int type) continue;
                    // 0x10 own process, 0x20 shared process (+0x100 interactive). Skips drivers and per-user service copies.
                    if ((type & ~0x100) is not (0x10 or 0x20)) continue;
                    string? image = svc.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                    if (string.IsNullOrWhiteSpace(image)) continue;
                    into[$"service|{name}"] = NormalizeServicePath(image);
                }
                catch
                {
                    // access denied on a few protected services
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing services failed", ex);
        }
    }

    private static void ReadStartupFolders(Dictionary<string, string> into)
    {
        foreach (string folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                 })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    into[$"startup|{Path.GetFileName(file)}"] = ShortcutTarget(file) ?? file;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Listing {folder} failed", ex);
            }
        }
    }

    private static string? ShortcutTarget(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(file);
            string target = shortcut.TargetPath;
            string args = shortcut.Arguments;
            return $"{target} {args}".Trim();
        }
        catch
        {
            return null;
        }
    }

    // ---------- Helpers ----------
    private static string NormalizeServicePath(string image)
    {
        string p = image.Trim();
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = FileTrust.WindowsDir + p[12..];
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        else if (p.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)) p = FileTrust.WindowsDir + p;
        return p;
    }

    /// <summary>Splits "C:\x\y.exe" -args into the exe path and its arguments.</summary>
    internal static (string? Exe, string Args) SplitCommand(string command)
    {
        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        string exe, args;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            if (end < 0) return (null, "");
            exe = c[1..end];
            args = c[(end + 1)..].Trim();
        }
        else
        {
            int exeEnd = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int cut = exeEnd >= 0 ? exeEnd + 4 : (c.IndexOf(' ') is int sp and >= 0 ? sp : c.Length);
            exe = c[..cut];
            args = c[cut..].Trim();
        }

        if (!Path.IsPathRooted(exe))
        {
            string inSystem32 = Path.Combine(FileTrust.WindowsDir, "System32", exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe");
            exe = File.Exists(inSystem32) ? inSystem32 : exe;
        }
        return (exe, args);
    }

    private static string Short(string text) => text.Length > 120 ? text[..117] + "..." : text;

    private static Dictionary<string, string>? LoadBaseline()
    {
        try
        {
            if (!File.Exists(BaselinePath)) return null;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BaselinePath));
            return map == null ? null : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Reading startup baseline failed", ex);
            return null;
        }
    }

    private static void SaveBaseline(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(BaselinePath, JsonSerializer.Serialize(map));
        }
        catch (Exception ex)
        {
            Log.Error("Saving startup baseline failed", ex);
        }
    }
}
