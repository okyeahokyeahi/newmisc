using System.Runtime.InteropServices;

namespace DesktopBuddy.Native;

/// <summary>
/// Per-process GPU usage from the Windows "GPU Engine" performance counters (the same data
/// Task Manager's GPU column uses). Returns the busiest 3D/compute engine per PID.
/// </summary>
internal sealed class GpuProcessCounters : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private IntPtr _query;
    private IntPtr _counter;
    private bool _primed;

    public GpuProcessCounters()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
            PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
        {
            Log.Info("GPU Engine performance counters unavailable.");
            _query = IntPtr.Zero;
        }
    }

    /// <summary>PID -> GPU % (busiest 3D/compute engine). Empty on the first call (needs two samples).</summary>
    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        foreach (var (pid, _, value) in SampleByAdapter())
            result[pid] = Math.Max(result.GetValueOrDefault(pid), value);
        return result;
    }

    /// <summary>(PID, graphics chip LUID, GPU %) for every busy 3D/compute engine.</summary>
    public List<(int Pid, long Luid, double Percent)> SampleByAdapter()
    {
        var result = new List<(int, long, double)>();
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return result;
        if (!_primed)
        {
            _primed = true;
            return result;
        }

        uint size = 0;
        uint status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, buffer) != 0) return result;

            // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer (8) + { CStatus (4) + pad (4) + double (8) }
            int itemSize = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * itemSize;
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                // CStatus 0 = valid data, 1 = valid new data
                if (name == null || (uint)Marshal.ReadInt32(item, IntPtr.Size) > 1) continue;
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));

                // e.g. "pid_1234_luid_0x0_0x1_phys_0_eng_0_engtype_3D"
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("engtype_Cuda", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.StartsWith("pid_", StringComparison.Ordinal)) continue;
                int end = name.IndexOf('_', 4);
                if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out int pid)) continue;

                result.Add((pid, ParseLuid(name), Math.Min(100, value)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    /// <summary>"..._luid_0x00000000_0x0000D1A3_..." -> 0x00000000_0000D1A3 (high, low).</summary>
    private static long ParseLuid(string name)
    {
        int i = name.IndexOf("_luid_0x", StringComparison.Ordinal);
        if (i < 0) return 0;
        string[] parts = name[(i + 6)..].Split('_');
        if (parts.Length < 2) return 0;
        try
        {
            long high = Convert.ToInt64(parts[0], 16);
            long low = Convert.ToInt64(parts[1], 16);
            return (high << 32) | (low & 0xFFFFFFFF);
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
