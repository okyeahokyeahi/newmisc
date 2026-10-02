using System.Diagnostics;
using DesktopBuddy.Monitors;
using DesktopBuddy.Native;

namespace DesktopBuddy.UI;

/// <summary>
/// The pop-up from Ctrl+Alt+B or a left-click on the tray face: live numbers, the main buttons,
/// keep-awake, recent downloads and quick reminders. Closes when you click elsewhere or press Esc.
/// </summary>
internal sealed class QuickPanel : Form
{
    private readonly BuddyServices _s;
    private readonly Label _stats = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    private readonly Label _heat = new() { AutoSize = true };
    private readonly Label _awakeState = new() { AutoSize = true, ForeColor = Ui.Grey };
    private readonly ListBox _downloads = new() { Height = 96, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox _reminders = new() { Height = 70, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _reminderText = new() { Dock = DockStyle.Fill, PlaceholderText = "Remind me to…" };
    private readonly ComboBox _reminderWhen = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };
    private List<string> _downloadPaths = [];

    public QuickPanel(BuddyServices services)
    {
        _s = services;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SystemColors.Window;
        ClientSize = new Size(380, 660);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Deactivate += (_, _) => Close();
        Paint += (_, e) => e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14), AutoScroll = true };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(_stats);
        root.Controls.Add(_heat);

        var buttons = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.Controls.Add(Wide("Get game-ready", () => { Close(); _s.ShowGameReady(); }));
        buttons.Controls.Add(Wide("Why slow / loud?", () => { Close(); _s.ShowDiagnosis(); }));
        buttons.Controls.Add(Wide("Ask Buddy", () => { Close(); _s.ShowAsk(null); }));
        buttons.Controls.Add(Wide("Full window", () => { Close(); _s.ShowStatus(); }));
        buttons.Controls.Add(Wide("Ask about screen", () => { Close(); _s.ShowScreenAsk(); }));
        buttons.Controls.Add(Wide("AI setup", () => { Close(); _s.ShowApiKey(); }));
        root.Controls.Add(buttons);

        root.Controls.Add(Heading("Keep awake"));
        var awake = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        awake.Controls.Add(Small("Off", () => SetAwake(null)));
        awake.Controls.Add(Small("1 hour", () => SetAwake(TimeSpan.FromHours(1))));
        awake.Controls.Add(Small("3 hours", () => SetAwake(TimeSpan.FromHours(3))));
        awake.Controls.Add(Small("Until off", () => SetAwake(TimeSpan.MaxValue)));
        root.Controls.Add(awake);
        root.Controls.Add(_awakeState);

        root.Controls.Add(Heading("Recent downloads (double-click to show)"));
        _downloads.DoubleClick += (_, _) => ShowDownload(open: false);
        var dlMenu = new ContextMenuStrip();
        dlMenu.Items.Add("Open", null, (_, _) => ShowDownload(open: true));
        dlMenu.Items.Add("Show in folder", null, (_, _) => ShowDownload(open: false));
        dlMenu.Items.Add("Is this safe? (ask AI)", null, (_, _) => AskAboutDownload());
        _downloads.ContextMenuStrip = dlMenu;
        root.Controls.Add(_downloads);

        root.Controls.Add(Heading("Reminders"));
        _reminderWhen.Items.AddRange(["in 20 min", "in 1 hour", "in 2 hours", "after my game"]);
        _reminderWhen.SelectedIndex = 0;
        var addRow = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.Controls.Add(_reminderText, 0, 0);
        addRow.Controls.Add(_reminderWhen, 1, 0);
        addRow.Controls.Add(Small("Add", AddReminder), 2, 0);
        _reminderText.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddReminder();
        };
        root.Controls.Add(addRow);
        var remMenu = new ContextMenuStrip();
        remMenu.Items.Add("Delete", null, (_, _) => DeleteReminder());
        _reminders.ContextMenuStrip = remMenu;
        _reminders.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteReminder(); };
        root.Controls.Add(_reminders);

        Controls.Add(root);

        _refresh.Tick += (_, _) => RefreshStats();
        Load += (_, _) =>
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);
            RefreshStats();
            RefreshDownloads();
            RefreshReminders();
            _refresh.Start();
            Activate();
        };
        FormClosed += (_, _) => _refresh.Dispose();
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 2),
    };

    private static Button Wide(string text, Action onClick)
    {
        var b = new Button { Text = text, Dock = DockStyle.Fill, Height = 34, Margin = new Padding(2) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Button Small(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(70, 28), Margin = new Padding(0, 0, 4, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void RefreshStats()
    {
        TemperatureSnapshot? t = _s.Temps.Latest;
        ResourceSnapshot? r = _s.Resources.Latest;
        _stats.Text = $"CPU {Format.Temp(t?.CpuC)} · GPU {Format.Temp(t?.GpuC)} · RAM {(r == null ? "…" : $"{r.RamPercent}%")}";

        HeatSlowdownSnapshot? h = _s.Heat.Latest;
        bool slowing = h?.CpuSlowingNow == true || h?.GpuSlowingNow == true;
        _heat.Text = slowing ? "🔥 Slowing down from heat right now" : _s.Games.InSession ? "🎮 Game mode is on" : "✔ All good";
        _heat.ForeColor = slowing ? Ui.Red : Ui.Green;

        _awakeState.Text = KeepAwake.Until switch
        {
            null => "Off: the PC sleeps as normal.",
            var u when u == DateTime.MaxValue => "On until you turn it off.",
            var u => $"On until {u:HH:mm}.",
        };
    }

    private void SetAwake(TimeSpan? duration)
    {
        KeepAwake.Set(duration);
        RefreshStats();
    }

    private void RefreshDownloads()
    {
        _downloads.Items.Clear();
        try
        {
            _downloadPaths = new DirectoryInfo(DownloadsWatcher.DownloadsFolder)
                .EnumerateFiles()
                .Where(f => !f.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) &&
                            !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                            !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                            !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(5)
                .Select(f => f.FullName)
                .ToList();
            foreach (string path in _downloadPaths)
            {
                var info = new FileInfo(path);
                _downloads.Items.Add($"{info.Name}  ·  {Format.Bytes(info.Length)}  ·  {Ago(info.LastWriteTime)}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing downloads failed", ex);
        }
        if (_downloads.Items.Count == 0) _downloads.Items.Add("(nothing yet)");
    }

    private void ShowDownload(bool open)
    {
        int i = _downloads.SelectedIndex;
        if (i < 0 || i >= _downloadPaths.Count) return;
        string path = _downloadPaths[i];
        try
        {
            // Through Explorer, so files open as you (not as admin, which Desktop Buddy runs as).
            Process.Start("explorer.exe", open ? $"\"{path}\"" : $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Error($"Opening {path} failed", ex);
        }
    }

    private void AskAboutDownload()
    {
        int i = _downloads.SelectedIndex;
        if (i < 0 || i >= _downloadPaths.Count) return;
        DownloadReport report = DownloadsWatcher.Inspect(_downloadPaths[i]);
        string source = report.FromHost != null ? $" from {report.FromHost}" + (report.ViaHost != null ? $" (linked from {report.ViaHost})" : "") : "";
        string flags = report.Warnings.Count > 0 ? $" Desktop Buddy noticed: {string.Join(" ", report.Warnings)}" : "";
        Close();
        _s.ShowAsk($"I downloaded a file called \"{report.FileName}\"{source}.{flags} Does this look like a scam or something risky? " +
                   "What should I check before opening it?");
    }

    private void AddReminder()
    {
        string text = _reminderText.Text.Trim();
        if (text.Length == 0) return;
        DateTime? due = _reminderWhen.SelectedIndex switch
        {
            0 => DateTime.Now.AddMinutes(20),
            1 => DateTime.Now.AddHours(1),
            2 => DateTime.Now.AddHours(2),
            _ => null, // after my game
        };
        _s.Reminders.Add(new Reminder { Text = text.Length > 120 ? text[..120] : text, DueAt = due });
        _reminderText.Clear();
        RefreshReminders();
    }

    private void DeleteReminder()
    {
        if (_reminders.SelectedItem is ReminderItem item)
        {
            _s.Reminders.Remove(item.Reminder);
            RefreshReminders();
        }
    }

    private void RefreshReminders()
    {
        _reminders.Items.Clear();
        foreach (Reminder r in _s.Reminders.All) _reminders.Items.Add(new ReminderItem(r));
    }

    private sealed record ReminderItem(Reminder Reminder)
    {
        public override string ToString() => Reminder.Describe();
    }

    private static string Ago(DateTime when)
    {
        TimeSpan t = DateTime.Now - when;
        return t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago" : $"{(int)t.TotalDays} d ago";
    }
}
