using System.Diagnostics;
using DesktopBuddy.Native;

namespace DesktopBuddy.Monitors;

/// <summary>One "app" = all processes sharing a name (e.g. every chrome.exe added together).</summary>
public sealed record AppUsage(string Name, double CpuPercent, long MemoryBytes, int ProcessCount);

public sealed record ResourceSnapshot(
    double CpuPercent,
    uint RamPercent,
    ulong RamUsedBytes,
    ulong RamTotalBytes,
    IReadOnlyList<AppUsage> TopApps);

/// <summary>Samples per-app CPU and RAM and raises an alert when an app stays heavy for a while.</summary>
public sealed class ResourceMonitor(Settings settings)
{
    private Dictionary<int, (string Name, TimeSpan Cpu)> _previous = [];
    private DateTime _previousTime;
    private readonly Dictionary<string, DateTime> _heavySince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignored = new(settings.ResourceIgnoreList, StringComparer.OrdinalIgnoreCase);
    private DateTime? _ramFullSince;

    public ResourceSnapshot? Latest { get; private set; }

    /// <summary>(title, message)</summary>
    public event Action<string, string>? Alert;

    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        double elapsedMs = _previousTime == default ? 0 : (now - _previousTime).TotalMilliseconds;
        var current = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        var apps = new Dictionary<string, (double CpuMs, long Memory, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                TimeSpan cpu;
                long memory;
                try
                {
                    name = process.ProcessName;
                    cpu = process.TotalProcessorTime;
                    memory = process.WorkingSet64;
                }
                catch
                {
                    continue; // exited mid-scan or access denied
                }

                current[process.Id] = (name, cpu);
                double cpuDeltaMs = 0;
                if (_previous.TryGetValue(process.Id, out var old) && old.Name == name)
                    cpuDeltaMs = Math.Max(0, (cpu - old.Cpu).TotalMilliseconds);

                apps.TryGetValue(name, out var sum);
                apps[name] = (sum.CpuMs + cpuDeltaMs, sum.Memory + memory, sum.Count + 1);
            }
        }

        _previous = current;
        _previousTime = now;

        double capacityMs = elapsedMs * Environment.ProcessorCount;
        List<AppUsage> usage = apps
            .Where(a => !_ignored.Contains(a.Key))
            .Select(a => new AppUsage(a.Key, capacityMs > 0 ? a.Value.CpuMs / capacityMs * 100 : 0, a.Value.Memory, a.Value.Count))
            .ToList();

        var memoryStatus = new NativeMethods.MEMORYSTATUSEX();
        NativeMethods.GlobalMemoryStatusEx(memoryStatus);

        List<AppUsage> top = usage
            .OrderByDescending(a => a.CpuPercent)
            .ThenByDescending(a => a.MemoryBytes)
            .Take(8)
            .ToList();

        Latest = new ResourceSnapshot(
            Math.Min(100, usage.Sum(a => a.CpuPercent)),
            memoryStatus.dwMemoryLoad,
            memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys,
            memoryStatus.ullTotalPhys,
            top);

        if (capacityMs > 0)
        {
            CheckHeavyApps(usage, now);
            CheckRamFull(usage, memoryStatus.dwMemoryLoad, now);
        }
    }

    private void CheckHeavyApps(List<AppUsage> usage, DateTime now)
    {
        var sustained = TimeSpan.FromMinutes(settings.SustainedMinutes);
        var heavyNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AppUsage app in usage)
        {
            bool heavyCpu = app.CpuPercent >= settings.AppCpuPercentThreshold;
            bool heavyRam = app.MemoryBytes >= (long)settings.AppMemoryMbThreshold * 1024 * 1024;
            if (!heavyCpu && !heavyRam) continue;

            heavyNow.Add(app.Name);
            _heavySince.TryAdd(app.Name, now);
            if (now - _heavySince[app.Name] < sustained || !CooldownOver(app.Name, now)) continue;

            _lastAlert[app.Name] = now;
            string what = (heavyCpu, heavyRam) switch
            {
                (true, true) => $"{app.CpuPercent:0}% CPU and {Format.Bytes(app.MemoryBytes)} RAM",
                (true, false) => $"{app.CpuPercent:0}% CPU",
                _ => $"{Format.Bytes(app.MemoryBytes)} RAM",
            };
            string processes = app.ProcessCount > 1 ? $" ({app.ProcessCount} processes)" : "";
            Alert?.Invoke($"{app.Name} is working hard",
                $"{app.Name}{processes} has been using {what} for {settings.SustainedMinutes}+ minutes.");
        }

        foreach (string name in _heavySince.Keys.Where(n => !heavyNow.Contains(n)).ToList())
            _heavySince.Remove(name);
    }

    private void CheckRamFull(List<AppUsage> usage, uint ramPercent, DateTime now)
    {
        if (ramPercent < settings.RamPercentWarn)
        {
            _ramFullSince = null;
            return;
        }

        _ramFullSince ??= now;
        const string key = "<ram>";
        if (now - _ramFullSince < TimeSpan.FromMinutes(settings.SustainedMinutes) || !CooldownOver(key, now)) return;

        _lastAlert[key] = now;
        string biggest = string.Join(", ", usage.OrderByDescending(a => a.MemoryBytes).Take(3)
            .Select(a => $"{a.Name} {Format.Bytes(a.MemoryBytes)}"));
        Alert?.Invoke("RAM is almost full", $"Memory is {ramPercent}% used. Biggest users: {biggest}.");
    }

    private bool CooldownOver(string key, DateTime now) =>
        !_lastAlert.TryGetValue(key, out DateTime last) || now - last >= TimeSpan.FromMinutes(settings.AlertCooldownMinutes);
}
