using System.Management;
using System.Text.Json;

namespace DesktopBuddy.Monitors;

public sealed record DefenderStatus(
    bool Readable,
    string Mode,              // "Normal", "Passive Mode", ...
    bool RealTimeOn,
    bool TamperProtected,
    int SignatureAgeDays,
    string? OtherAntivirus,   // set when a third-party antivirus is in charge
    IReadOnlyList<string> Exclusions);

/// <summary>
/// Watches Microsoft Defender for the #1 trick fake game cheats/executors use: "turn off your
/// antivirus" or quietly adding a do-not-scan exclusion. Alerts when protection goes off or a new
/// exclusion appears (also ones added while Desktop Buddy wasn't running).
/// </summary>
public sealed class DefenderMonitor
{
    private static readonly string BaselinePath = Path.Combine(Settings.Folder, "defender-exclusions.json");

    private bool? _lastRealTime;
    private HashSet<string>? _knownExclusions;
    private DateOnly _lastSignatureNag;

    public DefenderStatus? Latest { get; private set; }

    /// <summary>(title, message, critical)</summary>
    public event Action<string, string, bool>? Alert;

    public void Tick()
    {
        DefenderStatus status = Read();
        Latest = status;
        if (!status.Readable) return;

        bool defenderInCharge = status.Mode.Equals("Normal", StringComparison.OrdinalIgnoreCase);
        if (defenderInCharge)
        {
            if (!status.RealTimeOn && _lastRealTime != false)
            {
                Alert?.Invoke("Antivirus protection is OFF",
                    "Windows Defender real-time protection is turned off. Fake game cheats and \"executors\" ask you to do this so " +
                    "they can steal your accounts. Click to turn it back on (Real-time protection).", true);
            }
            _lastRealTime = status.RealTimeOn;

            if (status.SignatureAgeDays > 7 && _lastSignatureNag != DateOnly.FromDateTime(DateTime.Now))
            {
                _lastSignatureNag = DateOnly.FromDateTime(DateTime.Now);
                Alert?.Invoke("Antivirus definitions are old",
                    $"Defender's virus definitions are {status.SignatureAgeDays} days old. Run Windows Update to refresh them.", false);
            }
        }

        CheckExclusions(status.Exclusions);
    }

    private void CheckExclusions(IReadOnlyList<string> current)
    {
        bool firstEverRun = false;
        if (_knownExclusions == null)
        {
            var saved = LoadBaseline();
            firstEverRun = saved == null;
            _knownExclusions = saved ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var added = current.Where(e => !_knownExclusions.Contains(e)).ToList();
        if (added.Count == 0)
        {
            if (current.Count != _knownExclusions.Count || firstEverRun)
            {
                // some were removed (or first run with none): remember the new state so a re-add alerts again
                _knownExclusions = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
                SaveBaseline(current);
            }
            return;
        }

        string list = string.Join(", ", added.Take(3)) + (added.Count > 3 ? $" (+{added.Count - 3} more)" : "");
        if (firstEverRun)
        {
            Alert?.Invoke("Defender has scan exclusions",
                $"These are excluded from virus scans: {list}. If you don't recognise them, something may have added them. " +
                "Click to review (Virus & threat protection settings > Exclusions).", false);
        }
        else
        {
            Alert?.Invoke("Something told Defender to stop scanning",
                $"New do-not-scan exclusion: {list}. Malware does this to hide. If you didn't add it yourself, click and remove it " +
                "(Virus & threat protection settings > Exclusions), then run a full scan.", true);
        }

        foreach (string e in added) _knownExclusions.Add(e);
        SaveBaseline(_knownExclusions.ToList());
    }

    private static DefenderStatus Read()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Defender");
            scope.Connect();

            string mode = "Unknown";
            bool realTime = false, tamper = false;
            int sigAge = 0;
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                       "SELECT AMRunningMode, RealTimeProtectionEnabled, IsTamperProtected, AntivirusSignatureAge FROM MSFT_MpComputerStatus")))
            {
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using (o)
                    {
                        mode = o["AMRunningMode"]?.ToString() ?? "Unknown";
                        realTime = o["RealTimeProtectionEnabled"] is true;
                        tamper = o["IsTamperProtected"] is true;
                        sigAge = Convert.ToInt32(o["AntivirusSignatureAge"] ?? 0);
                    }
                }
            }

            var exclusions = new List<string>();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                       "SELECT ExclusionPath, ExclusionProcess, ExclusionExtension FROM MSFT_MpPreference")))
            {
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using (o)
                    {
                        foreach (string field in new[] { "ExclusionPath", "ExclusionProcess", "ExclusionExtension" })
                        {
                            if (o[field] is string[] values)
                                exclusions.AddRange(values.Where(v => !string.IsNullOrWhiteSpace(v) && !v.StartsWith("N/A", StringComparison.Ordinal)));
                        }
                    }
                }
            }

            string? other = mode.Equals("Normal", StringComparison.OrdinalIgnoreCase) ? null : OtherAntivirusName();
            return new DefenderStatus(true, mode, realTime, tamper, sigAge, other, exclusions);
        }
        catch (Exception ex)
        {
            Log.Error("Reading Defender status failed", ex);
            return new DefenderStatus(false, "Unknown", false, false, 0, null, []);
        }
    }

    /// <summary>Name of a third-party antivirus registered with Windows Security Center, if any.</summary>
    private static string? OtherAntivirusName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    string? name = o["displayName"]?.ToString();
                    if (name != null && !name.Contains("Defender", StringComparison.OrdinalIgnoreCase)) return name;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading Security Center failed", ex);
        }
        return null;
    }

    private static HashSet<string>? LoadBaseline()
    {
        try
        {
            if (!File.Exists(BaselinePath)) return null;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(BaselinePath));
            return list == null ? null : new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Reading Defender baseline failed", ex);
            return null;
        }
    }

    private static void SaveBaseline(IEnumerable<string> exclusions)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(BaselinePath, JsonSerializer.Serialize(exclusions.ToList()));
        }
        catch (Exception ex)
        {
            Log.Error("Saving Defender baseline failed", ex);
        }
    }
}
