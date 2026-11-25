// Remote control: who may do what here, run a PowerShell command on another PC, copy a file there or back. More in Remote.cs.
// Off by default. The person at the controlled PC grants it per PC and per capability ("ask" every time, or "allow"), and the grant is
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
    public string Id, Name, Key, Mode; // Key: public key this grant is tied to   Mode: ask | allow for everything, as older versions stored it
    public string Screen, Read, Write, Run, Mouse; // each null | ask | allow
    public long Until;                 // end of the grant in Util.Now() terms, 0 = none
    public bool Paused;                // stopped by the person here: refused until resumed
    public static readonly string[] Caps = { "screen", "read", "write", "run", "mouse" };
    public string Cap(string c) { string v = c == "screen" ? Screen : c == "read" ? Read : c == "write" ? Write : c == "run" ? Run : c == "mouse" ? Mouse : null; return v == "ask" || v == "allow" ? v : ""; }
    public bool Any { get { foreach (string c in Caps) if (Cap(c) != "") return true; return false; } }
    public bool Expired { get { return Until > 0 && Util.Now() >= Until; } }
    public static Grant Of(string screen, string read, string write, string run) { return Of(screen, read, write, run, ""); }
    public static Grant Of(string screen, string read, string write, string run, string mouse) { Grant g = new Grant(); g.Screen = screen; g.Read = read; g.Write = write; g.Run = run; g.Mouse = mouse; g.Tidy(); return g; }
    // Whoever may run commands unasked can read, write and look as well: say so instead of pretending. The mouse is always a choice of its
    // own, so a grant from before it existed doesn't include it; using it means seeing the screen.
    public void Tidy() {
        if (Mode == "ask" || Mode == "allow") Screen = Read = Write = Run = Mode;
        Mode = null;
        Screen = Cap("screen"); Read = Cap("read"); Write = Cap("write"); Run = Cap("run");
        Mouse = Cap("mouse");
        if (Run == "allow") Screen = Read = Write = "allow";
        if (Mouse == "allow" || Mouse == "ask" && Screen == "") Screen = Mouse;
    }
}

// A request waiting for the person at this PC.
public class ControlAsk {
    public const int Seconds = 60;
    public string PeerId, PeerName, Agent, Text, Op, Cap; // Cap: which capability "for a while" would cover
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
    static string CapOf(string op) { // null: not an operation   "": needs a grant, but no capability of its own
        switch (op) {
            case "run": case "bg": case "shell": case "tunnel": case "claude": case "update": return "run";
            case "shot": case "windows": return "screen";
            case "get": case "cat": case "pull": return "read";
            case "put": case "edit": case "push": return "write";
            case "input": return "mouse";
            case "info": case "jobs": case "log": case "stop": case "sessions": case "end": case "conn": return "";
        }
        return null;
    }
    public event Action ControlChanged; // something started or ended here, or was paused
    public event Action<ControlAsk> ControlAsked;
    readonly Dictionary<string, ControlSession> sessions = new Dictionary<string, ControlSession>(); // by message id
    readonly Dictionary<string, DateTime> shortGrants = new Dictionary<string, DateTime>();          // peer + capability, "ask" answered with "for a while"
    CngKey signKey;
    int asking;

    // One side of a running request.
    internal class ControlSession {
        readonly Channel ch;
        readonly object sending = new object();
        readonly bool client;
        bool quiet;
        Timer ping;
        public volatile bool Stopped;
        public volatile bool Waiting; // for the person here
        public bool Background; // a job that outlives its request
        public volatile ControlSession Also; // stopped along with this one
        public ControlSession(Channel c, bool isClient) { ch = c; client = isClient; }
        public void Send(object o) { lock (sending) { if (!quiet) ch.SendJson(o); } }
        public void SendSafe(object o) { try { Send(o); } catch (Exception) { if (!client) Stopped = true; } } // the asking side finds out by reading
        public void StartPing() { ping = new Timer(delegate { SendSafe(new { k = "p" }); }, null, 5000, 5000); }
        public void StopPing() { Timer t = ping; ping = null; if (t != null) t.Dispose(); lock (sending) { } } // returns once no ping is being written
        public void Last(object o) { StopPing(); lock (sending) { if (quiet) return; quiet = true; ch.SendJson(o); } }
        public void Stop() { Stopped = true; if (client) ch.Dispose(); ControlSession a = Also; if (a != null) a.Stop(); } // a closed connection makes the other side stop as well
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
    // mode: the same for every capability, "" removes the grant.
    public void SetGrant(string peerId, string name, string key, string mode) { SetGrant(peerId, name, key, Grant.Of(mode, mode, mode, mode)); }
    // Stops whatever that PC is doing here that it may no longer do.
    public void SetGrant(string peerId, string name, string key, Grant want) {
        want.Id = peerId; want.Name = name; want.Key = key; want.Tidy();
        Grant old;
        lock (L) {
            old = GrantFor(peerId);
            Cfg.Control.RemoveAll(delegate (Grant g) { return g.Id == peerId; });
            foreach (string c in Grant.Caps) shortGrants.Remove(peerId + c);
            if (want.Any) Cfg.Control.Add(want);
        }
        SaveConfig();
        if (old != null && Grant.Caps.Any(delegate (string c) { return old.Cap(c) != "" && want.Cap(c) == ""; })) StopFrom(peerId);
        RaiseControl();
    }
    public void ClearGrants() { foreach (Grant g in Cfg.Control.ToArray()) SetGrant(g.Id, "", "", ""); }
    // The person here presses Stop: everything ends, and nothing new starts until Resume.
    public void PauseControl(string peerId) { SetPaused(peerId, true); StopFrom(peerId); RaiseControl(); }
    public void ResumeControl(string peerId) { SetPaused(peerId, false); RaiseControl(); }
    void SetPaused(string peerId, bool on) {
        lock (L) { Grant g = GrantFor(peerId); if (g == null) return; g.Paused = on; foreach (string c in Grant.Caps) shortGrants.Remove(peerId + c); }
        SaveConfig();
    }
    void ExpireGrants() {
        List<Grant> over;
        lock (L) over = Cfg.Control.Where(delegate (Grant g) { return g.Expired; }).ToList();
        foreach (Grant g in over) SetGrant(g.Id, "", "", "");
    }
    void RaiseControl() { Action h = ControlChanged; if (h != null) h(); }
    // What other PCs are doing here right now.
    public List<ChatMsg> Controlling() {
        List<ChatMsg> l = new List<ChatMsg>();
        lock (L) foreach (KeyValuePair<string, ControlSession> s in sessions) { ChatMsg m = s.Value.Waiting ? null : Get(s.Key); if (m != null && m.From != Cfg.Id) l.Add(m); }
        return l;
    }
    void StopFrom(string peerId) {
        lock (L) foreach (KeyValuePair<string, ControlSession> s in sessions) { ChatMsg m = Get(s.Key); if (m != null && m.From == peerId) s.Value.Stop(); }
        CloseShells(delegate (Shell h) { return h.Peer == peerId; });
        Tunnel[] t; lock (L) t = tunnels.Where(delegate (Tunnel x) { return x.Peer == peerId; }).ToArray();
        foreach (Tunnel x in t) x.Close();
        CloseIdleInput();
    }
    public void StopControl(string msgId) { ControlSession s; lock (L) sessions.TryGetValue(msgId, out s); if (s != null) { s.Stop(); CloseIdleInput(); } }

    // ---- asking another PC ----
    Channel Dial(string peerId, string type, out Dictionary<string, object> reply) {
        string ip; int port; byte[] key;
        lock (L) {
            Peer p = Find(peerId);
            if (p == null) throw Lang.Err("没有这台电脑", "no such PC");
            if (!p.Online) throw Lang.Err("{0} 不在线", "{0} is offline", p.Name);
            ip = p.Ip; port = p.Port; key = master;
        }
        Channel ch = Channel.Connect(ip, port, key);
        try {
            ch.SendJson(new { t = type, id = Util.NewId(), from = Cfg.Id, name = Cfg.Name, port = Cfg.Port, to = peerId });
            reply = ch.ReceiveJson();
            if (!Util.Flag(reply, "ok")) throw Refusal(reply);
            return ch;
        } catch { ch.Dispose(); throw; }
    }
    static LtException Refusal(Dictionary<string, object> r) {
        Msg m = Msg.Wire(r, "err");
        if (m != null && m.Zh == OldVersion.Zh) m = Lang.M("对方的 LanTalk 版本太旧，请先更新那台电脑上的 LanTalk", "LanTalk on the other PC is too old, update it first");
        return new LtException(m ?? Lang.M("对方拒绝了", "the other PC refused"));
    }
    public string FetchKey(string peerId) {
        Dictionary<string, object> r;
        using (Dial(peerId, "key", out r)) { string k = Util.Str(r, "key"); Fingerprint(k); return k; }
    }
    // req: op (see CapOf) and its details. Progress goes to emit; throws when the request couldn't be carried out.
    public void Control(string peerId, Dictionary<string, object> req, string agent, string local, Action<object> emit) {
        string op = Util.Str(req, "op");
        Dictionary<string, object> b = new Dictionary<string, object>(req);
        b["agent"] = agent;
        FileStream src = null;
        TunnelEnd tun = null;
        InputUse use = null;
        string want = "";
        SyncJob sync = null;
        ChatMsg m = null;
        Msg end = Lang.M("中断", "interrupted");
        try {
            if (op == "run" || op == "bg" || op == "shell" || op == "claude") { if (Util.Str(b, "cmd").Trim().Length == 0) throw Lang.Err("命令是空的", "the command is empty"); if (Util.Str(b, "cmd").Length > MaxCommand) throw Lang.Err("命令太长，请先用 lt put 传脚本过去再运行", "the command is too long: copy a script over with lt put and run that"); }
            if (op == "put") {
                if (!File.Exists(local)) throw Lang.Err("找不到文件：{0}（文件夹请先打包，或用 lt file）", "file not found: {0} (zip folders first, or use lt file)", local);
                src = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read);
                b["size"] = src.Length; b["sha"] = Sha(src); src.Position = 0;
            }
            if (op == "update") {
                if (local == "") local = InstallDir;
                foreach (string f in Programs) if (!File.Exists(Path.Combine(local, f))) throw Lang.Err("文件夹里没有 {0}：{1}", "no {0} in {1}", f, local);
                b["sha"] = Programs.Select(delegate (string f) { return Convert.ToBase64String(Hex(ShaHex(Path.Combine(local, f)))); }).ToArray();
                b["size"] = Programs.Select(delegate (string f) { return new FileInfo(Path.Combine(local, f)).Length; }).ToArray();
                want = ShaHex(Path.Combine(local, Programs[0])).Substring(0, 8);
            }
            if (op == "push" || op == "pull") sync = new SyncJob(this, peerId, b, local);
            if (op == "input") foreach (string x in Strings(b, "steps")) InputStep.Parse(x); // mistakes show before anything happens
            if ((op == "shot" || op == "input" && Util.Flag(b, "look")) && local == "") local = Util.UniquePath(Path.Combine(Cfg.SaveDir, Lang.T("截图", "Screenshots"), Util.SafeRelative(Find(peerId) == null ? "" : Find(peerId).Name) + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png"));
            if ((op == "get" || op == "shot" || op == "input" && local != "") && File.Exists(local) && !Util.Flag(b, "force")) throw Lang.Err("本机已有这个文件：{0}（要覆盖请加 --force）", "this PC already has that file: {0} (--force overwrites it)", local);
            Dictionary<string, object> r;
            using (Channel ch = Dial(peerId, "control", out r)) {
                string body = Util.ToJson(b), nonce = Util.Str(r, "nonce");
                if (body.Length > 3000000) throw Lang.Err("内容太多，请分几次", "too much at once, split it");
                ch.SendJson(new { body = body, sig = Sign(Signed(peerId, Cfg.Id, nonce, body)) });
                ch.Timeout = 30000;
                ControlSession s = new ControlSession(ch, true);
                if (op == "input") { use = OutUse(peerId, agent, s); m = use.M; }
                else if (!Quiet(op)) m = Record(peerId, Cfg.Id, Cfg.Name, agent, op, Describe(b, local), s);
                if (Live(op)) s.StartPing();
                try {
                    while (true) {
                        Dictionary<string, object> f = ch.ReceiveJson();
                        string k = Util.Str(f, "k");
                        if (k == "") throw Refusal(f);
                        if (k == "go" && op == "update") { foreach (string pf in Programs) { string full = Path.Combine(local, pf); SyncFile sf = new SyncFile(); sf.P = pf; sf.S = new FileInfo(full).Length; Sync.SendFile(ch, full, sf, delegate { emit(new { ok = true, k = "p" }); }); } }
                        else if (k == "open") {
                            tun = new TunnelEnd(this, peerId, Util.Str(f, "tunnel"), (int)Util.Num(b, "local"), (int)Util.Num(b, "port"));
                            emit(new { ok = true, k = "tunnel", local = tun.Port, port = Util.Num(b, "port") });
                        }
                        else if (k == "go") { byte[] buf = new byte[Chunk]; int n; while ((n = src.Read(buf, 0, buf.Length)) > 0) { ch.Send(buf, n); emit(new { ok = true, k = "p" }); } }
                        else if (k == "data" && op == "input") {
                            s.StopPing();
                            GetBody(ch, local, Util.Num(f, "size"), Util.Str(f, "sha"), Util.Flag(b, "force"), emit);
                            OutDone(use, f);
                            f["k"] = "x"; f["ok"] = true; f["done"] = true; f["path"] = local; end = null; emit(f);
                            return;
                        }
                        else if (k == "data") { GetBody(ch, local, Util.Num(f, "size"), Util.Str(f, "sha"), Util.Flag(b, "force"), emit); end = Lang.M("已取回 · {0}", "fetched · {0}", Util.SizeText(Util.Num(f, "size")));
                            if (op == "shot") { lock (L) m.Path = local; end = Msg.Wire(f, "what") ?? end; }
                            emit(new { ok = true, k = "x", code = 0, done = true, path = local, what = Msg.Wire(f, "what") == null ? "" : Msg.Wire(f, "what").ToString() }); return; }
                        else if (k == "need") sync.Send(ch, f, emit);
                        else if (k == "list") sync.Choose(ch, f, emit);
                        else if (k == "x") {
                            Msg err = Msg.Wire(f, "err"); end = Msg.Wire(f, "end") ?? end;
                            if (err != null) throw new LtException(err);
                            if (op == "update") {
                                emit(new { ok = true, k = "e", d = Lang.T("（新版本已换上，等它重新上线…）", "(The new version is in place, waiting for it to come back...)") + "\n" });
                                string got = AfterUpdate(peerId, want, 90);
                                if (got != want) throw got == "" ? Lang.Err("换上之后那台电脑没有重新上线，请到那台电脑前看一下", "it hasn't come back after the update, have a look at that PC") : Lang.Err("新版本没能启动，那台电脑已自动换回原来的版本（{0}）", "the new version didn't start, that PC went back to the old one ({0})", got);
                                end = Lang.M("已更新 · {0}", "updated · {0}", want);
                            }
                            if (sync != null) { f["code"] = sync.Finish(f, emit); end = sync.Summary; }
                            if (use != null) { OutDone(use, f); end = null; }
                            f["ok"] = true; f["done"] = true; emit(f);
                            return;
                        } else { f["ok"] = true; emit(f); }
                    }
                } catch (Exception e) {
                    if (s.Stopped) { end = Stopped; throw Lang.Err("已在本机停止", "stopped on this PC"); }
                    if (e is EndOfStreamException || e.InnerException is System.Net.Sockets.SocketException) throw Lang.Err("和对方的连接断了，命令可能没有执行完", "lost the connection, the command may not have finished");
                    throw;
                } finally { s.StopPing(); if (tun != null) tun.Dispose(); }
            }
        } catch (CryptographicException) { throw Lang.Err("配对码不一致", "the pairing codes differ"); }
        catch (System.Net.Sockets.SocketException e) { throw Lang.Err("连不上对方：{0}", "can't reach the other PC: {0}", e.Message); }
        catch (UnauthorizedAccessException e) { throw new InvalidOperationException(e.Message); }
        catch (IOException e) { throw new InvalidOperationException(e.Message); }
        finally { if (src != null) src.Dispose(); if (use != null) OutEnd(use, end); else if (m != null) Finish(m, end); }
    }
    void GetBody(Channel ch, string local, long size, string sha, bool force, Action<object> emit) {
        Directory.CreateDirectory(Path.GetDirectoryName(local));
        ReceiveTo(local, force, size, sha, delegate { byte[] d = ch.Receive(); emit(new { ok = true, k = "p" }); return d; });
    }

    // ---- being asked ----
    void ServeControl(Channel ch, string from) {
        Grant g = GrantFor(from);
        if (g == null || g.Expired) { Reply(ch, false, Lang.M("对方还没有允许本机控制它。要由人在那台电脑上打开 LanTalk，选中本机，点“控制权限”", "the other PC doesn't let this PC control it. Someone there has to open LanTalk, pick this PC and click \"Control access\"")); return; }
        string nonce = Convert.ToBase64String(Crypto.Random(32));
        ch.SendJson(new { ok = true, nonce = nonce });
        Dictionary<string, object> r = ch.ReceiveJson();
        string body = Util.Str(r, "body");
        if (!Verify(g.Key, Signed(Cfg.Id, from, nonce, body), Util.Str(r, "sig"))) { Reply(ch, false, Lang.M("身份核对失败：本机的身份和那台电脑上授权的不一致。重装过的话，请在那台电脑上重新设置“控制权限”", "identity check failed: this isn't the identity that was allowed there. After a reinstall, set \"Control access\" again on that PC")); return; }
        Dictionary<string, object> b = Util.FromJson<Dictionary<string, object>>(body);
        string op = Util.Str(b, "op");
        string cap = CapOf(op);
        if (cap == null) { Reply(ch, false, OldVersion); return; }
        if (g.Paused) { Reply(ch, false, Lang.M("那台电脑上的人按了“停止”，要由人在那边点“恢复”", "someone at that PC pressed Stop. They have to press Resume there")); return; }
        string need = cap != "" ? cap : op == "info" ? null : "run"; // job commands belong to "run"
        if (need == null ? !g.Any : g.Cap(need) == "") { Reply(ch, false, new Msg("那台电脑没有给本机“" + CapName(need ?? "read").Zh + "”的权限。要由人在那边的“控制权限”里打开", "that PC doesn't give this PC the \"" + CapName(need ?? "read").En + "\" permission. Someone there has to switch it on in \"Control access\"")); return; }
        if (Util.Str(b, "cmd").Length > MaxCommand) { Reply(ch, false, Lang.M("命令太长", "the command is too long")); return; }
        ControlSession s = new ControlSession(ch, false);
        lock (L) if (sessions.Values.Count(delegate (ControlSession x) { return !x.Background; }) >= MaxControls) { Reply(ch, false, Lang.M("对方同时运行的命令太多", "too many commands are running there")); return; }
        ch.Timeout = 30000;
        Peer peer = Find(from);
        string name = peer == null ? g.Name : peer.Name, agent = Util.Cut(Util.Str(b, "agent"), 20);
        Msg end = Lang.M("中断", "interrupted");
        if (op == "conn") { DoConn(ch, b, from); return; }
        if (op == "input") { ServeInput(ch, s, g, b, from, name, agent); return; }
        if (Quiet(op)) {
            s.StartPing();
            try { if (op == "jobs") DoJobs(s, from); else if (op == "log") DoLog(s, b, from); else if (op == "sessions") DoSessions(s, from); else if (op == "end") DoEnd(s, b, from); else DoStop(s, b, from); }
            catch (Exception e) { Msg why = Msg.Of(e); try { s.Last(Done(why, why, 0)); } catch (Exception) { } }
            finally { s.StopPing(); }
            return;
        }
        Exception early = null; Shot shot = null; PushPlan push = null; BgJob job = null; EditPlan edit = null;
        s.StartPing();
        try { // what the person here should see before deciding
            if (op == "shot") b["what"] = (shot = new Shot(b)).What.ToString();
            else if (op == "edit") b["sum"] = (edit = new EditPlan(b)).Text;
            else if (op == "push") push = new PushPlan(b, delegate { return s.Stopped || stopping; });
            else if (op == "bg" || op == "claude") job = NewJob(from, b);
            else if (op == "shell") ShellName(b);
            else if (op == "update") CheckUpdate(b, "");
        } catch (Exception e) { early = e; }
        ChatMsg m = Record(from, from, name, agent, op, Describe(b, null), s);
        try {
            if (early != null && op != "edit" && op != "push") throw early;
            if (Live(op)) Run(delegate { // notices "the other side hung up"
                try { while (true) ch.ReceiveJson(); } catch (Exception) { s.Stopped = true; }
            });
            bool asked;
            if (!Allowed(g, name, agent, m.Text, s, op, cap, out asked)) { end = s.Stopped ? Lang.M("已取消", "cancelled") : Lang.M("已拒绝", "declined"); s.Last(Done(Lang.M("那台电脑上的人没有同意", "the person at that PC didn't approve it"), end, 0)); return; }
            if (early != null) throw early; // what is in the files here is only told once it was allowed
            switch (op) {
                case "run": end = DoRun(s, b); break;
                case "shell": end = DoShell(s, b, from); break;
                case "tunnel": end = DoTunnel(s, b, from); break;
                case "update": CheckUpdate(b, m.Id); end = DoUpdate(s, ch, b); break;
                case "bg": case "claude": StartJob(job, s, b, m); s.Last(new { k = "x", code = 0, err = "", job = job.Id, end = Lang.M("已在后台开始 · 任务 {0}", "started in the background · job {0}", job.Id).Zh, end_en = Lang.M("已在后台开始 · 任务 {0}", "started in the background · job {0}", job.Id).En }); break;
                case "put": end = DoPut(s, ch, b); break;
                case "get": end = DoGet(s, ch, b); break;
                case "cat": end = DoCat(s, b); break;
                case "edit": end = DoEdit(s, edit); break;
                case "shot": if (asked) Thread.Sleep(500); end = DoShot(s, ch, shot); SawPicture(from, shot); break; // let the question disappear first
                case "windows": end = DoWindows(s); break;
                case "push": end = DoPush(s, ch, push); break;
                case "pull": end = DoPull(s, ch, b); break;
                case "info": end = DoInfo(s); break;
            }
        } catch (Exception e) {
            Msg why = Msg.Of(e);
            end = s.Stopped ? Stopped : new Msg("失败：" + why.Zh, "failed: " + why.En);
            try { s.Last(Done(why, end, 0)); } catch (Exception) { }
        } finally { s.StopPing(); if (!s.Background) Finish(m, end); }
    }
    static bool Quiet(string op) { return op == "jobs" || op == "log" || op == "stop" || op == "sessions" || op == "end" || op == "conn"; } // about something that is in the chat already
    static bool Live(string op) { return op == "run" || op == "shell" || op == "tunnel" || op == "input"; } // ends when the asking side goes away
    public static bool IsJob(string op) { return op == "bg" || op == "claude"; }
    static Msg CapName(string cap) { return cap == "screen" ? Lang.M("看屏幕", "See the screen") : cap == "read" ? Lang.M("读文件", "Read files") : cap == "write" ? Lang.M("写文件", "Write files") : cap == "mouse" ? Lang.M("鼠标键盘", "Mouse and keyboard") : Lang.M("运行命令", "Run commands"); }
    static readonly Msg Stopped = Lang.M("已停止", "stopped");
    static object Done(Msg err, Msg end, int code) { return new { k = "x", code = code, err = err == null ? "" : err.Zh, err_en = err == null ? "" : err.En, end = end.Zh, end_en = end.En }; }
    // cap "" (information about this PC): unasked once anything is allowed unasked.
    bool Allowed(Grant g, string name, string agent, string text, ControlSession s, string op, string cap, out bool asked) {
        asked = false;
        lock (L) {
            DateTime until;
            if (!Current(ref g)) return false;
            string mode = cap != "" ? g.Cap(cap) : Grant.Caps.Any(delegate (string c) { return g.Cap(c) == "allow"; }) ? "allow" : g.Any ? "ask" : "";
            if (mode == "allow" || mode == "ask" && shortGrants.TryGetValue(g.Id + cap, out until) && DateTime.UtcNow < until) return true;
            if (mode != "ask" || asking >= 3) return false;
            asking++;
        }
        try {
            Action<ControlAsk> h = ControlAsked;
            if (h == null) return false;
            ControlAsk a = new ControlAsk(); a.PeerId = g.Id; a.PeerName = name; a.Agent = agent; a.Text = text; a.Op = op; a.Cap = cap;
            asked = true;
            s.Send(new { k = "wait" });
            s.Waiting = true;
            h(a);
            bool longer, ok = a.Wait(delegate { return s.Stopped || stopping; }, out longer) && !s.Stopped;
            s.Waiting = false;
            RaiseControl();
            lock (L) {
                if (!Current(ref g)) return false; // withdrawn meanwhile
                if (ok && longer) shortGrants[g.Id + cap] = DateTime.UtcNow.AddMinutes(30);
            }
            return ok;
        } finally { lock (L) asking--; }
    }
    bool Current(ref Grant g) { Grant now = GrantFor(g.Id); if (now == null || now.Key != g.Key || now.Paused || now.Expired) return false; g = now; return true; }
    static string Describe(Dictionary<string, object> b, string local) {
        string op = Util.Str(b, "op"), cwd = Util.Str(b, "cwd");
        if (op == "run") return (cwd != "" ? "[" + cwd + "]\n" : "") + Util.Str(b, "cmd");
        if (op != "put" && op != "get") return DescribeMore(op, b, local);
        if (op == "put") return Lang.T("写入文件 {0}（{1}{2}）", "Write file {0} ({1}{2})", Util.Str(b, "path"), Util.SizeText(Util.Num(b, "size")), Util.Flag(b, "force") ? Lang.T("，覆盖已有", ", overwriting") : "") + (local != null ? "\n" + Lang.T("来自 {0}", "from {0}", local) : "");
        return Lang.T("读取文件 {0}", "Read file {0}", Util.Str(b, "path")) + (local != null ? "\n" + Lang.T("存到 {0}", "into {0}", local) : "");
    }
    ChatMsg Record(string conv, string from, string fromName, string agent, string op, string text, ControlSession s) {
        ChatMsg m = new ChatMsg();
        m.Id = Util.NewId(); m.Conv = conv; m.From = from; m.FromName = fromName; m.Agent = agent; m.Kind = "run"; m.Op = op; m.Text = text; m.Time = Util.Now(); m.State = "run";
        lock (L) sessions[m.Id] = s;
        Add(m);
        RaiseControl();
        return m;
    }
    void Finish(ChatMsg m, Msg end) {
        lock (L) { sessions.Remove(m.Id); m.State = "ok"; m.Err(end); }
        Changed(m, true);
        RaiseControl();
    }

    Msg DoRun(ControlSession s, Dictionary<string, object> b) {
        int timeout = (int)Math.Max(1, Math.Min(86400, Util.Num(b, "timeout")));
        ProcessStartInfo si = ShellStart(b);
        Stopwatch sw = Stopwatch.StartNew();
        int left = MaxOutput;
        using (Job job = new Job(false))
        using (Process p = Process.Start(si)) {
            job.Add(p);
            p.StandardInput.Close();
            Thread[] readers = new Thread[] { Reader(p.StandardOutput, "o", s, delegate (int n) { return Interlocked.Add(ref left, -n) >= 0; }), Reader(p.StandardError, "e", s, delegate (int n) { return Interlocked.Add(ref left, -n) >= 0; }) };
            Msg why = null;
            while (!p.WaitForExit(100)) {
                if (s.Stopped || stopping) why = Stopped; else if (sw.Elapsed.TotalSeconds > timeout) why = Lang.M("超时（{0} 秒）", "timed out ({0} s)", timeout); else continue;
                job.Kill();
                p.WaitForExit(5000);
                break;
            }
            foreach (Thread t in readers) t.Join(1500); // a program left running may keep the pipe open
            Msg end = why ?? Lang.M("退出码 {0} · {1:0.0} 秒", "exit code {0} · {1:0.0} s", p.ExitCode, sw.Elapsed.TotalSeconds);
            if (left < 0) s.SendSafe(new { k = "e", d = "\n（输出太长，后面的已省略；可先写到文件，再用 lt get 取回）\n", d_en = "\n(Output too long, the rest was dropped. Write it to a file and fetch that with lt get.)\n" });
            if (why == Stopped) s.Last(Done(Lang.M("命令在那台电脑上被停止", "the command was stopped on that PC"), end, 0));
            else s.Last(Done(null, end, why != null ? 124 : p.ExitCode));
            return end;
        }
    }
    static ProcessStartInfo ShellStart(Dictionary<string, object> b) {
        string cwd = Util.Str(b, "cwd");
        if (cwd == "") cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(cwd)) throw Lang.Err("没有这个文件夹：{0}", "no such folder: {0}", cwd);
        string script = "$OutputEncoding = [Console]::OutputEncoding; $ProgressPreference = 'SilentlyContinue'\n"
            + Util.Str(b, "cmd") + "\nif (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } }";
        ProcessStartInfo si = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"),
            // -EncodedCommand would turn errors into CLIXML; this keeps them text and needs no quoting
            "-NoProfile -NonInteractive -OutputFormat Text -Command \"[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false; . ([scriptblock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) + "'))))\"");
        si.UseShellExecute = false; si.CreateNoWindow = true; si.WorkingDirectory = cwd;
        si.RedirectStandardInput = si.RedirectStandardOutput = si.RedirectStandardError = true;
        si.StandardOutputEncoding = si.StandardErrorEncoding = new UTF8Encoding(false);
        return si;
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
        if (p.Length < 4 || !(p[1] == ':' && (p[2] == '\\' || p[2] == '/')) && !p.StartsWith("\\\\")) throw Lang.Err("远程路径要写完整，例如 D:\\项目\\a.txt", "the remote path must be complete, like D:\\project\\a.txt");
        return Path.GetFullPath(p);
    }
    Msg DoPut(ControlSession s, Channel ch, Dictionary<string, object> b) {
        string path = RemotePath(b); long size = Util.Num(b, "size");
        if (Directory.Exists(path)) throw Lang.Err("那里是一个文件夹：{0}", "that is a folder: {0}", path);
        if (File.Exists(path) && !Util.Flag(b, "force")) throw Lang.Err("那台电脑上已有这个文件：{0}（要覆盖请加 --force）", "that PC already has this file: {0} (--force overwrites it)", path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        s.StopPing();
        s.Send(new { k = "go" });
        ReceiveTo(path, Util.Flag(b, "force"), size, Util.Str(b, "sha"), delegate { if (s.Stopped || stopping) throw new LtException(Stopped); return ch.Receive(); });
        Msg end = Lang.M("已写入 · {0}", "written · {0}", Util.SizeText(size));
        s.Last(Done(null, end, 0));
        return end;
    }
    Msg DoGet(ControlSession s, Channel ch, Dictionary<string, object> b) {
        string path = RemotePath(b);
        if (!File.Exists(path)) throw Lang.Err("那台电脑上没有这个文件：{0}", "no such file on that PC: {0}", path);
        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            long left = f.Length;
            string sha = Sha(f); f.Position = 0;
            s.Last(new { k = "data", size = left, sha = sha });
            byte[] buf = new byte[Chunk]; int n;
            while (left > 0 && (n = f.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0) { if (s.Stopped || stopping) throw new LtException(Stopped); ch.Send(buf, n); left -= n; }
            return Lang.M("已读取 · {0}", "read · {0}", Util.SizeText(f.Length));
        }
    }
    static string Sha(Stream s) { using (SHA256 h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(s)); }
    static void ReceiveTo(string path, bool force, long size, string sha, Func<byte[]> next) { ReceiveTo(path, force, size, sha, next, delegate { }); }
    static void ReceiveTo(string path, bool force, long size, string sha, Func<byte[]> next, Action before) { // before: last check, may throw
        string tmp = path + ".lt-" + Util.NewId().Substring(0, 8) + ".tmp";
        try {
            using (FileStream f = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536)) {
                for (long left = size; left > 0; ) {
                    byte[] d = next();
                    if (d.Length == 0 || d.Length > left) throw Lang.Err("数据长度错误", "wrong data length");
                    f.Write(d, 0, d.Length); left -= d.Length;
                }
                f.Position = 0;
                if (Sha(f) != sha) throw Lang.Err("文件校验不一致", "the file arrived damaged");
            }
            before();
            if (force && File.Exists(path)) Util.Replace(tmp, path); else File.Move(tmp, path); // Move refuses to overwrite
        } finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

// Groups a command with everything it starts, so stopping it stops all of it. Programs it leaves behind on a normal exit go on.
class Job : IDisposable {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr sec, string name);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int kind, byte[] info, int size);
    readonly bool bound;
    IntPtr h = CreateJobObject(IntPtr.Zero, null);
    // bound: ends together with LanTalk, so nothing is left running that nobody can stop.
    public Job(bool withApp) { bound = withApp; if (bound) Limit(0x2000); }
    void Limit(int flags) { // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: LimitFlags at offset 16; 0x2000 = kill when the last handle closes
        byte[] b = new byte[IntPtr.Size == 8 ? 144 : 112];
        BitConverter.GetBytes(flags).CopyTo(b, 16);
        SetInformationJobObject(h, 9, b, b.Length);
    }
    public void LetGo() { if (bound) Limit(0); }
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
