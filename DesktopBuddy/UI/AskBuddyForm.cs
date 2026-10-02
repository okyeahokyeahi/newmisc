using DesktopBuddy.Ai;

namespace DesktopBuddy.UI;

/// <summary>Chat with an AI that can see the laptop's live stats. Works only once an API key is stored.</summary>
internal sealed class AskBuddyForm : Form
{
    private const int MaxTurnsKept = 12;

    private readonly BuddyServices _s;
    private readonly RichTextBox _transcript = Ui.ReadOnlyText();
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly Label _status;
    private readonly List<(bool FromUser, string Text)> _history = [];
    private readonly CancellationTokenSource _closing = new();

    public AskBuddyForm(BuddyServices services, string? initialQuestion)
    {
        _s = services;
        Ui.Setup(this, "Ask Buddy", 560, 560);

        _input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Ask anything about your laptop…" };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                _ = Send();
            }
        };
        _send = Ui.Button("Send", async (_, _) => await Send());

        var inputRow = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 74, ColumnCount = 2, Padding = new Padding(8, 4, 8, 4) };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.Controls.Add(_input, 0, 0);
        inputRow.Controls.Add(_send, 1, 0);

        var quick = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        foreach (string q in new[] { "Why is it slow right now?", "Are my temperatures OK?", "Is my RAM enough?" })
            quick.Controls.Add(new LinkLabel { Text = q, AutoSize = true, Margin = new Padding(4, 6, 12, 2) }.With(l => l.LinkClicked += async (_, _) => await Send(q)));

        _status = new Label { Dock = DockStyle.Bottom, Height = 40, ForeColor = Ui.Grey, Padding = new Padding(12, 4, 12, 0) };
        var whatsSent = new LinkLabel { Text = "What gets sent?", Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(12, 0, 12, 0) };
        whatsSent.LinkClicked += (_, _) => MessageBox.Show(this,
            "Each question is sent with these live readings (program names and numbers only; no window titles, files, " +
            "clipboard or usernames):\n\n" + Prompts.Stats(_s), "What gets sent", MessageBoxButtons.OK, MessageBoxIcon.Information);

        var padded = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 4) };
        padded.Controls.Add(_transcript);

        Controls.Add(padded);
        Controls.Add(quick);
        Controls.Add(inputRow);
        Controls.Add(whatsSent);
        Controls.Add(_status);

        FormClosed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };
        Load += (_, _) =>
        {
            Greet();
            RefreshStatus();
            if (!string.IsNullOrWhiteSpace(initialQuestion)) _input.Text = initialQuestion;
            _input.Focus();
        };
    }

    /// <summary>Puts a question in the box (used when the window is already open).</summary>
    public void SetQuestion(string? question)
    {
        if (!string.IsNullOrWhiteSpace(question)) _input.Text = question;
        _input.Focus();
    }

    private void Greet()
    {
        if (_s.Ai.HasKey)
        {
            Ui.AppendBody(_transcript, "Hi! I can see your temperatures, RAM and busiest apps. Ask me anything about your laptop.", Ui.Grey);
        }
        else
        {
            Ui.AppendBody(_transcript, "Ask Buddy needs an AI API key. Tray menu > Set API key… (or click Send and I'll open it). " +
                                       "Until then, \"Why is my laptop slow or loud?\" works without one.", Ui.Amber);
        }
    }

    private void RefreshStatus()
    {
        var usage = AiClient.CurrentUsage();
        _status.Text = $"AI spend this month: ${usage.SpentUsd:0.00} of ${_s.Settings.AiMonthlyBudgetUsd:0.00} · " +
                       $"{usage.CallsToday}/{_s.Settings.AiMaxCallsPerDay} questions today · model {_s.Settings.AiModel}";
    }

    private async Task Send(string? quickQuestion = null)
    {
        string question = (quickQuestion ?? _input.Text).Trim();
        if (question.Length == 0 || !_send.Enabled) return;

        if (!_s.Ai.HasKey)
        {
            _s.ShowApiKey();
            if (!_s.Ai.HasKey) return;
        }

        if (quickQuestion == null) _input.Clear();
        Ui.AppendHeading(_transcript, "You");
        Ui.AppendBody(_transcript, question);
        _send.Enabled = false;
        _send.Text = "…";

        // Only the newest question carries live stats; older turns keep just the text.
        var turns = new List<(bool, string)>(_history) { (true, Prompts.WithStats(_s, question)) };
        try
        {
            string answer = await _s.Ai.Ask(Prompts.System, turns, _closing.Token);
            if (IsDisposed) return;
            _history.Add((true, question));
            _history.Add((false, answer));
            while (_history.Count > MaxTurnsKept) _history.RemoveRange(0, 2);

            Ui.AppendHeading(_transcript, "Buddy", Ui.Green);
            Ui.AppendBody(_transcript, answer);
        }
        catch (AiUnavailableException ex)
        {
            if (!IsDisposed) Ui.AppendBody(_transcript, ex.Message, Ui.Amber);
        }
        catch (OperationCanceledException)
        {
            // window closed mid-question
        }
        catch (Exception ex)
        {
            Log.Error("Ask Buddy failed", ex);
            if (!IsDisposed) Ui.AppendBody(_transcript, "Something went wrong. Try again (details are in the log).", Ui.Amber);
        }
        finally
        {
            if (!IsDisposed)
            {
                _send.Enabled = true;
                _send.Text = "Send";
                _transcript.SelectionStart = _transcript.TextLength;
                _transcript.ScrollToCaret();
                RefreshStatus();
            }
        }
    }
}

internal static class ControlExtensions
{
    public static T With<T>(this T control, Action<T> configure) where T : Control
    {
        configure(control);
        return control;
    }
}
