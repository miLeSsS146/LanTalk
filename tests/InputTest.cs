// Tests for Input.cs, part of Test.cs. A asks, B is controlled, with a hand that only writes down what it was told: nobody's mouse moves.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LanTalk {
class FakeHand : IHand {
    public readonly List<string> Log = new List<string>();
    public bool IsLocked, Password, On; public long HumanAt; public Msg No; public Action StopKey; public Action<string> Did = delegate { };
    void Add(string s) { lock (Log) Log.Add(s); Did(s); }
    public string All { get { lock (Log) return string.Join(" ", Log.ToArray()); } }
    public void Clear() { lock (Log) Log.Clear(); }
    public bool Locked { get { return IsLocked; } }
    public long LastHuman { get { return HumanAt; } }
    public void Watch(bool on, Action key) { On = on; StopKey = key; }
    public Point At; public void MoveTo(Point p) { At = p; Add("move"); }
    public void Button(string w, bool down) { Add(w + (down ? "+" : "-")); }
    public void Wheel(int c, bool sideways) { Add((sideways ? "h" : "v") + c); }
    public void Key(int vk, bool down) { Add("k" + vk + (down ? "+" : "-")); }
    public void Char(char c) { Add("" + c); }
    public void Front(IntPtr w) { Add("front"); }
    public Msg Refuses(Point p) { return No; }
    public Msg Refuses(IntPtr w) { return No; }
    public bool Covered; public bool Belongs(Point p, IntPtr w) { return !Covered; }
    public Msg RefusesKeys() { return No; }
    public bool InPassword { get { return Password; } }
    public string NameAt(Point p) { return "下一步"; }
}
static partial class Test {
    static bool BadStep(string s) { try { InputStep.Parse(s); return false; } catch (LtException) { return true; } }
    static Process LtStart(string data, string args) { ProcessStartInfo si = new ProcessStartInfo(ltExe, args + " --data \"" + data + "\""); si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardOutput = si.RedirectStandardError = true; return Process.Start(si); }
    static void InputTests(Node A, Node B, string da, string db) {
        int rc = 0; string o, ib = B.Cfg.Id, ia = A.Cfg.Id, q = "\"";
        Func<string, string> lt = delegate (string args) { return LtErr(da, out rc, args + " --to PC-B", null); };
        Func<string, string, string> ltIn = delegate (string args, string stdin) { return LtErr(da, out rc, args + " --to PC-B", stdin); };

        // ---- steps ----
        InputStep s = InputStep.Parse("click 12, 34 ctrl+shift"); Check(s.X == 12 && s.Y == 34 && s.Mods.Length == 2 && s.Places, "step: click with keys held");
        Check(InputStep.Parse("type   两个空格 \"引号\" a+b").Text == "  两个空格 \"引号\" a+b", "step: what is typed stays as it is");
        s = InputStep.Parse("key Ctrl+Shift+F5"); Check(s.Key == 0x74 && s.Mods.Length == 2 && s.Text == "Ctrl+Shift+F5", "step: key combination");
        s = InputStep.Parse("key down 5"); Check(s.Key == 40 && s.N == 5, "step: key several times");
        s = InputStep.Parse("scroll 5 6 up"); Check(s.N == 3 && s.Text == "up", "step: scroll"); s = InputStep.Parse("drag -1 2 3 4"); Check(s.X == -1 && s.Y2 == 4, "step: drag");
        string[] wrong = { "", "click", "click 1", "click a b", "click 1 2 3", "click 1 2 enter", "key", "key ctrl+", "key a+b", "key nope", "key down 101", "scroll 1 2", "scroll 1 2 side", "scroll 1 2 up 99", "type", "wait", "wait 99999", "focus", "run calc", "key win+l", "drag 1 2 3", "move 1 2 3" };
        Check(wrong.All(BadStep), "step: anything else is refused: " + string.Join(" | ", wrong.Where(delegate (string x) { return !BadStep(x); }).ToArray()));

        // ---- access ----
        Grant g = Grant.Of("", "", "", "allow"); Check(g.Cap("mouse") == "" && g.Screen == "allow", "mouse: running commands doesn't bring it along");
        g = Grant.Of("", "", "", "", "allow"); Check(g.Screen == "allow" && g.Run == "" && g.Any, "mouse: comes with seeing the screen");
        g = new Grant(); g.Mode = "allow"; g.Tidy(); Check(g.Cap("mouse") == "" && g.Run == "allow", "mouse: an older grant doesn't include it");
        B.SetGrant(ia, "PC-A", A.PublicKey, "allow");
        FakeHand hand = new FakeHand(); IHand real = B.Hand; B.Hand = hand; B.InputCalmMs = 600; B.InputIdleMs = 2500; A.InputIdleMs = 2500;
        try {
            o = lt("input \"key enter\""); Check(rc == 125 && Err.Contains("鼠标键盘") && hand.Log.Count == 0, "mouse: refused without its own permission");
            g = Grant.Of("", "", "", "", "allow"); B.SetGrant(ia, "PC-A", A.PublicKey, g);
            o = lt("input \"fly 1 2\""); Check(rc == 125 && Err.Contains("看不懂这一步") && hand.Log.Count == 0 && B.InputNow() == null, "mouse: a mistake shows before anything happens");
            hand.IsLocked = true; o = lt("input \"key enter\""); Check(rc == 125 && Err.Contains("锁屏") && B.InputNow() == null, "mouse: not on a locked screen"); hand.IsLocked = false;
            B.views.Clear(); o = lt("input --no-look \"click 5 5\""); Check(rc == 1 && Err.Contains("先截图") && !hand.All.Contains("left"), "mouse: positions need a picture first");
            Check(Until(delegate { return B.Controlling().Count == 0 && B.InputNow() == null && !hand.On && A.History(ib, 1)[0].State == "ok"; }, 12), "mouse: a use ends when nothing happens any more");
            hand.Clear(); int had = B.History(ia, 500).Count;
            o = ltIn("input --no-look -", "# Kommentar\nkey ctrl+s\n\ntype 中a\nwait 10\nkey down 2\n"); Check(rc == 0 && o.Contains("1  按 Ctrl+S") && o.Contains("2  输入“中a”") && hand.All == "k17+ k83+ k83- k17- 中 a k40+ k40- k40+ k40-", "mouse: keys and text: " + hand.All + " / " + Err);
            Check(hand.On && B.InputNow() != null && B.InputNow().Phase == "act" && B.Controlling().Count == 1, "mouse: it shows while it lasts");
            o = lt("input --no-look \"type x\""); ChatMsg m = B.History(ia, 1)[0];
            Check(B.History(ia, 500).Count == had + 1 && m.Op == "input" && m.Text.EndsWith("\n4  按 Down（2 次）\n5  输入“x”") && m.State == "run", "mouse: one entry in the chat for all of it: " + m.Text.Replace("\n", "|"));
            m = A.History(ib, 1)[0]; Check(m.Op == "input" && m.Text.EndsWith("\n5  输入“x”") && m.State == "run", "mouse: here as well");
            Check(Until(delegate { return B.History(ia, 1)[0].State == "ok" && A.History(ib, 1)[0].State == "ok"; }, 12) && B.History(ia, 1)[0].ErrText.StartsWith("5 步 · "), "mouse: and what came of it: " + B.History(ia, 1)[0].ErrText);

            if (!LanTalk.Shot.Locked) {
                string pic = Path.Combine(root, "in 1.png"); Rectangle mon = LanTalk.Shot.Monitors()[0];
                o = lt("shot --screen 1 " + q + pic + q); int w, h; using (Image i = Image.FromFile(pic)) { w = i.Width; h = i.Height; }
                hand.Clear(); List<Point> seen = new List<Point>(); B.InputPoint += delegate (Point p, bool c) { if (c) seen.Add(p); };
                string pic2 = Path.Combine(root, "in 2.png");
                o = lt("input \"click " + (w / 2) + " " + (h / 4) + "\" " + q + pic2 + q);
                Check(rc == 0 && o.Contains("1  点击“下一步”") && o.Contains(pic2) && File.Exists(pic2) && Math.Abs(hand.At.X - (mon.X + mon.Width / 2)) < 4 && Math.Abs(hand.At.Y - (mon.Y + mon.Height / 4)) < 4 && hand.All == "move left+ left-" && seen.Count == 1, "mouse: clicks where the picture says, and sends a new one: " + hand.At + " " + hand.All + " " + Err);
                o = lt("input --no-look \"click " + w + " 5\""); Check(rc == 1 && Err.Contains("不在截图里"), "mouse: not outside the picture");
                hand.Clear(); o = ltIn("input --no-look -", "double 9 9\nright 9 9 ctrl\nscroll 9 9 down 2\ndrag 9 9 20 20"); Check(rc == 0 && hand.All.StartsWith("move left+ left- left+ left- move k17+ right+ right- k17- move v-1 v-1 move left+ move") && hand.All.EndsWith("move left-"), "mouse: double, right, wheel, drag: " + hand.All);
                hand.No = Lang.M("那是 LanTalk 自己的窗口", "own window"); hand.Clear(); o = lt("input --no-look \"click 9 9\""); Check(rc == 1 && Err.Contains("LanTalk 自己的窗口") && hand.Log.Count == 0, "mouse: refused windows aren't touched");
                o = lt("input --no-look \"type x\""); Check(rc == 1 && hand.Log.Count == 0, "mouse: ...nor typed into"); hand.No = null;

                B.views[ia].Window = LanTalk.Shot.Windows()[0].H; B.views[ia].Area = LanTalk.Shot.FrameOf(B.views[ia].Window); hand.Covered = true; hand.Clear(); o = lt("input --no-look \"click 9 9\""); Check(B.views[ia].Area.IsEmpty || rc == 1 && Err.Contains("被挡住") && !hand.All.Contains("left"), "mouse: never through a window that covers the one in the picture: " + Err); hand.Covered = false; lt("shot --screen 1 --force " + q + pic + q);

                // ---- the person comes first ----
                hand.Clear(); hand.Did = delegate (string x) { if (x == "b") hand.HumanAt = Util.Now() + 1; };
                o = ltIn("input -", "type abcdef\ntype gh"); hand.Did = delegate { };
                Check(rc == 1 && Err.Contains("停在第 1 步") && Err.Contains("动了鼠标或键盘") && hand.All == "a b" && o.Contains(".png"), "mouse: stops in the middle when someone uses it: " + hand.All + " " + Err.Trim());
                hand.Clear(); o = lt("input --no-look \"type x\""); Check(rc == 0 && hand.All == "x", "mouse: goes on after a new picture " + Err);
                Thread.Sleep(30); hand.HumanAt = Util.Now(); hand.Clear(); o = lt("input --no-look \"type x\""); Check(rc == 1 && Err.Contains("有人动过") && hand.Log.Count == 0, "mouse: never acts on an old picture");
                o = lt("shot --screen 1 --force " + q + pic + q); o = lt("input --no-look \"type x\""); Check(rc == 0, "mouse: a screenshot is enough");
                Thread busy = new Thread(delegate () { for (int i = 0; i < 40; i++) { hand.HumanAt = Util.Now(); Thread.Sleep(100); } }); busy.Start(); Thread.Sleep(150);
                string phase = ""; Thread look = new Thread(delegate () { for (int i = 0; i < 20; i++) { InputLook n = B.InputNow(); if (n != null && n.Phase == "yield") phase = "yield"; Thread.Sleep(50); } }); look.Start();
                o = lt("input --no-look --timeout 1 \"type x\""); Check(rc == 1 && Err.Contains("有人在用") && phase == "yield", "mouse: waits while someone uses it, then says so: " + phase + " " + Err); busy.Join(); look.Join();
                lt("shot --screen 1 --force " + q + pic + q);

                // ---- passwords ----
                hand.Password = true; hand.Clear(); Thread.Sleep(700); o = ltIn("input --no-look -", "key tab\ntype geheim"); Check(rc == 1 && Err.Contains("密码框") && hand.All == "k9+ k9-" && B.InputNow().Phase == "password", "mouse: never into a password box: " + hand.All);
                o = lt("input --no-look \"key ctrl+v\""); Check(rc == 1 && hand.All == "k9+ k9-", "mouse: not by pasting either");
                hand.Password = false; o = lt("input --no-look \"type y\""); Check(rc == 0 && B.InputNow().Phase == "act" && B.History(ia, 50).Last(delegate (ChatMsg x) { return x.Op == "input"; }).Text.Contains("· 这里是密码框"), "mouse: the entry says where it stopped: " + rc + " " + Err + B.History(ia, 50).Last(delegate (ChatMsg x) { return x.Op == "input"; }).Text.Replace('\n', '|'));

                // ---- stopping ----
                Thread stop = new Thread(delegate () { Thread.Sleep(1500); hand.StopKey(); }); stop.Start();
                Stopwatch sw = Stopwatch.StartNew(); o = ltIn("input --no-look -", "wait 20000\ntype never"); Check(rc != 0 && sw.Elapsed.TotalSeconds < 8 && !hand.All.Contains("n e v"), "mouse: the stop key ends it (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s)");
                Check(Until(delegate { return B.InputNow() == null && !hand.On && B.GrantFor(ia).Paused; }, 8), "mouse: and everything else"); o = lt("input --no-look \"type x\""); Check(rc == 125 && Err.Contains("恢复"), "mouse: nothing new until resumed"); B.ResumeControl(ia);
                o = lt("input --no-look \"type x\""); hand.Clear(); Check(B.InputNow() != null, "mouse: works again after resuming " + Err); B.PauseControl(ia); Check(B.InputNow() == null && B.Controlling().Count == 0 && !hand.On && B.History(ia, 50).Last(delegate (ChatMsg x) { return x.Op == "input"; }).ErrText.StartsWith("已停止"), "mouse: Stop between two requests ends it at once"); B.ResumeControl(ia);
                Process hang = LtStart(da, "input --no-look --to PC-B \"wait 30000\""); Until(delegate { return B.InputNow() != null && B.Controlling().Count == 1; }, 8); Thread.Sleep(500); hang.Kill(); Thread.Sleep(7500);
                o = lt("input --no-look \"type z\""); Check(rc == 0, "mouse: killing lt ends the steps there: " + Err);
                Until(delegate { return B.InputNow() == null; }, 12);

                // ---- asking ----
                int asked = 0; string op = null, cap = null, text = null; bool yes = true;
                Action<ControlAsk> h2 = delegate (ControlAsk a) { asked++; op = a.Op; cap = a.Cap; text = a.Text; hand.HumanAt = Util.Now(); a.Answer(yes, false); };
                B.ControlAsked += h2;
                B.SetGrant(ia, "PC-A", A.PublicKey, Grant.Of("", "", "", "", "ask")); Check(B.GrantFor(ia).Screen == "ask", "mouse: asking comes with asking for the screen");
                yes = false; hand.Clear(); o = lt("input --no-look \"type x\""); Check(rc == 125 && Err.Contains("没有同意") && asked == 1 && hand.Log.Count == 0 && B.InputNow() == null, "mouse: declined");
                yes = true; o = lt("input --no-look \"type x\""); int first = rc; o = lt("input --no-look \"type x\""); Check(first == 0 && rc == 0 && asked == 2 && op == "input" && cap == "mouse" && text == "输入“x”", "mouse: asks once for one use: " + asked + " " + text + " " + Err);
                Check(Until(delegate { return B.InputNow() == null; }, 12), "mouse: the use ends"); lt("input --no-look \"type x\""); Check(asked == 3, "mouse: and then asks again");
                B.ControlAsked -= h2;
                B.SetGrant(ia, "PC-A", A.PublicKey, Grant.Of("allow", "", "", "")); Check(Until(delegate { return B.InputNow() == null; }, 8), "mouse: withdrawing it ends the use");
                B.SetGrant(ia, "PC-A", A.PublicKey, Grant.Of("allow", "allow", "allow", "allow", "allow"));
                A.Cfg.Lang = "en"; A.SaveConfig(); Lang.Set("en"); lt("shot --screen 1 --force " + q + pic + q);
                try { o = lt("input --no-look \"click 9 9\""); Check(rc == 0 && o.Contains("1  Click \"下一步\""), "lang: steps in English: " + o + Err); o = lt("input --no-look \"click 99999 9\""); Check(rc == 1 && Err.Contains("Not finished: stopped at step 1") && !Chinese(Err), "lang: why it stopped, in English: " + Err); }
                finally { Lang.Set("zh"); A.Cfg.Lang = "zh"; A.SaveConfig(); }
                Until(delegate { return B.InputNow() == null && A.Controlling().Count == 0; }, 12);
            }
        } finally { B.Hand = real; B.SetGrant(ia, "PC-A", A.PublicKey, "allow"); }

        // the real hand: what can be checked without touching anything
        Hand r = new Hand();
        using (System.Windows.Forms.Form f = new System.Windows.Forms.Form()) { IntPtr fh = f.Handle; Check(r.Refuses(fh) != null && r.Refuses(fh).Zh.Contains("LanTalk 自己"), "hand: LanTalk's own windows are off limits"); }
        Process ex = Process.GetProcessesByName("explorer").FirstOrDefault(delegate (Process p) { return p.SessionId == Process.GetCurrentProcess().SessionId; });
        LanTalk.Shot.Win any = LanTalk.Shot.Windows().Find(delegate (LanTalk.Shot.Win x) { return x.Process == "explorer"; }); if (any != null) Check(r.Refuses(any.H) == null, "hand: other programs' windows aren't");
        r.Watch(true, null); Check(r.LastHuman > 0 && r.LastHuman <= Util.Now(), "hand: knows when someone was here last"); r.Watch(false, null);
    }
}
}
