namespace DesktopBuddy.UI;

/// <summary>Shared look for Desktop Buddy windows.</summary>
internal static class Ui
{
    public static void Setup(Form form, string title, int width, int height)
    {
        form.Text = title;
        form.Icon = BuddyIcon.Create(BuddyMood.Calm);
        form.Font = new Font("Segoe UI", 9.5f);
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ClientSize = new Size(width, height);
        form.MinimumSize = new Size(Math.Min(width, 420), Math.Min(height, 300));
    }

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(90, 30), Margin = new Padding(4) };
        b.Click += onClick;
        return b;
    }

    public static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        row.Controls.AddRange(buttons);
        return row;
    }

    public static RichTextBox ReadOnlyText() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window,
        DetectUrls = false,
        Margin = new Padding(12),
    };

    public static void AppendHeading(RichTextBox box, string text, Color? color = null)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionFont = new Font(box.Font.FontFamily, 10.5f, FontStyle.Bold);
        box.SelectionColor = color ?? SystemColors.ControlText;
        box.AppendText(text + "\n");
    }

    public static void AppendBody(RichTextBox box, string text, Color? color = null)
    {
        box.SelectionStart = box.TextLength;
        box.SelectionFont = new Font(box.Font.FontFamily, 9.5f, FontStyle.Regular);
        box.SelectionColor = color ?? SystemColors.ControlText;
        box.AppendText(text + "\n\n");
    }

    public static readonly Color Red = Color.FromArgb(200, 30, 30);
    public static readonly Color Amber = Color.FromArgb(190, 110, 0);
    public static readonly Color Green = Color.FromArgb(22, 130, 60);
    public static readonly Color Grey = Color.FromArgb(110, 110, 110);
}
