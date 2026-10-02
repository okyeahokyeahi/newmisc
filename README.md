# Desktop Buddy

A small Windows tray app that watches your laptop and warns you about problems. It was built for an
Acer Nitro AN515-57 but runs on any Windows 10/11 PC.

## What v1 does

| Feature | What you see |
|---|---|
| **Heavy-app alerts** | A notification when an app (all its processes added together, e.g. every `chrome.exe`) stays above 25% CPU or 3 GB RAM for 3+ minutes. Also warns when total RAM stays above 90%. |
| **Temperature monitor** | Live CPU and GPU temperatures. The tray face goes green → orange (within 10°C of your limit, or a short spike over it) → red (over the limit for 60+ seconds), and you get a notification then. Defaults: 90°C CPU / 85°C GPU. Short spikes are normal for this CPU and are ignored. |
| **Suspicious-program check** | A popup with **Kill it / Always allow / Ignore / Show file** when a program has warning signs. It never kills anything on its own. One popup per program (not per copy). *Kill it* closes every copy; *Ignore* hides it until the app restarts. Popups wait while a full-screen game or video is in front. |
| **Startup watch** | A notification when something new adds itself to "start with Windows". |
| **Heat slowdown detector** | Tells you when the CPU or GPU is actually *slowing itself down* because it's too hot, which is what makes games stutter. The GPU reading comes straight from the NVIDIA driver; for the CPU it's flagged when a core is within 2°C of its 100°C limit. Also warns when the power brake (a weak charger) holds the GPU back. Shows minutes of slowdown today. |
| **"Why is my laptop slow or loud?"** | One click gives plain-English causes: RAM hogs (with fixes), which apps `msedgewebview2` is really working for, Windows chores like CompatTelRunner, heat, disk space. No AI needed. |
| **"What is this?"** | Double-click any app in the list (or use the button on the suspicious-program popup) for what it usually is, where it lives, and who signed it. With an API key there's an optional AI explanation. It never says "safe". |
| **Defender tamper alarm** | Alerts if Defender's real-time protection gets switched off or something adds a "don't scan this" exclusion. Fake Roblox executors and cheats do exactly this. Also warns about exclusions that already existed on first run. |
| **Hidden-startup watch** | Catches new scheduled tasks, services, Startup-folder items and changes to the Windows login settings. To stay quiet about normal updaters, it only alerts for unsigned programs outside Program Files, or script tools (PowerShell, cmd…) pointed at user folders or the internet. |
| **Downloads check** | When a program, script or archive lands in Downloads, it shows where it came from ("from mediafire.com via youtube.com") and flags password-protected zips, fake double extensions (`.pdf.exe`), huge padded installers and unsigned programs. Click the notification to scan the file with Defender. |
| **Hidden-miner check** | While you're away (5+ min without mouse or keyboard input), it watches for unsigned or unusual programs using lots of GPU/CPU. When you come back you get a "while you were away…" notice. |
| **Disk space warning** | Warns when C: drops below 20 GB or 10% free. |
| **Game mode + report** | While Roblox, a Steam/Epic/Riot game or any full-screen game runs, alerts and popups wait. Afterwards: play time, peak temperatures and RAM, heat-slowdown minutes, and any held alerts. Sessions are logged to `game-sessions.csv`. |
| **Ask Buddy (AI)** | A chat window that sees your live temperatures, RAM and busiest apps. Needs an API key (tray menu → *Set API key…*), stored in Windows Credential Manager. |

Right-click the tray icon for the menu, or double-click it for the live status window.
**Pause alerts for 1 hour** silences notifications and holds back suspicious-program popups (handy while gaming).

### What counts as "suspicious"
- A program named like a Windows system process (`svchost`, `lsass`, `explorer`…) that runs from **outside** `C:\Windows`.
- A name one letter off a system process (`svch0st`, `lsasss`).
- An **unsigned** program running from Temp, Downloads, the Public folder or the Recycle Bin.

This catches junk, adware and sketchy downloads. **It is not antivirus**; keep Windows Defender on.

## AI (Ask Buddy) details
- **Model:** `claude-opus-5-5` by default (change `AiModel` in settings, e.g. to the cheaper `claude-haiku-4-5`). If the model
  declines a question, Anthropic's server retries it on a fallback model automatically (`fallbacks: "default"`).
- **Cost:** roughly 1–3 cents per question on the default model. A hard cap stops it at **$2/month** and **100 questions/day**
  (`AiMonthlyBudgetUsd`, `AiMaxCallsPerDay`). The status window shows this month's spend.
- **What's sent:** program names and numbers only (temperatures, RAM, the busiest apps). Never window titles, files, the
  clipboard or your username. Click *What gets sent?* in the chat window to see it.
- **What it can do:** only answer. The AI can't click, close or change anything.

## What it does NOT do
- **No fan control.** NitroSense has no public API, and the undocumented route is risky. Use NitroSense's Max fan when it warns you.
- It doesn't start with Windows automatically (see below for a manual way).
- More ideas are saved in [ROADMAP.md](ROADMAP.md).

## How to get it running
1. On GitHub open the **Actions** tab → latest **Build Desktop Buddy** run → download **DesktopBuddy-win-x64** at the bottom.
2. Unzip it somewhere permanent, e.g. `C:\Tools\DesktopBuddy`.
3. **For CPU temperature:** install the free PawnIO driver once. Open *Terminal* and run
   `winget install namazso.PawnIO` (or download it from https://pawnio.eu). Without it, CPU temperature
   shows **n/a** and the status window tells you so. GPU temperature works either way.
4. Double-click `DesktopBuddy.exe` and click **Yes** on the admin (UAC) prompt.
   Admin is required to read CPU temperatures and to inspect programs running as admin.
5. Windows SmartScreen may warn because the app isn't code-signed: *More info → Run anyway*.

**Start with Windows (optional):** Task Scheduler → *Create Task* → tick *Run with highest privileges* →
Trigger *At log on* → Action *Start a program* → pick `DesktopBuddy.exe`. (A normal Startup shortcut
would show a UAC prompt on every boot.)

## Settings
Tray menu → *Edit settings* opens `%AppData%\DesktopBuddy\settings.json`. Restart the app after saving.

| Setting | Default | Meaning |
|---|---|---|
| `AppCpuPercentThreshold` | 25 | % of the whole CPU an app must use to count as heavy |
| `AppMemoryMbThreshold` | 3072 | RAM (MB) an app must use to count as heavy |
| `SustainedMinutes` | 3 | How long it must stay heavy before an alert |
| `AlertCooldownMinutes` | 30 | Minimum gap between repeat alerts for the same thing |
| `RamPercentWarn` | 90 | Total RAM % that triggers a warning |
| `CpuTempWarnC` / `GpuTempWarnC` | 90 / 85 | Temperature alert limits |
| `TempSustainedSeconds` | 60 | How long a temp must stay over its limit before an alert |
| `ScanForSuspiciousProcesses` | true | Turn the suspicious-program check on/off |
| `WatchStartupEntries` | true | Turn the startup watch on/off |
| `AllowedExePaths` | [] | Programs you clicked *Always allow* on |
| `ResourceIgnoreList` | Idle, System… | App names never reported as heavy |
| `HeatSlowdownAlertSeconds` | 30 | How long a heat slowdown must last before an alert |
| `IdleMinutes` / `IdleGpuPercent` / `IdleCpuPercent` | 5 / 30 / 40 | Hidden-miner check: what counts as away and as busy |
| `DiskFreeWarnGb` | 20 | Low-disk warning level |
| `GameProcessNames` | Roblox, Fortnite… | Extra games that turn on game mode (Steam/Epic/Riot games are detected automatically) |
| `WatchDefender` / `WatchHiddenStartup` / `WatchDownloads` | true | Turn those features on/off |
| `AiModel` / `AiMonthlyBudgetUsd` / `AiMaxCallsPerDay` | claude-opus-5-5 / 2.00 / 100 | AI model and spending cap |

A log is kept at `%AppData%\DesktopBuddy\log.txt` (tray menu → *Open log folder*).

## Known limitations
- If CPU temperature shows **n/a**, PawnIO isn't installed (see step 3) or was blocked by antivirus.
- Your Windows account should be an administrator. If it's a standard account, the UAC prompt runs the
  app as the admin account, so settings, the watched Downloads folder and the stored API key belong to
  that account instead of yours.
- `RunOnce` startup entries are skipped because Windows Update and driver installers use them constantly.
- CPU heat-slowdown detection is a close estimate (near the 100°C limit), not the CPU's internal flag. The GPU one is exact.
- The Downloads check can only spot password protection in `.zip` and RAR5 files, not `.7z`.
- RAM per app is the *working set*, which double-counts memory shared between processes, so
  multi-process apps like Chrome can look a bit bigger than in Task Manager.
- The signature check only reads signatures embedded in the file. That's why "unsigned" is never
  flagged on its own; it only counts together with a risky folder.

## Building from source
Requires the .NET 8 SDK on Windows:
```
dotnet publish DesktopBuddy/DesktopBuddy.csproj -c Release -r win-x64 --self-contained true -o publish
```
