using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopBuddy.Native;

/// <summary>Closes apps politely (like clicking X on each window); never kills.</summary>
internal static class AppCloser
{
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Posts WM_CLOSE to every visible top-level window owned by these apps.</summary>
    public static void CloseAllWindows(IEnumerable<string> names)
    {
        var pids = new HashSet<uint>();
        foreach (string name in names)
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                pids.Add((uint)p.Id);
                p.Dispose();
            }
        }

        EnumWindows((hwnd, _) =>
        {
            const uint GW_OWNER = 4;
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero && GetWindowTextLength(hwnd) > 0 &&
                GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pids.Contains(pid))
            {
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>True if any copy of the app has a visible main window.</summary>
    public static bool HasWindow(string processName)
    {
        Process[] ps = Process.GetProcessesByName(processName);
        bool any = ps.Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });
        foreach (Process p in ps) p.Dispose();
        return any;
    }
}
