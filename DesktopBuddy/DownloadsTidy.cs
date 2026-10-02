using System.Text.Json;
using DesktopBuddy.Monitors;

namespace DesktopBuddy;

/// <summary>
/// Weekly (when turned on): moves files older than N days from Downloads into Downloads\Older\yyyy-MM.
/// Only moves, never deletes, and every move is written to a journal so "Undo" can put them back.
/// </summary>
internal static class DownloadsTidy
{
    private static readonly string JournalPath = Path.Combine(Settings.Folder, "tidy-journal.json");
    private static readonly string OlderFolder = Path.Combine(DownloadsWatcher.DownloadsFolder, "Older");
    private static readonly object Gate = new(); // weekly run, "Tidy now" and Undo never overlap
    private static readonly string[] SkipExtensions = [".crdownload", ".part", ".tmp", ".partial", ".download"];

    public sealed class Move
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    public sealed class Batch
    {
        public DateTime When { get; set; }
        public List<Move> Moves { get; set; } = [];
    }

    private sealed class Journal
    {
        public DateTime? LastRun { get; set; }
        /// <summary>Files you put back with Undo: left alone for 30 days so the next tidy doesn't move them again.</summary>
        public Dictionary<string, DateTime> KeepUntil { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Batch> Batches { get; set; } = [];
    }

    public static bool DueThisWeek(Settings settings)
    {
        Journal j = Load();
        return settings.TidyDownloads && (j.LastRun == null || DateTime.Now - j.LastRun > TimeSpan.FromDays(7));
    }

    /// <summary>Moves old files. Returns how many were moved.</summary>
    public static int Run(Settings settings)
    {
        lock (Gate) return RunLocked(settings);
    }

    private static int RunLocked(Settings settings)
    {
        Journal journal = Load();
        var batch = new Batch { When = DateTime.Now };
        DateTime cutoff = DateTime.Now.AddDays(-Math.Max(1, settings.TidyAfterDays));

        foreach (string file in Directory.EnumerateFiles(DownloadsWatcher.DownloadsFolder))
        {
            try
            {
                var info = new FileInfo(file);
                if (SkipExtensions.Contains(info.Extension.ToLowerInvariant()) ||
                    info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                DateTime newest = Max(info.LastWriteTime, info.CreationTime);
                if (newest > cutoff || IsLocked(file)) continue;
                if (journal.KeepUntil.TryGetValue(file, out DateTime keep) && keep > DateTime.Now) continue;

                string folder = Path.Combine(OlderFolder, newest.ToString("yyyy-MM"));
                Directory.CreateDirectory(folder);
                string target = FreeName(Path.Combine(folder, info.Name));
                File.Move(file, target);
                batch.Moves.Add(new Move { From = file, To = target });
            }
            catch (Exception ex)
            {
                Log.Error($"Tidying {file} failed", ex);
            }
        }

        journal.LastRun = DateTime.Now;
        foreach (string expired in journal.KeepUntil.Where(k => k.Value <= DateTime.Now).Select(k => k.Key).ToList())
            journal.KeepUntil.Remove(expired);
        if (batch.Moves.Count > 0)
        {
            journal.Batches.Add(batch);
            if (journal.Batches.Count > 10) journal.Batches.RemoveAt(0);
        }
        Save(journal);
        Log.Info($"Downloads tidy: moved {batch.Moves.Count} file(s)");
        return batch.Moves.Count;
    }

    public static bool CanUndo => Load().Batches.Count > 0;

    /// <summary>Puts the last batch back. Returns (moved back, skipped because the spot is taken or the file is gone).</summary>
    public static (int Restored, int Skipped) UndoLast()
    {
        lock (Gate) return UndoLocked();
    }

    private static (int Restored, int Skipped) UndoLocked()
    {
        Journal journal = Load();
        if (journal.Batches.Count == 0) return (0, 0);
        Batch last = journal.Batches[^1];
        int restored = 0, skipped = 0;
        foreach (Move m in last.Moves)
        {
            try
            {
                if (File.Exists(m.To) && !File.Exists(m.From))
                {
                    DownloadsWatcher.Ignore(m.From);
                    File.Move(m.To, m.From);
                    journal.KeepUntil[m.From] = DateTime.Now.AddDays(30);
                    restored++;
                }
                else skipped++;
            }
            catch (Exception ex)
            {
                Log.Error($"Undoing move of {m.To} failed", ex);
                skipped++;
            }
        }
        journal.Batches.RemoveAt(journal.Batches.Count - 1);
        Save(journal);
        return (restored, skipped);
    }

    private static string FreeName(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static bool IsLocked(string file)
    {
        try
        {
            using var _ = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // read-only or protected: leave it alone
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static Journal Load()
    {
        try
        {
            return File.Exists(JournalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath)) ?? new() : new();
        }
        catch
        {
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
            Log.Error("Saving the tidy journal failed", ex);
        }
    }
}
