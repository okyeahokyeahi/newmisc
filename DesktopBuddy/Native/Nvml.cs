using System.Runtime.InteropServices;

namespace DesktopBuddy.Native;

/// <summary>
/// NVIDIA Management Library (nvml.dll ships with the NVIDIA driver). Tells us *why* the GPU is
/// running slower than it could: heat, power limit, or the laptop's power brake.
/// </summary>
internal static class Nvml
{
    // nvmlClocksThrottleReasons / nvmlClocksEventReasons bits
    public const ulong GpuIdle = 0x1;
    public const ulong SwPowerCap = 0x4;           // normal at full load on a laptop
    public const ulong HwSlowdown = 0x8;           // hardware forced clocks down (heat or power brake)
    public const ulong SwThermalSlowdown = 0x20;   // driver slowing down because of heat
    public const ulong HwThermalSlowdown = 0x40;   // hardware slowing down because of heat
    public const ulong HwPowerBrakeSlowdown = 0x80; // power brake (charger / battery limit)

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int Init();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int GetHandleByIndex(uint index, out IntPtr device);

    // Renamed in newer drivers; try the new name first and fall back to the old one.
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")]
    private static extern int GetEventReasons(IntPtr device, out ulong reasons);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
    private static extern int GetThrottleReasons(IntPtr device, out ulong reasons);

    private static bool _useOldName;

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int GetUtilization(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int Shutdown();

    private static IntPtr _device;
    private static bool _ready;
    private static bool _triedInit;

    /// <summary>False if there's no NVIDIA driver (or it's too old).</summary>
    public static bool Available
    {
        get
        {
            if (_triedInit) return _ready;
            _triedInit = true;
            try
            {
                _ready = Init() == 0 && GetHandleByIndex(0, out _device) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _ready = false;
            }
            if (!_ready) Log.Info("NVML not available; GPU slowdown reasons won't be shown.");
            return _ready;
        }
    }

    /// <summary>(reasons bitmask, GPU busy %) or null if unavailable.</summary>
    public static (ulong Reasons, uint BusyPercent)? Read()
    {
        if (!Available) return null;
        try
        {
            ulong reasons;
            int status;
            try
            {
                status = _useOldName ? GetThrottleReasons(_device, out reasons) : GetEventReasons(_device, out reasons);
            }
            catch (EntryPointNotFoundException) when (!_useOldName)
            {
                _useOldName = true;
                status = GetThrottleReasons(_device, out reasons);
            }
            if (status != 0) return null;
            uint busy = GetUtilization(_device, out var u) == 0 ? u.Gpu : 0;
            return (reasons, busy);
        }
        catch (Exception ex)
        {
            Log.Error("NVML read failed; GPU slowdown reasons turned off", ex);
            _ready = false; // don't retry (and log) every few seconds
            return null;
        }
    }

    public static void Close()
    {
        if (_ready)
        {
            try { Shutdown(); } catch { /* exiting */ }
            _ready = false;
        }
    }
}
