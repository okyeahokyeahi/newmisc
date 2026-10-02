using System.Runtime.InteropServices;
using static DesktopBuddy.Native.NativeMethods;

namespace DesktopBuddy.Native;

/// <summary>Stores the API key in Windows Credential Manager (encrypted with your Windows login), not in a file.</summary>
internal static class CredentialStore
{
    private const string ApiKeyTarget = "DesktopBuddy/ApiKey";

    public static bool HasApiKey => GetApiKey() != null;

    public static string? GetApiKey()
    {
        if (!CredRead(ApiKeyTarget, CRED_TYPE_GENERIC, 0, out IntPtr ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void SaveApiKey(string key)
    {
        IntPtr blob = Marshal.StringToHGlobalUni(key);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = ApiKeyTarget,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(key.Length * 2),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"Windows refused to save the key (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(blob);
        }
    }

    public static void DeleteApiKey() => CredDelete(ApiKeyTarget, CRED_TYPE_GENERIC, 0);
}
