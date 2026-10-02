using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Xml.Linq;

namespace DesktopBuddy.Monitors;

public sealed record CareTip(string Title, string Message, string? Url);

/// <summary>
/// Slow-moving laptop care, checked about once an hour, each tip at most every few weeks:
///  - "Runs hotter than it used to": same game, recent sessions vs your first ones (game-sessions.csv).
///  - Old NVIDIA driver.
///  - Windows starting much slower than usual, and what Windows blamed for it.
/// </summary>
public sealed class LaptopCareMonitor
{
    private static readonly string StatePath = Path.Combine(Settings.Folder, "care-state.json");
    private static readonly string SessionsCsv = Path.Combine(Settings.Folder, "game-sessions.csv");

    private sealed class State
    {
        public DateTime? LastVentTip { get; set; }
        public DateTime? LastDriverTip { get; set; }
        public long LastBootRecordSeen { get; set; }
    }

    private State? _state;

    /// <summary>Latest boot duration and the usual one, for the status window.</summary>
    public (TimeSpan Last, TimeSpan Usual)? BootTimes { get; private set; }
    public (string Version, DateTime Date)? NvidiaDriver { get; private set; }

    public event Action<CareTip>? Tip;

    public void Tick()
    {
        _state ??= Load();
        CheckVents();
        CheckNvidiaDriver();
        CheckBootTime();
        Save(_state);
    }

    // ---------- Dusty vents ----------
    private void CheckVents()
    {
        if (_state!.LastVentTip > DateTime.Now.AddDays(-60) || !File.Exists(SessionsCsv)) return;

        var rows = new List<(DateTime Start, string Game, double Minutes, double? Cpu, double? Gpu)>();
        foreach (string line in File.ReadLines(SessionsCsv).Skip(1))
        {
            string[] c = line.Split(',');
            if (c.Length < 5 || !DateTime.TryParseExact(c[0], ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH.mm"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime start)) continue; // older rows may use a '.' time separator
            rows.Add((start, c[1], Num(c[2]) ?? 0, Num(c[3]), Num(c[4])));
        }

        // Compare like with like: one game, sessions of 15+ minutes.
        foreach (var game in rows.Where(r => r.Minutes >= 15).GroupBy(r => r.Game))
        {
            var sessions = game.OrderBy(r => r.Start).ToList();
            if (sessions.Count < 20 || (sessions[^1].Start - sessions[0].Start).TotalDays < 21) continue;
            var first = sessions.Take(10).ToList();
            var last = sessions.TakeLast(10).ToList();
            double? cpuRise = Median(last.Select(r => r.Cpu)) - Median(first.Select(r => r.Cpu));
            double? gpuRise = Median(last.Select(r => r.Gpu)) - Median(first.Select(r => r.Gpu));
            double rise = Math.Max(cpuRise ?? 0, gpuRise ?? 0);
            if (rise < 6) continue;

            _state.LastVentTip = DateTime.Now;
            string part = (cpuRise ?? 0) >= (gpuRise ?? 0) ? "CPU" : "GPU";
            Tip?.Invoke(new CareTip("Your laptop may need a clean",
                $"In {Diagnosis.FriendlyName(game.Key)} your {part} now peaks about {rise:0}°C hotter than in {first[0].Start:MMMM}. " +
                "Dust in the vents is the usual reason (a warmer room can be too). A can of compressed air in the side and back vents, " +
                "with the laptop off, often fixes it. Click for a guide.",
                "https://www.youtube.com/results?search_query=clean+acer+nitro+5+vents+compressed+air"));
            return;
        }
    }

    private static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static double? Median(IEnumerable<double?> values)
    {
        var v = values.OfType<double>().OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    // ---------- NVIDIA driver age ----------
    private void CheckNvidiaDriver()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    if (o["Name"]?.ToString()?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) != true) continue;
                    string? raw = o["DriverDate"]?.ToString();
                    if (raw == null || raw.Length < 8 ||
                        !DateTime.TryParseExact(raw[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)) continue;
                    string version = o["DriverVersion"]?.ToString() ?? "?";
                    NvidiaDriver = (version, date);

                    if ((DateTime.Now - date).TotalDays < 270 || _state!.LastDriverTip > DateTime.Now.AddDays(-30)) return;
                    _state.LastDriverTip = DateTime.Now;
                    Tip?.Invoke(new CareTip("Your NVIDIA driver is getting old",
                        $"It's from {date:MMMM yyyy}. Newer drivers often fix crashes and add game optimizations. Update it in the NVIDIA app " +
                        "(or click for NVIDIA's download page). Acer's own drivers are older but fine if everything works.",
                        "https://www.nvidia.com/en-us/software/nvidia-app/"));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading the graphics driver date failed", ex);
        }
    }

    // ---------- Boot time ----------
    private void CheckBootTime()
    {
        try
        {
            var boots = new List<(long RecordId, DateTime When, double Ms)>();
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]")
            {
                ReverseDirection = true, // newest first
            };
            using (var reader = new EventLogReader(query))
            {
                for (EventRecord? e = reader.ReadEvent(); e != null && boots.Count < 15; e = reader.ReadEvent())
                {
                    using (e)
                    {
                        double? ms = DataField(e, "BootTime");
                        // The restart after installing updates is always slow; don't count it.
                        bool afterUpdate = DataText(e, "BootIsRebootAfterInstall")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                        if (ms is > 0 && !afterUpdate && e.RecordId is long id && e.TimeCreated is DateTime when) boots.Add((id, when, ms.Value));
                    }
                }
            }
            if (boots.Count == 0) return;

            var previous = boots.Skip(1).Select(b => b.Ms).OrderBy(x => x).ToList();
            double usual = previous.Count > 0 ? previous[previous.Count / 2] : boots[0].Ms;
            BootTimes = (TimeSpan.FromMilliseconds(boots[0].Ms), TimeSpan.FromMilliseconds(usual));

            var latest = boots[0];
            if (latest.RecordId <= _state!.LastBootRecordSeen) return;
            _state.LastBootRecordSeen = latest.RecordId;
            if (previous.Count < 5 || latest.Ms < 60_000 || latest.Ms < usual * 1.6) return;

            string culprits = BootCulprits(latest.When);
            Tip?.Invoke(new CareTip("Windows started slowly",
                $"Startup took {latest.Ms / 1000:0}s (usually about {usual / 1000:0}s)." +
                (culprits.Length > 0 ? $" Windows blamed: {culprits}." : "") +
                " New startup apps are the usual cause: check Task Manager > Startup apps.",
                null));
        }
        catch (EventLogNotFoundException)
        {
            // log not available on this edition
        }
        catch (Exception ex)
        {
            Log.Error("Reading boot times failed", ex);
        }
    }

    /// <summary>Apps/drivers/services Windows flagged as slowing that boot (events 101-103).</summary>
    private static string BootCulprits(DateTime bootEventTime)
    {
        var names = new List<string>();
        var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
            "*[System[(EventID=101 or EventID=102 or EventID=103)]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (EventRecord? e = reader.ReadEvent(); e != null && names.Count < 3; e = reader.ReadEvent())
        {
            using (e)
            {
                if (e.TimeCreated is not DateTime when) continue;
                if (when < bootEventTime.AddMinutes(-10)) break;
                if (when > bootEventTime.AddMinutes(10)) continue;
                string? name = DataText(e, "FriendlyName") ?? DataText(e, "Name");
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }
        }
        return string.Join(", ", names);
    }

    private static double? DataField(EventRecord e, string name) =>
        double.TryParse(DataText(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static string? DataText(EventRecord e, string name)
    {
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        return XDocument.Parse(e.ToXml()).Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value;
    }

    private static State Load()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(State s)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(s));
        }
        catch (Exception ex)
        {
            Log.Error("Saving laptop-care state failed", ex);
        }
    }
}
