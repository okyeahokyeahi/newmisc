# Desktop Buddy roadmap

## Done
- **v1:** tray app, heavy-app and RAM alerts, temperatures, suspicious-program popup, Run-key startup watch, API key slot.
- **v1.1:**
  - "Why is my laptop slow or loud?" (#1)
  - Defender tamper alarm (#2)
  - "What is this?" explainer (#4)
  - Disk space warning (#5)
  - Game mode and session report (#6)
  - Heat-slowdown (throttling) detector (#7)
  - Hidden-startup watch: scheduled tasks, services, Startup folder, Winlogon (#8)
  - Downloads check (#9)
  - Hidden-miner / idle-hog check (#10)
  - Ask Buddy AI chat, ready for an API key (#11)

- **v1.2:**
  - Start with Windows
  - Wrong graphics chip check
  - Get game-ready, plus Best performance during games
  - Lag explainer
  - 60 Hz check
  - Restart reminder
  - Quick panel (keep-awake, recent downloads, reminders)
  - Dusty-vents hint
  - Old NVIDIA driver tip
  - Downloads tidy
  - Boot-time tracker
  - AI model picker (Haiku default), plus screenshot, alert, session and download questions

- **v1.3:**
  - Settings window
  - Roblox kick explainer and Studio crash help
  - Plain-English actions in Ask Buddy (six actions, each behind Yes/No)
  - Weekly health report with battery wear and game-clip storage (clip size also in the low-disk warning)
  - Late-night nudge (opt-in)
  - "What is this?" from hidden-startup alerts
  - MAGI theme with optional AI voices
- **v1.4:**
  - Freeze (suspend) suspicious programs, with a Frozen programs menu
  - MAGI votes on Ask Buddy's actions
  - Tidy helper for Desktop and Downloads, with AI pros and cons
  - Removed: Ctrl+Alt+A sound switch (not wanted)

## Saved for later
- **#12 Roblox Studio plugin scanner.** Scan `%LocalAppData%\Roblox\Plugins` for patterns backdoored free plugins use
  (`require(<number>)`, `getfenv`, `loadstring`). Results shown as "maybe", since it's a best guess.
- **MAGI-style tray icon.** Offered, not built yet.

## Skipped on purpose
- **#3 "Gaming on battery" warning.** The laptop is always plugged in. (The power-brake warning in #7 still covers a weak charger.)
- **Battery charge limit.** Acer removed or broke it on the AN515-57, and forcing it is risky. The weekly report shows wear instead.
- **Automatic NitroSense fan control.** Undocumented, model-specific commands that conflict with NitroSense. Use NitroSense's Max fan.
- **RAM "boosters"/cleaners.** Snake oil; they make stutter worse.
- **A general "every internet connection" monitor.** Constant noise with nothing to act on.
