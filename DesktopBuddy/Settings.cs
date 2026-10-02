using System.Text.Json;

namespace DesktopBuddy;

/// <summary>User-editable settings, stored as JSON in %AppData%\DesktopBuddy\settings.json.</summary>
public sealed class Settings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopBuddy");

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

        var settings = new Settings();
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
