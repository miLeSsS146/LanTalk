// LanTalk's look: warm paper colours, one clay accent, serif headings, and the few self-drawn controls that need it.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LanTalk {

public static class Theme {
    static Color C(int rgb) { return Color.FromArgb(255, Color.FromArgb(rgb)); }
    public static readonly Color Back = C(0xFAF9F5), Side = C(0xF0EEE6), SideSel = C(0xE2DED1), Card = Color.White, Line = C(0xE0DCD0), LineFocus = C(0xBDB8A8);
    public static readonly Color Text = C(0x1F1E1D), Soft = C(0x3D3D3A), Dim = C(0x6B6A64);
    public static readonly Color Clay = C(0xD97757), Accent = C(0xC6613F), AccentDown = C(0xAD5233), AccentText = C(0xA04426);
    public static readonly Color Mine = C(0xECE8DC), Theirs = Color.White, AgentFill = C(0xFAEEE2), AgentLine = C(0xECD3BC);
    public static readonly Color Sand = C(0xD8D2C0), Olive = C(0x6F8753), Online = C(0x55703C), Bad = C(0xB53333);
    public static float Scale = 1f;
    public static int S(int v) { return (int)Math.Round(v * Scale); }
    public static readonly Font Body = new Font("Microsoft YaHei UI", 10f), Small = new Font("Microsoft YaHei UI", 8.5f), Bold = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
    public static readonly Font Brand = Serif(15f), Heading = Serif(14f), Greeting = Serif(19f);
    public static readonly Font Glyph = Pick(11f, "Segoe Fluent Icons", "Segoe MDL2 Assets"), GlyphBig = Pick(15f, "Segoe Fluent Icons", "Segoe MDL2 Assets");
    public static readonly Font Mono = Pick(9.5f, "Cascadia Mono", "Consolas");
    static Font Serif(float pt) { return Pick(pt, "Source Han Serif CN", "Noto Serif SC", "Microsoft YaHei UI"); }
    static Font Pick(float pt, params string[] names) {
        foreach (string n in names) { Font f = new Font(n, pt); if (f.OriginalFontName == f.Name) return f; f.Dispose(); } // a missing family silently becomes another one
        return new Font(SystemFonts.MessageBoxFont.FontFamily, pt);
    }

    static Graphics screen;
    // MeasureText ignores NoPadding unless it gets a device context.
    public static Size Measure(string s, Font f, Size room, TextFormatFlags flags) {
        if (screen == null) screen = Graphics.FromHwnd(IntPtr.Zero);
        return TextRenderer.MeasureText(screen, s, f, room, flags);
    }

    // Height of wrapped text; translations differ a lot in length.
    public static int TextHeight(string s, Font f, int width) { return Measure(s, f, new Size(width, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height; }

    // Embedded by build.cmd (/resource:LanTalk.ico); drawn by icon\make-icon.py.
    public static Icon AppIcon() { return AppIcon(SystemInformation.IconSize); }
    public static Icon AppIcon(Size size) {
        using (Stream s = typeof(Theme).Assembly.GetManifestResourceStream("LanTalk.ico"))
            return s == null ? SystemIcons.Application : new Icon(s, size);
    }
    static Bitmap mark;
    public static void DrawMark(Graphics g, Rectangle r) {
        if (mark == null || mark.Width < r.Width) using (Icon i = AppIcon(new Size(r.Width, r.Width))) mark = i.ToBitmap();
        InterpolationMode old = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(mark, r); g.InterpolationMode = old;
    }

    public static GraphicsPath Round(Rectangle r, int d) {
        d = Math.Max(1, Math.Min(d, Math.Min(r.Width, r.Height)));
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
    public static void Fill(Graphics g, Rectangle r, int d, Color fill, Color line) {
        using (GraphicsPath p = Round(r, d)) {
            if (fill != Color.Empty) using (Brush b = new SolidBrush(fill)) g.FillPath(b, p);
            if (line != Color.Empty) using (Pen n = new Pen(line)) g.DrawPath(n, p);
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    // Paper-coloured title bar. Windows 10 doesn't know these attributes and keeps its own colours.
    public static void Chrome(Form f, Color back) {
        int b = ColorTranslator.ToWin32(back), t = ColorTranslator.ToWin32(Soft);
        try { DwmSetWindowAttribute(f.Handle, 35, ref b, 4); DwmSetWindowAttribute(f.Handle, 36, ref t, 4); }
        catch (DllNotFoundException) { }
    }
    public static void InitMenus() { ToolStripManager.Renderer = new MenuLook(); }
    class MenuLook : ToolStripProfessionalRenderer {
        public MenuLook() : base(new MenuColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Theme.Text; base.OnRenderItemText(e); }
    }
    class MenuColors : ProfessionalColorTable {
        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return SideSel; } }
        public override Color MenuItemSelected { get { return SideSel; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
    }
    public static ContextMenuStrip Menu() { ContextMenuStrip m = new ContextMenuStrip(); m.Font = Body; m.ShowImageMargin = false; return m; }
}

// Rounded button: text, an icon glyph, or both.
public class RButton : Button {
    public Color Fill = Color.Empty, Hover = Theme.SideSel, Border = Color.Empty;
    public string Icon = ""; // Segoe icon glyph
    public bool LeftAlign;
    bool hot, down;
    public RButton(string text, string icon) {
        Text = text; Icon = icon; ForeColor = Theme.Soft; Font = Theme.Body; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    public static RButton Primary(string text) { RButton b = new RButton(text, ""); b.Fill = Theme.Accent; b.Hover = Theme.AccentDown; b.ForeColor = Color.White; b.Font = Theme.Bold; return b; }
    public static RButton Outline(string text) { RButton b = new RButton(text, ""); b.Fill = Theme.Card; b.Hover = Theme.Side; b.Border = Theme.Line; return b; }
    static int Measure(string s, Font f) { return Theme.Measure(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width; }
    int IconWidth { get { return Icon == "" ? 0 : Measure(Icon, Theme.Glyph); } }
    int TextWidth { get { return Text == "" ? 0 : Measure(Text, Font); } }
    int Gap { get { return Icon != "" && Text != "" ? Theme.S(8) : 0; } }
    public int FitWidth { get { return IconWidth + Gap + TextWidth + Theme.S(20); } }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.Clear(Parent == null ? Theme.Back : Parent.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.Fill(g, r, Theme.S(16), hot || down ? Hover : Fill, Border);
        if (Focused && ShowFocusCues) Theme.Fill(g, Rectangle.Inflate(r, -2, -2), Theme.S(13), Color.Empty, Fill == Theme.Accent ? Color.White : Theme.Accent);
        int iw = IconWidth, tw = TextWidth, x = LeftAlign ? Theme.S(18) : (Width - iw - Gap - tw) / 2;
        TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (iw > 0) TextRenderer.DrawText(g, Icon, Theme.Glyph, new Rectangle(x, Theme.S(1), iw, Height), ForeColor, f);
        if (tw > 0) TextRenderer.DrawText(g, Text, Font, new Rectangle(x + iw + Gap, 0, tw + 2, Height), ForeColor, f);
    }
}

// White rounded box around an input.
public class CardPanel : Panel {
    public int Corner = 12;
    bool active;
    public CardPanel() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Card; }
    protected override void OnEnter(EventArgs e) { active = true; Invalidate(); base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { active = false; Invalidate(); base.OnLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Parent == null ? Theme.Back : Parent.BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Fill(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(2 * Corner), BackColor, active ? Theme.LineFocus : Theme.Line);
    }
}

// Text box with a hint while empty. No native scroll bar: the owner grows it, the wheel scrolls past that.
public class HintBox : TextBox {
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    string hint = "";
    bool composing; // an input method is showing its own text in the box
    public string Hint { get { return hint; } set { if (hint != value) { hint = value; Invalidate(); } } }
    public void ScrollLines(int n) { if (IsHandleCreated) SendMessage(Handle, 0xB6, IntPtr.Zero, (IntPtr)n); } // EM_LINESCROLL
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (TextLength <= 1) Invalidate(); }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x20A && Multiline) { ScrollLines(((m.WParam.ToInt64() >> 16) & 0xFFFF) < 0x8000 ? -3 : 3); return; } // WM_MOUSEWHEEL
        base.WndProc(ref m);
        if (m.Msg == 0x10D || m.Msg == 0x10E) { composing = m.Msg == 0x10D; Invalidate(); } // WM_IME_STARTCOMPOSITION, WM_IME_ENDCOMPOSITION
        if ((m.Msg == 0xF || m.Msg == 0x318) && TextLength == 0 && !composing && hint != "") // WM_PAINT, WM_PRINTCLIENT
            using (Graphics g = m.Msg == 0xF ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
                TextRenderer.DrawText(g, hint, Font, new Rectangle(1, 0, Width - 2, Height), Theme.Dim, BackColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

// Thin scroll bar with the members ChatView needs from VScrollBar.
public class SlimBar : Control {
    int value, min, max = 100, large = 10;
    int grab = -1;
    bool hot;
    public int SmallChange = 1;
    public event EventHandler ValueChanged;
    public SlimBar() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); SetStyle(ControlStyles.Selectable, false);
        Width = Theme.S(12); BackColor = Theme.Back; Cursor = Cursors.Default;
    }
    public int Minimum { get { return min; } set { min = value; Invalidate(); } }
    public int Maximum { get { return max; } set { max = value; Invalidate(); } }
    public int LargeChange { get { return large; } set { large = value; Invalidate(); } }
    int Last { get { return Math.Max(min, max - large + 1); } } // highest reachable value, as in VScrollBar
    public int Value {
        get { return value; }
        set { int v = Math.Max(min, Math.Min(Last, value)); if (v == this.value) return; this.value = v; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
    }
    Rectangle Thumb {
        get {
            int pad = Theme.S(3), track = Math.Max(1, Height - 2 * pad), w = Theme.S(6);
            int h = Math.Min(track, Math.Max(Theme.S(32), (int)((long)track * large / Math.Max(1, max - min + 1))));
            int y = Last <= min ? 0 : (int)((long)(track - h) * (value - min) / (Last - min));
            return new Rectangle((Width - w) / 2, pad + y, w, h);
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Fill(e.Graphics, Thumb, Theme.S(6), hot || grab >= 0 ? Theme.LineFocus : Theme.Sand, Color.Empty);
    }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        Rectangle t = Thumb;
        if (e.Y >= t.Top && e.Y < t.Bottom) grab = e.Y - t.Top; else Value += e.Y < t.Top ? -large : large;
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        if (grab < 0) return;
        int pad = Theme.S(3), room = Height - 2 * pad - Thumb.Height;
        if (room > 0) Value = min + (int)((long)(Last - min) * Math.Max(0, Math.Min(room, e.Y - grab - pad)) / room);
    }
    protected override void OnMouseUp(MouseEventArgs e) { grab = -1; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) grab = -1; base.OnMouseCaptureChanged(e); }
}

// Conversation title in serif with a quiet second line.
public class HeaderBar : Control {
    string sub = "";
    public string Sub { get { return sub; } set { if (sub != value) { sub = value; Invalidate(); } } }
    public int RightSpace;
    public HeaderBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); SetStyle(ControlStyles.Selectable, false); BackColor = Theme.Back; }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) {
        TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        int x = Theme.S(22), w = Width - x - RightSpace - Theme.S(16);
        TextRenderer.DrawText(e.Graphics, Text, Theme.Heading, new Rectangle(x, Theme.S(9), w, Theme.S(30)), Theme.Text, f);
        TextRenderer.DrawText(e.Graphics, sub, Theme.Small, new Rectangle(x, Theme.S(39), w, Theme.S(20)), Theme.Dim, f);
        using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }
}

// App name at the top of the side bar.
public class BrandBar : Control {
    public BrandBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); SetStyle(ControlStyles.Selectable, false); BackColor = Theme.Side; }
    protected override void OnPaint(PaintEventArgs e) {
        int d = Theme.S(32);
        Theme.DrawMark(e.Graphics, new Rectangle(Theme.S(20), (Height - d) / 2, d, d));
        TextRenderer.DrawText(e.Graphics, "LanTalk", Theme.Brand, new Rectangle(Theme.S(20) + d + Theme.S(10), 0, Width, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}

public class RCheck : CheckBox {
    public RCheck() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics; g.Clear(Parent == null ? Theme.Back : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Theme.S(18);
        Rectangle r = new Rectangle(2, (Height - d) / 2, d, d);
        Theme.Fill(g, r, Theme.S(10), Checked ? Theme.Accent : Theme.Card, Checked ? Theme.Accent : Theme.LineFocus);
        if (Checked) using (Pen p = new Pen(Color.White, Theme.S(2))) {
            p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            g.DrawLines(p, new PointF[] { new PointF(r.X + d * .26f, r.Y + d * .53f), new PointF(r.X + d * .43f, r.Y + d * .70f), new PointF(r.X + d * .75f, r.Y + d * .32f) });
        }
        int x = r.Right + Theme.S(8);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) Theme.Fill(g, Rectangle.Inflate(r, 2, 2), Theme.S(13), Color.Empty, Theme.Accent);
    }
}
}
