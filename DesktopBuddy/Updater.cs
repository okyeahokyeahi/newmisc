using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace DesktopBuddy;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string? Sha256);

/// <summary>
/// Self-update from this repo's public GitHub Releases: check for a newer version, download the zip,
/// verify its SHA-256 (GitHub publishes one per file), then a tiny script swaps the files once
/// Desktop Buddy has exited and starts the new version. The download, the files and the script are staged in
/// an admin-only folder under %SystemRoot%\Temp, because the script runs as admin. Settings live in %AppData%,
/// so they survive.
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
        Log.Info($"Update check: latest is {tag}, running {CurrentVersion}");
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
        // Everything the elevated swap script later runs or copies lives in a folder only admins can write to.
        // The user's %TEMP% would let any program swap the script or the files during the wait, and get admin.
        string work = CreateAdminOnlyFolder();
        try
        {
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
            string sys = Environment.SystemDirectory; // full paths, so nothing is picked up from the current folder or PATH
            int pid = Environment.ProcessId;
            string script = Path.Combine(work, "apply-update.cmd");
            string scriptLog = Path.Combine(Settings.Folder, "update-log.txt");
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"echo %date% %time% waiting for Desktop Buddy (pid {pid}) to close> \"{scriptLog}\"\r\n" +
                "set /a tries=0\r\n" +
                ":wait\r\n" +
                "set /a tries+=1\r\n" +
                // give up waiting after ~60 s rather than hanging forever
                $"\"{sys}\\tasklist.exe\" /FI \"PID eq {pid}\" | \"{sys}\\find.exe\" \"{pid}\" >nul && if %tries% lss 60 (\"{sys}\\timeout.exe\" /t 1 /nobreak >nul & goto wait)\r\n" +
                $"echo %date% %time% copying files>> \"{scriptLog}\"\r\n" +
                $"\"{sys}\\robocopy.exe\" \"{staging}\" \"{installDir}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS >> \"{scriptLog}\"\r\n" +
                $"echo %date% %time% robocopy exit code %errorlevel% (0-7 = OK)>> \"{scriptLog}\"\r\n" +
                // The new app inherits this folder as its working folder (same as the logon task gives it).
                $"cd /d \"{installDir}\"\r\n" +
                $"start \"\" \"{exe}\"\r\n" +
                $"echo %date% %time% started the new version>> \"{scriptLog}\"\r\n" +
                // Last line: remove the staging folder, this script included (cmd reopens it per line, so that works).
                $"rd /s /q \"{work}\"\r\n");

            Log.Info($"Applying update {CurrentVersion} -> {update.Version} into {installDir} (staged in {work})");
            // Started from this (already elevated) app, so the relaunch needs no admin prompt.
            Process.Start(new ProcessStartInfo(Path.Combine(sys, "cmd.exe"), $"/c \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = sys, // not the staging folder, so the script can delete it at the end
            });
            return true;
        }
        catch
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception ex) { Log.Error($"Couldn't remove {work}", ex); }
            throw;
        }
    }

    /// <summary>
    /// Creates a new, randomly named folder under %SystemRoot%\Temp that only Administrators and SYSTEM can open.
    /// Standard users can create folders there but can't list it, so they can't guess or pre-create this one.
    /// </summary>
    private static string CreateAdminOnlyFolder()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        string work = Path.Combine(root, "DesktopBuddyUpdate-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(work)) throw new InvalidOperationException("Couldn't create a safe folder for the update. Try again.");

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // nothing inherited from Temp
        security.SetOwner(admins);
        foreach (SecurityIdentifier sid in new[] { admins, system })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        var dir = new DirectoryInfo(work);
        dir.Create(security); // the ACL is applied as the folder is created, not afterwards
        dir.Refresh();
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.EnumerateFileSystemInfos().Any()
            || dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != admins)
            throw new InvalidOperationException("Couldn't create a safe folder for the update. Try again.");
        return work;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopBuddy-Updater"); // GitHub's API requires a User-Agent
        return client;
    }
}
