using DesktopBuddy.Native;

namespace DesktopBuddy.UI;

/// <summary>Lets you store an AI API key now; v1 doesn't use it yet.</summary>
internal sealed class ApiKeyDialog : Form
{
    private readonly TextBox _keyBox;
    private readonly Label _status;

    public ApiKeyDialog()
    {
        Text = "Desktop Buddy: API key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 190);
        Padding = new Padding(16);

        var info = new Label
        {
            Text = "Paste an AI API key (for example an Anthropic key starting with \"sk-ant-\"). " +
                   "It's stored in Windows Credential Manager, not in a file. AI features arrive in v2.",
            Dock = DockStyle.Top,
            Height = 56,
        };
        _keyBox = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, PlaceholderText = "sk-ant-..." };
        _status = new Label { Dock = DockStyle.Top, Height = 34, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 8, 0, 0) };

        var save = new Button { Text = "Save", AutoSize = true };
        var remove = new Button { Text = "Remove key", AutoSize = true };
        var cancel = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Save();
        remove.Click += (_, _) => { CredentialStore.DeleteApiKey(); RefreshStatus(); };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.AddRange([cancel, save, remove]);

        Controls.Add(_status);
        Controls.Add(_keyBox);
        Controls.Add(info);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
        RefreshStatus();
    }

    private void Save()
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
        _status.Text = CredentialStore.HasApiKey ? "✔ A key is stored." : "No key stored.";
}
