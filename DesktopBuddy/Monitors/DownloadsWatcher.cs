using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using DesktopBuddy.Native;

namespace DesktopBuddy.Monitors;

public sealed record DownloadReport(string FilePath, string FileName, string? FromHost, string? ViaHost, IReadOnlyList<string> Warnings);

/// <summary>
/// When a program, script or archive lands in Downloads: says where it came from (the "Mark of the Web"
/// Windows stores on downloads) and flags the classic tricks Defender can't fully cover:
/// password-protected archives, fake double extensions, padded giant installers, unsigned programs.
/// Defender already scans files as they're saved; this adds context and a one-click scan.
/// </summary>
public sealed class DownloadsWatcher : IDisposable
{
    private static readonly HashSet<string> WatchedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".scr", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".hta", ".lnk", ".dll", ".jar",
        ".zip", ".rar", ".7z",
    };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".scr", ".com", ".dll" };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".bat", ".cmd", ".ps1", ".vbs", ".js", ".hta", ".lnk", ".jar" };

    private static readonly HashSet<string> DecoyExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".txt", ".jpg", ".jpeg", ".png", ".gif", ".mp4", ".mp3", ".xlsx", ".pptx", ".rbxl", ".rbxm" };

    public static string DownloadsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);

    public event Action<DownloadReport>? Downloaded;

    public DownloadsWatcher()
    {
        if (!Directory.Exists(DownloadsFolder)) return;
        _watcher = new FileSystemWatcher(DownloadsFolder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        // Browsers save to .crdownload/.part and rename at the end, so Renamed matters most.
        _watcher.Created += (_, e) => Consider(e.FullPath);
        _watcher.Renamed += (_, e) => Consider(e.FullPath);
    }

    private void Consider(string path)
    {
        if (!WatchedExtensions.Contains(Path.GetExtension(path))) return;
        // One report per file even though Windows fires several events. Only a finished inspection counts,
        // so Firefox's empty placeholder (same name, 0 bytes) doesn't block the real file.
        if (_recent.TryGetValue(path, out DateTime seen) && DateTime.UtcNow - seen < TimeSpan.FromMinutes(5)) return;
        if (!_inspecting.TryAdd(path, 0)) return;
        _ = Task.Run(() => InspectWhenFinished(path));
    }

    private readonly ConcurrentDictionary<string, byte> _inspecting = new(StringComparer.OrdinalIgnoreCase);

    private async Task InspectWhenFinished(string path)
    {
        try
        {
            // Wait until the file has content, stops growing and nothing has it open (up to ~10 minutes).
            long lastSize = -1;
            bool ready = false;
            for (int i = 0; i < 300 && !ready; i++)
            {
                await Task.Delay(2000);
                if (!File.Exists(path)) return;
                long size = new FileInfo(path).Length;
                ready = size > 0 && size == lastSize && CanOpen(path);
                lastSize = size;
            }
            if (!ready) return;
            // Files moved back in (e.g. undoing a Downloads tidy) keep their old creation time: not a new download.
            if (File.GetCreationTime(path) < DateTime.Now.AddHours(-1)) return;

            _recent[path] = DateTime.UtcNow;
            Downloaded?.Invoke(Inspect(path));
        }
        catch (Exception ex)
        {
            Log.Error($"Inspecting download {path} failed", ex);
        }
        finally
        {
            _inspecting.TryRemove(path, out _);
        }
    }

    public static DownloadReport Inspect(string path)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path);
        var warnings = new List<string>();

        string inner = Path.GetExtension(Path.GetFileNameWithoutExtension(name));
        if (inner.Length > 0 && DecoyExtensions.Contains(inner) && (ProgramExtensions.Contains(ext) || ScriptExtensions.Contains(ext)))
            warnings.Add($"Fake extension: really a {ext} program, not a {inner}.");

        if (ProgramExtensions.Contains(ext))
        {
            long size = new FileInfo(path).Length;
            if (size > 400L * 1024 * 1024 && ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Suspiciously huge program ({Format.Bytes(size)}), a trick to dodge scanners.");
            if (!FileTrust.IsSigned(path))
                warnings.Add("Not digitally signed.");
        }
        else if (ScriptExtensions.Contains(ext))
        {
            warnings.Add($"{ext} files run commands when opened.");
        }
        else
        {
            InspectArchive(path, ext, warnings);
        }

        var (from, via) = ReadMarkOfTheWeb(path);
        return new DownloadReport(path, name, from, via, warnings);
    }

    private static void InspectArchive(string path, string ext, List<string> warnings)
    {
        if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (LooksEncrypted7zOrRar(path))
                warnings.Add("Password-protected: antivirus can't see inside (classic executor scam).");
            return;
        }

        try
        {
            using ZipArchive zip = ZipFile.OpenRead(path);
            if (zip.Entries.Any(e => e.IsEncrypted))
                warnings.Add("Password-protected: antivirus can't see inside (classic executor scam).");
            var programs = zip.Entries.Where(e => ProgramExtensions.Contains(Path.GetExtension(e.Name)) || ScriptExtensions.Contains(Path.GetExtension(e.Name)))
                .Select(e => e.Name).Take(2).ToList();
            if (programs.Count > 0) warnings.Add($"Contains {string.Join(", ", programs)}.");
        }
        catch (InvalidDataException)
        {
            warnings.Add("The zip file is damaged or disguised.");
        }
    }

    /// <summary>Best effort: only RAR5 archives with encrypted file lists are detected (7z/RAR4 aren't checked).</summary>
    private static bool LooksEncrypted7zOrRar(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[64];
            int read = stream.Read(head, 0, head.Length);
            // RAR5: 8-byte signature, CRC32 (4), header size (1-byte vint), then header type 4 = "archive encryption header"
            if (read > 13 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21 && head[6] == 0x01)
                return head[13] == 4;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static (string? From, string? Via) ReadMarkOfTheWeb(string path)
    {
        try
        {
            string zone = File.ReadAllText(path + ":Zone.Identifier");
            string? Host(string key)
            {
                string? line = zone.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
                if (line == null) return null;
                return Uri.TryCreate(line[(key.Length + 1)..], UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0 ? uri.Host : null;
            }
            return (Host("HostUrl"), Host("ReferrerUrl"));
        }
        catch
        {
            return (null, null); // no mark: copied from USB, made locally, or the browser didn't add one
        }
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Runs a Defender scan on one file. Returns (clean, message). Defender quarantines anything it finds.</summary>
    public static async Task<(bool Clean, string Message)> ScanWithDefender(string path)
    {
        string mpCmdRun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(mpCmdRun)) return (false, "Couldn't find Defender's scanner (another antivirus may be in charge).");

        var start = new ProcessStartInfo(mpCmdRun, $"-Scan -ScanType 3 -File \"{path}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using Process? process = Process.Start(start);
        if (process == null) return (false, "Couldn't start the Defender scan.");
        await process.WaitForExitAsync();
        if (!File.Exists(path)) return (false, $"{Path.GetFileName(path)} is gone. Defender may have already removed it as a threat.");
        return process.ExitCode switch
        {
            0 => (true, $"Defender found nothing in {Path.GetFileName(path)}. (Brand-new malware can still slip past, so only run it if you trust the source.)"),
            2 => (false, $"Defender found a threat in {Path.GetFileName(path)} and quarantined it. Don't download it again."),
            _ => (false, $"The Defender scan didn't finish (code {process.ExitCode})."),
        };
    }

    public void Dispose() => _watcher?.Dispose();
}
