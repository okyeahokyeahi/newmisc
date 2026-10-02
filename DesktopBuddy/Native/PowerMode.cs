using System.Runtime.InteropServices;

namespace DesktopBuddy.Native;

/// <summary>Windows 11's power mode (Settings > System > Power > Power mode).</summary>
internal static class PowerMode
{
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    public static Guid? Current()
    {
        try
        {
            return PowerGetEffectiveOverlayScheme(out Guid g) == 0 ? g : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static bool Set(Guid overlay)
    {
        try
        {
            return PowerSetActiveOverlayScheme(overlay) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Starts a program as the normal (non-admin) user by handing it to Explorer.</summary>
    public static void StartAsUser(string exePath)
    {
        System.Diagnostics.Process.Start("explorer.exe", $"\"{exePath}\"");
    }
}
