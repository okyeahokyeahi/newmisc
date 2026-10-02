using System.Diagnostics;
using DesktopBuddy.Monitors;
using DesktopBuddy.Native;

namespace DesktopBuddy.Magi;

/// <summary>
/// Turns Desktop Buddy's findings into MAGI votes. Each core judges from its own angle using facts the
/// app already has; the verdict is a recommendation and nothing runs until you click.
///   MELCHIOR·1 (scientist): what it does to performance and hardware.
///   BALTHASAR·2 (mother):   what it means for you and your stuff.
///   CASPER·3 (woman):       security.
/// </summary>
internal static class MagiCases
{
    private static readonly string[] FileHosts =
        ["mediafire", "mega.nz", "mega.io", "anonfiles", "gofile", "pixeldrain", "workupload", "sendspace", "dropbox", "discordapp", "cdn.discord", "4shared", "zippyshare"];

    // Matched as whole words in the file name (so "hackathon" or "cheatsheet" don't count).
    private static readonly HashSet<string> LureWords = new(StringComparer.OrdinalIgnoreCase)
        { "executor", "exploit", "exploits", "cheat", "cheats", "hack", "hacks", "hacked", "injector", "inject", "robux",
          "freerobux", "aimbot", "crack", "cracked", "keygen", "bypass", "krnl", "solara", "fluxus" };

    // ---------- Suspicious program ----------
    public static MagiCase Suspicious(SuspiciousProcess item, AppUsage? usage, bool hasWindow,
        Action terminate, Action explain, Action allow, Action ignore)
    {
        bool disguise = item.Reasons.Any(r => r.Contains("Windows process", StringComparison.OrdinalIgnoreCase) || r.Contains("one letter off", StringComparison.OrdinalIgnoreCase));
        bool signedOrWindows = FileTrust.IsTrustedPublisher(item.ExePath);

        // CASPER: security
        var casper = new CoreVote(disguise || !signedOrWindows,
            item.Reasons.FirstOrDefault() ?? "Flagged by the suspicious-program check.");

        // MELCHIOR: is it costing anything?
        double cpu = usage?.CpuPercent ?? 0;
        long mem = usage?.MemoryBytes ?? 0;
        bool heavy = cpu >= 5 || mem >= 500L * 1024 * 1024;
        var melchior = new CoreVote(heavy || disguise,
            heavy ? $"Using {cpu:0}% CPU and {Format.Bytes(mem)} for nothing you asked for."
                  : $"Only {cpu:0.0}% CPU and {Format.Bytes(mem)}: not a performance problem{(disguise ? ", but the disguise is." : ".")}");

        // BALTHASAR: are you using it?
        var balthasar = new CoreVote(!hasWindow,
            hasWindow ? "It has a window open; you might be using it. Check before closing."
                      : "No window: it's running in the background without you.");

        return new MagiCase
        {
            Question = $"Should Desktop Buddy terminate {item.Name}?",
            Proposal = $"TERMINATE {item.Name} ?",
            Context = $"PID {item.Pid} · {FileTrust.Anonymize(item.ExePath)}",
            Code = Code(item.ExePath),
            Melchior = melchior,
            Balthasar = balthasar,
            Casper = casper,
            IfApproved = "TERMINATE RECOMMENDED. Your call.",
            IfDenied = "NOT RECOMMENDED. Check what it is first.",
            Security = true,
            Actions =
            [
                new MagiAction("Terminate", terminate, Primary: casper.Approve && melchior.Approve || casper.Approve && balthasar.Approve || melchior.Approve && balthasar.Approve),
                new MagiAction("What is this?", explain, KeepOpen: true),
                new MagiAction("Always allow", allow),
                new MagiAction("Ignore", ignore),
            ],
        };
    }

    // ---------- Risky download ----------
    public static MagiCase Download(DownloadReport report, Action scan, Action showFile, Action recycle)
    {
        string name = report.FileName.ToLowerInvariant();
        var words = System.Text.RegularExpressions.Regex.Split(Path.GetFileNameWithoutExtension(name), "[^a-z0-9]+");
        bool lure = words.Any(LureWords.Contains) || name.Replace(" ", "").Contains("freerobux", StringComparison.Ordinal);
        string? fromFileHost = FileHosts.FirstOrDefault(h => (report.FromHost ?? "").Contains(h, StringComparison.OrdinalIgnoreCase));
        bool passwordProtected = report.Warnings.Any(w => w.Contains("Password", StringComparison.OrdinalIgnoreCase));
        bool huge = report.Warnings.Any(w => w.Contains("huge", StringComparison.OrdinalIgnoreCase));
        bool unsigned = report.Warnings.Any(w => w.Contains("signed", StringComparison.OrdinalIgnoreCase));
        bool disguised = report.Warnings.Any(w => w.Contains("Fake extension", StringComparison.OrdinalIgnoreCase) ||
                                                  w.Contains("damaged or disguised", StringComparison.OrdinalIgnoreCase));
        bool script = report.Warnings.Any(w => w.Contains("run commands", StringComparison.OrdinalIgnoreCase));

        // The question is "allow opening it?", so APPROVE = fine to open.
        var casper = new CoreVote(report.Warnings.Count == 0,
            report.Warnings.FirstOrDefault() ?? "No red flags in the file itself.");
        var melchior = new CoreVote(!huge && !passwordProtected && !disguised && !script,
            huge ? "Padded to an absurd size to dodge virus scanners."
            : passwordProtected ? "Locked so antivirus can't see inside. No honest reason for that."
            : disguised ? "The file isn't what its name says it is."
            : script ? "A script: it runs commands the moment it's opened."
            : unsigned ? "Unsigned, but nothing structurally odd about it."
            : "Nothing odd about the file's structure.");
        var balthasar = new CoreVote(!lure && fromFileHost == null,
            lure ? "Named like a cheat/executor. Those steal Roblox and Discord accounts."
            : fromFileHost != null ? $"Came from a file-sharing site ({report.FromHost}). Anyone can upload there."
            : report.FromHost != null ? $"Came from {report.FromHost}." : "Couldn't tell where it came from.");

        string source = report.FromHost != null ? $"from {report.FromHost}" + (report.ViaHost != null && report.ViaHost != report.FromHost ? $" via {report.ViaHost}" : "") : "source unknown";
        return new MagiCase
        {
            Question = $"Should {report.FileName} be opened?",
            Proposal = $"ALLOW OPENING {report.FileName} ?",
            Context = $"{source} · {string.Join(" · ", report.Warnings.Take(2))}",
            Code = Code(report.FilePath),
            Melchior = melchior,
            Balthasar = balthasar,
            Casper = casper,
            IfApproved = "ALLOWED. Still worth a Defender scan first.",
            IfDenied = "BLOCKED. Scan it, then recycle it.",
            Security = true,
            Actions =
            [
                new MagiAction("Scan with Defender", scan, Primary: true),
                new MagiAction("Show file", showFile),
                new MagiAction("Move to Recycle Bin", recycle),
            ],
        };
    }

    // ---------- Restart pending ----------
    public static MagiCase Restart(int days, Action restart)
    {
        Process[] studio = Process.GetProcessesByName("RobloxStudioBeta");
        bool studioOpen = studio.Length > 0;
        foreach (Process p in studio) p.Dispose();
        int hour = DateTime.Now.Hour;
        var melchior = new CoreVote(true, $"An update has waited {days} days. Restarting finishes it and reloads drivers.");
        var balthasar = new CoreVote(!studioOpen,
            studioOpen ? "Roblox Studio is open. Save your place first." : "Nothing important open. Good moment.");
        var casper = new CoreVote(true, "Security fixes only take effect after the restart.");
        return new MagiCase
        {
            Question = "Should the PC restart to finish the Windows update?",
            Proposal = "RESTART TO FINISH WINDOWS UPDATE ?",
            Context = $"Waiting {days} days · {DateTime.Now:HH:mm}" + (hour >= 23 || hour < 5 ? " · late: restart, then sleep" : ""),
            Code = "204",
            Melchior = melchior,
            Balthasar = balthasar,
            Casper = casper,
            IfApproved = studioOpen ? "APPROVED. Save in Studio, then restart." : "APPROVED. Restart when ready.",
            IfDenied = "DENIED for now.",
            Actions = [new MagiAction("Restart in 30 s", restart, Primary: true)],
        };
    }

    // ---------- Test vote (tray menu) ----------
    public static MagiCase Test(Action done) => new()
    {
        Question = "Should one follow emotions instead of logic? (test vote)",
        Proposal = "TEST VOTE: SYSTEMS CHECK ?",
        Context = "No real decision. Checks the screen and your sounds.",
        Code = "000",
        Melchior = new CoreVote(true, "Sensors online. Temperatures and RAM readable."),
        Balthasar = new CoreVote(false, "Logic alone isn't how a person decides."),
        Casper = new CoreVote(true, "Security watchers running."),
        IfApproved = "SYSTEMS NOMINAL.",
        IfDenied = "SYSTEMS NOMINAL.",
        Actions = [new MagiAction("OK", done, Primary: true)],
    };

    /// <summary>Stable 3-digit code per item, for flavour.</summary>
    private static string Code(string key)
    {
        int h = 17;
        foreach (char c in key.ToLowerInvariant()) h = h * 31 + c;
        return (Math.Abs(h) % 900 + 100).ToString();
    }
}
