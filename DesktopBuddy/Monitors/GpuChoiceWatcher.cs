using System.Diagnostics;
using DesktopBuddy.Native;
using Microsoft.Win32;

namespace DesktopBuddy.Monitors;

public sealed record WrongGpuFinding(string AppName, string ExePath, string UsingChip, string BetterChip);

/// <summary>
/// Laptops have two graphics chips; Windows sometimes runs a game on the weak Intel one instead of
/// the RTX, which quietly halves FPS. This watches games and Roblox Studio and says so, and can set
/// Windows' per-app "High performance" graphics preference. Apps you fixed stay fixed: when Roblox
/// updates into a new folder, the preference is copied to the new exe automatically.
/// </summary>
public sealed class GpuChoiceWatcher(Settings settings, ResourceMonitor resources) : IDisposable
{
    private const string PreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const int SamplesNeeded = 12; // ~60 s at the 5 s poll

    private readonly GpuProcessCounters _gpu = new();
    private readonly Dictionary<string, int> _onWeakChip = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoFixedPaths = new(StringComparer.OrdinalIgnoreCase);

    public event Action<WrongGpuFinding>? WrongChip;

    /// <summary>Raised after a preference was copied to a new version of an app you fixed before.</summary>
    public event Action<string>? AutoFixed;

    public void Tick()
    {
        IReadOnlyList<GraphicsAdapter> chips = GraphicsAdapters.List();
        GraphicsAdapter? strong = chips.FirstOrDefault(c => c.IsDedicated);
        GraphicsAdapter? weak = chips.FirstOrDefault(c => c.IsIntel);
        if (strong == null || weak == null) return; // not a two-chip laptop (or the RTX is switched off)

        // Which watched apps are running, and where.
        var watched = new Dictionary<int, (string Name, string Path)>();
        foreach (AppUsage app in resources.Latest?.AllApps ?? [])
        {
            if (!IsWatched(app.Name)) continue;
            foreach (var (pid, path) in Pids(app.Name)) watched[pid] = (app.Name, path);
        }
        if (watched.Count == 0)
        {
            _onWeakChip.Clear();
            return;
        }

        KeepFixesAcrossUpdates(watched.Values);

        var usage = new Dictionary<string, (double Weak, double Strong, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, luid, percent) in _gpu.SampleByAdapter())
        {
            if (!watched.TryGetValue(pid, out var app)) continue;
            usage.TryGetValue(app.Name, out var u);
            if (luid == weak.Luid) u.Weak = Math.Max(u.Weak, percent);
            else if (luid == strong.Luid) u.Strong = Math.Max(u.Strong, percent);
            usage[app.Name] = (u.Weak, u.Strong, app.Path);
        }

        foreach (var (name, (weakPct, strongPct, path)) in usage)
        {
            // On Optimus laptops a game on the RTX still shows some Intel 3D work (copying frames to the screen),
            // so only count it when the RTX is basically idle for this app and the Intel chip is clearly busy.
            bool onWeak = weakPct >= 25 && strongPct < 2;
            _onWeakChip[name] = onWeak ? _onWeakChip.GetValueOrDefault(name) + 1 : 0;
            if (_onWeakChip[name] < SamplesNeeded || !_reported.Add(path)) continue;

            Log.Info($"{name} is rendering on {weak.Name} ({weakPct:0}%) instead of {strong.Name} ({strongPct:0}%)");
            WrongChip?.Invoke(new WrongGpuFinding(name, path, weak.Name, strong.Name));
        }
    }

    private bool IsWatched(string name) =>
        settings.GameProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.Equals("RobloxStudioBeta", StringComparison.OrdinalIgnoreCase) ||
        settings.ForceRtxApps.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets Windows' "High performance" graphics preference for this exe (applies next time it starts).</summary>
    public void UseStrongChip(string appName, string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesKey);
        key.SetValue(exePath, "GpuPreference=2;");
        if (!settings.ForceRtxApps.Contains(appName, StringComparer.OrdinalIgnoreCase))
        {
            settings.ForceRtxApps.Add(appName);
            settings.Save();
        }
        Log.Info($"Set High performance graphics for {exePath}");
    }

    /// <summary>Roblox installs each update into a new versions\version-xxxx folder; copy the preference along.</summary>
    private void KeepFixesAcrossUpdates(IEnumerable<(string Name, string Path)> running)
    {
        foreach (var (name, path) in running.DistinctBy(r => r.Path))
        {
            if (!settings.ForceRtxApps.Contains(name, StringComparer.OrdinalIgnoreCase) || _autoFixedPaths.Contains(path)) continue;
            _autoFixedPaths.Add(path);
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesKey);
                if (key.GetValue(path) is string existing && existing.Contains("GpuPreference=2", StringComparison.Ordinal)) continue;
                key.SetValue(path, "GpuPreference=2;");
                Log.Info($"Copied High performance graphics to new version: {path}");
                AutoFixed?.Invoke(name);
            }
            catch (Exception ex)
            {
                Log.Error($"Setting graphics preference for {path} failed", ex);
            }
        }
    }

    private static IEnumerable<(int Pid, string Path)> Pids(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (Process p in processes)
            {
                string? path = NativeMethods.GetProcessPath(p.Id);
                if (path != null) yield return (p.Id, path);
            }
        }
        finally
        {
            foreach (Process p in processes) p.Dispose();
        }
    }

    public void Dispose() => _gpu.Dispose();
}
