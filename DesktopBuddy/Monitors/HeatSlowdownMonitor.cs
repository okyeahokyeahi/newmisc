using DesktopBuddy.Native;

namespace DesktopBuddy.Monitors;

public sealed record HeatSlowdownSnapshot(
    bool CpuSlowingNow,
    bool GpuSlowingNow,
    bool GpuPowerBrakeNow,
    TimeSpan CpuToday,
    TimeSpan GpuToday,
    bool GpuReasonsAvailable,
    bool CpuHeadroomAvailable);

/// <summary>
/// Detects "thermal throttling": the CPU/GPU deliberately slowing down because it's too hot,
/// which is what actually makes games stutter.
///  - GPU: exact, from the NVIDIA driver's own slowdown reasons (NVML).
///  - CPU: Intel CPUs slow down when they reach their heat limit (TjMax, 100°C on 11th gen).
///         We flag it when any core is within 2°C of that limit. A close estimate, not the CPU's internal flag.
/// </summary>
public sealed class HeatSlowdownMonitor(Settings settings, TemperatureMonitor temps)
{
    private const double CpuHeadroomLimitC = 2;
    private const double CpuFallbackLimitC = 98; // when no TjMax sensor is available

    private readonly Queue<bool> _cpuRecent = new();
    private readonly Queue<bool> _gpuRecent = new();
    private DateTime? _cpuSince, _gpuSince, _brakeSince;
    private readonly Dictionary<string, DateTime> _lastAlert = [];
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private TimeSpan _cpuToday, _gpuToday;
    private DateTime _lastTick = DateTime.UtcNow;

    public HeatSlowdownSnapshot? Latest { get; private set; }

    /// <summary>(title, message)</summary>
    public event Action<string, string>? Alert;

    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan elapsed = now - _lastTick;
        if (elapsed > TimeSpan.FromSeconds(30)) elapsed = TimeSpan.FromSeconds(5); // after sleep/hibernate
        _lastTick = now;

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _today)
        {
            _today = today;
            _cpuToday = _gpuToday = TimeSpan.Zero;
        }

        TemperatureSnapshot? t = temps.Latest;
        bool cpuSample = t?.CpuDistanceToTjMax is double headroom
            ? headroom <= CpuHeadroomLimitC
            : t?.CpuC >= CpuFallbackLimitC;

        bool gpuSample = false, brakeSample = false;
        var nvml = Nvml.Read();
        if (nvml is { } gpuState && gpuState.BusyPercent >= 10 && (gpuState.Reasons & Nvml.GpuIdle) == 0)
        {
            ulong reasons = gpuState.Reasons;
            bool thermal = (reasons & (Nvml.SwThermalSlowdown | Nvml.HwThermalSlowdown)) != 0;
            bool hwSlow = (reasons & Nvml.HwSlowdown) != 0;
            bool brake = (reasons & Nvml.HwPowerBrakeSlowdown) != 0;
            // "HW slowdown" can be heat or the power brake; decide by temperature.
            gpuSample = thermal || (hwSlow && t?.GpuC >= 80);
            brakeSample = brake || (hwSlow && !gpuSample);
        }

        // Smooth single-sample blips: "slowing" = at least 2 of the last 3 samples.
        bool cpuNow = Smooth(_cpuRecent, cpuSample);
        bool gpuNow = Smooth(_gpuRecent, gpuSample);
        if (cpuNow) _cpuToday += elapsed;
        if (gpuNow) _gpuToday += elapsed;

        Latest = new HeatSlowdownSnapshot(cpuNow, gpuNow, brakeSample, _cpuToday, _gpuToday,
            nvml != null, t?.CpuDistanceToTjMax != null);

        Track(ref _cpuSince, cpuNow, now, "cpu", "Your CPU is slowing down from heat",
            "It hit its 100°C limit and is cutting its own speed, which makes games stutter. Fixes: NitroSense > Max fan, " +
            "raise the back of the laptop, keep the vents clear. If this happens every time you play, the cooling paste may need replacing.");
        Track(ref _gpuSince, gpuNow, now, "gpu", "Your GPU is slowing down from heat",
            "The NVIDIA driver is cutting graphics speed because the GPU is too hot. Fixes: NitroSense > Max fan, " +
            "raise the back of the laptop, keep the vents clear, or lower graphics settings.");
        Track(ref _brakeSince, brakeSample, now, "brake", "Your GPU is being held back by power",
            "The laptop's power brake is limiting the GPU. Make sure you're using the original Acer charger (the big brick) " +
            "and it's firmly plugged in. Weaker USB-C chargers cause this.");
    }

    private static bool Smooth(Queue<bool> recent, bool sample)
    {
        recent.Enqueue(sample);
        while (recent.Count > 3) recent.Dequeue();
        return recent.Count(x => x) >= 2;
    }

    private void Track(ref DateTime? since, bool active, DateTime now, string key, string title, string message)
    {
        if (!active)
        {
            since = null;
            return;
        }

        since ??= now;
        if (now - since < TimeSpan.FromSeconds(settings.HeatSlowdownAlertSeconds)) return;
        if (_lastAlert.TryGetValue(key, out DateTime last) && now - last < TimeSpan.FromMinutes(settings.AlertCooldownMinutes)) return;

        _lastAlert[key] = now;
        Alert?.Invoke(title, message);
    }
}
