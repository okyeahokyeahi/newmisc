using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopBuddy;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string? Sha256);

/// <summary>
/// Self-update from this repo's public GitHub Releases: check for a newer version, download the zip,
/// verify its SHA-256 (GitHub publishes one per file), then a tiny script swaps the files once
/// Desktop Buddy has exited and starts the new version. Settings live in %AppData%, so they survive.
/// </summary>
internal static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/okyeahokyeahi/newmisc/releases/latest";
    private const string AssetName = "DesktopBuddy-win-x64.zip";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    /// <summary>Returns the newer release, or null if this is already the latest (or GitHub can't be reached).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Info($"Update check: GitHub returned {(int)response.StatusCode}");
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest)) return null;
        if (Normalize(latest) <= Normalize(CurrentVersion)) return null;

        foreach (JsonElement asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            string url = asset.GetProperty("browser_download_url").GetString()!;
            string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null; // "sha256:..."
            string? sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
            return new UpdateInfo(latest, url, sha);
        }
        return null;
    }

    /// <summary>
    /// Downloads and stages the update, then starts the swap script. The caller must exit the app right
    /// after this returns true; the script waits for that, copies the files and relaunches.
    /// </summary>
    public static async Task<bool> PrepareAndLaunchAsync(UpdateInfo update)
    {
        string work = Path.Combine(Path.GetTempPath(), "DesktopBuddyUpdate");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        string staging = Path.Combine(work, "files");
        Directory.CreateDirectory(staging);
        string zipPath = Path.Combine(work, AssetName);

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(zipPath);
            await response.Content.CopyToAsync(file);
        }

        if (update.Sha256 != null)
        {
            await using var stream = File.OpenRead(zipPath);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded update was corrupted (checksum mismatch). Try again later.");
        }

        ZipFile.ExtractToDirectory(zipPath, staging);
        if (!File.Exists(Path.Combine(staging, "DesktopBuddy.exe")))
            throw new InvalidOperationException("The update package didn't contain DesktopBuddy.exe.");

        string installDir = AppContext.BaseDirectory.TrimEnd('\\');
        string exe = Path.Combine(installDir, "DesktopBuddy.exe");
        int pid = Environment.ProcessId;
        string script = Path.Combine(work, "apply-update.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"robocopy \"{staging}\" \"{installDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS >nul\r\n" +
            $"start \"\" \"{exe}\"\r\n");

        Log.Info($"Applying update {CurrentVersion} -> {update.Version} into {installDir}");
        // Started from this (already elevated) app, so the relaunch needs no admin prompt.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = work,
        });
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopBuddy-Updater"); // GitHub's API requires a User-Agent
        return client;
    }
}
