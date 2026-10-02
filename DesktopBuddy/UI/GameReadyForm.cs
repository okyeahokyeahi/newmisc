using System.Diagnostics;
using DesktopBuddy.Native;

namespace DesktopBuddy.UI;

/// <summary>
/// "Get game-ready": politely close the apps you tick (like clicking their X) to free RAM before playing.
/// Never offers Discord, Roblox, games or system processes; Roblox Studio is shown but can't be ticked,
/// because closing it could lose unsaved work.
/// </summary>
internal sealed class GameReadyForm : Form
{
    private static readonly string[] NeverClose =
        ["discord", "robloxplayerbeta", "robloxplayerlauncher", "desktopbuddy", "explorer", "steam", "steamwebhelper",
         "nvidia app", "nvcontainer", "nitrosense", "obs64", "textinputhost", "searchhost", "shellexperiencehost",
         "startmenuexperiencehost", "applicationframehost", "systemsettings", "lockapp"];

    private readonly BuddyServices _s;
    private readonly ListView _list;
    private readonly CheckBox _reopen;
    private readonly Label _result;
    private readonly Button _closeButton;

    public GameReadyForm(BuddyServices services)
    {
        _s = services;
        Ui.Setup(this, "Get game-ready", 500, 520);

        var intro = new Label
        {
            Text = "Tick the apps to close. They close normally, like clicking X (nothing is force-killed). " +
                   "Discord and Roblox are never closed.",
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(12, 10, 12, 0),
        };

        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        _list.Columns.Add("App", 300);
        _list.Columns.Add("Memory", 100, HorizontalAlignment.Right);
        _list.ItemCheck += (_, e) =>
        {
            if (_list.Items[e.Index].Tag is null) e.NewValue = CheckState.Unchecked; // Studio row
        };

        _reopen = new CheckBox
        {
            Text = "Reopen Chrome after my game (it restores tabs only if Chrome's \"Continue where you left off\" is on)",
            Dock = DockStyle.Bottom,
            Height = 44,
            Checked = services.Settings.ReopenBrowserAfterGame,
            Padding = new Padding(12, 0, 12, 0),
        };
        var bestPerformance = new CheckBox
        {
            Text = "Switch Windows to \"Best performance\" while I play (switches back after)",
            Dock = DockStyle.Bottom,
            Height = 28,
            Checked = services.Settings.BestPerformanceDuringGames,
            Padding = new Padding(12, 0, 12, 0),
        };
        bestPerformance.CheckedChanged += (_, _) =>
        {
            _s.Settings.BestPerformanceDuringGames = bestPerformance.Checked;
            _s.Settings.Save();
        };

        _result = new Label { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(12, 6, 12, 0), Font = new Font(Font, FontStyle.Bold) };
        _closeButton = Ui.Button("Close ticked apps", async (_, _) => await CloseTicked());
        var cancel = Ui.Button("Done", (_, _) => Close());
        CancelButton = cancel;

        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        listPanel.Controls.Add(_list);

        Controls.Add(listPanel);
        Controls.Add(intro);
        Controls.Add(_result);
        Controls.Add(_reopen);
        Controls.Add(bestPerformance);
        Controls.Add(Ui.ButtonRow(cancel, _closeButton));
        Load += (_, _) => Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        var apps = (_s.Resources.Latest?.AllApps ?? [])
            .Where(a => !NeverClose.Contains(a.Name.ToLowerInvariant()) &&
                        !_s.Settings.GameProcessNames.Contains(a.Name, StringComparer.OrdinalIgnoreCase) &&
                        HasWindow(a.Name))
            .OrderByDescending(a => a.MemoryBytes)
            .Take(15);

        foreach (var app in apps)
        {
            bool isStudio = app.Name.StartsWith("RobloxStudio", StringComparison.OrdinalIgnoreCase);
            var item = new ListViewItem([isStudio ? $"{Diagnosis.Label(app.Name)}: save, then close it yourself" : Diagnosis.Label(app.Name),
                Format.Bytes(app.MemoryBytes)])
            {
                Tag = isStudio ? null : app.Name,
                ForeColor = isStudio ? Ui.Grey : SystemColors.WindowText,
            };
            _list.Items.Add(item);
            if (!isStudio) item.Checked = _s.Settings.GameReadyCloseList.Contains(app.Name, StringComparer.OrdinalIgnoreCase);
        }

        if (_list.Items.Count == 0) _result.Text = "Nothing big is open. You're ready!";
    }

    private async Task CloseTicked()
    {
        var names = _list.CheckedItems.Cast<ListViewItem>().Select(i => i.Tag as string).OfType<string>().ToList();
        if (names.Count == 0) return;

        // Remember the choice for next time.
        _s.Settings.GameReadyCloseList = names;
        _s.Settings.ReopenBrowserAfterGame = _reopen.Checked;
        if (_reopen.Checked && names.Any(n => n.Equals("chrome", StringComparison.OrdinalIgnoreCase)))
            _s.Settings.BrowserToReopen = FirstPath("chrome");
        _s.Settings.Save();

        _closeButton.Enabled = false;
        _result.Text = "Closing…";
        ulong before = AvailableRam();

        foreach (string name in names)
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                    }
                    catch (InvalidOperationException)
                    {
                        // already exited
                    }
                }
            }
        }

        // Give apps a few seconds to close (and save) on their own.
        for (int i = 0; i < 16 && names.Any(StillRunning); i++) await Task.Delay(500);
        if (IsDisposed) return;

        double freedGb = Math.Max(0, (double)AvailableRam() - before) / 1073741824.0;
        var stubborn = names.Where(StillRunning).Select(Diagnosis.FriendlyName).ToList();
        _result.Text = $"Freed about {freedGb:0.0} GB of RAM." +
                       (stubborn.Count > 0 ? $" Still running (probably in the tray): {string.Join(", ", stubborn)}." : " Have fun!");
        _closeButton.Enabled = true;
        Fill();
    }

    private static bool StillRunning(string name)
    {
        Process[] ps = Process.GetProcessesByName(name);
        bool any = ps.Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });
        foreach (Process p in ps) p.Dispose();
        return any;
    }

    private static bool HasWindow(string name) => StillRunning(name);

    private static string? FirstPath(string name)
    {
        Process[] ps = Process.GetProcessesByName(name);
        try
        {
            return ps.Select(p => NativeMethods.GetProcessPath(p.Id)).FirstOrDefault(p => p != null);
        }
        finally
        {
            foreach (Process p in ps) p.Dispose();
        }
    }

    private static ulong AvailableRam()
    {
        var m = new NativeMethods.MEMORYSTATUSEX();
        NativeMethods.GlobalMemoryStatusEx(m);
        return m.ullAvailPhys;
    }
}
