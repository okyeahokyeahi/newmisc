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

## Saved for later (picked from the 15-idea list)
- **#12 Roblox Studio plugin scanner.** Scan `%LocalAppData%\Roblox\Plugins` for patterns backdoored free plugins use
  (`require(<number>)`, `getfenv`, `loadstring`). Results shown as "maybe", since it's a best guess.
- **#13 Daily/weekly health card.** For example "RAM over 90% for 3h today (Chrome + Studio), 1 new startup item, restart pending".
  Works locally; the AI can write a summary once there's a key.
- **#14 Plain-English actions.** "close Chrome", "pause alerts till 9", each behind a Yes/No confirmation and limited to about
  6 safe actions. Riskiest feature, because the app runs as admin.
- **#15 Battery health card.** Shows wear, e.g. "82% of original capacity". No charge limit: Acer removed or broke it on the
  AN515-57, and forcing it is risky.

## Skipped on purpose
- **#3 "Gaming on battery" warning.** The laptop is always plugged in. (The power-brake warning in #7 still covers a weak charger.)
- **Automatic NitroSense fan control.** Undocumented, model-specific commands that conflict with NitroSense. Use NitroSense's Max fan.
- **RAM "boosters"/cleaners.** Snake oil; they make stutter worse.
- **A general "every internet connection" monitor.** Constant noise with nothing to act on.
