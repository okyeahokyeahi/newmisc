using System.Runtime.InteropServices;

namespace DesktopBuddy.Monitors;

public sealed record RefreshRateFinding(string Device, string Name, int Width, int Height, int CurrentHz, int BestHz);

/// <summary>
/// Spots a screen running below its best refresh rate (e.g. stuck at 60 Hz on a 144 Hz laptop panel,
/// common after driver updates or plugging in a monitor). Only changes anything when you click.
/// </summary>
public sealed class RefreshRateMonitor
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x1;
    private const uint CDS_TEST = 0x2;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE mode, IntPtr hwnd, uint flags, IntPtr param);

    private readonly HashSet<string> _reported = [];

    /// <summary>Screens currently below their best refresh rate at their current resolution (new ones only).</summary>
    public List<RefreshRateFinding> Check()
    {
        var findings = new List<RefreshRateFinding>();
        foreach (var (device, name) in ActiveDisplays())
        {
            DEVMODE current = NewMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref current)) continue;

            int best = 0;
            DEVMODE mode = NewMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref mode); i++)
            {
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight &&
                    mode.dmBitsPerPel == current.dmBitsPerPel)
                    best = Math.Max(best, mode.dmDisplayFrequency);
                mode = NewMode();
            }

            // Only worth mentioning when there's a real gap (60 -> 120/144/165), not 59 vs 60.
            if (best < 90 || best - current.dmDisplayFrequency < 15) continue;
            string key = $"{device}|{current.dmDisplayFrequency}|{best}";
            if (!_reported.Add(key)) continue;

            findings.Add(new RefreshRateFinding(device, name, current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency, best));
        }
        return findings;
    }

    /// <summary>Switches the screen to the given refresh rate (tested first). Returns false if Windows refused.</summary>
    public static bool Apply(RefreshRateFinding f)
    {
        DEVMODE mode = NewMode();
        if (!EnumDisplaySettings(f.Device, ENUM_CURRENT_SETTINGS, ref mode)) return false;
        mode.dmDisplayFrequency = f.BestHz;
        mode.dmFields = DM_DISPLAYFREQUENCY;
        if (ChangeDisplaySettingsEx(f.Device, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != 0) return false;
        return ChangeDisplaySettingsEx(f.Device, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
    }

    private static IEnumerable<(string Device, string Name)> ActiveDisplays()
    {
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) yield break;
            if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string name = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) && monitor.DeviceString.Length > 0
                ? monitor.DeviceString
                : adapter.DeviceName;
            yield return (adapter.DeviceName, name);
        }
    }

    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
}
