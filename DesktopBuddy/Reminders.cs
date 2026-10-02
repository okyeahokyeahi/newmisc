using System.Text.Json;

namespace DesktopBuddy;

public sealed class Reminder
{
    public string Text { get; set; } = "";
    /// <summary>When it's due (local time), or null for "when my game ends".</summary>
    public DateTime? DueAt { get; set; }
    public bool AfterGame => DueAt == null;

    public string Describe() => AfterGame ? $"{Text} (after my game)" : $"{Text} ({DueAt:HH:mm})";
}

/// <summary>Little reminders from the quick panel, saved so they survive a restart.</summary>
internal sealed class ReminderStore
{
    private static readonly string FilePath = Path.Combine(Settings.Folder, "reminders.json");
    private readonly List<Reminder> _items = Load();
    private readonly object _gate = new();

    public IReadOnlyList<Reminder> All
    {
        get { lock (_gate) return [.. _items]; }
    }

    public void Add(Reminder r)
    {
        lock (_gate) _items.Add(r);
        Save();
    }

    public void Remove(Reminder r)
    {
        lock (_gate) _items.Remove(r);
        Save();
    }

    /// <summary>Removes and returns reminders that are due. Timed ones wait while a game runs.</summary>
    public List<Reminder> TakeDue(bool inGame, bool gameJustEnded)
    {
        List<Reminder> due;
        lock (_gate)
        {
            due = _items.Where(r => r.AfterGame ? gameJustEnded : !inGame && r.DueAt <= DateTime.Now).ToList();
            foreach (Reminder r in due) _items.Remove(r);
        }
        if (due.Count > 0) Save();
        return due;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            lock (_gate) File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
        }
        catch (Exception ex)
        {
            Log.Error("Saving reminders failed", ex);
        }
    }

    private static List<Reminder> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch
        {
            return [];
        }
    }
}
