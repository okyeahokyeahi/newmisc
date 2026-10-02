using System.Diagnostics;
using DesktopBuddy.Native;

namespace DesktopBuddy.Monitors;

public sealed record GameSessionReport(
    string Game,
    DateTime Started,
    TimeSpan Length,
    double? PeakCpuC,
    double? PeakGpuC,
    uint PeakRamPercent,
    TimeSpan CpuHeatSlowdown,
    TimeSpan GpuHeatSlowdown,
    IReadOnlyList<string> HeldAlerts);

/// <summary>
/// Game mode: while a game runs, popups and alerts wait. Afterwards you get a short report: how long
/// you played, peak temperatures and RAM, and whether anything slowed down from heat.
/// A game = a name from Settings.GameProcessNames, anything installed in a Steam/Epic/Riot games
/// folder, or any exclusive full-screen Direct3D app.
/// </summary>
public sealed class GameSessionTracker(Settings settings, ResourceMonitor resources, TemperatureMonitor temps, HeatSlowdownMonitor heat)
{
    private static readonly string[] GameFolderHints = [@"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\XboxGames\", @"\EA Games\"];

    // Things that live in game folders but aren't games (they'd keep game mode on forever).
    private static readonly string[] NotGames =
        ["wallpaper32", "wallpaper64", "webwallpaper32", "steamwebhelper", "crashhandler", "unitycrashhandler64", "easyanticheat",
         "easyanticheat_eos", "beservice", "riotclientservices", "epicwebhelper", "launcher", "updater"];

    private readonly Dictionary<string, bool> _isGameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _held = [];
    private readonly object _heldGate = new();
    private DateTime? _start;
    private DateTime _lastSeen;
    private string _game = "";
    private double? _peakCpu, _peakGpu;
    private uint _peakRam;
    private TimeSpan _cpuSlowAtStart, _gpuSlowAtStart;

    public bool InSession { get; private set; }
    public GameSessionReport? LastReport { get; private set; }

    public event Action<GameSessionReport>? SessionEnded;
    public event Action<string>? SessionStarted;

    /// <summary>Raised when a game stops, even short ones that don't produce a report.</summary>
    public event Action? GameStopped;

    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        string? game = DetectGame();

        if (game != null)
        {
            _lastSeen = now;
            if (!InSession)
            {
                lock (_heldGate) InSession = true;
                _start = now;
                _game = game;
                _peakCpu = _peakGpu = null;
                _peakRam = 0;
                _cpuSlowAtStart = heat.Latest?.CpuToday ?? TimeSpan.Zero;
                _gpuSlowAtStart = heat.Latest?.GpuToday ?? TimeSpan.Zero;
                Log.Info($"Game session started: {game}");
                SessionStarted?.Invoke(game);
            }
        }

        if (!InSession) return;

        TemperatureSnapshot? t = temps.Latest;
        _peakCpu = Max(_peakCpu, t?.CpuC);
        _peakGpu = Max(_peakGpu, t?.GpuC);
        _peakRam = Math.Max(_peakRam, resources.Latest?.RamPercent ?? 0);

        // End after a minute without the game (covers loading screens and alt-tabbing).
        if (game == null && now - _lastSeen > TimeSpan.FromSeconds(60)) End();
    }

    /// <summary>Called for alerts that arrive mid-game. Returns false if no game is running.</summary>
    public bool Hold(string alert)
    {
        lock (_heldGate)
        {
            if (!InSession) return false;
            _held.Add($"{DateTime.Now:HH:mm} {alert}");
            return true;
        }
    }

    private void End()
    {
        TimeSpan length = _lastSeen - _start!.Value;
        List<string> held;
        lock (_heldGate)
        {
            InSession = false;
            held = [.. _held];
            _held.Clear();
        }

        var heatNow = heat.Latest;
        var report = new GameSessionReport(
            _game, _start.Value.ToLocalTime(), length, _peakCpu, _peakGpu, _peakRam,
            Clamp((heatNow?.CpuToday ?? TimeSpan.Zero) - _cpuSlowAtStart),
            Clamp((heatNow?.GpuToday ?? TimeSpan.Zero) - _gpuSlowAtStart),
            held);
        Log.Info($"Game session ended: {_game}, {length:h\\:mm\\:ss}");
        GameStopped?.Invoke();

        if (length < TimeSpan.FromMinutes(2) && held.Count == 0) return; // a quick launcher/menu blip
        LastReport = report;
        AppendCsv(report);
        SessionEnded?.Invoke(report);
    }

    private string? DetectGame()
    {
        // Named games and games installed in a launcher's library folder.
        foreach (AppUsage app in resources.Latest?.AllApps ?? [])
        {
            if (settings.GameProcessNames.Contains(app.Name, StringComparer.OrdinalIgnoreCase)) return app.Name;
            if (app.CpuPercent >= 3 && !NotGames.Contains(app.Name.ToLowerInvariant()) && IsInGameFolder(app.Name)) return app.Name;
        }
        return NativeMethods.FullScreenGameRunning() ? "Full-screen game" : null;
    }

    private bool IsInGameFolder(string processName)
    {
        if (_isGameCache.TryGetValue(processName, out bool cached)) return cached;
        bool result = false;
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            string? path = processes.Select(p => NativeMethods.GetProcessPath(p.Id)).FirstOrDefault(p => p != null);
            result = path != null && GameFolderHints.Any(h => path.Contains(h, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (Process p in processes) p.Dispose();
        }
        return _isGameCache[processName] = result;
    }

    private static void AppendCsv(GameSessionReport r)
    {
        try
        {
            string file = Path.Combine(Settings.Folder, "game-sessions.csv");
            if (!File.Exists(file))
                File.WriteAllText(file, "started,game,minutes,peak_cpu_c,peak_gpu_c,peak_ram_pct,cpu_heat_slowdown_s,gpu_heat_slowdown_s\n");
            File.AppendAllText(file,
                $"{r.Started:yyyy-MM-dd HH:mm},{r.Game.Replace(',', ' ')},{r.Length.TotalMinutes:0},{r.PeakCpuC:0},{r.PeakGpuC:0}," +
                $"{r.PeakRamPercent},{r.CpuHeatSlowdown.TotalSeconds:0},{r.GpuHeatSlowdown.TotalSeconds:0}\n");
        }
        catch (Exception ex)
        {
            Log.Error("Writing game session log failed", ex);
        }
    }

    private static double? Max(double? a, double? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);
    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
