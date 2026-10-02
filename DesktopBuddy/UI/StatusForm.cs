using DesktopBuddy.Ai;
using DesktopBuddy.Monitors;

namespace DesktopBuddy.UI;

/// <summary>The window you get from the tray: live temps, CPU/RAM, heat slowdowns and the heaviest apps.</summary>
internal sealed class StatusForm : Form
{
    private readonly BuddyServices _s;
    private readonly Label _cpuTemp, _gpuTemp, _cpuLoad, _ram;
    private readonly Label _heatLine;
    private readonly ListView _apps;
    private readonly Label _footer;
    private readonly ToolTip _tooltip = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };

    public StatusForm(BuddyServices services)
    {
        _s = services;

        Text = $"Desktop Buddy {Updater.CurrentVersion.ToString(3)}";
        Icon = BuddyIcon.Create(BuddyMood.Calm);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleDimensions = new SizeF(96F, 96F); // designed at 100%; WinForms scales up for 125%+
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(600, 640);
        MinimumSize = new Size(540, 600);
        Padding = new Padding(12);

        var tiles = new TableLayoutPanel { Dock = DockStyle.Top, Height = 84, ColumnCount = 4 };
        for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tiles.Controls.Add(Tile("CPU temp", out _cpuTemp), 0, 0);
        tiles.Controls.Add(Tile("GPU temp", out _gpuTemp), 1, 0);
        tiles.Controls.Add(Tile("CPU load", out _cpuLoad), 2, 0);
        tiles.Controls.Add(Tile("RAM", out _ram), 3, 0);

        _heatLine = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
            AutoEllipsis = true,
        };

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 2, 0, 2) };
        actions.Controls.Add(Ui.Button("Why is it slow / loud?", (_, _) => _s.ShowDiagnosis()));
        actions.Controls.Add(Ui.Button("Ask Buddy", (_, _) => _s.ShowAsk(null)));
        actions.Controls.Add(Ui.Button("Get game-ready", (_, _) => _s.ShowGameReady()));

        var heading = new Label
        {
            Text = "Heaviest apps right now (double-click one to ask \"what is this?\")",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
        };

        _apps = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _apps.Columns.Add("App", 200);
        _apps.Columns.Add("CPU", 70, HorizontalAlignment.Right);
        _apps.Columns.Add("Memory", 90, HorizontalAlignment.Right);
        _apps.Columns.Add("Processes", 80, HorizontalAlignment.Right);
        _apps.DoubleClick += (_, _) => ExplainSelected();
        _apps.Resize += (_, _) => StretchFirstColumn();
        var menu = new ContextMenuStrip();
        menu.Items.Add("What is this?", null, (_, _) => ExplainSelected());
        _apps.ContextMenuStrip = menu;

        _footer = new Label { Dock = DockStyle.Bottom, Height = 180, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0), AutoEllipsis = true };

        // WinForms docks the last-added control first: footer, then tiles/heat/actions/heading, then the list fills the rest.
        Controls.Add(_apps);
        Controls.Add(heading);
        Controls.Add(actions);
        Controls.Add(_heatLine);
        Controls.Add(tiles);
        Controls.Add(_footer);

        _refresh.Tick += (_, _) => RefreshData();
        Load += (_, _) =>
        {
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
            StretchFirstColumn();
            RefreshData();
            _refresh.Start();
        };
        FormClosed += (_, _) =>
        {
            _refresh.Dispose();
            _tooltip.Dispose();
        };
    }

    private static Control Tile(string caption, out Label value)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4), BackColor = SystemColors.ControlLight };
        value = new Label
        {
            Dock = DockStyle.Fill,
            Text = "…",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var label = new Label { Dock = DockStyle.Top, Text = caption, Height = 22, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
        panel.Controls.Add(value);
        panel.Controls.Add(label);
        return panel;
    }

    private void StretchFirstColumn()
    {
        int others = _apps.Columns[1].Width + _apps.Columns[2].Width + _apps.Columns[3].Width;
        int width = _apps.ClientSize.Width - others - 4;
        if (width > 120) _apps.Columns[0].Width = width;
    }

    private void ExplainSelected()
    {
        if (_apps.SelectedItems.Count == 0) return;
        _s.ShowExplain(_apps.SelectedItems[0].Text, null, null);
    }

    private void RefreshData()
    {
        TemperatureSnapshot? t = _s.Temps.Latest;
        SetTemp(_cpuTemp, t?.CpuC, _s.Settings.CpuTempWarnC);
        SetTemp(_gpuTemp, t?.GpuC, _s.Settings.GpuTempWarnC);

        HeatSlowdownSnapshot? h = _s.Heat.Latest;
        string clock = t?.CpuClockMhz is double mhz ? $" · CPU running at {mhz / 1000:0.0} GHz" : "";
        if (h?.CpuSlowingNow == true || h?.GpuSlowingNow == true)
        {
            string part = h.CpuSlowingNow && h.GpuSlowingNow ? "CPU and GPU are" : h.CpuSlowingNow ? "CPU is" : "GPU is";
            _heatLine.Text = $"🔥 {part} slowing down from heat right now{clock}";
            _heatLine.ForeColor = Ui.Red;
        }
        else if (h?.GpuPowerBrakeNow == true)
        {
            _heatLine.Text = "⚡ GPU is being held back by the power limit (check the charger)";
            _heatLine.ForeColor = Ui.Amber;
        }
        else
        {
            _heatLine.Text = $"✔ No heat slowdown{clock}";
            _heatLine.ForeColor = Ui.Green;
        }

        ResourceSnapshot? r = _s.Resources.Latest;
        if (r != null)
        {
            _cpuLoad.Text = $"{r.CpuPercent:0}%";
            _ram.Text = $"{r.RamPercent}%";
            _ram.ForeColor = r.RamPercent >= _s.Settings.RamPercentWarn ? Ui.Red : r.RamPercent >= 80 ? Ui.Amber : SystemColors.ControlText;
            _tooltip.SetToolTip(_ram, $"{Format.Bytes(r.RamUsedBytes)} of {Format.Bytes(r.RamTotalBytes)}");

            string? selected = _apps.SelectedItems.Count > 0 ? _apps.SelectedItems[0].Text : null;
            _apps.BeginUpdate();
            _apps.Items.Clear();
            foreach (AppUsage app in r.TopApps)
            {
                var item = new ListViewItem([app.Name, $"{app.CpuPercent:0.0}%", Format.Bytes(app.MemoryBytes), app.ProcessCount.ToString()]);
                _apps.Items.Add(item);
                if (app.Name == selected) item.Selected = true;
            }
            _apps.EndUpdate();
        }

        _footer.Text = string.Join("\n", FooterLines());
    }

    private IEnumerable<string> FooterLines()
    {
        if (_s.Temps.Problem != null) yield return _s.Temps.Problem;

        HeatSlowdownSnapshot? h = _s.Heat.Latest;
        if (h != null)
        {
            string gpu = h.GpuReasonsAvailable ? Minutes(h.GpuToday) : "n/a";
            yield return $"Heat slowdowns today: CPU {Minutes(h.CpuToday)} · GPU {gpu}";
        }

        DefenderStatus? d = _s.Defender.Latest;
        if (d != null)
        {
            yield return !d.Readable ? "Defender: couldn't read status."
                : d.OtherAntivirus != null ? $"Antivirus: {d.OtherAntivirus} is in charge."
                : d.RealTimeOn ? $"Defender: on{(d.Exclusions.Count > 0 ? $" ({d.Exclusions.Count} scan exclusion{(d.Exclusions.Count == 1 ? "" : "s")})" : "")}."
                : "Defender: real-time protection is OFF!";
        }

        if (_s.Disk.FreeBytes is long free) yield return $"Free space on C: {Format.Bytes(free)}" +
            (_s.Care.BootTimes is var (last, usual) ? $" · last startup {last.TotalSeconds:0}s (usually {usual.TotalSeconds:0}s)" : "");

        if (_s.Games.InSession) yield return "🎮 Game running: alerts are held until you finish.";
        else if (_s.Games.LastReport is { } g) yield return $"Last game: {g.Game}, {(int)g.Length.TotalMinutes} min, peak CPU {Format.Temp(g.PeakCpuC)} / GPU {Format.Temp(g.PeakGpuC)}";

        yield return _s.Settings.ScanForSuspiciousProcesses
            ? $"Suspicious-program check: on, {_s.Scanner.FlaggedCount} flagged this session."
            : "Suspicious-program check: off.";

        var usage = AiClient.CurrentUsage();
        yield return _s.Ai.HasKey
            ? $"AI: on · ${usage.SpentUsd:0.00} of ${_s.Settings.AiMonthlyBudgetUsd:0.00} used this month"
            : "AI: no API key yet (tray menu > Set API key…)";
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";

    private static void SetTemp(Label label, double? celsius, double warnAt)
    {
        label.Text = Format.Temp(celsius);
        label.ForeColor = celsius switch
        {
            null => SystemColors.GrayText,
            var c when c >= warnAt => Ui.Red,
            var c when c >= warnAt - 10 => Ui.Amber,
            _ => SystemColors.ControlText,
        };
    }
}
