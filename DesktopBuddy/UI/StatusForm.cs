using DesktopBuddy.Monitors;
using DesktopBuddy.Native;

namespace DesktopBuddy.UI;

/// <summary>The window you get from the tray: live temps, CPU/RAM, and the heaviest apps.</summary>
internal sealed class StatusForm : Form
{
    private readonly ResourceMonitor _resources;
    private readonly TemperatureMonitor _temps;
    private readonly SuspiciousProcessScanner _scanner;
    private readonly Settings _settings;
    private readonly Label _cpuTemp, _gpuTemp, _cpuLoad, _ram;
    private readonly ListView _apps;
    private readonly Label _footer;
    private readonly ToolTip _tooltip = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };

    public StatusForm(Settings settings, ResourceMonitor resources, TemperatureMonitor temps, SuspiciousProcessScanner scanner)
    {
        _settings = settings;
        _resources = resources;
        _temps = temps;
        _scanner = scanner;

        Text = "Desktop Buddy";
        Icon = BuddyIcon.Create(BuddyMood.Calm);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(560, 470);
        MinimumSize = new Size(460, 380);
        Padding = new Padding(12);

        var tiles = new TableLayoutPanel { Dock = DockStyle.Top, Height = 84, ColumnCount = 4 };
        for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        tiles.Controls.Add(Tile("CPU temp", out _cpuTemp), 0, 0);
        tiles.Controls.Add(Tile("GPU temp", out _gpuTemp), 1, 0);
        tiles.Controls.Add(Tile("CPU load", out _cpuLoad), 2, 0);
        tiles.Controls.Add(Tile("RAM", out _ram), 3, 0);

        var heading = new Label
        {
            Text = "Heaviest apps right now",
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

        _footer = new Label { Dock = DockStyle.Bottom, Height = 70, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 6, 0, 0) };

        Controls.Add(_apps);
        Controls.Add(heading);
        Controls.Add(tiles);
        Controls.Add(_footer);

        _refresh.Tick += (_, _) => RefreshData();
        Load += (_, _) =>
        {
            // Sit above the tray, bottom-right of the main screen.
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
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

    private void RefreshData()
    {
        TemperatureSnapshot? t = _temps.Latest;
        SetTemp(_cpuTemp, t?.CpuC, _settings.CpuTempWarnC);
        SetTemp(_gpuTemp, t?.GpuC, _settings.GpuTempWarnC);

        ResourceSnapshot? r = _resources.Latest;
        if (r != null)
        {
            _cpuLoad.Text = $"{r.CpuPercent:0}%";
            _ram.Text = $"{r.RamPercent}%";
            _tooltip.SetToolTip(_ram, $"{Format.Bytes(r.RamUsedBytes)} of {Format.Bytes(r.RamTotalBytes)}");

            _apps.BeginUpdate();
            _apps.Items.Clear();
            foreach (AppUsage app in r.TopApps)
            {
                _apps.Items.Add(new ListViewItem(
                [
                    app.Name,
                    $"{app.CpuPercent:0.0}%",
                    Format.Bytes(app.MemoryBytes),
                    app.ProcessCount.ToString(),
                ]));
            }
            _apps.EndUpdate();
        }

        string scan = _settings.ScanForSuspiciousProcesses
            ? $"Suspicious-process scan: on, {_scanner.FlaggedCount} flagged this session."
            : "Suspicious-process scan: off.";
        string key = CredentialStore.HasApiKey ? "API key: stored (AI features coming in v2)." : "API key: not set.";
        _footer.Text = _temps.Problem == null ? $"{scan}\n{key}" : $"{_temps.Problem}\n{scan}\n{key}";
    }

    private static void SetTemp(Label label, double? celsius, double warnAt)
    {
        label.Text = Format.Temp(celsius);
        label.ForeColor = celsius switch
        {
            null => SystemColors.GrayText,
            var c when c >= warnAt => Color.FromArgb(220, 38, 38),
            var c when c >= warnAt - 10 => Color.FromArgb(217, 119, 6),
            _ => SystemColors.ControlText,
        };
    }
}
