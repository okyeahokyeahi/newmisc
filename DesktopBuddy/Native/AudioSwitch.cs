using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DesktopBuddy.Native;

/// <summary>
/// Moves Windows' sound output to the next speaker/headset (Ctrl+Alt+A). Uses the same hidden interface the
/// Windows sound panel uses (IPolicyConfig); it's undocumented but has been stable since Windows 7.
/// </summary>
internal static class AudioSwitch
{
    /// <summary>Switches to the next active output and returns its name, or null when there's only one (or it failed).</summary>
    public static string? Next()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            if (devices.Count < 2) return null;
            string? currentId = null;
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                using MMDevice current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                currentId = current.ID;
            }
            int index = devices.FindIndex(d => d.ID == currentId);
            MMDevice next = devices[(index + 1) % devices.Count];

            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                // All three roles, like picking it in the sound panel: games, music, and Discord calls follow.
                foreach (ERole role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(next.ID, role));
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
            return next.FriendlyName;
        }
        finally
        {
            foreach (MMDevice d in devices) d.Dispose();
        }
    }

    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    // Only the method we call is typed; the earlier slots keep the vtable order right.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
