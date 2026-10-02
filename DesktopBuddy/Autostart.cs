using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace DesktopBuddy;

/// <summary>
/// "Start with Windows" via a Task Scheduler task that runs at logon with highest privileges. A normal
/// Startup shortcut would show an admin (UAC) prompt on every boot because the app requires admin.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "DesktopBuddy";

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "DesktopBuddy.exe");

    /// <summary>True if the logon task exists and was registered for this copy of the app.</summary>
    public static bool IsEnabled(Settings settings) =>
        string.Equals(settings.AutostartRegisteredPath, ExePath, StringComparison.OrdinalIgnoreCase) && TaskExists();

    public static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"").Code == 0;

    public static bool Enable(Settings settings)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string exe = SecurityElement.Escape(ExePath)!;
        string dir = SecurityElement.Escape(AppContext.BaseDirectory.TrimEnd('\\'))!;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Desktop Buddy when you log in.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        string file = Path.Combine(Path.GetTempPath(), "DesktopBuddy-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode); // matches the UTF-16 declaration
            var (code, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (code != 0) Log.Error($"Creating the startup task failed: {output.Trim()}");
            else
            {
                Log.Info("Start with Windows: on");
                settings.AutostartRegisteredPath = ExePath;
                settings.Save();
            }
            return code == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }

    public static void Disable()
    {
        if (!TaskExists()) return;
        var (code, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (code != 0) Log.Error($"Removing the startup task failed: {output.Trim()}");
        else Log.Info("Start with Windows: off");
    }

    /// <summary>Called at startup: keeps the task in line with the setting (e.g. after the app folder moved).</summary>
    public static void Sync(Settings settings)
    {
        try
        {
            if (settings.StartWithWindows && !IsEnabled(settings)) Enable(settings);
            else if (!settings.StartWithWindows) Disable();
        }
        catch (Exception ex)
        {
            Log.Error("Syncing the startup task failed", ex);
        }
    }

    private static (int Code, string Output) RunSchtasks(string args)
    {
        var start = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(start)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (p.ExitCode, output);
    }
}
