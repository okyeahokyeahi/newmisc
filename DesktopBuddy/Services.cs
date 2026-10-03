using DesktopBuddy.Ai;
using DesktopBuddy.Monitors;

namespace DesktopBuddy;

/// <summary>Everything the windows need, passed around as one object.</summary>
internal sealed class BuddyServices
{
    public required Settings Settings { get; init; }
    public required ResourceMonitor Resources { get; init; }
    public required TemperatureMonitor Temps { get; init; }
    public required HeatSlowdownMonitor Heat { get; init; }
    public required SuspiciousProcessScanner Scanner { get; init; }
    public required DefenderMonitor Defender { get; init; }
    public required DiskMonitor Disk { get; init; }
    public required GameSessionTracker Games { get; init; }
    public required LaptopCareMonitor Care { get; init; }
    public required AiClient Ai { get; init; }

    public required Action ShowDiagnosis { get; init; }
    /// <summary>Opens Ask Buddy, optionally with a question typed in.</summary>
    public required Action<string?> ShowAsk { get; init; }
    /// <summary>(process name, exe path or null, flag reasons or null)</summary>
    public required Action<string, string?, IReadOnlyList<string>?> ShowExplain { get; init; }
    public required Action ShowApiKey { get; init; }
    public required Action ShowGameReady { get; init; }
    public required Action ShowStatus { get; init; }
    public required ReminderStore Reminders { get; init; }
    public required Action ShowScreenAsk { get; init; }
    public required Action ShowSettings { get; init; }
    public required Action ShowWeeklyReport { get; init; }
    public required Action<int> PauseAlerts { get; init; }

    /// <summary>The most recent notification ("title: text"), so you can ask Buddy what it meant.</summary>
    public string? LastAlert { get; set; }
}
