using System.Text.Json;

namespace DesktopBuddy;

/// <summary>User-editable settings, stored as JSON in %AppData%\DesktopBuddy\settings.json.</summary>
public sealed class Settings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopBuddy");

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Bumped when a default changes in a way existing settings files should pick up.</summary>
    public int SettingsVersion { get; set; } // 0 when missing from an older file

    private const int CurrentSettingsVersion = 2;

    // --- Resource hog alerts ---
    public int PollSeconds { get; set; } = 5;
    /// <summary>Percent of the whole CPU (all cores) an app must use to count as "heavy".</summary>
    public double AppCpuPercentThreshold { get; set; } = 25;
    public int AppMemoryMbThreshold { get; set; } = 3072;
    /// <summary>How long an app must stay over a threshold before you get an alert.</summary>
    public int SustainedMinutes { get; set; } = 3;
    /// <summary>Minimum time between two alerts about the same app or sensor.</summary>
    public int AlertCooldownMinutes { get; set; } = 30;
    /// <summary>Warn when total RAM use stays above this percent.</summary>
    public uint RamPercentWarn { get; set; } = 90;
    public List<string> ResourceIgnoreList { get; set; } = ["Idle", "System", "Memory Compression", "Registry"];

    // --- Temperatures ---
    public double CpuTempWarnC { get; set; } = 90;
    public double GpuTempWarnC { get; set; } = 85;
    /// <summary>Temps must stay over the limit this long before an alert (ignores short spikes).</summary>
    public int TempSustainedSeconds { get; set; } = 60;

    // --- Suspicious process scanning ---
    public bool ScanForSuspiciousProcesses { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 20;
    public bool WatchStartupEntries { get; set; } = true;
    public bool WatchDefender { get; set; } = true;
    /// <summary>Scheduled tasks, services, Startup folder, Winlogon.</summary>
    public bool WatchHiddenStartup { get; set; } = true;
    public bool WatchDownloads { get; set; } = true;

    // --- Heat slowdown (throttling) ---
    /// <summary>How long the CPU/GPU must keep slowing down from heat before you get an alert.</summary>
    public int HeatSlowdownAlertSeconds { get; set; } = 30;

    // --- Hidden miner / idle hog check ---
    public int IdleMinutes { get; set; } = 5;
    public double IdleGpuPercent { get; set; } = 30;
    public double IdleCpuPercent { get; set; } = 40;

    // --- Downloads tidy (off until you turn it on) ---
    public bool TidyDownloads { get; set; }
    public int TidyAfterDays { get; set; } = 14;

    // --- Disk ---
    public int DiskFreeWarnGb { get; set; } = 20;

    // --- Games (popups wait and you get a report afterwards) ---
    public List<string> GameProcessNames { get; set; } =
    [
        "RobloxPlayerBeta", "Minecraft.Windows", "FortniteClient-Win64-Shipping", "VALORANT-Win64-Shipping",
        "cs2", "GTA5", "GTA5_Enhanced", "r5apex", "r5apex_dx12", "RocketLeague",
    ];

    /// <summary>Start automatically when you log in (a Task Scheduler task, so no admin prompt).</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Check GitHub for a newer version at startup and every few hours.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Apps "Get game-ready" ticks by default (your last choice is remembered).</summary>
    public List<string> GameReadyCloseList { get; set; } = ["chrome", "msedge", "firefox", "opera", "spotify", "ms-teams"];
    public bool ReopenBrowserAfterGame { get; set; }
    public string? BrowserToReopen { get; set; }
    /// <summary>Switch Windows' power mode to Best performance during games, and back afterwards.</summary>
    public bool BestPerformanceDuringGames { get; set; } = true;

    /// <summary>Apps you told to always use the RTX; kept fixed when they update into a new folder.</summary>
    public List<string> ForceRtxApps { get; set; } = [];
    public bool WatchGraphicsChip { get; set; } = true;

    // --- AI (only used once you add an API key) ---
    /// <summary>Claude Haiku 4.5 is the cheapest and plenty for this app; pick another in the AI setup window.</summary>
    public string AiModel { get; set; } = "claude-haiku-4-5";
    public double AiMonthlyBudgetUsd { get; set; } = 2.00;
    public int AiMaxCallsPerDay { get; set; } = 100;

    /// <summary>Full exe paths you clicked "Always allow" on.</summary>
    public List<string> AllowedExePaths { get; set; } = [];

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null)
                {
                    if (loaded.SettingsVersion < CurrentSettingsVersion)
                    {
                        // v1.1 defaulted to Opus before a model picker existed; switch to the cheaper recommended model.
                        if (loaded.AiModel == "claude-opus-5-5") loaded.AiModel = "claude-haiku-4-5";
                        loaded.SettingsVersion = CurrentSettingsVersion;
                    }
                    loaded.Save(); // writes back any settings added in newer versions
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"settings.json could not be read, using defaults.\n\n{ex.Message}", "Desktop Buddy",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return new Settings(); // don't overwrite the broken file; let the user fix it
        }

        var settings = new Settings { SettingsVersion = CurrentSettingsVersion };
        settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
