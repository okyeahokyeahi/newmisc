using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DesktopBuddy.Magi;

/// <summary>
/// The MAGI vote screen: three cores deliberate (with your deciding sound), each turns 承認 (approve)
/// or 否決 (deny), the verdict lands, and you pick what to do. It only recommends; nothing happens
/// until you click.
/// </summary>
internal sealed class MagiForm : Form
{
    // Design size in logical pixels; everything is painted in these coordinates and scaled to the window.
    private const float DesignW = 800, DesignH = 712;
    private const int ButtonRowHeight = 56;

    private static readonly Color Black = Color.Black;
    private static readonly Color Amber = Color.FromArgb(255, 138, 31);
    private static readonly Color Cyan = Color.FromArgb(74, 182, 220);
    private static readonly Color CyanDim = Color.FromArgb(52, 140, 172);
    private static readonly Color Go = Color.FromArgb(63, 209, 122);
    private static readonly Color Red = Color.FromArgb(227, 38, 46);
    private static readonly Color Ink = Color.FromArgb(243, 220, 196);
    private static readonly Color InkDim = Color.FromArgb(167, 137, 109);
    private static readonly Color Rule = Color.FromArgb(60, 154, 106);
    private static readonly Color Stripe = Color.FromArgb(58, 29, 8);
    private static readonly Color PanelText = Color.FromArgb(5, 8, 10);

    private static readonly Core[] Order = [Core.Melchior, Core.Balthasar, Core.Casper];

    private readonly MagiCase _case;
    private readonly SoundBank _sounds;
    private readonly Task<IReadOnlyDictionary<Core, string>?>? _voices;
    private readonly CancellationTokenSource? _voicesCancel;
    private readonly Canvas _canvas;
    private readonly FlowLayoutPanel _buttons;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 40 };
    private readonly Fonts _fonts = new();
    private DateTime _start;

    private Action<int>? _stopDeciding;
    private IReadOnlyDictionary<Core, string>? _voiceLines;
    private DateTime? _revealStart;
    private readonly HashSet<Core> _revealed = [];
    private bool _resolved;
    private int _tick;
    private bool FlickerOn => _tick / 4 % 2 == 0; // ~6 blinks a second, not a strobe

    public MagiForm(MagiCase magiCase, SoundBank sounds, Task<IReadOnlyDictionary<Core, string>?>? voices, CancellationTokenSource? voicesCancel)
    {
        _case = magiCase;
        _sounds = sounds;
        _voices = voices;
        _voicesCancel = voicesCancel;

        Text = "MAGI";
        BackColor = Black;
        ForeColor = Ink;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size((int)DesignW, (int)DesignH + ButtonRowHeight);
        try { Icon = UI.BuddyIcon.Create(UI.BuddyMood.Calm); } catch { /* cosmetic */ }

        _canvas = new Canvas(this) { Dock = DockStyle.Fill };
        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = ButtonRowHeight,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Black,
        };
        _buttons.Controls.Add(MakeButton("Dismiss", primary: false, keepOpen: false, () => { }));
        foreach (MagiAction a in magiCase.Actions.Reverse())
            _buttons.Controls.Add(MakeButton(a.Label, a.Primary, a.KeepOpen, a.Run));
        SetActionsEnabled(false);

        Controls.Add(_canvas);
        Controls.Add(_buttons);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Load += (_, _) => FitOnScreen();
        Shown += (_, _) => Begin();
        FormClosed += (_, _) =>
        {
            _clock.Dispose();
            _stopDeciding?.Invoke(150);
            _voicesCancel?.Cancel();
            _fonts.Dispose();
        };
    }

    /// <summary>Shrinks the window to fit the screen (the drawing scales with it) and centres it.</summary>
    private void FitOnScreen()
    {
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        double ratio = Math.Min(1.0, Math.Min((double)area.Width / Width, (double)area.Height / Height));
        if (ratio < 1.0) Size = new Size((int)(Width * ratio), (int)(Height * ratio));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    private Button MakeButton(string text, bool primary, bool keepOpen, Action run)
    {
        var b = new Button
        {
            Text = text.ToUpperInvariant(),
            AutoSize = true,
            MinimumSize = new Size(110, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Amber : Black,
            ForeColor = primary ? Black : Amber,
            Font = _fonts.Button,
            Margin = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            Tag = text == "Dismiss" ? "dismiss" : null,
        };
        b.FlatAppearance.BorderColor = Amber;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(255, 160, 74) : Color.FromArgb(40, 255, 138, 31);
        b.Click += (_, _) =>
        {
            _sounds.Play(Cue.Tick);
            if (!keepOpen) Close();
            try { run(); } catch (Exception ex) { Log.Error($"MAGI action {text} failed", ex); }
        };
        return b;
    }

    /// <summary>Actions unlock at the verdict; Dismiss (like Esc) works the whole time.</summary>
    private void SetActionsEnabled(bool enabled)
    {
        foreach (Control c in _buttons.Controls)
            c.Enabled = enabled || (string?)c.Tag == "dismiss";
    }

    private void Begin()
    {
        _start = DateTime.UtcNow;
        // Your own deciding sound replaces the alarm; the built-in alarm only plays when there isn't one.
        if (_case.Security && SoundBank.CustomFile(Cue.Deciding) == null) _sounds.Play(Cue.Alarm);
        _stopDeciding = _sounds.StartDeciding();
        _clock.Tick += (_, _) => Step();
        _clock.Start();
    }

    /// <summary>The deliberation timeline, driven by a 40 ms clock.</summary>
    private void Step()
    {
        _tick++;
        TimeSpan t = DateTime.UtcNow - _start;

        // Deliberate at least 1.5 s; if AI voices were asked for, wait for them (up to 8 s).
        if (_revealStart == null)
        {
            bool voicesReady = _voices == null || _voices.IsCompleted;
            if ((t.TotalSeconds >= 1.5 && voicesReady) || t.TotalSeconds >= 8)
            {
                if (_voices is { IsCompletedSuccessfully: true }) _voiceLines = _voices.Result;
                _revealStart = DateTime.UtcNow;
            }
        }
        else if (!_resolved)
        {
            double since = (DateTime.UtcNow - _revealStart.Value).TotalSeconds;
            for (int i = 0; i < Order.Length; i++)
            {
                if (since >= i * 0.65 && _revealed.Add(Order[i]))
                    _sounds.Play(_case.Vote(Order[i]).Approve ? Cue.Approve : Cue.Deny);
            }
            if (since >= Order.Length * 0.65 + 0.2)
            {
                _resolved = true;
                _stopDeciding?.Invoke(600);
                _stopDeciding = null;
                _sounds.Play(Cue.Resolve);
                SetActionsEnabled(true);
                _clock.Stop(); // nothing moves after the verdict
            }
        }
        _canvas.Invalidate();
    }

    // ---------- painting ----------
    private string TypedQuestion()
    {
        if (_start == default) return "";
        int chars = (int)((DateTime.UtcNow - _start).TotalMilliseconds / 22);
        return _case.Question.Length <= chars ? _case.Question : _case.Question[..chars];
    }

    private enum PanelState { Idle, Think, Approve, Deny }

    private PanelState StateOf(Core c) =>
        _revealed.Contains(c) ? (_case.Vote(c).Approve ? PanelState.Approve : PanelState.Deny)
        : _resolved ? PanelState.Idle : PanelState.Think;

    private static readonly StringFormat OneLine = new(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

    private void PaintScreen(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Fonts f = _fonts;

        // striped frame (drawn as lines so it scales with the window) and the orange border
        using (var stripePen = new Pen(Stripe, 4f))
        {
            g.SetClip(new RectangleF(0, 0, DesignW, 460));
            for (float x = -460; x < DesignW; x += 12) g.DrawLine(stripePen, x, 460, x + 460, 0);
            g.ResetClip();
        }
        using var amberPen = new Pen(Amber, 1.5f);
        g.FillRectangle(Brushes.Black, 8, 8, 784, 444);
        g.DrawRectangle(amberPen, 8, 8, 784, 444);
        g.DrawLine(amberPen, 8, 400, 792, 400);

        // 質問 / 解決 headers between green double rules
        using var rulePen = new Pen(Rule, 1.5f);
        foreach (var (x1, x2) in new[] { (22f, 278f), (524f, 780f) })
            foreach (float ry in new[] { 36f, 42f, 96f, 102f }) g.DrawLine(rulePen, x1, ry, x2, ry);
        using var amber = new SolidBrush(Amber);
        Center(g, "質　問", f.Header, amber, 150, 70);
        Center(g, "解　決", f.Header, amber, 652, 70);

        // code block
        g.DrawString($"CODE:{_case.Code}", f.DataBig, amber, 30, 118);
        bool busy = !_resolved;
        string[] lines = ["FILE:DESKTOP_BUDDY", "EXTENTION:3023", $"EX_MODE:{(busy ? "ON" : "OFF")}", "PRIORITY:AAA"];
        for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], f.DataSmall, amber, 62, 147 + i * 13);

        // 情報 box and the verdict under 解決
        using var cyanPen = new Pen(Cyan, 2f);
        using var cyan = new SolidBrush(Cyan);
        g.DrawRectangle(cyanPen, 682, 146, 84, 44);
        Center(g, "情報", f.Info, cyan, 724, 168);
        string verdict = _resolved ? (_case.Passed ? "承認" : "否決") : (FlickerOn ? "審議中" : "");
        using var verdictBrush = new SolidBrush(_resolved ? (_case.Passed ? Go : Red) : Amber);
        Center(g, verdict, f.Verdict, verdictBrush, 652, 126);

        // links and MAGI
        using var linkPen = new Pen(Amber, 7f);
        g.DrawLine(linkPen, 292, 232, 306, 246);
        g.DrawLine(linkPen, 508, 232, 494, 246);
        g.DrawLine(linkPen, 340, 320, 460, 320);
        Center(g, "MAGI", f.Magi, amber, 400, 272);

        // the three cores
        DrawCore(g, [new(278, 32), new(522, 32), new(522, 210), new(462, 252), new(338, 252), new(278, 210)], 400, 140, "BALTHASAR • 2", StateOf(Core.Balthasar));
        DrawCore(g, [new(35, 210), new(232, 210), new(338, 285), new(338, 385), new(35, 385)], 186, 298, "CASPER • 3", StateOf(Core.Casper));
        DrawCore(g, [new(568, 210), new(768, 210), new(768, 385), new(462, 385), new(462, 285)], 615, 298, "MELCHIOR • 1", StateOf(Core.Melchior));

        // access code + question fields
        g.DrawString("access code:", f.DataSmall, amber, 18, 409);
        g.DrawString("question:", f.DataSmall, amber, 18, 428);
        using var thinPen = new Pen(Amber, 1f);
        g.DrawRectangle(thinPen, 98, 408, 682, 14);
        g.DrawRectangle(thinPen, 98, 427, 682, 14);
        g.DrawString(new string('•', 56), f.DataSmall, amber, 102, 408);
        g.DrawString(TypedQuestion(), f.DataSmall, amber, new RectangleF(102, 427, 676, 15), OneLine);

        // proposal + deliberation log + resolution (all single lines, "…" if too long)
        using var ink = new SolidBrush(Ink);
        using var inkDim = new SolidBrush(InkDim);
        g.DrawString(_case.Proposal, f.Proposal, ink, new RectangleF(16, 468, 768, 34), OneLine);
        g.DrawString(_case.Context, f.DataMid, inkDim, new RectangleF(16, 504, 768, 18), OneLine);

        float y = 532;
        foreach (Core c in Order)
        {
            bool shown = _revealed.Contains(c);
            CoreVote vote = _case.Vote(c);
            int alpha = shown ? 255 : 70;
            using var who = new SolidBrush(Color.FromArgb(alpha, Amber));
            using var fact = new SolidBrush(Color.FromArgb(alpha, Ink));
            g.DrawString(c switch { Core.Melchior => "MELCHIOR·1", Core.Balthasar => "BALTHASAR·2", _ => "CASPER·3" }, f.DataMid, who, 16, y + 2);
            if (shown)
            {
                Color pill = vote.Approve ? Go : Red;
                using var pillPen = new Pen(pill, 1f);
                using var pillBrush = new SolidBrush(pill);
                g.DrawRectangle(pillPen, 130, y + 1, 70, 18);
                Center(g, vote.Approve ? "APPROVE" : "DENY", f.DataSmall, pillBrush, 165, y + 10);
            }
            // fact always starts after the pill's space, so it doesn't jump when the pill appears
            g.DrawString(vote.Fact, f.Label, fact, new RectangleF(210, y, 574, 20), OneLine);
            if (shown && _voiceLines != null && _voiceLines.TryGetValue(c, out string? line) && !string.IsNullOrWhiteSpace(line))
                g.DrawString($"「{line}」", f.Voice, inkDim, new RectangleF(210, y + 21, 574, 19), OneLine);
            y += 46;
        }

        if (_resolved)
        {
            using var res = new SolidBrush(_case.Passed ? Go : Red);
            g.DrawString($"{_case.Yes} – {3 - _case.Yes}", f.Tally, res, 16, 672);
            g.DrawString(_case.Passed ? _case.IfApproved : _case.IfDenied, f.Label, res, new RectangleF(110, 680, 674, 22), OneLine);
        }
        else
        {
            g.DrawString("DELIBERATING…", f.DataMid, amber, 16, 680);
        }
    }

    private void DrawCore(Graphics g, PointF[] points, float cx, float cy, string name, PanelState state)
    {
        Color fill = state switch
        {
            PanelState.Approve => Go,
            PanelState.Deny => Red,
            PanelState.Think when !FlickerOn => CyanDim,
            _ => Cyan,
        };
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(Amber, 1.5f);
        g.FillPolygon(brush, points);
        g.DrawPolygon(pen, points);

        using var dark = new SolidBrush(PanelText);
        Center(g, name, _fonts.PanelName, dark, cx, cy);
        if (state == PanelState.Think)
        {
            using var faint = new SolidBrush(Color.FromArgb(FlickerOn ? 150 : 80, PanelText));
            Center(g, "審議中", _fonts.PanelKanji, faint, cx, cy + 42);
        }
        else if (state is PanelState.Approve or PanelState.Deny)
        {
            Center(g, state == PanelState.Approve ? "承認" : "否決", _fonts.PanelKanji, dark, cx, cy + 42);
        }
    }

    private static void Center(Graphics g, string text, Font font, Brush brush, float cx, float cy)
    {
        if (text.Length == 0) return;
        SizeF size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, cx - size.Width / 2, cy - size.Height / 2);
    }

    /// <summary>All fonts, created once. Picks installed families (Yu Mincho isn't on every Windows).</summary>
    private sealed class Fonts : IDisposable
    {
        private static readonly HashSet<string> Installed =
            new(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        private static string Pick(params string[] families) => families.FirstOrDefault(Installed.Contains) ?? "Segoe UI";

        private static readonly string Gothic = Pick("Yu Gothic UI", "Yu Gothic", "Meiryo UI", "Meiryo", "Segoe UI");
        private static readonly string Mincho = Pick("Yu Mincho", "MS Mincho", "BIZ UDMincho", "Yu Gothic UI", "Georgia");
        private static readonly string Data = Pick("Consolas", "Cascadia Mono", "Courier New");
        private static readonly string Narrow = Pick("Bahnschrift", "Segoe UI");
        private static readonly string Heavy = Pick("Arial Black", "Segoe UI Black", "Segoe UI");

        private static Font Px(string family, float px, FontStyle style = FontStyle.Regular) => new(family, px, style, GraphicsUnit.Pixel);

        public readonly Font Header = Px(Gothic, 40, FontStyle.Bold);
        public readonly Font Info = Px(Gothic, 24, FontStyle.Bold);
        public readonly Font Verdict = Px(Gothic, 28, FontStyle.Bold);
        public readonly Font PanelKanji = Px(Gothic, 34, FontStyle.Bold);
        public readonly Font PanelName = Px(Heavy, 25);
        public readonly Font Magi = Px(Mincho, 32, FontStyle.Bold);
        public readonly Font Proposal = Px(Mincho, 24, FontStyle.Bold);
        public readonly Font Tally = Px(Mincho, 24, FontStyle.Bold);
        public readonly Font Voice = Px(Mincho, 13, FontStyle.Bold);
        public readonly Font DataBig = Px(Data, 21);
        public readonly Font DataMid = Px(Data, 12.5f);
        public readonly Font DataSmall = Px(Data, 11.5f);
        public readonly Font Label = Px(Narrow, 14);
        public readonly Font Button = new(Narrow, 10.5f, FontStyle.Bold);

        public void Dispose()
        {
            foreach (Font font in new[] { Header, Info, Verdict, PanelKanji, PanelName, Magi, Proposal, Tally, Voice, DataBig, DataMid, DataSmall, Label, Button })
                font.Dispose();
        }
    }

    /// <summary>Double-buffered surface that paints the design at any window size / DPI.</summary>
    private sealed class Canvas : Control
    {
        private readonly MagiForm _owner;

        public Canvas(MagiForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = Math.Min(ClientSize.Width / DesignW, ClientSize.Height / DesignH);
            e.Graphics.ScaleTransform(scale, scale);
            _owner.PaintScreen(e.Graphics);
        }
    }
}
