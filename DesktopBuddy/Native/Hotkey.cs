using System.Runtime.InteropServices;

namespace DesktopBuddy.Native;

/// <summary>A system-wide keyboard shortcut (e.g. Ctrl+Alt+B), delivered to a hidden window.</summary>
internal sealed class Hotkey : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private static int _nextId = 0xB0DD;
    private readonly int _id = _nextId++;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private readonly Action _onPressed;

    public bool Registered { get; }

    public Hotkey(Keys key, Action onPressed)
    {
        _onPressed = onPressed;
        CreateHandle(new CreateParams());
        Registered = RegisterHotKey(Handle, _id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)key);
        if (!Registered) Log.Info($"Ctrl+Alt+{key} is taken by another app; use the tray menu instead.");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == _id) _onPressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(Handle, _id);
        DestroyHandle();
    }
}
