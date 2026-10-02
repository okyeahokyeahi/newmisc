namespace DesktopBuddy;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, @"Global\DesktopBuddy.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Desktop Buddy is already running (check the system tray).", "Desktop Buddy",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        // Monitors raise events on a background thread; this context lets us marshal them to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Application.Run(new BuddyContext(Settings.Load()));
    }
}
