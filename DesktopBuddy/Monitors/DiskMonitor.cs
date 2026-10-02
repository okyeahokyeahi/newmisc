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
        Alert?.Invoke($"Drive {drive.Name.TrimEnd('\\')} is almost full",
            $"Only {Format.Bytes(FreeBytes.Value)} free. Windows needs room for updates and for spare memory when RAM is full. " +
            "Click to open Storage settings (\"Cleanup recommendations\" is the quick win).");
    }
}
