using System.Management;

namespace DesktopBuddy.Monitors;

/// <summary>Battery wear: how much charge it holds now vs when new. Read from the battery's own report (WMI root\wmi).</summary>
public static class BatteryHealth
{
    public sealed record Reading(double DesignedWh, double FullWh)
    {
        public double Percent => DesignedWh > 0 ? Math.Min(100, FullWh / DesignedWh * 100) : 0;
    }

    public static Reading? Read()
    {
        try
        {
            double designed = Sum("SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
            double full = Sum("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
            // Values are in mWh. Some firmware reports nonsense (0, or "full" bigger than double the design).
            if (designed <= 0 || full <= 0 || full > designed * 2) return null;
            return new Reading(designed / 1000, full / 1000);
        }
        catch (Exception ex)
        {
            Log.Error("Reading battery health failed", ex);
            return null;
        }
    }

    private static double Sum(string query, string field)
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", query);
        double total = 0;
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject o in results)
        {
            using (o)
            {
                if (o[field] is { } v) total += Convert.ToDouble(v);
            }
        }
        return total;
    }
}
