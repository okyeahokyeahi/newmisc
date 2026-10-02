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
        if (!TryReadAll(out Dictionary<string, string> current)) return; // partial read: don't save or compare it
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

        bool isNew = !_known!.ContainsKey(key);
        string label = place switch
        {
            StartupPlace.ScheduledTask => "scheduled task",
            StartupPlace.Service => "Windows service",
            _ => "Startup-folder item",
        };
        string what = $"{(isNew ? "New" : "Changed")} {label} \"{Short(name, 50)}\"";

        // Startup folder: rare enough to always mention.
        if (place == StartupPlace.StartupFolder)
        {
            Alert?.Invoke(new HiddenStartupAlert(place, "New startup item",
                $"{what} starts with Windows: {Short(command, 90)}. Didn't install anything? Click to look."));
            return;
        }

        var (exe, args) = SplitCommand(command);
        if (exe == null) return;
        string exeName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();

        string? reason = null;
        if (ScriptHosts.Contains(exeName) &&
            SuspiciousArgumentHints.Any(h => args.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"uses {exeName} to run something from a user folder or the internet";
        }
        else if (File.Exists(exe) && !FileTrust.IsInProtectedFolder(exe) && !FileTrust.IsSigned(exe))
        {
            reason = $"runs an unsigned program from {FileTrust.DescribeLocation(exe)}";
        }

        if (reason == null) return; // looks like a normal updater; logged above

        Alert?.Invoke(new HiddenStartupAlert(place, "Something set itself to auto-start",
            $"{what} {reason}: {Short(command, 80)}. Info-stealers hide like this. Didn't install anything? Click to review."));
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
    private bool TryReadAll(out Dictionary<string, string> all)
    {
        all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return ReadTasks(all) & ReadServices(all) & ReadStartupFolders(all);
    }

    private static bool ReadTasks(Dictionary<string, string> into)
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(TasksRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Listing scheduled tasks failed", ex);
            return false;
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
        return true;
    }

    private static bool ReadServices(Dictionary<string, string> into)
    {
        try
        {
            using RegistryKey? services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null) return false;
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
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Listing services failed", ex);
            return false;
        }
    }

    private bool ReadStartupFolders(Dictionary<string, string> into)
    {
        bool ok = true;
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
                ok = false;
            }
        }
        return ok;
    }

    private readonly Dictionary<string, (DateTime Modified, string? Target)> _shortcutCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a .lnk points. Cached per file version so COM isn't spun up every minute.</summary>
    private string? ShortcutTarget(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        DateTime modified = File.GetLastWriteTimeUtc(file);
        if (_shortcutCache.TryGetValue(file, out var cached) && cached.Modified == modified) return cached.Target;

        object? shell = null, shortcut = null;
        string? result = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                shell = Activator.CreateInstance(shellType)!;
                shortcut = ((dynamic)shell).CreateShortcut(file);
                string target = ((dynamic)shortcut).TargetPath;
                string args = ((dynamic)shortcut).Arguments;
                result = $"{target} {args}".Trim();
            }
        }
        catch
        {
            // not a readable shortcut
        }
        finally
        {
            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        _shortcutCache[file] = (modified, result);
        return result;
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
            // Unquoted paths can contain spaces ("C:\Program Files\x\y.cmd"), so cut after the first known extension.
            int cut = -1;
            foreach (string ext in new[] { ".exe", ".cmd", ".bat", ".ps1", ".vbs", ".js", ".com" })
            {
                int i = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (cut < 0 || i + ext.Length < cut)) cut = i + ext.Length;
            }
            if (cut < 0) cut = c.IndexOf(' ') is int sp and >= 0 ? sp : c.Length;
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

    private static string Short(string text, int max) => text.Length > max ? text[..(max - 3)] + "..." : text;

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
