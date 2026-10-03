using System.Globalization;
using DesktopBuddy.Ai;
using DesktopBuddy.Monitors;

namespace DesktopBuddy.UI;

/// <summary>The last 7 days vs the 7 before: memory, heat, games, startup, disk, battery, clip storage.</summary>
internal sealed class WeeklyReportForm : Form
{
    private sealed record Section(string Heading, string Body, Color? Color = null);

    private readonly BuddyServices _s;
    private readonly HealthLog _log;
    private readonly RichTextBox _text = Ui.ReadOnlyText();
    private readonly Button _aiButton;
    private readonly CancellationTokenSource _closing = new();
    private List<Section> _sections = [];

    public WeeklyReportForm(BuddyServices services, HealthLog log)
    {
        _s = services;
        _log = log;
        Ui.Setup(this, "Weekly health report", 560, 600);

        _aiButton = Ui.Button(_s.Ai.HasKey ? "Sum it up (AI)" : "Sum it up (needs API key)", async (_, _) => await AskAi());
        _aiButton.Enabled = false;
        _aiButton.Visible = _s.Settings.AiWeeklySummary;
        var close = Ui.Button("Close", (_, _) => Close());
        CancelButton = close;

        var padded = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 4) };
        padded.Controls.Add(_text);
        Controls.Add(padded);
        Controls.Add(Ui.ButtonRow(close, _aiButton));
        Ui.AppendBody(_text, "Gathering the week's numbers…", Ui.Grey);

        Load += async (_, _) => await Fill();
        FormClosed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };
    }

    private async Task Fill()
    {
        try
        {
            CancellationToken token = _closing.Token;
            _sections = await Task.Run(() => Build(token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Building the weekly report failed", ex);
            _sections = [new Section("Something went wrong", "Couldn't build the report (details are in the log).", Ui.Amber)];
        }
        if (IsDisposed) return;

        _text.Clear();
        foreach (Section s in _sections)
        {
            Ui.AppendHeading(_text, s.Heading, s.Color);
            Ui.AppendBody(_text, s.Body, s.Color);
        }
        Ui.AppendBody(_text, "Buddy keeps about 60 days of daily numbers in health-days.json in the log folder.", Ui.Grey);
        _text.SelectionStart = 0;
        _text.ScrollToCaret();
        _aiButton.Enabled = true;
    }

    // ---------- The numbers ----------
    private List<Section> Build(CancellationToken cancel)
    {
        var sections = new List<Section>();
        Settings settings = _s.Settings;
        DateTime today = DateTime.Today;
        string weekStart = today.AddDays(-6).ToString("yyyy-MM-dd"), prevStart = today.AddDays(-13).ToString("yyyy-MM-dd");
        IReadOnlyList<DayStats> days = _log.Days();
        var week = days.Where(d => string.CompareOrdinal(d.Day, weekStart) >= 0).ToList();
        var prev = days.Where(d => string.CompareOrdinal(d.Day, prevStart) >= 0 && string.CompareOrdinal(d.Day, weekStart) < 0).ToList();
        bool hasPrev = prev.Sum(d => d.OnMinutes) >= 120;

        double onHours = week.Sum(d => d.OnMinutes) / 60;
        sections.Add(new Section($"The week of {today.AddDays(-6):MMM d} to {today:MMM d}",
            week.Count == 0
                ? "No numbers yet. Buddy needs to run for a few days first."
                : $"Buddy watched for about {onHours:0} hours across {week.Count(d => d.OnMinutes >= 10)} days." +
                  (hasPrev ? " Arrows compare with the week before." : " Next week's report will compare with this one.")));

        // Memory
        double? ram = Average(week, d => d.AverageRam, d => d.Samples), ramPrev = Average(prev, d => d.AverageRam, d => d.Samples);
        double full = Share(week), fullPrev = Share(prev);
        if (ram is double avgRam)
        {
            string body = $"Average RAM use {avgRam:0}%{Change(avgRam, hasPrev ? ramPrev : null, "%")}. " +
                          $"Very full (90%+) {full:P0} of the time{Change(full * 100, hasPrev ? fullPrev * 100 : null, " pts")}.";
            if (full >= 0.25)
                body += "\nThat's a lot. Closing Chrome and Studio before games helps today; a second 16 GB stick is the real fix " +
                        "(the AN515-57 has two slots and takes up to 32 GB of DDR4-3200).";
            sections.Add(new Section("Memory", body, full >= 0.25 ? Ui.Amber : null));
        }

        // Heat
        if (week.Any(d => d.PeakCpuC != null || d.PeakGpuC != null))
        {
            double? peakCpu = week.Max(d => d.PeakCpuC), peakGpu = week.Max(d => d.PeakGpuC);
            double slowMin = week.Sum(d => d.CpuSlowSeconds + d.GpuSlowSeconds) / 60;
            double slowPrevMin = prev.Sum(d => d.CpuSlowSeconds + d.GpuSlowSeconds) / 60;
            bool bad = slowMin >= 10;
            string body = $"Peak CPU {Format.Temp(peakCpu)} · peak GPU {Format.Temp(peakGpu)}.\n" +
                          (slowMin < 0.5 ? "No slowing down from heat. Cooling kept up."
                              : $"Slowed down from heat for {slowMin:0} minutes{Change(slowMin, hasPrev ? slowPrevMin : null, " min")}.");
            if (bad) body += " Max fan in NitroSense while gaming and lift the back of the laptop. If it keeps growing week to week, the vents need cleaning.";
            sections.Add(new Section("Heat", body, bad ? Ui.Red : peakCpu >= settings.CpuTempWarnC || peakGpu >= settings.GpuTempWarnC ? Ui.Amber : Ui.Green));
        }

        // Games
        var sessions = ReadSessions();
        var thisWeek = sessions.Where(x => x.Start.Date >= today.AddDays(-6)).ToList();
        var lastWeek = sessions.Where(x => x.Start.Date >= today.AddDays(-13) && x.Start.Date < today.AddDays(-6)).ToList();
        if (thisWeek.Count > 0)
        {
            double hours = thisWeek.Sum(x => x.Minutes) / 60, hoursPrev = lastWeek.Sum(x => x.Minutes) / 60;
            var top = thisWeek.GroupBy(x => x.Game, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Game: g.Key, Hours: g.Sum(x => x.Minutes) / 60)).OrderByDescending(g => g.Hours).Take(3);
            double lateMin = thisWeek.Where(x => x.Start.Hour >= 23 || x.Start.Hour < 5).Sum(x => x.Minutes);
            string body = $"{thisWeek.Count} session(s), {hours:0.0} hours{Change(hours, lastWeek.Count > 0 ? hoursPrev : null, " h")}.\n" +
                          "Most played: " + string.Join(", ", top.Select(g => $"{Diagnosis.FriendlyName(g.Game)} {g.Hours:0.0} h")) + ".";
            double? avgPeakGpu = thisWeek.Where(x => x.PeakGpu != null).Select(x => x.PeakGpu!.Value).DefaultIfEmpty().Average() is double a && a > 0 ? a : null;
            if (avgPeakGpu != null) body += $"\nTypical peak GPU while playing: {avgPeakGpu:0}°C.";
            if (lateMin >= 60) body += $"\n{lateMin / 60:0.0} hours of that started after 11 pm.";
            sections.Add(new Section("Games", body));
        }
        else
        {
            sections.Add(new Section("Games", "No game sessions this week."));
        }

        // Startup
        var boots = week.Where(d => d.BootSeconds != null).Select(d => d.BootSeconds!.Value).ToList();
        var bootsPrev = prev.Where(d => d.BootSeconds != null).Select(d => d.BootSeconds!.Value).ToList();
        if (boots.Count > 0)
        {
            double median = Median(boots);
            bool slow = bootsPrev.Count > 0 && median > Median(bootsPrev) * 1.4 && median > 45;
            sections.Add(new Section("Startup",
                $"Windows usually took {median:0}s to start{Change(median, bootsPrev.Count > 0 ? Median(bootsPrev) : null, "s")}." +
                (slow ? " Noticeably slower than last week: check Task Manager > Startup apps for something new." : ""),
                slow ? Ui.Amber : null));
        }

        // Disk
        if (_s.Disk.FreeBytes is long free)
        {
            double freeGb = free / (1024d * 1024 * 1024);
            double? weekAgo = week.FirstOrDefault(d => d.DiskFreeGb != null)?.DiskFreeGb;
            double change = weekAgo is double w ? freeGb - w : 0;
            bool low = freeGb < settings.DiskFreeWarnGb;
            sections.Add(new Section("Drive C:",
                $"{freeGb:0} GB free." + (Math.Abs(change) >= 2 ? change < 0 ? $" Down {-change:0} GB this week." : $" Up {change:0} GB this week." : " About the same as a week ago."),
                low ? Ui.Amber : null));
        }

        cancel.ThrowIfCancellationRequested();

        // Clips
        if (ClipStorage.Measure(cancel) is { TotalBytes: > 0 } clips)
        {
            bool big = clips.TotalBytes >= 20L * 1024 * 1024 * 1024;
            sections.Add(new Section("Game clips & recordings",
                $"{Format.Bytes(clips.TotalBytes)} in Videos (and Roblox screenshots).\n" +
                string.Join("\n", clips.Biggest.Select(f => $"• {Path.GetFileName(f.Path.TrimEnd('\\'))}: {Format.Bytes(f.Bytes)}")) +
                (big ? "\nOld clips are the easiest space to win back: move the ones you want to keep to a USB drive." : ""),
                big ? Ui.Amber : null));
        }

        // Battery
        if (BatteryHealth.Read() is { } battery)
        {
            double pct = battery.Percent;
            sections.Add(new Section("Battery",
                $"Holds {battery.FullWh:0} Wh of its original {battery.DesignedWh:0} Wh ({pct:0}%)." +
                (pct < 80
                    ? " It's worn. Since the laptop is always plugged in, turn on the battery charge limit (80%) in NitroSense or Acer Care Center if yours has it: sitting at 100% wears it fastest."
                    : " Fine. Being plugged in all the time wears it slowly; an 80% charge limit (NitroSense or Acer Care Center, if offered) keeps it that way."),
                pct < 70 ? Ui.Amber : null));
        }

        return sections;
    }

    // ---------- AI summary ----------
    private async Task AskAi()
    {
        if (!_s.Ai.HasKey)
        {
            _s.ShowApiKey();
            _aiButton.Text = _s.Ai.HasKey ? "Sum it up (AI)" : "Sum it up (needs API key)";
            return;
        }

        _aiButton.Enabled = false;
        _aiButton.Text = "Thinking…";
        try
        {
            string report = string.Join("\n\n", _sections.Select(s => $"{s.Heading}\n{s.Body}"));
            string answer = await _s.Ai.Ask(Prompts.System,
                [(true, "Here is my laptop's weekly health report:\n<weekly_report>\n" + report + "\n</weekly_report>\n\n" +
                        "In 3 short bullet points: what went well, what got worse, and the single most useful thing to do this week. Plain words, no jargon.")],
                _closing.Token);
            if (IsDisposed) return;
            Ui.AppendHeading(_text, "Buddy's summary (AI)", Ui.Grey);
            Ui.AppendBody(_text, answer);
            _text.ScrollToCaret();
        }
        catch (AiUnavailableException ex)
        {
            if (!IsDisposed) Ui.AppendBody(_text, ex.Message, Ui.Amber);
        }
        catch (OperationCanceledException)
        {
            // window closed mid-question
        }
        catch (Exception ex)
        {
            Log.Error("Weekly report AI summary failed", ex);
            if (!IsDisposed) Ui.AppendBody(_text, "Something went wrong. Try again (details are in the log).", Ui.Amber);
        }
        finally
        {
            if (!IsDisposed)
            {
                _aiButton.Enabled = true;
                _aiButton.Text = "Sum it up again";
            }
        }
    }

    // ---------- Helpers ----------
    private sealed record Session(DateTime Start, string Game, double Minutes, double? PeakGpu);

    private static List<Session> ReadSessions()
    {
        var list = new List<Session>();
        string file = Path.Combine(Settings.Folder, "game-sessions.csv");
        if (!File.Exists(file)) return list;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            reader.ReadLine(); // header
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                string[] c = line.Split(',');
                if (c.Length < 5 || !DateTime.TryParseExact(c[0], ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH.mm"], CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime start)) continue;
                list.Add(new Session(start, c[1], Num(c[2]) ?? 0, Num(c[4])));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading game sessions for the weekly report failed", ex);
        }
        return list;
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static double? Average(List<DayStats> days, Func<DayStats, double?> value, Func<DayStats, int> weight)
    {
        double sum = 0, total = 0;
        foreach (DayStats d in days)
        {
            if (value(d) is not double v) continue;
            sum += v * weight(d);
            total += weight(d);
        }
        return total > 0 ? sum / total : null;
    }

    private static double Share(List<DayStats> days)
    {
        int samples = days.Sum(d => d.Samples);
        return samples > 0 ? (double)days.Sum(d => d.RamFullSamples) / samples : 0;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>" (↑ 4%)" style comparison, or "" when there's nothing to compare with.</summary>
    private static string Change(double now, double? before, string unit)
    {
        if (before is not double b) return "";
        double diff = now - b;
        if (Math.Abs(diff) < 0.5) return " (same as last week)";
        return $" ({(diff > 0 ? "↑" : "↓")} {Math.Abs(diff):0}{unit} vs last week)";
    }
}
