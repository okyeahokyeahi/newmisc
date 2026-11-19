using System.Security.AccessControl;
using System.Security.Principal;

namespace DesktopBuddy;

/// <summary>
/// Scratch folders for files that an elevated step later runs or reads (the update script, the startup task XML).
/// The user's %TEMP% won't do: any program running as the user could swap the file in between and get admin.
/// </summary>
internal static class AdminOnlyFolder
{
    /// <summary>
    /// Creates a new, randomly named folder under %SystemRoot%\Temp that only Administrators and SYSTEM can open.
    /// Standard users can create folders there but can't list it, so they can't guess or pre-create this one.
    /// Needs the elevated token (Desktop Buddy always runs as admin).
    /// </summary>
    public static string Create(string namePrefix)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        string path = Path.Combine(root, namePrefix + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path)) throw new InvalidOperationException("Couldn't create a safe temporary folder. Try again.");

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // nothing inherited from Temp
        security.SetOwner(admins);
        foreach (SecurityIdentifier sid in new[] { admins, system })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        var dir = new DirectoryInfo(path);
        dir.Create(security); // the ACL is applied as the folder is created, not afterwards
        dir.Refresh();
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.EnumerateFileSystemInfos().Any()
            || dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != admins)
            throw new InvalidOperationException("Couldn't create a safe temporary folder. Try again.");
        return path;
    }

    /// <summary>Best-effort removal; failures are only logged.</summary>
    public static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log.Error($"Couldn't remove {path}", ex); }
    }
}
