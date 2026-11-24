// Loopback tests: three nodes plus an outsider with another pairing code. Run tests\run-tests.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LanTalk {
static partial class Test {
    static string root, ltExe;
    static int passed, failed;
    static void Check(bool ok, string what) { if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); }
    static bool Until(Func<bool> f, int seconds) {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (f()) return true; Thread.Sleep(50); }
        return f();
    }
    static Node Make(string name, int port, string code, params int[] others) {
        string d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        Node n = new Node(d, IPAddress.Loopback);
        n.Cfg.Name = name; n.Cfg.Port = port; n.Cfg.SaveDir = Path.Combine(root, name + "-收到");
        n.Cfg.Manual = others.Select(delegate (int p) { return "127.0.0.1:" + p; }).ToList();
        n.SetCode(code);
        return n;
    }
    static string Hash(string f) { using (SHA256 s = SHA256.Create()) using (FileStream fs = File.OpenRead(f)) return Convert.ToBase64String(s.ComputeHash(fs)); }
    static ChatMsg Last(Node n, string conv) { return n.History(conv, 1).FirstOrDefault(); }
    static string Lt(string data, out int code, string args) { return Lt(data, out code, args, null); }
    static string Lt(string data, out int code, string args, string stdin) {
        ProcessStartInfo si = new ProcessStartInfo(ltExe, args + " --data \"" + data + "\"");
        si.UseShellExecute = false; si.RedirectStandardOutput = true; si.RedirectStandardInput = true; si.StandardOutputEncoding = Encoding.UTF8; si.CreateNoWindow = true;
        using (Process p = Process.Start(si)) {
            if (stdin != null) { byte[] b = Encoding.UTF8.GetBytes(stdin); p.StandardInput.BaseStream.Write(b, 0, b.Length); }
            p.StandardInput.Close();
            string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); code = p.ExitCode; return o;
        }
    }
    static Dictionary<string, object> Evil(Node from, Node to, string id, string title, bool folder, string path, byte[] body) {
        Dictionary<string, object> f = new Dictionary<string, object>(); f["p"] = path; f["s"] = body.Length;
        Dictionary<string, object> h = new Dictionary<string, object>();
        h["t"] = "file"; h["id"] = id; h["from"] = from.Cfg.Id; h["name"] = from.Cfg.Name; h["port"] = from.Cfg.Port; h["to"] = to.Cfg.Id; h["time"] = Util.Now();
        h["text"] = title; h["total"] = body.Length; h["folder"] = folder; h["files"] = new object[] { f };
        using (Channel ch = Channel.Connect("127.0.0.1", to.Cfg.Port, Crypto.Master(from.Code))) {
            ch.SendJson(h);
            Dictionary<string, object> r = ch.ReceiveJson();
            if (!Util.Flag(r, "ok")) return r;
            ch.Send(body, body.Length);
            return ch.ReceiveJson();
        }
    }

    [STAThread] static int Main(string[] a) {
        root = a[0]; ltExe = a[1];
        if (a.Length > 2) { Shot(a[2]); return 0; }
        Directory.CreateDirectory(root);
        string code = Crypto.NewCode();
        Check(Crypto.NormalizeCode(code.ToLowerInvariant().Replace("-", " ")) == code && Crypto.NormalizeCode("123456") == null, "pairing code normalizes, weak codes rejected");
        foreach (string[] t in new string[][] { new string[] { "..\\..\\evil.txt", "evil.txt" }, new string[] { "C:\\Windows\\x", "C_\\Windows\\x" }, new string[] { "a/../../b", "a\\b" }, new string[] { "NUL.txt", "_NUL.txt" }, new string[] { "x.txt::$DATA", "x.txt__$DATA" }, new string[] { " ... ", "file" } })
            Check(Util.SafeRelative(t[0]) == t[1], "SafeRelative " + t[0] + " -> " + Util.SafeRelative(t[0]));

        Node A = Make("PC-A", 47901, code, 47902, 47903), B = Make("PC-B", 47902, code, 47901, 47903), C = Make("PC-C", 47903, code, 47901, 47902), X = Make("Outsider", 47904, Crypto.NewCode(), 47901, 47902, 47903);
        A.Cfg.Manual.Add("127.0.0.1:47904");
        A.Start(); B.Start(); C.Start(); X.Start();
        new IpcServer(A, Path.Combine(root, "PC-A")).Start(); new IpcServer(B, Path.Combine(root, "PC-B")).Start();
        Check(Until(delegate { return A.Peers().Count(delegate (Peer p) { return p.Online; }) == 2 && B.Peers().Count == 2 && C.Peers().Count == 2; }, 10), "three nodes find each other");
        Thread.Sleep(3500);
        Check(X.Peers().Count == 0 && A.Peers().Count == 2, "node with another pairing code stays invisible");

        string ia = A.Cfg.Id, ib = B.Cfg.Id, ic = C.Cfg.Id;
        ChatMsg m = A.SendText(ib, "你好 日本語 😀\n第二行");
        Check(Until(delegate { return m.State == "ok"; }, 5) && Last(B, ia).Text == m.Text && Last(C, ia) == null, "direct text arrives intact, only at its target");
        m = B.SendText(Node.All, "大家好");
        Check(Until(delegate { return m.State == "ok"; }, 5) && Last(A, Node.All).Text == "大家好" && Last(C, Node.All).Text == "大家好", "group text reaches everyone");

        // files
        string f = Path.Combine(root, "大文件.bin"); byte[] rnd = Crypto.Random(1024 * 1024);
        using (FileStream fs = File.Create(f)) for (int i = 0; i < 300; i++) fs.Write(rnd, 0, rnd.Length);
        Stopwatch sw = Stopwatch.StartNew();
        ChatMsg big = A.SendPath(ib, f);
        Thread.Sleep(150);
        ChatMsg quick = A.SendText(ib, "插队");
        bool q1 = Until(delegate { return quick.State == "ok"; }, 5), stillSending = big.State == "sending";
        Check(Until(delegate { return big.State == "ok"; }, 90), "300 MB file delivered, " + (300 / sw.Elapsed.TotalSeconds).ToString("0") + " MB/s");
        Check(q1 && stillSending, "text overtakes a running file transfer");
        ChatMsg got = B.History(ia, 10).Find(delegate (ChatMsg x) { return x.Id == big.Id; });
        Check(got.State == "ok" && Hash(got.Path) == Hash(f), "file content identical");
        string small = Path.Combine(root, "a.txt"); File.WriteAllText(small, "one");
        m = A.SendPath(ib, small); ChatMsg m2 = A.SendPath(ib, small);
        Check(Until(delegate { return m.State == "ok" && m2.State == "ok"; }, 8) && File.Exists(Path.Combine(B.Cfg.SaveDir, "a.txt")) && File.Exists(Path.Combine(B.Cfg.SaveDir, "a (1).txt")), "same name twice: second one is numbered, nothing overwritten");
        string empty = Path.Combine(root, "empty.dat"); File.WriteAllBytes(empty, new byte[0]);
        m = A.SendPath(ib, empty);
        Check(Until(delegate { return m.State == "ok"; }, 5) && File.Exists(Path.Combine(B.Cfg.SaveDir, "empty.dat")), "empty file");
        string d = Path.Combine(root, "项目 v1.0"); Directory.CreateDirectory(Path.Combine(d, "子目录")); File.WriteAllText(Path.Combine(d, "子目录", "x.txt"), "深"); File.WriteAllText(Path.Combine(d, "y.txt"), "浅");
        m = A.SendPath(Node.Ai, d, "Claude");
        Check(Until(delegate { return m.State == "ok"; }, 8) && File.ReadAllText(Path.Combine(B.Cfg.SaveDir, "项目 v1.0", "子目录", "x.txt")) == "深" && File.Exists(Path.Combine(C.Cfg.SaveDir, "项目 v1.0", "y.txt")), "folder to a group keeps its structure");
        m = A.SendPath(ib, small); File.Delete(small);
        Until(delegate { return m.State == "ok" || m.State == "fail"; }, 8);
        Check(m.State == "ok" || m.State == "fail" && m.Error != null, "source deleted while sending ends cleanly (" + m.State + ")");

        big = A.SendPath(ib, f);
        Until(delegate { return big.Done > 20 * 1024 * 1024; }, 10);
        A.Cancel(big);
        Check(Until(delegate { ChatMsg g = B.History(ia, 20).Find(delegate (ChatMsg x) { return x.Id == big.Id; }); return big.State == "cancel" && g != null && g.State == "fail"; }, 15), "cancel stops the transfer on both sides");
        Check(Until(delegate { return Directory.GetDirectories(B.Cfg.SaveDir, ".recv-*").Length == 0; }, 5), "no partial files left behind");

        // hostile but paired sender
        string idx = Util.NewId();
        Evil(A, B, idx, "..\\..\\evil.txt", false, "..\\..\\evil.txt", new byte[] { 1, 2, 3 });
        Evil(A, B, Util.NewId(), "dir", true, "dir/../../../evil2.txt", new byte[] { 1, 2, 3 });
        Evil(A, B, Util.NewId(), "C:\\evil3.txt", false, "C:\\evil3.txt", new byte[] { 1 });
        Thread.Sleep(300);
        Check(!File.Exists(Path.Combine(root, "evil.txt")) && !Directory.GetFiles(root, "evil*", SearchOption.AllDirectories).Any(delegate (string p) { return !p.StartsWith(B.Cfg.SaveDir + "\\"); }) && !File.Exists("C:\\evil3.txt"), "path traversal stays inside the receive folder");
        bool refused = false;
        try { using (Channel ch = Channel.Connect("127.0.0.1", 47902, Crypto.Master(X.Code))) { ch.SendJson(new { t = "text", id = Util.NewId(), from = ia, to = ib, text = "fake" }); ch.ReceiveJson(); } } catch (Exception) { refused = true; }
        Check(refused && !B.History(ia, 50).Exists(delegate (ChatMsg x) { return x.Text == "fake"; }), "wrong pairing code is refused");
        Dictionary<string, object> wrong;
        using (Channel ch = Channel.Connect("127.0.0.1", 47902, Crypto.Master(code))) { ch.SendJson(new { t = "text", id = Util.NewId(), from = ia, to = ic, text = "relayed" }); wrong = ch.ReceiveJson(); }
        Check(!Util.Flag(wrong, "ok"), "message addressed to another PC is refused");
        string dup = Util.NewId();
        for (int i = 0; i < 2; i++) using (Channel ch = Channel.Connect("127.0.0.1", 47902, Crypto.Master(code))) { ch.SendJson(new { t = "text", id = dup, from = ia, to = ib, name = "PC-A", port = 47901, text = "dup", time = Util.Now() }); ch.ReceiveJson(); }
        Check(B.History(ia, 100).Count(delegate (ChatMsg x) { return x.Id == dup; }) == 1, "resent message shows once");
        using (TcpClient t = new TcpClient("127.0.0.1", 47902)) { byte[] junk = Crypto.Random(500); t.GetStream().Write(junk, 0, 500); }
        using (UdpClient u = new UdpClient()) { byte[] junk = Crypto.Random(200); u.Send(junk, 200, "127.0.0.1", 47902); }
        m = A.SendText(ib, "still alive");
        Check(Until(delegate { return m.State == "ok"; }, 5), "survives garbage on both ports");

        // command line
        int rc; string da = Path.Combine(root, "PC-A"), db = Path.Combine(root, "PC-B");
        string o = Lt(da, out rc, "list"); Check(rc == 0 && o.Contains("[在线] PC-B"), "lt list");
        Lt(db, out rc, "inbox"); // clear what the folder test left
        o = Lt(da, out rc, "send --to PC-B \"编译好了吗？\""); Check(rc == 0 && o.StartsWith("已送达"), "lt send: " + o.Trim());
        o = Lt(da, out rc, "send -", "多行\n\"引号\" $x `y`\n"); Check(rc == 0, "lt send from stdin to the Claude channel");
        o = Lt(db, out rc, "inbox --json"); Check(o.Contains("编译好了吗？") && o.Contains("\\\"引号\\\" $x `y`") && o.Contains("\"agent\":\"Claude\"") && !o.Contains("插队"), "lt inbox: Claude messages only, human private chat left out");
        o = Lt(db, out rc, "inbox"); Check(o.Contains("没有消息"), "lt inbox is empty the second time");
        Thread th = new Thread(delegate () { Thread.Sleep(1500); C.SendText(Node.Ai, "我是用户，插一句"); }); th.Start();
        sw.Restart(); o = Lt(db, out rc, "wait --timeout 20"); Check(rc == 0 && o.Contains("用户@PC-C") && sw.Elapsed.TotalSeconds < 8, "lt wait wakes up on a message: " + o.Replace("\n", " | "));
        o = Lt(db, out rc, "wait --timeout 1"); Check(rc == 3, "lt wait times out with exit code 3");
        File.WriteAllText(small, "cli"); o = Lt(da, out rc, "file --to PC-B \"" + small + "\""); Check(rc == 0, "lt file: " + o.Trim());
        o = Lt(db, out rc, "wait --timeout 5 --json --ascii"); Check(rc == 0 && o.All(delegate (char c) { return c < 128; }) && o.Contains("a (") , "lt file shows up with its saved path, --ascii");
        o = Lt(da, out rc, "send --to nobody hi"); Check(rc == 1 && o.Contains("没有叫"), "lt: unknown PC is an error");
        o = Lt(Path.Combine(root, "none"), out rc, "list"); Check(rc == 2 && o.Contains("没有运行"), "lt: app not running is an error");

        ControlTests(A, B, C, da, db);

        // offline queue, restart
        B.Dispose();
        Check(Until(delegate { return !A.Find(ib).Online; }, 5), "closing one is seen at once");
        m = A.SendText(ib, "离线消息"); m2 = A.SendPath(ib, small);
        o = Lt(da, out rc, "send --to PC-B later"); Check(rc == 4 && o.Contains("等待：PC-B"), "lt send to an offline PC reports queued: " + o.Trim());
        Thread.Sleep(1500);
        Check(m.State == "queued" && m2.State == "queued", "messages wait while the target is offline");
        A.Dispose(); Thread.Sleep(500);
        Node A2 = new Node(da, IPAddress.Loopback), B2 = new Node(db, IPAddress.Loopback);
        A2.Start();
        Check(A2.History(ib, 100).Count(delegate (ChatMsg x) { return x.State == "queued"; }) == 3, "queue survives a restart");
        B2.Start();
        Check(Until(delegate { return B2.History(ia, 100).Exists(delegate (ChatMsg x) { return x.Text == "离线消息"; }) && A2.History(ib, 100).All(delegate (ChatMsg x) { return x.Pending.Count == 0; }); }, 25), "queued text and file delivered after it comes back");
        Check(B2.History(ia, 100).Exists(delegate (ChatMsg x) { return x.Text == "编译好了吗？" && x.Agent == "Claude"; }), "history survives a restart");
        Check(!File.ReadAllText(Path.Combine(da, "config.json")).Contains(code), "pairing code is not stored in plain text");
        A2.Dispose(); B2.Dispose(); C.Dispose(); X.Dispose();
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    // Renders the window off-screen into a PNG, to look at the layout.
    static void Shot(string file) {
        Program.InitUi();
        string code = Crypto.NewCode();
        Node A = Make("笔记本", 47911, code, 47912, 47913), B = Make("台式机", 47912, code, 47911), C = Make("第三台", 47913, code, 47911);
        B.Cfg.Manual.Add("127.0.0.1:47913"); C.Cfg.Manual.Add("127.0.0.1:47912");
        A.Start(); B.Start(); C.Start();
        Until(delegate { return A.Peers().Count == 2 && B.Peers().Count == 2; }, 10);
        MainForm form = new MainForm(A, Path.Combine(root, "笔记本"));
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.ShowInTaskbar = false; form.Show();
        string pic = Path.Combine(root, "示意图.png");
        using (Bitmap b = new Bitmap(600, 300)) { using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.SteelBlue); g.FillEllipse(Brushes.Orange, 200, 60, 200, 180); } b.Save(pic); }
        string doc = Path.Combine(root, "需求说明.docx"); File.WriteAllBytes(doc, new byte[123456]);
        B.SendText(Node.Ai, "接口改完了：/api/items 现在返回分页字段 next。笔记本那边可以联调。", "Claude"); Pump(900);
        A.SendText(Node.Ai, "收到，我先跑一遍测试，再把日志发你。", "Claude"); Pump(900);
        B.SendText(Node.Ai, "测试数据用昨天那份。"); Pump(900);
        A.SendText(Node.Ai, "好的，别动 main 分支。很长的链接也要能换行：https://example.com/" + new string('a', 120)); Pump(900);
        C.SendPath(Node.Ai, pic, "Claude"); Pump(1500);
        A.SendPath(Node.Ai, doc, "Claude"); Pump(1500);
        C.SendText(A.Cfg.Id, "在吗"); Pump(900);
        C.Dispose(); Pump(900);
        Save(form, file);
        using (SettingsForm s = new SettingsForm(A, true)) { s.StartPosition = FormStartPosition.Manual; s.Location = new Point(-5000, -5000); s.ShowInTaskbar = false; s.Show(); Pump(300); Save(s, file.Replace(".png", "-settings.png")); }
        A.Dispose(); B.Dispose();
    }
    static void Pump(int ms) { DateTime end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(20); } }
    static void Save(Form f, string file) { using (Bitmap b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(file); } }
}
}
