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
    class Item { public ChatMsg M; public Rectangle Bubble, Head; public int Top, Height; public bool Mine; public Image Thumb; }
    const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    public const int MaxColumn = 860; // wide windows keep a readable column in the middle
    readonly SlimBar bar = new SlimBar();
    readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>();
    List<Item> items = new List<Item>();
    List<ChatMsg> msgs = new List<ChatMsg>();
    public string MyId;
    public event Action<ChatMsg> OpenRequested;
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
        if (m.Kind == "file" && m.State == "ok" && it.Thumb == null && IsImage(m)) Relayout(true); else Invalidate();
    }
    static bool IsImage(ChatMsg m) { return m.Kind == "file" && m.State == "ok" && m.Path != null && Regex.IsMatch(m.Path, "\\.(png|jpe?g|bmp|gif)$", RegexOptions.IgnoreCase); }
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
            else s = Theme.Measure(TextOf(m), m.Kind == "run" ? Theme.Mono : Theme.Body, new Size(max - 2 * pad, int.MaxValue), Flags);
            int bw = s.Width + 2 * pad + 2, bh = s.Height + 2 * Theme.S(10);
            int x = it.Mine ? x0 + w - bw : x0;
            it.Head = new Rectangle(x0 + Theme.S(2), y, w - Theme.S(4), Theme.S(20));
            it.Bubble = new Rectangle(x, y + Theme.S(24), bw, bh);
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
            string status = m.Kind == "run" ? (it.Mine ? "远程执行" : "控制了这台电脑") + " · " + (m.State == "run" ? "运行中（右键停止）" : m.Error) : it.Mine ? Status(m) : (m.State == "fail" ? "接收失败：" + m.Error : "");
            string head = (it.Mine ? (agent ? m.Agent + "（本机）" : "") : (agent ? m.Agent + " @ " + m.FromName : m.FromName)) + "  " + Stamp(m.LocalTime) + (status != "" ? "  · " + status : "");
            Rectangle hr = it.Head; hr.Offset(0, -off);
            TextRenderer.DrawText(g, head.Trim(), Theme.Small, hr, m.State == "fail" ? Theme.Bad : agent ? Theme.AccentText : Theme.Dim, TextFormatFlags.NoPadding | (it.Mine ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Theme.Fill(g, b, Theme.S(24), m.Kind == "run" ? Theme.Side : agent ? Theme.AgentFill : it.Mine ? Theme.Mine : Theme.Theirs, m.Kind == "run" ? (m.State == "run" ? Theme.Accent : Theme.Line) : agent ? Theme.AgentLine : it.Mine ? Color.Empty : Theme.Line);
            Rectangle inner = Rectangle.Inflate(b, -Theme.S(14), -Theme.S(10));
            if (m.Kind != "file") { TextRenderer.DrawText(g, TextOf(m), m.Kind == "run" ? Theme.Mono : Theme.Body, inner, Theme.Text, Flags); continue; }
            TextFormatFlags one = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            if (it.Thumb != null) {
                g.DrawImage(it.Thumb, inner.X, inner.Y, it.Thumb.Width, it.Thumb.Height);
                TextRenderer.DrawText(g, m.Text, Theme.Small, new Rectangle(inner.X, inner.Bottom - Theme.S(18), inner.Width, Theme.S(18)), Theme.Dim, one | TextFormatFlags.EndEllipsis);
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
            TextRenderer.DrawText(g, "还没有消息", Theme.Greeting, new Rectangle(0, c - Theme.S(44), ClientSize.Width, Theme.S(44)), Theme.Text, mid);
            TextRenderer.DrawText(g, "写点什么，或者直接把文件、文件夹拖进来。", Theme.Body, new Rectangle(0, c + Theme.S(4), ClientSize.Width, Theme.S(28)), Theme.Dim, mid);
        }
    }
    static string TextOf(ChatMsg m) { string t = m.Text ?? ""; return m.Kind != "run" ? t : t.Length <= 1500 ? AskForm.Plain(t) : AskForm.Plain(t.Substring(0, 1500)) + "…"; }
    static string Stamp(DateTime t) { return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("M-d HH:mm"); }
    static string Status(ChatMsg m) {
        switch (m.State) {
            case "queued": return "等待对方上线";
            case "sending": return "发送中";
            case "fail": return "失败（右键重试） " + m.Error;
            case "cancel": return "已取消";
        }
        return "";
    }
    Item Hit(Point p) { p.Y += bar.Visible ? bar.Value : 0; return items.Find(delegate (Item i) { return i.Bubble.Contains(p); }); }
    protected override void OnMouseDown(MouseEventArgs e) {
        Focus();
        Item it = Hit(e.Location);
        if (it != null && e.Button == MouseButtons.Right && MenuRequested != null) MenuRequested(it.M, e.Location);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { Item it = Hit(e.Location); if (it != null && it.M.Kind == "file" && OpenRequested != null) OpenRequested(it.M); }
    protected override void OnMouseMove(MouseEventArgs e) { Item it = Hit(e.Location); Cursor = it != null && it.M.Kind == "file" ? Cursors.Hand : Cursors.Default; }
}

public class MainForm : Form {
    class Conv { public string Id, Title; public bool Online; public override string ToString() { return Title; } }
    readonly Node node;
    readonly string dataDir;
    readonly ListBox list = new ListBox();
    readonly ChatView view = new ChatView();
    readonly HeaderBar title = new HeaderBar();
    readonly HintBox input = new HintBox();
    readonly Panel bottom = new Panel();
    readonly CardPanel card = new CardPanel();
    readonly RButton perm = new RButton("控制权限", "\uE72E");
    readonly Dictionary<string, DateTime> toldControl = new Dictionary<string, DateTime>();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Dictionary<string, int> unread = new Dictionary<string, int>();
    string current = Node.Ai;
    bool quitting, toldTray, filling;

    [DllImport("user32.dll")] static extern bool FlashWindow(IntPtr h, bool invert);

    public MainForm(Node n, string data) {
        node = n; dataDir = data;
        Text = "LanTalk · " + n.Cfg.Name;
        Icon = Theme.AppIcon(); Font = Theme.Body; BackColor = Theme.Back; KeyPreview = true;
        ClientSize = new Size(Theme.S(900), Theme.S(620)); MinimumSize = new Size(Theme.S(620), Theme.S(420));
        StartPosition = FormStartPosition.CenterScreen;

        Panel side = new Panel(); side.Dock = DockStyle.Left; side.Width = Theme.S(236); side.BackColor = Theme.Side; side.Padding = new Padding(0, 0, 1, 0);
        side.Paint += delegate (object s, PaintEventArgs e) { using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
        BrandBar brand = new BrandBar(); brand.Dock = DockStyle.Top; brand.Height = Theme.S(68);
        list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.BackColor = Theme.Side; list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = Theme.S(60); list.IntegralHeight = false;
        list.AccessibleName = "会话列表";
        list.DrawItem += DrawConv;
        list.SelectedIndexChanged += delegate { if (!filling && list.SelectedItem != null) Select(((Conv)list.SelectedItem).Id); };
        list.MouseDown += ListMouse;
        Panel foot = new Panel(); foot.Dock = DockStyle.Bottom; foot.Height = Theme.S(58); foot.BackColor = Theme.Side; foot.Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(12));
        RButton set = new RButton("设置", "\uE713"); set.Dock = DockStyle.Fill; set.LeftAlign = true; set.Click += delegate { OpenSettings(); };
        foot.Controls.Add(set);
        side.Controls.Add(list); side.Controls.Add(brand); side.Controls.Add(foot);

        title.Dock = DockStyle.Top; title.Height = Theme.S(68);
        RButton bo = new RButton("打开接收目录", "\uE838"); bo.Hover = Theme.Side; bo.Size = new Size(bo.FitWidth, Theme.S(34));
        bo.Click += delegate { Directory.CreateDirectory(node.Cfg.SaveDir); Process.Start("explorer.exe", "\"" + node.Cfg.SaveDir + "\""); };
        perm.Hover = Theme.Side; perm.Size = new Size(perm.FitWidth, Theme.S(34)); perm.Click += delegate { EditGrant(); };
        title.Controls.Add(bo); title.Controls.Add(perm);
        title.Layout += delegate { bo.Location = new Point(title.Width - bo.Width - Theme.S(14), (title.Height - bo.Height) / 2); perm.Location = new Point(bo.Left - perm.Width, bo.Top); title.RightSpace = title.Width - (perm.Visible ? perm.Left : bo.Left); };

        bottom.Dock = DockStyle.Bottom; bottom.BackColor = Theme.Back;
        RButton bf = new RButton("文件", "\uE723"), bd = new RButton("文件夹", "\uE8B7"), send = RButton.Primary("");
        foreach (RButton b in new RButton[] { bf, bd }) { b.Hover = Theme.Side; b.ForeColor = Theme.Dim; b.Size = new Size(b.FitWidth, Theme.S(34)); }
        bf.AccessibleName = "发文件"; bd.AccessibleName = "发文件夹"; send.AccessibleName = "发送";
        send.Icon = "\uE74A"; send.Size = new Size(Theme.S(38), Theme.S(34));
        ToolTip tips = new ToolTip(); tips.SetToolTip(send, "发送（回车）"); tips.SetToolTip(bf, "发文件，也可以拖进窗口"); tips.SetToolTip(bd, "发文件夹，也可以拖进窗口");
        bf.Click += delegate { using (OpenFileDialog d = new OpenFileDialog()) { d.Multiselect = true; if (d.ShowDialog(this) == DialogResult.OK) SendPaths(d.FileNames); } };
        bd.Click += delegate { using (FolderBrowserDialog d = new FolderBrowserDialog()) { if (d.ShowDialog(this) == DialogResult.OK) SendPaths(new string[] { d.SelectedPath }); } };
        send.Click += delegate { SendText(); };
        input.Multiline = true; input.BorderStyle = BorderStyle.None; input.Font = Theme.Body; input.BackColor = Theme.Card; input.ForeColor = Theme.Text; input.MaxLength = Node.MaxText; input.AccessibleName = "输入消息，回车发送，Shift+回车换行";
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
        Controls.Add(view); Controls.Add(bottom); Controls.Add(title); Controls.Add(side);

        foreach (Control c in new Control[] { this, view, input, list }) {
            c.AllowDrop = true;
            c.DragEnter += delegate (object s, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            c.DragDrop += delegate (object s, DragEventArgs e) { string[] f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) SendPaths(f); };
        }

        tray.Icon = Theme.AppIcon(SystemInformation.SmallIconSize); tray.Text = Util.Cut("LanTalk · " + n.Cfg.Name, 60);
        tray.ContextMenuStrip = Theme.Menu();
        tray.ContextMenuStrip.Items.Add("打开", null, delegate { ShowUp(); });
        tray.ContextMenuStrip.Items.Add("退出", null, delegate { quitting = true; Close(); });
        tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowUp(); };
        tray.BalloonTipClicked += delegate { ShowUp(); };

        node.PeersChanged += delegate { Ui(RefreshList); };
        node.MessageAdded += delegate (ChatMsg m) { Ui(delegate { OnAdded(m); }); };
        node.MessageChanged += delegate (ChatMsg m) { Ui(delegate { if (m.Conv == current) view.Touch(m); if (m.From != node.Cfg.Id && m.Kind == "file" && m.State == "ok") Notify(m); }); };
        node.ControlAsked += delegate (ControlAsk a) { Ui(delegate { new AskForm(a).Show(); System.Media.SystemSounds.Exclamation.Play(); }); };
        Activated += delegate { MarkRead(); };
        IntPtr h = Handle; // create it now so BeginInvoke works while still in the tray
        RefreshList(); Select(Node.Ai);
    }
    public void ShowTray() { tray.Visible = true; }
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
        Conv ai = new Conv(); ai.Id = Node.Ai; ai.Title = "Claude 协作"; ai.Online = true; l.Add(ai);
        Conv all = new Conv(); all.Id = Node.All; all.Title = "所有电脑"; all.Online = true; l.Add(all);
        foreach (Peer p in node.Peers().OrderBy(delegate (Peer x) { return x.Name; })) { Conv c = new Conv(); c.Id = p.Id; c.Title = p.Name; c.Online = p.Online; l.Add(c); }
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
        title.Sub = c.Id == Node.Ai ? "各电脑的 Claude Code 在这里交流，你也可以发言 · " + on + " 台在线"
            : c.Id == Node.All ? on + " 台在线" : c.Online ? "在线" : "离线，消息会在对方上线后送达";
        input.Hint = "发消息到 " + c.Title;
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
            try { key = node.FetchKey(p.Id); } catch (Exception e) { problem = e is InvalidOperationException ? e.Message : "连不上（" + e.Message + "）"; }
            Ui(delegate {
                perm.Enabled = true;
                using (ControlForm f = new ControlForm(node, p, key, problem)) if (f.ShowDialog(this) == DialogResult.OK) RefreshList();
            });
        });
    }
    void MarkRead() { if (Visible && unread.Remove(current)) list.Invalidate(); }
    void DrawConv(object s, DrawItemEventArgs e) {
        if (e.Index < 0) return;
        Conv c = (Conv)list.Items[e.Index];
        Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (Brush b = new SolidBrush(Theme.Side)) g.FillRectangle(b, e.Bounds);
        Rectangle row = new Rectangle(e.Bounds.X + Theme.S(10), e.Bounds.Y + Theme.S(2), e.Bounds.Width - Theme.S(20), e.Bounds.Height - Theme.S(4));
        if (sel) Theme.Fill(g, row, Theme.S(20), Theme.SideSel, Color.Empty);
        int d = Theme.S(36);
        Rectangle av = new Rectangle(row.X + Theme.S(10), row.Y + (row.Height - d) / 2, d, d);
        bool group = Node.IsGroup(c.Id);
        TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        if (c.Id == Node.Ai) Theme.DrawMark(g, Rectangle.Inflate(av, -Theme.S(1), -Theme.S(1)));
        else {
            using (Brush b = new SolidBrush(group ? Theme.Olive : c.Online ? Theme.Soft : Theme.Sand)) g.FillEllipse(b, av);
            if (group) TextRenderer.DrawText(g, "\uE716", Theme.GlyphBig, av, Color.White, mid);
            else TextRenderer.DrawText(g, c.Title.Substring(0, 1).ToUpperInvariant(), Theme.Heading, av, c.Online ? Theme.Back : Theme.Dim, mid);
        }
        if (!group && c.Online) { // status dot, cut out of the avatar
            Rectangle dot = new Rectangle(av.Right - Theme.S(11), av.Bottom - Theme.S(11), Theme.S(12), Theme.S(12));
            using (Brush b = new SolidBrush(sel ? Theme.SideSel : Theme.Side)) g.FillEllipse(b, dot);
            using (Brush b = new SolidBrush(Theme.Olive)) g.FillEllipse(b, Rectangle.Inflate(dot, -Theme.S(2), -Theme.S(2)));
        }
        Grant grant = group ? null : node.GrantFor(c.Id);
        int n;
        bool mark = unread.TryGetValue(c.Id, out n) && n > 0;
        int x = av.Right + Theme.S(12), w = row.Right - x - Theme.S(mark ? 44 : 10);
        TextFormatFlags left = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, c.Title, mark ? Theme.Bold : Theme.Body, group ? new Rectangle(x, row.Y, w, row.Height) : new Rectangle(x, row.Y + Theme.S(7), w, Theme.S(23)), Theme.Text, left);
        if (!group) TextRenderer.DrawText(g, (c.Online ? "在线" : "离线") + (grant == null ? "" : grant.Mode == "allow" ? " · 可控制本机" : " · 控制需确认"), Theme.Small, new Rectangle(x, row.Y + Theme.S(29), w, Theme.S(20)), grant != null ? Theme.AccentText : c.Online ? Theme.Online : Theme.Dim, left);
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
        m.Items.Add("移除这台电脑", null, delegate {
            if (MessageBox.Show(this, "把“" + c.Title + "”从列表移除？还没送达的消息会取消，聊天记录保留在磁盘上。它再次上线时会重新出现。", "LanTalk", MessageBoxButtons.OKCancel) == DialogResult.OK) { node.ForgetPeer(c.Id); RefreshList(); Select(current); }
        });
        m.Show(list, e.Location);
    }

    // ---- messages ----
    void OnAdded(ChatMsg m) {
        if (m.Conv == current) view.SetMessages(node.History(current, 500), m.From == node.Cfg.Id);
        if (m.From != node.Cfg.Id && m.Kind == "text") Notify(m);
        if (m.From != node.Cfg.Id && m.Kind == "run") { // visible, but not once per command
            DateTime last; toldControl.TryGetValue(m.From, out last);
            if (tray.Visible && (DateTime.UtcNow - last).TotalMinutes > 10) tray.ShowBalloonTip(4000, m.FromName + " 正在控制这台电脑", Util.Cut(m.Text + " ", 120), ToolTipIcon.None);
            toldControl[m.From] = DateTime.UtcNow;
        }
    }
    void Notify(ChatMsg m) {
        if (Visible && ContainsFocus && m.Conv == current) return;
        if (!(Visible && m.Conv == current && Form.ActiveForm == this)) { int n; unread.TryGetValue(m.Conv, out n); unread[m.Conv] = n + 1; list.Invalidate(); }
        if (tray.Visible) tray.ShowBalloonTip(4000, (string.IsNullOrEmpty(m.Agent) ? "" : m.Agent + " @ ") + m.FromName, m.Kind == "file" ? "[文件] " + m.Text : Util.Cut(m.Text + " ", 120), ToolTipIcon.None);
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
        if (paths.Length > 20 && MessageBox.Show(this, "要把 " + paths.Length + " 个项目发给“" + c.Title + "”吗？", "LanTalk", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
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
                    string f = Path.Combine(d, "截图 " + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    using (Image i = Clipboard.GetImage()) i.Save(f, ImageFormat.Png);
                    node.SendPath(current, f);
                });
            }
        }
    }
    void Reveal(ChatMsg m) {
        if (m.Path != null && (File.Exists(m.Path) || Directory.Exists(m.Path))) Process.Start("explorer.exe", "/select,\"" + m.Path + "\"");
        else if (m.State == "ok") MessageBox.Show(this, "文件已被移动或删除。", "LanTalk");
    }
    void ShowMenu(ChatMsg m, Point at) {
        ContextMenuStrip c = Theme.Menu();
        if (m.Kind == "file") {
            if (m.State == "ok" || m.From == node.Cfg.Id) { c.Items.Add("打开所在文件夹", null, delegate { Reveal(m); }); c.Items.Add("复制路径", null, delegate { if (!string.IsNullOrEmpty(m.Path)) Clipboard.SetText(m.Path); }); }
        } else {
            c.Items.Add("复制", null, delegate { if (!string.IsNullOrEmpty(m.Text)) Clipboard.SetText(m.Text); });
            Match u = m.Kind == "run" ? Match.Empty : Regex.Match(m.Text ?? "", "https?://[^\\s\"<>]+");
            if (u.Success) c.Items.Add("打开链接", null, delegate { Process.Start(u.Value); });
        }
        if (m.Kind == "run" && m.State == "run") c.Items.Add("停止", null, delegate { node.StopControl(m.Id); });
        if (m.From == node.Cfg.Id && m.Pending.Count > 0) c.Items.Add("取消发送", null, delegate { node.Cancel(m); });
        if (m.From == node.Cfg.Id && (m.State == "fail" || m.State == "cancel")) c.Items.Add("重新发送", null, delegate { node.Retry(m); });
        if (c.Items.Count > 0) c.Show(view, at);
    }

    // ---- window ----
    public void ShowUp() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); MarkRead(); }
    void OpenSettings() {
        using (SettingsForm f = new SettingsForm(node, false)) if (f.ShowDialog(this) == DialogResult.OK) { Text = "LanTalk · " + node.Cfg.Name; RefreshList(); }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!quitting && e.CloseReason == CloseReason.UserClosing) {
            e.Cancel = true; Hide();
            if (!toldTray) { toldTray = true; tray.ShowBalloonTip(3000, "LanTalk", "仍在后台运行，点这里的图标可以打开，右键可以退出。", ToolTipIcon.None); }
            return;
        }
        tray.Visible = false;
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

    public SettingsForm(Node n, bool first) {
        node = n; oldCode = n.Code;
        Text = first ? "LanTalk 首次设置" : "LanTalk 设置"; Font = Theme.Body; BackColor = Theme.Back; ForeColor = Theme.Text; Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        Label head = new Label(); head.Text = first ? "先设置一下这台电脑" : "设置"; head.Font = Theme.Greeting; head.TextAlign = ContentAlignment.MiddleLeft;
        head.SetBounds(Theme.S(17), Theme.S(16), Theme.S(490), Theme.S(46)); Controls.Add(head);
        int y = Theme.S(76);
        Row("本机名称（别的电脑看到的名字）", name, 490, ref y);
        RButton gen = RButton.Outline("生成新的"), copy = RButton.Outline("复制");
        Row("配对码（每台电脑必须相同）", code, 300, ref y);
        Control box = code.Parent;
        gen.SetBounds(box.Right + Theme.S(8), box.Top, Theme.S(104), box.Height); copy.SetBounds(gen.Right + Theme.S(8), box.Top, Theme.S(70), box.Height);
        gen.Click += delegate { code.Text = Crypto.NewCode(); };
        copy.Click += delegate { if (code.Text.Length > 0) Clipboard.SetText(code.Text); };
        Controls.Add(gen); Controls.Add(copy);
        Label tip = new Label(); tip.Text = "第一台点“生成新的”，其余的填同一个配对码。配对码等于钥匙，不要外传。"; tip.ForeColor = Theme.Dim; tip.Font = Theme.Small;
        y -= Theme.S(8); tip.SetBounds(Theme.S(21), y, Theme.S(490), Theme.S(24)); Controls.Add(tip); y += Theme.S(36);
        Row("收到的文件存到", dir, 490, ref y);
        Row("手动添加电脑 IP（自动找不到时才填，逗号分隔）", manual, 490, ref y);
        Label fp = new Label(); fp.Text = "本机校验码（别的电脑给本机控制权限时核对）：" + Node.Fingerprint(n.PublicKey); fp.ForeColor = Theme.Dim; fp.Font = Theme.Small;
        fp.SetBounds(Theme.S(21), y - Theme.S(4), Theme.S(500), Theme.S(24)); Controls.Add(fp); y += Theme.S(26);
        auto.Text = "开机自动启动（缩到托盘）"; auto.SetBounds(Theme.S(22), y, Theme.S(400), Theme.S(30)); Controls.Add(auto);
        y += Theme.S(46);
        RButton ok = RButton.Primary("确定"), cancel = RButton.Outline("取消");
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(Theme.S(314), y, Theme.S(96), Theme.S(38)); ok.SetBounds(Theme.S(418), y, Theme.S(96), Theme.S(38));
        ok.Click += delegate { Save(); };
        Controls.Add(cancel); Controls.Add(ok); AcceptButton = ok; CancelButton = cancel;
        ClientSize = new Size(Theme.S(538), ok.Bottom + Theme.S(22));

        name.Text = n.Cfg.Name; code.Text = oldCode ?? ""; dir.Text = n.Cfg.SaveDir; manual.Text = string.Join(", ", n.Cfg.Manual.ToArray());
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) auto.Checked = k != null && k.GetValue("LanTalk") != null;
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
        string err = nm.Length == 0 ? "请填写本机名称。" : nm.Length > 40 ? "名称太长。" : c == null ? "配对码应是 16 位（不含 I、O、0、1），例如 ABCD-EFGH-JKLM-NPQR。" : null;
        if (err == null && ips.Any(delegate (string s) { return Node.ParseEndpoint(s) == null; })) err = "IP 地址格式不对，例如 192.168.1.20。";
        if (err == null) try { if (!Path.IsPathRooted(d)) throw new ArgumentException("请填写完整的文件夹路径。"); Directory.CreateDirectory(d); } catch (Exception e) { err = "接收文件夹不能用：" + e.Message; }
        if (err != null) { MessageBox.Show(this, err, "LanTalk"); return; }
        lock (node.L) { node.Cfg.Name = nm; node.Cfg.SaveDir = d; node.Cfg.Manual = ips.ToList(); }
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
        string data = Ipc.DefaultData; IPAddress bind = IPAddress.Any; bool trayOnly = false;
        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--tray") trayOnly = true;
            else if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
            else if (args[i] == "--bind" && i + 1 < args.Length) bind = IPAddress.Parse(args[++i]);
        }
        InitUi();
        string key = Ipc.PipeName(data);
        bool created;
        using (Mutex one = new Mutex(true, "Local\\" + key, out created))
        using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + key + "-show")) {
            if (!created) { show.Set(); return 0; } // already running: bring it up
            Node node = new Node(data, bind);
            if (node.Code == null) {
                using (SettingsForm f = new SettingsForm(node, true)) { f.ShowInTaskbar = true; if (f.ShowDialog() != DialogResult.OK) return 0; }
                trayOnly = false;
            }
            try { node.Start(); }
            catch (SocketException e) { MessageBox.Show("端口 " + node.Cfg.Port + " 无法使用：" + e.Message, "LanTalk"); return 2; }
            new IpcServer(node, data).Start();
            MainForm form = new MainForm(node, data);
            form.ShowTray();
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
