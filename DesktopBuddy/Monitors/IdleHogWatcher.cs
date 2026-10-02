using System.Diagnostics;
using DesktopBuddy.Knowledge;
using DesktopBuddy.Native;

namespace DesktopBuddy.Monitors;

public sealed record IdleHogFinding(string Name, string? ExePath, string What, TimeSpan Duration);

/// <summary>
/// Hidden crypto-miners often pause while you use the PC and run when you walk away. While you're
/// idle (no mouse/keyboard for a few minutes) this watches for programs using lots of GPU or CPU,
/// then tells you when you come back.
///
/// Noise control: signed programs (Steam updates, Roblox Studio, Discord) and Windows' own components
/// (Defender scans, Windows Update) are ignored; script tools like PowerShell are always checked.
/// </summary>
public sealed class IdleHogWatcher(Settings settings, ResourceMonitor resources) : IDisposable
{
    private static readonly string[] AlwaysCheck = ["powershell", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32"];

    private readonly GpuProcessCounters _gpu = new();
    private readonly Dictionary<string, (DateTime Since, string What)> _busySince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IdleHogFinding> _findings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _trustedCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _wasIdle;

    /// <summary>Raised when you return, with everything that was busy while you were away.</summary>
    public event Action<IReadOnlyList<IdleHogFinding>>? Report;

    /// <param name="gaming">Skip while a game is running (AFK in a game isn't suspicious).</param>
    public void Tick(bool gaming)
    {
        bool idle = NativeMethods.IdleTime() >= TimeSpan.FromMinutes(settings.IdleMinutes) && !gaming;

        if (!idle)
        {
            if (_wasIdle && _findings.Count > 0) Report?.Invoke(_findings.Values.ToList());
            _wasIdle = false;
            _busySince.Clear();
            _findings.Clear();
            return;
        }

        _wasIdle = true;
        DateTime now = DateTime.UtcNow;
        var busyNow = new Dictionary<string, (string What, string? Path)>(StringComparer.OrdinalIgnoreCase);

        // GPU, per process
        foreach (var (pid, percent) in _gpu.Sample())
        {
            if (percent < settings.IdleGpuPercent) continue;
            string? path = NativeMethods.GetProcessPath(pid);
            if (path == null) continue;
            busyNow[Path.GetFileNameWithoutExtension(path)] = ($"{percent:0}% GPU", path);
        }

        // CPU, per app (from the resource monitor's latest sample)
        foreach (AppUsage app in resources.Latest?.AllApps ?? [])
        {
            if (app.CpuPercent < settings.IdleCpuPercent) continue;
            string what = busyNow.TryGetValue(app.Name, out var existing)
                ? $"{existing.What} and {app.CpuPercent:0}% CPU"
                : $"{app.CpuPercent:0}% CPU";
            busyNow[app.Name] = (what, existing.Path ?? FirstPath(app.Name));
        }

        foreach (var (name, (what, path)) in busyNow)
        {
            if (!ShouldCheck(name, path)) continue;
            if (!_busySince.TryGetValue(name, out var since)) _busySince[name] = since = (now, what);
            TimeSpan duration = now - since.Since;
            if (duration >= TimeSpan.FromMinutes(2))
                _findings[name] = new IdleHogFinding(name, path, what, duration);
        }

        foreach (string gone in _busySince.Keys.Where(n => !busyNow.ContainsKey(n)).ToList())
            _busySince.Remove(gone);
    }

    private bool ShouldCheck(string name, string? path)
    {
        if (settings.ResourceIgnoreList.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (AlwaysCheck.Contains(name.ToLowerInvariant())) return true;
        if (KnownProcesses.IsWindowsChore(name)) return false;
        if (path == null) return false;

        if (!_trustedCache.TryGetValue(path, out bool trusted))
            _trustedCache[path] = trusted = FileTrust.IsTrustedPublisher(path); // miners are almost always unsigned
        return !trusted;
    }

    private static string? FirstPath(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Select(p => NativeMethods.GetProcessPath(p.Id)).FirstOrDefault(p => p != null);
        }
        finally
        {
            foreach (Process p in processes) p.Dispose();
        }
    }

    public void Dispose() => _gpu.Dispose();
}
