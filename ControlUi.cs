// Windows for remote control: who may control this PC, and "another PC wants to do this, OK?".
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LanTalk {

// One selectable option with a title and an explanation.
public class ChoiceCard : RadioButton {
    public string Sub = "";
    public ChoiceCard() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) {
        Graphics g = e.Graphics; g.Clear(Parent == null ? Theme.Back : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
        Theme.Fill(g, r, Theme.S(20), Checked ? Theme.AgentFill : Theme.Card, Checked || Focused && ShowFocusCues ? Theme.Accent : Theme.Line);
        int d = Theme.S(16);
        Rectangle dot = new Rectangle(Theme.S(16), (Height - d) / 2, d, d);
        using (Pen p = new Pen(Checked ? Theme.Accent : Theme.LineFocus, Theme.S(2))) g.DrawEllipse(p, dot);
        if (Checked) using (Brush b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, Rectangle.Inflate(dot, -Theme.S(4), -Theme.S(4)));
        int x = dot.Right + Theme.S(14), w = Width - x - Theme.S(12);
        TextRenderer.DrawText(g, Text, Theme.Bold, new Rectangle(x, Theme.S(10), w, Theme.S(24)), Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, Sub, Theme.Small, new Rectangle(x, Theme.S(34), w, Height - Theme.S(38)), Theme.Dim, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
    }
}

public class ControlForm : Form {
    readonly Node node;
    readonly Peer peer;
    readonly string key;   // what the other PC presents now; null when it can't be asked
    readonly ChoiceCard no = new ChoiceCard(), ask = new ChoiceCard(), allow = new ChoiceCard();

    public ControlForm(Node n, Peer p, string currentKey, string problem) {
        node = n; peer = p; key = currentKey;
        Grant g = n.GrantFor(p.Id);
        bool changed = g != null && key != null && g.Key != key;
        Text = Lang.T("LanTalk 控制权限", "LanTalk control access"); Font = Theme.Body; BackColor = Theme.Back; ForeColor = Theme.Text; Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        int y = Theme.S(16);
        Add(Line(Lang.T("允许 {0} 控制这台电脑？", "Let {0} control this PC?", p.Name), Theme.Greeting, Theme.Text, 17, ref y, 46));
        y += Theme.S(2);
        Add(Line(Lang.T("指 {0} 上的 Claude（或人）通过 lt run / put / get 在这台电脑上运行命令、读写文件。", "That is: Claude (or a person) on {0} running commands and reading or writing files on this PC through lt run / put / get.", p.Name), Theme.Small, Theme.Dim, 21, ref y, 26));
        y += Theme.S(6);
        Card(no, Lang.T("不允许", "No"), Lang.T("默认。对方仍然可以聊天和传文件。", "The default. The other PC can still chat and send files."), ref y);
        Card(ask, Lang.T("每次询问", "Ask every time"), Lang.T("每条命令都在这台电脑上弹窗，你看过再决定。60 秒没人理就拒绝。", "Every command pops up on this PC and you decide. No answer within 60 seconds means no."), ref y);
        Card(allow, Lang.T("直接允许", "Always allow"), Lang.T("不再询问。它能运行这个 Windows 账户能运行的任何东西、读改任何文件，和你自己坐在这里一样。请只给你自己的、别人碰不到的电脑。", "No more questions. It can run anything and change any file this Windows account can, as if you were sitting here. Only for your own PCs that nobody else can get to."), ref y);
        y += Theme.S(6);
        string note = key == null ? Lang.T("对方现在联系不上，只能取消授权：", "The other PC can't be reached now, so access can only be removed: ") + problem
            : changed ? Lang.T("注意：{0} 的身份变了（重装过，或者有人在冒充）。原来的授权已经不能用，要继续请核对校验码后重新选择。", "Careful: the identity of {0} has changed (reinstalled, or someone is posing as it). The old access no longer works. To go on, compare the check code and choose again.", p.Name)
            : Lang.T("请核对：{0} 的 LanTalk“设置”里显示的本机校验码应当是", "Compare: Settings in LanTalk on {0} should show this check code", p.Name);
        Add(Line(note, Theme.Small, key == null || changed ? Theme.Bad : Theme.Dim, 21, ref y, 24));
        if (key != null) Add(Line(Node.Fingerprint(key), Theme.Mono, Theme.Text, 21, ref y, 28));
        y += Theme.S(12);
        RButton ok = RButton.Primary(Lang.T("确定", "OK")), cancel = RButton.Outline(Lang.T("取消", "Cancel"));
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(Theme.S(314), y, Theme.S(96), Theme.S(38)); ok.SetBounds(Theme.S(418), y, Theme.S(96), Theme.S(38));
        ok.Click += delegate { Save(); };
        Controls.Add(cancel); Controls.Add(ok); AcceptButton = ok; CancelButton = cancel;
        ClientSize = new Size(Theme.S(538), ok.Bottom + Theme.S(22));
        ask.Enabled = allow.Enabled = key != null;
        (g == null || changed || key == null ? no : g.Mode == "allow" ? allow : ask).Checked = true;
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this, Theme.Back); }
    void Add(Control c) { Controls.Add(c); }
    static Label Line(string text, Font f, Color c, int x, ref int y, int h) {
        Label l = new Label(); l.Text = text; l.Font = f; l.ForeColor = c; l.UseMnemonic = false; l.TextAlign = ContentAlignment.MiddleLeft;
        int w = Theme.S(496), need = Math.Max(Theme.S(h), Theme.TextHeight(text, f, w - Theme.S(8)) + Theme.S(8));
        l.SetBounds(Theme.S(x), y, w, need); y += need;
        return l;
    }
    void Card(ChoiceCard c, string title, string sub, ref int y) {
        c.Text = title; c.Sub = sub; c.AccessibleDescription = sub;
        int h = Theme.S(48) + Theme.TextHeight(sub, Theme.Small, Theme.S(490 - 46 - 16)); // text area of ChoiceCard
        c.SetBounds(Theme.S(24), y, Theme.S(490), h); y += h + Theme.S(8);
        Controls.Add(c);
    }
    void Save() {
        Grant g = node.GrantFor(peer.Id);
        string mode = allow.Checked ? "allow" : ask.Checked ? "ask" : "";
        bool same = g != null && g.Mode == mode && g.Key == key;
        if (mode == "allow" && !same && MessageBox.Show(this, Lang.T("让 {0} 不经询问就控制这台电脑？\n\n校验码：{1}\n请确认它和 {0} 上显示的一致。", "Let {0} control this PC without asking?\n\nCheck code: {1}\nMake sure {0} shows the same one.", peer.Name, Node.Fingerprint(key)), "LanTalk", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        if (!same) node.SetGrant(peer.Id, peer.Name, key, mode);
        DialogResult = DialogResult.OK;
    }
}

// Pops up without taking the keyboard, so nobody approves by accident while typing.
public class AskForm : Form {
    readonly ControlAsk ask;
    readonly Timer tick = new Timer();
    readonly RButton no = RButton.Outline("");
    readonly DateTime end = DateTime.UtcNow.AddSeconds(ControlAsk.Seconds);

    public AskForm(ControlAsk a) {
        ask = a;
        Text = Lang.T("LanTalk 远程控制", "LanTalk remote control"); Font = Theme.Body; BackColor = Theme.Back; ForeColor = Theme.Text; Icon = Theme.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        Label head = new Label(); head.Text = Lang.T("{0} 想在这台电脑上执行", "{0} wants to run this here", a.PeerName); head.Font = Theme.Greeting; head.UseMnemonic = false; head.TextAlign = ContentAlignment.MiddleLeft; head.AutoEllipsis = true;
        head.SetBounds(Theme.S(17), Theme.S(16), Theme.S(590), Theme.S(46));
        CardPanel box = new CardPanel(); box.Corner = 9; box.SetBounds(Theme.S(24), Theme.S(74), Theme.S(580), Theme.S(200));
        TextBox t = new TextBox(); t.Multiline = true; t.ReadOnly = true; t.BorderStyle = BorderStyle.None; t.ScrollBars = ScrollBars.Vertical; t.Font = Theme.Mono; t.BackColor = Theme.Card; t.ForeColor = Theme.Text; t.TabStop = false; t.AccessibleName = Lang.T("要执行的内容", "What would run");
        t.Text = Plain(a.Text); t.SetBounds(Theme.S(12), Theme.S(10), Theme.S(558), Theme.S(180));
        box.Controls.Add(t);
        Label who = new Label(); who.Text = Lang.T("由那台电脑上的 {0} 发起。看不懂就拒绝。", "Asked for by {0} on that PC. If it makes no sense to you, decline.", a.Agent); who.Font = Theme.Small; who.ForeColor = Theme.Dim; who.UseMnemonic = false;
        who.SetBounds(Theme.S(21), Theme.S(282), Theme.S(580), Theme.S(24));
        RButton once = RButton.Primary(Lang.T("允许这一次", "Allow once")), longer = RButton.Outline(Lang.T("30 分钟内都允许", "Allow for 30 minutes"));
        no.SetBounds(Theme.S(24), Theme.S(318), Theme.S(120), Theme.S(38));
        longer.SetBounds(Theme.S(298), Theme.S(318), Theme.S(170), Theme.S(38)); once.SetBounds(Theme.S(476), Theme.S(318), Theme.S(128), Theme.S(38));
        no.Click += delegate { Answer(false, false); }; once.Click += delegate { Answer(true, false); }; longer.Click += delegate { Answer(true, true); };
        Controls.AddRange(new Control[] { no, longer, once, head, box, who });
        ClientSize = new Size(Theme.S(628), Theme.S(378));
        CancelButton = no;
        tick.Interval = 500; tick.Tick += delegate { ShowLeft(); }; tick.Start(); ShowLeft();
        a.Closed += delegate { try { BeginInvoke((MethodInvoker)Close); } catch (InvalidOperationException) { } };
        FormClosed += delegate { tick.Dispose(); ask.Answer(false, false); };
        IntPtr h = Handle; // so Closed can reach it
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this, Theme.Back); }
    void ShowLeft() { no.Text = Lang.T("拒绝（{0}）", "Decline ({0})", Math.Max(0, (int)(end - DateTime.UtcNow).TotalSeconds)); no.Invalidate(); }
    void Answer(bool ok, bool longer) { ask.Answer(ok, longer); Close(); }
    static bool Hidden(System.Globalization.UnicodeCategory c) { return c == System.Globalization.UnicodeCategory.Format || c == System.Globalization.UnicodeCategory.LineSeparator || c == System.Globalization.UnicodeCategory.ParagraphSeparator; }
    // Invisible and text-direction characters could make a command read differently from what runs.
    public static string Plain(string s) {
        System.Text.StringBuilder b = new System.Text.StringBuilder();
        foreach (char c in s.Replace("\r\n", "\n").Replace('\r', '\n')) {
            if (c == '\n') b.Append("\r\n");
            else if (c == '\t') b.Append("    ");
            else if (char.IsControl(c) || Hidden(char.GetUnicodeCategory(c))) b.Append("[U+" + ((int)c).ToString("X4") + "]");
            else b.Append(c);
        }
        return b.ToString();
    }
}
}
