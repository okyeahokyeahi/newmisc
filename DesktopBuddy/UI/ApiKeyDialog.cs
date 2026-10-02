using DesktopBuddy.Native;

namespace DesktopBuddy.UI;

/// <summary>Stores the AI API key (in Windows Credential Manager) and picks which Claude model to use.</summary>
internal sealed class ApiKeyDialog : Form
{
    public static readonly (string Id, string Label)[] Models =
    [
        ("claude-haiku-4-5", "Claude Haiku 4.5: cheapest, about ¼–½¢ per question (recommended)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5: smarter, about 1¢ per question"),
        ("claude-opus-5-5", "Claude Opus 5.5: smartest, about 1–3¢ per question"),
    ];

    private readonly Settings _settings;
    private readonly TextBox _keyBox;
    private readonly ComboBox _model;
    private readonly Label _status;

    public ApiKeyDialog(Settings settings)
    {
        _settings = settings;
        Text = "Desktop Buddy: AI setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 300);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "Paste an Anthropic API key (starts with \"sk-ant-\"). It's stored in Windows Credential Manager, not in a file. " +
                   "Used by Ask Buddy, \"What is this?\" and the other AI extras.",
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            Margin = new Padding(0, 0, 0, 8),
        });
        _keyBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "sk-ant-..." };
        layout.Controls.Add(_keyBox);
        _status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 12) };
        layout.Controls.Add(_status);

        layout.Controls.Add(new Label { Text = "Model:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _model = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var (_, label) in Models) _model.Items.Add(label);
        int current = Array.FindIndex(Models, m => m.Id == settings.AiModel);
        if (current < 0)
        {
            _model.Items.Add($"{settings.AiModel} (set in settings.json)");
            current = _model.Items.Count - 1;
        }
        _model.SelectedIndex = current;
        _model.SelectedIndexChanged += (_, _) => SaveModel();
        layout.Controls.Add(_model);
        layout.Controls.Add(new Label
        {
            Text = $"Spending cap: ${settings.AiMonthlyBudgetUsd:0.00}/month and {settings.AiMaxCallsPerDay} questions/day (change in settings.json).",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
        });

        var save = new Button { Text = "Save key", AutoSize = true };
        var remove = new Button { Text = "Remove key", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveKey();
        remove.Click += (_, _) => { CredentialStore.DeleteApiKey(); RefreshStatus(); };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange([close, save, remove]);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = close;
        RefreshStatus();
    }

    private void SaveModel()
    {
        if (_model.SelectedIndex >= 0 && _model.SelectedIndex < Models.Length)
        {
            _settings.AiModel = Models[_model.SelectedIndex].Id;
            _settings.Save();
        }
    }

    private void SaveKey()
    {
        string key = _keyBox.Text.Trim();
        if (key.Length < 10)
        {
            MessageBox.Show(this, "That doesn't look like a full API key.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            CredentialStore.SaveApiKey(key);
            _keyBox.Clear();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshStatus() =>
        _status.Text = CredentialStore.HasApiKey ? "✔ A key is stored." : "No key stored yet.";
}
