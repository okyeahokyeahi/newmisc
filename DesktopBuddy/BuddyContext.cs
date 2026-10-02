using System.Diagnostics;
using DesktopBuddy.Monitors;
using DesktopBuddy.Native;
using DesktopBuddy.UI;

namespace DesktopBuddy;

/// <summary>Owns the tray icon, the background monitoring loop, and every popup.</summary>
internal sealed class BuddyContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly ResourceMonitor _resources;
    private readonly TemperatureMonitor _temps;
    private readonly SuspiciousProcessScanner _scanner;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private readonly Queue<SuspiciousProcess> _pendingSuspicious = new();
    private readonly Task _loop;

    private StatusForm? _statusForm;
    private bool _suspiciousDialogOpen;
    private DateTime _pausedUntil = DateTime.MinValue;
    private BuddyMood? _currentMood;

    public BuddyContext(Settings settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _resources = new ResourceMonitor(settings);
        _temps = new TemperatureMonitor(settings);
        _scanner = new SuspiciousProcessScanner(settings);

        _resources.Alert += (title, text) => _ui.Post(_ => Notify(title, text), null);
        _temps.Alert += (title, text) => _ui.Post(_ => Notify(title, text), null);
        _scanner.Alert += (title, text) => _ui.Post(_ => Notify(title, text), null);
        _scanner.Flagged += item => _ui.Post(_ => EnqueueSuspicious(item), null);

        _pauseItem = new ToolStripMenuItem("Pause alerts for 1 hour", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Desktop Buddy", null, (_, _) => ShowStatus()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set API key…", null, (_, _) => { using var d = new ApiKeyDialog(); d.ShowDialog(); });
        menu.Items.Add("Edit settings (restart to apply)", null, (_, _) => OpenFile("notepad.exe", Settings.FilePath));
        menu.Items.Add("Open log folder", null, (_, _) => OpenFile("explorer.exe", Settings.Folder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Text = "Desktop Buddy: starting…",
            ContextMenuStrip = menu,
            Visible = true,
        };
        SetMood(BuddyMood.Calm);
        _tray.DoubleClick += (_, _) => ShowStatus();
        _tray.BalloonTipClicked += (_, _) => ShowStatus();

        _uiTimer.Tick += (_, _) => UiTick();
        _uiTimer.Start();

        Log.Info("Desktop Buddy started");
        _loop = Task.Run(() => MonitorLoop(_stop.Token));
        Notify("Desktop Buddy is running", "I'm in the system tray. Double-click me for live stats.", force: true);
    }

    private bool Paused => DateTime.Now < _pausedUntil;

    // ---------- Background loop ----------
    private async Task MonitorLoop(CancellationToken token)
    {
        _temps.Open();
        DateTime nextScan = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            Safe("resource monitor", _resources.Tick);
            Safe("temperature monitor", _temps.Tick);
            if (_settings.ScanForSuspiciousProcesses && DateTime.UtcNow >= nextScan)
            {
                Safe("process scanner", _scanner.Tick);
                nextScan = DateTime.UtcNow.AddSeconds(Math.Max(5, _settings.ScanIntervalSeconds));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.PollSeconds)), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"{what} failed", ex);
        }
    }

    // ---------- UI thread ----------
    private void UiTick()
    {
        TemperatureSnapshot? t = _temps.Latest;
        ResourceSnapshot? r = _resources.Latest;

        string tip = $"Desktop Buddy\nCPU {Format.Temp(t?.CpuC)} · GPU {Format.Temp(t?.GpuC)}";
        if (r != null) tip += $"\nLoad {r.CpuPercent:0}% · RAM {r.RamPercent}%";
        if (Paused) tip += "\nAlerts paused";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        double hottestOverLimit = Math.Max(
            (t?.CpuC ?? 0) - _settings.CpuTempWarnC,
            (t?.GpuC ?? 0) - _settings.GpuTempWarnC);
        SetMood(Paused ? BuddyMood.Paused
            : hottestOverLimit >= 0 ? BuddyMood.Hot
            : hottestOverLimit >= -10 ? BuddyMood.Warm
            : BuddyMood.Calm);

        _pauseItem.Text = Paused ? $"Resume alerts (paused until {_pausedUntil:HH:mm})" : "Pause alerts for 1 hour";
        ShowNextSuspicious();
    }

    private void SetMood(BuddyMood mood)
    {
        if (_currentMood == mood) return;
        _currentMood = mood;
        Icon? old = _tray.Icon;
        _tray.Icon = BuddyIcon.Create(mood);
        old?.Dispose();
    }

    private void Notify(string title, string text, bool force = false)
    {
        Log.Info($"Alert: {title}: {text}");
        if (Paused && !force) return;
        _tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning);
    }

    private void TogglePause()
    {
        _pausedUntil = Paused ? DateTime.MinValue : DateTime.Now.AddHours(1);
        UiTick();
    }

    private void EnqueueSuspicious(SuspiciousProcess item)
    {
        _pendingSuspicious.Enqueue(item);
        ShowNextSuspicious();
    }

    /// <summary>One dialog at a time; held back while alerts are paused (e.g. during a game).</summary>
    private void ShowNextSuspicious()
    {
        if (_suspiciousDialogOpen || Paused) return;

        while (_pendingSuspicious.Count > 0)
        {
            SuspiciousProcess item = _pendingSuspicious.Dequeue();
            if (!StillRunning(item)) continue;

            _suspiciousDialogOpen = true;
            SuspiciousChoice choice;
            using (var dialog = new SuspiciousProcessDialog(item))
            {
                dialog.ShowDialog();
                choice = dialog.Choice;
            }
            _suspiciousDialogOpen = false;
            HandleChoice(item, choice);
            return; // next one on the following tick
        }
    }

    private void HandleChoice(SuspiciousProcess item, SuspiciousChoice choice)
    {
        Log.Info($"User chose {choice} for {item.ExePath} (pid {item.Pid})");
        switch (choice)
        {
            case SuspiciousChoice.Kill:
                if (!StillRunning(item)) return; // already gone, and the PID may now belong to something else
                try
                {
                    using Process process = Process.GetProcessById(item.Pid);
                    process.Kill();
                    Notify("Closed", $"{item.Name} was closed.", force: true);
                }
                catch (Exception ex)
                {
                    Log.Error($"Killing {item.Name} failed", ex);
                    MessageBox.Show($"Couldn't close {item.Name}: {ex.Message}", "Desktop Buddy",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                break;

            case SuspiciousChoice.AlwaysAllow:
                _scanner.Allow(item.ExePath);
                if (!_settings.AllowedExePaths.Contains(item.ExePath, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.AllowedExePaths.Add(item.ExePath);
                    _settings.Save();
                }
                break;
        }
    }

    private static bool StillRunning(SuspiciousProcess item) =>
        string.Equals(NativeMethods.GetProcessPath(item.Pid), item.ExePath, StringComparison.OrdinalIgnoreCase);

    private void ShowStatus()
    {
        if (_statusForm is { IsDisposed: false })
        {
            _statusForm.Activate();
            return;
        }
        _statusForm = new StatusForm(_settings, _resources, _temps, _scanner);
        _statusForm.FormClosed += (_, _) => _statusForm = null;
        _statusForm.Show();
    }

    private static void OpenFile(string program, string path)
    {
        try
        {
            Process.Start(program, $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Error($"Opening {path} failed", ex);
        }
    }

    protected override void ExitThreadCore()
    {
        Log.Info("Desktop Buddy exiting");
        _stop.Cancel();
        _uiTimer.Stop();
        _tray.Visible = false;
        try { _loop.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down anyway */ }
        _temps.Dispose();
        _tray.Dispose();
        _statusForm?.Close();
        base.ExitThreadCore();
    }
}
