namespace DesktopBuddy.UI;

/// <summary>"Why is my laptop slow / loud?" results.</summary>
internal sealed class DiagnosisForm : Form
{
    private readonly BuddyServices _s;
    private readonly RichTextBox _text = Ui.ReadOnlyText();
    private List<Finding> _findings = [];

    public DiagnosisForm(BuddyServices services)
    {
        _s = services;
        Ui.Setup(this, "Why is my laptop slow or loud?", 560, 480);

        var refresh = Ui.Button("Check again", async (_, _) => await Fill());
        var ask = Ui.Button("Ask Buddy about this", (_, _) => AskAi());
        var close = Ui.Button("Close", (_, _) => Close());
        CancelButton = close;

        var padded = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 4) };
        padded.Controls.Add(_text);
        Controls.Add(padded);
        Controls.Add(Ui.ButtonRow(close, ask, refresh));
        Load += async (_, _) => await Fill();
    }

    private async Task Fill()
    {
        _text.Text = "Checking…";
        try
        {
            // Off the UI thread: the WebView2 lookup asks WMI, which can take a second or two when RAM is full.
            _findings = await Task.Run(() => Diagnosis.Run(_s.Settings, _s.Resources.Latest, _s.Temps.Latest, _s.Heat.Latest, _s.Disk));
        }
        catch (Exception ex)
        {
            Log.Error("Diagnosis failed", ex);
            _findings = [new Finding(Severity.Info, "Couldn't finish the check", "Try again in a moment.")];
        }
        if (IsDisposed) return;
        _text.Clear();
        foreach (Finding f in _findings)
        {
            (string icon, Color color) = f.Severity switch
            {
                Severity.Problem => ("●", Ui.Red),
                Severity.Warning => ("●", Ui.Amber),
                Severity.Good => ("✔", Ui.Green),
                _ => ("ℹ", Ui.Grey),
            };
            Ui.AppendHeading(_text, $"{icon}  {f.Title}", color);
            Ui.AppendBody(_text, f.Detail);
        }
        _text.SelectionStart = 0;
    }

    private void AskAi()
    {
        string summary = string.Join("\n", _findings.Select(f => $"- {f.Title}"));
        _s.ShowAsk($"Desktop Buddy's check found:\n{summary}\n\nWhat should I do first?");
    }
}
