// Mouse and keyboard for another PC: a list of steps, done one after the other, then a picture of the result.
// The person here always comes first: whoever touches the mouse or keyboard interrupts it, and it looks again before going on.
// It never types into password boxes, and it can't use LanTalk's own windows (that would let it raise its own access),
// windows of programs running as administrator, or a locked screen.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace LanTalk {

// One step, written as a line: "click 640 380", "type hello", "key ctrl+s". Positions are pixels of the last picture.
public class InputStep {
    public const int Max = 50, MaxText = 5000;
    public string Verb = "", Text = "";
    public int X, Y, X2, Y2, N = 1;
    public int[] Mods = new int[0];
    public int Key;
    public string Button { get { return Verb == "right" || Verb == "middle" ? Verb : "left"; } }
    public int Clicks { get { return Verb == "double" ? 2 : Verb == "triple" ? 3 : 1; } }
    public bool Places { get { return Verb == "click" || Verb == "double" || Verb == "triple" || Verb == "right" || Verb == "middle" || Verb == "move" || Verb == "drag" || Verb == "scroll"; } }
    static readonly Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        { "ctrl", 0x11 }, { "control", 0x11 }, { "shift", 0x10 }, { "alt", 0x12 }, { "win", 0x5B }, { "enter", 13 }, { "return", 13 }, { "esc", 27 }, { "escape", 27 }, { "tab", 9 }, { "space", 32 },
        { "backspace", 8 }, { "delete", 46 }, { "del", 46 }, { "insert", 45 }, { "home", 36 }, { "end", 35 }, { "pageup", 33 }, { "pagedown", 34 }, { "left", 37 }, { "up", 38 }, { "right", 39 }, { "down", 40 },
        { "menu", 0x5D }, { "printscreen", 0x2C }, { "capslock", 20 }, { "plus", 0xBB }, { "minus", 0xBD }, { "comma", 0xBC }, { "period", 0xBE } };
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScan(char c);
    static bool IsMod(int vk) { return vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B; }
    static int Vk(string s) {
        int vk, f;
        if (names.TryGetValue(s, out vk)) return vk;
        if (s.Length > 1 && (s[0] == 'f' || s[0] == 'F') && int.TryParse(s.Substring(1), out f) && f >= 1 && f <= 24) return 0x6F + f;
        if (s.Length == 1) { short v = VkKeyScan(char.ToLowerInvariant(s[0])); if (v != -1 && (v >> 8) == 0) return v & 0xFF; } // keys that need no shift
        return 0;
    }
    static Exception Bad(string line) { return Lang.Err("看不懂这一步：{0}", "can't read this step: {0}", Util.Cut(line, 60)); }
    public static InputStep Parse(string line) {
        InputStep s = new InputStep();
        string t = line.TrimStart(' ', '\t');
        int cut = t.IndexOfAny(new char[] { ' ', '\t' });
        s.Verb = (cut < 0 ? t.TrimEnd('\r', '\n') : t.Substring(0, cut)).ToLowerInvariant();
        string rest = cut < 0 ? "" : t.Substring(cut + 1);
        string[] a = rest.Split(new char[] { ' ', '\t', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Func<int, int> num = delegate (int i) { int v; if (i >= a.Length || !int.TryParse(a[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) throw Bad(line); return v; };
        int used = 0;
        switch (s.Verb) {
            case "click": case "double": case "triple": case "right": case "middle": case "move":
                s.X = num(0); s.Y = num(1); used = 2;
                if (a.Length > 2 && s.Verb != "move") { s.Mods = a[2].Split('+').Select(Vk).ToArray(); if (!s.Mods.All(IsMod)) throw Bad(line); used = 3; }
                break;
            case "drag": s.X = num(0); s.Y = num(1); s.X2 = num(2); s.Y2 = num(3); used = 4; break;
            case "scroll":
                s.X = num(0); s.Y = num(1);
                if (a.Length < 3) throw Bad(line);
                s.Text = a[2].ToLowerInvariant();
                if (s.Text != "up" && s.Text != "down" && s.Text != "left" && s.Text != "right") throw Bad(line);
                s.N = a.Length > 3 ? num(3) : 3; used = 4;
                if (s.N < 1 || s.N > 50) throw Bad(line);
                break;
            case "type":
                if (rest.Length == 0) throw Bad(line);
                if (rest.Length > MaxText) throw Lang.Err("一步最多输入 {0} 个字", "at most {0} characters in one step", MaxText);
                s.Text = rest.Replace("\r\n", "\n").Replace('\r', '\n'); return s;
            case "key": {
                if (a.Length == 0) throw Bad(line);
                int[] k = a[0].Split('+').Select(Vk).ToArray();
                if (k.Any(delegate (int v) { return v == 0; }) || !k.Take(k.Length - 1).All(IsMod)) throw Bad(line);
                s.Mods = k.Take(k.Length - 1).ToArray(); s.Key = k.Last();
                s.Text = string.Join("+", a[0].Split('+').Select(delegate (string p) { return char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant(); }).ToArray());
                if (a.Length > 1) s.N = num(1);
                used = 2;
                if (s.N < 1 || s.N > 100) throw Bad(line);
                if (s.Mods.Contains(0x5B) && s.Key == 'L') throw Lang.Err("Win+L 会锁屏，之后就操作不了了", "Win+L locks the screen, and then nothing more can be done");
                break;
            }
            case "wait": s.N = num(0); used = 1; if (s.N < 0 || s.N > 30000) throw Bad(line); break;
            case "focus": s.Text = rest.Trim(); if (s.Text == "") throw Bad(line); return s;
            default: throw Bad(line);
        }
        if (a.Length > used) throw Bad(line);
        return s;
    }
    // at: what is there, as far as it has a name.
    public Msg Label(string at) {
        bool named = !string.IsNullOrEmpty(at);
        string zh = named ? "“" + at + "”" : " (" + X + ", " + Y + ")", en = named ? " \"" + at + "\"" : " (" + X + ", " + Y + ")";
        switch (Verb) {
            case "click": return new Msg("点击" + zh, "Click" + en);
            case "double": return new Msg("双击" + zh, "Double-click" + en);
            case "triple": return new Msg("三击" + zh, "Triple-click" + en);
            case "right": return new Msg("右键点击" + zh, "Right-click" + en);
            case "middle": return new Msg("中键点击" + zh, "Middle-click" + en);
            case "move": return new Msg("移到" + zh, "Move to" + en);
            case "drag": return new Msg("拖动 (" + X + ", " + Y + ") 到 (" + X2 + ", " + Y2 + ")", "Drag (" + X + ", " + Y + ") to (" + X2 + ", " + Y2 + ")");
            case "scroll": return Text == "up" ? Lang.M("向上滚动", "Scroll up") : Text == "down" ? Lang.M("向下滚动", "Scroll down") : Text == "left" ? Lang.M("向左滚动", "Scroll left") : Lang.M("向右滚动", "Scroll right");
            case "type": return Lang.M("输入“{0}”", "Type \"{0}\"", Util.Cut(Text.Replace('\n', ' '), 60));
            case "key": return N > 1 ? Lang.M("按 {0}（{1} 次）", "Press {0} ({1} times)", Text, N) : Lang.M("按 {0}", "Press {0}", Text);
            case "wait": return Lang.M("等 {0} 毫秒", "Wait {0} ms", N);
            case "focus": return Lang.M("切到“{0}”", "Switch to \"{0}\"", Text);
        }
        return new Msg(Verb, Verb);
    }
}

// What the picture that positions refer to showed.
class PicView {
    public Rectangle Area; public int W, H; public IntPtr Window; public int Screen; public long At;
}

// The machine side. Tests use another one, so they don't move anybody's mouse.
interface IHand {
    bool Locked { get; }
    long LastHuman { get; }                    // when the person here last did something, in Util.Now() terms
    void Watch(bool on, Action stopKey);       // stopKey: Ctrl+Alt+Space was pressed
    void MoveTo(Point p);
    void Button(string which, bool down);
    void Wheel(int clicks, bool sideways);
    void Key(int vk, bool down);
    void Char(char c);
    void Front(IntPtr window);
    Msg Refuses(Point p);                      // why what is there can't be used, null when it can
    Msg Refuses(IntPtr window);
    bool Belongs(Point p, IntPtr window);      // what is there is this window, or a menu or the like of the same program
    Msg RefusesKeys();
    bool InPassword { get; }
    string NameAt(Point p);
}

// Something another PC does with the mouse and keyboard, from the first step until it hasn't done anything for a while.
class InputUse {
    public string Peer, Agent; public ChatMsg M;
    public Node.Token S;
    public long Start = Util.Now(), Last = Util.Now(), Excused;
    public bool Busy, Over;
    public volatile string Phase = "act"; // act | yield | password
    public int Count;
    readonly List<string> lines = new List<string>();
    public string Detail { get { lock (lines) return lines.Count == 0 ? "" : lines[lines.Count - 1]; } }
    public void Add(Msg m, bool step) {
        lock (lines) {
            if (step) Count++;
            lines.Add(m);
            if (lines.Count > 500) lines.RemoveAt(0);
            int first = Count - lines.Count(delegate (string l) { return !l.StartsWith("· "); }) + 1, n = first;
            M.Text = Lang.T("鼠标键盘", "Mouse and keyboard") + "\n" + string.Join("\n", lines.Select(delegate (string l) { return l.StartsWith("· ") ? l : (n++) + "  " + l; }).ToArray());
        }
    }
    public Msg Summary { get { Msg span = BgJob.Span(Math.Max(0, (Last - Start) / 1000.0)); return new Msg(Count + " 步 · " + span.Zh, Count + (Count == 1 ? " step · " : " steps · ") + span.En); } }
}

public class InputLook { public string Peer, Phase, Detail; }

public partial class Node {
    const int InputIdle = 60000, InputCalm = 5000; // a use ends after this long without anything; the person here must be quiet for this long
    internal IHand Hand = new Hand();
    InputUse inputUse;                                                                         // what is going on here
    readonly Dictionary<string, InputUse> inputOut = new Dictionary<string, InputUse>();       // what this PC does elsewhere, by peer
    internal readonly Dictionary<string, PicView> views = new Dictionary<string, PicView>();            // last picture each peer got
    internal int InputCalmMs = InputCalm, InputIdleMs = InputIdle;
    public event Action<Point, bool> InputPoint; // false: about to act there, true: clicked

    // Stands for a use in the list of what runs: stopping it stops the request that is being carried out.
    internal class Token { internal ControlSession S; }

    class InputStop : Exception { public readonly Msg Why; public InputStop(Msg why) { Why = why; } }

    public InputLook InputNow() {
        lock (L) {
            if (inputUse == null || inputUse.Over || inputUse.M == null || inputUse.S.S.Waiting || inputUse.S.S.Stopped) return null;
            InputLook l = new InputLook(); l.Peer = inputUse.Peer; l.Phase = inputUse.Phase; l.Detail = inputUse.Detail;
            return l;
        }
    }
    void SawPicture(string peer, Shot shot) { if (shot.View != null) lock (L) views[peer] = shot.View; }

    void EndInput(InputUse u, Msg end) {
        bool here;
        lock (L) {
            if (u.Over) return;
            u.Over = true;
            here = inputUse == u;
            if (here) inputUse = null; else if (inputOut.ContainsKey(u.Peer) && inputOut[u.Peer] == u) inputOut.Remove(u.Peer);
        }
        if (here) Hand.Watch(false, null);
        if (u.M != null) Finish(u.M, end ?? u.Summary);
    }
    void CloseIdleInput() {
        List<InputUse> all = new List<InputUse>();
        lock (L) { if (inputUse != null) all.Add(inputUse); all.AddRange(inputOut.Values); }
        foreach (InputUse u in all) {
            if (u.Busy || u.M == null) continue;
            if (u.S.S.Stopped) EndInput(u, new Msg(Stopped.Zh + " · " + u.Summary.Zh, Stopped.En + " · " + u.Summary.En));
            else if (Util.Now() - u.Last > InputIdleMs) EndInput(u, null);
        }
    }

    // ---- being asked ----
    void ServeInput(Channel ch, ControlSession s, Grant g, Dictionary<string, object> b, string from, string name, string agent) {
        InputUse u = null; bool fresh = false, mine = false;
        s.StartPing();
        try {
            List<string> raw = Strings(b, "steps");
            if (raw.Count == 0) throw Lang.Err("没有要做的步骤", "there are no steps");
            if (raw.Count > InputStep.Max) throw Lang.Err("一次最多 {0} 步", "at most {0} steps at once", InputStep.Max);
            List<InputStep> steps = raw.Select(InputStep.Parse).ToList();
            if (Hand.Locked) throw Lang.Err("那台电脑锁屏了（或正在等待管理员确认），操作不了", "that PC is locked (or showing an administrator prompt), nothing can be done");
            lock (L) {
                if (inputUse != null && inputUse.Peer != from) throw Lang.Err("那台电脑的鼠标键盘正由另一台电脑在用", "another PC is using the mouse and keyboard there");
                u = inputUse;
                if (u == null) { u = new InputUse(); u.Peer = from; u.Agent = agent; u.S = new Token(); u.S.S = new ControlSession(ch, false); u.S.S.Background = true; inputUse = u; fresh = true; }
                else if (u.Busy) throw Lang.Err("上一次操作还没做完", "the steps before haven't finished yet");
                u.Busy = mine = true;
                u.S.S.Also = s;
            }
            Run(delegate { try { while (true) ch.ReceiveJson(); } catch (Exception) { s.Stopped = true; } }); // notices "the other side hung up"
            if (fresh) {
                u.S.S.Waiting = true;
                u.M = Record(from, from, name, agent, "input", Lang.T("鼠标键盘", "Mouse and keyboard"), u.S.S);
                bool asked;
                if (!Allowed(g, name, agent, string.Join("\n", steps.Select(delegate (InputStep x) { return x.Label(null).ToString(); }).ToArray()), s, "input", "mouse", out asked)) {
                    Msg no = s.Stopped ? Lang.M("已取消", "cancelled") : Lang.M("已拒绝", "declined");
                    u.S.S.Waiting = false;
                    EndInput(u, no);
                    s.Last(Done(Lang.M("那台电脑上的人没有同意", "the person at that PC didn't approve it"), no, 0));
                    return;
                }
                u.S.S.Waiting = false;
                Hand.Watch(true, delegate { Run(delegate { PauseControl(from); }); });
                if (asked) { Thread.Sleep(500); u.Excused = Util.Now(); } // the click on "allow" itself
                u.Start = Util.Now();
                RaiseControl();
            } else if (!string.IsNullOrEmpty(agent)) u.Agent = agent;

            List<Msg> done = new List<Msg>();
            Msg note = Steps(u, s, steps, Math.Max(1, Math.Min(600, Util.Num(b, "timeout"))), from, done);
            byte[] png = null; Msg what = null;
            if (Util.Flag(b, "look") && !s.Stopped && !u.S.S.Stopped) {
                for (int i = 0; i < 4 && !s.Stopped; i++) Thread.Sleep(100); // let the screen settle
                try { Shot shot = Look(from); png = shot.Take(); what = shot.What; SawPicture(from, shot); }
                catch (LtException e) { note = note ?? e.M; }
            }
            object[] st = done.Select(delegate (Msg m) { return (object)new string[] { m.Zh, m.En }; }).ToArray();
            string nz = note == null ? "" : note.Zh, ne = note == null ? "" : note.En;
            int code = note == null ? 0 : 1;
            if (png == null) s.Last(new { k = "x", code = code, err = "", end = "", steps = st, note = nz, note_en = ne });
            else {
                s.Last(new { k = "data", size = png.Length, sha = Sha(new MemoryStream(png)), what = what.Zh, what_en = what.En, code = code, steps = st, note = nz, note_en = ne });
                for (int i = 0; i < png.Length; i += Chunk) {
                    byte[] part = new byte[Math.Min(Chunk, png.Length - i)];
                    Buffer.BlockCopy(png, i, part, 0, part.Length);
                    ch.Send(part, part.Length);
                }
            }
        } catch (Exception e) {
            Msg why = Msg.Of(e);
            try { s.Last(Done(why, new Msg("失败：" + why.Zh, "failed: " + why.En), 0)); } catch (Exception) { }
            if (fresh) EndInput(u, new Msg("失败：" + why.Zh, "failed: " + why.En));
        } finally {
            s.StopPing();
            if (mine) { lock (L) { u.Busy = false; u.Last = Util.Now(); } if (u.S.S.Stopped) CloseIdleInput(); else RaiseControl(); }
        }
    }
    // The same part of the screen as the last picture, if that is still there.
    Shot Look(string peer) {
        PicView v; lock (L) views.TryGetValue(peer, out v);
        Dictionary<string, object> b = new Dictionary<string, object>(); b["max"] = 1568;
        if (v != null && v.Window != IntPtr.Zero) {
            b["window"] = "0x" + v.Window.ToInt64().ToString("X");
            try { return new Shot(b); } catch (LtException) { b.Remove("window"); }
            b["screen"] = Shot.MonitorOf(v.Area);
        } else b["screen"] = v == null ? 1 : v.Screen;
        return new Shot(b);
    }

    // Returns why it stopped early, null when every step was done.
    Msg Steps(InputUse u, ControlSession s, List<InputStep> steps, long timeout, string from, List<Msg> done) {
        PicView v; lock (L) views.TryGetValue(from, out v);
        Action<string> phase = delegate (string p) { if (u.Phase != p) { u.Phase = p; RaiseControl(); } };
        Func<bool> over = delegate { return s.Stopped || u.S.S.Stopped || stopping; };
        Func<long, bool> calm = delegate (long limit) { // waits until the person here has left the mouse and keyboard alone
            long end = Util.Now() + limit * 1000;
            while (Util.Now() - Hand.LastHuman < InputCalmMs && Hand.LastHuman > u.Excused) { if (over() || Util.Now() > end) return false; phase("yield"); Thread.Sleep(100); }
            return !over();
        };
        Msg note = null;
        long began = 0;
        Action check = delegate {
            if (over()) throw new InputStop(Lang.M("被停止了", "it was stopped"));
            if (Hand.LastHuman > began) throw new InputStop(Lang.M("那台电脑前的人动了鼠标或键盘，先让开了。看新图再接着做", "someone at that PC used the mouse or keyboard, so it stepped aside. Look at the new picture before going on"));
            if (Hand.Locked) throw new InputStop(Lang.M("那台电脑锁屏了（或正在等待管理员确认）", "that PC is locked (or showing an administrator prompt)"));
        };
        int at = 0;
        try {
            if (!calm(timeout)) throw new InputStop(over() ? Lang.M("被停止了", "it was stopped") : Lang.M("那台电脑前有人在用，等了 {0} 秒，什么都没做", "someone is using that PC; waited {0} seconds and did nothing", timeout));
            if (v != null && Hand.LastHuman > v.At && Hand.LastHuman > u.Excused) throw new InputStop(Lang.M("上次截图之后有人动过那台电脑，什么都没做。看新图再操作", "someone used that PC after the last picture, so nothing was done. Look at the new picture first"));
            if (steps.Any(delegate (InputStep x) { return x.Places; })) {
                if (v == null) throw new InputStop(Lang.M("要先截图（lt shot）：坐标以最近一张截图为准", "take a screenshot first (lt shot): positions refer to the last picture"));
                if (v.Window != IntPtr.Zero) { Msg no = Hand.Refuses(v.Window); if (no != null) throw new InputStop(no); try { Hand.Front(v.Window); } catch (LtException) { } } // what isn't covered can still be clicked
            }
            began = Util.Now();
            phase("act");
            foreach (InputStep x in steps) {
                at++;
                check();
                Msg label = One(x, v, check, phase);
                done.Add(label);
                u.Add(label, true);
                Changed(u.M, false);
                RaiseControl();
            }
        } catch (InputStop e) { note = e.Why; }
        catch (LtException e) { note = e.M; }
        catch (System.ComponentModel.Win32Exception e) { note = new Msg(e.Message, e.Message); }
        if (note == null) return null;
        u.Add(new Msg("· " + note.Zh, "· " + note.En), false);
        Changed(u.M, false);
        RaiseControl();
        if (began > 0 && Hand.LastHuman > began && !over()) calm(Math.Min(timeout, 60)); // so the picture shows what the person left
        if (u.Phase == "yield") phase("act");
        Msg n = at > 0 ? new Msg("停在第 " + at + " 步：" + note.Zh, "stopped at step " + at + ": " + note.En) : note;
        return n;
    }
    static Point Place(PicView v, int x, int y) {
        Rectangle a = v.Area;
        if (v.Window != IntPtr.Zero) {
            Rectangle now = Shot.FrameOf(v.Window);
            if (now.Width < 1) throw new InputStop(Lang.M("那个窗口已经关了，先重新截图", "that window has been closed, take a new screenshot first"));
            if (Math.Abs(now.Width - a.Width) > 2 || Math.Abs(now.Height - a.Height) > 2) throw new InputStop(Lang.M("那个窗口的大小变了，先重新截图", "that window has a different size now, take a new screenshot first"));
            a = new Rectangle(now.X, now.Y, a.Width, a.Height);
        }
        if (x < 0 || y < 0 || x >= v.W || y >= v.H) throw new InputStop(Lang.M("({0}, {1}) 不在截图里（{2} × {3}）", "({0}, {1}) is outside the picture ({2} x {3})", x, y, v.W, v.H));
        return new Point(a.X + (int)((x + 0.5) * a.Width / v.W), a.Y + (int)((y + 0.5) * a.Height / v.H));
    }
    Msg One(InputStep x, PicView v, Action check, Action<string> phase) {
        Point p = x.Places ? Place(v, x.X, x.Y) : Point.Empty;
        string at = null;
        if (x.Places) {
            Point2(p, false);
            Msg no = Hand.Refuses(p); if (no != null) throw new InputStop(no);
            if (v.Window != IntPtr.Zero && !Hand.Belongs(p, v.Window)) throw new InputStop(Lang.M("那个窗口在这里被挡住了，也没能切到前面。截整个屏幕（--screen 1）再操作", "that window is covered here and couldn't be brought to the front. Take a picture of the whole screen (--screen 1) instead"));
            if (x.Verb != "drag" && x.Verb != "scroll") at = Hand.NameAt(p);
            check();
            Hand.MoveTo(p);
            Thread.Sleep(30);
            no = Hand.Refuses(p); if (no != null) throw new InputStop(no);
            check();
        }
        switch (x.Verb) {
            case "click": case "double": case "triple": case "right": case "middle":
                Holding(x.Mods, delegate {
                    for (int i = 0; i < x.Clicks; i++) { if (i > 0) Thread.Sleep(60); Hand.Button(x.Button, true); Thread.Sleep(20); Hand.Button(x.Button, false); }
                });
                Point2(p, true);
                Thread.Sleep(80);
                break;
            case "drag": {
                Point q = Place(v, x.X2, x.Y2);
                Msg no = Hand.Refuses(q); if (no != null) throw new InputStop(no);
                Hand.Button("left", true);
                try { for (int i = 1; i <= 15; i++) { Thread.Sleep(15); check(); Hand.MoveTo(new Point(p.X + (q.X - p.X) * i / 15, p.Y + (q.Y - p.Y) * i / 15)); } Thread.Sleep(60); }
                finally { Hand.Button("left", false); }
                Point2(q, true);
                break;
            }
            case "scroll":
                for (int i = 0; i < x.N; i++) { if (i > 0) { Thread.Sleep(25); check(); } Hand.Wheel(x.Text == "up" || x.Text == "right" ? 1 : -1, x.Text == "left" || x.Text == "right"); }
                break;
            case "type":
                for (int i = 0; i < x.Text.Length; i++) {
                    if (i % 16 == 0 || i > 0 && (x.Text[i - 1] == '\n' || x.Text[i - 1] == '\t')) Keys(phase, true);
                    check();
                    if (x.Text[i] == '\n') { Hand.Key(13, true); Hand.Key(13, false); Thread.Sleep(60); }
                    else if (x.Text[i] == '\t') { Hand.Key(9, true); Hand.Key(9, false); Thread.Sleep(60); }
                    else Hand.Char(x.Text[i]);
                    Thread.Sleep(4);
                }
                break;
            case "key": {
                bool safe = x.Mods.Length == 0 || x.Mods.Length == 1 && x.Mods[0] == 0x10;
                safe = safe && (x.Key == 9 || x.Key == 13 || x.Key == 27); // leaving a password box is fine
                for (int i = 0; i < x.N; i++) {
                    if (i > 0) { Thread.Sleep(40); check(); }
                    Keys(phase, !safe);
                    Holding(x.Mods, delegate { Hand.Key(x.Key, true); Thread.Sleep(15); Hand.Key(x.Key, false); });
                }
                Thread.Sleep(60);
                break;
            }
            case "wait": for (int left = x.N; left > 0; left -= 100) { Thread.Sleep(Math.Min(100, left)); check(); } break;
            case "focus": {
                Shot.Win w = Shot.Find(x.Text);
                Msg no = Hand.Refuses(w.H); if (no != null) throw new InputStop(no);
                Hand.Front(w.H);
                return Lang.M("切到“{0}”", "Switch to \"{0}\"", Util.Cut(w.Title, 60));
            }
        }
        return x.Label(at);
    }
    void Keys(Action<string> phase, bool text) {
        Msg no = Hand.RefusesKeys(); if (no != null) throw new InputStop(no);
        if (text && Hand.InPassword) { phase("password"); throw new InputStop(Lang.M("这里是密码框，没有输入，要由人在那台电脑上输", "this is a password box, so nothing was typed. Someone at that PC has to do it")); }
    }
    void Holding(int[] mods, Action act) {
        int held = 0;
        try { foreach (int m in mods) { Hand.Key(m, true); held++; } act(); }
        finally { while (held > 0) Hand.Key(mods[--held], false); }
    }
    void Point2(Point p, bool clicked) { Action<Point, bool> h = InputPoint; if (h != null) h(p, clicked); }

    // ---- asking another PC ----
    InputUse OutUse(string peer, string agent, ControlSession s) {
        InputUse u, old = null;
        lock (L) {
            inputOut.TryGetValue(peer, out u);
            if (u != null && (u.Agent != agent || u.S.S.Stopped)) { old = u; u = null; }
            if (u != null && u.Busy) throw Lang.Err("上一次操作还没做完", "the steps before haven't finished yet");
            if (u != null) { u.S.S = s; u.Busy = true; sessions[u.M.Id] = s; }
        }
        if (old != null) EndInput(old, null);
        if (u != null) return u;
        u = new InputUse(); u.Peer = peer; u.Agent = agent; u.S = new Token(); u.S.S = s; u.Busy = true;
        u.M = Record(peer, Cfg.Id, Cfg.Name, agent, "input", Lang.T("鼠标键盘", "Mouse and keyboard"), s);
        lock (L) inputOut[peer] = u;
        return u;
    }
    void OutDone(InputUse u, Dictionary<string, object> f) {
        foreach (System.Collections.IList r in Rows(f, "steps")) if (r.Count > 1) u.Add(new Msg(Util.Cut(Convert.ToString(r[0]), 200), Util.Cut(Convert.ToString(r[1]), 200)), true);
        Msg note = Msg.Wire(f, "note");
        if (note != null && note.Zh != "") u.Add(new Msg("· " + note.Zh, "· " + note.En), false);
        Changed(u.M, false);
    }
    void OutEnd(InputUse u, Msg failed) { // failed: the request itself didn't work
        lock (L) { u.Busy = false; u.Last = Util.Now(); }
        if (u.S.S.Stopped) EndInput(u, new Msg(Stopped.Zh + " · " + u.Summary.Zh, Stopped.En + " · " + u.Summary.En));
        else if (failed != null && u.Count == 0) EndInput(u, failed);
        else RaiseControl();
    }
}

// The real mouse and keyboard.
class Hand : IHand {
    [StructLayout(LayoutKind.Sequential)] struct MOUSE { public int X, Y; public int Data; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBD { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT { [FieldOffset(0)] public int Type; [FieldOffset(8)] public MOUSE M; [FieldOffset(8)] public KEYBD K; } // 64 bit; LanTalk is built for any CPU and runs as 64 bit on all supported systems
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLL { public POINT P; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KBLL { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUT { public int Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr H; public uint Message; public IntPtr W, L; public uint Time; public POINT P; }
    [StructLayout(LayoutKind.Sequential)] struct GUI { public int Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public int L, T, R, B; }
    delegate IntPtr Hook(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint kind);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool alt);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool on);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUI g);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr r);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUT l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Hook f, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int kind, IntPtr b, int n, out int need);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, int i);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    static readonly IntPtr Tag = (IntPtr)0x4C544B31; // marks what LanTalk does; everything else counts as the person, remote desktop programs included
    long human, own;
    Thread hooks; uint hookThread; Action stopKey; Hook mouse, keyboard; // the delegates must stay alive while hooked
    readonly object gate = new object();
    public bool Locked { get { return Shot.Locked; } }
    public long LastHuman { get { return Interlocked.Read(ref human); } }

    public void Watch(bool on, Action key) {
        lock (gate) {
            stopKey = key;
            if (!on) { if (hooks != null) { PostThreadMessage(hookThread, 0x12, IntPtr.Zero, IntPtr.Zero); hooks = null; } return; }
            if (hooks != null) return;
            LASTINPUT l = new LASTINPUT(); l.Size = Marshal.SizeOf(l);
            if (GetLastInputInfo(ref l)) { long t = Util.Now() - unchecked((uint)Environment.TickCount - l.Time); if (t > Interlocked.Read(ref own) + 200) Interlocked.Exchange(ref human, Math.Max(LastHuman, t)); }
            ManualResetEvent ready = new ManualResetEvent(false);
            hooks = new Thread(delegate () {
                hookThread = GetCurrentThreadId();
                mouse = OnMouse; keyboard = OnKey;
                IntPtr a = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0), b = SetWindowsHookEx(13, keyboard, GetModuleHandle(null), 0);
                ready.Set();
                MSG m; while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(a); UnhookWindowsHookEx(b);
            });
            hooks.IsBackground = true; hooks.Priority = ThreadPriority.AboveNormal; hooks.Start();
            ready.WaitOne(3000);
        }
    }
    void Human() { Interlocked.Exchange(ref human, Util.Now()); }
    POINT was; long wasAt; int moved;
    IntPtr OnMouse(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            MSLL m = (MSLL)Marshal.PtrToStructure(l, typeof(MSLL));
            if (m.Extra != Tag) {
                if (w.ToInt64() != 0x200) Human();
                else { // a mouse lying on a desk trembles now and then
                    long now = Util.Now();
                    POINT p; GetCursorPos(out p);
                    if (now - wasAt > 600) moved = 0;
                    moved += Math.Abs(m.P.X - p.X) + Math.Abs(m.P.Y - p.Y); wasAt = now; was = p;
                    if (moved >= 12) Human();
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }
    IntPtr OnKey(int code, IntPtr w, IntPtr l) {
        if (code >= 0) {
            KBLL k = (KBLL)Marshal.PtrToStructure(l, typeof(KBLL));
            if (k.Extra != Tag) {
                Human();
                if (k.Vk == 32 && (w.ToInt64() == 0x100 || w.ToInt64() == 0x104) && GetAsyncKeyState(0x11) < 0 && GetAsyncKeyState(0x12) < 0) { Action a = stopKey; if (a != null) a(); return (IntPtr)1; }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    void Send(INPUT i) {
        if (SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT))) != 1) throw Lang.Err("Windows 不让操作鼠标键盘", "Windows doesn't let the mouse and keyboard be used now");
        Interlocked.Exchange(ref own, Util.Now());
    }
    void Mouse(uint flags, int x, int y, int data) { INPUT i = new INPUT(); i.Type = 0; i.M.Flags = flags; i.M.X = x; i.M.Y = y; i.M.Data = data; i.M.Extra = Tag; Send(i); }
    public void MoveTo(Point p) {
        Shot.RealPixels(delegate {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            POINT c = new POINT();
            for (int t = 0; t < 3; t++) {
                Mouse(0x1 | 0x8000 | 0x4000, (int)(((long)(p.X - vx) * 65536 + vw / 2) / vw), (int)(((long)(p.Y - vy) * 65536 + vh / 2) / vh), 0);
                Thread.Sleep(8);
                GetCursorPos(out c);
                if (Math.Abs(c.X - p.X) <= 2 && Math.Abs(c.Y - p.Y) <= 2) return true;
            }
            throw Lang.Err("鼠标到不了那个位置", "the pointer can't get to that place");
        });
    }
    public void Button(string which, bool down) {
        bool swapped = GetSystemMetrics(23) != 0;
        uint f = which == "middle" ? (down ? 0x20u : 0x40u) : (which == "left") != swapped ? (down ? 0x2u : 0x4u) : (down ? 0x8u : 0x10u);
        Mouse(f, 0, 0, 0);
    }
    public void Wheel(int clicks, bool sideways) { Mouse(sideways ? 0x1000u : 0x800u, 0, 0, clicks * 120); }
    public void Key(int vk, bool down) {
        bool ext = vk >= 33 && vk <= 40 || vk == 45 || vk == 46 || vk == 0x5B || vk == 0x5D || vk == 0x2C;
        INPUT i = new INPUT(); i.Type = 1; i.K.Vk = (ushort)vk; i.K.Scan = (ushort)MapVirtualKey((uint)vk, 0); i.K.Flags = (ext ? 1u : 0) | (down ? 0 : 2u); i.K.Extra = Tag;
        Send(i);
    }
    public void Char(char c) {
        for (int up = 0; up < 2; up++) { INPUT i = new INPUT(); i.Type = 1; i.K.Scan = c; i.K.Flags = 4u | (up == 1 ? 2u : 0); i.K.Extra = Tag; Send(i); }
    }
    static IntPtr Root(IntPtr h) { IntPtr r = GetAncestor(h, 2); return r == IntPtr.Zero ? h : r; }
    // Windows doesn't let just any program change the front window; each of these works in some situations.
    public void Front(IntPtr w) {
        IntPtr root = Root(w);
        if (IsIconic(root)) { ShowWindow(root, 9); Thread.Sleep(250); }
        for (int t = 0; t < 6; t++) {
            if (Root(GetForegroundWindow()) == root) { if (t > 0) Thread.Sleep(150); return; }
            uint pid, front = GetWindowThreadProcessId(GetForegroundWindow(), out pid), me = GetCurrentThreadId();
            if (t == 0) SetForegroundWindow(root);
            else if (t == 1 || t == 3) {
                bool joined = front != 0 && front != me && !IsHungAppWindow(GetForegroundWindow()) && AttachThreadInput(me, front, true);
                try { BringWindowToTop(root); SetForegroundWindow(root); } finally { if (joined) AttachThreadInput(me, front, false); }
            }
            else if (t == 2) { Key(0x12, true); try { SetForegroundWindow(root); } finally { Key(0x12, false); } } // with Alt held it is allowed
            else if (t == 4) SwitchToThisWindow(root, true);
            else { ShowWindow(root, 6); Thread.Sleep(250); ShowWindow(root, 9); } // minimized and back
            Thread.Sleep(150);
        }
        if (Root(GetForegroundWindow()) != root) throw Lang.Err("没能把那个窗口切到前面", "couldn't bring that window to the front");
    }

    static int level = -1;
    static int Level(uint pid) { // integrity; int.MaxValue when it can't be asked, which means it is above ours
        IntPtr p = OpenProcess(0x1000, false, pid), t;
        if (p == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5 ? int.MaxValue : 0;
        try {
            if (!OpenProcessToken(p, 8, out t)) return int.MaxValue;
            try {
                int need; GetTokenInformation(t, 25, IntPtr.Zero, 0, out need);
                IntPtr b = Marshal.AllocHGlobal(Math.Max(need, 64));
                try {
                    if (!GetTokenInformation(t, 25, b, need, out need)) return int.MaxValue;
                    IntPtr sid = Marshal.ReadIntPtr(b);
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, Marshal.ReadByte(GetSidSubAuthorityCount(sid)) - 1));
                } finally { Marshal.FreeHGlobal(b); }
            } finally { CloseHandle(t); }
        } finally { CloseHandle(p); }
    }
    public Msg Refuses(IntPtr h) {
        if (h == IntPtr.Zero) return null;
        uint pid; GetWindowThreadProcessId(h, out pid);
        uint me = (uint)Process.GetCurrentProcess().Id;
        if (pid == me) return Lang.M("那是 LanTalk 自己的窗口，不能替人操作", "that is LanTalk's own window, only the person there may use it");
        if (level < 0) level = Level(me);
        if (Level(pid) > level) return Lang.M("那是以管理员身份运行的窗口，操作不了", "that window runs as administrator and can't be used");
        return null;
    }
    public Msg Refuses(Point p) { POINT q = new POINT(); q.X = p.X; q.Y = p.Y; return Shot.RealPixels(delegate { return Refuses(WindowFromPoint(q)); }); }
    public bool Belongs(Point p, IntPtr w) {
        POINT q = new POINT(); q.X = p.X; q.Y = p.Y;
        IntPtr h = Shot.RealPixels(delegate { return WindowFromPoint(q); });
        uint a, b; GetWindowThreadProcessId(h, out a); GetWindowThreadProcessId(w, out b);
        return a == b || Root(h) == Root(w);
    }
    public Msg RefusesKeys() { return Refuses(GetForegroundWindow()); }

    // Asking another program can hang, so it gets a thread and a limit.
    static T Timed<T>(Func<T> f, int ms, T otherwise) {
        T r = otherwise; object done = new object(); bool ok = false;
        Thread t = new Thread(delegate () { try { T v = Shot.RealPixels(f); lock (done) { r = v; ok = true; } } catch (Exception) { } });
        t.IsBackground = true; t.Start();
        t.Join(ms);
        lock (done) return ok ? r : otherwise;
    }
    public string NameAt(Point p) {
        return Timed(delegate {
            AutomationElement e = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
            string n = (e.Current.Name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return Util.Cut(n, 40);
        }, 700, "");
    }
    public bool InPassword {
        get {
            GUI g = new GUI(); g.Size = Marshal.SizeOf(g);
            if (GetGUIThreadInfo(0, ref g) && g.Focus != IntPtr.Zero) {
                StringBuilder c = new StringBuilder(100); GetClassName(g.Focus, c, 100);
                if (c.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0) {
                    if ((GetWindowLong(g.Focus, -16) & 0x20) != 0) return true;
                    IntPtr r; if (SendMessageTimeout(g.Focus, 0xD2, IntPtr.Zero, IntPtr.Zero, 2, 300, out r) != IntPtr.Zero && r != IntPtr.Zero) return true;
                }
            }
            return Timed(delegate { AutomationElement e = AutomationElement.FocusedElement; return e != null && e.Current.IsPassword; }, 900, false);
        }
    }
}
}
