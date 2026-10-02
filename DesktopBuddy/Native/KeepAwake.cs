using System.Runtime.InteropServices;

namespace DesktopBuddy.Native;

/// <summary>Stops the PC sleeping / the screen turning off (for AFK sessions, big downloads, Studio publishes).</summary>
internal static class KeepAwake
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>When keep-awake ends; DateTime.MaxValue = until turned off; null = off.</summary>
    public static DateTime? Until { get; private set; }

    /// <summary>Must be called on the UI thread (the setting belongs to the calling thread).</summary>
    public static void Set(TimeSpan? duration)
    {
        if (duration == null)
        {
            SetThreadExecutionState(ES_CONTINUOUS);
            Until = null;
            Log.Info("Keep awake: off");
            return;
        }
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        Until = duration == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.Now + duration.Value;
        Log.Info($"Keep awake until {Until}");
    }

    /// <summary>Called every second from the UI timer to switch off when time is up.</summary>
    public static bool ExpireIfDue()
    {
        if (Until is DateTime until && until != DateTime.MaxValue && DateTime.Now >= until)
        {
            Set(null);
            return true;
        }
        return false;
    }
}
