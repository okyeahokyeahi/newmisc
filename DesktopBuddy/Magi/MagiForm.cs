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

    private static readonly Color Black = Color.Black;
    private static readonly Color Amber = Color.FromArgb(255, 138, 31);
    private static readonly Color AmberDim = Color.FromArgb(154, 82, 22);
    private static readonly Color Cyan = Color.FromArgb(74, 182, 220);
    private static readonly Color Go = Color.FromArgb(63, 209, 122);
    private static readonly Color Red = Color.FromArgb(227, 38, 46);
    private static readonly Color Ink = Color.FromArgb(243, 220, 196);
    private static readonly Color InkDim = Color.FromArgb(167, 137, 109);
    private static readonly Color Rule = Color.FromArgb(60, 154, 106);
    private static readonly Color Stripe = Color.FromArgb(58, 29, 8);

    private static readonly Core[] Order = [Core.Melchior, Core.Balthasar, Core.Casper];

    private readonly MagiCase _case;
    private readonly SoundBank _sounds;
    private readonly Task<IReadOnlyDictionary<Core, string>?>? _voices;
    private readonly Canvas _canvas;
    private readonly FlowLayoutPanel _buttons;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 40 };
    private readonly DateTime _start = DateTime.UtcNow;

    private Action<int>? _stopDeciding;
    private IReadOnlyDictionary<Core, string>? _voiceLines;
    private DateTime? _revealStart;     // when vote reveals begin (after deliberation / AI voices)
    private readonly HashSet<Core> _revealed = [];
    private bool _resolved;
    private bool _flickerOn;

    public MagiForm(MagiCase magiCase, SoundBank sounds, Task<IReadOnlyDictionary<Core, string>?>? voices)
    {
        _case = magiCase;
        _sounds = sounds;
        _voices = voices;

        Text = "MAGI";
        BackColor = Black;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size((int)DesignW, (int)DesignH + 56);
        try { Icon = UI.BuddyIcon.Create(UI.BuddyMood.Calm); } catch { /* cosmetic */ }

        _canvas = new Canvas(this) { Dock = DockStyle.Fill };
        _buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Black,
            Enabled = false,
        };
        var close = MakeButton("Dismiss", primary: false, () => Close());
        _buttons.Controls.Add(close);
        foreach (MagiAction a in magiCase.Actions.Reverse())
        {
            _buttons.Controls.Add(MakeButton(a.Label, a.Primary, () =>
            {
                _sounds.Play(Cue.Tick);
                Close();
                try { a.Run(); } catch (Exception ex) { Log.Error($"MAGI action {a.Label} failed", ex); }
            }));
        }

        Controls.Add(_canvas);
        Controls.Add(_buttons);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => Begin();
        FormClosed += (_, _) =>
        {
            _clock.Dispose();
            _stopDeciding?.Invoke(150);
        };
    }

    private Button MakeButton(string text, bool primary, Action onClick)
    {
        var b = new Button
        {
            Text = text.ToUpperInvariant(),
            AutoSize = true,
            MinimumSize = new Size(110, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Amber : Black,
            ForeColor = primary ? Black : Amber,
            Font = new Font("Bahnschrift SemiBold", 10.5f),
            Margin = new Padding(6, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Amber;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(255, 160, 74) : Color.FromArgb(40, 255, 138, 31);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Begin()
    {
        if (_case.Security) _sounds.Play(Cue.Alarm);
        _stopDeciding = _sounds.StartDeciding();
        _clock.Tick += (_, _) => Step();
        _clock.Start();
    }

    /// <summary>The deliberation timeline, driven by a 40 ms clock.</summary>
    private void Step()
    {
        TimeSpan t = DateTime.UtcNow - _start;
        _flickerOn = !_flickerOn;

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
                _buttons.Enabled = true;
                _clock.Interval = 250; // only the idle blink from here on
            }
        }
        _canvas.Invalidate();
    }

    // ---------- painting ----------
    private string TypedQuestion()
    {
        int chars = (int)((DateTime.UtcNow - _start).TotalMilliseconds / 22);
        return _case.Question.Length <= chars ? _case.Question : _case.Question[..chars];
    }

    private enum PanelState { Idle, Think, Approve, Deny }

    private PanelState StateOf(Core c) =>
        _revealed.Contains(c) ? (_case.Vote(c).Approve ? PanelState.Approve : PanelState.Deny)
        : _resolved ? PanelState.Idle : PanelState.Think;

    private void PaintScreen(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // striped frame and orange border
        using (var stripes = new HatchBrush(HatchStyle.WideUpwardDiagonal, Stripe, Black)) g.FillRectangle(stripes, 0, 0, DesignW, 460);
        using var amberPen = new Pen(Amber, 1.5f);
        g.FillRectangle(Brushes.Black, 8, 8, 784, 444);
        g.DrawRectangle(amberPen, 8, 8, 784, 444);
        g.DrawLine(amberPen, 8, 400, 792, 400);

        // 質問 / 解決 headers between green double rules
        using var rulePen = new Pen(Rule, 1.5f);
        foreach (var (x1, x2) in new[] { (22f, 278f), (524f, 780f) })
        {
            foreach (float ry in new[] { 36f, 42f, 96f, 102f }) g.DrawLine(rulePen, x1, ry, x2, ry);
        }
        using var kanjiBig = new Font("Yu Gothic UI", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var amber = new SolidBrush(Amber);
        Center(g, "質　問", kanjiBig, amber, 150, 70);
        Center(g, "解　決", kanjiBig, amber, 652, 70);

        // code block
        using var dataBig = new Font("Consolas", 21f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var dataSmall = new Font("Consolas", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString($"CODE:{_case.Code}", dataBig, amber, 30, 118);
        bool busy = !_resolved;
        string[] lines = ["FILE:DESKTOP_BUDDY", $"EXTENTION:{(busy ? "3023" : "0000")}", $"EX_MODE:{(busy ? "ON" : "OFF")}", "PRIORITY:AAA"];
        for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], dataSmall, amber, 62, 147 + i * 13);

        // 情報 box and the verdict under 解決
        using var cyanPen = new Pen(Cyan, 2f);
        using var cyan = new SolidBrush(Cyan);
        g.DrawRectangle(cyanPen, 682, 146, 84, 44);
        using var kanjiMid = new Font("Yu Gothic UI", 22f, FontStyle.Bold, GraphicsUnit.Pixel);
        Center(g, "情報", kanjiMid, cyan, 724, 168);
        string verdict = _resolved ? (_case.Passed ? "承認" : "否決") : (_flickerOn ? "審議中" : "");
        using var verdictBrush = new SolidBrush(_resolved ? (_case.Passed ? Go : Red) : Amber);
        Center(g, verdict, kanjiMid, verdictBrush, 652, 126);

        // links and MAGI
        using var linkPen = new Pen(Amber, 7f);
        g.DrawLine(linkPen, 292, 232, 306, 246);
        g.DrawLine(linkPen, 508, 232, 494, 246);
        g.DrawLine(linkPen, 340, 320, 460, 320);
        using var magiFont = new Font("Yu Mincho", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
        Center(g, "MAGI", magiFont, amber, 400, 272);

        // the three cores
        DrawCore(g, [new(278, 32), new(522, 32), new(522, 210), new(462, 252), new(338, 252), new(278, 210)], 400, 140, "BALTHASAR • 2", StateOf(Core.Balthasar));
        DrawCore(g, [new(35, 210), new(232, 210), new(338, 285), new(338, 385), new(35, 385)], 186, 298, "CASPER • 3", StateOf(Core.Casper));
        DrawCore(g, [new(568, 210), new(768, 210), new(768, 385), new(462, 385), new(462, 285)], 615, 298, "MELCHIOR • 1", StateOf(Core.Melchior));

        // access code + question fields
        g.DrawString("access code:", dataSmall, amber, 18, 409);
        g.DrawString("question:", dataSmall, amber, 18, 428);
        using var thinPen = new Pen(Amber, 1f);
        g.DrawRectangle(thinPen, 98, 408, 682, 14);
        g.DrawRectangle(thinPen, 98, 427, 682, 14);
        g.DrawString(new string('•', 56), dataSmall, amber, 102, 408);
        g.DrawString(TypedQuestion(), dataSmall, amber, 102, 427);

        // proposal + deliberation log + resolution
        using var proposalFont = new Font("Yu Mincho", 24f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Ink);
        using var inkDim = new SolidBrush(InkDim);
        g.DrawString(_case.Proposal, proposalFont, ink, new RectangleF(16, 468, 768, 32));
        using var dataMid = new Font("Consolas", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString(_case.Context, dataMid, inkDim, new RectangleF(16, 502, 768, 18));

        using var labelFont = new Font("Bahnschrift", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var voiceFont = new Font("Yu Mincho", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        float y = 530;
        foreach (Core c in Order)
        {
            bool shown = _revealed.Contains(c);
            CoreVote vote = _case.Vote(c);
            int alpha = shown ? 255 : 70;
            using var who = new SolidBrush(Color.FromArgb(alpha, Amber));
            using var fact = new SolidBrush(Color.FromArgb(alpha, Ink));
            g.DrawString(c switch { Core.Melchior => "MELCHIOR·1", Core.Balthasar => "BALTHASAR·2", _ => "CASPER·3" }, dataMid, who, 16, y + 2);
            float x = 130;
            if (shown)
            {
                Color pill = vote.Approve ? Go : Red;
                using var pillPen = new Pen(pill, 1f);
                using var pillBrush = new SolidBrush(pill);
                string word = vote.Approve ? "APPROVE" : "DENY";
                g.DrawRectangle(pillPen, x, y + 1, 70, 18);
                Center(g, word, dataSmall, pillBrush, x + 35, y + 10);
                x += 80;
            }
            g.DrawString(vote.Fact, labelFont, fact, new RectangleF(x, y, 784 - x, 20));
            if (shown && _voiceLines != null && _voiceLines.TryGetValue(c, out string? line) && !string.IsNullOrWhiteSpace(line))
                g.DrawString($"「{line}」", voiceFont, inkDim, new RectangleF(130, y + 21, 654, 18));
            y += 46;
        }

        // resolution
        using var tallyFont = new Font("Yu Mincho", 24f, FontStyle.Bold, GraphicsUnit.Pixel);
        if (_resolved)
        {
            using var res = new SolidBrush(_case.Passed ? Go : Red);
            g.DrawString($"{_case.Yes} – {3 - _case.Yes}", tallyFont, res, 16, 672);
            g.DrawString(_case.Passed ? _case.IfApproved : _case.IfDenied, labelFont, res, new RectangleF(110, 678, 674, 22));
        }
        else
        {
            g.DrawString("DELIBERATING…", dataMid, amber, 16, 680);
        }
    }

    private void DrawCore(Graphics g, PointF[] points, float cx, float cy, string name, PanelState state)
    {
        Color fill = state switch
        {
            PanelState.Approve => Go,
            PanelState.Deny => Red,
            PanelState.Think when !_flickerOn => Color.FromArgb(52, 140, 172),
            _ => Cyan,
        };
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(Amber, 1.5f);
        g.FillPolygon(brush, points);
        g.DrawPolygon(pen, points);

        using var nameFont = new Font("Arial Black", 23f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var dark = new SolidBrush(Color.FromArgb(5, 8, 10));
        Center(g, name, nameFont, dark, cx, cy);
        string word = state switch { PanelState.Approve => "承認", PanelState.Deny => "否決", _ => "" };
        if (word.Length > 0)
        {
            using var kanji = new Font("Yu Gothic UI", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
            Center(g, word, kanji, dark, cx, cy + 40);
        }
    }

    private static void Center(Graphics g, string text, Font font, Brush brush, float cx, float cy)
    {
        if (text.Length == 0) return;
        SizeF size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, cx - size.Width / 2, cy - size.Height / 2);
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
