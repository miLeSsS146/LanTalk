// Windows for remote control: who may do what on this PC, "another PC wants to do this, OK?", and what shows while it happens.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LanTalk {

// Names and icons of the five things another PC may do here.
public static class CapLook {
    public static string Name(string cap) { return cap == "screen" ? Lang.T("看屏幕", "See the screen") : cap == "read" ? Lang.T("读文件", "Read files") : cap == "write" ? Lang.T("写文件", "Write files") : cap == "run" ? Lang.T("运行命令", "Run commands") : cap == "mouse" ? Lang.T("鼠标键盘", "Mouse and keyboard") : ""; }
    public static string Sub(string cap) {
        return cap == "screen" ? Lang.T("截一张图，不能点、不能打字", "One picture. No clicking, no typing")
            : cap == "read" ? Lang.T("查看、取回你能打开的文件", "Look at and fetch files you can open")
            : cap == "write" ? Lang.T("新建、修改、同步文件", "Create, edit and sync files")
            : cap == "mouse" ? Lang.T("点击、打字，没锁屏就能用", "Click and type, unless locked")
            : Lang.T("和你一样的权限，没有管理员权限", "With your rights, not as administrator");
    }
    static readonly Dictionary<string, string> icons = new Dictionary<string, string> { // lines on a 20 x 20 grid
        { "screen", "M3 4 H17 V13 H3 Z M7 17 H13 M10 13 V17" }, { "read", "M5 2.5 H12 L15.5 6 V17.5 H5 Z M12 2.5 V6 H15.5 M8 10 H12.5 M8 13.5 H12.5" },
        { "write", "M4 16 L4.6 12.6 L13.4 3.8 L16.2 6.6 L7.4 15.4 Z M11.6 5.6 L14.4 8.4" }, { "run", "M3 4 H17 V16 H3 Z M6 8 L8.5 10 L6 12 M10 12.5 H13.5" },
        { "mouse", "M5 3 L5 15.4 L8.3 12.4 L10.6 17.2 L12.8 16.2 L10.5 11.5 L15 11.2 Z" }, { "lock", "M4.5 9 H15.5 V16.5 H4.5 Z M7 9 V6 L8.2 4.2 H11.8 L13 6 V9" } };
    public static void Icon(Graphics g, string cap, Rectangle r, Color c) {
        float k = r.Width / 20f;
        using (GraphicsPath p = new GraphicsPath())
        using (Pen pen = new Pen(c, 1.5f * k)) {
            pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
            string[] t = icons[cap].Split(' ');
            PointF at = PointF.Empty;
            for (int i = 0; i < t.Length; i++) {
                char op = t[i][0];
                if (op == 'Z') { p.CloseFigure(); continue; }
                float v = float.Parse(t[i].Substring(1), CultureInfo.InvariantCulture);
                PointF to = op == 'H' ? new PointF(v, at.Y) : op == 'V' ? new PointF(at.X, v) : new PointF(v, float.Parse(t[++i], CultureInfo.InvariantCulture));
                if (op == 'M') p.StartFigure(); else p.AddLine(r.X + at.X * k, r.Y + at.Y * k, r.X + to.X * k, r.Y + to.Y * k);
                at = to;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawPath(pen, p);
        }
    }
    // For the list: what that PC may do here.
    public static string Of(Grant g) {
        string s = g.Paused ? Lang.T("已停止", "stopped")
            : g.Run == "allow" ? Lang.T("可控制本机", "has access")
            : Grant.Caps.All(delegate (string c) { return g.Cap(c) == "ask" || c == "mouse" && g.Mouse != "allow"; }) ? Lang.T("控制需确认", "asks first")
            : g.Write == "" && g.Run == "" && g.Cap("mouse") == "" ? Lang.T("只能看", "view only") : Lang.T("部分权限", "some access");
        if (g.Until == 0) return s;
        double min = Math.Max(1, (g.Until - Util.Now()) / 60000.0);
        return s + " · " + (min < 60 ? Lang.T("剩 {0} 分钟", "{0} min left", (int)min) : min < 48 * 60 ? Lang.T("剩 {0} 小时", "{0} h left", (int)(min / 60)) : Lang.T("剩 {0} 天", "{0} days left", (int)(min / 1440)));
    }
    // What an entry in the chat was about.
    public static string Doing(string op) {
        switch (op) {
            case "bg": return Lang.T("后台任务", "background job");
            case "claude": return Lang.T("派活给 Claude", "work for Claude");
            case "shell": return Lang.T("会话命令", "command in a session");
            case "tunnel": return Lang.T("打开端口", "forwarded port");
            case "update": return Lang.T("更新 LanTalk", "update LanTalk");
            case "shot": return Lang.T("看屏幕", "screenshot");
            case "windows": return Lang.T("看打开的窗口", "list of windows");
            case "get": case "cat": return Lang.T("读文件", "read a file");
            case "put": return Lang.T("写文件", "write a file");
            case "edit": return Lang.T("改文件", "edit a file");
            case "push": return Lang.T("同步文件夹", "sync a folder");
            case "pull": return Lang.T("读文件夹", "read a folder");
            case "input": return Lang.T("鼠标键盘", "mouse and keyboard");
            case "info": return Lang.T("看电脑信息", "facts about the PC");
        }
        return "";
    }
}

// A choice among a few next to each other.
public class Chip : RButton {
    bool on;
    public Chip(string text) : base(text, "") { Set(false); }
    public void Set(bool v) { on = v; Fill = on ? Theme.AgentFill : Theme.Card; Hover = on ? Theme.AgentFill : Theme.Side; Border = on ? Theme.Accent : Theme.Line; ForeColor = Enabled ? on ? Theme.AccentText : Theme.Soft : Theme.Dim; AccessibleDescription = on ? Lang.T("已选", "selected") : ""; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Set(on); }
}

// One capability: icon, name, and no / ask / allow. Click a part or use the arrow keys.
public class CapRow : Control {
    public readonly string Cap;
    public bool First;
    int value, hot = -1;
    string included = "";
    public event Action Changed;
    public CapRow(string cap) {
        Cap = cap; BackColor = Theme.Card; TabStop = true; AccessibleRole = AccessibleRole.Slider;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Named();
    }
    static string Text3(int v) { return v == 0 ? Lang.T("不允许", "No") : v == 1 ? Lang.T("询问", "Ask") : Lang.T("允许", "Allow"); }
    void Named() { AccessibleName = CapLook.Name(Cap) + " " + Text3(value); }
    public int Value { get { return value; } set { this.value = value; Named(); Invalidate(); } }
    public string Included { set { included = value; Invalidate(); } } // the capability this one comes with, "" when it is on its own
    void Pick(int v) { if (!Enabled || v < 0 || v > 2 || v == value) return; Value = v; if (Changed != null) Changed(); }
    Rectangle Track { get { int w = Theme.S(186), h = Theme.S(36); return new Rectangle(Width - w - Theme.S(4), (Height - h) / 2, w, h); } }
    Rectangle Part(int i) { Rectangle t = Track; int p = Theme.S(3), w = (t.Width - 2 * p) / 3; return new Rectangle(t.X + p + i * w, t.Y + p, w, t.Height - 2 * p); }
    int Hit(Point p) { for (int i = 0; i < 3; i++) if (Part(i).Contains(p)) return i; return -1; }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (e.Button == MouseButtons.Left) Pick(Hit(e.Location)); }
    protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.Location); if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Left) Pick(value - 1); else if (e.KeyCode == Keys.Right) Pick(value + 1); }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!First) using (Pen p = new Pen(Theme.Side)) g.DrawLine(p, 0, 0, Width, 0);
        int d = Theme.S(20), x = Theme.S(6);
        CapLook.Icon(g, Cap, new Rectangle(x, (Height - d) / 2, d, d), value > 0 && Enabled ? Theme.AccentText : Theme.Dim);
        x += d + Theme.S(12);
        Rectangle t = Track;
        TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, CapLook.Name(Cap), Theme.Bold, new Rectangle(x, Theme.S(10), t.X - x - Theme.S(8), Theme.S(22)), Theme.Text, f);
        TextRenderer.DrawText(g, included != "" ? Lang.T("已包含在“{0}”里", "Comes with \"{0}\"", CapLook.Name(included)) : CapLook.Sub(Cap), Theme.Small, new Rectangle(x, Theme.S(32), t.X - x - Theme.S(8), Theme.S(19)), Theme.Dim, f);
        Theme.Fill(g, new Rectangle(t.X, t.Y, t.Width - 1, t.Height - 1), Theme.S(18), Theme.Side, Focused && ShowFocusCues ? Theme.Accent : Color.Empty);
        Func<Color, Color> fade = delegate (Color c) { return included == "" && Enabled ? c : Color.FromArgb((c.R + Theme.Side.R) / 2, (c.G + Theme.Side.G) / 2, (c.B + Theme.Side.B) / 2); };
        for (int i = 0; i < 3; i++) {
            Rectangle r = Part(i);
            bool sel = i == value;
            if (sel) Theme.Fill(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(14), fade(i == 2 ? Theme.AccentDown : i == 1 ? Theme.AskFill : Theme.Card), i == 2 ? Color.Empty : fade(i == 1 ? Theme.AskLine : Theme.Sand));
            else if (i == hot && Enabled) Theme.Fill(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(14), Theme.SideSel, Color.Empty);
            TextRenderer.DrawText(g, Text3(i), Theme.Small, r, !Enabled ? Theme.Dim : sel ? i == 2 ? Color.White : i == 1 ? Theme.AccentText : Theme.Text : Theme.Dim, f | TextFormatFlags.HorizontalCenter);
        }
    }
}

public class ControlForm : Form {
    static readonly int[][] Presets = { new int[] { 0, 0, 0, 0, 0 }, new int[] { 2, 2, 0, 0, 0 }, new int[] { 1, 1, 1, 1, 1 }, new int[] { 2, 2, 2, 2, 2 } };
    readonly Node node;
    readonly Peer peer;
    readonly string key;   // what the other PC presents now; null when it can't be asked
    readonly Grant old;
    readonly CapRow[] rows = Grant.Caps.Select(delegate (string c) { return new CapRow(c); }).ToArray();
    readonly Chip[] presets, terms;
    readonly Label note = new Label();
    int term; // index into terms, -1 = leave the present end as it is

    public ControlForm(Node n, Peer p, string currentKey, string problem) {
        node = n; peer = p; key = currentKey; old = n.GrantFor(p.Id);
        bool changed = old != null && key != null && old.Key != key;
        Text = Lang.T("LanTalk 控制权限", "LanTalk control access"); Font = Theme.Body; BackColor = Theme.Back; ForeColor = Theme.Text; Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        int y = Theme.S(12);
        Add(Line(Lang.T("允许 {0} 控制这台电脑？", "Let {0} control this PC?", p.Name), Theme.Greeting, Theme.Text, 17, ref y, 46));
        Add(Line(Lang.T("指 {0} 上的 Claude（或人）。能做什么，由你一项一项定。", "That is Claude (or a person) on {0}. You decide what it may do, one by one.", p.Name), Theme.Small, Theme.Dim, 21, ref y, 24));
        y += Theme.S(10);
        presets = Chips(new string[] { Lang.T("不允许", "Nothing"), Lang.T("只看", "View only"), Lang.T("每次询问", "Ask every time"), Lang.T("全部允许", "Everything") }, 24, 490, y, 38);
        for (int i = 0; i < 4; i++) { int k = i; presets[i].Click += delegate { for (int c = 0; c < rows.Length; c++) rows[c].Value = Presets[k][c]; Refreshed(); }; presets[i].Enabled = i == 0 || key != null; }
        y += Theme.S(38 + 12);
        CardPanel card = new CardPanel(); card.SetBounds(Theme.S(24), y, Theme.S(490), Theme.S(rows.Length * 62 + 8));
        for (int i = 0; i < rows.Length; i++) {
            CapRow r = rows[i];
            r.First = i == 0; r.SetBounds(Theme.S(8), Theme.S(4 + i * 62), Theme.S(474), Theme.S(62)); r.Enabled = key != null;
            string had = old == null || changed || key == null ? "" : old.Cap(r.Cap);
            r.Value = had == "allow" ? 2 : had == "ask" ? 1 : 0;
            r.Changed += delegate {
                if (r.Cap == "run") { if (r.Value == 2) foreach (CapRow x in rows.Take(3)) x.Value = 2; }
                else if (r.Cap == "mouse") { if (rows[0].Value < r.Value) rows[0].Value = r.Value; } // it has to see where it clicks
                else if (rows[3].Value == 2 && r.Value != 2) rows[3].Value = 1; // not everything any more
                if (r.Cap == "screen" && rows[4].Value > r.Value) rows[4].Value = r.Value;
                Refreshed();
            };
            card.Controls.Add(r);
        }
        Controls.Add(card);
        y += card.Height + Theme.S(8);
        note.Font = Theme.Small; note.UseMnemonic = false; note.SetBounds(Theme.S(21), y, Theme.S(496), Theme.S(42)); Controls.Add(note);
        y += Theme.S(44);
        Label tl = new Label(); tl.Text = Lang.T("有效期", "For"); tl.ForeColor = Theme.Soft; tl.TextAlign = ContentAlignment.MiddleLeft;
        int tw = Theme.TextHeight("x", Theme.Body, 99) > 0 ? TextRenderer.MeasureText(tl.Text, Theme.Body).Width + Theme.S(8) : 0;
        tl.SetBounds(Theme.S(21), y, tw, Theme.S(34)); Controls.Add(tl);
        int left = (int)((Theme.S(21) + tw) / Theme.Scale);
        terms = Chips(new string[] { Lang.T("一直有效", "Always"), Lang.T("1 小时", "1 hour"), Lang.T("到今晚 0 点", "Until midnight"), Lang.T("7 天", "7 days") }, left, 514 - left, y, 34);
        for (int i = 0; i < 4; i++) { int k = i; terms[i].Font = Theme.Small; terms[i].Click += delegate { term = k; Refreshed(); }; terms[i].Enabled = key != null; }
        term = old != null && old.Until > 0 && !changed ? -1 : 0;
        y += Theme.S(34 + 14);
        Panel rule = new Panel(); rule.BackColor = Theme.Line; rule.SetBounds(Theme.S(24), y, Theme.S(490), 1); Controls.Add(rule);
        y += Theme.S(8);
        string check = key == null ? Lang.T("对方现在联系不上，只能取消授权：", "The other PC can't be reached now, so access can only be removed: ") + problem
            : changed ? Lang.T("注意：{0} 的身份变了（重装过，或者有人在冒充）。原来的授权已经不能用，要继续请核对校验码后重新选择。", "Careful: the identity of {0} has changed (reinstalled, or someone is posing as it). The old access no longer works. To go on, compare the check code and choose again.", p.Name)
            : Lang.T("请核对：{0} 的 LanTalk“设置”里显示的本机校验码应当是", "Compare: Settings in LanTalk on {0} should show this check code", p.Name);
        Add(Line(check, Theme.Small, key == null || changed ? Theme.Bad : Theme.Dim, 21, ref y, 24));
        if (key != null) Add(Line(Node.Fingerprint(key), Theme.Mono, Theme.Text, 21, ref y, 26));
        y += Theme.S(10);
        RButton ok = RButton.Primary(Lang.T("确定", "OK")), cancel = RButton.Outline(Lang.T("取消", "Cancel"));
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(Theme.S(314), y, Theme.S(96), Theme.S(38)); ok.SetBounds(Theme.S(418), y, Theme.S(96), Theme.S(38));
        ok.Click += delegate { Save(); };
        Controls.Add(cancel); Controls.Add(ok); AcceptButton = ok; CancelButton = cancel;
        ClientSize = new Size(Theme.S(538), ok.Bottom + Theme.S(22));
        Refreshed();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this, Theme.Back); }
    void Add(Control c) { Controls.Add(c); }
    Chip[] Chips(string[] texts, int x, int width, int y, int h) {
        int gap = Theme.S(8), w = (Theme.S(width) - gap * (texts.Length - 1)) / texts.Length;
        Chip[] l = new Chip[texts.Length];
        for (int i = 0; i < l.Length; i++) { l[i] = new Chip(texts[i]); l[i].SetBounds(Theme.S(x) + i * (w + gap), y, w, Theme.S(h)); Controls.Add(l[i]); }
        return l;
    }
    static Label Line(string text, Font f, Color c, int x, ref int y, int h) {
        Label l = new Label(); l.Text = text; l.Font = f; l.ForeColor = c; l.UseMnemonic = false; l.TextAlign = ContentAlignment.MiddleLeft;
        int w = Theme.S(496), need = Math.Max(Theme.S(h), Theme.TextHeight(text, f, w - Theme.S(8)) + Theme.S(8));
        l.SetBounds(Theme.S(x), y, w, need); y += need;
        return l;
    }
    void Refreshed() {
        bool run = rows[3].Value == 2, mouse = rows[4].Value == 2, any = rows.Any(delegate (CapRow r) { return r.Value > 0; }), ask = rows.Any(delegate (CapRow r) { return r.Value == 1; });
        for (int i = 0; i < 4; i++) { presets[i].Set(rows.Select(delegate (CapRow r) { return r.Value; }).SequenceEqual(Presets[i])); terms[i].Set(any && term == i); }
        for (int i = 0; i < rows.Length; i++) rows[i].Included = run && i < 3 ? "run" : mouse && i == 0 ? "mouse" : "";
        note.Text = (mouse ? Lang.T("能用鼠标键盘，就等于能做你坐在这里能做的一切。你一动鼠标或键盘它就让开。请只给你自己的、别人碰不到的电脑。", "Using the mouse and keyboard means doing anything you could do here. It steps aside when you touch them. Only for your own PCs that nobody else can get to.")
            : run ? Lang.T("能运行命令就等于能做其余一切，和你自己坐在这里一样。请只给你自己的、别人碰不到的电脑。", "Whoever can run commands can do all the rest, as if sitting here. Only for your own PCs that nobody else can get to.")
            : ask ? Lang.T("“询问”会在这台电脑上弹窗，你看过再决定。60 秒没人理就拒绝。", "\"Ask\" pops up on this PC and you decide. No answer within 60 seconds means no.")
            : any ? Lang.T("“允许”不再弹窗，但每一次都会记在聊天里。", "\"Allow\" doesn't pop up any more, but every time is recorded in the chat.")
            : Lang.T("默认。对方仍然可以聊天和传文件。", "The default. The other PC can still chat and send files."))
            + (term == -1 && any ? " " + Lang.T("现在的授权到 {0} 为止。", "The present access ends {0}.", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(old.Until).ToLocalTime().ToString("M-d HH:mm")) : "");
        note.ForeColor = run || mouse ? Theme.Bad : Theme.Dim;
    }
    void Save() {
        string[] v = { "", "ask", "allow" };
        Grant want = Grant.Of(v[rows[0].Value], v[rows[1].Value], v[rows[2].Value], v[rows[3].Value], v[rows[4].Value]);
        want.Until = term == -1 ? old.Until : term == 0 ? 0 : term == 2 ? Util.Now() + (long)(DateTime.Today.AddDays(1) - DateTime.Now).TotalMilliseconds : Util.Now() + (term == 1 ? 3600000L : 7 * 86400000L);
        want.Paused = old != null && old.Paused;
        bool same = old != null ? key != null && old.Key == key && old.Until == want.Until && Grant.Caps.All(delegate (string c) { return old.Cap(c) == want.Cap(c); }) : !want.Any;
        if (!same && (want.Run == "allow" && (old == null || old.Run != "allow") || want.Mouse == "allow" && (old == null || old.Mouse != "allow") || want.Write == "allow" && (old == null || old.Write != "allow"))
            && MessageBox.Show(this, (want.Mouse == "allow" && (old == null || old.Mouse != "allow") ? Lang.T("让 {0} 不经询问就用这台电脑的鼠标和键盘？", "Let {0} use the mouse and keyboard here without asking?", peer.Name) : want.Run == "allow" ? Lang.T("让 {0} 不经询问就控制这台电脑？", "Let {0} control this PC without asking?", peer.Name) : Lang.T("让 {0} 不经询问就改这台电脑上的文件？", "Let {0} change files on this PC without asking?", peer.Name)) + Lang.T("\n\n校验码：{0}\n请确认它和 {1} 上显示的一致。", "\n\nCheck code: {0}\nMake sure {1} shows the same one.", Node.Fingerprint(key), peer.Name), "LanTalk", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        if (!same) node.SetGrant(peer.Id, peer.Name, key, want);
        DialogResult = DialogResult.OK;
    }
}

// Text that can't be changed, with the lines of a difference in colour.
public class DiffBox : RichTextBox {
    public DiffBox() { ReadOnly = true; BorderStyle = BorderStyle.None; Font = Theme.Mono; BackColor = Theme.Card; ForeColor = Theme.Text; DetectUrls = false; TabStop = false; ScrollBars = RichTextBoxScrollBars.Vertical; WordWrap = true; }
    public void Fill(string text, bool diff) {
        Clear();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) {
            char s = diff && i > 0 && lines[i].Length > 0 ? lines[i][0] : ' ';
            SelectionBackColor = s == '-' ? Theme.DelFill : s == '+' || s == '~' ? Theme.AddFill : BackColor;
            SelectionColor = s == '-' ? Theme.DelText : s == '+' || s == '~' ? Theme.AddText : diff && i > 0 ? Theme.Dim : Theme.Text;
            SelectionFont = Theme.MonoFor(lines[i]);
            AppendText(lines[i] + (i + 1 < lines.Length ? "\n" : ""));
        }
        Select(0, 0);
    }
}

// Pops up without taking the keyboard, so nobody approves by accident while typing.
public class AskForm : Form {
    readonly ControlAsk ask;
    readonly Timer tick = new Timer();
    readonly RButton no = RButton.Outline("");
    readonly DateTime end = DateTime.UtcNow.AddSeconds(ControlAsk.Seconds);

    static string Title(string op, string pc) {
        switch (op) {
            case "shot": return Lang.T("{0} 想看这台电脑的屏幕", "{0} wants to see this screen", pc);
            case "windows": return Lang.T("{0} 想看打开了哪些窗口", "{0} wants to see which windows are open", pc);
            case "info": return Lang.T("{0} 想看这台电脑的信息", "{0} wants facts about this PC", pc);
            case "cat": case "get": return Lang.T("{0} 想读这里的一个文件", "{0} wants to read a file here", pc);
            case "pull": return Lang.T("{0} 想读这里的一个文件夹", "{0} wants to read a folder here", pc);
            case "put": return Lang.T("{0} 想往这里写一个文件", "{0} wants to write a file here", pc);
            case "edit": return Lang.T("{0} 想修改这里的一个文件", "{0} wants to edit a file here", pc);
            case "push": return Lang.T("{0} 想同步这里的一个文件夹", "{0} wants to sync a folder here", pc);
            case "claude": return Lang.T("{0} 想让这里的 Claude 干活", "{0} has work for the Claude here", pc);
            case "tunnel": return Lang.T("{0} 想连这里的一个端口", "{0} wants to use a port here", pc);
            case "input": return Lang.T("{0} 想用这台电脑的鼠标和键盘", "{0} wants to use the mouse and keyboard", pc);
            case "update": return Lang.T("{0} 想更新这里的 LanTalk", "{0} wants to update LanTalk here", pc);
            case "bg": return Lang.T("{0} 想在这里后台运行", "{0} wants to run this in the background", pc);
        }
        return Lang.T("{0} 想在这台电脑上执行", "{0} wants to run this here", pc);
    }
    public AskForm(ControlAsk a) {
        ask = a;
        Text = Lang.T("LanTalk 远程控制", "LanTalk remote control"); Font = Theme.Body; BackColor = Theme.Back; ForeColor = Theme.Text; Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        Label head = new Label(); head.Text = Title(a.Op, a.PeerName); head.Font = Theme.Greeting; head.UseMnemonic = false; head.TextAlign = ContentAlignment.MiddleLeft; head.AutoEllipsis = true;
        head.SetBounds(Theme.S(17), Theme.S(12), Theme.S(590), Theme.S(46));
        bool hand = a.Op == "input", look = a.Op == "shot" || a.Op == "windows" || a.Op == "info" || hand;
        CardPanel box = new CardPanel();
        int h = look ? 96 : 200, y = 70;
        box.SetBounds(Theme.S(24), Theme.S(y), Theme.S(580), Theme.S(h));
        if (look) {
            string what = a.Text; int cut = what.IndexOfAny(new char[] { '：', ':' });
            box.Paint += delegate (object s, PaintEventArgs e) {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Theme.S(44);
                CapLook.Icon(e.Graphics, hand ? "mouse" : a.Op == "info" ? "read" : "screen", new Rectangle(Theme.S(22), (box.Height - d) / 2, d, d), Theme.Accent);
                int x = Theme.S(22) + d + Theme.S(20), w = box.Width - x - Theme.S(14);
                TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(e.Graphics, hand ? Lang.T("整个桌面，会同时看屏幕", "The whole desktop, and it sees the screen") : Plain(a.Op == "shot" && cut > 0 ? what.Substring(cut + 1).Trim() : what), Theme.Bold, new Rectangle(x, Theme.S(14), w, Theme.S(44)), Theme.Text, f | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(e.Graphics, hand ? Lang.T("点击、打字、滚动，到它停手 1 分钟为止", "Clicking, typing and scrolling, until it has been idle for a minute") : a.Op == "shot" ? Lang.T("只截一张，不能点、不能打字", "One picture. No clicking, no typing") : a.Op == "windows" ? Lang.T("标题和程序名", "Their titles and programs") : Lang.T("系统、硬件、磁盘、装了哪些工具", "Windows, hardware, disks, tools"), Theme.Small, new Rectangle(x, Theme.S(60), w, Theme.S(20)), Theme.Dim, f | TextFormatFlags.SingleLine);
            };
            box.AccessibleName = what;
        } else {
            DiffBox t = new DiffBox(); t.AccessibleName = Lang.T("要执行的内容", "What would run");
            t.SetBounds(Theme.S(14), Theme.S(10), Theme.S(556), Theme.S(h - 20));
            box.Controls.Add(t);
            t.Fill(Plain(a.Text), a.Op == "edit" || a.Op == "push");
        }
        y += h + 10;
        Label who = new Label(); who.Text = hand ? Lang.T("由那台电脑上的 {0} 发起。你一动鼠标或键盘它就让开；它不往密码框里打字，也碰不到管理员窗口。", "Asked for by {0} on that PC. It steps aside when you touch the mouse or keyboard, never types into password boxes, and can't use administrator windows.", a.Agent) : a.Op == "shot" ? Lang.T("由那台电脑上的 {0} 发起。会截到当时屏幕上的一切，先收起不想给它看的窗口。", "Asked for by {0} on that PC. Everything on the screen will be in it, so close what it shouldn't see.", a.Agent) : Lang.T("由那台电脑上的 {0} 发起。看不懂就拒绝。", "Asked for by {0} on that PC. If it makes no sense to you, decline.", a.Agent); who.Font = Theme.Small; who.ForeColor = Theme.Dim; who.UseMnemonic = false;
        int wh = Theme.TextHeight(who.Text, Theme.Small, Theme.S(570)) + Theme.S(6);
        who.SetBounds(Theme.S(21), Theme.S(y), Theme.S(584), wh);
        int by = Theme.S(y) + wh + Theme.S(10);
        string cap = CapLook.Name(a.Cap ?? "");
        RButton once = RButton.Primary(Lang.T("允许这一次", "Allow once")), longer = RButton.Outline(cap == "" ? Lang.T("30 分钟内都允许", "Allow for 30 minutes") : hand ? Lang.T("30 分钟内都允许", "Allow for 30 minutes") : Lang.T("30 分钟内都允许{0}", "30 minutes: {0}", cap));
        int ow = Theme.S(128), lw = Math.Max(Theme.S(170), longer.FitWidth + Theme.S(8));
        no.SetBounds(Theme.S(24), by, Theme.S(120), Theme.S(38));
        once.SetBounds(Theme.S(604) - ow, by, ow, Theme.S(38)); longer.SetBounds(once.Left - Theme.S(8) - lw, by, lw, Theme.S(38));
        no.Click += delegate { Answer(false, false); }; once.Click += delegate { Answer(true, false); }; longer.Click += delegate { Answer(true, true); };
        Controls.AddRange(new Control[] { no, longer, once, head, box, who });
        ClientSize = new Size(Theme.S(628), by + Theme.S(38 + 22));
        CancelButton = no;
        tick.Interval = 500; tick.Tick += delegate { ShowLeft(); }; tick.Start(); ShowLeft();
        a.Closed += delegate { try { BeginInvoke((MethodInvoker)Close); } catch (InvalidOperationException) { } };
        FormClosed += delegate { tick.Dispose(); ask.Answer(false, false); };
        IntPtr handle = Handle; // so Closed can reach it
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this, Theme.Back); }
    void ShowLeft() { no.Text = Lang.T("拒绝（{0}）", "Decline ({0})", Math.Max(0, (int)(end - DateTime.UtcNow).TotalSeconds)); no.Invalidate(); }
    void Answer(bool ok, bool longer) { ask.Answer(ok, longer); Close(); }
    static bool Hidden(UnicodeCategory c) { return c == UnicodeCategory.Format || c == UnicodeCategory.LineSeparator || c == UnicodeCategory.ParagraphSeparator; }
    // Invisible and text-direction characters could make a command read differently from what runs.
    public static string Plain(string s) {
        StringBuilder b = new StringBuilder();
        foreach (char c in s.Replace("\r\n", "\n").Replace('\r', '\n')) {
            if (c == '\n') b.Append("\r\n");
            else if (c == '\t') b.Append("    ");
            else if (char.IsControl(c) || Hidden(char.GetUnicodeCategory(c))) b.Append("[U+" + ((int)c).ToString("X4") + "]");
            else b.Append(c);
        }
        return b.ToString();
    }
}

// What other PCs are doing here, worked out for everything that shows it. Quick commands don't make it flicker.
public class ControlWatch {
    public enum Kind { Off, Active, Paused }
    readonly Node node;
    readonly Dictionary<string, DateTime> told = new Dictionary<string, DateTime>();
    DateTime last, pausedAt;
    List<string> ids = new List<string>();
    public Kind State;
    public int Count;
    public bool Includes(string id) { return ids.Contains(id); }
    public string Names = "", Detail = "";
    public InputLook Hands; // another PC uses the mouse and keyboard
    public bool JustPaused { get { return State == Kind.Paused && (DateTime.UtcNow - pausedAt).TotalSeconds < 12; } }
    public ControlWatch(Node n) { node = n; }
    string NameOf(string id) { Peer p = node.Find(id); Grant g = node.GrantFor(id); return p != null ? p.Name : g != null ? g.Name : id; }
    string Join(IEnumerable<string> l) { return string.Join(Lang.T("、", ", "), l.Select(NameOf).ToArray()); }
    // True when something to show has changed.
    public bool Update() {
        string before = Key;
        List<ChatMsg> now = node.Controlling();
        now.RemoveAll(delegate (ChatMsg m) { Grant g = node.GrantFor(m.From); return g != null && g.Paused; }); // on its way out
        bool fresh = now.Exists(delegate (ChatMsg m) { DateTime t; return m.Op == "input" || Util.Now() - m.Time > 1000 && m.State == "run" || !told.TryGetValue(m.From, out t) || (DateTime.UtcNow - t).TotalMinutes > 10; });
        if (now.Count > 0 && (fresh || State == Kind.Active)) {
            State = Kind.Active; last = DateTime.UtcNow; Count = now.Count;
            ids = now.Select(delegate (ChatMsg m) { return m.From; }).Distinct().ToList();
            ChatMsg newest = now.OrderBy(delegate (ChatMsg m) { return m.Seq; }).Last();
            string[] lines = (newest.Text ?? "").Split('\n').Where(delegate (string s) { return s.Trim() != "" && !s.StartsWith("["); }).ToArray();
            Detail = lines.Length == 0 ? "" : AskForm.Plain(Node.IsJob(newest.Op) && lines.Length > 1 ? lines[1] : lines[0]);
            Names = Join(ids);
        } else if (State == Kind.Active && (DateTime.UtcNow - last).TotalSeconds > 3) State = Kind.Off;
        foreach (ChatMsg m in now) told[m.From] = DateTime.UtcNow;
        if (State != Kind.Active) {
            List<string> p; lock (node.L) p = node.Cfg.Control.Where(delegate (Grant g) { return g.Paused; }).Select(delegate (Grant g) { return g.Id; }).ToList();
            if (p.Count == 0) State = Kind.Off;
            else { State = Kind.Paused; ids = p; Names = Join(p); Detail = ""; Count = 0; }
        }
        Hands = State == Kind.Active ? node.InputNow() : null;
        if (Hands != null) { ids = new List<string> { Hands.Peer }.Concat(ids).Distinct().ToList(); Names = NameOf(Hands.Peer); Detail = AskForm.Plain(Hands.Detail); }
        return before != Key;
    }
    string Key { get { return State + "|" + Count + "|" + Names + "|" + Detail + "|" + JustPaused + (Hands == null ? "" : "|" + Hands.Phase); } }
    public void Press() {
        bool stop = State == Kind.Active;
        foreach (string id in ids.ToArray()) if (stop) node.PauseControl(id); else node.ResumeControl(id);
        if (stop) pausedAt = DateTime.UtcNow;
        State = Kind.Off;
        Update();
    }
    public string Button { get { return State == Kind.Active ? Lang.T("停止", "Stop") : Lang.T("恢复", "Resume"); } }
    public string Head { get { return Hands != null ? Hands.Phase == "yield" ? Lang.T("你在用，它先让开", "You are using it, so it waits") : Hands.Phase == "password" ? Lang.T("这里是密码框，要你来输", "Password box: over to you") : Lang.T("{0} 正在用鼠标键盘", "{0} is using the mouse", Names) : State == Kind.Active ? Lang.T("{0} 正在控制这台电脑", "{0} is controlling this PC", Names) : Lang.T("已停止 {0} 的控制", "Stopped {0}", Names); } }
    public string Third { get { return Hands != null && Hands.Phase == "act" ? Lang.T("Ctrl+Alt+空格 也能停", "Ctrl+Alt+Space stops it too") : ""; } }
    public string Sub { get { return Hands != null && Hands.Phase == "yield" ? Lang.T("你停手 5 秒后它继续", "It goes on 5 seconds after you stop") : Hands != null && Hands.Phase == "password" ? Lang.T("它不往密码框里打字", "It never types into those") : State == Kind.Active ? Detail : Lang.T("你恢复之前，它不能再操作", "On hold until you resume"); } }
    public string MenuText { get { return State == Kind.Active ? Lang.T("停止 {0} 的控制", "Stop {0}", Names) : Lang.T("恢复 {0} 的控制", "Let {0} go on", Names); } }
}

// Strip below the title of the main window.
public class ControlBar : Control {
    readonly ControlWatch watch;
    readonly RButton go = RButton.Primary("");
    public event Action Pressed;
    public ControlBar(ControlWatch w) {
        watch = w;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Theme.S(52); Controls.Add(go);
        go.Click += delegate { if (Pressed != null) Pressed(); };
        Layout += delegate { go.Size = new Size(Math.Max(Theme.S(64), go.FitWidth + Theme.S(12)), Theme.S(34)); go.Location = new Point(Width - go.Width - Theme.S(14), (Height - go.Height) / 2); };
    }
    public void Show(bool visible) {
        bool active = watch.State == ControlWatch.Kind.Active;
        BackColor = active ? Theme.AgentFill : Theme.Side;
        go.Text = watch.Button; go.Fill = active ? Theme.AccentDown : Theme.Card; go.Hover = active ? Theme.Accent : Theme.Back; go.Border = active ? Color.Empty : Theme.Sand; go.ForeColor = active ? Color.White : Theme.Soft;
        Visible = visible; AccessibleName = watch.Head;
        PerformLayout(); Invalidate(); go.Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        bool active = watch.State == ControlWatch.Kind.Active;
        int d = Theme.S(8), x = Theme.S(22);
        using (Brush b = new SolidBrush(active ? Theme.Accent : Theme.Dim)) if (active) g.FillEllipse(b, x, (Height - d) / 2, d, d); else g.FillRectangle(b, x, (Height - d) / 2, d, d);
        x += d + Theme.S(10);
        TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        string text = watch.Head + (active ? watch.Count > 1 ? Lang.T("，{0} 项进行中", ", {0} things running", watch.Count) : "" : Lang.T("，", ". ") + watch.Sub);
        TextRenderer.DrawText(g, text, Theme.Body, new Rectangle(x, 0, go.Left - x - Theme.S(10), Height), active ? Theme.AccentText : Theme.Soft, f);
        using (Pen p = new Pen(active ? Theme.AgentLine : Theme.Line)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }
}

// The same in the corner of the screen, for when the main window isn't in front. Never takes the keyboard and isn't in screenshots.
public class PillForm : Form {
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    readonly ControlWatch watch;
    readonly RButton go = RButton.Primary("");
    bool up; // moved out of the way of a click
    public event Action Pressed;
    public PillForm(ControlWatch w) {
        watch = w; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; BackColor = Theme.Back; Font = Theme.Body; AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        ClientSize = new Size(Theme.S(340), Theme.S(66));
        go.Click += delegate { if (Pressed != null) Pressed(); };
        Controls.Add(go);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 8; p.ClassStyle |= 0x20000; return p; } } // no activation, not in Alt+Tab, topmost; shadow
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetWindowDisplayAffinity(Handle, 0x11); } // has no effect before Windows 10 2004
    public void Show(bool visible) {
        if (!visible) { if (Visible) Hide(); return; }
        bool active = watch.State == ControlWatch.Kind.Active;
        go.Text = watch.Button; go.Fill = active ? Theme.AccentDown : Theme.Card; go.Hover = active ? Theme.Accent : Theme.Side; go.Border = active ? Color.Empty : Theme.Sand; go.ForeColor = active ? Color.White : Theme.Soft;
        go.Size = new Size(Math.Max(Theme.S(62), go.FitWidth + Theme.S(8)), Theme.S(34)); go.Location = new Point(Width - go.Width - Theme.S(12), (Height - go.Height) / 2);
        AccessibleName = watch.Head;
        if (watch.Hands == null) up = false;
        ClientSize = new Size(ClientSize.Width, Theme.S(watch.Third != "" ? 84 : 66));
        go.Top = (Height - go.Height) / 2;
        Place();
        using (GraphicsPath p = Theme.Round(new Rectangle(0, 0, Width, Height), Theme.S(24))) Region = new Region(p);
        if (!Visible) Show();
        Invalidate(); go.Invalidate();
    }
    void Place() { Rectangle area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Right - Width - Theme.S(16), up ? area.Top + Theme.S(16) : area.Bottom - Height - Theme.S(16)); }
    // Whoever uses the mouse can't see this window, so it must not be where the click goes. p in real pixels.
    public void MakeRoom(Point p) { if (Visible && Shot.Outer(Handle).Contains(p)) { up = !up; Place(); Update(); } }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        bool active = watch.State == ControlWatch.Kind.Active;
        Theme.Fill(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(24), Color.Empty, active ? Theme.AskLine : Theme.Sand);
        int d = Theme.S(watch.Hands != null ? 20 : 10), x = Theme.S(watch.Hands != null ? 14 : 16);
        if (watch.Hands != null) CapLook.Icon(g, watch.Hands.Phase == "password" ? "lock" : "mouse", new Rectangle(x, (Height - d) / 2, d, d), watch.Hands.Phase == "yield" ? Theme.Dim : Theme.Accent);
        else using (Brush b = new SolidBrush(active ? Theme.Accent : Theme.Dim)) if (active) g.FillEllipse(b, x, (Height - d) / 2, d, d); else g.FillRectangle(b, x, (Height - d) / 2, d, d);
        x += d + Theme.S(watch.Hands != null ? 10 : 12);
        TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        int w = go.Left - x - Theme.S(8);
        TextRenderer.DrawText(g, watch.Head, Theme.Bold, new Rectangle(x, Theme.S(11), w, Theme.S(23)), Theme.Text, f);
        TextRenderer.DrawText(g, watch.Sub, Theme.Small, new Rectangle(x, Theme.S(35), w, Theme.S(19)), Theme.Dim, f);
        if (watch.Third != "") TextRenderer.DrawText(g, watch.Third, Theme.Small, new Rectangle(x, Theme.S(54), w, Theme.S(19)), Theme.Dim, f);
    }
}

// What a background job has printed so far.
public class OutputForm : Form {
    readonly TextBox t = new TextBox();
    readonly Timer tick = new Timer();
    readonly Func<string> read;
    public OutputForm(string title, Func<string> source) {
        read = source;
        Text = title; Font = Theme.Body; BackColor = Theme.Back; ForeColor = Theme.Text; Icon = Theme.AppIcon(); ClientSize = new Size(Theme.S(720), Theme.S(460)); MinimumSize = new Size(Theme.S(360), Theme.S(240)); StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowInTaskbar = false;
        t.Multiline = true; t.ReadOnly = true; t.BorderStyle = BorderStyle.None; t.ScrollBars = ScrollBars.Vertical; t.Font = Theme.Mono; t.BackColor = Theme.Card; t.ForeColor = Theme.Text; t.AccessibleName = title; t.Dock = DockStyle.Fill;
        CardPanel box = new CardPanel(); box.Dock = DockStyle.Fill; box.Padding = new Padding(Theme.S(14), Theme.S(12), Theme.S(8), Theme.S(12)); box.Controls.Add(t);
        Panel outer = new Panel(); outer.Dock = DockStyle.Fill; outer.Padding = new Padding(Theme.S(16)); outer.Controls.Add(box); Controls.Add(outer);
        tick.Interval = 1000; tick.Tick += delegate { Load2(); }; tick.Start();
        FormClosed += delegate { tick.Dispose(); };
        Load2();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this, Theme.Back); }
    void Load2() {
        string s;
        try { s = read(); } catch (System.IO.IOException) { return; }
        if (s == null) { tick.Stop(); s = Lang.T("（输出已经清掉了）", "(The output is gone)"); }
        s = s.Length == 0 ? Lang.T("（还没有输出）", "(No output yet)") : AskForm.Plain(s);
        if (s == t.Text) return;
        t.Font = Theme.MonoFor(s); t.Text = s; t.Select(s.Length, 0); t.ScrollToCaret();
    }
}
}
