// Remote control: run a PowerShell command on another PC, read or write a file there.
// Off by default. The person at the controlled PC grants it per PC ("ask" every time, or "allow"), and the grant is
// tied to that PC's own signing key: the shared pairing code alone is never enough. Every request is signed over a
// one-time challenge, shows up in the chat of both PCs and can be stopped from either side.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LanTalk {

public class Grant {
    public string Id, Name, Key, Mode; // Key: public key this grant is tied to   Mode: ask | allow
}

// A request waiting for the person at this PC.
public class ControlAsk {
    public const int Seconds = 60;
    public string PeerId, PeerName, Agent, Text;
    public event Action Closed; // answered, timed out, or the other side went away
    readonly ManualResetEvent done = new ManualResetEvent(false);
    bool approved, forAWhile, closed;
    public void Answer(bool ok, bool longer) { lock (done) { if (closed) return; approved = ok; forAWhile = ok && longer; done.Set(); } }
    internal bool Wait(Func<bool> gone, out bool longer) {
        DateTime end = DateTime.UtcNow.AddSeconds(Seconds);
        while (!done.WaitOne(200)) if (DateTime.UtcNow > end || gone()) break;
        lock (done) closed = true;
        longer = forAWhile;
        Action c = Closed;
        if (c != null) c();
        return approved;
    }
}

public partial class Node {
    const int MaxCommand = 8000, MaxOutput = 4 * 1024 * 1024, MaxControls = 8;
    public event Action<ControlAsk> ControlAsked;
    readonly Dictionary<string, ControlSession> sessions = new Dictionary<string, ControlSession>(); // by message id
    readonly Dictionary<string, DateTime> shortGrants = new Dictionary<string, DateTime>();          // "ask" peers allowed for a while
    CngKey signKey;
    int asking;

    // One side of a running request.
    class ControlSession {
        readonly Channel ch;
        readonly object sending = new object();
        readonly bool client;
        bool quiet;
        Timer ping;
        public volatile bool Stopped;
        public ControlSession(Channel c, bool isClient) { ch = c; client = isClient; }
        public void Send(object o) { lock (sending) { if (!quiet) ch.SendJson(o); } }
        public void SendSafe(object o) { try { Send(o); } catch (Exception) { if (!client) Stopped = true; } } // the asking side finds out by reading
        public void StartPing() { ping = new Timer(delegate { SendSafe(new { k = "p" }); }, null, 5000, 5000); }
        public void StopPing() { Timer t = ping; ping = null; if (t != null) t.Dispose(); lock (sending) { } } // returns once no ping is being written
        public void Last(object o) { StopPing(); lock (sending) { if (quiet) return; quiet = true; ch.SendJson(o); } }
        public void Stop() { Stopped = true; if (client) ch.Dispose(); } // a closed connection makes the other side stop as well
    }

    // ---- identity ----
    void LoadKey() {
        lock (L) {
            if (signKey != null) return;
            if (!string.IsNullOrEmpty(Cfg.KeyProtected))
                try { signKey = CngKey.Import(Convert.FromBase64String(Crypto.Unprotect(Cfg.KeyProtected)), CngKeyBlobFormat.EccPrivateBlob); return; }
                catch (CryptographicException) { } // copied from another account: this PC needs its own key
                catch (FormatException) { }
            CngKeyCreationParameters p = new CngKeyCreationParameters(); p.ExportPolicy = CngExportPolicies.AllowPlaintextExport;
            signKey = CngKey.Create(CngAlgorithm.ECDsaP256, null, p);
            Cfg.KeyProtected = Crypto.Protect(Convert.ToBase64String(signKey.Export(CngKeyBlobFormat.EccPrivateBlob)));
        }
        SaveConfig();
    }
    public string PublicKey { get { LoadKey(); lock (L) return Convert.ToBase64String(signKey.Export(CngKeyBlobFormat.EccPublicBlob)); } }
    public static string Fingerprint(string key) {
        using (SHA256 s = SHA256.Create()) {
            string h = BitConverter.ToString(s.ComputeHash(Convert.FromBase64String(key)), 0, 10).Replace("-", "");
            return string.Join("-", Enumerable.Range(0, 5).Select(delegate (int i) { return h.Substring(i * 4, 4); }).ToArray());
        }
    }
    static byte[] Signed(string to, string from, string nonce, string body) { return Encoding.UTF8.GetBytes("LanTalk-control-v1\n" + to + "\n" + from + "\n" + nonce + "\n" + body); }
    string Sign(byte[] data) { LoadKey(); lock (L) using (ECDsaCng e = new ECDsaCng(signKey)) return Convert.ToBase64String(e.SignData(data)); }
    static bool Verify(string key, byte[] data, string sig) {
        try { using (CngKey k = CngKey.Import(Convert.FromBase64String(key), CngKeyBlobFormat.EccPublicBlob)) using (ECDsaCng e = new ECDsaCng(k)) return e.VerifyData(data, Convert.FromBase64String(sig)); }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    // ---- grants ----
    public Grant GrantFor(string peerId) { lock (L) return Cfg.Control.Find(delegate (Grant g) { return g.Id == peerId; }); }
    // mode: "" removes the grant. Stops whatever that PC is running here unless it may go on.
    public void SetGrant(string peerId, string name, string key, string mode) {
        lock (L) {
            Cfg.Control.RemoveAll(delegate (Grant g) { return g.Id == peerId; });
            shortGrants.Remove(peerId);
            if (mode != "") { Grant g = new Grant(); g.Id = peerId; g.Name = name; g.Key = key; g.Mode = mode; Cfg.Control.Add(g); }
        }
        SaveConfig();
        if (mode == "") StopFrom(peerId);
    }
    public void ClearGrants() { foreach (Grant g in Cfg.Control.ToArray()) SetGrant(g.Id, "", "", ""); }
    void StopFrom(string peerId) {
        lock (L) foreach (KeyValuePair<string, ControlSession> s in sessions) { ChatMsg m = Get(s.Key); if (m != null && m.From == peerId) s.Value.Stop(); }
    }
    public void StopControl(string msgId) { ControlSession s; lock (L) sessions.TryGetValue(msgId, out s); if (s != null) s.Stop(); }

    // ---- asking another PC ----
    Channel Dial(string peerId, string type, out Dictionary<string, object> reply) {
        string ip; int port; byte[] key;
        lock (L) {
            Peer p = Find(peerId);
            if (p == null) throw new InvalidOperationException("没有这台电脑");
            if (!p.Online) throw new InvalidOperationException(p.Name + " 不在线");
            ip = p.Ip; port = p.Port; key = master;
        }
        Channel ch = Channel.Connect(ip, port, key);
        try {
            ch.SendJson(new { t = type, id = Util.NewId(), from = Cfg.Id, name = Cfg.Name, port = Cfg.Port, to = peerId });
            reply = ch.ReceiveJson();
            if (!Util.Flag(reply, "ok")) throw new InvalidOperationException(Util.Str(reply, "err") == "版本不一致" ? "对方的 LanTalk 版本太旧，请先更新那台电脑上的 LanTalk" : Util.Str(reply, "err"));
            return ch;
        } catch { ch.Dispose(); throw; }
    }
    public string FetchKey(string peerId) {
        Dictionary<string, object> r;
        using (Dial(peerId, "key", out r)) { string k = Util.Str(r, "key"); Fingerprint(k); return k; }
    }
    // req: op run|put|get and its details. Progress goes to emit; throws when the request couldn't be carried out.
    public void Control(string peerId, Dictionary<string, object> req, string agent, string local, Action<object> emit) {
        string op = Util.Str(req, "op");
        Dictionary<string, object> b = new Dictionary<string, object>(req);
        b["agent"] = agent;
        FileStream src = null;
        ChatMsg m = null;
        string end = "中断";
        try {
            if (op == "run") { if (Util.Str(b, "cmd").Trim().Length == 0) throw new InvalidOperationException("命令是空的"); if (Util.Str(b, "cmd").Length > MaxCommand) throw new InvalidOperationException("命令太长，请先用 lt put 传脚本过去再运行"); }
            if (op == "put") {
                if (!File.Exists(local)) throw new InvalidOperationException("找不到文件：" + local + "（文件夹请先打包，或用 lt file）");
                src = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read);
                b["size"] = src.Length; b["sha"] = Sha(src); src.Position = 0;
            }
            if (op == "get" && File.Exists(local) && !Util.Flag(b, "force")) throw new InvalidOperationException("本机已有这个文件：" + local + "（要覆盖请加 --force）");
            Dictionary<string, object> r;
            using (Channel ch = Dial(peerId, "control", out r)) {
                string body = Util.ToJson(b), nonce = Util.Str(r, "nonce");
                ch.SendJson(new { body = body, sig = Sign(Signed(peerId, Cfg.Id, nonce, body)) });
                ch.Timeout = 30000;
                ControlSession s = new ControlSession(ch, true);
                m = Record(peerId, Cfg.Id, Cfg.Name, agent, Describe(b, local), s);
                if (op == "run") s.StartPing();
                try {
                    while (true) {
                        Dictionary<string, object> f = ch.ReceiveJson();
                        string k = Util.Str(f, "k");
                        if (k == "") throw new InvalidOperationException(Util.Str(f, "err")); // plain refusal
                        if (k == "go") { byte[] buf = new byte[Chunk]; int n; while ((n = src.Read(buf, 0, buf.Length)) > 0) { ch.Send(buf, n); emit(new { ok = true, k = "p" }); } }
                        else if (k == "data") { GetBody(ch, local, Util.Num(f, "size"), Util.Str(f, "sha"), Util.Flag(b, "force"), emit); end = "已取回 · " + Util.SizeText(Util.Num(f, "size")); emit(new { ok = true, k = "x", code = 0, done = true }); return; }
                        else if (k == "x") {
                            string err = Util.Str(f, "err"); end = Util.Str(f, "end");
                            if (err != "") throw new InvalidOperationException(err);
                            emit(new { ok = true, k = "x", code = Util.Num(f, "code"), done = true });
                            return;
                        } else { f["ok"] = true; emit(f); }
                    }
                } catch (Exception e) {
                    if (s.Stopped) { end = "已停止"; throw new InvalidOperationException("已在本机停止"); }
                    if (e is EndOfStreamException || e.InnerException is System.Net.Sockets.SocketException) throw new InvalidOperationException("和对方的连接断了，命令可能没有执行完");
                    throw;
                } finally { s.StopPing(); }
            }
        } catch (CryptographicException) { throw new InvalidOperationException("配对码不一致"); }
        catch (System.Net.Sockets.SocketException e) { throw new InvalidOperationException("连不上对方：" + e.Message); }
        catch (UnauthorizedAccessException e) { throw new InvalidOperationException(e.Message); }
        catch (IOException e) { throw new InvalidOperationException(e.Message); }
        finally { if (src != null) src.Dispose(); if (m != null) Finish(m, end); }
    }
    void GetBody(Channel ch, string local, long size, string sha, bool force, Action<object> emit) {
        Directory.CreateDirectory(Path.GetDirectoryName(local));
        ReceiveTo(local, force, size, sha, delegate { byte[] d = ch.Receive(); emit(new { ok = true, k = "p" }); return d; });
    }

    // ---- being asked ----
    void ServeControl(Channel ch, string from) {
        Grant g = GrantFor(from);
        if (g == null) { Reply(ch, false, "对方还没有允许本机控制它。要由人在那台电脑上打开 LanTalk，选中本机，点“控制权限”"); return; }
        string nonce = Convert.ToBase64String(Crypto.Random(32));
        ch.SendJson(new { ok = true, nonce = nonce });
        Dictionary<string, object> r = ch.ReceiveJson();
        string body = Util.Str(r, "body");
        if (!Verify(g.Key, Signed(Cfg.Id, from, nonce, body), Util.Str(r, "sig"))) { Reply(ch, false, "身份核对失败：本机的身份和那台电脑上授权的不一致。重装过的话，请在那台电脑上重新设置“控制权限”"); return; }
        Dictionary<string, object> b = Util.FromJson<Dictionary<string, object>>(body);
        string op = Util.Str(b, "op");
        if (op != "run" && op != "put" && op != "get") { Reply(ch, false, "版本不一致"); return; }
        if (Util.Str(b, "cmd").Length > MaxCommand) { Reply(ch, false, "命令太长"); return; }
        ControlSession s = new ControlSession(ch, false);
        lock (L) if (sessions.Count >= MaxControls) { Reply(ch, false, "对方同时运行的命令太多"); return; }
        ch.Timeout = 30000;
        Peer peer = Find(from);
        string name = peer == null ? g.Name : peer.Name, agent = Util.Cut(Util.Str(b, "agent"), 20), end = "中断";
        ChatMsg m = Record(from, from, name, agent, Describe(b, null), s);
        try {
            s.StartPing();
            if (op == "run") Run(delegate { // notices "the other side hung up"
                try { while (true) ch.ReceiveJson(); } catch (Exception) { s.Stopped = true; }
            });
            if (!Allowed(g, name, agent, m.Text, s)) { end = s.Stopped ? "已取消" : "已拒绝"; s.Last(new { k = "x", err = "那台电脑上的人没有同意", end = end }); return; }
            if (op == "run") end = DoRun(s, b); else if (op == "put") end = DoPut(s, ch, b); else end = DoGet(s, ch, b);
        } catch (Exception e) {
            end = s.Stopped ? "已停止" : "失败：" + e.Message;
            try { s.Last(new { k = "x", err = e.Message, end = end }); } catch (Exception) { }
        } finally { s.StopPing(); Finish(m, end); }
    }
    bool Allowed(Grant g, string name, string agent, string text, ControlSession s) {
        lock (L) {
            DateTime until;
            if (!Current(ref g)) return false;
            if (g.Mode == "allow" || shortGrants.TryGetValue(g.Id, out until) && DateTime.UtcNow < until) return true;
            if (g.Mode != "ask" || asking >= 3) return false;
            asking++;
        }
        try {
            Action<ControlAsk> h = ControlAsked;
            if (h == null) return false;
            ControlAsk a = new ControlAsk(); a.PeerId = g.Id; a.PeerName = name; a.Agent = agent; a.Text = text;
            s.Send(new { k = "wait" });
            h(a);
            bool longer, ok = a.Wait(delegate { return s.Stopped || stopping; }, out longer) && !s.Stopped;
            lock (L) {
                if (!Current(ref g)) return false; // withdrawn meanwhile
                if (ok && longer) shortGrants[g.Id] = DateTime.UtcNow.AddMinutes(30);
            }
            return ok;
        } finally { lock (L) asking--; }
    }
    bool Current(ref Grant g) { Grant now = GrantFor(g.Id); if (now == null || now.Key != g.Key) return false; g = now; return true; }
    static string Describe(Dictionary<string, object> b, string local) {
        string op = Util.Str(b, "op"), cwd = Util.Str(b, "cwd");
        if (op == "run") return (cwd != "" ? "[" + cwd + "]\n" : "") + Util.Str(b, "cmd");
        if (op == "put") return "写入文件 " + Util.Str(b, "path") + "（" + Util.SizeText(Util.Num(b, "size")) + (Util.Flag(b, "force") ? "，覆盖已有" : "") + "）" + (local != null ? "\n来自 " + local : "");
        return "读取文件 " + Util.Str(b, "path") + (local != null ? "\n存到 " + local : "");
    }
    ChatMsg Record(string conv, string from, string fromName, string agent, string text, ControlSession s) {
        ChatMsg m = new ChatMsg();
        m.Id = Util.NewId(); m.Conv = conv; m.From = from; m.FromName = fromName; m.Agent = agent; m.Kind = "run"; m.Text = text; m.Time = Util.Now(); m.State = "run";
        lock (L) sessions[m.Id] = s;
        Add(m);
        return m;
    }
    void Finish(ChatMsg m, string end) {
        lock (L) { sessions.Remove(m.Id); m.State = "ok"; m.Error = end; }
        Changed(m, true);
    }

    string DoRun(ControlSession s, Dictionary<string, object> b) {
        string cwd = Util.Str(b, "cwd");
        int timeout = (int)Math.Max(1, Math.Min(86400, Util.Num(b, "timeout")));
        if (cwd == "") cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(cwd)) throw new InvalidOperationException("没有这个文件夹：" + cwd);
        string script = "[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false; $OutputEncoding = [Console]::OutputEncoding; $ProgressPreference = 'SilentlyContinue'\n"
            + Util.Str(b, "cmd") + "\nif (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } }";
        ProcessStartInfo si = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"),
            // -EncodedCommand would turn errors into CLIXML; this keeps them text and needs no quoting
            "-NoProfile -NonInteractive -OutputFormat Text -Command \". ([scriptblock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) + "'))))\"");
        si.UseShellExecute = false; si.CreateNoWindow = true; si.WorkingDirectory = cwd;
        si.RedirectStandardInput = si.RedirectStandardOutput = si.RedirectStandardError = true;
        si.StandardOutputEncoding = si.StandardErrorEncoding = new UTF8Encoding(false);
        Stopwatch sw = Stopwatch.StartNew();
        int left = MaxOutput;
        using (Job job = new Job())
        using (Process p = Process.Start(si)) {
            job.Add(p);
            p.StandardInput.Close();
            Thread[] readers = new Thread[] { Reader(p.StandardOutput, "o", s, delegate (int n) { return Interlocked.Add(ref left, -n) >= 0; }), Reader(p.StandardError, "e", s, delegate (int n) { return Interlocked.Add(ref left, -n) >= 0; }) };
            string why = null;
            while (!p.WaitForExit(100)) {
                if (s.Stopped || stopping) why = "已停止"; else if (sw.Elapsed.TotalSeconds > timeout) why = "超时（" + timeout + " 秒）"; else continue;
                job.Kill();
                p.WaitForExit(5000);
                break;
            }
            foreach (Thread t in readers) t.Join(1500); // a program left running may keep the pipe open
            string time = sw.Elapsed.TotalSeconds.ToString("0.0") + " 秒", end = why != null ? why : "退出码 " + p.ExitCode + " · " + time;
            if (left < 0) s.SendSafe(new { k = "e", d = "\n（输出太长，后面的已省略；可先写到文件，再用 lt get 取回）\n" });
            if (why == "已停止") s.Last(new { k = "x", err = "命令在那台电脑上被停止", end = end });
            else s.Last(new { k = "x", code = why != null ? 124 : p.ExitCode, end = end });
            return end;
        }
    }
    static Thread Reader(StreamReader r, string kind, ControlSession s, Func<int, bool> room) {
        Thread t = new Thread(delegate () {
            char[] buf = new char[8192];
            int kept = 0, n;
            try {
                while ((n = r.Read(buf, kept, buf.Length - kept)) > 0) {
                    n += kept; kept = char.IsHighSurrogate(buf[n - 1]) ? 1 : 0; // never split a character
                    if (n - kept > 0 && room(n - kept)) s.SendSafe(new { k = kind, d = new string(buf, 0, n - kept) });
                    if (kept == 1) buf[0] = buf[n - 1];
                }
            } catch (Exception) { } // process gone
        });
        t.IsBackground = true; t.Start();
        return t;
    }
    static string RemotePath(Dictionary<string, object> b) {
        string p = Util.Str(b, "path");
        if (p.Length < 4 || !(p[1] == ':' && (p[2] == '\\' || p[2] == '/')) && !p.StartsWith("\\\\")) throw new InvalidOperationException("远程路径要写完整，例如 D:\\项目\\a.txt");
        return Path.GetFullPath(p);
    }
    string DoPut(ControlSession s, Channel ch, Dictionary<string, object> b) {
        string path = RemotePath(b); long size = Util.Num(b, "size");
        if (Directory.Exists(path)) throw new InvalidOperationException("那里是一个文件夹：" + path);
        if (File.Exists(path) && !Util.Flag(b, "force")) throw new InvalidOperationException("那台电脑上已有这个文件：" + path + "（要覆盖请加 --force）");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        s.StopPing();
        s.Send(new { k = "go" });
        ReceiveTo(path, Util.Flag(b, "force"), size, Util.Str(b, "sha"), delegate { if (s.Stopped || stopping) throw new InvalidOperationException("已停止"); return ch.Receive(); });
        string end = "已写入 · " + Util.SizeText(size);
        s.Last(new { k = "x", code = 0, end = end });
        return end;
    }
    string DoGet(ControlSession s, Channel ch, Dictionary<string, object> b) {
        string path = RemotePath(b);
        if (!File.Exists(path)) throw new InvalidOperationException("那台电脑上没有这个文件：" + path);
        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            long left = f.Length;
            string sha = Sha(f); f.Position = 0;
            s.Last(new { k = "data", size = left, sha = sha });
            byte[] buf = new byte[Chunk]; int n;
            while (left > 0 && (n = f.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0) { if (s.Stopped || stopping) throw new InvalidOperationException("已停止"); ch.Send(buf, n); left -= n; }
            return "已读取 · " + Util.SizeText(f.Length);
        }
    }
    static string Sha(Stream s) { using (SHA256 h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(s)); }
    static void ReceiveTo(string path, bool force, long size, string sha, Func<byte[]> next) {
        string tmp = path + ".lt-" + Util.NewId().Substring(0, 8) + ".tmp";
        try {
            using (FileStream f = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536)) {
                for (long left = size; left > 0; ) {
                    byte[] d = next();
                    if (d.Length == 0 || d.Length > left) throw new IOException("数据长度错误");
                    f.Write(d, 0, d.Length); left -= d.Length;
                }
                f.Position = 0;
                if (Sha(f) != sha) throw new IOException("文件校验不一致");
            }
            if (force && File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); // Move refuses to overwrite
        } finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

// Groups a command with everything it starts, so stopping it stops all of it. Programs it leaves behind on a normal exit go on.
class Job : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr sec, string name);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    IntPtr h = CreateJobObject(IntPtr.Zero, null);
    Process main;
    bool grouped;
    public void Add(Process p) { main = p; grouped = AssignProcessToJobObject(h, p.Handle); }
    public void Kill() {
        if (grouped) TerminateJobObject(h, 1);
        else try { main.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public void Dispose() { if (h != IntPtr.Zero) CloseHandle(h); h = IntPtr.Zero; }
}
}
