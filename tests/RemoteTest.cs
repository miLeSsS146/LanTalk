// Tests for Remote.cs and for grants per capability, part of Test.cs. A asks, B is controlled.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace LanTalk {
static partial class Test {
    static Grant G(string screen, string read, string write, string run) { return Grant.Of(screen, read, write, run); }
    static void RemoteTests(Node A, Node B, string da, string db) {
        int rc = 0; string o, ib = B.Cfg.Id, ia = A.Cfg.Id, work = Path.Combine(root, "远程 2"), q = "\"";
        Directory.CreateDirectory(work);
        Func<string, string> lt = delegate (string args) { return LtErr(da, out rc, args + " --to PC-B", null); };
        Func<string, string, string> ltIn = delegate (string args, string stdin) { return LtErr(da, out rc, args + " --to PC-B", stdin); };

        // ---- capabilities ----
        string f = Path.Combine(work, "a.txt"); File.WriteAllText(f, "一\n二\n三\n");
        Grant g = G("", "", "", "allow"); Check(g.Screen == "allow" && g.Read == "allow" && g.Write == "allow", "caps: running commands unasked includes the rest, and says so");
        g = new Grant(); g.Mode = "ask"; g.Tidy(); Check(g.Mode == null && g.Run == "ask" && g.Screen == "ask", "caps: an older grant keeps its meaning");
        g = G("x", "Allow", null, "ask"); Check(g.Cap("screen") == "" && g.Cap("read") == "" && g.Cap("write") == "" && g.Cap("run") == "ask", "caps: unknown values mean no");
        B.SetGrant(ia, "PC-A", A.PublicKey, G("", "allow", "", ""));
        o = lt("cat " + q + f + q); Check(rc == 0 && o == "一\n二\n三\n", "caps: reading is allowed");
        o = lt("run 1"); Check(rc == 125 && Err.Contains("运行命令"), "caps: running isn't");
        o = lt("run --bg 1"); Check(rc == 125 && Err.Contains("运行命令"), "caps: running in the background isn't");
        o = lt("jobs"); Check(rc == 125 && Err.Contains("运行命令"), "caps: jobs need it too");
        o = lt("shot"); Check(rc == 125 && Err.Contains("看屏幕"), "caps: the screen isn't");
        o = ltIn("edit " + q + f + q + " -", "<<<<<<<\n二\n=======\n2\n>>>>>>>"); Check(rc == 125 && Err.Contains("写文件") && File.ReadAllText(f).Contains("二"), "caps: writing isn't");
        o = lt("sync " + q + work + q + " " + q + work + "x" + q); Check(rc == 125 && Err.Contains("写文件") && !Directory.Exists(work + "x"), "caps: syncing into it isn't");
        o = lt("info"); Check(rc == 0 && o.Contains("系统：Windows") && o.Contains("处理器：") && o.Contains(Environment.MachineName), "info: facts about the PC");
        Check(o.Contains("git  git version"), "info: finds tools: " + Regex.Matches(o, "(?m)^    (\\S+)").Cast<Match>().Aggregate("", delegate (string a, Match m) { return a + m.Groups[1].Value + " "; }));
        o = lt("info --json --ascii"); Check(rc == 0 && o.Contains("\"key\":\"os\"") && o.All(delegate (char c) { return c < 128; }), "info: --json");
        B.SetGrant(ia, "PC-A", A.PublicKey, G("", "ask", "", ""));
        o = lt("info"); Check(rc == 125 && Err.Contains("没有同意"), "info: asks when everything asks");
        int asked = 0; string op = null, cap = null; bool longer = false;
        Action<ControlAsk> h = delegate (ControlAsk a) { asked++; op = a.Op; cap = a.Cap; a.Answer(true, longer); };
        B.ControlAsked += h;
        B.SetGrant(ia, "PC-A", A.PublicKey, G("ask", "ask", "ask", "ask"));
        longer = true; lt("cat " + q + f + q); Check(rc == 0 && asked == 1 && op == "cat" && cap == "read", "caps: the question names the capability");
        lt("cat " + q + f + q); Check(rc == 0 && asked == 1, "caps: 'for a while' holds for reading");
        longer = false; lt("run 1"); Check(rc == 0 && asked == 2 && cap == "run", "caps: ...but not for running");
        B.ControlAsked -= h;

        // ---- expiry and pause ----
        g = G("", "allow", "", ""); g.Until = Util.Now() + 1500; B.SetGrant(ia, "PC-A", A.PublicKey, g);
        lt("cat " + q + f + q); int r1 = rc; Thread.Sleep(1600); lt("cat " + q + f + q); Check(r1 == 0 && rc == 125 && Err.Contains("控制权限"), "grant: ends at its time");
        Check(Until(delegate { return B.GrantFor(ia) == null; }, 8), "grant: and is removed then");
        B.SetGrant(ia, "PC-A", A.PublicKey, "allow");
        string beat = Path.Combine(work, "beat.txt"), loop = "while(1){[IO.File]::WriteAllText('" + beat + "',[DateTime]::Now.Ticks);Start-Sleep -m 100}";
        ltIn("run --bg -", loop); Check(rc == 0 && Until(delegate { return File.Exists(beat); }, 10) && B.Controlling().Count == 1, "pause: something is running: " + B.Controlling().Count);
        int told = 0; Action ch = delegate { told++; }; B.ControlChanged += ch;
        B.PauseControl(ia); Check(Until(delegate { return B.Controlling().Count == 0; }, 8) && Frozen(beat) && told > 0, "pause: Stop ends what runs, background jobs too");
        o = lt("run 1"); Check(rc == 125 && Err.Contains("恢复"), "pause: nothing new starts");
        Check(File.ReadAllText(Path.Combine(db, "config.json")).Contains("\"Paused\":true"), "pause: survives a restart");
        B.ResumeControl(ia); o = lt("run \"'back'\""); Check(rc == 0 && o.Contains("back"), "pause: Resume");
        B.ControlChanged -= ch;

        // ---- background jobs ----
        Stopwatch sw = Stopwatch.StartNew();
        o = ltIn("run --bg --cwd " + q + work + q + " -", "'开始 😀'; (Get-Location).Path; Start-Sleep 3; [Console]::Error.WriteLine('错'); cmd /c 'exit 7'");
        Match jm = Regex.Match(o, "任务 (\\d+)"); string id = jm.Groups[1].Value;
        Check(rc == 0 && jm.Success && sw.Elapsed.TotalSeconds < 3, "jobs: returns at once");
        o = lt("jobs"); Check(rc == 0 && Regex.IsMatch(o, "(?m)^" + id + "  运行中"), "jobs: listed as running");
        Until(delegate { return lt("log " + id).Contains("开始 😀"); }, 5); o = lt("log " + id);
        Check(rc == 0 && o.Contains("开始 😀") && o.Contains(work) && !o.Contains("错") && Err.Contains("运行中"), "jobs: output so far");
        string pos = Regex.Match(Err, "--from (\\d+)").Groups[1].Value;
        o = lt("log " + id + " --follow --from " + pos); Check(rc == 7 && o.Trim() == "错" && Err.Contains("退出码 7"), "jobs: --follow goes on where it was, waits and passes the exit code (" + rc + ")");
        o = lt("log " + id + " -n 1"); Check(rc == 0 && o == "错\r\n" || o == "错\n", "jobs: last line only");
        o = lt("jobs"); Check(o.Contains("退出码 7"), "jobs: listed as ended");
        ChatMsg mb = B.History(ia, 1)[0]; Check(mb.Text.Contains("后台任务 " + id) && mb.State == "ok" && mb.Error.StartsWith("退出码 7") && B.JobOutput(mb.Id).Contains("开始 😀"), "jobs: one entry in the chat, updated at the end");
        File.Delete(beat); ltIn("run --bg -", "& powershell -NoProfile -Command \"" + loop + "\""); id = Regex.Match(Lt(da, out rc, "jobs --to PC-B"), "(?m)^(\\d+)  运行中").Groups[1].Value;
        Until(delegate { return File.Exists(beat); }, 10);
        sw.Restart(); lt("log " + id + " --follow --timeout 1"); Check(rc == 124 && sw.Elapsed.TotalSeconds < 5, "jobs: --follow gives up at its timeout, the job goes on");
        o = lt("stop " + id); Check(rc == 0 && Frozen(beat), "jobs: stop ends it and its children");
        o = lt("stop " + id); Check(rc == 125 && Err.Contains("已经结束"), "jobs: stopping twice is an error");
        o = lt("log 999"); Check(rc == 125 && Err.Contains("没有后台任务"), "jobs: unknown job");
        File.Delete(beat); ltIn("run --bg --timeout 2 -", loop); Check(Until(delegate { return File.Exists(beat); }, 10) && Until(delegate { return B.Controlling().Count == 0; }, 10) && Frozen(beat) && Lt(da, out rc, "jobs --to PC-B").Contains("超时"), "jobs: timeout");
        File.Delete(beat); ltIn("run --bg -", loop); Until(delegate { return File.Exists(beat); }, 10);
        B.StopControl(B.History(ia, 1)[0].Id); Check(Until(delegate { return B.Controlling().Count == 0; }, 8) && Frozen(beat), "jobs: can be stopped at the controlled PC");
        File.Delete(beat); ltIn("run --bg -", loop); Until(delegate { return File.Exists(beat); }, 10);
        B.SetGrant(ia, "PC-A", A.PublicKey, G("allow", "allow", "allow", "")); Check(Until(delegate { return B.Controlling().Count == 0; }, 8) && Frozen(beat), "jobs: withdrawing 'run' ends them");
        B.SetGrant(ia, "PC-A", A.PublicKey, "allow");
        string late = Path.Combine(work, "late.txt");
        ltIn("run --bg -", "Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep 3; Set-Content ''" + late + "'' late'");
        Check(Until(delegate { return File.Exists(late); }, 12), "jobs: a program started on purpose keeps running");
        byte[] u = Encoding.UTF8.GetBytes("a中😀"); Check(Node.WholeUtf8(u, 8) == 8 && Node.WholeUtf8(u, 7) == 4 && Node.WholeUtf8(u, 5) == 4 && Node.WholeUtf8(u, 3) == 1 && Node.WholeUtf8(u, 4) == 4, "jobs: characters aren't split");

        // ---- cat and edit ----
        o = lt("cat --from 2 -n 1 --number " + q + f + q); Check(rc == 0 && o == "     2\t二\n" && Err.Contains("共 3 行"), "cat: part of a file with numbers");
        o = lt("cat " + q + Path.Combine(work, "nope") + q); Check(rc == 125 && Err.Contains("没有这个文件"), "cat: missing file");
        string bin = Path.Combine(work, "b.bin"); File.WriteAllBytes(bin, new byte[] { 1, 0, 2 }); o = lt("cat " + q + bin + q); Check(rc == 125 && Err.Contains("不是文本"), "cat: binary file");
        o = lt("cat relative.txt"); Check(rc == 125 && Err.Contains("写完整"), "cat: path must be complete");
        string big = Path.Combine(work, "big.txt"); File.WriteAllText(big, string.Join("\n", Enumerable.Range(1, 60000).Select(delegate (int i) { return "行 " + i; }).ToArray()));
        o = lt("cat -n 60000 " + q + big + q); Check(rc == 0 && o.Contains("行 60000") && o.Split('\n').Length == 60001, "cat: big file");
        byte[] win = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("one\r\n  two\r\nthree\r\n")).ToArray();
        File.WriteAllBytes(f, win);
        o = ltIn("edit " + q + f + q + " -", "<<<<<<<\none\n  two\n=======\n一\n  <=> $x `y` \"z\"\n>>>>>>>\n<<<<<<<\nthree\n=======\nthree\nfour\n>>>>>>>\n");
        Check(rc == 0 && File.ReadAllBytes(f).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("一\r\n  <=> $x `y` \"z\"\r\nthree\r\nfour\r\n"))), "edit: two edits, CRLF and BOM kept: " + o.Trim());
        mb = B.History(ia, 1)[0]; Check(mb.Op == "edit" && mb.Text.EndsWith("\n-    1  one\n-    2    two\n+    1  一\n+    2    <=> $x `y` \"z\"\n     3  three\n     3  three\n+    4  four"), "edit: the chat shows the changed lines with numbers: " + mb.Text.Replace("\n", "|"));
        File.WriteAllText(f, "a b c\nsame\nx\n"); o = ltIn("edit " + q + f + q + " -", "<<<<<<<\nb\n=======\nB\n>>>>>>>\n<<<<<<<\nsame\nx\n=======\nsame\ny\nz\n>>>>>>>");
        Check(rc == 0 && File.ReadAllText(f) == "a B c\nsame\ny\nz\n" && B.History(ia, 1)[0].Text.EndsWith("\n-    1  a b c\n+    1  a B c\n     2  same\n     2  same\n-    3  x\n+    3  y\n+    4  z"), "edit: part of a line shows the whole line, unchanged lines stay out: " + B.History(ia, 1)[0].Text.Replace("\n", "|"));
        B.SetGrant(ia, "PC-A", A.PublicKey, G("", "", "ask", "")); o = ltIn("edit " + q + f + q + " -", "<<<<<<<\nnope\n=======\nB\n>>>>>>>"); Check(rc == 125 && Err.Contains("没有同意"), "edit: what is in a file isn't told before someone agreed");
        B.SetGrant(ia, "PC-A", A.PublicKey, "allow");
        File.WriteAllText(f, "x x"); o = ltIn("edit " + q + f + q + " -", "<<<<<<<\nx\n=======\ny\n>>>>>>>"); Check(rc == 125 && Err.Contains("2 次") && File.ReadAllText(f) == "x x", "edit: ambiguous text is refused");
        o = ltIn("edit --all " + q + f + q + " -", "<<<<<<<\nx\n=======\ny\n>>>>>>>"); Check(rc == 0 && File.ReadAllText(f) == "y y", "edit: --all");
        o = ltIn("edit " + q + f + q + " -", "<<<<<<<\ny y\n=======\nz\n>>>>>>>\n<<<<<<<\nnope\n=======\nz\n>>>>>>>"); Check(rc == 125 && Err.Contains("什么都没改") && File.ReadAllText(f) == "y y", "edit: all or nothing");
        o = ltIn("edit " + q + f + q + " -", "<<<<<<<\ny y\n=======\n"); Check(rc == 1 && o.Contains("没有写完"), "edit: incomplete input");
        Encoding ansi = Encoding.Default; File.WriteAllText(f, "中文 abc", ansi);
        o = ltIn("edit " + q + f + q + " -", "<<<<<<<\nabc\n=======\n改\n>>>>>>>"); Check(rc == 0 && File.ReadAllText(f, ansi) == "中文 改", "edit: " + ansi.WebName + " stays " + ansi.WebName);
        o = ltIn("edit " + q + f + q + " -", "<<<<<<<\n改\n=======\n😀\n>>>>>>>"); Check(rc == 125 && File.ReadAllText(f, ansi) == "中文 改", "edit: characters the encoding can't hold are refused");
        File.WriteAllText(f, "held");
        using (FileStream hold = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { Thread tr = new Thread(delegate () { Thread.Sleep(1500); hold.Dispose(); }); tr.Start(); o = ltIn("edit " + q + f + q + " -", "<<<<<<<\nheld\n=======\nfree\n>>>>>>>"); }
        Check(rc == 0 && File.ReadAllText(f) == "free", "edit: a file that is held for a moment is tried again");
        string of = Path.Combine(root, "old.txt"), nf = Path.Combine(root, "new.txt"); File.WriteAllText(of, "=======\n"); File.WriteAllText(nf, ">>>>>>>\n"); File.WriteAllText(f, "a\n=======\nb\n");
        o = lt("edit " + q + f + q + " --old " + q + of + q + " --new " + q + nf + q); Check(rc == 0 && File.ReadAllText(f) == "a\n>>>>>>>\nb\n", "edit: from files, marks and all");

        // ---- sync ----
        string src = Path.Combine(root, "来源"), dst = Path.Combine(work, "目标"); Directory.CreateDirectory(Path.Combine(src, "子 目录")); Directory.CreateDirectory(Path.Combine(src, ".git"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "1"); File.WriteAllText(Path.Combine(src, "子 目录", "b.txt"), "2"); File.WriteAllBytes(Path.Combine(src, "c.bin"), Crypto.Random(700000)); File.WriteAllBytes(Path.Combine(src, "empty"), new byte[0]); File.WriteAllText(Path.Combine(src, ".git", "HEAD"), "x"); File.WriteAllText(Path.Combine(src, "x.log"), "x");
        string sync = "sync --skip *.log " + q + src + q + " " + q + dst + q;
        o = lt(sync + " --dry"); Check(rc == 0 && o.Contains("只比较：新增 4") && !Directory.Exists(dst), "sync: --dry changes nothing");
        o = lt(sync); Check(rc == 0 && o.Contains("新增 子 目录/b.txt") && o.Contains("新增 4，更新 0") && Hash(Path.Combine(src, "c.bin")) == Hash(Path.Combine(dst, "c.bin")) && new FileInfo(Path.Combine(dst, "empty")).Length == 0 && !Directory.Exists(Path.Combine(dst, ".git")) && !File.Exists(Path.Combine(dst, "x.log")), "sync: first copy, skips .git and *.log");
        Check(B.History(ia, 1)[0].Text.Contains("+ a.txt"), "sync: the chat lists the changes");
        File.WriteAllText(Path.Combine(src, "a.txt"), "1b"); DateTime t0 = File.GetLastWriteTimeUtc(Path.Combine(dst, "c.bin"));
        o = lt(sync); Check(rc == 0 && o.Contains("更新 a.txt") && o.Contains("新增 0，更新 1，删除 0，没变 3") && File.ReadAllText(Path.Combine(dst, "a.txt")) == "1b" && File.GetLastWriteTimeUtc(Path.Combine(dst, "c.bin")) == t0, "sync: only what changed");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "theirs"); File.WriteAllText(Path.Combine(src, "a.txt"), "mine"); File.WriteAllText(Path.Combine(src, "子 目录", "b.txt"), "2b");
        o = lt(sync); Check(rc == 1 && Err.Contains("两边都改过") && Err.Contains("a.txt") && File.ReadAllText(Path.Combine(dst, "a.txt")) == "theirs" && File.ReadAllText(Path.Combine(dst, "子 目录", "b.txt")) == "2b", "sync: a file changed over there is left alone, the rest goes");
        o = lt(sync + " --force"); Check(rc == 0 && File.ReadAllText(Path.Combine(dst, "a.txt")) == "mine", "sync: --force takes the source's version");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "only there"); o = lt(sync); Check(rc == 1 && Err.Contains("只有那边改过") && Err.Contains("--pull") && !Err.Contains("两边都改过") && File.ReadAllText(Path.Combine(dst, "a.txt")) == "only there", "sync: changed only over there, says so");
        File.Delete(Path.Combine(dst, "a.txt")); o = lt(sync); Check(rc == 1 && Err.Contains("那边已经删掉") && !Err.Contains("改过") && !File.Exists(Path.Combine(dst, "a.txt")), "sync: deleted over there, says so");
        o = lt(sync + " --force"); Check(rc == 0 && File.ReadAllText(Path.Combine(dst, "a.txt")) == "mine", "sync: --force puts it back");
        File.Delete(Path.Combine(src, "c.bin")); File.Delete(Path.Combine(src, "empty")); File.WriteAllText(Path.Combine(dst, "empty"), "changed"); File.WriteAllText(Path.Combine(dst, "theirs.txt"), "t");
        o = lt(sync); Check(rc == 0 && File.Exists(Path.Combine(dst, "c.bin")), "sync: deletes nothing by itself");
        o = lt(sync + " --delete"); Check(rc == 1 && Err.Contains("没有删，本机已经删掉") && Err.Contains("empty") && o.Contains("删除 c.bin") && !File.Exists(Path.Combine(dst, "c.bin")) && File.Exists(Path.Combine(dst, "empty")) && File.Exists(Path.Combine(dst, "theirs.txt")), "sync: --delete only removes what was synced and untouched");
        o = lt("sync --delete --skip *.log --skip a.txt " + q + src + q + " " + q + dst + q); Check(File.Exists(Path.Combine(dst, "a.txt")), "sync: skipped files are never deleted");
        string other = Path.Combine(work, "别处"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "a.txt"), "someone's");
        o = lt("sync --skip *.log " + q + src + q + " " + q + other + q); Check(rc == 1 && Err.Contains("以前没同步过") && File.ReadAllText(Path.Combine(other, "a.txt")) == "someone's" && File.Exists(Path.Combine(other, "子 目录", "b.txt")), "sync: files never synced before are not overwritten");
        string back = Path.Combine(root, "拉回");
        o = lt("sync --pull " + q + dst + q + " " + q + back + q); Check(rc == 0 && File.ReadAllText(Path.Combine(back, "theirs.txt")) == "t" && File.ReadAllText(Path.Combine(back, "子 目录", "b.txt")) == "2b", "sync: --pull");
        File.WriteAllText(Path.Combine(back, "a.txt"), "local edit"); File.WriteAllText(Path.Combine(dst, "a.txt"), "remote edit"); File.Delete(Path.Combine(dst, "theirs.txt"));
        o = lt("sync --pull --delete " + q + dst + q + " " + q + back + q); Check(rc == 1 && Err.Contains("两边都改过") && Err.Contains("本机的改动") && File.ReadAllText(Path.Combine(back, "a.txt")) == "local edit" && !File.Exists(Path.Combine(back, "theirs.txt")), "sync: --pull keeps local edits, --delete follows");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "mine"); o = lt("sync --pull " + q + dst + q + " " + q + back + q); Check(rc == 1 && Err.Contains("只有本机改过") && File.ReadAllText(Path.Combine(back, "a.txt")) == "local edit", "sync: --pull, changed only here, says so");
        File.Delete(Path.Combine(back, "a.txt")); o = lt("sync --pull " + q + dst + q + " " + q + back + q); Check(rc == 1 && Err.Contains("本机已经删掉") && !File.Exists(Path.Combine(back, "a.txt")), "sync: --pull, deleted here, says so");
        o = lt("sync " + q + src + q + " C:\\"); Check(rc == 125, "sync: never a whole drive"); o = lt("sync --pull C:\\ " + q + back + q); Check(rc == 125, "sync: never a whole drive, the other way");
        Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "sync: no partial files left behind");
        foreach (string evil in new string[] { "../../evil9.txt", "C:/evil9.txt", "a/../../evil9.txt", "NUL", "x\\..\\y" }) {
            string body = Util.ToJson(new { op = "push", path = dst, files = new object[] { new object[] { evil, 1, "AA==", "" } }, gone = new object[0], agent = "x" });
            Dictionary<string, object> r = ForgeX(A, B, body); Check(Util.Str(r, "err").Contains("文件名"), "sync: refuses " + evil);
        }

        // ---- screen ----
        B.SetGrant(ia, "PC-A", A.PublicKey, G("allow", "", "", ""));
        if (LanTalk.Shot.Locked) Console.WriteLine("SKIP  shot: the screen is locked");
        else {
            string png = Path.Combine(root, "shot", "s.png");
            o = lt("shot --screen 1 " + q + png + q); using (Image i = Image.FromFile(png)) Check(rc == 0 && o.StartsWith(png) && Math.Max(i.Width, i.Height) <= 1568 && o.Contains("显示器 1"), "shot: " + o.Trim().Replace("\n", " | "));
            Check(A.History(ib, 1)[0].Path == png && B.History(ia, 1)[0].Text.Contains("看屏幕：显示器 1"), "shot: recorded on both PCs");
            o = lt("shot --screen 1 " + q + png + q); Check(rc == 125 && Err.Contains("--force"), "shot: doesn't overwrite by itself");
            o = lt("shot --full --force " + q + png + q); using (Image i = Image.FromFile(png)) Check(rc == 0 && i.Width >= 640 && o.Contains("整个屏幕") && o.Contains(i.Width + " × " + i.Height), "shot: whole screen in full size");
            o = lt("shot --screen 99"); Check(rc == 125 && Err.Contains("显示器"), "shot: no such monitor");
            o = lt("shot"); Check(rc == 0 && o.StartsWith(A.Cfg.SaveDir) && File.Exists(o.Split('\n')[0]), "shot: picks its own file name");
            Form w = null;
            Thread ui = new Thread(delegate () { w = new Form(); w.Text = "LanTalk 测试窗口 42"; w.BackColor = Color.FromArgb(10, 200, 30); w.StartPosition = FormStartPosition.Manual; w.ShowInTaskbar = false; w.Bounds = new Rectangle(-4000, -4000, 400, 300); Application.Run(w); });
            ui.SetApartmentState(ApartmentState.STA); ui.IsBackground = true; ui.Start();
            Until(delegate { return LanTalk.Shot.Windows().Exists(delegate (LanTalk.Shot.Win x) { return x.Title == "LanTalk 测试窗口 42"; }); }, 5); Thread.Sleep(300);
            o = lt("shot --list"); Check(rc == 0 && o.Contains("测试窗口 42") && o.Contains("显示器 1"), "shot: --list");
            Color c42 = Color.Empty; Size s42 = Size.Empty; // Windows takes a moment to draw a new window
            Until(delegate { lt("shot --force --window \"测试窗口 42\" " + q + png + q); using (Bitmap i = new Bitmap(png)) { c42 = i.GetPixel(i.Width / 2, i.Height / 2); s42 = i.Size; } return c42.G == 200; }, 15);
            Check(rc == 0 && c42.R == 10 && c42.G == 200 && c42.B == 30 && s42.Width > 300 && s42.Width <= 400, "shot: one window, even off the screen: " + c42 + " " + s42);
            w.BeginInvoke((MethodInvoker)w.Close);
            o = lt("shot --window nope-nope-nope"); Check(rc == 125 && Err.Contains("没有标题"), "shot: no such window");
        }
        Lang.Set("en"); A.Cfg.Lang = "en"; A.SaveConfig();
        try { // everything above, in English
            B.SetGrant(ia, "PC-A", A.PublicKey, "allow");
            o = lt("info"); Check(rc == 0 && o.Contains("Windows: Windows") && !Chinese(Regex.Replace(o, "(?m)^(Computer|Account|Disks|LanTalk|    ).*$", "")), "lang: info in English");
            o = lt("run --bg 1"); Check(o.Contains("Started in the background: job ") && !Chinese(o), "lang: background job in English");
            o = lt("jobs"); Check(!Chinese(Regex.Replace(o, "(?m)  .*$", "").Replace("\n", "")) && (o.Contains("exit code") || o.Contains("running")), "lang: job list in English");
            o = ltIn("edit " + q + f + q + " -", "<<<<<<<\nnope\n=======\nz\n>>>>>>>"); Check(Err.Contains("isn't in the file") && !Chinese(Err), "lang: edit error in English");
            File.WriteAllText(Path.Combine(dst, "a.txt"), "zz"); o = lt("sync --dry " + q + src + q + " " + q + dst + q); Check(o.Contains("Compare only: ") && !Chinese(Regex.Replace(o, "(?m)^(new|updated|deleted) .*$", "")), "lang: sync in English: " + o.Trim().Split('\n').Last()); Check(rc == 1 && Err.Contains("changed only over there") && !Chinese(Err), "lang: what sync left alone, in English");
            B.SetGrant(ia, "PC-A", A.PublicKey, G("allow", "", "", "")); o = lt("run 1"); Check(Err.Contains("\"Run commands\"") && !Chinese(Err), "lang: missing permission in English");
        } finally { Lang.Set("zh"); A.Cfg.Lang = "zh"; A.SaveConfig(); }
        // a program left running must not keep the port: LanTalk couldn't start again
        Node D = Make("PC-D", 47906, B.Code, 47901); D.Start();
        Check(Until(delegate { return A.Peers().Exists(delegate (Peer p) { return p.Name == "PC-D" && p.Online; }) && D.Peers().Exists(delegate (Peer p) { return p.Name == "PC-A" && p.Online; }); }, 10), "restart: fourth node");
        D.SetGrant(ia, "PC-A", A.PublicKey, "allow");
        LtErr(da, out rc, "run --to PC-D -", "$i = New-Object Diagnostics.ProcessStartInfo 'ping.exe', '-n 12 127.0.0.1'; $i.UseShellExecute = $false; $i.CreateNoWindow = $true; [void][Diagnostics.Process]::Start($i)");
        D.Dispose(); Thread.Sleep(500);
        Node D2 = new Node(Path.Combine(root, "PC-D"), IPAddress.Loopback); string why = "";
        try { D2.Start(); } catch (System.Net.Sockets.SocketException e) { why = e.Message; }
        Check(why == "", "restart: the port is free again while a program it started is still running " + why);
        D2.Dispose(); A.ForgetPeer(D.Cfg.Id); B.ForgetPeer(D.Cfg.Id);
        B.ClearGrants();
        string old = Path.Combine(root, "old-grant"); Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "config.json"), "{\"Control\":[{\"Id\":\"" + ia + "\",\"Name\":\"x\",\"Key\":\"k\",\"Mode\":\"allow\"},{\"Id\":\"" + ib + "\",\"Name\":\"y\",\"Key\":\"k\",\"Mode\":\"nonsense\"}]}");
        Node n = new Node(old, IPAddress.Loopback); Check(n.GrantFor(ia).Run == "allow" && n.GrantFor(ia).Screen == "allow" && n.GrantFor(ib) == null, "caps: a stored older grant is carried over, nonsense isn't");
    }
    // A signed request from A with a body of its own making; the first reply that isn't a ping.
    static Dictionary<string, object> ForgeX(Node A, Node B, string body) {
        using (Channel ch = Channel.Connect("127.0.0.1", B.Cfg.Port, Crypto.Master(B.Code))) {
            ch.SendJson(new { t = "control", id = Util.NewId(), from = A.Cfg.Id, name = A.Cfg.Name, port = A.Cfg.Port, to = B.Cfg.Id });
            Dictionary<string, object> r = ch.ReceiveJson();
            ch.SendJson(new { body = body, sig = SignAs(A, B.Cfg.Id, A.Cfg.Id, Util.Str(r, "nonce"), body) });
            do r = ch.ReceiveJson(); while (Util.Str(r, "k") == "p");
            return r;
        }
    }
}
}
