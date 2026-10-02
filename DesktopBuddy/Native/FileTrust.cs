using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace DesktopBuddy.Native;

/// <summary>Shared "can I trust this file?" helpers: signature, publisher, and what kind of folder it lives in.</summary>
internal static class FileTrust
{
    private static readonly ConcurrentDictionary<string, bool> SignatureCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string?> PublisherCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private static readonly string[] ProtectedFolders =
    [
        WindowsDir,
        Dir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
        Dir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
    ];

    private static readonly (string Folder, string Label)[] RiskyFolders = BuildRiskyFolders();

    // Windows' own files are mostly signed through a Windows "catalog" rather than inside the file, so the
    // embedded-signature check calls them unsigned. These admin-only folders hold Windows components.
    private static readonly string[] WindowsComponentFolders =
    [
        WindowsDir,
        Dir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")),
        Dir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender")),
    ];

    /// <summary>Folders only admins can write to (Windows, Program Files, Defender's platform folder).</summary>
    public static bool IsInProtectedFolder(string path) =>
        ProtectedFolders.Concat(WindowsComponentFolders).Any(f => f.Length > 1 && path.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>Part of Windows itself (Windows folder, Store/system apps, Defender).</summary>
    public static bool IsWindowsComponent(string path) =>
        WindowsComponentFolders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>Signed, or a Windows component (catalog-signed). The practical "someone vouches for it" test.</summary>
    public static bool IsTrustedPublisher(string path) => IsWindowsComponent(path) || IsSigned(path);

    /// <summary>"your Downloads folder" etc. if the file sits somewhere throwaway, otherwise null.</summary>
    public static string? RiskyFolderLabel(string path) =>
        RiskyFolders.Where(f => path.StartsWith(f.Folder, StringComparison.OrdinalIgnoreCase)).Select(f => f.Label).FirstOrDefault();

    /// <summary>A plain-English description of where a file lives.</summary>
    public static string DescribeLocation(string path)
    {
        if (path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) return "the Windows folder";
        if (IsInProtectedFolder(path)) return "Program Files (where installed apps live)";
        if (RiskyFolderLabel(path) is string risky) return risky;
        string profile = Dir(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (path.StartsWith(profile + "AppData\\", StringComparison.OrdinalIgnoreCase)) return "your AppData folder (per-user apps and data)";
        if (path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) return "your user folder";
        return "another folder";
    }

    /// <summary>Path with the user's profile replaced by %USERPROFILE%, so usernames aren't shared with the AI.</summary>
    public static string Anonymize(string path)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(profile, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + path[profile.Length..] : path;
    }

    /// <summary>
    /// True if the file has a valid embedded signature. Files signed only via a Windows catalog report
    /// false, so "unsigned" must never be treated as suspicious on its own. Unknown -> true.
    /// </summary>
    public static bool IsSigned(string path) => SignatureCache.GetOrAdd(CacheKey(path), _ =>
    {
        try
        {
            return NativeMethods.HasValidEmbeddedSignature(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Signature check failed for {path}", ex);
            return true;
        }
    });

    /// <summary>Path + size + timestamp, so a different file saved under the same name gets checked again.</summary>
    private static string CacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Company name from the signing certificate, e.g. "NVIDIA Corporation", or null.</summary>
    public static string? Publisher(string path) => PublisherCache.GetOrAdd(CacheKey(path), _ =>
    {
        try
        {
            if (!IsSigned(path)) return null;
#pragma warning disable SYSLIB0057 // simplest way to read an Authenticode signer
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch
        {
            return null;
        }
    });

    private static string Dir(string p) => p.TrimEnd('\\') + "\\";

    private static (string, string)[] BuildRiskyFolders()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            (Dir(NativeMethods.ToLongPath(Path.GetTempPath())), "your Temp folder"),
            (Dir(Path.Combine(WindowsDir, "Temp")), "the Windows Temp folder"),
            (Dir(Path.Combine(profile, "Downloads")), "your Downloads folder"),
            (Dir(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public"), "the Public user folder"),
            (Dir(Path.Combine(Path.GetPathRoot(WindowsDir)!, "$Recycle.Bin")), "the Recycle Bin"),
        ];
    }
}
