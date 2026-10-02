using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using DesktopBuddy.Ai;

namespace DesktopBuddy.UI;

/// <summary>
/// "Ask about something on screen": drag a box around an error or setting, check the preview, type a
/// question, and only then is that picture sent to the AI. Nothing is ever captured automatically.
/// </summary>
internal static class ScreenAsk
{
    private static bool _picking;

    public static void Start(BuddyServices s)
    {
        if (_picking) return; // Ctrl+Alt+S pressed again while the picker is up
        _picking = true;
        try
        {
            StartCore(s);
        }
        finally
        {
            _picking = false;
        }
    }

    private static void StartCore(BuddyServices s)
    {
        if (!s.Ai.HasKey)
        {
            s.ShowApiKey();
            if (!s.Ai.HasKey) return;
        }
        Bitmap? picked = RegionPicker.Pick();
        if (picked != null) new ScreenAskForm(s, picked).Show();
    }
}

/// <summary>Full-screen overlay showing a frozen screenshot; drag to select, Esc to cancel.</summary>
internal sealed class RegionPicker : Form
{
    private readonly Bitmap _screen;
    private Point _start;
    private Rectangle _selection;
    private bool _dragging;

    private RegionPicker(Bitmap screen, Rectangle bounds)
    {
        _screen = screen;
        AutoScaleMode = AutoScaleMode.None; // bounds and screenshot are both in physical pixels
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        DpiChanged += (_, e) => e.Cancel = true; // keep 1:1 pixels with the screenshot on mixed-DPI setups
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        MouseDown += (_, e) => { _dragging = true; _start = e.Location; _selection = Rectangle.Empty; };
        MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            _selection = Rectangle.Intersect(ClientRectangle, // dragging past the edge can give negative coordinates
                Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y), Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y)));
            Invalidate();
        };
        MouseUp += (_, _) =>
        {
            _dragging = false;
            if (_selection.Width < 10 || _selection.Height < 10) return; // a click, not a drag
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImage(_screen, new Rectangle(Point.Empty, _screen.Size)); // exact pixels, ignoring bitmap DPI
        using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
        using var region = new Region(ClientRectangle);
        if (!_selection.IsEmpty) region.Exclude(_selection);
        e.Graphics.FillRegion(dim, region);
        if (!_selection.IsEmpty)
        {
            using var pen = new Pen(Color.FromArgb(22, 163, 74), 2);
            e.Graphics.DrawRectangle(pen, _selection);
        }
        using var font = new Font("Segoe UI", 12f, FontStyle.Bold);
        e.Graphics.DrawString("Drag a box around what you want to ask about · Esc to cancel", font, Brushes.White, 20, 20);
    }

    /// <summary>Captures the screen under the mouse and lets the user pick a region. Null if cancelled.</summary>
    public static Bitmap? Pick()
    {
        Rectangle bounds = Screen.FromPoint(Cursor.Position).Bounds;
        using var screen = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        using var picker = new RegionPicker(screen, bounds);
        if (picker.ShowDialog() != DialogResult.OK) return null;
        return screen.Clone(picker._selection, screen.PixelFormat);
    }
}

/// <summary>Preview + question + answer. The picture is only sent when you press Ask.</summary>
internal sealed class ScreenAskForm : Form
{
    private const int MaxEdge = 1568; // larger images are scaled down by the API anyway

    private readonly BuddyServices _s;
    private readonly Bitmap _image;
    private readonly TextBox _question;
    private readonly RichTextBox _answer = Ui.ReadOnlyText();
    private readonly Button _ask;
    private readonly CancellationTokenSource _closing = new();

    public ScreenAskForm(BuddyServices services, Bitmap image)
    {
        _s = services;
        _image = image;
        Ui.Setup(this, "Ask about this picture", 620, 620);

        var preview = new PictureBox { Dock = DockStyle.Top, Height = 240, SizeMode = PictureBoxSizeMode.Zoom, Image = image, BackColor = SystemColors.ControlDark };
        var note = new Label
        {
            Text = "Only this picture and your question are sent. Check it doesn't show anything private.",
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Ui.Grey,
            Padding = new Padding(12, 6, 12, 0),
        };
        _question = new TextBox { Dock = DockStyle.Top, Height = 54, Multiline = true, Text = "What does this mean and what should I do?" };
        _ask = Ui.Button("Ask", async (_, _) => await Ask());
        var close = Ui.Button("Close", (_, _) => Close());
        CancelButton = close;

        var answerPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 4) };
        answerPanel.Controls.Add(_answer);
        var questionPanel = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(12, 4, 12, 4) };
        questionPanel.Controls.Add(_question);

        Controls.Add(answerPanel);
        Controls.Add(questionPanel);
        Controls.Add(note);
        Controls.Add(preview);
        Controls.Add(Ui.ButtonRow(close, _ask));
        FormClosed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
            _image.Dispose();
        };
    }

    private async Task Ask()
    {
        string question = _question.Text.Trim();
        if (question.Length == 0) return;
        _ask.Enabled = false;
        _ask.Text = "Asking…";
        try
        {
            var (bytes, isPng) = Encode(_image);
            string answer = await _s.Ai.AskWithImage(Prompts.System, bytes, isPng, question, _closing.Token);
            if (IsDisposed) return;
            Ui.AppendHeading(_answer, "Buddy", Ui.Green);
            Ui.AppendBody(_answer, answer);
        }
        catch (AiUnavailableException ex)
        {
            if (!IsDisposed) Ui.AppendBody(_answer, ex.Message, Ui.Amber);
        }
        catch (OperationCanceledException)
        {
            // closed mid-question
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot question failed", ex);
            if (!IsDisposed) Ui.AppendBody(_answer, "Something went wrong. Try again (details are in the log).", Ui.Amber);
        }
        finally
        {
            if (!IsDisposed)
            {
                _ask.Enabled = true;
                _ask.Text = "Ask again";
            }
        }
    }

    /// <summary>
    /// PNG (sharp text), scaled so the longest edge is at most 1568 px. Busy game scenes can make a huge PNG,
    /// so those fall back to JPEG to stay well under the API's per-image size limit.
    /// </summary>
    private static (byte[] Bytes, bool IsPng) Encode(Bitmap image)
    {
        double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(image.Width, image.Height));
        using var resized = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        using (Graphics g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, resized.Width, resized.Height);
        }
        using var png = new MemoryStream();
        resized.Save(png, ImageFormat.Png);
        if (png.Length <= 3_500_000) return (png.ToArray(), true);

        using var jpeg = new MemoryStream();
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        resized.Save(jpeg, codec, quality);
        return (jpeg.ToArray(), false);
    }
}
