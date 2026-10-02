using System.Diagnostics;
using DesktopBuddy.Native;
using Microsoft.Win32;

namespace DesktopBuddy.Monitors;

public sealed record SuspiciousProcess(int Pid, string Name, string ExePath, IReadOnlyList<string> Reasons);

/// <summary>
/// Flags processes with red flags (fake system names, unsigned programs running from Temp/Downloads)
/// and new "start with Windows" entries. It only reports; you decide whether anything gets killed.
/// This is a junk/adware tripwire, not an antivirus.
/// </summary>
public sealed class SuspiciousProcessScanner(Settings settings)
{
    // Real Windows processes that malware likes to impersonate. All of them live under C:\Windows.
    private static readonly string[] SystemNames =
    [
        "svchost", "lsass", "csrss", "winlogon", "services", "smss", "wininit", "explorer", "spoolsv",
        "taskhostw", "dwm", "conhost", "rundll32", "dllhost", "sihost", "ctfmon", "runtimebroker", "searchhost",
    ];

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private static readonly (string Folder, string Label)[] RiskyFolders = BuildRiskyFolders();

    private readonly Dictionary<int, string> _seen = [];               // pid -> exe path already evaluated
    private readonly Dictionary<string, bool> _signatureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowed = new(settings.AllowedExePaths, StringComparer.OrdinalIgnoreCase);
    private readonly object _allowedGate = new();
    private Dictionary<string, string>? _startupBaseline;

    public int FlaggedCount { get; private set; }

    public event Action<SuspiciousProcess>? Flagged;

    /// <summary>(title, message)</summary>
    public event Action<string, string>? Alert;

    /// <summary>Called from the UI thread when you click "Always allow".</summary>
    public void Allow(string exePath)
    {
        lock (_allowedGate) _allowed.Add(exePath);
    }

    public void Tick()
    {
        ScanProcesses();
        if (settings.WatchStartupEntries) ScanStartupEntries();
    }

    private void ScanProcesses()
    {
        int ownPid = Environment.ProcessId;
        var alive = new HashSet<int>();

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                alive.Add(pid);
                if (pid == ownPid || pid <= 4) continue;

                string? path = NativeMethods.GetProcessPath(pid);
                if (path == null) continue; // protected process; Windows won't say where it lives
                if (_seen.TryGetValue(pid, out string? seenPath) && seenPath == path) continue;
                _seen[pid] = path;

                lock (_allowedGate)
                    if (_allowed.Contains(path)) continue;

                List<string> reasons = Evaluate(path);
                if (reasons.Count == 0) continue;

                FlaggedCount++;
                Log.Info($"Flagged {path} (pid {pid}): {string.Join("; ", reasons)}");
                Flagged?.Invoke(new SuspiciousProcess(pid, Path.GetFileName(path), path, reasons));
            }
        }

        foreach (int gone in _seen.Keys.Where(p => !alive.Contains(p)).ToList())
            _seen.Remove(gone);
    }

    private List<string> Evaluate(string path)
    {
        var reasons = new List<string>();
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        bool inWindowsFolder = path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);

        // Strong signal 1: real system name, wrong place.
        if (SystemNames.Contains(name) && !inWindowsFolder)
        {
            reasons.Add($"It uses the name of a Windows system process (\"{name}\") but runs from outside the Windows folder. Real ones never do.");
        }
        // Strong signal 2: a near-copy of a system name, e.g. "svch0st" or "lsasss".
        else if (!inWindowsFolder && !SystemNames.Contains(name) &&
                 SystemNames.FirstOrDefault(s => s.Length >= 5 && Levenshtein(name, s) == 1) is string original)
        {
            reasons.Add($"Its name \"{name}\" is one letter off the Windows process \"{original}\", a common disguise.");
        }

        // Weaker signal: an unsigned program running from a throwaway folder. Needs both parts.
        string? riskyFolder = RiskyFolders
            .Where(f => path.StartsWith(f.Folder, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Label)
            .FirstOrDefault();
        if (riskyFolder != null && !IsSigned(path))
        {
            reasons.Add($"It runs from {riskyFolder} and has no valid digital signature.");
        }
        else if (reasons.Count > 0 && !IsSigned(path))
        {
            reasons.Add("It has no valid digital signature.");
        }

        return reasons;
    }

    private bool IsSigned(string path)
    {
        if (_signatureCache.TryGetValue(path, out bool cached)) return cached;
        bool signed;
        try
        {
            signed = NativeMethods.HasValidEmbeddedSignature(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Signature check failed for {path}", ex);
            signed = true; // unknown -> don't accuse it
        }
        return _signatureCache[path] = signed;
    }

    private void ScanStartupEntries()
    {
        Dictionary<string, string> current = ReadStartupEntries();
        if (_startupBaseline == null)
        {
            _startupBaseline = current; // first run: remember what's already there
            return;
        }

        foreach (var (key, command) in current)
        {
            if (_startupBaseline.ContainsKey(key)) continue;
            string entryName = key[(key.LastIndexOf('|') + 1)..];
            Log.Info($"New startup entry {key} -> {command}");
            Alert?.Invoke("New startup program",
                $"\"{entryName}\" just set itself to start with Windows: {command}\n" +
                "If you didn't just install something, check Task Manager > Startup apps.");
        }
        _startupBaseline = current;
    }

    private static Dictionary<string, string> ReadStartupEntries()
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        (RegistryKey Root, string Path)[] locations =
        [
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        ];

        foreach (var (root, path) in locations)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key == null) continue;
                foreach (string valueName in key.GetValueNames())
                    entries[$@"{root.Name}\{path}|{valueName}"] = key.GetValue(valueName)?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                Log.Error($"Reading {root.Name}\\{path} failed", ex);
            }
        }
        return entries;
    }

    private static (string, string)[] BuildRiskyFolders()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Dir(string p) => p.TrimEnd('\\') + "\\";
        return
        [
            (Dir(Path.GetTempPath()), "your Temp folder"),
            (Dir(Path.Combine(WindowsDir, "Temp")), "the Windows Temp folder"),
            (Dir(Path.Combine(profile, "Downloads")), "your Downloads folder"),
            (Dir(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public"), "the Public user folder"),
            (Dir(Path.Combine(Path.GetPathRoot(WindowsDir)!, "$Recycle.Bin")), "the Recycle Bin"),
        ];
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
