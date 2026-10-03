using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace DesktopBuddy.Monitors;

public sealed record RobloxExit(int? Code, string Explanation, string? KickMessage);

/// <summary>
/// "Why did Roblox kick me?": after a Roblox session, reads the newest Roblox client log and turns the
/// disconnect code into plain English. Roblox's log format isn't official, so matching is loose and it
/// says "couldn't tell" rather than guess.
/// </summary>
internal static partial class RobloxLogReader
{
    public static string LogsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

    private static readonly Dictionary<int, string> Codes = new()
    {
        [267] = "The game kicked you (a script or the game's anti-cheat removed you).",
        [268] = "Kicked for unexpected client behaviour. Usually cheats/executors, sometimes a glitch.",
        [273] = "Your account joined from somewhere else (another device or tab), so this one was disconnected.",
        [274] = "The server shut down, usually because the game's developer updated it.",
        [277] = "Lost connection to the server: your internet dropped, or the server stopped responding.",
        [278] = "Kicked for being idle for 20 minutes.",
        [279] = "Couldn't connect to the game: firewall, network, or the server didn't answer.",
        [280] = "Your Roblox app is out of date. Restart Roblox to update it.",
        [517] = "The server you were on is no longer available (it closed or restarted).",
        [524] = "Not allowed to join (private server, or a permission problem).",
        [529] = "Roblox's own servers were having trouble. Nothing on your side.",
        [610] = "Couldn't join that server (it was full, closed, or unavailable).",
        [769] = "A teleport between places failed.",
    };

    [GeneratedRegex(@"disconnect(?:ed)?\s+with\s+reason:?\s*(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex ReasonCode();

    [GeneratedRegex(@"Error\s*Code:?\s*(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorCode();

    [GeneratedRegex(@"kick(?:ed)?[^:\r\n]{0,40}:\s*(.{3,120})$", RegexOptions.IgnoreCase)]
    private static partial Regex KickText();

    /// <summary>The disconnect from the newest client log written since the session started, or null if none found.</summary>
    public static RobloxExit? LastExit(DateTime sessionStartLocal)
    {
        try
        {
            if (!Directory.Exists(LogsFolder)) return null;
            FileInfo? log = new DirectoryInfo(LogsFolder).EnumerateFiles("*.log")
                .Where(f => f.LastWriteTime >= sessionStartLocal.AddMinutes(-2) && !f.Name.Contains("Studio", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (log == null) return null;

            // Only the end of the log matters; Roblox keeps writing it, so open it shared.
            List<string> tail = ReadTail(log.FullName, 4000);
            int? code = null;
            string? kick = null;
            foreach (string line in tail)
            {
                Match m = ReasonCode().Match(line);
                if (!m.Success) m = ErrorCode().Match(line);
                if (m.Success) code = int.Parse(m.Groups[1].Value);
                Match k = KickText().Match(line.Trim());
                if (k.Success && line.Contains("kick", StringComparison.OrdinalIgnoreCase)) kick = k.Groups[1].Value.Trim();
            }

            if (code == null && kick == null) return new RobloxExit(null, "No disconnect recorded: you probably just left the game.", null);
            string text = code is int c && Codes.TryGetValue(c, out string? known) ? known
                : code != null ? $"Error {code}: not one Desktop Buddy recognises."
                : "You were kicked.";
            return new RobloxExit(code, text, kick);
        }
        catch (Exception ex)
        {
            Log.Error("Reading Roblox logs failed", ex);
            return null;
        }
    }

    public static bool IsKnown(int code) => Codes.ContainsKey(code);

    private static List<string> ReadTail(string path, int maxLines)
    {
        var lines = new Queue<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4_000_000) stream.Seek(-4_000_000, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.Enqueue(line);
            if (lines.Count > maxLines) lines.Dequeue();
        }
        return [.. lines];
    }
}

public sealed record StudioCrash(string? RecoveryFile, string? BackupFolder);

/// <summary>
/// Roblox Studio crash help: when Studio disappears and Windows logged a crash or hang for it, points you
/// to the newest auto-save/recovery file and keeps a copy (Studio deletes old ones).
/// </summary>
internal sealed class StudioCrashWatcher(Settings settings)
{
    public static string BackupRoot { get; } = Path.Combine(Settings.Folder, "StudioBackups");

    private bool _wasRunning;
    private DateTime _startedAt;

    public event Action<StudioCrash>? Crashed;

    public void Tick(IEnumerable<string> runningNames)
    {
        if (!settings.StudioCrashHelp) return;
        bool running = runningNames.Any(n => n.Equals("RobloxStudioBeta", StringComparison.OrdinalIgnoreCase));
        if (running && !_wasRunning) _startedAt = DateTime.Now;
        if (!running && _wasRunning && CrashLoggedSince(DateTime.Now.AddMinutes(-3)))
        {
            string? recovery = NewestRecoveryFile(_startedAt.AddMinutes(-5));
            string? backup = recovery != null ? Backup(recovery) : null;
            Log.Info($"Roblox Studio crashed; recovery file: {recovery ?? "none"}");
            Crashed?.Invoke(new StudioCrash(recovery, backup));
        }
        _wasRunning = running;
    }

    /// <summary>Windows logs "Application Error" (1000) or "Application Hang" (1002) when a program crashes or is ended while frozen.</summary>
    private static bool CrashLoggedSince(DateTime since)
    {
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                "*[System[(EventID=1000 or EventID=1002) and TimeCreated[timediff(@SystemTime) <= 300000]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated < since) break;
                    string xml = e.ToXml();
                    if (xml.Contains("RobloxStudioBeta.exe", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading crash events failed", ex);
        }
        return false;
    }

    private static IEnumerable<string> RecoveryFolders()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Path.Combine(docs, "ROBLOX", "AutoSaves"),
            Path.Combine(local, "Roblox", "AutoSaves"),
            Path.Combine(local, "Roblox", "Recovery"),
        }.Where(Directory.Exists);
    }

    private static string? NewestRecoveryFile(DateTime since)
    {
        return RecoveryFolders()
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*.rbxl*", SearchOption.AllDirectories))
            .Where(f => f.LastWriteTime >= since)
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    /// <summary>Copies the recovery file into %AppData%\DesktopBuddy\StudioBackups (keeps the newest 20).</summary>
    private static string? Backup(string file)
    {
        try
        {
            string folder = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            Directory.CreateDirectory(folder);
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
            foreach (DirectoryInfo old in new DirectoryInfo(BackupRoot).EnumerateDirectories().OrderByDescending(d => d.Name).Skip(20))
                old.Delete(recursive: true);
            return folder;
        }
        catch (Exception ex)
        {
            Log.Error($"Backing up {file} failed", ex);
            return null;
        }
    }
}
