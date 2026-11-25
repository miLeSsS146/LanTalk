// Remote control tests, part of Test.cs. A asks, B is controlled, C is a paired PC without a grant.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LanTalk {
static partial class Test {
    static string Err;
    static string LtErr(string data, out int code, string args, string stdin) { // like Lt, keeps stderr in Err
        ProcessStartInfo si = new ProcessStartInfo(ltExe, args + " --data \"" + data + "\"");
        si.UseShellExecute = false; si.RedirectStandardOutput = si.RedirectStandardError = si.RedirectStandardInput = true; si.StandardOutputEncoding = si.StandardErrorEncoding = Encoding.UTF8; si.CreateNoWindow = true;
        using (Process p = Process.Start(si)) {
            StringBuilder e = new StringBuilder();
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs a) { if (a.Data != null) lock (e) e.AppendLine(a.Data); };
            p.BeginErrorReadLine();
            if (stdin != null) { byte[] b = Encoding.UTF8.GetBytes(stdin); p.StandardInput.BaseStream.Write(b, 0, b.Length); }
            p.StandardInput.Close();
            string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); code = p.ExitCode;
            Err = e.ToString();
            return o;
        }
    }
    static string SignAs(Node n, string to, string from, string nonce, string body) {
        byte[] d = (byte[])typeof(Node).GetMethod("Signed", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { to, from, nonce, body });
        return (string)typeof(Node).GetMethod("Sign", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(n, new object[] { d });
    }
    // Raw request claiming to come from `from`, signed by `signer`, optionally over a stale nonce. Returns the first reply after the signature.
    static Dictionary<string, object> Forge(Node from, Node signer, Node to, string body, string sentBody, string nonce) {
        using (Channel ch = Channel.Connect("127.0.0.1", to.Cfg.Port, Crypto.Master(to.Code))) {
            ch.SendJson(new { t = "control", id = Util.NewId(), from = from.Cfg.Id, name = from.Cfg.Name, port = from.Cfg.Port, to = to.Cfg.Id });
            Dictionary<string, object> r = ch.ReceiveJson();
            if (!Util.Flag(r, "ok")) return r;
            ch.SendJson(new { body = sentBody, sig = SignAs(signer, to.Cfg.Id, from.Cfg.Id, nonce ?? Util.Str(r, "nonce"), body) });
            return ch.ReceiveJson();
        }
    }
    static bool Frozen(string f) { // nothing writes to f any more
        Thread.Sleep(400);
        string a = File.ReadAllText(f); Thread.Sleep(900);
        return a == File.ReadAllText(f);
    }
    static void ControlTests(Node A, Node B, Node C, string da, string db) {
        int rc; string o, ib = B.Cfg.Id, ia = A.Cfg.Id, work = Path.Combine(root, "远程 目录"); Directory.CreateDirectory(work);
        string mark = Path.Combine(work, "mark.txt");
        o = LtErr(da, out rc, "run --to PC-B -", "Set-Content '" + mark + "' x");
        Check(rc == 125 && Err.Contains("控制权限") && !File.Exists(mark), "control: refused without a grant");
        Check(A.FetchKey(ib) == B.PublicKey && B.FetchKey(ia) == A.PublicKey && A.PublicKey != B.PublicKey, "control: each PC has its own key");

        B.SetGrant(ia, "PC-A", B.FetchKey(ia), "allow");
        o = LtErr(da, out rc, "run --to PC-B --cwd \"" + work + "\" -", "'你好 😀 \"引号\" $x'\n(Get-Location).Path\n[Console]::Error.WriteLine('出错了')\ncmd /c 'echo native & exit 7'");
        Check(rc == 7 && o.Contains("你好 😀 \"引号\" $x") && o.Contains(work) && o.Contains("native") && Err.Contains("出错了") && !Err.Contains("CLIXML"), "control: output, errors, folder and exit code come back (" + rc + ")");
        o = LtErr(da, out rc, "run --to PC-B \"Get-Item C:\\nope-nope\"", null); Check(rc == 1 && Err.Contains("nope-nope") && !Err.Contains("CLIXML"), "control: failing cmdlet is readable and exits 1");
        o = LtErr(da, out rc, "run --to PC-B -", "'中文' )"); Check(rc == 1 && Err.Contains("中文"), "control: a syntax error is readable");
        o = LtErr(da, out rc, "run --to PC-B --json --ascii -", "'中文'"); Check(rc == 0 && o.Contains("\"code\":0") && o.Contains("\\u4e2d") && o.All(delegate (char c) { return c < 128; }), "control: --json --ascii");
        ChatMsg ma = A.History(ib, 1)[0], mb = B.History(ia, 1)[0];
        Check(ma.Kind == "run" && mb.Kind == "run" && mb.Text == "'中文'" && mb.Error.StartsWith("退出码 0") && mb.Agent == "Claude", "control: recorded in the chat of both PCs");
        o = Lt(db, out rc, "inbox"); Check(!o.Contains("中文'"), "control: records stay out of the Claude inbox");
        o = LtErr(da, out rc, "run --to PC-B -", "[Console]::Out.Write('x' * 6000000)"); Check(rc == 0 && o.Length <= 4 * 1024 * 1024 && o.Length > 1000000 && Err.Contains("输出太长"), "control: huge output is cut, not endless (" + o.Length + ")");

        // stopping
        string beat = Path.Combine(work, "beat.txt"), child = "& powershell -NoProfile -Command \"while(1){[IO.File]::WriteAllText('" + beat + "',[DateTime]::Now.Ticks);Start-Sleep -m 100}\"";
        Stopwatch sw = Stopwatch.StartNew();
        o = LtErr(da, out rc, "run --to PC-B --timeout 3 -", child); Check(rc == 124 && sw.Elapsed.TotalSeconds < 9 && Frozen(beat), "control: timeout kills the command and its children");
        File.Delete(beat);
        ProcessStartInfo si = new ProcessStartInfo(ltExe, "run --to PC-B \"" + child.Replace("\"", "\\\"") + "\" --data \"" + da + "\""); si.UseShellExecute = false; si.CreateNoWindow = true;
        using (Process p = Process.Start(si)) { Until(delegate { return File.Exists(beat); }, 10); p.Kill(); }
        Check(File.Exists(beat) && Until(delegate { return B.History(ia, 1)[0].State == "ok"; }, 15) && Frozen(beat), "control: killing lt stops the remote command");
        foreach (bool atB in new bool[] { true, false }) {
            File.Delete(beat); int rc2 = 0; string err = null;
            Thread t = new Thread(delegate () { LtErr(da, out rc2, "run --to PC-B -", child); err = Err; }); t.Start();
            Until(delegate { return File.Exists(beat); }, 10);
            if (atB) B.StopControl(B.History(ia, 1)[0].Id); else A.StopControl(A.History(ib, 1)[0].Id);
            Check(t.Join(15000) && rc2 == 125 && err.Contains("停止") && Frozen(beat), "control: can be stopped on the " + (atB ? "controlled" : "asking") + " PC");
        }
        File.Delete(beat);
        Thread w = new Thread(delegate () { LtErr(da, out rc, "run --to PC-B -", child); }); w.Start();
        Until(delegate { return File.Exists(beat); }, 10);
        o = Lt(da, out rc, "list"); Check(rc == 0, "control: other commands work while one is running");
        B.SetGrant(ia, "PC-A", "", "");
        Check(w.Join(15000) && Frozen(beat), "control: withdrawing the grant stops what is running");
        B.SetGrant(ia, "PC-A", A.PublicKey, "allow");
        sw.Restart();
        o = LtErr(da, out rc, "run --to PC-B -", "Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep 4; Set-Content ''" + mark + "'' late'; 'started'");
        Check(rc == 0 && o.Contains("started") && sw.Elapsed.TotalSeconds < 5 && Until(delegate { return File.Exists(mark); }, 12), "control: a program started on purpose keeps running afterwards");
        File.Delete(mark);

        // pretending to be A: C knows the pairing code, but not A's key
        string body = Util.ToJson(new { op = "run", cmd = "Set-Content '" + mark + "' forged", cwd = "", timeout = 10, agent = "Claude" });
        Dictionary<string, object> r = Forge(A, C, B, body, body, null); Check(Util.Str(r, "err").Contains("身份核对失败"), "control: the pairing code alone can't pass as a granted PC");
        r = Forge(C, C, B, body, body, null); Check(!Util.Flag(r, "ok") && Util.Str(r, "err").Contains("控制权限"), "control: a paired PC without a grant is refused");
        r = Forge(A, A, B, body, body, Convert.ToBase64String(new byte[32])); Check(Util.Str(r, "err").Contains("身份核对失败"), "control: a signature over another challenge is refused");
        r = Forge(A, A, B, Util.ToJson(new { op = "run", cmd = "1" }), body, null); Check(Util.Str(r, "err").Contains("身份核对失败"), "control: a changed command is refused");
        Thread.Sleep(1500); Check(!File.Exists(mark), "control: none of the forged commands ran");

        // files
        string src = Path.Combine(root, "put.bin"), dst = Path.Combine(work, "新建", "put.bin"), back = Path.Combine(root, "back", "put.bin"); File.WriteAllBytes(src, Crypto.Random(3000000));
        o = LtErr(da, out rc, "put --to PC-B \"" + src + "\" \"" + dst + "\"", null); Check(rc == 0 && Hash(src) == Hash(dst), "control: put");
        o = LtErr(da, out rc, "get --to PC-B \"" + dst + "\" \"" + back + "\"", null); Check(rc == 0 && Hash(src) == Hash(back), "control: get");
        File.WriteAllText(src, "new"); File.WriteAllText(dst, "old");
        o = LtErr(da, out rc, "put --to PC-B \"" + src + "\" \"" + dst + "\"", null); Check(rc == 125 && Err.Contains("--force") && File.ReadAllText(dst) == "old", "control: put doesn't overwrite by itself");
        o = LtErr(da, out rc, "put --to PC-B --force \"" + src + "\" \"" + dst + "\"", null); Check(rc == 0 && File.ReadAllText(dst) == "new", "control: put --force does");
        o = LtErr(da, out rc, "get --to PC-B \"" + dst + "\" \"" + back + "\"", null); Check(rc == 125 && Err.Contains("--force"), "control: get doesn't overwrite by itself");
        o = LtErr(da, out rc, "put --to PC-B \"" + src + "\" relative.txt", null); Check(rc == 125 && Err.Contains("写完整"), "control: remote path must be complete");
        string zero = Path.Combine(root, "zero"), zback = Path.Combine(root, "zero2"); File.WriteAllBytes(zero, new byte[0]);
        LtErr(da, out rc, "put --to PC-B \"" + zero + "\" \"" + dst + ".0\"", null); int z = rc; LtErr(da, out rc, "get --to PC-B \"" + dst + ".0\" \"" + zback + "\"", null); Check(z == 0 && rc == 0 && new FileInfo(zback).Length == 0, "control: empty file both ways");
        Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "control: no partial files left behind");

        // asking first
        B.SetGrant(ia, "PC-A", A.PublicKey, "ask");
        o = LtErr(da, out rc, "run --to PC-B \"1\"", null); Check(rc == 125 && Err.Contains("没有同意"), "control: nobody there to ask means no");
        bool yes = false, longer = false; int asked = 0; string seen = null;
        Action<ControlAsk> h = delegate (ControlAsk a) { asked++; seen = a.PeerName + a.Text; a.Answer(yes, longer); };
        B.ControlAsked += h;
        o = LtErr(da, out rc, "run --to PC-B \"Set-Content '" + mark + "' x\"", null); Check(rc == 125 && asked == 1 && seen.Contains("PC-A") && seen.Contains("Set-Content") && !File.Exists(mark), "control: denied by the person at the PC");
        yes = true;
        o = LtErr(da, out rc, "run --to PC-B \"'ok'\"", null); Check(rc == 0 && o.Contains("ok") && Err.Contains("等那台电脑") && asked == 2, "control: approved once");
        longer = true; LtErr(da, out rc, "run --to PC-B \"1\"", null);
        B.ControlAsked -= h;
        o = LtErr(da, out rc, "run --to PC-B \"'again'\"", null); Check(rc == 0 && o.Contains("again") && asked == 3, "control: approved for a while");
        B.SetGrant(ia, "PC-A", A.PublicKey, "ask");
        bool closed = false;
        h = delegate (ControlAsk a) { a.Closed += delegate { closed = true; }; };
        B.ControlAsked += h;
        using (Process p = Process.Start(new ProcessStartInfo(ltExe, "run --to PC-B 1 --data \"" + da + "\"") { UseShellExecute = false, CreateNoWindow = true })) { Thread.Sleep(1500); p.Kill(); }
        Check(Until(delegate { return closed; }, 15), "control: the question goes away when the asker does");
        B.ControlAsked -= h;

        o = LtErr(da, out rc, "run --to nobody 1", null); Check(rc == 125 && Err.Contains("没有叫"), "control: unknown PC");
        o = LtErr(da, out rc, "run 1", null); Check(rc == 1 && o.Contains("--to"), "control: target is mandatory");
        string cfg = File.ReadAllText(Path.Combine(db, "config.json"));
        Check(cfg.Contains(A.PublicKey.Substring(0, 20).Replace("+", "\\u002b")) || cfg.Contains(A.PublicKey.Substring(0, 20)), "control: grant is saved");
        B.ClearGrants(); Check(B.GrantFor(ia) == null, "control: grants cleared");
    }
}
}
