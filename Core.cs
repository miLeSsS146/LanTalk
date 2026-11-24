// LanTalk core: discovery, encrypted transport, message queue, history. No UI in here.
// Written for the C# 5 compiler that ships with Windows (.NET Framework 4.7.2+), no dependencies.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace LanTalk {

public class Peer {
    public string Id, Name, Ip;
    public int Port;
    [ScriptIgnore] public DateTime LastSeen;
    [ScriptIgnore] public DateTime RetryAfter;
    [ScriptIgnore] public bool Online { get { return (DateTime.UtcNow - LastSeen).TotalSeconds < 12; } }
}

public class ChatMsg {
    public string Id, Conv, From, FromName, Agent, Kind, Text, Path, State, Error, ErrorEn; // Kind: text|file|run  State: queued|sending|ok|recv|fail|cancel, run while a remote command runs
    public long Time, Size, Seq;   // Seq: local arrival order, the CLI inbox cursor
    public List<string> Pending = new List<string>();  // peers that don't have it yet (own messages only)
    public void Err(Msg m) { Error = m == null ? null : m.Zh; ErrorEn = m == null ? null : m.En; }
    [ScriptIgnore] public string ErrText { get { return new Msg(Error ?? "", string.IsNullOrEmpty(ErrorEn) ? Error ?? "" : ErrorEn); } }
    [ScriptIgnore] public long Done;
    [ScriptIgnore] public volatile bool Cancelled;
    [ScriptIgnore] public DateTime LocalTime { get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(Time).ToLocalTime(); } }
}

public class Settings {
    public string Id, Name, CodeProtected, KeyProtected, SaveDir, Lang; // Lang: zh | en | auto
    public int Port;
    public List<string> Manual = new List<string>();  // "ip" or "ip:port"
    public List<Peer> Peers = new List<Peer>();
    public List<Grant> Control = new List<Grant>();   // PCs that may control this one, see Control.cs
}

public static class Util {
    public static string ToJson(object o) { JavaScriptSerializer j = new JavaScriptSerializer(); j.MaxJsonLength = int.MaxValue; return j.Serialize(o); }
    public static T FromJson<T>(string s) { JavaScriptSerializer j = new JavaScriptSerializer(); j.MaxJsonLength = int.MaxValue; return j.Deserialize<T>(s); }
    public static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
    public static long Num(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToInt64(v) : 0; }
    public static bool Flag(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
    public static bool IsId(string s) { return s != null && Regex.IsMatch(s, "\\A[0-9a-f]{32}\\z"); }
    public static string NewId() { return Guid.NewGuid().ToString("N"); }
    public static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
    public static string Cut(string s, int n) { return s.Length <= n ? s : s.Substring(0, n); }

    public static string SizeText(long n) {
        if (n < 1024) return n + " B";
        if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0") + " KB";
        if (n < 1024L * 1024 * 1024) return (n / 1048576.0).ToString("0.0") + " MB";
        return (n / 1073741824.0).ToString("0.00") + " GB";
    }

    static readonly Regex Reserved = new Regex("^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[0-9]|LPT[0-9])(\\..*)?$", RegexOptions.IgnoreCase);

    // Turns a path sent by another PC into a safe relative path: no drive, no "..", no device names.
    public static string SafeRelative(string p) {
        List<string> parts = new List<string>();
        foreach (string raw in (p ?? "").Split('/', '\\')) {
            StringBuilder b = new StringBuilder();
            foreach (char c in raw) b.Append(c < 32 || "<>:\"|?*".IndexOf(c) >= 0 ? '_' : c);
            string s = Cut(b.ToString(), 150).Trim(' ').TrimEnd('.', ' ');
            if (s.Length == 0) continue;               // also drops "." and ".."
            if (Reserved.IsMatch(s)) s = "_" + s;
            parts.Add(s);
        }
        return parts.Count == 0 ? "file" : string.Join("\\", parts.ToArray());
    }

    // Two names can become equal once sanitized (or differ only in case): number the later one.
    public static string UniqueRel(string rel, HashSet<string> seen) {
        string r = rel;
        for (int i = 1; !seen.Add(r); i++) r = Path.Combine(Path.GetDirectoryName(rel), Path.GetFileNameWithoutExtension(rel) + " (" + i + ")" + Path.GetExtension(rel));
        return r;
    }

    public static string UniquePath(string path) {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++) {
            string t = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(t) && !Directory.Exists(t)) return t;
        }
    }
}

public static class Crypto {
    const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 characters, no I O 0 1

    public static string NewCode() {
        byte[] r = Random(16);
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < 16; i++) { if (i > 0 && i % 4 == 0) b.Append('-'); b.Append(Alphabet[r[i] & 31]); }
        return b.ToString();
    }
    // Returns the canonical form, or null when it isn't a valid pairing code.
    public static string NormalizeCode(string s) {
        StringBuilder b = new StringBuilder();
        foreach (char c in (s ?? "").ToUpperInvariant()) {
            if (c == '-' || char.IsWhiteSpace(c)) continue;
            if (Alphabet.IndexOf(c) < 0) return null;
            if (b.Length > 0 && b.Length % 5 == 4) b.Append('-');
            b.Append(c);
        }
        return b.Length == 19 ? b.ToString() : null;
    }
    public static byte[] Random(int n) { byte[] b = new byte[n]; using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
    public static byte[] Master(string code) {
        using (Rfc2898DeriveBytes k = new Rfc2898DeriveBytes(code, Encoding.ASCII.GetBytes("LanTalk-v1-pairing"), 100000, HashAlgorithmName.SHA256)) return k.GetBytes(32);
    }
    public static byte[] Hmac(byte[] key, params byte[][] parts) {
        using (HMACSHA256 h = new HMACSHA256(key)) {
            foreach (byte[] p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
            h.TransformFinalBlock(new byte[0], 0, 0);
            return h.Hash;
        }
    }
    public static bool Same(byte[] a, byte[] b) {
        if (a.Length != b.Length) return false;
        int d = 0;
        for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
        return d == 0;
    }
    public static string Protect(string s) { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
    public static string Unprotect(string s) { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
}

// One encrypted TCP connection. Both sides contribute a nonce, so keys are fresh per connection and
// recorded traffic can't be replayed. Frames: length | iv | AES-256-CBC | HMAC-SHA256(seq, iv, ct).
public class Channel : IDisposable {
    const int MaxFrame = 8 * 1024 * 1024;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("LTK1");
    readonly TcpClient tcp;
    readonly NetworkStream ns;
    byte[] sendEnc, sendMac, recvEnc, recvMac;
    long sendSeq, recvSeq;
    readonly Aes aes = new AesCryptoServiceProvider(), aesIn = new AesCryptoServiceProvider(); // one each way: remote control sends and receives at once

    Channel(TcpClient c) { tcp = c; c.NoDelay = true; ns = c.GetStream(); ns.ReadTimeout = 10000; ns.WriteTimeout = 30000; }

    public static Channel Connect(string ip, int port, byte[] master) {
        TcpClient c = new TcpClient(AddressFamily.InterNetwork);
        try {
            IAsyncResult r = c.BeginConnect(IPAddress.Parse(ip), port, null, null);
            if (!r.AsyncWaitHandle.WaitOne(4000)) throw new IOException(Lang.T("连接超时", "the connection timed out"));
            c.EndConnect(r);
            Channel ch = new Channel(c);
            byte[] mine = Crypto.Random(32);
            ch.ns.Write(Magic, 0, 4); ch.ns.Write(mine, 0, 32);
            byte[] theirs = ch.ReadExact(32);
            ch.SetKeys(master, mine, theirs, true);
            return ch;
        } catch { c.Close(); throw; }
    }
    public static Channel Accept(TcpClient c, byte[] master) {
        Channel ch = new Channel(c);
        if (!Crypto.Same(ch.ReadExact(4), Magic)) throw new IOException("bad magic");
        byte[] theirs = ch.ReadExact(32), mine = Crypto.Random(32);
        ch.ns.Write(mine, 0, 32);
        ch.SetKeys(master, theirs, mine, false);
        return ch;
    }
    void SetKeys(byte[] m, byte[] cn, byte[] sn, bool client) {
        byte[] ce = Crypto.Hmac(m, Encoding.ASCII.GetBytes("c2s-enc"), cn, sn), cm = Crypto.Hmac(m, Encoding.ASCII.GetBytes("c2s-mac"), cn, sn);
        byte[] se = Crypto.Hmac(m, Encoding.ASCII.GetBytes("s2c-enc"), cn, sn), sm = Crypto.Hmac(m, Encoding.ASCII.GetBytes("s2c-mac"), cn, sn);
        sendEnc = client ? ce : se; sendMac = client ? cm : sm; recvEnc = client ? se : ce; recvMac = client ? sm : cm;
    }
    public int Timeout { set { ns.ReadTimeout = value; ns.WriteTimeout = value; } }
    public string RemoteIp { get { return ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString(); } }

    byte[] ReadExact(int n) {
        byte[] b = new byte[n];
        int got = 0;
        while (got < n) { int r = ns.Read(b, got, n - got); if (r <= 0) throw new EndOfStreamException(Lang.T("连接已断开", "the connection was closed")); got += r; }
        return b;
    }
    public void Send(byte[] data, int count) {
        byte[] iv = Crypto.Random(16), ct;
        using (ICryptoTransform t = aes.CreateEncryptor(sendEnc, iv)) ct = t.TransformFinalBlock(data, 0, count);
        byte[] mac = Crypto.Hmac(sendMac, BitConverter.GetBytes(sendSeq++), iv, ct);
        byte[] f = new byte[4 + 16 + ct.Length + 32];
        Buffer.BlockCopy(BitConverter.GetBytes(f.Length - 4), 0, f, 0, 4);
        Buffer.BlockCopy(iv, 0, f, 4, 16); Buffer.BlockCopy(ct, 0, f, 20, ct.Length); Buffer.BlockCopy(mac, 0, f, 20 + ct.Length, 32);
        ns.Write(f, 0, f.Length);
    }
    public byte[] Receive() {
        int len = BitConverter.ToInt32(ReadExact(4), 0);
        if (len < 64 || len > MaxFrame) throw new IOException("bad frame");
        byte[] f = ReadExact(len), iv = new byte[16], ct = new byte[len - 48], mac = new byte[32];
        Buffer.BlockCopy(f, 0, iv, 0, 16); Buffer.BlockCopy(f, 16, ct, 0, ct.Length); Buffer.BlockCopy(f, len - 32, mac, 0, 32);
        if (!Crypto.Same(mac, Crypto.Hmac(recvMac, BitConverter.GetBytes(recvSeq++), iv, ct))) throw new CryptographicException(Lang.T("配对码不一致", "the pairing codes differ"));
        using (ICryptoTransform t = aesIn.CreateDecryptor(recvEnc, iv)) return t.TransformFinalBlock(ct, 0, ct.Length);
    }
    public void SendJson(object o) { byte[] b = Encoding.UTF8.GetBytes(Util.ToJson(o)); Send(b, b.Length); }
    public Dictionary<string, object> ReceiveJson() { return Util.FromJson<Dictionary<string, object>>(Encoding.UTF8.GetString(Receive())); }
    public void Dispose() { tcp.Close(); aes.Dispose(); aesIn.Dispose(); }
}

// The receiver said no (disk full, ...) or the source is unreadable: retrying won't help.
public class PermanentException : Exception {
    public readonly Msg M;
    public PermanentException(Msg m) { M = m; }
    public override string Message { get { return M; } }
}

public partial class Node : IDisposable {
    public const int DefaultPort = 47820, MaxText = 100000, MaxFiles = 20000;
    public const string All = "all", Ai = "ai";   // everyone | the Claude collaboration channel
    const int Chunk = 256 * 1024;

    public readonly object L = new object();
    public readonly Settings Cfg;
    public event Action PeersChanged;
    public event Action<ChatMsg> MessageAdded;    // new message (own or incoming)
    public event Action<ChatMsg> MessageChanged;  // state/progress

    readonly string dir;
    readonly IPAddress bind;
    readonly Dictionary<string, List<ChatMsg>> history = new Dictionary<string, List<ChatMsg>>();
    readonly HashSet<string> busy = new HashSet<string>();
    readonly Semaphore slots = new Semaphore(64, 64);
    readonly AutoResetEvent pumpSignal = new AutoResetEvent(false);
    byte[] master, discKey;
    UdpClient udp;
    TcpListener tcp;
    volatile bool stopping;
    string lastPresence = "";

    public Node(string dataDir, IPAddress bindTo) {
        dir = dataDir; bind = bindTo;
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "config.json");
        bool known = File.Exists(f);
        Cfg = known ? Util.FromJson<Settings>(File.ReadAllText(f, Encoding.UTF8)) : new Settings();
        if (string.IsNullOrEmpty(Cfg.Lang)) Cfg.Lang = known ? "zh" : "auto"; // set up before there was a choice: it was Chinese
        if (!Util.IsId(Cfg.Id)) Cfg.Id = Util.NewId();
        if (string.IsNullOrEmpty(Cfg.Name)) Cfg.Name = Environment.MachineName;
        if (Cfg.Port <= 0) Cfg.Port = DefaultPort;
        if (string.IsNullOrEmpty(Cfg.SaveDir)) Cfg.SaveDir = Ipc.DefaultSave;
        Cfg.Peers.RemoveAll(delegate (Peer p) { return !Util.IsId(p.Id); });
        if (Cfg.Control == null) Cfg.Control = new List<Grant>();
        Cfg.Control.RemoveAll(delegate (Grant g) { return !Util.IsId(g.Id) || string.IsNullOrEmpty(g.Key); });
    }

    public static bool IsGroup(string conv) { return conv == All || conv == Ai; }

    // ---- settings ----
    public string Code { get { try { return string.IsNullOrEmpty(Cfg.CodeProtected) ? null : Crypto.Unprotect(Cfg.CodeProtected); } catch (CryptographicException) { return null; } } }
    public void SetCode(string code) {
        byte[] m = Crypto.Master(code);
        lock (L) { Cfg.CodeProtected = Crypto.Protect(code); master = m; discKey = Crypto.Hmac(m, Encoding.ASCII.GetBytes("discovery")); }
        SaveConfig();
    }
    public void SaveConfig() {
        lock (L) {
            string f = Path.Combine(dir, "config.json"), t = f + ".tmp";
            File.WriteAllText(t, Util.ToJson(Cfg), Encoding.UTF8);
            if (File.Exists(f)) File.Replace(t, f, null); else File.Move(t, f);
        }
    }

    // ---- lifetime ----
    public void Start() {
        string code = Code;
        if (code == null) throw new InvalidOperationException("no pairing code");
        SetCode(code);
        LoadKey();
        LoadHistory();
        Directory.CreateDirectory(Cfg.SaveDir);
        foreach (string d in Directory.GetDirectories(Cfg.SaveDir, ".recv-*")) // leftovers of interrupted transfers
            if (Util.IsId(Path.GetFileName(d).Substring(6))) try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        tcp = new TcpListener(bind, Cfg.Port);
        tcp.ExclusiveAddressUse = true;
        tcp.Start();
        udp = new UdpClient(new IPEndPoint(bind, Cfg.Port));
        udp.EnableBroadcast = true;
        udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); // SIO_UDP_CONNRESET off: ICMP unreachable must not break Receive
        Run(AcceptLoop); Run(UdpLoop); Run(HelloLoop); Run(PumpLoop);
    }
    static void Run(ThreadStart a) { Thread t = new Thread(a); t.IsBackground = true; t.Start(); }
    public void Dispose() {
        if (stopping) return;
        if (udp != null) Hello(true);
        stopping = true;
        pumpSignal.Set();
        if (tcp != null) tcp.Stop();
        if (udp != null) udp.Close();
    }

    // ---- discovery ----
    void HelloLoop() {
        while (!stopping) {
            Hello(false);
            CheckPresence();
            Thread.Sleep(3000);
        }
    }
    void Hello(bool bye) {
        byte[] body, key;
        List<IPEndPoint> to = new List<IPEndPoint>();
        lock (L) {
            key = discKey;
            body = Encoding.UTF8.GetBytes(Util.ToJson(new { id = Cfg.Id, name = Cfg.Name, port = Cfg.Port, bye = bye }));
            foreach (Peer p in Cfg.Peers) { IPAddress a; if (IPAddress.TryParse(p.Ip, out a)) to.Add(new IPEndPoint(a, p.Port)); }
            foreach (string m in Cfg.Manual) { IPEndPoint e = ParseEndpoint(m); if (e != null) to.Add(e); }
        }
        if (!IPAddress.IsLoopback(bind)) to.AddRange(Broadcasts());
        byte[] mac = Crypto.Hmac(key, body), pkt = new byte[32 + body.Length];
        Buffer.BlockCopy(mac, 0, pkt, 0, 32); Buffer.BlockCopy(body, 0, pkt, 32, body.Length);
        foreach (IPEndPoint e in to.Distinct())
            try { udp.Send(pkt, pkt.Length, e); } catch (SocketException) { } catch (ObjectDisposedException) { return; }
    }
    public static IPEndPoint ParseEndpoint(string s) {
        string[] p = (s ?? "").Trim().Split(':');
        IPAddress a; int port = DefaultPort;
        if (p.Length > 2 || p[0].Split('.').Length != 4 || !IPAddress.TryParse(p[0], out a)) return null;
        if (p.Length == 2 && (!int.TryParse(p[1], out port) || port < 1 || port > 65535)) return null;
        return new IPEndPoint(a, port);
    }
    List<IPEndPoint> Broadcasts() {
        List<IPEndPoint> r = new List<IPEndPoint>();
        try {
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces()) {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation u in n.GetIPProperties().UnicastAddresses) {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] a = u.Address.GetAddressBytes(), m = u.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++) a[i] |= (byte)~m[i];
                    r.Add(new IPEndPoint(new IPAddress(a), Cfg.Port));
                }
            }
        } catch (NetworkInformationException) { }
        return r;
    }
    void UdpLoop() {
        while (!stopping) {
            try {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] d = udp.Receive(ref from);
                if (d.Length < 34 || d.Length > 4096) continue;
                byte[] mac = new byte[32], body = new byte[d.Length - 32], key;
                Buffer.BlockCopy(d, 0, mac, 0, 32); Buffer.BlockCopy(d, 32, body, 0, body.Length);
                lock (L) key = discKey;
                if (!Crypto.Same(mac, Crypto.Hmac(key, body))) continue;
                Dictionary<string, object> j = Util.FromJson<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                Seen(Util.Str(j, "id"), Util.Str(j, "name"), from.Address.ToString(), (int)Util.Num(j, "port"), Util.Flag(j, "bye"));
            } catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (stopping) return; Thread.Sleep(100); }
            catch (Exception) { } // malformed packet
        }
    }
    void Seen(string id, string name, string ip, int port, bool bye) {
        if (!Util.IsId(id) || id == Cfg.Id || port < 1 || port > 65535) return;
        name = Util.Cut(name.Trim(), 40);
        if (name.Length == 0) name = ip;
        bool save = false, cameOnline;
        lock (L) {
            Peer p = Cfg.Peers.Find(delegate (Peer x) { return x.Id == id; });
            if (p == null) { if (bye) return; p = new Peer(); p.Id = id; Cfg.Peers.Add(p); save = true; }
            if (p.Name != name || p.Ip != ip || p.Port != port) { p.Name = name; p.Ip = ip; p.Port = port; save = true; }
            cameOnline = !bye && !p.Online;
            p.LastSeen = bye ? DateTime.MinValue : DateTime.UtcNow;
            if (cameOnline) p.RetryAfter = DateTime.MinValue;
        }
        if (save) SaveConfig();
        CheckPresence();
        if (cameOnline) pumpSignal.Set();
    }
    void CheckPresence() {
        string s;
        lock (L) {
            s = string.Join("|", Cfg.Peers.Select(delegate (Peer p) { return p.Id + p.Name + p.Online; }).ToArray());
            if (s == lastPresence) return;
            lastPresence = s;
        }
        Action h = PeersChanged;
        if (h != null) h();
    }
    public List<Peer> Peers() { lock (L) return new List<Peer>(Cfg.Peers); }
    public void ForgetPeer(string id) {
        List<ChatMsg> changed = new List<ChatMsg>();
        lock (L) {
            Cfg.Peers.RemoveAll(delegate (Peer p) { return p.Id == id; });
            Cfg.Control.RemoveAll(delegate (Grant g) { return g.Id == id; });
            foreach (List<ChatMsg> l in history.Values) foreach (ChatMsg m in l)
                if (m.Pending.Remove(id) && m.Pending.Count == 0) { m.State = m.Conv == id ? "cancel" : "ok"; m.Cancelled = m.Conv == id; changed.Add(m); }
            foreach (ChatMsg m in changed) Append(m);
        }
        StopFrom(id);
        SaveConfig(); CheckPresence();
    }

    // ---- history: one JSON-lines file per conversation, a later line with the same id wins ----
    void LoadHistory() {
        lock (L) {
            history.Clear();
            foreach (string f in Directory.GetFiles(dir, "chat-*.jsonl")) {
                string conv = Path.GetFileNameWithoutExtension(f).Substring(5);
                if (!IsGroup(conv) && !Util.IsId(conv)) continue;
                List<string> order = new List<string>();
                Dictionary<string, ChatMsg> map = new Dictionary<string, ChatMsg>();
                int lines = 0;
                foreach (string line in File.ReadAllLines(f, Encoding.UTF8)) {
                    if (line.Length == 0) continue;
                    lines++;
                    try {
                        ChatMsg m = Util.FromJson<ChatMsg>(line);
                        if (m == null || !Util.IsId(m.Id)) continue;
                        if (m.Pending == null) m.Pending = new List<string>();
                        if (!map.ContainsKey(m.Id)) order.Add(m.Id);
                        map[m.Id] = m;
                    } catch (Exception) { } // half-written last line after a crash
                }
                List<ChatMsg> list = order.Select(delegate (string id) { return map[id]; }).ToList();
                foreach (ChatMsg m in list) {
                    if (m.State == "sending") m.State = "queued";
                    if (m.State == "run") { m.State = "ok"; m.Err(Lang.M("中断", "interrupted")); }
                    if (m.State == "recv") { m.State = "fail"; m.Err(Lang.M("传输中断", "transfer interrupted")); }
                }
                history[conv] = list;
                foreach (ChatMsg m in list) lastSeq = Math.Max(lastSeq, m.Seq);
                if (lines > list.Count + 200) {
                    string t = f + ".tmp";
                    File.WriteAllLines(t, list.Select(delegate (ChatMsg m) { return Util.ToJson(m); }).ToArray(), new UTF8Encoding(false));
                    File.Replace(t, f, null);
                }
            }
        }
    }
    void Append(ChatMsg m) { // caller holds L
        File.AppendAllText(Path.Combine(dir, "chat-" + m.Conv + ".jsonl"), Util.ToJson(m) + "\n", new UTF8Encoding(false));
    }
    List<ChatMsg> ListOf(string conv) { // caller holds L
        List<ChatMsg> l;
        if (!history.TryGetValue(conv, out l)) history[conv] = l = new List<ChatMsg>();
        return l;
    }
    public List<ChatMsg> History(string conv, int max) {
        lock (L) { List<ChatMsg> l = ListOf(conv); return l.Skip(Math.Max(0, l.Count - max)).ToList(); }
    }
    public Peer Find(string id) { lock (L) return Cfg.Peers.Find(delegate (Peer p) { return p.Id == id; }); }
    long lastSeq;
    long NextSeq() { lastSeq = Math.Max(lastSeq + 1, Util.Now()); return lastSeq; } // caller holds L
    // What the local Claude should see: the Claude channel, plus anything another PC's Claude sent here directly.
    public List<ChatMsg> Inbox(long after) {
        lock (L) return history.Values.SelectMany(delegate (List<ChatMsg> l) { return l; })
            .Where(delegate (ChatMsg m) { return m.From != Cfg.Id && m.Kind != "run" && m.State == "ok" && m.Seq > after && (m.Conv == Ai || !string.IsNullOrEmpty(m.Agent)); })
            .OrderBy(delegate (ChatMsg m) { return m.Seq; }).ToList();
    }
    public ChatMsg Get(string id) {
        lock (L) foreach (List<ChatMsg> l in history.Values) foreach (ChatMsg m in l) if (m.Id == id) return m;
        return null;
    }
    void Add(ChatMsg m) {
        lock (L) { List<ChatMsg> l = ListOf(m.Conv); l.RemoveAll(delegate (ChatMsg x) { return x.Id == m.Id; }); l.Add(m); m.Seq = NextSeq(); Append(m); }
        Action<ChatMsg> h = MessageAdded;
        if (h != null) h(m);
    }
    void Changed(ChatMsg m, bool persist) {
        if (persist) lock (L) Append(m);
        Action<ChatMsg> h = MessageChanged;
        if (h != null) h(m);
    }

    // ---- sending ----
    ChatMsg NewOwn(string conv, string kind, string text, string agent) {
        if (!IsGroup(conv) && Find(conv) == null) throw Lang.Err("没有这台电脑", "no such PC");
        ChatMsg m = new ChatMsg();
        m.Id = Util.NewId(); m.Conv = conv; m.From = Cfg.Id; m.FromName = Cfg.Name; m.Agent = agent ?? ""; m.Kind = kind; m.Text = text; m.Time = Util.Now(); m.State = "queued";
        lock (L) m.Pending = IsGroup(conv) ? Cfg.Peers.Select(delegate (Peer p) { return p.Id; }).ToList() : new List<string> { conv };
        if (m.Pending.Count == 0) throw Lang.Err("还没有发现其他电脑", "no other PC has been found yet");
        return m;
    }
    public ChatMsg SendText(string conv, string text) { return SendText(conv, text, null); }
    public ChatMsg SendText(string conv, string text, string agent) {
        if (text.Trim().Length == 0) throw Lang.Err("消息是空的", "the message is empty");
        if (text.Length > MaxText) throw Lang.Err("文字太长，请存成文件再发", "the text is too long, send it as a file");
        ChatMsg m = NewOwn(conv, "text", text, agent);
        Add(m); pumpSignal.Set();
        return m;
    }
    public ChatMsg SendPath(string conv, string path) { return SendPath(conv, path, null); }
    public ChatMsg SendPath(string conv, string path, string agent) {
        path = Path.GetFullPath(path);
        if (path.Length > 3) path = path.TrimEnd('\\');
        if (Path.GetFileName(path).Length == 0) throw Lang.Err("不能直接发送整个磁盘", "can't send a whole drive");
        if (!File.Exists(path) && !Directory.Exists(path)) throw Lang.Err("找不到：{0}", "not found: {0}", path);
        ChatMsg m = NewOwn(conv, "file", Path.GetFileName(path), agent);
        m.Path = path;
        if (File.Exists(path)) m.Size = new FileInfo(path).Length;
        Add(m); pumpSignal.Set();
        return m;
    }
    public void Cancel(ChatMsg m) {
        lock (L) {
            if (m.From != Cfg.Id || m.Pending.Count == 0) return;
            m.Cancelled = true; m.Pending.Clear(); m.State = "cancel"; Append(m);
        }
        Changed(m, false);
    }
    public void Retry(ChatMsg m) {
        lock (L) {
            if (m.From != Cfg.Id || m.State != "fail" && m.State != "cancel") return;
            m.Cancelled = false; m.Err(null); m.State = "queued"; m.Done = 0;
            m.Pending = IsGroup(m.Conv) ? Cfg.Peers.Select(delegate (Peer p) { return p.Id; }).ToList() : new List<string> { m.Conv };
            Append(m);
        }
        Changed(m, false); pumpSignal.Set();
    }

    // Text and files go out on separate threads per peer, so a big file never holds up the chat.
    void PumpLoop() {
        while (!stopping) {
            pumpSignal.WaitOne(1000);
            if (stopping) return;
            lock (L) {
                foreach (Peer p in Cfg.Peers) {
                    if (!p.Online || DateTime.UtcNow < p.RetryAfter) continue;
                    foreach (string kind in new string[] { "text", "file" }) {
                        string key = p.Id + kind;
                        if (busy.Contains(key) || NextFor(p.Id, kind) == null) continue;
                        busy.Add(key);
                        Peer peer = p; string k = kind;
                        Run(delegate { try { Drain(peer, k); } finally { lock (L) busy.Remove(peer.Id + k); } });
                    }
                }
            }
        }
    }
    ChatMsg NextFor(string peerId, string kind) { // caller holds L
        ChatMsg best = null;
        foreach (List<ChatMsg> l in history.Values) foreach (ChatMsg m in l)
            if (m.Kind == kind && m.Pending.Contains(peerId) && (best == null || m.Time < best.Time)) best = m;
        return best;
    }
    void Drain(Peer p, string kind) {
        while (!stopping) {
            ChatMsg m; string ip; int port; byte[] key;
            lock (L) { m = NextFor(p.Id, kind); ip = p.Ip; port = p.Port; key = master; if (m != null) m.State = "sending"; }
            if (m == null) return;
            Msg error = null; bool permanent = false;
            try {
                Changed(m, false);
                using (Channel ch = Channel.Connect(ip, port, key)) {
                    if (kind == "text") SendTextTo(ch, p, m); else SendFileTo(ch, p, m);
                }
            } catch (PermanentException e) { error = e.M; permanent = true; }
            catch (Exception e) { error = Msg.Of(e); }
            lock (L) {
                if (m.Cancelled) { m.State = "cancel"; m.Pending.Clear(); }
                else if (error == null || permanent) {
                    m.Pending.Remove(p.Id);
                    if (permanent) m.Err(new Msg(p.Name + "：" + error.Zh, p.Name + ": " + error.En));
                    m.State = m.Pending.Count > 0 ? "queued" : (m.Error != null ? "fail" : "ok");
                } else { m.State = "queued"; m.Done = 0; p.RetryAfter = DateTime.UtcNow.AddSeconds(5); } // try again later
                if (error == null && !m.Cancelled) p.LastSeen = DateTime.UtcNow;
                Append(m);
            }
            Changed(m, false);
            if (error != null && !permanent && !m.Cancelled) return;
        }
    }
    Dictionary<string, object> Header(string type, Peer p, ChatMsg m) {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["t"] = type; d["id"] = m.Id; d["from"] = Cfg.Id; d["name"] = Cfg.Name; d["port"] = Cfg.Port; d["to"] = p.Id;
        d["conv"] = IsGroup(m.Conv) ? m.Conv : ""; d["agent"] = m.Agent ?? ""; d["time"] = m.Time; d["text"] = m.Text;
        return d;
    }
    static void Expect(Dictionary<string, object> r) {
        if (!Util.Flag(r, "ok")) throw new PermanentException(Msg.Wire(r, "err") ?? Lang.M("对方拒绝接收", "the other PC refused it"));
    }
    void SendTextTo(Channel ch, Peer p, ChatMsg m) { ch.SendJson(Header("text", p, m)); Expect(ch.ReceiveJson()); }

    void SendFileTo(Channel ch, Peer p, ChatMsg m) {
        List<string> full = new List<string>();
        List<Dictionary<string, object>> files = new List<Dictionary<string, object>>();
        long total = 0;
        try {
            if (File.Exists(m.Path)) AddFile(m.Path, m.Text, full, files, ref total);
            else if (Directory.Exists(m.Path)) Walk(m.Path, m.Text, full, files, ref total);
            else throw new PermanentException(Lang.M("原文件已不存在", "the original file is gone"));
        } catch (IOException e) { throw new PermanentException(Msg.Of(e)); }
        catch (UnauthorizedAccessException e) { throw new PermanentException(Msg.Of(e)); }
        Dictionary<string, object> h = Header("file", p, m);
        h["total"] = total; h["files"] = files; h["folder"] = Directory.Exists(m.Path);
        m.Size = total; m.Done = 0;
        ch.Timeout = 60000;
        ch.SendJson(h);
        Dictionary<string, object> r = ch.ReceiveJson();
        Expect(r);
        if (Util.Flag(r, "have")) return;
        byte[] buf = new byte[Chunk];
        DateTime tick = DateTime.UtcNow;
        for (int i = 0; i < full.Count; i++) {
            long left = Convert.ToInt64(files[i]["s"]);
            FileStream fs;
            try { fs = new FileStream(full[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan); }
            catch (Exception e) { throw new PermanentException(Msg.Of(e)); }
            using (fs) {
                while (left > 0) {
                    if (m.Cancelled || stopping) throw new IOException(Lang.T("已取消", "cancelled"));
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new PermanentException(Lang.M("发送过程中文件被修改", "the file changed while it was being sent"));
                    ch.Send(buf, n);
                    left -= n; m.Done += n;
                    if ((DateTime.UtcNow - tick).TotalMilliseconds > 150) { tick = DateTime.UtcNow; Changed(m, false); }
                }
            }
        }
        Expect(ch.ReceiveJson());
    }
    static void AddFile(string path, string rel, List<string> full, List<Dictionary<string, object>> files, ref long total) {
        if (full.Count >= MaxFiles) throw new PermanentException(Lang.M("文件太多（超过 {0} 个），请先打包成 zip", "too many files (over {0}), zip them first", MaxFiles));
        long s = new FileInfo(path).Length;
        Dictionary<string, object> d = new Dictionary<string, object>(); d["p"] = rel; d["s"] = s;
        full.Add(path); files.Add(d); total += s;
    }
    static void Walk(string path, string rel, List<string> full, List<Dictionary<string, object>> files, ref long total) {
        foreach (string f in Directory.GetFiles(path)) AddFile(f, rel + "/" + Path.GetFileName(f), full, files, ref total);
        foreach (string d in Directory.GetDirectories(path)) {
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; // junctions could loop
            Walk(d, rel + "/" + Path.GetFileName(d), full, files, ref total);
        }
    }

    // ---- receiving ----
    void AcceptLoop() {
        while (!stopping) {
            TcpClient c;
            try { c = tcp.AcceptTcpClient(); } catch (Exception) { if (stopping) return; Thread.Sleep(100); continue; }
            if (!slots.WaitOne(0)) { c.Close(); continue; }
            TcpClient client = c;
            Run(delegate {
                try { byte[] key; lock (L) key = master; using (Channel ch = Channel.Accept(client, key)) Serve(ch); }
                catch (Exception) { } // wrong pairing code, port scan, dropped connection
                finally { client.Close(); slots.Release(); }
            });
        }
    }
    static void Reply(Channel ch, bool ok, Msg err) { ch.SendJson(new { ok = ok, err = err == null ? "" : err.Zh, err_en = err == null ? "" : err.En }); }
    static readonly Msg OldVersion = Lang.M("版本不一致", "version mismatch"), BadList = Lang.M("文件列表无效", "invalid file list"); // the first one is also what older versions send

    void Serve(Channel ch) {
        Dictionary<string, object> h = ch.ReceiveJson(); // authenticated from here on
        string from = Util.Str(h, "from"), id = Util.Str(h, "id"), type = Util.Str(h, "t");
        if (Util.Str(h, "to") != Cfg.Id) { Reply(ch, false, Lang.M("这条消息不是发给本机的", "this message is for another PC")); return; }
        if (!Util.IsId(from) || from == Cfg.Id || !Util.IsId(id)) return;
        Seen(from, Util.Str(h, "name"), ch.RemoteIp, (int)Util.Num(h, "port"), false);

        if (type == "key") { ch.SendJson(new { ok = true, key = PublicKey }); return; }
        if (type == "control") { ServeControl(ch, from); return; }

        ChatMsg m = new ChatMsg();
        m.Id = id; m.Conv = IsGroup(Util.Str(h, "conv")) ? Util.Str(h, "conv") : from; m.Agent = Util.Cut(Util.Str(h, "agent"), 20); m.From = from; m.FromName = Util.Cut(Util.Str(h, "name"), 40);
        m.Time = Util.Num(h, "time"); m.Text = Util.Cut(Util.Str(h, "text"), MaxText);
        bool have;
        lock (L) have = ListOf(m.Conv).Exists(delegate (ChatMsg x) { return x.Id == id && x.State == "ok"; });
        if (type == "text") {
            m.Kind = "text"; m.State = "ok";
            if (!have) Add(m); // a resend after a lost reply must not show up twice
            Reply(ch, true, null);
        } else if (type == "file") {
            if (have) { ch.SendJson(new { ok = true, have = true }); return; }
            ReceiveFile(ch, h, m);
        } else Reply(ch, false, OldVersion);
    }

    void ReceiveFile(Channel ch, Dictionary<string, object> h, ChatMsg m) {
        m.Kind = "file"; m.State = "recv"; m.Size = Util.Num(h, "total");
        m.Text = Util.SafeRelative(m.Text).Replace("\\", "_");
        string stage = Path.Combine(Cfg.SaveDir, ".recv-" + m.Id);
        bool folder = Util.Flag(h, "folder");
        List<string> paths = new List<string>(); List<long> sizes = new List<long>();
        long sum = 0;
        IEnumerable list = h.ContainsKey("files") ? h["files"] as IEnumerable : null;
        if (list == null) { Reply(ch, false, BadList); return; }
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object o in list) {
            Dictionary<string, object> f = o as Dictionary<string, object>;
            if (f == null) { Reply(ch, false, BadList); return; }
            string rel = Util.SafeRelative(Util.Str(f, "p"));
            rel = folder ? Util.UniqueRel(rel, seen) : m.Text;
            long s = Util.Num(f, "s");
            if (s < 0) { Reply(ch, false, BadList); return; }
            paths.Add(rel); sizes.Add(s); sum += s;
        }
        if (sum != m.Size || paths.Count > MaxFiles || (!folder && paths.Count != 1)) { Reply(ch, false, BadList); return; }
        try {
            Directory.CreateDirectory(Cfg.SaveDir);
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Cfg.SaveDir))).AvailableFreeSpace < sum + 50L * 1024 * 1024) { Reply(ch, false, Lang.M("对方磁盘空间不足", "not enough disk space on the other PC")); return; }
        } catch (ArgumentException) { } // network share: no way to check
        catch (Exception e) { Reply(ch, false, Lang.M("对方无法保存：{0}", "the other PC can't save it: {0}", e.Message)); return; }

        lock (L) { if (!busy.Add("recv" + m.Id)) { Reply(ch, false, Lang.M("正在接收", "already receiving it")); return; } }
        try {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(Path.Combine(stage, m.Text));
            string root = folder ? stage : Path.Combine(stage, m.Text);
            Add(m);
            Reply(ch, true, null);
            ch.Timeout = 60000;
            DateTime tick = DateTime.UtcNow;
            for (int i = 0; i < paths.Count; i++) {
                string dest = Path.GetFullPath(Path.Combine(root, paths[i]));
                if (!dest.StartsWith(Path.GetFullPath(root) + "\\", StringComparison.OrdinalIgnoreCase)) throw Lang.Err("非法路径", "illegal path");
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (FileStream fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536)) {
                    long left = sizes[i];
                    while (left > 0) {
                        byte[] d = ch.Receive();
                        if (d.Length == 0 || d.Length > left) throw Lang.Err("数据长度错误", "wrong data length");
                        fs.Write(d, 0, d.Length);
                        left -= d.Length; m.Done += d.Length;
                        if ((DateTime.UtcNow - tick).TotalMilliseconds > 150) { tick = DateTime.UtcNow; Changed(m, false); }
                    }
                }
            }
            string final;
            lock (L) { // two transfers of the same name must not pick the same free name
                final = Util.UniquePath(Path.Combine(Cfg.SaveDir, m.Text));
                if (folder) Directory.Move(Path.Combine(stage, m.Text), final);
                else File.Move(Path.Combine(root, m.Text), final);
                m.Path = final; m.State = "ok"; m.Done = m.Size; m.Seq = NextSeq();
            }
            Changed(m, true);
            Reply(ch, true, null);
        } catch (Exception e) {
            if (m.State != "ok") {
                m.State = "fail"; m.Err(e is EndOfStreamException || e.InnerException is SocketException ? Lang.M("传输中断", "transfer interrupted") : Msg.Of(e));
                Changed(m, true);
                if (!(e is EndOfStreamException)) try { Reply(ch, false, Lang.M("对方保存失败：{0}", "the other PC couldn't save it: {0}", e.Message)); } catch (Exception) { }
            }
        } finally {
            lock (L) busy.Remove("recv" + m.Id);
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
}
