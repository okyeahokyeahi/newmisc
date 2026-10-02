using System.Management;
using DesktopBuddy.Knowledge;
using DesktopBuddy.Monitors;

namespace DesktopBuddy;

public enum Severity { Good, Info, Warning, Problem }

public sealed record Finding(Severity Severity, string Title, string Detail);

/// <summary>
/// "Why is my laptop slow / loud right now?" - plain rules over the live numbers, no AI needed.
/// </summary>
internal static class Diagnosis
{
    public static List<Finding> Run(Settings settings, ResourceSnapshot? r, TemperatureSnapshot? t, HeatSlowdownSnapshot? heat, DiskMonitor disk)
    {
        var findings = new List<Finding>();
        if (r == null)
        {
            findings.Add(new Finding(Severity.Info, "Still measuring", "Give me a few seconds after startup, then try again."));
            return findings;
        }

        // --- RAM ---
        var byMemory = r.AllApps.OrderByDescending(a => a.MemoryBytes).ToList();
        if (r.RamPercent >= 85)
        {
            string top = string.Join("\n", byMemory.Take(4).Select(a => $"   • {Label(a.Name)}: {Format.Bytes(a.MemoryBytes)}"));
            string tips = RamTips(byMemory);
            findings.Add(new Finding(Severity.Problem, $"RAM is {r.RamPercent}% full ({Format.Bytes(r.RamUsedBytes)} of {Format.Bytes(r.RamTotalBytes)})",
                "When RAM fills up, Windows swaps to the SSD and everything stutters. Biggest users:\n" + top + "\n" + tips));
        }
        else if (r.RamPercent >= 70)
        {
            findings.Add(new Finding(Severity.Info, $"RAM is {r.RamPercent}% used", "Fine for now, but opening a game on top of this could fill it."));
        }

        // WebView2 is confusing; say who it belongs to.
        if (byMemory.FirstOrDefault(a => a.Name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) is { MemoryBytes: > 300L * 1024 * 1024 } webView)
        {
            string owners = WebView2Owners();
            findings.Add(new Finding(Severity.Info, $"\"msedgewebview2\" is using {Format.Bytes(webView.MemoryBytes)}",
                "That's Edge's built-in web engine, used by other apps to draw their windows." +
                (owners.Length > 0 ? $" Right now it's working for: {owners}." : "") +
                " Closing those apps (or turning off Widgets: right-click taskbar > Taskbar settings) frees it."));
        }

        // --- CPU ---
        var byCpu = r.AllApps.OrderByDescending(a => a.CpuPercent).ToList();
        if (r.CpuPercent >= 70)
        {
            string top = string.Join("\n", byCpu.Take(3).Select(a => $"   • {Label(a.Name)}: {a.CpuPercent:0}% CPU"));
            findings.Add(new Finding(Severity.Warning, $"The processor is {r.CpuPercent:0}% busy", "Busiest right now:\n" + top +
                "\nA busy CPU means heat, and heat means loud fans."));
        }

        foreach (AppUsage chore in byCpu.Where(a => a.CpuPercent >= 5 && KnownProcesses.IsWindowsChore(a.Name)).Take(2))
        {
            var known = KnownProcesses.Find(chore.Name)!;
            findings.Add(new Finding(Severity.Info, $"{known.Name} is running ({chore.CpuPercent:0}% CPU)", known.Description));
        }

        // --- Heat ---
        if (heat?.CpuSlowingNow == true || heat?.GpuSlowingNow == true)
        {
            string part = heat.CpuSlowingNow && heat.GpuSlowingNow ? "CPU and GPU are" : heat.CpuSlowingNow ? "CPU is" : "GPU is";
            findings.Add(new Finding(Severity.Problem, $"Your {part} slowing down from heat right now",
                "This is the main cause of stutter and lag in games. Fixes: NitroSense > Max fan, raise the back of the laptop, " +
                "keep the vents clear. If it happens every session, the cooling paste may need replacing (a repair shop job)."));
        }
        else if (t?.CpuC >= settings.CpuTempWarnC - 10 || t?.GpuC >= settings.GpuTempWarnC - 10)
        {
            findings.Add(new Finding(Severity.Warning, $"Running warm (CPU {Format.Temp(t?.CpuC)}, GPU {Format.Temp(t?.GpuC)})",
                "That's why the fans are loud. It's not slowing down yet. Max fan in NitroSense or a laptop stand helps."));
        }

        if (heat?.GpuPowerBrakeNow == true)
        {
            findings.Add(new Finding(Severity.Warning, "GPU is being held back by power",
                "The laptop's power brake is limiting the GPU. Use the original Acer charger and make sure it's firmly plugged in."));
        }

        // --- Disk ---
        if (disk.FreeBytes is long free && disk.TotalBytes is long total &&
            (free < (long)settings.DiskFreeWarnGb * 1024 * 1024 * 1024 || free < total / 10))
        {
            findings.Add(new Finding(Severity.Warning, $"Only {Format.Bytes(free)} free on C:",
                "Low disk space slows things down when RAM is full. Settings > System > Storage > Cleanup recommendations."));
        }

        if (findings.All(f => f.Severity is Severity.Good or Severity.Info))
        {
            findings.Insert(0, new Finding(Severity.Good, "Nothing looks wrong right now",
                $"CPU {r.CpuPercent:0}% busy, RAM {r.RamPercent}%, CPU {Format.Temp(t?.CpuC)}, GPU {Format.Temp(t?.GpuC)}. " +
                "If it felt slow a moment ago, the cause may have finished. Try again while it's happening."));
        }

        return findings;
    }

    /// <summary>Friendly label, e.g. "chrome (Google Chrome)".</summary>
    public static string Label(string processName) =>
        KnownProcesses.Find(processName) is { } k && !k.Name.Equals(processName, StringComparison.OrdinalIgnoreCase)
            ? $"{processName} ({k.Name})"
            : processName;

    private static string RamTips(List<AppUsage> byMemory)
    {
        var names = byMemory.Take(5).Select(a => a.Name.ToLowerInvariant()).ToHashSet();
        var tips = new List<string>();
        if (names.Contains("chrome") || names.Contains("msedge"))
            tips.Add("Close tabs you aren't using, or turn on Memory Saver (Chrome: Settings > Performance).");
        if (names.Contains("robloxstudiobeta"))
            tips.Add("Roblox Studio: close other places and stop Play-test sessions you're done with.");
        if (names.Contains("discord"))
            tips.Add("Discord: turn off Hardware Acceleration if you don't need it.");
        tips.Add("Restarting a big app clears its memory build-up. Don't bother with \"RAM cleaner\" apps; they make it worse.");
        return "What to do: " + string.Join(" ", tips);
    }

    /// <summary>Which apps the WebView2 processes belong to, from their --webview-exe-name flag.</summary>
    private static string WebView2Owners()
    {
        try
        {
            var owners = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine, WorkingSetSize FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    string commandLine = o["CommandLine"]?.ToString() ?? "";
                    const string flag = "--webview-exe-name=";
                    int i = commandLine.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    string rest = commandLine[(i + flag.Length)..];
                    string exe = Path.GetFileNameWithoutExtension(rest.Split(' ', '"')[0]);
                    owners[exe] = owners.GetValueOrDefault(exe) + Convert.ToUInt64(o["WorkingSetSize"] ?? 0UL);
                }
            }
            return string.Join(", ", owners.OrderByDescending(kv => kv.Value).Take(4)
                .Select(kv => $"{Label(kv.Key)} {Format.Bytes(kv.Value)}"));
        }
        catch (Exception ex)
        {
            Log.Error("Reading WebView2 owners failed", ex);
            return "";
        }
    }
}
