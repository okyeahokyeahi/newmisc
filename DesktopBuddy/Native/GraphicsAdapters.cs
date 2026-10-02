using System.Runtime.InteropServices;

namespace DesktopBuddy.Native;

public sealed record GraphicsAdapter(string Name, uint VendorId, long Luid)
{
    public bool IsNvidia => VendorId == 0x10DE;
    public bool IsAmd => VendorId == 0x1002;
    public bool IsIntel => VendorId == 0x8086;
    public bool IsDedicated => IsNvidia || IsAmd;
}

/// <summary>
/// Lists the graphics chips (via DXGI) with the LUID Windows uses to tag per-process GPU usage,
/// so "pid 1234 is busy on luid X" can be turned into "Roblox is using the Intel chip".
/// </summary>
internal static class GraphicsAdapters
{
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    // Vtable order matters: IUnknown is implied; then IDXGIObject (4), IDXGIFactory (5), IDXGIFactory1.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] bool IsCurrent();
    }

    // IDXGIObject (4), IDXGIAdapter (3), IDXGIAdapter1.
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    private static IReadOnlyList<GraphicsAdapter>? _cache;

    public static IReadOnlyList<GraphicsAdapter> List()
    {
        if (_cache != null) return _cache;
        var result = new List<GraphicsAdapter>();
        try
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out IDXGIFactory1 factory) != 0) return result;
            try
            {
                for (uint i = 0; ; i++)
                {
                    int hr = factory.EnumAdapters1(i, out IDXGIAdapter1 adapter);
                    if (hr == DXGI_ERROR_NOT_FOUND || hr != 0) break;
                    try
                    {
                        if (adapter.GetDesc1(out var desc) != 0) continue;
                        if ((desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE (Microsoft Basic Render)
                        long luid = ((long)desc.LuidHighPart << 32) | desc.LuidLowPart;
                        result.Add(new GraphicsAdapter(desc.Description.Trim(), desc.VendorId, luid));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing graphics adapters failed", ex);
        }

        Log.Info("Graphics chips: " + string.Join("; ", result.Select(a => $"{a.Name} (luid {a.Luid:X})")));
        return _cache = result;
    }
}
