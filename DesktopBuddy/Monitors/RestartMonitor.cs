using System.Text.Json;
using DesktopBuddy.Native;
using Microsoft.Win32;

namespace DesktopBuddy.Monitors;

/// <summary>
/// Reminds you when Windows has been waiting 3+ days to restart for an update. Never during a game,
/// at most once a day, and only while you're actually at the PC.
/// </summary>
public sealed class RestartMonitor(GameSessionTracker games)
{
    private static readonly string StatePath = Path.Combine(Settings.Folder, "restart-state.json");

    private sealed class State
    {
        public DateTime? PendingSinceUtc { get; set; }
        public string? LastReminderDay { get; set; }
    }

    private State? _state;

    public bool Pending { get; private set; }
    public TimeSpan PendingFor => _state?.PendingSinceUtc is DateTime since ? DateTime.UtcNow - since : TimeSpan.Zero;

    /// <summary>(days waiting)</summary>
    public event Action<int>? Remind;

    public void Tick()
    {
        _state ??= Load();
        Pending = IsRestartPending();
        string today = DateTime.Now.ToString("yyyy-MM-dd");

        if (!Pending)
        {
            if (_state.PendingSinceUtc != null)
            {
                _state.PendingSinceUtc = null;
                Save(_state);
            }
            return;
        }

        if (_state.PendingSinceUtc == null)
        {
            _state.PendingSinceUtc = DateTime.UtcNow;
            Save(_state);
        }

        bool goodMoment = !games.InSession &&
                          NativeMethods.IdleTime() < TimeSpan.FromMinutes(2) &&     // they're at the PC
                          Environment.TickCount64 > TimeSpan.FromMinutes(15).TotalMilliseconds && // not right after boot
                          !NativeMethods.UserIsBusy();
        if (PendingFor < TimeSpan.FromDays(3) || !goodMoment || _state.LastReminderDay == today) return;

        _state.LastReminderDay = today;
        Save(_state);
        Remind?.Invoke((int)PendingFor.TotalDays);
    }

    private static bool IsRestartPending()
    {
        string[] keys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        ];
        foreach (string k in keys)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(k);
            if (key != null) return true;
        }
        return false;
    }

    private static State Load()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(State s)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(s));
        }
        catch (Exception ex)
        {
            Log.Error("Saving restart state failed", ex);
        }
    }
}
