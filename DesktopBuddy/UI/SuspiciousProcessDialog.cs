using System.Diagnostics;
using DesktopBuddy.Monitors;

namespace DesktopBuddy.UI;

public enum SuspiciousChoice { Ignore, Kill, AlwaysAllow, Freeze }

/// <summary>"Something looks off about this program": you choose Kill / Freeze / Ignore / Always allow.</summary>
internal sealed class SuspiciousProcessDialog : Form
{
    public SuspiciousChoice Choice { get; private set; } = SuspiciousChoice.Ignore;

    public SuspiciousProcessDialog(SuspiciousProcess item, Action<SuspiciousProcess>? explain = null)
    {
        Text = "Desktop Buddy: suspicious program";
        Icon = SystemIcons.Warning;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleDimensions = new SizeF(96F, 96F); // designed at 100%; WinForms scales up for 125%+
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 330);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = $"{item.Name}  (PID {item.Pid})",
            AutoSize = true,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
        });
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new TextBox
        {
            Text = item.ExePath,
            TabStop = false,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 6, 3, 10),
        });
        layout.Controls.Add(new Label { Text = "Why it was flagged:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        layout.Controls.Add(new Label
        {
            Text = string.Join("\n", item.Reasons.Select(r => "• " + r)) +
                   "\n\nNot sure? \"Freeze\" pauses it without closing it, so it can't do anything while you check " +
                   "(\"What is this?\" or search the name online). Resume or end it later from the tray menu > Frozen programs. " +
                   "\"Ignore\" hides it until Desktop Buddy restarts.",
            Dock = DockStyle.Fill,
        });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        Button ignore = MakeButton("Ignore", SuspiciousChoice.Ignore);
        CancelButton = ignore; // Esc = Ignore
        buttons.Controls.Add(ignore);
        buttons.Controls.Add(MakeButton("Always allow", SuspiciousChoice.AlwaysAllow));
        buttons.Controls.Add(MakeButton("Kill it", SuspiciousChoice.Kill));
        buttons.Controls.Add(MakeButton("Freeze", SuspiciousChoice.Freeze));
        var show = new Button { Text = "Show file", AutoSize = true };
        show.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{item.ExePath}\"");
        buttons.Controls.Add(show);
        if (explain != null)
        {
            var whatIsIt = new Button { Text = "What is this?", AutoSize = true };
            whatIsIt.Click += (_, _) => explain(item);
            buttons.Controls.Add(whatIsIt);
        }
        layout.Controls.Add(buttons);

        Controls.Add(layout);
    }

    private Button MakeButton(string text, SuspiciousChoice choice)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(90, 0) };
        button.Click += (_, _) =>
        {
            Choice = choice;
            Close();
        };
        return button;
    }
}
