// LanTalk window: contact list, chat bubbles, tray icon, settings.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanTalk {

// Scrolling list of chat bubbles.
public class ChatView : Control {
    class Item { public ChatMsg M; public Rectangle Bubble, Head; public int Top, Height; public bool Mine; public Image Thumb; public List<string> Diff; public Dictionary<string, Rectangle> Buttons = new Dictionary<string, Rectangle>(); }
    const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    public const int MaxColumn = 860; // wide windows keep a readable column in the middle
    readonly SlimBar bar = new SlimBar();
    readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>();
    List<Item> items = new List<Item>();
    List<ChatMsg> msgs = new List<ChatMsg>();
    public string MyId;
    public event Action<ChatMsg> OpenRequested;
    public event Action<ChatMsg, string> ButtonPressed; // output | stop
    public Func<ChatMsg, bool> HasOutput = delegate { return false; };
    public event Action<ChatMsg, Point> MenuRequested;

    public ChatView() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Back;
        bar.Dock = DockStyle.Right; bar.Visible = false;
        bar.ValueChanged += delegate { Invalidate(); };
        Controls.Add(bar);
    }
    int lastMax; // scroll range before a resize
    int MaxScroll { get { return Math.Max(0, bar.Maximum - bar.LargeChange + 1); } }
    public void SetMessages(List<ChatMsg> l, bool toEnd) {
        bool atEnd = !bar.Visible || bar.Value >= MaxScroll - Theme.S(40);
        msgs = l; Relayout(toEnd || atEnd);
    }
    public void Touch(ChatMsg m) { // state or progress changed
        Item it = items.Find(delegate (Item x) { return x.M == m; });
        if (it == null) return;
        if (it.Thumb == null && IsImage(m)) Relayout(true); else if (Node.IsJob(m.Op) || m.Op == "tunnel") Relayout(false); else if (m.Op == "input") Relayout(!bar.Visible || bar.Value >= MaxScroll - Theme.S(40)); else Invalidate();
    }
    static bool IsImage(ChatMsg m) { return (m.Kind == "file" || m.Op == "shot") && m.State == "ok" && m.Path != null && Regex.IsMatch(m.Path, "\\.(png|jpe?g|bmp|gif)$", RegexOptions.IgnoreCase); }
    Image Thumb(ChatMsg m) {
        if (!IsImage(m)) return null;
        Image t;
        if (thumbs.TryGetValue(m.Id, out t)) return t;
        try {
            FileInfo f = new FileInfo(m.Path);
            if (f.Exists && f.Length < 20 * 1024 * 1024)
                using (FileStream fs = f.OpenRead()) using (Image src = Image.FromStream(fs, false, false)) // no validation: reads the header only
                    if ((long)src.Width * src.Height <= 40000000) {
                        double k = Math.Min(1.0, Math.Min(Theme.S(260) / (double)src.Width, Theme.S(200) / (double)src.Height));
                        t = new Bitmap(src, Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
                    }
        } catch (Exception) { t = null; } // unreadable picture: shown as a plain file
        thumbs[m.Id] = t;
        return t;
    }
    void Relayout(bool toEnd) {
        int pad = Theme.S(14), w = Math.Min(ClientSize.Width - 2 * Theme.S(22), Theme.S(MaxColumn)), x0 = (ClientSize.Width - w) / 2, y = Theme.S(16);
        int max = Math.Max(Theme.S(120), (int)(w * 0.76));
        items = new List<Item>();
        foreach (ChatMsg m in msgs) {
            Item it = new Item(); it.M = m; it.Mine = m.From == MyId; it.Top = y; it.Thumb = Thumb(m);
            Size s;
            if (it.Thumb != null) s = new Size(Math.Max(it.Thumb.Width, Theme.S(120)), it.Thumb.Height + Theme.S(22));
            else if (m.Kind == "file") s = new Size(Math.Min(Theme.S(300), max - 2 * pad), Theme.S(54));
            else if (m.Op == "edit") { it.Diff = DiffOf(m); s = new Size(0, it.Diff.Count * DiffLine + Theme.S(2)); foreach (string l in it.Diff) s.Width = Math.Min(max - 2 * pad, Math.Max(s.Width, Theme.Measure(l, Theme.MonoFor(l), new Size(int.MaxValue, int.MaxValue), OneLine).Width + 2)); }
            else s = Theme.Measure(TextOf(m), FontOf(m), new Size(max - 2 * pad, int.MaxValue), Flags);
            List<string> btns = new List<string>();
            if (Node.IsJob(m.Op) && !it.Mine && HasOutput(m)) { btns.Add("output"); if (m.State == "run") btns.Add("stop"); }
            if (m.Op == "input" && StepCount(m) > 4) btns.Add("steps");
            if ((m.Op == "tunnel" || m.Op == "input") && m.State == "run") btns.Add("stop");
            int textH = s.Height;
            if (btns.Count > 0) { s.Height += Theme.S(38); s.Width = Math.Max(s.Width, Theme.S(170)); }
            int bw = s.Width + 2 * pad + 2, bh = s.Height + 2 * Theme.S(10);
            int x = it.Mine ? x0 + w - bw : x0;
            it.Head = new Rectangle(x0 + Theme.S(2), y, w - Theme.S(4), Theme.S(20));
            it.Bubble = new Rectangle(x, y + Theme.S(24), bw, bh);
            int bx = it.Bubble.X + pad;
            foreach (string id in btns) { int w2 = Theme.Measure(ButtonText(id, m), Theme.Small, new Size(int.MaxValue, int.MaxValue), OneLine).Width + Theme.S(26); it.Buttons[id] = new Rectangle(bx, it.Bubble.Y + Theme.S(10) + textH + Theme.S(8), w2, Theme.S(30)); bx += w2 + Theme.S(8); }
            it.Height = Theme.S(24) + bh + Theme.S(16);
            y += it.Height;
            items.Add(it);
        }
        int old = bar.Value;
        bar.Visible = y > ClientSize.Height;
        bar.Minimum = 0; bar.SmallChange = Theme.S(40); bar.LargeChange = Math.Max(1, ClientSize.Height); bar.Maximum = Math.Max(y, 1);
        bar.Value = bar.Visible ? (toEnd ? MaxScroll : Math.Min(old, MaxScroll)) : 0;
        lastMax = MaxScroll;
        Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (ClientSize.Height > 0) Relayout(!bar.Visible || bar.Value >= lastMax - Theme.S(40)); }
    protected override void OnMouseWheel(MouseEventArgs e) { if (bar.Visible) bar.Value = Math.Max(0, Math.Min(MaxScroll, bar.Value - Math.Sign(e.Delta) * Theme.S(90))); }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int off = bar.Visible ? bar.Value : 0;
        foreach (Item it in items) {
            if (it.Top + it.Height < off || it.Top > off + ClientSize.Height) continue;
            ChatMsg m = it.M;
            Rectangle b = it.Bubble; b.Offset(0, -off);
            bool agent = !string.IsNullOrEmpty(m.Agent);
            string status = m.Kind == "run" ? RunTitle(m, it.Mine) + " · " + (m.State != "run" ? m.ErrText : m.Op == "input" ? Lang.T("{0} 步", "{0} steps", StepCount(m)) : Node.IsJob(m.Op) || m.Op == "tunnel" ? Lang.T("已运行 {0}", "running for {0}", BgJob.Span(Math.Max(0, (Util.Now() - m.Time) / 1000.0)).ToString()) : Lang.T("运行中（右键停止）", "running (right-click to stop)")) : it.Mine ? Status(m) : (m.State == "fail" ? Lang.T("接收失败：", "not received: ") + m.ErrText : "");
            string head = (it.Mine ? (agent ? m.Agent + Lang.T("（本机）", " (this PC)") : "") : (agent ? m.Agent + " @ " + m.FromName : m.FromName)) + "  " + Stamp(m.LocalTime) + (status != "" ? "  · " + status : "");
            Rectangle hr = it.Head; hr.Offset(0, -off);
            TextRenderer.DrawText(g, head.Trim(), Theme.Small, hr, m.State == "fail" ? Theme.Bad : agent ? Theme.AccentText : Theme.Dim, TextFormatFlags.NoPadding | (it.Mine ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Theme.Fill(g, b, Theme.S(24), m.Kind == "run" ? Theme.Side : agent ? Theme.AgentFill : it.Mine ? Theme.Mine : Theme.Theirs, m.Kind == "run" ? (m.State == "run" ? Theme.Accent : Theme.Line) : agent ? Theme.AgentLine : it.Mine ? Color.Empty : Theme.Line);
            Rectangle inner = Rectangle.Inflate(b, -Theme.S(14), -Theme.S(10));
            if (it.Diff != null) { PaintDiff(g, it, b, inner); continue; }
            if (m.Kind != "file" && it.Thumb == null) {
                TextRenderer.DrawText(g, TextOf(m), FontOf(m), inner, Theme.Text, Flags);
                foreach (KeyValuePair<string, Rectangle> k in it.Buttons) {
                    Rectangle r = k.Value; r.Offset(0, -off);
                    Theme.Fill(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(14), hot == k.Key && hotItem == m ? Theme.SideSel : Theme.Card, Theme.Sand);
                    TextRenderer.DrawText(g, ButtonText(k.Key, m), Theme.Small, r, k.Key == "stop" ? Theme.AccentText : Theme.Soft, OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                continue;
            }
            TextFormatFlags one = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            if (it.Thumb != null) {
                g.DrawImage(it.Thumb, inner.X, inner.Y, it.Thumb.Width, it.Thumb.Height);
                TextRenderer.DrawText(g, m.Kind == "run" ? TextOf(m) : m.Text, Theme.Small, new Rectangle(inner.X, inner.Bottom - Theme.S(18), inner.Width, Theme.S(18)), Theme.Dim, one | TextFormatFlags.EndEllipsis);
                continue;
            }
            int tile = Theme.S(42), tx = inner.X + tile + Theme.S(12), tw = inner.Right - tx;
            Rectangle tr = new Rectangle(inner.X, inner.Y + (inner.Height - tile) / 2, tile, tile);
            Theme.Fill(g, tr, Theme.S(16), agent ? Theme.Clay : Theme.Sand, Color.Empty);
            TextRenderer.DrawText(g, "\uE8A5", Theme.GlyphBig, tr, agent ? Color.White : Theme.Soft, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, m.Text, Theme.Bold, new Rectangle(tx, inner.Y + Theme.S(4), tw, Theme.S(24)), Theme.Text, one | TextFormatFlags.EndEllipsis);
            bool moving = m.State == "sending" || m.State == "recv";
            string info = moving && m.Size > 0 ? Util.SizeText(m.Done) + " / " + Util.SizeText(m.Size) : Util.SizeText(m.Size);
            TextRenderer.DrawText(g, info, Theme.Small, new Rectangle(tx, inner.Y + Theme.S(28), tw, Theme.S(20)), Theme.Dim, one);
            if (moving && m.Size > 0) {
                Rectangle pr = new Rectangle(tx, inner.Bottom - Theme.S(4), tw, Theme.S(4));
                Theme.Fill(g, pr, pr.Height, Theme.Line, Color.Empty);
                pr.Width = (int)(pr.Width * Math.Min(1.0, m.Done / (double)m.Size));
                if (pr.Width > 1) Theme.Fill(g, pr, pr.Height, Theme.Clay, Color.Empty);
            }
        }
        if (items.Count == 0) {
            TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int c = ClientSize.Height / 2;
            TextRenderer.DrawText(g, Lang.T("还没有消息", "No messages yet"), Theme.Greeting, new Rectangle(0, c - Theme.S(44), ClientSize.Width, Theme.S(44)), Theme.Text, mid);
            TextRenderer.DrawText(g, Lang.T("写点什么，或者直接把文件、文件夹拖进来。", "Write something, or drop files and folders here."), Theme.Body, new Rectangle(0, c + Theme.S(4), ClientSize.Width, Theme.S(28)), Theme.Dim, mid);
        }
    }
    const TextFormatFlags OneLine = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
    string hot; ChatMsg hotItem; // button under the mouse
    static int DiffLine { get { return Theme.S(20); } }
    static int StepCount(ChatMsg m) { int n = 0; foreach (Match x in Regex.Matches(m.Text ?? "", "(?m)^(\\d+)  ")) int.TryParse(x.Groups[1].Value, out n); return n; }
    static string ButtonText(string id, ChatMsg m) { return id == "steps" ? Lang.T("看全部 {0} 步", "All {0} steps", StepCount(m)) : id == "output" ? Lang.T("看输出", "Output") : Lang.T("停止", "Stop"); }
    static string RunTitle(ChatMsg m, bool mine) {
        if (Node.IsJob(m.Op)) { string t = (m.Text ?? "").Split('\n')[0]; return t.Length > 0 ? AskForm.Plain(t) : CapLook.Doing(m.Op); } // with its number
        if (m.Op == "input") return m.State == "run" ? Lang.T("正在用鼠标键盘", "using the mouse and keyboard") : Lang.T("用了鼠标键盘", "used the mouse and keyboard");
        string d = CapLook.Doing(m.Op);
        return d != "" ? d : mine ? Lang.T("远程执行", "remote command") : Lang.T("控制了这台电脑", "controlled this PC");
    }
    static Font FontOf(ChatMsg m) { return m.Kind != "run" || m.Op == "shot" || m.Op == "windows" || m.Op == "info" || m.Op == "input" ? Theme.Body : Theme.MonoFor(TextOf(m)); }
    // An edit in short: the file, then the lines that went and came.
    static List<string> DiffOf(ChatMsg m) {
        string[] all = (m.Text ?? "").Split('\n');
        List<string> l = new List<string>(), d = all.Skip(1).Where(delegate (string s) { return s.Length > 0 && s[0] != ' '; }).ToList();
        l.Add(AskForm.Plain(all[0]));
        foreach (string s in d.Take(16)) l.Add(s[0] + " " + AskForm.Plain(System.Text.RegularExpressions.Regex.Replace(s.Substring(1), "^ +\\d+  ", "").TrimStart(s.Length > 1 && s[1] == ' ' ? new char[] { ' ' } : new char[0])));
        if (d.Count > 16) l.Add("  " + Lang.T("…还有 {0} 行", "...{0} more lines", d.Count - 16));
        return l;
    }
    void PaintDiff(Graphics g, Item it, Rectangle b, Rectangle inner) {
        Region was = g.Clip;
        using (GraphicsPath p = Theme.Round(Rectangle.Inflate(b, -1, 0), Theme.S(24))) g.SetClip(p, CombineMode.Intersect);
        for (int i = 0; i < it.Diff.Count; i++) {
            string t = it.Diff[i]; char c = i > 0 && t.Length > 0 ? t[0] : ' ';
            Rectangle r = new Rectangle(b.X + 1, inner.Y + i * DiffLine + (i > 0 ? Theme.S(2) : 0), b.Width - 1, DiffLine);
            if (c == '-' || c == '+') using (Brush f = new SolidBrush(c == '-' ? Theme.DelFill : Theme.AddFill)) g.FillRectangle(f, r);
            TextRenderer.DrawText(g, t, Theme.MonoFor(t), new Rectangle(inner.X, r.Y, inner.Width + 2, r.Height), c == '-' ? Theme.DelText : c == '+' ? Theme.AddText : i == 0 ? Theme.Text : Theme.Dim, OneLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
        }
        g.Clip = was;
    }
    static string Short(ChatMsg m) {
        string t = m.Text ?? "";
        if (Node.IsJob(m.Op)) { int i = t.IndexOf('\n'); return i < 0 ? t : t.Substring(i + 1); }
        if (m.Op == "input") { string[] l = t.Split('\n').Skip(1).ToArray(); return l.Length == 0 ? Lang.T("（还没有动作）", "(Nothing done yet)") : string.Join("\n", l.Skip(l.Length - 4).ToArray()); }
        if (m.Op == "shot") { int i = t.IndexOfAny(new char[] { '：', ':' }); return i < 0 ? t : t.Substring(i + 1).Trim(); }
        if (m.Op == "push") return string.Join("\n", t.Split('\n').Where(delegate (string s) { return s.Length < 2 || s[1] != ' ' || "+-~".IndexOf(s[0]) < 0; }).ToArray()); // the file names are in the copy
        return t;
    }
    static string TextOf(ChatMsg m) { string t = Short(m); return m.Kind != "run" ? t : t.Length <= 1500 ? AskForm.Plain(t) : AskForm.Plain(t.Substring(0, 1500)) + "…"; }
    static string Stamp(DateTime t) { return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("M-d HH:mm"); }
    static string Status(ChatMsg m) {
        switch (m.State) {
            case "queued": return Lang.T("等待对方上线", "waiting for the other PC");
            case "sending": return Lang.T("发送中", "sending");
            case "fail": return Lang.T("失败（右键重试） ", "failed (right-click to retry) ") + m.ErrText;
            case "cancel": return Lang.T("已取消", "cancelled");
        }
        return "";
    }
    Item Hit(Point p) { p.Y += bar.Visible ? bar.Value : 0; return items.Find(delegate (Item i) { return i.Bubble.Contains(p); }); }
    protected override void OnMouseDown(MouseEventArgs e) {
        Focus();
        Item it = Hit(e.Location);
        string id = it == null || e.Button != MouseButtons.Left ? null : ButtonAt(it, e.Location);
        if (id != null) { if (ButtonPressed != null) ButtonPressed(it.M, id); return; }
        if (it != null && e.Button == MouseButtons.Right && MenuRequested != null) MenuRequested(it.M, e.Location);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { Item it = Hit(e.Location); if (it != null && (it.M.Kind == "file" || it.M.Op == "shot" && it.M.Path != null) && OpenRequested != null) OpenRequested(it.M); }
    string ButtonAt(Item it, Point p) { p.Y += bar.Visible ? bar.Value : 0; foreach (KeyValuePair<string, Rectangle> k in it.Buttons) if (k.Value.Contains(p)) return k.Key; return null; }
    protected override void OnMouseMove(MouseEventArgs e) {
        Item it = Hit(e.Location);
        string id = it == null ? null : ButtonAt(it, e.Location);
        if (id != hot || id != null && it.M != hotItem) { hot = id; hotItem = id == null ? null : it.M; Invalidate(); }
        Cursor = it != null && (it.M.Kind == "file" || id != null || it.M.Op == "shot" && it.M.Path != null) ? Cursors.Hand : Cursors.Default;
    }
    public bool Ticking { get { return msgs.Exists(delegate (ChatMsg m) { return (Node.IsJob(m.Op) || m.Op == "tunnel") && m.State == "run"; }); } }
}

public class MainForm : Form {
    class Conv { public string Id, Title, Device, Caption; public bool Online; public override string ToString() { return Title; } }
    readonly Node node;
    readonly string dataDir;
    readonly ConvList list = new ConvList();
    readonly BrandBar brand = new BrandBar();
    readonly ChatView view = new ChatView();
    readonly HeaderBar title = new HeaderBar();
    readonly HintBox input = new HintBox();
    readonly Panel bottom = new Panel();
    readonly CardPanel card = new CardPanel();
    readonly RButton perm = new RButton("", "\uE72E"), set = new RButton("", "\uE713"), bo = new RButton("", "\uE838"), bf = new RButton("", "\uE723"), bd = new RButton("", "\uE8B7"), send = RButton.Primary("");
    readonly ToolTip tips = new ToolTip();
    readonly ControlWatch watch;
    readonly ControlBar bar;
    readonly PillForm pill;
    readonly ScreenEdge edge = new ScreenEdge();
    readonly ClickRing ring = new ClickRing();
    readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
    int ticks;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Dictionary<string, int> unread = new Dictionary<string, int>();
    string current = Node.Ai;
    bool quitting, toldTray, filling;

    [DllImport("user32.dll")] static extern bool FlashWindow(IntPtr h, bool invert);

    public MainForm(Node n, string data) {
        node = n; dataDir = data;
        watch = new ControlWatch(n); bar = new ControlBar(watch); pill = new PillForm(watch);
        Text = "LanTalk · " + n.Cfg.Name;
        Icon = Theme.AppIcon(); Font = Theme.Body; BackColor = Theme.Back; KeyPreview = true;
        ClientSize = new Size(Theme.S(900), Theme.S(620)); MinimumSize = new Size(Theme.S(620), Theme.S(420));
        StartPosition = FormStartPosition.CenterScreen;

        Panel side = new Panel(); side.Dock = DockStyle.Left; side.Width = Theme.S(236); side.BackColor = Theme.Side; side.Padding = new Padding(0, 0, 1, 0);
        side.Paint += delegate (object s, PaintEventArgs e) { using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
        brand.Dock = DockStyle.Top; brand.Height = Theme.S(68);
        list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.BackColor = Theme.Side; list.DrawMode = DrawMode.OwnerDrawVariable; list.ItemHeight = RowHeight; list.IntegralHeight = false;
        list.DrawItem += DrawConv;
        list.MeasureItem += delegate (object s, MeasureItemEventArgs e) { e.ItemHeight = RowHeight + (e.Index >= 0 && ((Conv)list.Items[e.Index]).Caption != null ? CaptionHeight : 0); };
        list.Dead = delegate (Point p) { int i = list.IndexFromPoint(p); if (i < 0 || i >= list.Items.Count) return true; Rectangle r = list.GetItemRectangle(i); return !r.Contains(p) || ((Conv)list.Items[i]).Caption != null && p.Y < r.Top + CaptionHeight; };
        list.SelectedIndexChanged += delegate { if (!filling && list.SelectedItem != null) Select(((Conv)list.SelectedItem).Id); };
        list.MouseDown += ListMouse;
        Panel foot = new Panel(); foot.Dock = DockStyle.Bottom; foot.Height = Theme.S(58); foot.BackColor = Theme.Side; foot.Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(12));
        set.Dock = DockStyle.Fill; set.LeftAlign = true; set.Click += delegate { OpenSettings(); };
        foot.Controls.Add(set);
        side.Controls.Add(list); side.Controls.Add(brand); side.Controls.Add(foot);

        title.Dock = DockStyle.Top; title.Height = Theme.S(68);
        bo.Hover = Theme.Side;
        bo.Click += delegate { Directory.CreateDirectory(node.Cfg.SaveDir); Process.Start("explorer.exe", "\"" + node.Cfg.SaveDir + "\""); };
        perm.Hover = Theme.Side; perm.Click += delegate { EditGrant(); };
        title.Controls.Add(bo); title.Controls.Add(perm);
        title.Layout += delegate { bo.Location = new Point(title.Width - bo.Width - Theme.S(14), (title.Height - bo.Height) / 2); perm.Location = new Point(bo.Left - perm.Width, bo.Top); title.RightSpace = title.Width - (perm.Visible ? perm.Left : bo.Left); };

        bottom.Dock = DockStyle.Bottom; bottom.BackColor = Theme.Back;
        foreach (RButton b in new RButton[] { bf, bd }) { b.Hover = Theme.Side; b.ForeColor = Theme.Dim; }
        send.Icon = "\uE74A"; send.Size = new Size(Theme.S(38), Theme.S(34));
        bf.Click += delegate { using (OpenFileDialog d = new OpenFileDialog()) { d.Multiselect = true; if (d.ShowDialog(this) == DialogResult.OK) SendPaths(d.FileNames); } };
        bd.Click += delegate { using (FolderBrowserDialog d = new FolderBrowserDialog()) { if (d.ShowDialog(this) == DialogResult.OK) SendPaths(new string[] { d.SelectedPath }); } };
        send.Click += delegate { SendText(); };
        input.Multiline = true; input.BorderStyle = BorderStyle.None; input.Font = Theme.Body; input.BackColor = Theme.Card; input.ForeColor = Theme.Text; input.MaxLength = Node.MaxText;
        input.KeyDown += InputKey;
        input.TextChanged += delegate { PlaceComposer(); };
        card.Corner = 14; card.Controls.Add(input); card.Controls.Add(bf); card.Controls.Add(bd); card.Controls.Add(send);
        card.MouseDown += delegate { input.Focus(); };
        card.Layout += delegate {
            int y = card.Height - bf.Height - Theme.S(8);
            bf.Location = new Point(Theme.S(8), y); bd.Location = new Point(bf.Right + Theme.S(2), y); send.Location = new Point(card.Width - send.Width - Theme.S(9), y);
        };
        bottom.Controls.Add(card);
        bottom.Resize += delegate { PlaceComposer(); };

        view.Dock = DockStyle.Fill; view.MyId = n.Cfg.Id;
        view.OpenRequested += Reveal; view.MenuRequested += ShowMenu;
        view.HasOutput = delegate (ChatMsg m) { return node.HasJob(m.Id); };
        view.ButtonPressed += delegate (ChatMsg m, string id) { if (id == "stop") node.StopControl(m.Id); else if (id == "steps") ShowSteps(m); else ShowOutput(m); };
        bar.Dock = DockStyle.Top; bar.Visible = false;
        bar.Pressed += PressControl; pill.Pressed += PressControl;
        Controls.Add(view); Controls.Add(bottom); Controls.Add(bar); Controls.Add(title); Controls.Add(side);

        foreach (Control c in new Control[] { this, view, input, list }) {
            c.AllowDrop = true;
            c.DragEnter += delegate (object s, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            c.DragDrop += delegate (object s, DragEventArgs e) { string[] f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) SendPaths(f); };
        }

        tray.Icon = Theme.AppIcon(SystemInformation.SmallIconSize); tray.Text = Util.Cut("LanTalk · " + n.Cfg.Name, 60);
        tray.ContextMenuStrip = Theme.Menu();
        tray.ContextMenuStrip.Items.Add("", null, delegate { PressControl(); }); tray.ContextMenuStrip.Items[0].Visible = false;
        tray.ContextMenuStrip.Items.Add("", null, delegate { ShowUp(); });
        tray.ContextMenuStrip.Items.Add("", null, delegate { quitting = true; Close(); });
        ApplyTexts();
        tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowUp(); };
        tray.BalloonTipClicked += delegate { ShowUp(); };

        node.PeersChanged += delegate { Ui(RefreshList); };
        node.MessageAdded += delegate (ChatMsg m) { Ui(delegate { OnAdded(m); }); };
        node.MessageChanged += delegate (ChatMsg m) { Ui(delegate { if (m.Conv == current) view.Touch(m); if (m.From != node.Cfg.Id && m.Kind == "file" && m.State == "ok") Notify(m); }); };
        node.ControlAsked += delegate (ControlAsk a) { Ui(delegate { new AskForm(a).Show(); System.Media.SystemSounds.Exclamation.Play(); }); };
        node.ControlChanged += delegate { Ui(Watch); };
        node.InputPoint += delegate (Point p, bool clicked) {
            if (clicked) { Ui(delegate { ring.At(p); }); return; }
            System.Threading.ManualResetEvent moved = new System.Threading.ManualResetEvent(false);
            Ui(delegate { try { pill.MakeRoom(p); } finally { moved.Set(); } });
            moved.WaitOne(500);
        };
        FormClosed += delegate { edge.Dispose(); ring.Dispose(); };
        node.RestartRequested += delegate { Ui(delegate { quitting = true; Close(); }); };
        clock.Interval = 500; clock.Tick += delegate { Watch(); ticks++; if (ticks % 2 == 0 && Visible && view.Ticking) view.Invalidate(); if (ticks % 60 == 0 && Visible) list.Invalidate(); }; clock.Start();
        Activated += delegate { MarkRead(); Watch(); };
        Deactivate += delegate { Ui(Watch); };
        IntPtr h = Handle; // create it now so BeginInvoke works while still in the tray
        RefreshList(); Select(Node.Ai);
    }
    public void ShowTray() { tray.Visible = true; }
    // ---- being controlled ----
    bool busyShown;
    string grantsShown = "";
    void Watch() {
        if (IsDisposed || quitting) return;
        bool changed = watch.Update(), active = watch.State == ControlWatch.Kind.Active, on = watch.State != ControlWatch.Kind.Off;
        if (changed || on != bar.Visible && Visible) bar.Show(on);
        bool corner = (active || watch.JustPaused) && !(Visible && Form.ActiveForm == this);
        if (changed || corner != pill.Visible) pill.Show(corner);
        edge.Show(watch.Hands != null);
        if (changed) {
            if (on) tray.ContextMenuStrip.Items[0].Text = watch.MenuText;
            tray.ContextMenuStrip.Items[0].Visible = on;
            list.Invalidate();
        }
        if (active != busyShown) { busyShown = active; tray.Icon = active ? Theme.BusyIcon() : Theme.AppIcon(SystemInformation.SmallIconSize); tray.Text = Util.Cut(active ? watch.Head : "LanTalk · " + node.Cfg.Name, 60); }
        string grants; lock (node.L) grants = string.Join("|", node.Cfg.Control.Select(delegate (Grant g) { return g.Id + CapLook.Of(g); }).ToArray()); // one may have run out
        if (grants != grantsShown) { grantsShown = grants; list.Invalidate(); SetTitle(); }
    }
    void PressControl() { watch.Press(); Watch(); bar.Show(watch.State != ControlWatch.Kind.Off); pill.Show(watch.JustPaused && !(Visible && Form.ActiveForm == this)); if (watch.State != ControlWatch.Kind.Off) tray.ContextMenuStrip.Items[0].Text = watch.MenuText; tray.ContextMenuStrip.Items[0].Visible = watch.State != ControlWatch.Kind.Off; list.Invalidate(); }
    void ShowSteps(ChatMsg m) { new OutputForm(Lang.T("鼠标键盘：每一步", "Mouse and keyboard: every step"), delegate { string t = m.Text ?? ""; int i = t.IndexOf('\n'); return i < 0 ? "" : t.Substring(i + 1); }).Show(this); }
    void ShowOutput(ChatMsg m) { new OutputForm(AskForm.Plain((m.Text ?? "").Split('\n')[0]) + Lang.T(" 的输出", ": output"), delegate { return node.JobOutput(m.Id); }).Show(this); }
    // Everything that keeps its text; the rest is written when drawn or opened.
    void ApplyTexts() {
        list.AccessibleName = Lang.T("会话列表", "Conversations");
        set.Text = Lang.T("设置", "Settings"); bo.Text = Lang.T("打开接收目录", "Received files"); perm.Text = Lang.T("控制权限", "Control access");
        bf.Text = Lang.T("文件", "File"); bd.Text = Lang.T("文件夹", "Folder");
        foreach (RButton b in new RButton[] { bo, perm, bf, bd }) b.Size = new Size(b.FitWidth, Theme.S(34));
        bf.AccessibleName = Lang.T("发文件", "Send a file"); bd.AccessibleName = Lang.T("发文件夹", "Send a folder"); send.AccessibleName = Lang.T("发送", "Send");
        tips.SetToolTip(send, Lang.T("发送（回车）", "Send (Enter)")); tips.SetToolTip(bf, Lang.T("发文件，也可以拖进窗口", "Send a file, or drop it on the window")); tips.SetToolTip(bd, Lang.T("发文件夹，也可以拖进窗口", "Send a folder, or drop it on the window"));
        input.AccessibleName = Lang.T("输入消息，回车发送，Shift+回车换行", "Message. Enter sends, Shift+Enter starts a new line");
        tray.ContextMenuStrip.Items[1].Text = Lang.T("打开", "Open"); tray.ContextMenuStrip.Items[2].Text = Lang.T("退出", "Quit");
        title.PerformLayout(); card.PerformLayout(); view.Invalidate();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this, Theme.Side); }
    // Keeps the composer in the same column as the messages and grows it with the text, 2 to 8 lines.
    bool placing;
    readonly int lineHeight = TextRenderer.MeasureText("字g", Theme.Body).Height;
    void PlaceComposer() {
        if (placing || bottom.Width < Theme.S(100)) return;
        placing = true;
        int w = Math.Min(bottom.Width - 2 * Theme.S(22), Theme.S(ChatView.MaxColumn)) + Theme.S(4), px = Theme.S(16);
        input.Width = w - 2 * px; // before counting lines: it decides the wrapping
        int lines = input.TextLength == 0 ? 1 : input.GetLineFromCharIndex(input.TextLength) + 1;
        int h = Math.Max(2, Math.Min(8, lines)) * lineHeight + 4;
        bool grew = h > input.Height;
        bottom.Height = Theme.S(8) + Theme.S(14) + h + Theme.S(48) + Theme.S(18);
        card.SetBounds((bottom.Width - w) / 2, Theme.S(8), w, Theme.S(14) + h + Theme.S(48));
        input.SetBounds(px, Theme.S(14), w - 2 * px, h);
        if (grew && lines <= 8) input.ScrollLines(-lines); // the edit control scrolled before it had room
        placing = false;
    }
    void Ui(MethodInvoker a) {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch (InvalidOperationException) { } // closing
    }

    // ---- conversations ----
    void RefreshList() {
        List<Conv> l = new List<Conv>();
        Conv ai = new Conv(); ai.Id = Node.Ai; ai.Title = Lang.T("Claude 协作", "Claude channel"); ai.Online = true; l.Add(ai);
        Conv all = new Conv(); all.Id = Node.All; all.Title = Lang.T("所有电脑", "All PCs"); all.Online = true; l.Add(all);
        foreach (Peer p in node.Peers().OrderBy(delegate (Peer x) { return x.Name; })) { Conv c = new Conv(); c.Id = p.Id; c.Title = p.Name; c.Device = p.Device; c.Online = p.Online; l.Add(c); }
        ai.Caption = Lang.T("频道", "Channels");
        if (l.Count > 2) l[2].Caption = Lang.T("电脑", "PCs");
        brand.Pc = node.Cfg.Name; brand.Invalidate();
        if (!l.Exists(delegate (Conv c) { return c.Id == current; })) current = Node.Ai;
        filling = true;
        list.BeginUpdate(); list.Items.Clear();
        foreach (Conv c in l) list.Items.Add(c);
        list.SelectedIndex = l.FindIndex(delegate (Conv c) { return c.Id == current; });
        list.EndUpdate();
        filling = false;
        SetTitle();
    }
    public void Select(string id) {
        current = id;
        for (int i = 0; i < list.Items.Count; i++) if (((Conv)list.Items[i]).Id == id && list.SelectedIndex != i) { filling = true; list.SelectedIndex = i; filling = false; }
        SetTitle();
        view.SetMessages(node.History(id, 500), true);
        MarkRead();
        input.Focus();
    }
    void SetTitle() {
        Conv c = list.SelectedItem as Conv;
        if (c == null) return;
        int on = node.Peers().Count(delegate (Peer p) { return p.Online; });
        title.Text = c.Title;
        title.Sub = c.Id == Node.Ai ? Lang.T("各电脑的 Claude Code 在这里交流，你也可以发言 · {0} 台在线", "Where Claude Code on your PCs talks. You can join in · {0} online", on)
            : c.Id == Node.All ? Lang.T("{0} 台在线", "{0} online", on) : c.Online ? Lang.T("在线", "Online") : Lang.T("离线，消息会在对方上线后送达", "Offline. Messages arrive when it is back");
        input.Hint = Lang.T("发消息到 {0}", "Message {0}", c.Title);
        bool group = Node.IsGroup(c.Id);
        perm.Visible = !group;
        perm.ForeColor = !group && node.GrantFor(c.Id) != null ? Theme.AccentText : Theme.Soft;
        title.PerformLayout(); title.Invalidate();
    }
    void EditGrant() {
        Conv c = list.SelectedItem as Conv;
        Peer p = c == null ? null : node.Find(c.Id);
        if (p == null) return;
        perm.Enabled = false;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { // asking the other PC may take seconds
            string key = null, problem = "";
            try { key = node.FetchKey(p.Id); } catch (Exception e) { problem = e is InvalidOperationException ? e.Message : Lang.T("连不上（{0}）", "can't connect ({0})", e.Message); }
            Ui(delegate {
                perm.Enabled = true;
                using (ControlForm f = new ControlForm(node, p, key, problem)) if (f.ShowDialog(this) == DialogResult.OK) RefreshList();
            });
        });
    }
    void MarkRead() { if (Visible && unread.Remove(current)) list.Invalidate(); }
    static int RowHeight { get { return Theme.S(58); } }
    static int CaptionHeight { get { return Theme.S(36); } }
    void DrawConv(object s, DrawItemEventArgs e) {
        if (e.Index < 0) return;
        Conv c = (Conv)list.Items[e.Index];
        Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (Brush b = new SolidBrush(Theme.Side)) g.FillRectangle(b, e.Bounds);
        int top = e.Bounds.Y;
        if (c.Caption != null) {
            TextRenderer.DrawText(g, c.Caption, Theme.Small, new Rectangle(e.Bounds.X + Theme.S(20), top + Theme.S(10), e.Bounds.Width - Theme.S(30), CaptionHeight - Theme.S(12)), Theme.Dim, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            top += CaptionHeight;
        }
        Rectangle row = new Rectangle(e.Bounds.X + Theme.S(10), top + Theme.S(2), e.Bounds.Width - Theme.S(20), RowHeight - Theme.S(4));
        if (sel) Theme.Fill(g, row, Theme.S(20), Theme.SideSel, Color.Empty);
        int d = Theme.S(36);
        Rectangle av = new Rectangle(row.X + Theme.S(10), row.Y + (row.Height - d) / 2, d, d);
        bool group = Node.IsGroup(c.Id), acting = !group && watch.State == ControlWatch.Kind.Active && watch.Includes(c.Id);
        TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        // Channels are coloured tiles, PCs white ones, dimmed while offline.
        Color fill = c.Id == Node.Ai ? Theme.Clay : group ? Theme.Olive : c.Online ? Theme.Card : Color.Empty, ink = group ? Color.White : c.Online ? Theme.Soft : Theme.Dim;
        Theme.Fill(g, new Rectangle(av.X, av.Y, d - 1, d - 1), Theme.S(20), fill, group ? Color.Empty : c.Online ? Theme.Line : Theme.Sand);
        if (c.Id == Node.Ai) Theme.DrawPrompt(g, av, ink);
        else if (group) TextRenderer.DrawText(g, "\uE716", Theme.GlyphBig, av, ink, mid);
        else if (c.Device != null) { int q = Theme.S(24); Theme.DrawDevice(g, c.Device, new RectangleF(av.X + (d - q) / 2, av.Y + (d - q) / 2, q, q), ink); }
        else TextRenderer.DrawText(g, c.Title.Substring(0, 1).ToUpperInvariant(), Theme.Heading, av, ink, mid);
        if (!group && c.Online) { // status dot on the corner of the tile, clear of the icon
            Rectangle dot = new Rectangle(av.Right - Theme.S(9), av.Bottom - Theme.S(9), Theme.S(12), Theme.S(12));
            using (Brush b = new SolidBrush(sel ? Theme.SideSel : Theme.Side)) g.FillEllipse(b, dot);
            using (Brush b = new SolidBrush(acting ? Theme.Accent : Theme.Olive)) g.FillEllipse(b, Rectangle.Inflate(dot, -Theme.S(2), -Theme.S(2)));
        }
        Grant grant = group ? null : node.GrantFor(c.Id);
        int n;
        bool mark = unread.TryGetValue(c.Id, out n) && n > 0;
        int x = av.Right + Theme.S(12), w = row.Right - x - Theme.S(mark ? 44 : 10);
        TextFormatFlags left = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, c.Title, mark ? Theme.Bold : Theme.Body, group ? new Rectangle(x, row.Y, w, row.Height) : new Rectangle(x, row.Y + Theme.S(7), w, Theme.S(23)), Theme.Text, left);
        if (!group) TextRenderer.DrawText(g, acting ? Lang.T("正在控制本机", "Controlling this PC") : grant != null && Lang.En && c.Online ? char.ToUpperInvariant(CapLook.Of(grant)[0]) + CapLook.Of(grant).Substring(1) : (c.Online ? Lang.T("在线", "Online") : Lang.T("离线", "Offline")) + (grant == null ? "" : " · " + CapLook.Of(grant)), Theme.Small, new Rectangle(x, row.Y + Theme.S(29), w, Theme.S(20)), grant != null ? Theme.AccentText : c.Online ? Theme.Online : Theme.Dim, left);
        if (mark) {
            Rectangle r = new Rectangle(row.Right - Theme.S(38), row.Y + (row.Height - Theme.S(20)) / 2, Theme.S(28), Theme.S(20));
            Theme.Fill(g, r, r.Height, Theme.AccentDown, Color.Empty);
            TextRenderer.DrawText(g, n > 99 ? "99+" : n.ToString(), Theme.Small, r, Color.White, mid);
        }
    }
    void ListMouse(object s, MouseEventArgs e) {
        if (e.Button != MouseButtons.Right) return;
        int i = list.IndexFromPoint(e.Location);
        if (i < 0 || Node.IsGroup(((Conv)list.Items[i]).Id)) return;
        Conv c = (Conv)list.Items[i];
        ContextMenuStrip m = Theme.Menu();
        m.Items.Add(Lang.T("移除这台电脑", "Remove this PC"), null, delegate {
            if (MessageBox.Show(this, Lang.T("把“{0}”从列表移除？还没送达的消息会取消，聊天记录保留在磁盘上。它再次上线时会重新出现。", "Remove \"{0}\" from the list? Messages not delivered yet are cancelled; the chat history stays on disk. It shows up again when it comes online.", c.Title), "LanTalk", MessageBoxButtons.OKCancel) == DialogResult.OK) { node.ForgetPeer(c.Id); RefreshList(); Select(current); }
        });
        m.Show(list, e.Location);
    }

    // ---- messages ----
    void OnAdded(ChatMsg m) {
        if (m.Conv == current) view.SetMessages(node.History(current, 500), m.From == node.Cfg.Id);
        if (m.From != node.Cfg.Id && m.Kind == "text") Notify(m);
    }
    void Notify(ChatMsg m) {
        if (Visible && ContainsFocus && m.Conv == current) return;
        if (!(Visible && m.Conv == current && Form.ActiveForm == this)) { int n; unread.TryGetValue(m.Conv, out n); unread[m.Conv] = n + 1; list.Invalidate(); }
        if (tray.Visible) tray.ShowBalloonTip(4000, (string.IsNullOrEmpty(m.Agent) ? "" : m.Agent + " @ ") + m.FromName, m.Kind == "file" ? Lang.T("[文件] ", "[File] ") + m.Text : Util.Cut(m.Text + " ", 120), ToolTipIcon.None);
        if (Visible) FlashWindow(Handle, true);
    }
    void Try(Action a) {
        try { a(); }
        catch (InvalidOperationException e) { MessageBox.Show(this, e.Message, "LanTalk"); }
        catch (IOException e) { MessageBox.Show(this, e.Message, "LanTalk"); }
        catch (UnauthorizedAccessException e) { MessageBox.Show(this, e.Message, "LanTalk"); }
    }
    void SendText() {
        if (input.Text.Trim().Length == 0) return;
        Try(delegate { node.SendText(current, input.Text.TrimEnd()); input.Clear(); });
        input.Focus();
    }
    void SendPaths(string[] paths) {
        Conv c = (Conv)list.SelectedItem;
        if (paths.Length > 20 && MessageBox.Show(this, Lang.T("要把 {0} 个项目发给“{1}”吗？", "Send {0} items to \"{1}\"?", paths.Length, c.Title), "LanTalk", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        foreach (string p in paths) { string f = p; Try(delegate { node.SendPath(current, f); }); }
    }
    void InputKey(object s, KeyEventArgs e) {
        if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control) { e.SuppressKeyPress = true; SendText(); }
        else if (e.Control && e.KeyCode == Keys.V) {
            if (Clipboard.ContainsFileDropList()) { e.SuppressKeyPress = true; SendPaths(Clipboard.GetFileDropList().Cast<string>().ToArray()); }
            else if (!Clipboard.ContainsText() && Clipboard.ContainsImage()) {
                e.SuppressKeyPress = true;
                Try(delegate {
                    string d = Path.Combine(dataDir, "pasted"); Directory.CreateDirectory(d);
                    string f = Path.Combine(d, Lang.T("截图 ", "Screenshot ") + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    using (Image i = Clipboard.GetImage()) i.Save(f, ImageFormat.Png);
                    node.SendPath(current, f);
                });
            }
        }
    }
    void Reveal(ChatMsg m) {
        if (m.Path != null && (File.Exists(m.Path) || Directory.Exists(m.Path))) Process.Start("explorer.exe", "/select,\"" + m.Path + "\"");
        else if (m.State == "ok") MessageBox.Show(this, Lang.T("文件已被移动或删除。", "The file was moved or deleted."), "LanTalk");
    }
    void ShowMenu(ChatMsg m, Point at) {
        ContextMenuStrip c = Theme.Menu();
        if (m.Kind == "file") {
            if (m.State == "ok" || m.From == node.Cfg.Id) { c.Items.Add(Lang.T("打开所在文件夹", "Open its folder"), null, delegate { Reveal(m); }); c.Items.Add(Lang.T("复制路径", "Copy path"), null, delegate { if (!string.IsNullOrEmpty(m.Path)) Clipboard.SetText(m.Path); }); }
        } else {
            c.Items.Add(Lang.T("复制", "Copy"), null, delegate { if (!string.IsNullOrEmpty(m.Text)) Clipboard.SetText(m.Text); });
            Match u = m.Kind == "run" ? Match.Empty : Regex.Match(m.Text ?? "", "https?://[^\\s\"<>]+");
            if (u.Success) c.Items.Add(Lang.T("打开链接", "Open link"), null, delegate { Process.Start(u.Value); });
        }
        if (m.Op == "shot" && m.Path != null) c.Items.Add(Lang.T("打开所在文件夹", "Open its folder"), null, delegate { Reveal(m); });
        if (Node.IsJob(m.Op) && m.From != node.Cfg.Id && node.HasJob(m.Id)) c.Items.Add(Lang.T("看输出", "Output"), null, delegate { ShowOutput(m); });
        if (m.Kind == "run" && m.State == "run") c.Items.Add(Lang.T("停止", "Stop"), null, delegate { node.StopControl(m.Id); });
        if (m.From == node.Cfg.Id && m.Pending.Count > 0) c.Items.Add(Lang.T("取消发送", "Cancel sending"), null, delegate { node.Cancel(m); });
        if (m.From == node.Cfg.Id && (m.State == "fail" || m.State == "cancel")) c.Items.Add(Lang.T("重新发送", "Send again"), null, delegate { node.Retry(m); });
        if (c.Items.Count > 0) c.Show(view, at);
    }

    // ---- window ----
    public void ShowUp() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); MarkRead(); }
    void OpenSettings() {
        using (SettingsForm f = new SettingsForm(node, false)) { if (f.ShowDialog(this) == DialogResult.OK) Text = "LanTalk · " + node.Cfg.Name; ApplyTexts(); RefreshList(); }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!quitting && e.CloseReason == CloseReason.UserClosing) {
            e.Cancel = true; Hide();
            if (!toldTray) { toldTray = true; tray.ShowBalloonTip(3000, "LanTalk", Lang.T("仍在后台运行，点这里的图标可以打开，右键可以退出。", "Still running. Click this icon to open it, right-click it to quit."), ToolTipIcon.None); }
            return;
        }
        tray.Visible = false; clock.Stop(); pill.Close();
        base.OnFormClosing(e);
    }
}

// Doubles as the first-run dialog.
public class SettingsForm : Form {
    const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    readonly Node node;
    readonly TextBox name = new TextBox(), code = new TextBox(), dir = new TextBox(), manual = new TextBox();
    readonly RCheck auto = new RCheck();
    readonly string oldCode;
    readonly bool first;
    string lang, device; // device: a kind, null = detect

    public SettingsForm(Node n, bool firstRun) {
        node = n; oldCode = n.Code; first = firstRun; lang = n.Cfg.Lang; device = n.Cfg.Device;
        Font = Theme.Body; BackColor = Theme.Back; ForeColor = Theme.Text; Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        name.Text = n.Cfg.Name; code.Text = oldCode ?? ""; dir.Text = n.Cfg.SaveDir; manual.Text = string.Join(", ", n.Cfg.Manual.ToArray());
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) auto.Checked = k != null && k.GetValue("LanTalk") != null;
        Build();
        FormClosed += delegate { Lang.Set(node.Cfg.Lang); }; // a choice that wasn't saved is undone
    }
    // Lays the dialog out in the current language; the boxes keep what was typed.
    void Build() {
        SuspendLayout();
        foreach (Control t in new Control[] { name, code, dir, manual, auto }) if (t.Parent != null) t.Parent.Controls.Remove(t);
        foreach (Control c in Controls.Cast<Control>().ToArray()) { Controls.Remove(c); c.Dispose(); }
        Text = first ? Lang.T("LanTalk 首次设置", "LanTalk setup") : Lang.T("LanTalk 设置", "LanTalk settings");
        Label head = new Label(); head.Text = first ? Lang.T("先设置一下这台电脑", "Set up this PC") : Lang.T("设置", "Settings"); head.Font = Theme.Greeting; head.TextAlign = ContentAlignment.MiddleLeft;
        head.SetBounds(Theme.S(17), Theme.S(16), Theme.S(270), Theme.S(46)); Controls.Add(head);
        string[,] langs = { { "auto", Lang.T("跟随系统", "System") }, { "zh", "中文" }, { "en", "English" } };
        int lx = Theme.S(514);
        for (int i = 2; i >= 0; i--) {
            string id = langs[i, 0];
            RButton b = RButton.Outline(langs[i, 1]); b.Font = Theme.Small; b.AccessibleName = Lang.T("语言：", "Language: ") + langs[i, 1];
            if (id == lang) { b.Fill = b.Hover = Theme.SideSel; b.Border = Theme.LineFocus; }
            b.Size = new Size(b.FitWidth, Theme.S(30)); lx -= b.Width; b.Location = new Point(lx, Theme.S(25)); lx -= Theme.S(6);
            b.Click += delegate { lang = id; Lang.Set(id); BeginInvoke((MethodInvoker)Build); }; // not now: Build disposes this button
            Controls.Add(b);
        }
        int y = Theme.S(76);
        Row(Lang.T("本机名称（别的电脑看到的名字）", "Name of this PC (what the others see)"), name, 490, ref y);
        Label kl = new Label(); kl.Text = Lang.T("本机类型（别的电脑看到的图标）", "Type of this PC (the icon the others see)"); kl.ForeColor = Theme.Soft; kl.SetBounds(Theme.S(21), y, Theme.S(490), Theme.S(24)); Controls.Add(kl);
        string[] kinds = { null, "tower", "mini", "laptop" }, kindNames = { Lang.T("自动", "Auto"), Lang.T("大机箱", "Big case"), Lang.T("小机箱", "Small case"), Lang.T("笔记本", "Laptop") };
        Panel kp = new Panel(); kp.SetBounds(Theme.S(24), y + Theme.S(26), Theme.S(490), Theme.S(40));
        for (int i = 0; i < kinds.Length; i++) {
            string kind = kinds[i];
            DeviceChoice b = new DeviceChoice(kindNames[i], kind ?? LanTalk.Device.Detected);
            b.SetBounds(Theme.S(i * 124), 0, Theme.S(118), Theme.S(40)); b.Checked = kind == device;
            b.CheckedChanged += delegate { if (b.Checked) device = kind; };
            kp.Controls.Add(b);
        }
        Controls.Add(kp); y += Theme.S(80);
        RButton gen = RButton.Outline(Lang.T("生成新的", "New code")), copy = RButton.Outline(Lang.T("复制", "Copy"));
        Row(Lang.T("配对码（每台电脑必须相同）", "Pairing code (the same on every PC)"), code, 300, ref y);
        Control box = code.Parent;
        gen.SetBounds(box.Right + Theme.S(8), box.Top, Theme.S(104), box.Height); copy.SetBounds(gen.Right + Theme.S(8), box.Top, Theme.S(70), box.Height);
        gen.Click += delegate { code.Text = Crypto.NewCode(); };
        copy.Click += delegate { if (code.Text.Length > 0) Clipboard.SetText(code.Text); };
        Controls.Add(gen); Controls.Add(copy);
        Label tip = new Label(); tip.Text = Lang.T("第一台点“生成新的”，其余的填同一个配对码。配对码等于钥匙，不要外传。", "First PC: New code. Other PCs: enter the same one. It is a key, so keep it private."); tip.ForeColor = Theme.Dim; tip.Font = Theme.Small;
        y -= Theme.S(8); tip.SetBounds(Theme.S(21), y, Theme.S(490), Theme.S(24)); Controls.Add(tip); y += Theme.S(36);
        Row(Lang.T("收到的文件存到", "Received files go to"), dir, 490, ref y);
        Row(Lang.T("手动添加电脑 IP（自动找不到时才填，逗号分隔）", "IPs of PCs that don't show up by themselves (comma-separated)"), manual, 490, ref y);
        Label fp = new Label(); fp.Text = Lang.T("本机校验码（别的电脑给本机控制权限时核对）：", "Check code of this PC (for control access): ") + Node.Fingerprint(node.PublicKey); fp.ForeColor = Theme.Dim; fp.Font = Theme.Small;
        fp.SetBounds(Theme.S(21), y - Theme.S(4), Theme.S(500), Theme.S(24)); Controls.Add(fp); y += Theme.S(26);
        auto.Text = Lang.T("开机自动启动（缩到托盘）", "Start with Windows (in the tray)"); auto.SetBounds(Theme.S(22), y, Theme.S(400), Theme.S(30)); Controls.Add(auto);
        y += Theme.S(46);
        RButton ok = RButton.Primary(Lang.T("确定", "OK")), cancel = RButton.Outline(Lang.T("取消", "Cancel"));
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(Theme.S(314), y, Theme.S(96), Theme.S(38)); ok.SetBounds(Theme.S(418), y, Theme.S(96), Theme.S(38));
        ok.Click += delegate { Save(); };
        Controls.Add(cancel); Controls.Add(ok); AcceptButton = ok; CancelButton = cancel;
        ClientSize = new Size(Theme.S(538), ok.Bottom + Theme.S(22));
        ResumeLayout();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this, Theme.Back); }
    void Row(string text, TextBox box, int width, ref int y) {
        Label l = new Label(); l.Text = text; l.ForeColor = Theme.Soft; l.SetBounds(Theme.S(21), y, Theme.S(490), Theme.S(24)); Controls.Add(l);
        CardPanel p = new CardPanel(); p.Corner = 9; p.SetBounds(Theme.S(24), y + Theme.S(26), Theme.S(width), Theme.S(38));
        box.BorderStyle = BorderStyle.None; box.Font = Theme.Body; box.BackColor = Theme.Card; box.ForeColor = Theme.Text; box.AccessibleName = text;
        box.SetBounds(Theme.S(12), (p.Height - box.PreferredHeight) / 2, p.Width - Theme.S(24), box.PreferredHeight);
        p.Controls.Add(box); p.MouseDown += delegate { box.Focus(); };
        Controls.Add(p);
        y += Theme.S(78);
    }
    void Save() {
        string c = Crypto.NormalizeCode(code.Text), nm = name.Text.Trim(), d = dir.Text.Trim();
        string[] ips = manual.Text.Split(new char[] { ',', '，', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        string err = nm.Length == 0 ? Lang.T("请填写本机名称。", "Enter a name for this PC.") : nm.Length > 40 ? Lang.T("名称太长。", "The name is too long.") : c == null ? Lang.T("配对码应是 16 位（不含 I、O、0、1），例如 ABCD-EFGH-JKLM-NPQR。", "A pairing code has 16 characters (no I, O, 0 or 1), like ABCD-EFGH-JKLM-NPQR.") : null;
        if (err == null && ips.Any(delegate (string s) { return Node.ParseEndpoint(s) == null; })) err = Lang.T("IP 地址格式不对，例如 192.168.1.20。", "That is not an IP address. Example: 192.168.1.20.");
        if (err == null) try { if (!Path.IsPathRooted(d)) throw new ArgumentException(Lang.T("请填写完整的文件夹路径。", "Enter the complete folder path.")); Directory.CreateDirectory(d); } catch (Exception e) { err = Lang.T("接收文件夹不能用：", "The folder for received files can't be used: ") + e.Message; }
        if (err != null) { MessageBox.Show(this, err, "LanTalk"); return; }
        lock (node.L) { node.Cfg.Name = nm; node.Cfg.SaveDir = d; node.Cfg.Manual = ips.ToList(); node.Cfg.Lang = lang; node.Cfg.Device = device; }
        if (c != oldCode) { node.ClearGrants(); node.SetCode(c); } else node.SaveConfig(); // a new code means a new circle of PCs
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey)) {
            if (auto.Checked) k.SetValue("LanTalk", "\"" + Application.ExecutablePath + "\" --tray");
            else if (k.GetValue("LanTalk") != null) k.DeleteValue("LanTalk");
        }
        DialogResult = DialogResult.OK;
    }
}

public static class Program {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    public static void InitUi() {
        SetProcessDPIAware();
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Theme.InitMenus();
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
    }
    [STAThread] public static int Main(string[] args) {
        string data = Ipc.DefaultData; string updated = null; IPAddress bind = IPAddress.Any; bool trayOnly = false;
        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--tray") trayOnly = true;
            else if (args[i].StartsWith("--updated=")) updated = args[i].Substring(10);
            else if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
            else if (args[i] == "--bind" && i + 1 < args.Length) bind = IPAddress.Parse(args[++i]);
        }
        InitUi();
        string key = Ipc.PipeName(data);
        bool created;
        using (Mutex one = new Mutex(true, "Local\\" + key, out created))
        using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + key + "-show")) {
            if (!created) { show.Set(); return 0; } // already running: bring it up
            Node node = new Node(data, bind); node.LaunchArgs = args;
            Lang.Set(node.Cfg.Lang);
            if (node.Code == null) {
                using (SettingsForm f = new SettingsForm(node, true)) { f.ShowInTaskbar = true; if (f.ShowDialog() != DialogResult.OK) return 0; }
                trayOnly = false;
            }
            try { node.Start(); }
            catch (SocketException e) { if (updated != null) return 3; MessageBox.Show(Lang.T("端口 {0} 无法使用：{1}", "Port {0} can't be used: {1}", node.Cfg.Port, e.Message), "LanTalk"); return 2; }
            new IpcServer(node, data).Start();
            MainForm form = new MainForm(node, data);
            form.ShowTray();
            if (updated != null) try { File.WriteAllText(updated, "ok"); } catch (IOException) { } // the update script waits for it
            Thread t = new Thread(delegate () { while (true) { show.WaitOne(); try { form.BeginInvoke((MethodInvoker)form.ShowUp); } catch (InvalidOperationException) { return; } } });
            t.IsBackground = true; t.Start();
            form.FormClosed += delegate { Application.ExitThread(); };
            if (!trayOnly) form.Show();
            Application.Run();
            node.Dispose();
        }
        return 0;
    }
}
}
