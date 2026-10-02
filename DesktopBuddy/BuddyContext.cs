using System.Diagnostics;
using DesktopBuddy.Ai;
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
    private readonly HeatSlowdownMonitor _heat;
    private readonly SuspiciousProcessScanner _scanner;
    private readonly DefenderMonitor _defender;
    private readonly HiddenStartupWatcher _hiddenStartup;
    private readonly DiskMonitor _disk;
    private readonly GameSessionTracker _games;
    private readonly IdleHogWatcher _idleHogs;
    private readonly DownloadsWatcher? _downloads;
    private readonly BuddyServices _services;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private readonly Queue<SuspiciousProcess> _pendingSuspicious = new();
    private readonly Task _loop;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60_000 };
    private bool _updating;

    private StatusForm? _statusForm;
    private AskBuddyForm? _askForm;
    private bool _suspiciousDialogOpen;
    private DateTime _pausedUntil = DateTime.MinValue;
    private BuddyMood? _currentMood;
    private Action? _balloonClick;
    private DateTime _balloonShownAt;
    private readonly List<DateTime> _recentDownloadToasts = [];
    private readonly HashSet<string> _scansRunning = new(StringComparer.OrdinalIgnoreCase);

    public BuddyContext(Settings settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _resources = new ResourceMonitor(settings);
        _temps = new TemperatureMonitor(settings);
        _heat = new HeatSlowdownMonitor(settings, _temps);
        _scanner = new SuspiciousProcessScanner(settings);
        _defender = new DefenderMonitor();
        _hiddenStartup = new HiddenStartupWatcher();
        _disk = new DiskMonitor(settings);
        _games = new GameSessionTracker(settings, _resources, _temps, _heat);
        _idleHogs = new IdleHogWatcher(settings, _resources);

        _services = new BuddyServices
        {
            Settings = settings,
            Resources = _resources,
            Temps = _temps,
            Heat = _heat,
            Scanner = _scanner,
            Defender = _defender,
            Disk = _disk,
            Games = _games,
            Ai = new AiClient(settings),
            ShowDiagnosis = ShowDiagnosis,
            ShowAsk = ShowAsk,
            ShowExplain = (name, path, reasons) => new ExplainForm(_services!, name, path, reasons).Show(),
            ShowApiKey = () => { using var d = new ApiKeyDialog(); d.ShowDialog(); },
        };

        // Monitors run on a background thread; every alert hops to the UI thread here.
        void OnUi(Action action) => _ui.Post(_ => action(), null);
        _resources.Alert += (title, text) => OnUi(() => Notify(title, text, onClick: ShowDiagnosis));
        _temps.Alert += (title, text) => OnUi(() => Notify(title, text, onClick: ShowDiagnosis));
        _heat.Alert += (title, text) => OnUi(() => Notify(title, text, onClick: ShowDiagnosis));
        _disk.Alert += (title, text) => OnUi(() => Notify(title, text, onClick: () => OpenUri("ms-settings:storagesense")));
        // Security alerts are "critical": shown even mid-game or while paused, because a fake executor gets run while Roblox is open.
        _scanner.Alert += (title, text) => OnUi(() => Notify(title, text, critical: true, onClick: () => OpenUri("ms-settings:startupapps")));
        _scanner.Flagged += item => OnUi(() => EnqueueSuspicious(item));
        _defender.Alert += (title, text, critical) => OnUi(() =>
            Notify(title, text, critical: critical, onClick: () => OpenUri("windowsdefender://threatsettings")));
        _hiddenStartup.Alert += alert => OnUi(() => Notify(alert.Title, alert.Message, critical: true, onClick: () => OpenStartupPlace(alert.Place)));
        _games.SessionEnded += report => OnUi(() => OnGameEnded(report));
        _idleHogs.Report += findings => OnUi(() => OnIdleHogs(findings));

        if (settings.WatchDownloads)
        {
            try
            {
                _downloads = new DownloadsWatcher();
                _downloads.Downloaded += report => OnUi(() => OnDownload(report));
            }
            catch (Exception ex)
            {
                Log.Error("Starting the Downloads watcher failed", ex);
            }
        }

        _pauseItem = new ToolStripMenuItem("Pause alerts for 1 hour", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open Desktop Buddy", null, (_, _) => ShowStatus()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add("Why is it slow / loud?", null, (_, _) => ShowDiagnosis());
        menu.Items.Add("Ask Buddy…", null, (_, _) => ShowAsk(null));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set API key…", null, (_, _) => _services.ShowApiKey());
        menu.Items.Add($"Check for updates (you have {Updater.CurrentVersion.ToString(3)})", null, async (_, _) => await CheckForUpdates(manual: true));
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
        // Windows only says "a balloon was clicked", not which one. Older ones clicked later from the
        // notification centre could run the wrong action, so after a minute a click just opens the status window.
        _tray.BalloonTipClicked += (_, _) =>
            (DateTime.UtcNow - _balloonShownAt < TimeSpan.FromSeconds(60) ? _balloonClick ?? ShowStatus : ShowStatus)();

        _uiTimer.Tick += (_, _) => UiTick();
        _uiTimer.Start();

        if (settings.CheckForUpdates)
        {
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
                await CheckForUpdates(manual: false);
            };
            _updateTimer.Start(); // first check a minute after startup
        }

        Log.Info("Desktop Buddy started");
        _loop = Task.Run(() => MonitorLoop(_stop.Token));
        Notify("Desktop Buddy is running", "I'm in the system tray. Double-click me for live stats.", ToolTipIcon.Info, critical: true);
    }

    private bool Paused => DateTime.Now < _pausedUntil;

    // ---------- Background loop ----------
    private async Task MonitorLoop(CancellationToken token)
    {
        _temps.Open();
        DateTime nextScan = DateTime.MinValue, nextSlow = DateTime.MinValue, nextDisk = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            DateTime now = DateTime.UtcNow;
            Safe("resource monitor", _resources.Tick);
            Safe("temperature monitor", _temps.Tick);
            Safe("heat slowdown monitor", _heat.Tick);
            Safe("game tracker", _games.Tick);
            Safe("idle hog watcher", () => _idleHogs.Tick(_games.InSession));

            if (_settings.ScanForSuspiciousProcesses && now >= nextScan)
            {
                Safe("process scanner", _scanner.Tick);
                nextScan = now.AddSeconds(Math.Max(5, _settings.ScanIntervalSeconds));
            }
            if (now >= nextSlow)
            {
                if (_settings.WatchDefender) Safe("Defender monitor", _defender.Tick);
                if (_settings.WatchHiddenStartup) Safe("hidden startup watcher", _hiddenStartup.Tick);
                nextSlow = now.AddSeconds(60);
            }
            if (now >= nextDisk)
            {
                Safe("disk monitor", _disk.Tick);
                nextDisk = now.AddMinutes(10);
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
        HeatSlowdownSnapshot? h = _heat.Latest;

        string tip = $"Desktop Buddy\nCPU {Format.Temp(t?.CpuC)} · GPU {Format.Temp(t?.GpuC)}";
        if (r != null) tip += $"\nLoad {r.CpuPercent:0}% · RAM {r.RamPercent}%";
        if (h?.CpuSlowingNow == true || h?.GpuSlowingNow == true) tip += "\nSlowing down from heat!";
        if (_games.InSession) tip += "\nGame mode";
        else if (Paused) tip += "\nAlerts paused";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        double hottestOverLimit = Math.Max(
            (t?.CpuC ?? 0) - _settings.CpuTempWarnC,
            (t?.GpuC ?? 0) - _settings.GpuTempWarnC);
        bool slowing = h?.CpuSlowingNow == true || h?.GpuSlowingNow == true;
        SetMood(Paused ? BuddyMood.Paused
            : _temps.SustainedHot || slowing ? BuddyMood.Hot
            : hottestOverLimit >= -10 ? BuddyMood.Warm // includes short spikes over the limit
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

    /// <summary>
    /// Shows a notification. Non-critical ones are dropped while paused and held for the game report
    /// during a game. Critical ones (antivirus switched off, new Defender exclusion) always show.
    /// </summary>
    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning, bool critical = false, Action? onClick = null)
    {
        Log.Info($"Alert: {title}: {text}");
        if (!critical)
        {
            if (Paused) return;
            if (_games.Hold($"{title}: {text}")) return;
        }
        _balloonClick = onClick;
        _balloonShownAt = DateTime.UtcNow;
        // Windows cuts balloon text at 255 characters and titles at 63.
        if (text.Length > 255) text = text[..252] + "...";
        if (title.Length > 63) title = title[..60] + "...";
        _tray.ShowBalloonTip(8000, title, text, icon);
    }

    private void TogglePause()
    {
        _pausedUntil = Paused ? DateTime.MinValue : DateTime.Now.AddHours(1);
        UiTick();
    }

    // ---------- Feature handlers ----------
    private void OnGameEnded(GameSessionReport report)
    {
        bool throttled = report.CpuHeatSlowdown > TimeSpan.FromSeconds(10) || report.GpuHeatSlowdown > TimeSpan.FromSeconds(10);
        string text = $"{(int)report.Length.TotalMinutes} min · peak CPU {Format.Temp(report.PeakCpuC)}, GPU {Format.Temp(report.PeakGpuC)}, RAM {report.PeakRamPercent}%" +
                      (throttled ? " · slowed down from heat" : "") +
                      (report.HeldAlerts.Count > 0 ? $" · {report.HeldAlerts.Count} alert(s) waiting" : "") +
                      ". Click for the report.";
        Notify($"Game over: {Diagnosis.FriendlyName(report.Game)}", text, throttled || report.HeldAlerts.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info,
            onClick: () => new GameReportForm(report, _settings).Show());
    }

    private void OnIdleHogs(IReadOnlyList<IdleHogFinding> findings)
    {
        IdleHogFinding top = findings.OrderByDescending(f => f.Duration).First();
        string others = findings.Count > 1 ? $" (+{findings.Count - 1} more)" : "";
        Notify("Busy while you were away",
            $"{top.Name} used {top.What} for {(int)top.Duration.TotalMinutes}+ min while you weren't using the PC{others}. " +
            "Hidden crypto-miners do this. Click to see what it is.",
            critical: true,
            onClick: () => _services.ShowExplain(top.Name, top.ExePath,
                [$"Used {top.What} for {(int)top.Duration.TotalMinutes}+ minutes while the computer was idle."]));
    }

    private void OnDownload(DownloadReport report)
    {
        // "Extract here" can drop dozens of files at once: show the first few, log the rest.
        DateTime now = DateTime.UtcNow;
        _recentDownloadToasts.RemoveAll(t => now - t > TimeSpan.FromSeconds(30));
        if (_recentDownloadToasts.Count >= 3)
        {
            Log.Info($"Download (not shown, too many at once): {report.FileName} {string.Join(" ", report.Warnings)}");
            return;
        }
        _recentDownloadToasts.Add(now);

        string from = report.FromHost != null
            ? $"From {report.FromHost}{(report.ViaHost != null && report.ViaHost != report.FromHost ? $" (via {report.ViaHost})" : "")}. "
            : "";
        string warnings = report.Warnings.Count > 0 ? "⚠ " + string.Join(" ", report.Warnings) + " " : "";
        Notify($"Downloaded: {report.FileName}", $"Click to scan it with Defender. {from}{warnings}".Trim(),
            report.Warnings.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info,
            critical: report.Warnings.Count > 0,
            onClick: () => _ = ScanDownload(report.FilePath));
    }

    private async Task ScanDownload(string path)
    {
        if (!_scansRunning.Add(path)) return; // already scanning this file
        try
        {
            Notify("Scanning…", $"Defender is checking {Path.GetFileName(path)}.", ToolTipIcon.Info, critical: true);
            var (clean, message) = await DownloadsWatcher.ScanWithDefender(path);
            Notify(clean ? "Scan finished" : "Scan result", message, clean ? ToolTipIcon.Info : ToolTipIcon.Error, critical: true);
        }
        catch (Exception ex)
        {
            Log.Error("Defender scan failed", ex);
            Notify("Scan failed", "Couldn't run the Defender scan. Right-click the file > Scan with Microsoft Defender instead.", ToolTipIcon.Error, critical: true);
        }
        finally
        {
            _scansRunning.Remove(path);
        }
    }

    private void EnqueueSuspicious(SuspiciousProcess item)
    {
        _pendingSuspicious.Enqueue(item);
        ShowNextSuspicious();
    }

    /// <summary>
    /// One dialog at a time. Held back while alerts are paused, a game is running, or anything
    /// full-screen is in front, so it never yanks you out of a game.
    /// </summary>
    private void ShowNextSuspicious()
    {
        if (_suspiciousDialogOpen || Paused || _games.InSession || NativeMethods.UserIsBusy()) return;

        while (_pendingSuspicious.Count > 0)
        {
            SuspiciousProcess item = _pendingSuspicious.Dequeue();
            List<Process> running = RunningCopies(item.ExePath);
            running.ForEach(p => p.Dispose());
            if (running.Count == 0)
            {
                _scanner.ForgetReport(item.ExePath); // closed on its own; report again if it comes back
                continue;
            }

            _suspiciousDialogOpen = true;
            try
            {
                SuspiciousChoice choice;
                using (var dialog = new SuspiciousProcessDialog(item, explain: p =>
                       {
                           using var explainForm = new ExplainForm(_services, Path.GetFileNameWithoutExtension(p.Name), p.ExePath, p.Reasons);
                           explainForm.ShowDialog();
                       }))
                {
                    dialog.ShowDialog();
                    choice = dialog.Choice;
                }
                HandleChoice(item, choice); // may show a MessageBox, so keep the guard up until it's done
            }
            finally
            {
                _suspiciousDialogOpen = false;
            }
            return; // next one on the following tick
        }
    }

    private void HandleChoice(SuspiciousProcess item, SuspiciousChoice choice)
    {
        Log.Info($"User chose {choice} for {item.ExePath} (pid {item.Pid})");
        switch (choice)
        {
            case SuspiciousChoice.Kill:
                var failures = new List<string>();
                List<Process> copies = RunningCopies(item.ExePath); // every copy, not just the first PID
                foreach (Process process in copies)
                {
                    using (process)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Killing {item.Name} (pid {process.Id}) failed", ex);
                            failures.Add(ex.Message);
                        }
                    }
                }
                _scanner.ForgetReport(item.ExePath); // if it comes back, tell me again
                if (failures.Count == 0)
                    Notify("Closed", $"{item.Name} was closed.", ToolTipIcon.Info, critical: true);
                else
                    MessageBox.Show($"Couldn't close {item.Name}: {failures[0]}", "Desktop Buddy",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    /// <summary>All running processes started from this exe. Caller disposes them.</summary>
    private static List<Process> RunningCopies(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        var copies = new List<Process>();
        foreach (Process process in Process.GetProcessesByName(name))
        {
            if (string.Equals(NativeMethods.GetProcessPath(process.Id), exePath, StringComparison.OrdinalIgnoreCase))
                copies.Add(process);
            else
                process.Dispose();
        }
        return copies;
    }

    // ---------- Self-update ----------
    private async Task CheckForUpdates(bool manual)
    {
        if (_updating) return;
        try
        {
            UpdateInfo? update = await Updater.CheckAsync();
            if (update == null)
            {
                if (manual) Notify("You're up to date", $"Desktop Buddy {Updater.CurrentVersion.ToString(3)} is the latest version.", ToolTipIcon.Info, critical: true);
                return;
            }
            Notify($"Update available: {update.Version.ToString(3)}",
                "Click to install it. Takes about 30 seconds; Desktop Buddy restarts by itself and keeps your settings.",
                ToolTipIcon.Info, critical: manual, onClick: () => _ = InstallUpdate(update));
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            if (manual) Notify("Couldn't check for updates", "GitHub couldn't be reached. Are you online?", ToolTipIcon.Warning, critical: true);
        }
    }

    private async Task InstallUpdate(UpdateInfo update)
    {
        if (_updating) return;
        _updating = true;
        Notify("Updating…", $"Downloading Desktop Buddy {update.Version.ToString(3)}. It will restart in a moment.", ToolTipIcon.Info, critical: true);
        try
        {
            if (await Updater.PrepareAndLaunchAsync(update)) ExitThread();
        }
        catch (Exception ex)
        {
            _updating = false;
            Log.Error("Update failed", ex);
            Notify("Update failed", ex is InvalidOperationException ? ex.Message : "The download didn't work. Try again later (details in the log).",
                ToolTipIcon.Error, critical: true);
        }
    }

    // ---------- Windows ----------
    private void ShowStatus()
    {
        if (_statusForm is { IsDisposed: false })
        {
            _statusForm.Activate();
            return;
        }
        _statusForm = new StatusForm(_services);
        _statusForm.FormClosed += (_, _) => _statusForm = null;
        _statusForm.Show();
    }

    private void ShowDiagnosis() => new DiagnosisForm(_services).Show();

    private void ShowAsk(string? question)
    {
        if (_askForm is { IsDisposed: false })
        {
            _askForm.SetQuestion(question);
            _askForm.Activate();
            return;
        }
        _askForm = new AskBuddyForm(_services, question);
        _askForm.FormClosed += (_, _) => _askForm = null;
        _askForm.Show();
    }

    private static void OpenStartupPlace(StartupPlace place)
    {
        switch (place)
        {
            case StartupPlace.ScheduledTask: OpenFile("mmc.exe", "taskschd.msc"); break;
            case StartupPlace.Service: OpenFile("mmc.exe", "services.msc"); break;
            case StartupPlace.StartupFolder: OpenFile("explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.Startup)); break;
            default: OpenUri("windowsdefender://threat"); break;
        }
    }

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Opening {uri} failed", ex);
        }
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
        _updateTimer.Stop();
        _tray.Visible = false;
        bool loopStopped;
        try { loopStopped = _loop.Wait(TimeSpan.FromSeconds(10)); } catch { loopStopped = true; }
        if (loopStopped)
        {
            _temps.Dispose(); // never close the sensor driver while a read is still running
            _idleHogs.Dispose();
            Nvml.Close();
        }
        _downloads?.Dispose();
        _tray.Dispose();
        _statusForm?.Close();
        _askForm?.Close();
        base.ExitThreadCore();
    }
}
