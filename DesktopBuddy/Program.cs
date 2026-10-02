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

        // Safety net: log unexpected errors instead of showing the WinForms crash dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled error", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved background error", e.Exception);
            e.SetObserved();
        };
        // Monitors raise events on a background thread; this context lets us marshal them to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Application.Run(new BuddyContext(Settings.Load()));
    }
}
