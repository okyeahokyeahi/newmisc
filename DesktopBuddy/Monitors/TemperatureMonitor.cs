using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace DesktopBuddy.Monitors;

public sealed record TemperatureSnapshot(double? CpuC, double? GpuC, string? CpuName, string? GpuName);

/// <summary>Reads CPU/GPU temperatures through LibreHardwareMonitor. Read-only: it never changes fan speeds.</summary>
public sealed class TemperatureMonitor(Settings settings) : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private readonly Dictionary<string, DateTime> _lastAlert = [];
    private readonly Dictionary<string, DateTime> _overSince = [];
    private bool _opened;

    public TemperatureSnapshot? Latest { get; private set; }

    /// <summary>Set when the sensor library couldn't start (shown in the status window).</summary>
    public string? Problem { get; private set; }

    /// <summary>True when CPU or GPU has been over its limit for TempSustainedSeconds.</summary>
    public bool SustainedHot => _overSince.Values.Any(since =>
        DateTime.UtcNow - since >= TimeSpan.FromSeconds(settings.TempSustainedSeconds));

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

        Problem = cpu != null ? null
            : !PawnIoInstalled() ? "CPU temp needs the free PawnIO driver: run \"winget install namazso.PawnIO\" (or get it from pawnio.eu), then restart Desktop Buddy."
            : "CPU temp unavailable. Make sure Desktop Buddy runs as administrator.";

        Latest = new TemperatureSnapshot(cpu, gpu, cpuName, gpuName);
        CheckAlert("CPU", cpu, settings.CpuTempWarnC);
        CheckAlert("GPU", gpu, settings.GpuTempWarnC);
    }

    /// <summary>LibreHardwareMonitor reads Intel CPU temps through the PawnIO driver, which is a separate install.</summary>
    private static bool PawnIoInstalled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch
        {
            return true; // can't tell; don't send the user on a wild goose chase
        }
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
        DateTime now = DateTime.UtcNow;
        if (celsius == null || celsius < limit)
        {
            _overSince.Remove(part);
            return;
        }

        _overSince.TryAdd(part, now);
        if (now - _overSince[part] < TimeSpan.FromSeconds(settings.TempSustainedSeconds)) return;
        if (_lastAlert.TryGetValue(part, out DateTime last) && now - last < TimeSpan.FromMinutes(settings.AlertCooldownMinutes))
            return;

        _lastAlert[part] = now;
        bool onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        string advice = onBattery
            ? "You're on battery; plug in or close heavy apps."
            : "Consider switching NitroSense to a higher fan mode or closing heavy apps.";
        Alert?.Invoke($"{part} is hot: {celsius:0}°C",
            $"Your {part} has been over your {limit:0}°C limit for {settings.TempSustainedSeconds}+ seconds. {advice}");
    }

    public void Dispose()
    {
        if (_opened) _computer.Close();
    }
}
