# Desktop Buddy

A small Windows tray app that watches your laptop and warns you about problems. It was built for an
Acer Nitro AN515-57 but runs on any Windows 10/11 PC.

## What v1 does

| Feature | What you see |
|---|---|
| **Heavy-app alerts** | A notification when an app (all its processes added together, e.g. every `chrome.exe`) stays above 25% CPU or 3 GB RAM for 3+ minutes. Also warns when total RAM stays above 90%. |
| **Temperature monitor** | Live CPU and GPU temperatures. The tray face goes green → orange (within 10°C of your limit, or a short spike over it) → red (over the limit for 60+ seconds), and you get a notification then. Defaults: 90°C CPU / 85°C GPU. Short spikes are normal for this CPU and are ignored. |
| **Suspicious-program check** | A popup with **Kill it / Freeze / Always allow / Ignore / Show file** when a program has warning signs. It never kills anything on its own. One popup per program (not per copy). *Kill it* closes every copy; *Freeze* pauses every copy without closing it, so it can't do anything while you check it (resume or end it later from tray menu → *Frozen programs*; a PC restart also unfreezes it); *Ignore* hides it until the app restarts. Popups wait while a full-screen game or video is in front. |
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
| **Quick panel** | **Ctrl+Alt+B** or a left-click on the tray face: temperatures, RAM, the main buttons, keep-awake (1 h / 3 h / until off), your 5 newest downloads, and quick reminders (including "after my game"). |
| **Start with Windows** | On by default (tray menu checkbox). Uses a Task Scheduler task, so there's no admin prompt at every boot. |
| **Get game-ready** | Tick apps to close before playing (they close normally, like clicking X), see how much RAM was freed. Discord, Roblox and games are never offered, and Roblox Studio is never closed for you. Can reopen Chrome after the game. |
| **Best performance during games** | Switches Windows' power mode to Best performance while a game runs and puts it back afterwards. |
| **Wrong graphics chip check** | Warns if Roblox, Studio or a game draws on the weak Intel chip instead of the RTX for a minute, and fixes it with one click. Fixed apps stay fixed when Roblox updates. |
| **Lag explainer** | The game report says how many lag spikes you had and whether they came from your Wi-Fi (with signal and 2.4/5 GHz) or from past your router. |
| **Screen refresh check** | Notices a screen stuck at 60 Hz when it can do more (e.g. 144 Hz) and switches it after you confirm. |
| **Restart reminder** | If a Windows update has waited 3+ days for a restart: one reminder a day, never during a game. |
| **Laptop care** | Occasional tips: "runs ~7°C hotter in Roblox than in March, the vents may be dusty", an old NVIDIA driver, and a slow Windows startup with what Windows blamed. The status window shows your last startup time. |
| **Roblox kick explainer** | When Roblox disconnects you, the game report says why in plain English (error 267, 277, 279…) and quotes the game's kick message. Unknown codes get an *Ask Buddy what it means* button. |
| **Studio crash help** | If Roblox Studio closes unexpectedly, a notification points to your newest recovery/autosave file and Buddy keeps a backup copy in `%AppData%\DesktopBuddy\StudioBackups` (newest 20). |
| **Weekly health report** | Once a week (or tray menu → *Weekly health report*): this week vs last for RAM, heat slowdown, games played, Windows startup time, free disk space, battery wear, and how much space game clips take. Optional AI summary. |
| **Late-night nudge** (off by default) | After a game that ends late, the game-over notification mentions the time. |
| **Settings window** | Tray menu → *Settings…*: every option in tabs (General, Alerts, Security, Games, AI), no file editing. |
| **Tidy helper** | Tray menu → *Tidy* (or the quick panel): groups loose files on your Desktop and in Downloads (installers, zips, pictures, videos, documents, Roblox files, shortcuts, exact duplicates…) and moves the groups you tick into a `Tidied` folder next to them. Never moves folders, never touches Documents, never deletes, skips files from the last 24 hours, and every tidy can be undone. With an API key, *Ask AI* weighs the pros and cons of each group and whether moving could break anything. |
| **Downloads auto-tidy** (off by default) | Weekly, moves files older than 14 days into `Downloads\Older\<year-month>`. Never deletes, and every tidy can be undone (tray menu → *Downloads tidy*). |

Right-click the tray icon for the menu, or double-click it for the live status window.
**Pause alerts for 1 hour** silences notifications and holds back suspicious-program popups (handy while gaming).

### What counts as "suspicious"
- A program named like a Windows system process (`svchost`, `lsass`, `explorer`…) that runs from **outside** `C:\Windows`.
- A name one letter off a system process (`svch0st`, `lsasss`).
- An **unsigned** program running from Temp, Downloads, the Public folder or the Recycle Bin.

This catches junk, adware and sketchy downloads. **It is not antivirus**; keep Windows Defender on.

## MAGI theme (optional)
An Evangelion-style look for big decisions. Turn it on in the tray menu: **Look & sounds → MAGI theme**. A test vote
plays straight away so you can see and hear it.

- **What it does:** suspicious programs, risky downloads and the restart reminder open the **MAGI vote screen** instead
  of a normal popup. **BALTHASAR·2** (you and your stuff), **CASPER·3** (security) and **MELCHIOR·1** (performance)
  each vote 承認 (approve) or 否決 (deny) based on Desktop Buddy's own checks. Then the verdict lands, and you pick
  what to do. MAGI only recommends; nothing happens until you click.
- **Sounds:** built-in synthesized sounds by default. To use your own, open **Look & sounds → Open sounds folder**
  and drop files named `deciding`, `approve`, `deny`, `resolve`, `alarm` or `tick` (`.wav` or `.mp3`).
  - **Your Data transmission sound:** copy it into that folder and rename it to **`deciding.wav`**. It plays while
    the cores decide and fades out at the verdict. Quiet files are levelled automatically.
  - These files stay on your PC and are never part of the app or this repo.
- **AI voices** (optional, needs an API key): each core gets a short in-character line. That's one AI call per vote,
  about ⅕ of a cent on Haiku. The AI only voices the votes and can't change them.
- **Quiet during games:** sounds stay quiet during games, and vote screens wait until you're out of a full-screen game.

This is a fan homage. The visuals are original and no assets from the show are included.

## AI details (once you add an API key)
- **Model:** pick it in tray menu → *Set API key…*. **Claude Haiku 4.5** is the default and recommended: the cheapest, at
  about ¼–½ cent per question. Sonnet 5.5 (~1¢) and Opus 5.5 (~1–3¢) are smarter options. On Sonnet/Opus, a question the
  model declines is retried automatically on a fallback model (`fallbacks: "default"`); Haiku doesn't support that.
- **Cost cap:** stops at **$2/month** and **100 questions/day** (`AiMonthlyBudgetUsd`, `AiMaxCallsPerDay`). The status window
  shows this month's spend.
- **Where AI shows up:** Ask Buddy chat; "What is this?" on any app; *About my last alert*; *Ask Buddy about this session* in
  game reports; *Is this safe?* on a recent download in the quick panel; and **Ask about something on screen** (Ctrl+Alt+S):
  drag a box around an error or setting, check the preview, then send it with your question.
- **What's sent:** program names and numbers only (temperatures, RAM, the busiest apps), plus a screenshot only when you
  pick one yourself and press Ask, and file names in the tidy helper only when *Include file names* is ticked and you press *Ask AI*. Never window titles, files, the clipboard or your username. Click *What gets sent?* in the
  chat window to see it. Every AI question starts with you pressing a button; nothing is sent in the background.
- **What it can do:** in Ask Buddy you can say things like "close Chrome", "pause alerts for an hour", "keep it awake for 3 hours",
  "open Sound settings" or "empty the recycle bin". Only those six kinds of action exist, and **every one asks you Yes/No
  first**. It refuses to close games, Discord, Roblox Studio or Windows parts. Turn it off with `AiActions` (Settings → AI).
  With the MAGI theme on, closing an app, pausing alerts, keeping awake and emptying the Recycle Bin go to a **MAGI vote** first:
  the three cores vote from Buddy's own facts (RAM freed, unsaved work, security), optionally voiced by the AI, then you decide.
  Turn that off with `MagiVotesOnAiActions`.
- **Weekly report summary:** *Sum it up (AI)* in the weekly report sends the report's text (numbers only) for a 3-point summary.

## What it does NOT do
- **No fan control.** NitroSense has no public API, and the undocumented route is risky. Use NitroSense's Max fan when it warns you.
- More ideas are saved in [ROADMAP.md](ROADMAP.md).

## How to get it running
1. Open the repo's **Releases** page (https://github.com/okyeahokyeahi/newmisc/releases/latest) and download
   **DesktopBuddy-win-x64.zip**. No GitHub login is needed.
2. Unzip it somewhere permanent, e.g. `C:\Tools\DesktopBuddy`.
3. **For CPU temperature:** install the free PawnIO driver once. Open *Terminal* and run
   `winget install namazso.PawnIO` (or download it from https://pawnio.eu). Without it, CPU temperature
   shows **n/a** and the status window tells you so. GPU temperature works either way.
4. Double-click `DesktopBuddy.exe` and click **Yes** on the admin (UAC) prompt.
   Admin is required to read CPU temperatures and to inspect programs running as admin.
5. Windows SmartScreen may warn because the app isn't code-signed: *More info → Run anyway*.

Desktop Buddy starts with Windows automatically from then on (untick *Start with Windows* in the tray menu to stop that).

## Updating
Desktop Buddy updates itself. A minute after it starts (and every 6 hours after that) it checks the Releases page. When a
newer version exists you get an "Update available" notification: click it and the app downloads the new version, checks
its SHA-256 checksum, swaps the files and restarts itself. Settings, the allow list, logs and your API key are kept.
You can also check by hand: tray menu → *Check for updates*. Turn it off with `CheckForUpdates: false`.

## Settings
Tray menu → *Settings…* opens the Settings window. Everything is also stored in `%AppData%\DesktopBuddy\settings.json`.

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
| `AiModel` / `AiMonthlyBudgetUsd` / `AiMaxCallsPerDay` | claude-haiku-4-5 / 2.00 / 100 | AI model and spending cap |
| `StartWithWindows` | true | Start at logon (also a tray checkbox) |
| `GameReadyCloseList` | chrome, msedge… | Apps *Get game-ready* ticks by default (remembers your last choice) |
| `BestPerformanceDuringGames` | true | Best performance power mode during games |
| `WatchGraphicsChip` / `ForceRtxApps` | true / [] | Wrong-chip check, and apps you switched to the RTX |
| `TidyDownloads` / `TidyAfterDays` | false / 14 | Weekly Downloads tidy |
| `RobloxKickExplainer` / `StudioCrashHelp` | true / true | Roblox disconnect reasons, Studio crash help |
| `WeeklyReport` / `AiWeeklySummary` | true / true | Weekly report notice, and its AI summary button |
| `LateNightNudge` / `LateNightHour` | false / 23 | Late-night mention after games, and when "late" starts |
| `AiActions` | true | Let Ask Buddy do the six confirmed actions |
| `MagiVotesOnAiActions` | true | With the MAGI theme, put those actions to a MAGI vote first |

A log is kept at `%AppData%\DesktopBuddy\log.txt` (tray menu → *Open log folder*).

## Known limitations
- If CPU temperature shows **n/a**, PawnIO isn't installed (see step 3) or was blocked by antivirus.
- Your Windows account should be an administrator. If it's a standard account, the UAC prompt runs the
  app as the admin account, so settings, the watched Downloads folder and the stored API key belong to
  that account instead of yours.
- `RunOnce` startup entries are skipped because Windows Update and driver installers use them constantly.
- CPU heat-slowdown detection is a close estimate (near the 100°C limit), not the CPU's internal flag. The GPU one is exact.
- The Downloads check can only spot password protection in `.zip` and RAR5 files, not `.7z`.
- The lag explainer reads Wi-Fi signal/band from `netsh`, which only works on English Windows (and needs location access on
  Windows 11 24H2). Ping-based spike counting works regardless.
- *Start with Windows* needs your account to be an administrator (the app requires admin rights).
- The wrong-graphics-chip check only warns when the RTX is basically idle for that app, so a game split across both chips
  may not be caught.
- RAM per app is the *working set*, which double-counts memory shared between processes, so
  multi-process apps like Chrome can look a bit bigger than in Task Manager.
- The signature check only reads signatures embedded in the file. That's why "unsigned" is never
  flagged on its own; it only counts together with a risky folder.

## Building from source
Requires the .NET 8 SDK on Windows:
```
dotnet publish DesktopBuddy/DesktopBuddy.csproj -c Release -r win-x64 --self-contained true -o publish
```
