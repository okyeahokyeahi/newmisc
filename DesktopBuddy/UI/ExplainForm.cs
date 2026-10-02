using DesktopBuddy.Ai;
using DesktopBuddy.Knowledge;
using DesktopBuddy.Monitors;
using DesktopBuddy.Native;

namespace DesktopBuddy.UI;

/// <summary>"What is this program?" Facts from Windows plus the built-in table; optional AI hint.</summary>
internal sealed class ExplainForm : Form
{
    private readonly BuddyServices _s;
    private readonly string _name;
    private readonly string? _path;
    private readonly IReadOnlyList<string>? _reasons;
    private readonly RichTextBox _text = Ui.ReadOnlyText();
    private readonly Button _askButton;

    public ExplainForm(BuddyServices services, string name, string? path, IReadOnlyList<string>? reasons)
    {
        _s = services;
        _name = name;
        _path = path ?? FindPath(name);
        _reasons = reasons;
        Ui.Setup(this, $"What is {name}?", 540, 440);

        _askButton = Ui.Button(_s.Ai.HasKey ? "Ask AI about it" : "Ask AI (needs API key)", async (_, _) => await AskAi());
        var showFile = Ui.Button("Show file", (_, _) => ShowFile());
        showFile.Enabled = _path != null;
        var close = Ui.Button("Close", (_, _) => Close());
        CancelButton = close;

        var padded = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 4) };
        padded.Controls.Add(_text);
        Controls.Add(padded);
        Controls.Add(Ui.ButtonRow(close, _askButton, showFile));
        Load += (_, _) => Fill();
    }

    private void Fill()
    {
        KnownProcess? known = KnownProcesses.Find(_name);
        Ui.AppendHeading(_text, known != null ? $"{_name}: usually {known.Name}" : _name);
        Ui.AppendBody(_text, known?.Description ?? "Not in Desktop Buddy's list of common programs.");

        if (_path != null)
        {
            string? publisher = FileTrust.Publisher(_path);
            bool signed = FileTrust.IsSigned(_path);
            Ui.AppendHeading(_text, "Where it lives");
            Ui.AppendBody(_text, $"{_path}\nThat's {FileTrust.DescribeLocation(_path)}.");
            Ui.AppendHeading(_text, "Who made it");
            Ui.AppendBody(_text, signed
                    ? $"Digitally signed by {publisher ?? "a verified publisher"}."
                    : "Not digitally signed. Many small legit tools aren't, but most malware isn't either.",
                signed ? null : Ui.Amber);

            if (known != null && known.Kind is ProcessKind.WindowsCore or ProcessKind.WindowsChore &&
                !_path.StartsWith(FileTrust.WindowsDir, StringComparison.OrdinalIgnoreCase))
            {
                Ui.AppendBody(_text, "⚠ The real version of this Windows program lives in C:\\Windows. This copy doesn't. " +
                                     "That's a common malware disguise. Run a Defender scan.", Ui.Red);
            }
        }
        else
        {
            Ui.AppendBody(_text, "Windows won't say where this one lives (normal for protected system processes).", Ui.Grey);
        }

        if (_s.Resources.Latest?.AllApps.FirstOrDefault(a => a.Name.Equals(_name, StringComparison.OrdinalIgnoreCase)) is AppUsage usage)
        {
            Ui.AppendHeading(_text, "Right now");
            Ui.AppendBody(_text, $"{usage.ProcessCount} running cop{(usage.ProcessCount == 1 ? "y" : "ies")}, " +
                                 $"{usage.CpuPercent:0.0}% CPU, {Format.Bytes(usage.MemoryBytes)} RAM.");
        }

        if (_reasons is { Count: > 0 })
        {
            Ui.AppendHeading(_text, "Why it was flagged", Ui.Red);
            Ui.AppendBody(_text, string.Join("\n", _reasons.Select(r => "• " + r)));
        }

        Ui.AppendBody(_text, "Names can be faked, so this is a guide, not a guarantee.", Ui.Grey);
    }

    private async Task AskAi()
    {
        if (!_s.Ai.HasKey)
        {
            _s.ShowApiKey();
            _askButton.Text = _s.Ai.HasKey ? "Ask AI about it" : "Ask AI (needs API key)";
            return;
        }

        _askButton.Enabled = false;
        _askButton.Text = "Asking…";
        try
        {
            string answer = await _s.Ai.Ask(Prompts.System, [(true, Prompts.ExplainProcess(_name, _path, _reasons))]);
            Ui.AppendHeading(_text, "AI's take (a hint, not a safety verdict)", Ui.Grey);
            Ui.AppendBody(_text, answer);
            _text.ScrollToCaret();
        }
        catch (AiUnavailableException ex)
        {
            Ui.AppendBody(_text, ex.Message, Ui.Amber);
        }
        finally
        {
            if (!IsDisposed)
            {
                _askButton.Enabled = true;
                _askButton.Text = "Ask AI again";
            }
        }
    }

    private void ShowFile()
    {
        if (_path != null) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_path}\"");
    }

    private static string? FindPath(string processName)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(processName);
        try
        {
            return processes.Select(p => NativeMethods.GetProcessPath(p.Id)).FirstOrDefault(p => p != null);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }
}
