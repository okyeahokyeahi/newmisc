using System.Diagnostics;

namespace DesktopBuddy.UI;

/// <summary>Every setting in one window, grouped in tabs, so nobody has to edit settings.json.</summary>
internal sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly List<Action> _apply = []; // copies each control's value into the settings on Save
    private readonly Action _afterSave;
    private readonly Action _restart;

    public SettingsForm(Settings settings, Action afterSave, Action restart)
    {
        _settings = settings;
        _afterSave = afterSave;
        _restart = restart;
        Ui.Setup(this, "Desktop Buddy settings", 560, 600);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(General());
        tabs.TabPages.Add(Alerts());
        tabs.TabPages.Add(Watchers());
        tabs.TabPages.Add(Games());
        tabs.TabPages.Add(Ai());

        var save = Ui.Button("Save", (_, _) => Save(restart: false));
        var saveRestart = Ui.Button("Save and restart Desktop Buddy", (_, _) => Save(restart: true));
        var cancel = Ui.Button("Cancel", (_, _) => Close());
        CancelButton = cancel;
        var note = new Label
        {
            Text = "Most changes apply right away. A few (marked ↻) apply after a restart.",
            Dock = DockStyle.Bottom,
            Height = 26,
            ForeColor = Ui.Grey,
            Padding = new Padding(12, 6, 12, 0),
        };

        Controls.Add(tabs);
        Controls.Add(note);
        Controls.Add(Ui.ButtonRow(cancel, saveRestart, save));
    }

    // ---------- tabs ----------
    private TabPage General()
    {
        var (page, add) = Page("General");
        add(Check("Start with Windows", () => _settings.StartWithWindows, v => _settings.StartWithWindows = v));
        add(Check("Check for updates automatically", () => _settings.CheckForUpdates, v => _settings.CheckForUpdates = v));
        add(Heading("Look"));
        add(Choice("Theme", ["classic", "magi"], ["Classic", "MAGI (Evangelion-style votes)"], () => _settings.Theme, v => _settings.Theme = v));
        add(Check("MAGI sounds", () => _settings.MagiSounds, v => _settings.MagiSounds = v));
        add(Number("MAGI volume (%)", 0, 150, () => (decimal)(_settings.MagiVolume * 100), v => _settings.MagiVolume = (double)v / 100));
        add(Heading("Extras"));
        add(Check("Weekly health report", () => _settings.WeeklyReport, v => _settings.WeeklyReport = v));
        add(Check("Late-night nudge after games", () => _settings.LateNightNudge, v => _settings.LateNightNudge = v));
        add(Number("…counts as late from (24-hour clock, e.g. 23)", 0, 23, () => _settings.LateNightHour, v => _settings.LateNightHour = (int)v));
        add(Heading("Downloads tidy"));
        add(Check("Tidy Downloads weekly (moves, never deletes)", () => _settings.TidyDownloads, v => _settings.TidyDownloads = v));
        add(Number("…files older than (days)", 1, 365, () => _settings.TidyAfterDays, v => _settings.TidyAfterDays = (int)v));
        return page;
    }

    private TabPage Alerts()
    {
        var (page, add) = Page("Alerts");
        add(Heading("Temperatures"));
        add(Number("CPU warning (°C)", 60, 105, () => (decimal)_settings.CpuTempWarnC, v => _settings.CpuTempWarnC = (double)v));
        add(Number("GPU warning (°C)", 60, 100, () => (decimal)_settings.GpuTempWarnC, v => _settings.GpuTempWarnC = (double)v));
        add(Number("Must stay hot for (seconds)", 5, 600, () => _settings.TempSustainedSeconds, v => _settings.TempSustainedSeconds = (int)v));
        add(Number("Heat slowdown must last (seconds)", 5, 600, () => _settings.HeatSlowdownAlertSeconds, v => _settings.HeatSlowdownAlertSeconds = (int)v));
        add(Heading("Busy apps and memory"));
        add(Number("App counts as busy at (% CPU)", 5, 100, () => (decimal)_settings.AppCpuPercentThreshold, v => _settings.AppCpuPercentThreshold = (double)v));
        add(Number("App counts as heavy at (MB RAM)", 256, 32768, () => _settings.AppMemoryMbThreshold, v => _settings.AppMemoryMbThreshold = (int)v));
        add(Number("RAM warning (% used)", 50, 99, () => _settings.RamPercentWarn, v => _settings.RamPercentWarn = (uint)v));
        add(Number("Must last (minutes)", 1, 60, () => _settings.SustainedMinutes, v => _settings.SustainedMinutes = (int)v));
        add(Number("Don't repeat an alert for (minutes)", 1, 1440, () => _settings.AlertCooldownMinutes, v => _settings.AlertCooldownMinutes = (int)v));
        add(Heading("Disk"));
        add(Number("Warn below (GB free)", 1, 500, () => _settings.DiskFreeWarnGb, v => _settings.DiskFreeWarnGb = (int)v));
        return page;
    }

    private TabPage Watchers()
    {
        var (page, add) = Page("Security");
        add(Check("Suspicious-program check", () => _settings.ScanForSuspiciousProcesses, v => _settings.ScanForSuspiciousProcesses = v));
        add(Check("Startup-list watch (Run keys)", () => _settings.WatchStartupEntries, v => _settings.WatchStartupEntries = v));
        add(Check("Hidden-startup watch (tasks, services)", () => _settings.WatchHiddenStartup, v => _settings.WatchHiddenStartup = v));
        add(Check("Defender tamper alarm", () => _settings.WatchDefender, v => _settings.WatchDefender = v));
        add(Check("Downloads check ↻", () => _settings.WatchDownloads, v => _settings.WatchDownloads = v));
        add(Heading("Hidden-miner check"));
        add(Number("Away after (minutes idle)", 1, 120, () => _settings.IdleMinutes, v => _settings.IdleMinutes = (int)v));
        add(Number("Busy GPU while away (%)", 5, 100, () => (decimal)_settings.IdleGpuPercent, v => _settings.IdleGpuPercent = (double)v));
        add(Number("Busy CPU while away (%)", 5, 100, () => (decimal)_settings.IdleCpuPercent, v => _settings.IdleCpuPercent = (double)v));
        add(Heading("Programs you allowed"));
        add(List("Always-allowed programs (one path per line) ↻", () => _settings.AllowedExePaths, v => _settings.AllowedExePaths = v, 90));
        return page;
    }

    private TabPage Games()
    {
        var (page, add) = Page("Games");
        add(Check("Best performance power mode while playing", () => _settings.BestPerformanceDuringGames, v => _settings.BestPerformanceDuringGames = v));
        add(Check("Wrong graphics chip check", () => _settings.WatchGraphicsChip, v => _settings.WatchGraphicsChip = v));
        add(Check("Explain Roblox disconnects in the game report", () => _settings.RobloxKickExplainer, v => _settings.RobloxKickExplainer = v));
        add(Check("Roblox Studio crash help (recovery pointer + backups)", () => _settings.StudioCrashHelp, v => _settings.StudioCrashHelp = v));
        add(List("Extra games for game mode (process names, one per line)", () => _settings.GameProcessNames, v => _settings.GameProcessNames = v, 110));
        add(List("Get game-ready ticks these by default", () => _settings.GameReadyCloseList, v => _settings.GameReadyCloseList = v, 80));
        return page;
    }

    private TabPage Ai()
    {
        var (page, add) = Page("AI");
        add(new Label { Text = "Needs an API key: tray menu > AI setup.", AutoSize = true, ForeColor = Ui.Grey, Margin = new Padding(0, 0, 0, 8) });
        add(Choice("Model", UI.ApiKeyDialog.Models.Select(m => m.Id).ToArray(), UI.ApiKeyDialog.Models.Select(m => m.Label).ToArray(),
            () => _settings.AiModel, v => _settings.AiModel = v));
        add(Number("Monthly budget (US$)", 0, 100, () => (decimal)_settings.AiMonthlyBudgetUsd, v => _settings.AiMonthlyBudgetUsd = (double)v, decimals: 2));
        add(Number("Max questions per day", 1, 1000, () => _settings.AiMaxCallsPerDay, v => _settings.AiMaxCallsPerDay = (int)v));
        add(Check("Ask Buddy can offer actions (each asks you first)", () => _settings.AiActions, v => _settings.AiActions = v));
        add(Check("…put them to a MAGI vote first (MAGI theme only)", () => _settings.MagiVotesOnAiActions, v => _settings.MagiVotesOnAiActions = v));
        add(Check("AI summary in the weekly report", () => _settings.AiWeeklySummary, v => _settings.AiWeeklySummary = v));
        add(Check("AI voices for the MAGI cores", () => _settings.MagiAiVoices, v => _settings.MagiAiVoices = v));
        return page;
    }

    // ---------- builders ----------
    private static (TabPage Page, Action<Control> Add) Page(string title)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(14) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        page.Controls.Add(flow);
        return (page, c => flow.Controls.Add(c));
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        Margin = new Padding(0, 12, 0, 4),
    };

    private CheckBox Check(string text, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Text = text, Checked = get(), AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        _apply.Add(() => set(box.Checked));
        return box;
    }

    private Control Number(string text, decimal min, decimal max, Func<decimal> get, Action<decimal> set, int decimals = 0)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        var input = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Increment = decimals > 0 ? 0.5m : 1,
            Value = Math.Clamp(get(), min, max),
            Width = 80,
        };
        row.Controls.Add(input);
        row.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(6, 5, 0, 0) });
        _apply.Add(() => set(input.Value));
        return row;
    }

    private Control Choice(string text, string[] values, string[] labels, Func<string> get, Action<string> set)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        combo.Items.AddRange(labels);
        int index = Array.IndexOf(values, get());
        if (index < 0)
        {
            combo.Items.Add(get()); // something custom from settings.json: keep it
            index = combo.Items.Count - 1;
        }
        combo.SelectedIndex = index;
        row.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
        row.Controls.Add(combo);
        _apply.Add(() =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < values.Length) set(values[combo.SelectedIndex]);
        });
        return row;
    }

    private Control List(string text, Func<List<string>> get, Action<List<string>> set, int height)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        panel.Controls.Add(new Label { Text = text, AutoSize = true });
        var box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 480, Height = height, Text = string.Join(Environment.NewLine, get()) };
        panel.Controls.Add(box);
        _apply.Add(() => set(box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        return panel;
    }

    private void Save(bool restart)
    {
        foreach (Action apply in _apply) apply();
        _settings.Save();
        _afterSave();
        Log.Info("Settings saved from the settings window");
        Close();
        if (restart) _restart();
    }

    /// <summary>Starts a fresh copy a couple of seconds after this one exits (same admin rights, no prompt).</summary>
    public static void ScheduleRelaunch()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "DesktopBuddy.exe");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & start \"\" \"{exe}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
