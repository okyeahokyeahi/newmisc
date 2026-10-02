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

    /// <summary>Folders only admins can write to (Windows, Program Files).</summary>
    public static bool IsInProtectedFolder(string path) =>
        ProtectedFolders.Any(f => f.Length > 1 && path.StartsWith(f, StringComparison.OrdinalIgnoreCase));

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
    public static bool IsSigned(string path) => SignatureCache.GetOrAdd(path, p =>
    {
        try
        {
            return NativeMethods.HasValidEmbeddedSignature(p);
        }
        catch (Exception ex)
        {
            Log.Error($"Signature check failed for {p}", ex);
            return true;
        }
    });

    /// <summary>Company name from the signing certificate, e.g. "NVIDIA Corporation", or null.</summary>
    public static string? Publisher(string path) => PublisherCache.GetOrAdd(path, p =>
    {
        try
        {
            if (!IsSigned(p)) return null;
#pragma warning disable SYSLIB0057 // simplest way to read an Authenticode signer
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(p));
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
