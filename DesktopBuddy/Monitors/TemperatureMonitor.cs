using LibreHardwareMonitor.Hardware;

namespace DesktopBuddy.Monitors;

public sealed record TemperatureSnapshot(double? CpuC, double? GpuC, string? CpuName, string? GpuName);

/// <summary>Reads CPU/GPU temperatures through LibreHardwareMonitor. Read-only: it never changes fan speeds.</summary>
public sealed class TemperatureMonitor(Settings settings) : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private readonly Dictionary<string, DateTime> _lastAlert = [];
    private bool _opened;

    public TemperatureSnapshot? Latest { get; private set; }

    /// <summary>Set when the sensor library couldn't start (shown in the status window).</summary>
    public string? Problem { get; private set; }

    /// <summary>(title, message)</summary>
    public event Action<string, string>? Alert;

    /// <summary>Slow (loads a driver), so call it from a background thread.</summary>
    public void Open()
    {
        try
        {
            _computer.Open();
            _opened = true;
        }
        catch (Exception ex)
        {
            Problem = "Temperature sensors unavailable: " + ex.Message;
            Log.Error("Opening LibreHardwareMonitor failed", ex);
        }
    }

    public void Tick()
    {
        if (!_opened) return;

        double? cpu = null, gpu = null;
        string? cpuName = null, gpuName = null;
        int bestGpuRank = -1;

        foreach (IHardware hardware in _computer.Hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware) sub.Update();

            var temps = hardware.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150)
                .ToList();
            if (temps.Count == 0) continue;

            if (hardware.HardwareType == HardwareType.Cpu)
            {
                cpu = Pick(temps, "CPU Package", "Core Max", "Core Average");
                cpuName = hardware.Name;
            }
            else if (GpuRank(hardware.HardwareType) is int rank and >= 0 && rank > bestGpuRank)
            {
                // Prefer the dedicated GPU (the RTX card) over the Intel integrated one.
                bestGpuRank = rank;
                gpu = Pick(temps, "GPU Core", "GPU Hot Spot");
                gpuName = hardware.Name;
            }
        }

        if (cpu == null && gpu == null && Problem == null)
            Problem = "No temperature sensors found. Make sure Desktop Buddy is running as administrator.";
        else if (cpu != null || gpu != null)
            Problem = null;

        Latest = new TemperatureSnapshot(cpu, gpu, cpuName, gpuName);
        CheckAlert("CPU", cpu, settings.CpuTempWarnC);
        CheckAlert("GPU", gpu, settings.GpuTempWarnC);
    }

    private static double? Pick(List<ISensor> temps, params string[] preferredNames)
    {
        foreach (string name in preferredNames)
        {
            ISensor? match = temps.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match?.Value != null) return match.Value;
        }
        return temps.Max(s => s.Value);
    }

    private static int GpuRank(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => 2,
        HardwareType.GpuAmd => 2,
        HardwareType.GpuIntel => 1,
        _ => -1,
    };

    private void CheckAlert(string part, double? celsius, double limit)
    {
        if (celsius == null || celsius < limit) return;
        DateTime now = DateTime.UtcNow;
        if (_lastAlert.TryGetValue(part, out DateTime last) && now - last < TimeSpan.FromMinutes(settings.AlertCooldownMinutes))
            return;

        _lastAlert[part] = now;
        bool onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        string advice = onBattery
            ? "You're on battery; plug in or close heavy apps."
            : "Consider switching NitroSense to a higher fan mode or closing heavy apps.";
        Alert?.Invoke($"{part} is hot: {celsius:0}°C", $"Your {part} passed your {limit:0}°C limit. {advice}");
    }

    public void Dispose()
    {
        if (_opened) _computer.Close();
    }
}
