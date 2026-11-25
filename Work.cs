// Remote control for longer work: sessions that keep their state, forwarded ports, work handed to the Claude Code over there,
// and updating LanTalk itself. Control.cs checks who may do what before anything in here runs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LanTalk {

public partial class Node {
    public const string Version = "0.2.1";
    const int MaxShells = 8, ShellIdleMinutes = 120, MaxConns = 48;

    // ---- sessions: one PowerShell per name that remembers folder, variables and environment ----
    class Shell : IDisposable {
        public string Peer, Name;
        public DateTime Last = DateTime.UtcNow, Start = DateTime.UtcNow;
        public readonly object Busy = new object();
        readonly Process p;
        readonly Job box = new Job(true);
        readonly NamedPipeServerStream pipe;
        readonly object gate = new object();
        // the command that is running
        string mark; Action<string, string> sink; int code;
        readonly ManualResetEvent outDone = new ManualResetEvent(false), errDone = new ManualResetEvent(false);

        public Shell(ProcessStartInfo si, string pipeName) {
            PipeSecurity sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 65536, sec);
            try {
                IAsyncResult r = pipe.BeginWaitForConnection(null, null);
                p = Process.Start(si);
                box.Add(p);
                p.StandardInput.Close();
                Run(delegate { Read(p.StandardOutput, "o", outDone); }); Run(delegate { Read(p.StandardError, "e", errDone); });
                if (!r.AsyncWaitHandle.WaitOne(15000)) throw Lang.Err("会话没能启动", "the session didn't start");
                pipe.EndWaitForConnection(r);
            } catch { Dispose(); throw; }
        }
        public bool Alive { get { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } } }
        // Runs one command. Returns its exit code; ended: the command closed the session (exit) or it was stopped.
        public int Command(string text, Action<string, string> output, Func<Msg> stop, out Msg why, out bool ended) {
            string id = Util.NewId();
            lock (gate) { mark = "\u001e" + id; sink = output; code = 0; outDone.Reset(); errDone.Reset(); }
            byte[] line = Encoding.UTF8.GetBytes(id + " " + Convert.ToBase64String(Encoding.Unicode.GetBytes(text)) + "\n");
            why = null; ended = false;
            try { pipe.Write(line, 0, line.Length); pipe.Flush(); } catch (IOException) { ended = true; }
            while (!ended && !outDone.WaitOne(100)) {
                if ((why = stop()) != null) { Dispose(); ended = true; break; }
                if (!Alive) { outDone.WaitOne(1000); ended = true; code = p.ExitCode; } // "exit" in the command
            }
            if (!ended) errDone.WaitOne(2000); else if (why == null) Thread.Sleep(300);
            lock (gate) { mark = null; sink = null; Last = DateTime.UtcNow; return code; }
        }
        void Read(StreamReader r, string kind, ManualResetEvent done) {
            char[] buf = new char[8192];
            string held = ""; int n;
            try {
                while ((n = r.Read(buf, 0, buf.Length)) > 0) {
                    held += new string(buf, 0, n);
                    lock (gate) {
                        if (mark == null) { held = ""; continue; } // something left running in the background
                        int at = held.IndexOf(mark, StringComparison.Ordinal);
                        if (at >= 0) {
                            int end = held.IndexOf('\u001e', at + mark.Length);
                            if (at > 0) { sink(kind, held.Substring(0, at)); held = held.Substring(at); end -= at; at = 0; }
                            if (end < 0) continue;
                            if (kind == "o") int.TryParse(held.Substring(mark.Length, end - mark.Length).Trim(), out code);
                            held = held.Substring(end + 1);
                            done.Set();
                            continue;
                        }
                        int keep = 0; // the start of the mark may be at the end, and a character may be split
                        int s = held.LastIndexOf('\u001e');
                        if (s >= 0 && mark.StartsWith(held.Substring(s), StringComparison.Ordinal)) keep = held.Length - s;
                        else if (held.Length > 0 && char.IsHighSurrogate(held[held.Length - 1])) keep = 1;
                        if (held.Length > keep) { sink(kind, held.Substring(0, held.Length - keep)); held = held.Substring(held.Length - keep); }
                    }
                }
            } catch (Exception) { } // process gone
        }
        public void Dispose() { box.Kill(); box.Dispose(); pipe.Dispose(); }
    }
    readonly List<Shell> shells = new List<Shell>();

    static string ShellName(Dictionary<string, object> b) {
        string n = Util.Str(b, "session");
        if (!Regex.IsMatch(n, "\\A[\\w.-]{1,30}\\z")) throw Lang.Err("会话名只能用字母、数字、点、横线，最长 30 个字", "a session name has letters, digits, dots and dashes, 30 at most");
        return n;
    }
    Shell OpenShell(string peer, string name) {
        Shell old;
        lock (L) {
            old = shells.Find(delegate (Shell x) { return x.Peer == peer && x.Name == name; });
            if (old != null && old.Alive) return old;
            if (old != null) shells.Remove(old);
            if (shells.Count >= MaxShells) throw Lang.Err("那台电脑上的会话太多（{0} 个），先用 lt end 关掉一些", "too many sessions there ({0}), close some with lt end", MaxShells);
        }
        if (old != null) old.Dispose();
        string pipe = "LanTalk-shell-" + Util.NewId();
        Dictionary<string, object> b = new Dictionary<string, object>();
        b["cmd"] = "$global:__lt = New-Object IO.StreamReader((& { $p = New-Object IO.Pipes.NamedPipeClientStream('.', '" + pipe + "', [IO.Pipes.PipeDirection]::In); $p.Connect(10000); $p }), [Text.Encoding]::UTF8)\n"
            + "while ($null -ne ($global:__ltline = $global:__lt.ReadLine())) {\n"
            + "  $global:LASTEXITCODE = $null; $global:__ltok = $true\n"
            + "  do { try { . ([scriptblock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($global:__ltline.Split(' ')[1])) + \"`n`$global:__ltok = `$?\")) | Out-Default } catch { $global:__ltok = $false; [Console]::Error.WriteLine(($_ | Out-String).Trim()) } } while ($false)\n"
            + "  $global:__ltid = $global:__ltline.Split(' ')[0]\n"
            + "  [Console]::Out.Write([string][char]30 + $global:__ltid + ' ' + $(if ($global:__ltok) { 0 } elseif ($global:LASTEXITCODE) { $global:LASTEXITCODE } else { 1 }) + [char]30); [Console]::Out.Flush()\n"
            + "  [Console]::Error.Write([string][char]30 + $global:__ltid + [char]30); [Console]::Error.Flush()\n"
            + "}\n";
        Shell h = new Shell(ShellStart(b), pipe); h.Peer = peer; h.Name = name;
        lock (L) shells.Add(h);
        return h;
    }
    void CloseShells(Func<Shell, bool> which) {
        Shell[] l;
        lock (L) { l = shells.Where(which).ToArray(); foreach (Shell h in l) shells.Remove(h); }
        foreach (Shell h in l) h.Dispose();
    }
    void IdleShells() {
        CloseShells(delegate (Shell h) {
            if (!h.Alive) return true;
            if ((DateTime.UtcNow - h.Last).TotalMinutes < ShellIdleMinutes || !Monitor.TryEnter(h.Busy)) return false;
            Monitor.Exit(h.Busy);
            return true;
        });
    }
    Msg DoShell(ControlSession s, Dictionary<string, object> b, string from) {
        string name = ShellName(b), cwd = Util.Str(b, "cwd");
        int timeout = (int)Math.Max(1, Math.Min(86400, Util.Num(b, "timeout"))), left = MaxOutput;
        if (cwd != "" && !Directory.Exists(cwd)) throw Lang.Err("没有这个文件夹：{0}", "no such folder: {0}", cwd);
        Shell h = OpenShell(from, name);
        if (!Monitor.TryEnter(h.Busy)) throw Lang.Err("会话 {0} 正在运行另一条命令", "session {0} is busy with another command", name);
        try {
            Stopwatch sw = Stopwatch.StartNew();
            Msg why; bool ended;
            int code = h.Command((cwd != "" ? "Set-Location -LiteralPath '" + cwd.Replace("'", "''") + "'\n" : "") + Util.Str(b, "cmd"),
                delegate (string kind, string d) { if (Interlocked.Add(ref left, -d.Length) >= 0) s.SendSafe(new { k = kind, d = d }); },
                delegate { return s.Stopped || stopping ? Stopped : sw.Elapsed.TotalSeconds > timeout ? Lang.M("超时（{0} 秒）", "timed out ({0} s)", timeout) : null; }, out why, out ended);
            if (ended) CloseShells(delegate (Shell x) { return x == h; });
            Msg end = why ?? Lang.M("退出码 {0} · {1:0.0} 秒", "exit code {0} · {1:0.0} s", code, sw.Elapsed.TotalSeconds);
            if (left < 0) s.SendSafe(new { k = "e", d = "\n（输出太长，后面的已省略）\n", d_en = "\n(Output too long, the rest was dropped.)\n" });
            if (ended) s.SendSafe(new { k = "e", d = "（会话 " + name + " 已结束，里面的变量和目录都没有了）\n", d_en = "(Session " + name + " has ended, its variables and folder are gone.)\n" });
            if (why == Stopped) s.Last(Done(Lang.M("命令在那台电脑上被停止", "the command was stopped on that PC"), end, 0));
            else s.Last(Done(null, end, why != null ? 124 : code));
            return end;
        } finally { Monitor.Exit(h.Busy); }
    }
    void DoSessions(ControlSession s, string from) {
        Shell[] mine;
        lock (L) mine = shells.Where(delegate (Shell h) { return h.Peer == from && h.Alive; }).ToArray();
        s.Last(new { k = "x", code = 0, err = "", end = "", sessions = mine.Select(delegate (Shell h) {
            bool busy = !Monitor.TryEnter(h.Busy); if (!busy) Monitor.Exit(h.Busy);
            Msg idle = BgJob.Span((DateTime.UtcNow - h.Last).TotalSeconds);
            return new { name = h.Name, busy = busy, idle = idle.Zh, idle_en = idle.En };
        }).ToArray() });
    }
    void DoEnd(ControlSession s, Dictionary<string, object> b, string from) {
        string name = ShellName(b);
        bool had; lock (L) had = shells.Exists(delegate (Shell h) { return h.Peer == from && h.Name == name; });
        if (!had) throw Lang.Err("没有叫 {0} 的会话", "no session called {0}", name);
        CloseShells(delegate (Shell h) { return h.Peer == from && h.Name == name; });
        s.Last(Done(null, Stopped, 0));
    }

    // ---- forwarded ports: a port on this PC that leads to one on the other. Each connection is a channel of its own ----
    class Tunnel {
        public string Id, Peer;
        public int Port;
        public readonly List<IDisposable> Open = new List<IDisposable>();
        public bool Closed;
        public void Close() { IDisposable[] l; lock (Open) { Closed = true; l = Open.ToArray(); Open.Clear(); } foreach (IDisposable d in l) d.Dispose(); }
        public bool Add(IDisposable d) { lock (Open) { if (Closed) return false; Open.Add(d); return true; } }
        public void Remove(IDisposable d) { lock (Open) Open.Remove(d); }
    }
    readonly List<Tunnel> tunnels = new List<Tunnel>(); // into this PC
    int conns;

    static TcpClient Loopback(int port) {
        Exception last = null;
        foreach (IPAddress a in new IPAddress[] { IPAddress.Loopback, IPAddress.IPv6Loopback }) { // some servers listen on one of them only
            TcpClient c = new TcpClient(a.AddressFamily);
            try { Channel.Keep(c.Client); c.Connect(a, port); c.NoDelay = true; return c; }
            catch (SocketException e) { c.Close(); last = e; }
        }
        throw last;
    }
    static void Pump(Channel ch, TcpClient t) {
        ch.Timeout = Timeout.Infinite; ch.KeepAlive();
        NetworkStream ns = t.GetStream();
        Thread up = new Thread(delegate () {
            byte[] buf = new byte[65536]; int n;
            try { while ((n = ns.Read(buf, 0, buf.Length)) > 0) ch.Send(buf, n); ch.Send(buf, 0); } // empty: that was all
            catch (Exception) { ch.Dispose(); t.Close(); }
        });
        up.IsBackground = true; up.Start();
        try { byte[] d; while ((d = ch.Receive()).Length > 0) ns.Write(d, 0, d.Length); t.Client.Shutdown(SocketShutdown.Send); up.Join(); }
        catch (Exception) { }
        ch.Dispose(); t.Close();
    }
    Msg DoTunnel(ControlSession s, Dictionary<string, object> b, string from) {
        long port = Util.Num(b, "port");
        if (port < 1 || port > 65535) throw Lang.Err("端口要在 1 到 65535 之间", "a port is between 1 and 65535");
        Tunnel t = new Tunnel(); t.Id = Util.NewId(); t.Peer = from; t.Port = (int)port;
        try { Loopback(t.Port).Close(); } catch (SocketException) { s.SendSafe(new { k = "e", d = "（那台电脑的 " + port + " 端口现在没有程序在听，开好之后就能用）\n", d_en = "(Nothing listens on port " + port + " over there yet. It will work once something does.)\n" }); }
        lock (L) tunnels.Add(t);
        Stopwatch sw = Stopwatch.StartNew();
        try {
            s.Send(new { k = "open", tunnel = t.Id });
            while (!s.Stopped && !stopping) Thread.Sleep(200);
        } finally { lock (L) tunnels.Remove(t); t.Close(); }
        Msg span = BgJob.Span(sw.Elapsed.TotalSeconds), end = new Msg("已关闭 · 开了 " + span.Zh, "closed · open for " + span.En);
        try { s.Last(Done(Lang.M("端口转发在那台电脑上被关闭", "the forwarded port was closed on that PC"), end, 0)); } catch (Exception) { }
        return end;
    }
    void DoConn(Channel ch, Dictionary<string, object> b, string from) {
        Tunnel t;
        lock (L) t = tunnels.Find(delegate (Tunnel x) { return x.Id == Util.Str(b, "tunnel") && x.Peer == from; });
        if (t == null) { Reply(ch, false, Lang.M("端口转发已经关闭", "the forwarded port has been closed")); return; }
        if (Interlocked.Increment(ref conns) > MaxConns) { Interlocked.Decrement(ref conns); Reply(ch, false, Lang.M("连接太多", "too many connections")); return; }
        TcpClient c = null;
        try {
            try { c = Loopback(t.Port); } catch (SocketException e) { Reply(ch, false, Msg.Of(e)); return; }
            if (!t.Add(ch)) return;
            ch.SendJson(new { ok = true, k = "go" });
            Pump(ch, c);
        } finally { t.Remove(ch); if (c != null) c.Close(); Interlocked.Decrement(ref conns); }
    }
    // Asking side: the listening port here.
    class TunnelEnd : IDisposable {
        readonly Node node; readonly string peer, id; readonly TcpListener l; readonly Tunnel open = new Tunnel();
        public int Port;
        public TunnelEnd(Node n, string peerId, string tunnel, int want, int remote) {
            node = n; peer = peerId; id = tunnel;
            for (int i = 0; ; i++) {
                int port = want > 0 ? want : i == 0 ? remote : 0; // the same number if it is free, otherwise any
                l = new TcpListener(IPAddress.Loopback, port); Channel.Keep(l.Server); l.ExclusiveAddressUse = true;
                try { l.Start(); break; } catch (SocketException e) { l.Stop(); if (want > 0 || i > 0) throw Lang.Err("本机的 {0} 端口不能用：{1}", "port {0} on this PC can't be used: {1}", port, e.Message); }
            }
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            Run(Accept);
        }
        void Accept() {
            while (true) {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch (Exception) { return; } // closed
                Run(delegate {
                    Channel ch = null;
                    try {
                        Channel.Keep(c.Client); c.NoDelay = true;
                        Dictionary<string, object> r;
                        ch = node.Dial(peer, "control", out r);
                        string body = Util.ToJson(new { op = "conn", tunnel = id });
                        ch.SendJson(new { body = body, sig = node.Sign(Signed(peer, node.Cfg.Id, Util.Str(r, "nonce"), body)) });
                        if (Util.Str(ch.ReceiveJson(), "k") == "go" && open.Add(ch)) Pump(ch, c);
                    } catch (Exception) { } // the program using the port sees a closed connection
                    finally { if (ch != null) { open.Remove(ch); ch.Dispose(); } c.Close(); }
                });
            }
        }
        public void Dispose() { l.Stop(); open.Close(); }
    }

    // ---- handing work to the Claude Code on this PC: a background job whose output is made readable ----
    public static string FindClaude() {
        List<string> at = new List<string>();
        string set = Environment.GetEnvironmentVariable("LANTALK_CLAUDE");
        if (!string.IsNullOrEmpty(set)) return File.Exists(set) ? set : null;
        foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) foreach (string ext in new string[] { ".exe", ".cmd" }) if (d.Trim().Length > 2) try { at.Add(Path.Combine(d.Trim().Trim('"'), "claude" + ext)); } catch (ArgumentException) { }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        at.Add(Path.Combine(home, ".local\\bin\\claude.exe")); at.Add(Path.Combine(roam, "npm\\claude.cmd"));
        string own = Path.Combine(roam, "Claude\\claude-code"); // what the desktop app brings along
        try { if (Directory.Exists(own)) at.AddRange(Directory.GetFiles(own, "claude.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return at.Find(File.Exists);
    }
    static ProcessStartInfo ClaudeStart(Dictionary<string, object> b) {
        if (Util.Num(b, "hop") > 0) throw Lang.Err("这个 Claude 本身就是别的电脑派来的，不能再往外派活", "this Claude was itself sent by another PC, it can't hand work on");
        string exe = FindClaude(), cwd = Util.Str(b, "cwd"), mode = Util.Str(b, "mode"), resume = Util.Str(b, "resume"), model = Util.Str(b, "model");
        if (exe == null) throw Lang.Err("那台电脑没有装 Claude Code", "Claude Code isn't installed on that PC");
        if (cwd == "") cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(cwd)) throw Lang.Err("没有这个文件夹：{0}", "no such folder: {0}", cwd);
        if (mode != "" && mode != "plan" && mode != "acceptEdits") throw Lang.Err("--mode 只能是 plan 或 acceptEdits", "--mode is plan or acceptEdits");
        if (resume != "" && !Regex.IsMatch(resume, "\\A[0-9a-f-]{36}\\z")) throw Lang.Err("--resume 后面要是会话 id", "--resume needs a session id");
        if (model != "" && !Regex.IsMatch(model, "\\A[\\w.\\[\\]-]{1,60}\\z")) throw Lang.Err("模型名不对", "not a model name");
        string args = "-p --output-format stream-json --verbose" + (mode != "" ? " --permission-mode " + mode : "") + (resume != "" ? " --resume " + resume : "") + (model != "" ? " --model " + model : "");
        bool script = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo si = new ProcessStartInfo(script ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe, script ? "/d /c \"\"" + exe + "\" " + args + "\"" : args);
        si.UseShellExecute = false; si.CreateNoWindow = true; si.WorkingDirectory = cwd;
        si.RedirectStandardInput = si.RedirectStandardOutput = si.RedirectStandardError = true;
        si.StandardOutputEncoding = si.StandardErrorEncoding = new UTF8Encoding(false);
        si.EnvironmentVariables["LANTALK_HOP"] = "1";
        return si;
    }
    // One line of Claude's stream-json as text, null when there is nothing to show. Notes the session and the outcome.
    public static string ClaudeLine(string line, BgJob j) {
        Dictionary<string, object> d;
        try { d = Util.FromJson<Dictionary<string, object>>(line); } catch (Exception) { return line.Trim().Length == 0 ? null : line; } // not JSON: a warning
        if (d == null) return null;
        string id = Util.Str(d, "session_id"), type = Util.Str(d, "type");
        if (id != "") j.Session = id;
        StringBuilder o = new StringBuilder();
        if (type == "assistant" && d.ContainsKey("message") && d["message"] is Dictionary<string, object>)
            foreach (Dictionary<string, object> c in Dicts((Dictionary<string, object>)d["message"], "content")) {
                string k = Util.Str(c, "type");
                if (k == "text") o.Append(Util.Str(c, "text")).Append('\n');
                else if (k == "tool_use") {
                    Dictionary<string, object> i = c.ContainsKey("input") ? c["input"] as Dictionary<string, object> : null;
                    string what = "";
                    if (i != null) foreach (string key in new string[] { "command", "file_path", "path", "pattern", "query", "url", "description", "prompt" }) if (what == "") what = Util.Str(i, key);
                    o.Append("· ").Append(Util.Str(c, "name")).Append(what == "" ? "" : ": " + Util.Cut(what.Replace("\r", "").Replace("\n", " ; "), 160)).Append('\n');
                }
            }
        if (type == "result") {
            j.Failed = Util.Flag(d, "is_error") || Util.Str(d, "subtype").StartsWith("error");
            int denied = Dicts(d, "permission_denials").Count();
            if (denied > 0) o.Append(Lang.T("（有 {0} 次操作没有权限，被跳过。那边的设置里可以放行，或用 --mode acceptEdits）", "({0} actions were not permitted and left out. Allow them in the settings over there, or use --mode acceptEdits)", denied)).Append('\n');
            if (j.Failed && Util.Str(d, "result").IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 || Util.Str(d, "result").StartsWith("Not logged in")) { j.Failed = true; o.Append(Lang.T("（那台电脑的 Claude Code 还没登录。要由人在那边打开终端运行 claude auth login）", "(Claude Code on that PC isn't logged in. Someone there has to run claude auth login in a terminal)")).Append('\n'); }
        }
        return o.Length == 0 ? null : o.ToString();
    }
    static Thread PourClaude(StreamReader from, FileStream log, BgJob j) {
        Thread t = new Thread(delegate () {
            string line;
            try {
                while ((line = from.ReadLine()) != null) {
                    string s = ClaudeLine(line, j);
                    if (s == null) continue;
                    byte[] b = Encoding.UTF8.GetBytes(s);
                    lock (log) if (log.Length < MaxLog) { log.Write(b, 0, b.Length); log.Flush(); }
                }
            } catch (Exception) { } // process gone
        });
        t.IsBackground = true; t.Start();
        return t;
    }

    // ---- updating LanTalk ----
    public string[] LaunchArgs = new string[0]; // so it starts again the way it was started
    public event Action RestartRequested;
    static string build;
    public static string Build { get { if (build == null) try { build = ShaHex(typeof(Node).Assembly.Location).Substring(0, 8); } catch (IOException) { build = ""; } return build; } }
    public static string ShaHex(string f) { using (SHA256 h = SHA256.Create()) using (FileStream s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    static readonly string[] Programs = { "LanTalk.exe", "lt.exe" };
    static string InstallDir { get { return Path.GetDirectoryName(typeof(Node).Assembly.Location); } }

    // What an update would interrupt here.
    int Ongoing(string exceptMsg) {
        lock (L) {
            int n = sessions.Keys.Count(delegate (string id) { return id != exceptMsg; });
            foreach (Shell h in shells) if (!Monitor.TryEnter(h.Busy)) n++; else Monitor.Exit(h.Busy);
            foreach (List<ChatMsg> l in history.Values) n += l.Count(delegate (ChatMsg m) { return m.Kind == "file" && (m.State == "sending" || m.State == "recv"); });
            return n;
        }
    }
    void CheckUpdate(Dictionary<string, object> b, string exceptMsg) {
        if (!string.Equals(Path.GetFileName(typeof(Node).Assembly.Location), Programs[0], StringComparison.OrdinalIgnoreCase)) throw Lang.Err("那台电脑运行的不是 LanTalk.exe", "that PC isn't running LanTalk.exe");
        int n = Ongoing(exceptMsg);
        if (n > 0 && !Util.Flag(b, "force")) throw Lang.Err("那台电脑上有 {0} 项正在进行，更新会打断它们。等它们结束，或加 --force", "{0} things are running on that PC and an update would interrupt them. Wait for them, or use --force", n);
        List<string> sha = Strings(b, "sha");
        if (sha.Count != 2) throw Lang.Err("文件列表无效", "invalid file list");
        bool same = true;
        for (int i = 0; i < 2; i++) { string f = Path.Combine(InstallDir, Programs[i]); same &= File.Exists(f) && Convert.ToBase64String(Hex(ShaHex(f))) == sha[i]; }
        if (same) throw Lang.Err("已经是这个版本了", "it already has this version");
    }
    static byte[] Hex(string s) { byte[] b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16); return b; }
    static string NewName(string f) { return Path.Combine(InstallDir, Path.GetFileNameWithoutExtension(f) + ".new.exe"); }
    Msg DoUpdate(ControlSession s, Channel ch, Dictionary<string, object> b) {
        List<string> sha = Strings(b, "sha"), size = Strings(b, "size");
        s.StopPing();
        s.Send(new { k = "go" });
        for (int i = 0; i < 2; i++) ReceiveTo(NewName(Programs[i]), true, long.Parse(size[i]), sha[i], delegate { if (s.Stopped || stopping) throw new LtException(Stopped); return ch.Receive(); });
        Swap();
        Msg end = Lang.M("已换上，正在重启", "in place, restarting");
        s.Last(Done(null, end, 0));
        Run(delegate { Thread.Sleep(500); Restart(); });
        return end;
    }
    // From the command line on this PC: install a build that lies in a folder here.
    public void UpdateSelf(string folder, bool force) {
        Dictionary<string, object> b = new Dictionary<string, object>(); b["force"] = force;
        b["sha"] = Programs.Select(delegate (string f) { return Convert.ToBase64String(Hex(ShaHex(Path.Combine(folder, f)))); }).ToArray();
        CheckUpdate(b, "");
        foreach (string f in Programs) File.Copy(Path.Combine(folder, f), NewName(f), true);
        Swap();
        Run(delegate { Thread.Sleep(500); Restart(); });
    }
    string swapped; // time stamp of the copies that were put aside
    void Swap() {
        ProcessStartInfo si = new ProcessStartInfo(NewName(Programs[1]), "version"); si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardOutput = true; // does it run at all?
        using (Process p = Process.Start(si)) {
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000) || p.ExitCode != 0 || !o.StartsWith("LanTalk ")) throw Lang.Err("新版本运行不起来，没有换", "the new version doesn't run, nothing was changed");
        }
        string at = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        List<string> done = new List<string>();
        try {
            foreach (string f in Programs) {
                string now = Path.Combine(InstallDir, f), old = Path.Combine(InstallDir, Path.GetFileNameWithoutExtension(f) + ".old-" + at + ".exe");
                File.Move(now, old); done.Add(f); // a running program can be renamed
                File.Move(NewName(f), now);
            }
        } catch {
            foreach (string f in done) { string now = Path.Combine(InstallDir, f), old = Path.Combine(InstallDir, Path.GetFileNameWithoutExtension(f) + ".old-" + at + ".exe"); if (File.Exists(now)) File.Delete(now); File.Move(old, now); }
            throw;
        }
        swapped = at;
    }
    // Ends this program; a small script starts the new one, and the old one again if the new one doesn't come up.
    void Restart() {
        string flag = Path.Combine(dir, "updated-" + Util.NewId());
        Func<string, string> q = delegate (string x) { return "'" + x.Replace("'", "''") + "'"; };
        string args = string.Join(" ", LaunchArgs.Where(delegate (string a) { return a != "--tray" && !a.StartsWith("--updated"); }).Select(delegate (string a) { return a.Contains(" ") ? "\"" + a + "\"" : a; }).ToArray());
        string script = "$d = " + q(InstallDir) + "; $flag = " + q(flag) + "; $args2 = " + q(("--tray " + args).Trim()) + "\n"
            + "try { Wait-Process -Id " + Process.GetCurrentProcess().Id + " -Timeout 15 -ErrorAction Stop } catch { Stop-Process -Id " + Process.GetCurrentProcess().Id + " -Force -ErrorAction SilentlyContinue }\n"
            + "$p = Start-Process \"$d\\LanTalk.exe\" -ArgumentList ($args2 + ' \"--updated=' + $flag + '\"') -PassThru\n"
            + "for ($i = 0; $i -lt 100 -and -not (Test-Path -LiteralPath $flag) -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 300 }\n"
            + "if (Test-Path -LiteralPath $flag) { Remove-Item -LiteralPath $flag; exit }\n"
            + "if (-not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }\n"
            + "foreach ($x in 'LanTalk', 'lt') { if (Test-Path \"$d\\$x.failed.exe\") { Remove-Item \"$d\\$x.failed.exe\" }; Move-Item \"$d\\$x.exe\" \"$d\\$x.failed.exe\"; Move-Item \"$d\\$x.old-" + swapped + ".exe\" \"$d\\$x.exe\" }\n"
            + "Start-Process \"$d\\LanTalk.exe\" -ArgumentList $args2\n";
        ProcessStartInfo si = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"), "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        si.UseShellExecute = true; si.WindowStyle = ProcessWindowStyle.Hidden; // not a child in the usual sense: inherits nothing
        Process.Start(si).Dispose();
        Action h = RestartRequested;
        if (h != null) h();
    }
    // Asking side: the other PC says itself which build it runs.
    public string AfterUpdate(string peerId, string wantBuild, int seconds) {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        string seen = "";
        bool gone = false;
        while (DateTime.UtcNow < end) {
            Peer p = Find(peerId);
            if (p == null) break;
            if (!p.Online) gone = true;
            else if (p.Build == wantBuild) return wantBuild;
            else if (gone) { seen = p.Build ?? ""; if (seen != "") { Thread.Sleep(3500); p = Find(peerId); return p == null ? "" : p.Build ?? ""; } }
            Thread.Sleep(300);
        }
        return seen;
    }
}
}
