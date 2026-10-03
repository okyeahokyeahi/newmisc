namespace DesktopBuddy.Monitors;

/// <summary>
/// How much space game clips and recordings take: the Videos folder (Xbox Game Bar "Captures", NVIDIA, Discord
/// and most recorders save there) plus Roblox's own Videos\Roblox and Pictures\Roblox.
/// </summary>
public static class ClipStorage
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public sealed record Folder(string Path, long Bytes);

    public sealed record Result(long TotalBytes, IReadOnlyList<Folder> Biggest, string VideosPath);

    /// <summary>Walks the folders (can take a few seconds on a big Videos folder; call off the UI thread).</summary>
    public static Result? Measure(CancellationToken cancel = default)
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); // follows OneDrive/moved folders
        if (string.IsNullOrEmpty(videos) || !Directory.Exists(videos)) return null;

        var folders = new List<Folder>();
        long looseFiles = SizeOf(videos, recursive: false, cancel);
        foreach (string dir in SafeDirectories(videos))
        {
            cancel.ThrowIfCancellationRequested();
            folders.Add(new Folder(dir, SizeOf(dir, recursive: true, cancel)));
        }
        string robloxPictures = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Roblox");
        if (Directory.Exists(robloxPictures)) folders.Add(new Folder(robloxPictures, SizeOf(robloxPictures, recursive: true, cancel)));
        if (looseFiles > 0) folders.Add(new Folder(videos, looseFiles));

        return new Result(folders.Sum(f => f.Bytes), folders.Where(f => f.Bytes > 0).OrderByDescending(f => f.Bytes).Take(4).ToList(), videos);
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static long SizeOf(string path, bool recursive, CancellationToken cancel)
    {
        long total = 0;
        int count = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            // Skip links, and OneDrive "online-only" files (they don't use disk space here).
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess,
        };
        try
        {
            foreach (FileInfo f in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                total += f.Length;
                if (++count % 500 == 0) cancel.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // folder vanished or locked mid-walk; partial size is fine
        }
        return total;
    }
}
