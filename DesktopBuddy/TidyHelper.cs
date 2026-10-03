using System.Security.Cryptography;
using System.Text.Json;
using DesktopBuddy.Monitors;

namespace DesktopBuddy;

/// <summary>
/// The tidy helper: sorts loose files on the Desktop and in Downloads into a "Tidied" folder next to them
/// (Desktop\Tidied\Pictures, Downloads\Tidied\Installers…). Only loose files: folders are never moved (that
/// breaks portable apps and games), Documents is never touched (game saves, Studio places), nothing is deleted,
/// and every batch can be undone.
/// </summary>
internal static class TidyHelper
{
    private static readonly string JournalPath = Path.Combine(Settings.Folder, "tidy-helper-journal.json");
    private static readonly object Gate = new();
    private static readonly string[] SkipExtensions = [".crdownload", ".part", ".tmp", ".partial", ".download", ".ini"];

    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public sealed record Group(string Key, string Category, string Place, string SourceFolder, IReadOnlyList<FileInfo> Files,
        bool CheckedByDefault, string Note)
    {
        public long Bytes => Files.Sum(f => f.Length);
        public string Destination => Path.Combine(SourceFolder, "Tidied", Category);
    }

    private static readonly (string Category, string[] Extensions, bool On, string Note)[] Categories =
    [
        ("Installers", [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"], true,
            "Setup files. Once installed, the app doesn't need them; moving is safe."),
        ("Archives", [".zip", ".rar", ".7z", ".tar", ".gz"], true, "Zips. Safe to move; anything extracted elsewhere isn't affected."),
        ("Pictures", [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".tiff"], true, "Safe to move."),
        ("Videos", [".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv"], true, "Safe to move."),
        ("Music & sounds", [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aiff", ".aif"], true, "Safe to move."),
        ("Documents", [".pdf", ".docx", ".doc", ".txt", ".xlsx", ".xls", ".pptx", ".rtf", ".csv", ".odt", ".md"], true,
            "Safe to move. Word/Excel 'Recent' lists will point to the old spot until you reopen them."),
        ("Roblox files", [".rbxl", ".rbxlx", ".rbxm", ".rbxmx"], false,
            "Studio's 'Recent' list will lose these; reopen them with File > Open. Off by default."),
        ("Shortcuts", [".lnk", ".url"], false, "You probably start apps from these. Moving doesn't break them, but they leave the Desktop."),
    ];

    /// <summary>Looks at the Desktop and Downloads (top level only) and proposes groups. Doesn't change anything.</summary>
    public static List<Group> Plan()
    {
        var groups = new List<Group>();
        foreach (var (place, folder) in new[] { ("Desktop", DesktopFolder), ("Downloads", DownloadsWatcher.DownloadsFolder) })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            List<FileInfo> files = Candidates(folder);

            // Duplicates first: "photo (1).png" that's byte-for-byte the same as "photo.png" next to it.
            var duplicates = files.Where(f => IsCopyOf(f, folder)).ToList();
            if (duplicates.Count > 0)
                groups.Add(new Group($"{place}|Duplicates", "Duplicates", place, folder, duplicates, true,
                    "Exact copies of another file right next to them. Moved, not deleted: check, then delete them yourself."));
            var rest = files.Except(duplicates).ToList();

            foreach (var (category, extensions, on, note) in Categories)
            {
                var match = rest.Where(f => extensions.Contains(f.Extension.ToLowerInvariant())).ToList();
                if (match.Count == 0) continue;
                rest = rest.Except(match).ToList();
                if (category == "Shortcuts" && place == "Downloads") continue; // shortcuts in Downloads are rare; leave them
                groups.Add(new Group($"{place}|{category}", category, place, folder, match, on, note));
            }
            if (rest.Count > 0)
                groups.Add(new Group($"{place}|Other", "Other", place, folder, rest, false,
                    "Everything else. Off by default because some programs expect their files where they are."));
        }
        return groups;
    }

    /// <summary>Moves the files in the chosen groups. Returns (moved, skipped because in use/changed).</summary>
    public static (int Moved, int Skipped) Run(IEnumerable<Group> chosen)
    {
        lock (Gate)
        {
            Journal journal = Load();
            var batch = new Batch { When = DateTime.Now };
            int skipped = 0;
            foreach (Group g in chosen)
            {
                foreach (FileInfo f in g.Files)
                {
                    try
                    {
                        f.Refresh();
                        if (!f.Exists || DownloadsTidy.IsLocked(f.FullName)) { skipped++; continue; }
                        Directory.CreateDirectory(g.Destination);
                        string target = DownloadsTidy.FreeName(Path.Combine(g.Destination, f.Name));
                        File.Move(f.FullName, target);
                        batch.Moves.Add(new DownloadsTidy.Move { From = f.FullName, To = target });
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Tidy helper: moving {f.FullName} failed", ex);
                        skipped++;
                    }
                }
            }
            if (batch.Moves.Count > 0)
            {
                journal.Batches.Add(batch);
                if (journal.Batches.Count > 10) journal.Batches.RemoveAt(0);
                Save(journal);
            }
            Log.Info($"Tidy helper: moved {batch.Moves.Count}, skipped {skipped}");
            return (batch.Moves.Count, skipped);
        }
    }

    public static bool CanUndo
    {
        get
        {
            lock (Gate) return Load().Batches.Count > 0;
        }
    }

    /// <summary>Puts the last batch back. Returns (restored, skipped).</summary>
    public static (int Restored, int Skipped) UndoLast()
    {
        lock (Gate)
        {
            Journal journal = Load();
            if (journal.Batches.Count == 0) return (0, 0);
            Batch last = journal.Batches[^1];
            int restored = 0, skipped = 0;
            foreach (DownloadsTidy.Move m in last.Moves)
            {
                try
                {
                    if (File.Exists(m.To) && !File.Exists(m.From))
                    {
                        DownloadsWatcher.Ignore(m.From); // don't treat it as a new download
                        File.Move(m.To, m.From);
                        restored++;
                    }
                    else skipped++;
                }
                catch (Exception ex)
                {
                    Log.Error($"Tidy helper: undoing {m.To} failed", ex);
                    skipped++;
                }
            }
            journal.Batches.RemoveAt(journal.Batches.Count - 1);
            Save(journal);
            // Remove Tidied folders the undo left empty.
            foreach (string dir in last.Moves.Select(m => Path.GetDirectoryName(m.To)!).Distinct())
                TryRemoveEmpty(dir);
            return (restored, skipped);
        }
    }

    // ---------- helpers ----------
    private static List<FileInfo> Candidates(string folder)
    {
        var list = new List<FileInfo>();
        DateTime recent = DateTime.Now.AddHours(-24);
        try
        {
            foreach (FileInfo f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (SkipExtensions.Contains(f.Extension.ToLowerInvariant())) continue;
                if (f.LastWriteTime > recent || f.CreationTime > recent) continue; // might still be downloading or in use
                list.Add(f);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Tidy helper: listing {folder} failed", ex);
        }
        return list;
    }

    private static readonly System.Text.RegularExpressions.Regex CopySuffix = new(@"^(.*?)(?: \(\d+\)| - Copy(?: \(\d+\))?)$");

    private static bool IsCopyOf(FileInfo f, string folder)
    {
        var m = CopySuffix.Match(Path.GetFileNameWithoutExtension(f.Name));
        if (!m.Success) return false;
        var original = new FileInfo(Path.Combine(folder, m.Groups[1].Value + f.Extension));
        if (!original.Exists || original.Length != f.Length || f.Length > 2L * 1024 * 1024 * 1024) return false;
        try
        {
            return Hash(original.FullName).SequenceEqual(Hash(f.FullName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    private static void TryRemoveEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            string? parent = Path.GetDirectoryName(dir);
            if (parent != null && Path.GetFileName(parent) == "Tidied" && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception)
        {
            // not empty or in use; leave it
        }
    }

    private sealed class Batch
    {
        public DateTime When { get; set; }
        public List<DownloadsTidy.Move> Moves { get; set; } = [];
    }

    private sealed class Journal
    {
        public List<Batch> Batches { get; set; } = [];
    }

    private static Journal Load()
    {
        try
        {
            return File.Exists(JournalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath)) ?? new() : new();
        }
        catch (Exception ex)
        {
            Log.Error("Reading the tidy helper journal failed", ex);
            return new();
        }
    }

    private static void Save(Journal j)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(JournalPath, JsonSerializer.Serialize(j));
        }
        catch (Exception ex)
        {
            Log.Error("Saving the tidy helper journal failed", ex);
        }
    }
}
