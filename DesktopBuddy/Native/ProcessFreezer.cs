using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopBuddy.Native;

/// <summary>
/// "Freeze": pauses every copy of a program without closing it (like Resource Monitor's "Suspend process").
/// A frozen program can't do anything until it's resumed or killed; a restart unfreezes it. The list is saved
/// so Buddy can still resume them after it restarts (e.g. after an update).
/// </summary>
internal static class ProcessFreezer
{
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private static readonly string StatePath = Path.Combine(Settings.Folder, "frozen.json");
    private static readonly object Gate = new();

    // Freezing these would hang Windows itself; a real copy of them is never flagged, but a check is cheap.
    private static readonly HashSet<string> NeverFreeze = new(StringComparer.OrdinalIgnoreCase)
        { "csrss", "wininit", "winlogon", "lsass", "services", "smss", "svchost", "explorer", "dwm", "system", "registry", "audiodg", "fontdrvhost" };

    public sealed class FrozenCopy
    {
        public int Pid { get; set; }
        public DateTime StartUtc { get; set; }
    }

    public sealed class Entry
    {
        public string ExePath { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime Since { get; set; }
        public List<FrozenCopy> Copies { get; set; } = [];
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Null if it may be frozen, otherwise why not (shown to the user).</summary>
    public static string? WhyNot(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (string.Equals(exePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return "That's Desktop Buddy itself.";
        if (FileTrust.IsWindowsComponent(exePath) || NeverFreeze.Contains(name) && FileTrust.IsInProtectedFolder(exePath))
            return "It's part of Windows; freezing it could hang the whole PC.";
        return null;
    }

    /// <summary>Freezes the given copies (the caller still disposes them). Returns how many froze, and the first error.</summary>
    public static (int Frozen, string? Error) Freeze(string exePath, string name, IReadOnlyList<Process> copies)
    {
        if (WhyNot(exePath) is string why) return (0, why);
        lock (Gate)
        {
            List<Entry> all = LoadAlive();
            Entry entry = all.FirstOrDefault(e => e.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                          ?? AddNew(all, exePath, name);
            int frozen = 0;
            string? error = null;
            foreach (Process p in copies)
            {
                try
                {
                    if (entry.Copies.Any(c => c.Pid == p.Id)) { frozen++; continue; } // already frozen
                    DateTime start = p.StartTime.ToUniversalTime();
                    int status = Call(p.Id, NtSuspendProcess);
                    if (status != 0) { error ??= $"Windows refused (status 0x{status:X8})."; continue; }
                    entry.Copies.Add(new FrozenCopy { Pid = p.Id, StartUtc = start });
                    frozen++;
                }
                catch (Exception ex)
                {
                    Log.Error($"Freezing {name} (pid {p.Id}) failed", ex);
                    error ??= ex.Message;
                }
            }
            if (entry.Copies.Count == 0) all.Remove(entry);
            Save(all);
            Log.Info($"Froze {frozen} cop(ies) of {exePath}");
            return (frozen, error);
        }
    }

    /// <summary>Programs that are frozen right now (copies that have since exited are dropped).</summary>
    public static IReadOnlyList<Entry> List()
    {
        lock (Gate)
        {
            List<Entry> all = LoadAlive();
            Save(all);
            return all;
        }
    }

    /// <summary>Unfreezes every frozen copy of the program. Returns how many resumed.</summary>
    public static int Resume(string exePath)
    {
        lock (Gate)
        {
            List<Entry> all = LoadAlive();
            Entry? entry = all.FirstOrDefault(e => e.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return 0;
            int resumed = 0;
            foreach (FrozenCopy c in entry.Copies)
            {
                try
                {
                    if (Call(c.Pid, NtResumeProcess) == 0) resumed++;
                }
                catch (Exception ex)
                {
                    Log.Error($"Resuming pid {c.Pid} failed", ex);
                }
            }
            all.Remove(entry);
            Save(all);
            Log.Info($"Resumed {resumed} cop(ies) of {exePath}");
            return resumed;
        }
    }

    /// <summary>Ends every frozen copy of the program. Returns how many were ended.</summary>
    public static int Kill(string exePath)
    {
        lock (Gate)
        {
            List<Entry> all = LoadAlive();
            Entry? entry = all.FirstOrDefault(e => e.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return 0;
            int killed = 0;
            foreach (FrozenCopy c in entry.Copies)
            {
                try
                {
                    using Process p = Process.GetProcessById(c.Pid);
                    p.Kill();
                    killed++;
                }
                catch (Exception ex)
                {
                    Log.Error($"Ending frozen pid {c.Pid} failed", ex);
                }
            }
            all.Remove(entry);
            Save(all);
            return killed;
        }
    }

    private static Entry AddNew(List<Entry> all, string exePath, string name)
    {
        var e = new Entry { ExePath = exePath, Name = name, Since = DateTime.Now };
        all.Add(e);
        return e;
    }

    private static int Call(int pid, Func<IntPtr, int> fn)
    {
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return fn(h);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Saved list, minus copies that exited (a reused PID won't match the saved start time).</summary>
    private static List<Entry> LoadAlive()
    {
        List<Entry> all;
        try
        {
            all = File.Exists(StatePath) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StatePath)) ?? [] : [];
        }
        catch (Exception ex)
        {
            Log.Error("Reading frozen.json failed", ex);
            all = [];
        }
        foreach (Entry e in all)
        {
            e.Copies.RemoveAll(c =>
            {
                try
                {
                    using Process p = Process.GetProcessById(c.Pid);
                    return Math.Abs((p.StartTime.ToUniversalTime() - c.StartUtc).TotalSeconds) > 2 ||
                           !string.Equals(NativeMethods.GetProcessPath(c.Pid), e.ExePath, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    return true; // exited
                }
            });
        }
        all.RemoveAll(e => e.Copies.Count == 0);
        return all;
    }

    private static void Save(List<Entry> all)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(all));
        }
        catch (Exception ex)
        {
            Log.Error("Saving frozen.json failed", ex);
        }
    }
}
