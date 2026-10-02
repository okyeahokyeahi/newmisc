namespace DesktopBuddy.Knowledge;

public enum ProcessKind
{
    WindowsCore,   // part of Windows, always running
    WindowsChore,  // Windows background job that finishes on its own
    Hardware,      // driver/helper from NVIDIA, Intel, Acer, Realtek...
    App,
    Browser,
    Game,
    Launcher,
}

public sealed record KnownProcess(string Name, string Description, ProcessKind Kind);

/// <summary>
/// Plain-English descriptions of common processes. Matching is by name only, and malware can
/// copy a name, so this explains what a name USUALLY is; it never proves a file is safe.
/// </summary>
public static class KnownProcesses
{
    private static readonly Dictionary<string, KnownProcess> Table = Build();

    public static KnownProcess? Find(string processName) =>
        Table.TryGetValue(Path.GetFileNameWithoutExtension(processName).ToLowerInvariant(), out var p) ? p : null;

    public static bool IsWindowsChore(string processName) => Find(processName)?.Kind == ProcessKind.WindowsChore;

    private static Dictionary<string, KnownProcess> Build()
    {
        var t = new Dictionary<string, KnownProcess>();
        void Add(ProcessKind kind, string name, string description, params string[] exeNames)
        {
            foreach (string exe in exeNames) t[exe.ToLowerInvariant()] = new KnownProcess(name, description, kind);
        }

        // --- Windows core ---
        Add(ProcessKind.WindowsCore, "Service Host", "Runs many small Windows services. Lots of copies is normal.", "svchost");
        Add(ProcessKind.WindowsCore, "Desktop Window Manager", "Draws every window and effect on your screen. Uses more GPU with more monitors/animations.", "dwm");
        Add(ProcessKind.WindowsCore, "Windows Explorer", "Your taskbar, Start menu and File Explorer.", "explorer");
        Add(ProcessKind.WindowsCore, "Windows login/security core", "Core Windows security and login processes. Never close these.", "lsass", "csrss", "winlogon", "wininit", "smss", "services", "lsaiso");
        Add(ProcessKind.WindowsCore, "Windows shell host", "Hosts parts of the Windows interface (Start, notifications, settings pages).", "sihost", "shellhost", "shellexperiencehost", "startmenuexperiencehost", "textinputhost", "ctfmon", "taskhostw", "runtimebroker", "applicationframehost", "systemsettings", "lockapp", "fontdrvhost", "dllhost", "conhost", "smartscreen", "securityhealthsystray", "securityhealthservice", "audiodg", "spoolsv", "wudfhost", "dashost", "unsecapp", "wlanext", "sgrmbroker", "registry", "memory compression", "system", "idle", "secure system");
        Add(ProcessKind.WindowsCore, "Windows Search / Widgets", "Windows Search and the Widgets/news panel. Widgets use Edge WebView2 behind the scenes.", "searchhost", "searchapp", "widgets", "widgetservice", "phoneexperiencehost");
        Add(ProcessKind.WindowsCore, "Microsoft Defender", "Windows' built-in antivirus. High CPU usually means a scan is running; it settles down by itself.", "msmpeng", "mpdefendercoreservice", "nissrv", "mpcmdrun");

        // --- Windows chores (temporary) ---
        Add(ProcessKind.WindowsChore, "Microsoft Compatibility Telemetry", "Windows collects compatibility/usage data. Runs for a while, then stops. Safe, just annoying.", "compattelrunner", "devicecensus");
        Add(ProcessKind.WindowsChore, "Windows Update", "Downloading or installing Windows updates. Finishes by itself; a restart may follow.", "tiworker", "trustedinstaller", "mousocoreworker", "usoclient", "wuauclt", "musnotification", "musnotificationux");
        Add(ProcessKind.WindowsChore, "Windows Search indexer", "Indexing your files so search is fast. Busy after big copies/installs, then quiet.", "searchindexer", "searchprotocolhost", "searchfilterhost");
        Add(ProcessKind.WindowsChore, "Windows Management (WMI)", "Answers system-info requests from apps (including this one). Short bursts are normal.", "wmiprvse");
        Add(ProcessKind.WindowsChore, ".NET optimizer", "Windows pre-compiling .NET apps after an update. Temporary.", "mscorsvw", "ngentask");
        Add(ProcessKind.WindowsChore, "Installer", "Windows Installer is installing/updating something.", "msiexec");
        Add(ProcessKind.WindowsChore, "Windows Error Reporting", "Collecting a crash report after something crashed.", "werfault", "wermgr");

        // --- Hardware ---
        Add(ProcessKind.Hardware, "NVIDIA driver helpers", "Part of your NVIDIA graphics driver and the NVIDIA app.", "nvdisplay.container", "nvcontainer", "nvidia share", "nvidia web helper", "nvsphelper64", "nvidia overlay", "nvidia app", "nvbroadcast.container", "nvcplui");
        Add(ProcessKind.Hardware, "Intel driver helpers", "Part of Intel's graphics/audio/Wi-Fi drivers.", "igfxem", "igfxcuiservice", "igfxext", "intelaudioservice", "jhi_service", "lms", "esrv", "esrv_svc", "intelcphdcpsvc", "intelcpheciservice", "oneapp.igcc.winservice", "igcc");
        Add(ProcessKind.Hardware, "Acer software", "Acer's NitroSense / Care Center / Quick Access helpers.", "nitrosense", "nitrosenseservice", "predatorsense", "acerservice", "acercentralservice", "acerqaagent", "acersysmonitor", "acercarecenter", "acerccagent", "quickaccess", "accessagent", "acerdiagent", "acerqaplatformservice");
        Add(ProcessKind.Hardware, "Realtek audio", "Your sound driver's control panel and background service.", "rtkauduservice64", "rtkaudioservice64", "realtekaudiocontrol", "ravbg64");
        Add(ProcessKind.Hardware, "Killer networking", "Rivet/Intel Killer network software that ships on many gaming laptops.", "killernetworkservice", "killer.analytics.service", "killercontrolcenter");

        // --- Browsers & web engines ---
        Add(ProcessKind.Browser, "Google Chrome", "Each tab and extension is its own process, so 20-40 copies is normal. Memory grows with open tabs.", "chrome");
        Add(ProcessKind.Browser, "Microsoft Edge", "Each tab and extension is its own process.", "msedge");
        Add(ProcessKind.Browser, "Firefox", "Each group of tabs runs in its own process.", "firefox");
        Add(ProcessKind.Browser, "Opera GX", "Gaming browser; each tab is its own process.", "opera", "opera_gx");
        Add(ProcessKind.Browser, "Edge WebView2", "A built-in web engine other apps use to show their screens (Widgets, Teams, Copilot, many launchers). Its memory belongs to those apps.", "msedgewebview2");

        // --- Apps ---
        Add(ProcessKind.App, "Discord", "Voice/chat app. Several copies is normal.", "discord", "discordptb", "discordcanary");
        Add(ProcessKind.App, "Roblox Studio", "Roblox's game editor. Big places and Play-test sessions use a lot of RAM.", "robloxstudiobeta", "robloxstudiolauncherbeta");
        Add(ProcessKind.App, "Spotify", "Music app.", "spotify");
        Add(ProcessKind.App, "OneDrive", "Microsoft cloud file sync. Busy while syncing.", "onedrive", "filecoauth");
        Add(ProcessKind.App, "Microsoft Teams", "Chat/meetings app (uses WebView2).", "ms-teams", "teams");
        Add(ProcessKind.App, "Copilot", "Microsoft's AI assistant (uses WebView2).", "copilot", "microsoft.copilot");
        Add(ProcessKind.App, "OBS Studio", "Recording/streaming software.", "obs64");
        Add(ProcessKind.App, "Visual Studio Code", "Code editor.", "code");
        Add(ProcessKind.App, "Microsoft Office", "Office apps.", "winword", "excel", "powerpnt", "outlook", "olk");
        Add(ProcessKind.App, "Desktop Buddy", "This app.", "desktopbuddy");

        // --- Launchers ---
        Add(ProcessKind.Launcher, "Steam", "Game launcher. Uses CPU/disk while downloading or updating games.", "steam", "steamwebhelper", "steamservice", "gameoverlayui64");
        Add(ProcessKind.Launcher, "Epic Games Launcher", "Game launcher.", "epicgameslauncher", "epicwebhelper", "epiconlineservices", "epiconlineserviceshost");
        Add(ProcessKind.Launcher, "Riot Client / Vanguard", "Riot's launcher and anti-cheat (Valorant, League).", "riotclientservices", "riotclientux", "riotclientcrashhandler", "vgc", "vgtray");
        Add(ProcessKind.Launcher, "Battle.net", "Blizzard's launcher.", "battle.net", "agent");
        Add(ProcessKind.Launcher, "Xbox app", "Xbox / Game Pass app.", "xboxpcapp", "gamingservices", "gamingservicesnet", "xboxappservices");
        Add(ProcessKind.Launcher, "EA app", "EA's launcher.", "eadesktop", "eabackgroundservice");

        // --- Games ---
        Add(ProcessKind.Game, "Roblox", "The Roblox game client.", "robloxplayerbeta", "robloxplayerlauncher");
        Add(ProcessKind.Game, "Minecraft", "Minecraft.", "minecraft.windows", "minecraftlauncher");
        Add(ProcessKind.Game, "Fortnite", "Fortnite.", "fortniteclient-win64-shipping");
        Add(ProcessKind.Game, "Valorant", "Valorant.", "valorant-win64-shipping");
        Add(ProcessKind.Game, "Counter-Strike 2", "CS2.", "cs2");
        Add(ProcessKind.Game, "GTA V", "Grand Theft Auto V.", "gta5", "gta5_enhanced");
        Add(ProcessKind.Game, "Apex Legends", "Apex Legends.", "r5apex", "r5apex_dx12");
        Add(ProcessKind.Game, "Rocket League", "Rocket League.", "rocketleague");

        return t;
    }
}
