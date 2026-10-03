namespace DesktopBuddy.Monitors;

/// <summary>Warns when the Windows drive gets low. A full drive breaks updates and makes a full-RAM PC even slower.</summary>
public sealed class DiskMonitor(Settings settings)
{
    private DateTime _lastAlert;

    public long? FreeBytes { get; private set; }
    public long? TotalBytes { get; private set; }

    /// <summary>(title, message)</summary>
    public event Action<string, string>? Alert;

    public void Tick()
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        if (!drive.IsReady) return;
        FreeBytes = drive.AvailableFreeSpace;
        TotalBytes = drive.TotalSize;

        bool low = FreeBytes < (long)settings.DiskFreeWarnGb * 1024 * 1024 * 1024;
        if (!low || DateTime.UtcNow - _lastAlert < TimeSpan.FromHours(12)) return;

        _lastAlert = DateTime.UtcNow;
        long freeNow = FreeBytes.Value;
        // Walking a big Videos folder can take a while; do it off the monitoring loop, with a time limit.
        _ = Task.Run(() => SendAlert(drive.Name, freeNow));
    }

    private void SendAlert(string driveName, long free)
    {
        string clips = "";
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (ClipStorage.Measure(limit.Token) is { TotalBytes: >= 5L * 1024 * 1024 * 1024 } c &&
                string.Equals(Path.GetPathRoot(c.VideosPath), driveName, StringComparison.OrdinalIgnoreCase)) // only if they're on this drive
                clips = $" Game clips and recordings in Videos take {Format.Bytes(c.TotalBytes)}.";
        }
        catch (OperationCanceledException)
        {
            // too slow to measure; send the alert without it
        }
        catch (Exception ex)
        {
            Log.Error("Measuring clip storage failed", ex);
        }
        Alert?.Invoke($"Drive {driveName.TrimEnd('\\')} is almost full",
            $"Only {Format.Bytes(free)} free. Windows needs room for updates and spare memory." + clips +
            (clips.Length > 0 ? " Click for Storage settings." : " Click to open Storage settings (\"Cleanup recommendations\" is the quick win)."));
    }
}
