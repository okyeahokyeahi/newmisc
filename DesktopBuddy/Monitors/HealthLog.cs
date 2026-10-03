using System.Text.Json;

namespace DesktopBuddy.Monitors;

/// <summary>One day of numbers for the weekly report. Small on purpose: about 60 of these are kept.</summary>
public sealed class DayStats
{
    public string Day { get; set; } = "";
    public double OnMinutes { get; set; }
    public int Samples { get; set; }
    public double RamPercentSum { get; set; }
    public int RamFullSamples { get; set; }
    public double? PeakCpuC { get; set; }
    public double? PeakGpuC { get; set; }
    public int HotSamples { get; set; }
    public double CpuSlowSeconds { get; set; }
    public double GpuSlowSeconds { get; set; }
    public double? BootSeconds { get; set; }
    public double? DiskFreeGb { get; set; }

    public double? AverageRam => Samples > 0 ? RamPercentSum / Samples : null;
    public double RamFullShare => Samples > 0 ? (double)RamFullSamples / Samples : 0;

    public DayStats Copy() => (DayStats)MemberwiseClone();
}

/// <summary>
/// Writes a small daily summary (RAM, temps, heat slowdown, startup time, free disk) to health-days.json,
/// and says when the weekly report is due.
/// </summary>
public sealed class HealthLog(Settings settings, ResourceMonitor resources, TemperatureMonitor temps,
    HeatSlowdownMonitor heat, DiskMonitor disk, LaptopCareMonitor care)
{
    private const int KeepDays = 60;
    private static readonly string FilePath = Path.Combine(Settings.Folder, "health-days.json");

    private sealed class Store
    {
        public List<DayStats> Days { get; set; } = [];
        public DateTime? LastWeeklyNotice { get; set; }
        public DateTime? FirstSeen { get; set; }
    }

    private readonly object _gate = new();
    private Store? _store;
    private DateTime _lastTick = DateTime.UtcNow, _lastSave = DateTime.UtcNow;
    private TimeSpan? _lastCpuToday, _lastGpuToday;

    /// <summary>Called every monitoring loop pass (a few seconds apart).</summary>
    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        double minutes = Math.Min((now - _lastTick).TotalMinutes, 0.5); // a sleep/hibernate gap isn't "on" time
        _lastTick = now;

        lock (_gate)
        {
            Store store = _store ??= Load();
            store.FirstSeen ??= DateTime.Now;
            DayStats day = Today(store);
            day.OnMinutes += minutes;

            if (resources.Latest is { } r)
            {
                day.Samples++;
                day.RamPercentSum += r.RamPercent;
                if (r.RamPercent >= 90) day.RamFullSamples++;
            }
            if (temps.Latest is { } t)
            {
                day.PeakCpuC = Max(day.PeakCpuC, t.CpuC);
                day.PeakGpuC = Max(day.PeakGpuC, t.GpuC);
                if (t.CpuC >= settings.CpuTempWarnC || t.GpuC >= settings.GpuTempWarnC) day.HotSamples++;
            }
            if (heat.Latest is { } h)
            {
                // The heat monitor's "today" totals restart at 0 at midnight and whenever Buddy restarts,
                // so add what's new since the last look instead of copying the total.
                day.CpuSlowSeconds += Growth(ref _lastCpuToday, h.CpuToday);
                day.GpuSlowSeconds += Growth(ref _lastGpuToday, h.GpuToday);
            }
            if (disk.FreeBytes is long free) day.DiskFreeGb = free / (1024d * 1024 * 1024);
            if (care.LastBootAt is DateTime bootAt && care.BootTimes is var (last, _))
            {
                string bootDay = bootAt.ToString("yyyy-MM-dd");
                DayStats? bootRecord = store.Days.FirstOrDefault(d => d.Day == bootDay);
                if (bootRecord != null) bootRecord.BootSeconds = last.TotalSeconds;
            }

            if (now - _lastSave >= TimeSpan.FromMinutes(5)) SaveLocked();
        }
    }

    /// <summary>Saves now (called on exit so the last few minutes aren't lost).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_store != null) SaveLocked();
        }
    }

    /// <summary>Copies of the stored days, oldest first.</summary>
    public IReadOnlyList<DayStats> Days()
    {
        lock (_gate)
        {
            Store store = _store ??= Load();
            return store.Days.Select(d => d.Copy()).ToList();
        }
    }

    /// <summary>True once a week, after Buddy has watched for at least 3 days, in the daytime.</summary>
    public bool WeeklyNoticeDue()
    {
        if (!settings.WeeklyReport) return false;
        int hour = DateTime.Now.Hour;
        if (hour < 10 || hour >= 22) return false;
        lock (_gate)
        {
            Store store = _store ??= Load();
            if (store.FirstSeen is not DateTime first || DateTime.Now - first < TimeSpan.FromDays(3)) return false;
            if (store.Days.Count(d => d.OnMinutes >= 30) < 3) return false;
            return store.LastWeeklyNotice is not DateTime last || DateTime.Now - last >= TimeSpan.FromDays(7);
        }
    }

    public void MarkWeeklyNoticeShown()
    {
        lock (_gate)
        {
            Store store = _store ??= Load();
            store.LastWeeklyNotice = DateTime.Now;
            SaveLocked();
        }
    }

    private static DayStats Today(Store store)
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        DayStats? day = store.Days.Count > 0 && store.Days[^1].Day == today ? store.Days[^1] : store.Days.FirstOrDefault(d => d.Day == today);
        if (day != null) return day;
        day = new DayStats { Day = today };
        store.Days.Add(day);
        store.Days.Sort((a, b) => string.CompareOrdinal(a.Day, b.Day));
        if (store.Days.Count > KeepDays) store.Days.RemoveRange(0, store.Days.Count - KeepDays);
        return day;
    }

    private void SaveLocked()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_store));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving the health log failed", ex);
        }
    }

    private static Store Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new Store();
        }
        catch (Exception ex)
        {
            Log.Error("Reading the health log failed; starting a fresh one (old copy kept as .bad)", ex);
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch (Exception) { /* best effort */ }
        }
        return new Store();
    }

    private static double Growth(ref TimeSpan? last, TimeSpan now)
    {
        TimeSpan? before = last;
        last = now;
        if (before is not TimeSpan b) return now.TotalSeconds; // first look since start: counter started with us
        return now >= b ? (now - b).TotalSeconds : now.TotalSeconds; // went down = it was reset
    }

    private static double? Max(double? a, double? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);
}
