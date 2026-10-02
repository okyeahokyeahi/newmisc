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
| **API key slot** | Tray menu → *Set API key…* stores a key in Windows Credential Manager. Nothing uses it yet; AI features come in v2. |

Right-click the tray icon for the menu, or double-click it for the live status window.
**Pause alerts for 1 hour** silences notifications and holds back suspicious-program popups (handy while gaming).

### What counts as "suspicious"
- A program named like a Windows system process (`svchost`, `lsass`, `explorer`…) that runs from **outside** `C:\Windows`.
- A name one letter off a system process (`svch0st`, `lsasss`).
- An **unsigned** program running from Temp, Downloads, the Public folder or the Recycle Bin.

This catches junk, adware and sketchy downloads. **It is not antivirus**; keep Windows Defender on.

## What it does NOT do (yet)
- **No fan control.** NitroSense has no public API. v1 only *reads* temperatures; switching fan modes is planned for v2 once it's tested on this exact laptop.
- No AI features yet. The key is only stored for now.
- It doesn't start with Windows automatically yet (see below for a manual way).

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

A log is kept at `%AppData%\DesktopBuddy\log.txt` (tray menu → *Open log folder*).

## Known limitations
- If CPU temperature shows **n/a**, PawnIO isn't installed (see step 3) or was blocked by antivirus.
- Your Windows account should be an administrator. If it's a standard account, the UAC prompt runs the
  app as the admin account, so settings, the watched Downloads folder and the stored API key belong to
  that account instead of yours.
- Only `Run` startup entries are watched. `RunOnce` is skipped because Windows Update and driver installers
  use it constantly.
- RAM per app is the *working set*, which double-counts memory shared between processes, so
  multi-process apps like Chrome can look a bit bigger than in Task Manager.
- The signature check only reads signatures embedded in the file. That's why "unsigned" is never
  flagged on its own; it only counts together with a risky folder.

## Building from source
Requires the .NET 8 SDK on Windows:
```
dotnet publish DesktopBuddy/DesktopBuddy.csproj -c Release -r win-x64 --self-contained true -o publish
```
