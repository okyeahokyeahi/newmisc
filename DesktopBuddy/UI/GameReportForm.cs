using DesktopBuddy.Monitors;

namespace DesktopBuddy.UI;

/// <summary>The after-game summary.</summary>
internal sealed class GameReportForm : Form
{
    public GameReportForm(GameSessionReport r, Settings settings)
    {
        Ui.Setup(this, $"Game report: {Diagnosis.FriendlyName(r.Game)}", 500, 420);
        var text = Ui.ReadOnlyText();

        Ui.AppendHeading(text, $"{Diagnosis.FriendlyName(r.Game)}: {Duration(r.Length)} (started {r.Started:HH:mm})");

        Ui.AppendHeading(text, "Temperatures");
        Ui.AppendBody(text, $"Peak CPU {Format.Temp(r.PeakCpuC)} · peak GPU {Format.Temp(r.PeakGpuC)}",
            r.PeakCpuC >= settings.CpuTempWarnC || r.PeakGpuC >= settings.GpuTempWarnC ? Ui.Amber : null);

        Ui.AppendHeading(text, "Slowed down from heat");
        bool throttled = r.CpuHeatSlowdown > TimeSpan.FromSeconds(10) || r.GpuHeatSlowdown > TimeSpan.FromSeconds(10);
        Ui.AppendBody(text,
            throttled
                ? $"CPU {Duration(r.CpuHeatSlowdown)}, GPU {Duration(r.GpuHeatSlowdown)}. Any stutter during those moments was heat. " +
                  "Max fan in NitroSense and raising the back of the laptop help; if it happens every game, consider a repaste."
                : "No. Cooling kept up the whole time.",
            throttled ? Ui.Red : Ui.Green);

        Ui.AppendHeading(text, "Memory");
        Ui.AppendBody(text, $"Peak RAM use {r.PeakRamPercent}%." +
            (r.PeakRamPercent >= 90 ? " That's very full: close Chrome/Studio before playing for smoother frames." : ""),
            r.PeakRamPercent >= 90 ? Ui.Amber : null);

        if (r.HeldAlerts.Count > 0)
        {
            Ui.AppendHeading(text, $"Alerts held while you played ({r.HeldAlerts.Count})");
            Ui.AppendBody(text, string.Join("\n", r.HeldAlerts.Take(15)));
        }

        Ui.AppendBody(text, "Every session is also saved to game-sessions.csv in the log folder.", Ui.Grey);

        var close = Ui.Button("Close", (_, _) => Close());
        CancelButton = close;
        var padded = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 4) };
        padded.Controls.Add(text);
        Controls.Add(padded);
        Controls.Add(Ui.ButtonRow(close));
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";
}
