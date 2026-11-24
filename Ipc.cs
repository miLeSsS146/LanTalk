// Local bridge between the running LanTalk app and lt.exe (the command line Claude Code uses).
// A named pipe only the current Windows user can open; one JSON request, one JSON reply.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace LanTalk {

public static class Ipc {
    // Everything lives in D:\LanTalk; only a PC without a D: hard disk falls back to the user profile.
    static bool HasD { get { DriveInfo d = new DriveInfo("D"); return d.DriveType == DriveType.Fixed && d.IsReady; } }
    public static string DefaultData { get { return HasD ? "D:\\LanTalk\\Data" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanTalk"); } }
    public static string DefaultSave { get { return HasD ? "D:\\LanTalk\\Files" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads\\LanTalk"); } }
    public static string PipeName(string dataDir) {
        byte[] key = Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User.Value);
        byte[] h = Crypto.Hmac(key, Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\').ToUpperInvariant()));
        return "LanTalk-" + BitConverter.ToString(h, 0, 8).Replace("-", "");
    }
    public static string ReadAll(Stream s) {
        byte[] n = new byte[4]; Fill(s, n);
        int len = BitConverter.ToInt32(n, 0);
        if (len < 0 || len > 16 * 1024 * 1024) throw new IOException("bad message");
        byte[] b = new byte[len]; Fill(s, b);
        return Encoding.UTF8.GetString(b);
    }
    static void Fill(Stream s, byte[] b) {
        int got = 0;
        while (got < b.Length) { int r = s.Read(b, got, b.Length - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
    }
    public static void WriteAll(Stream s, string text) {
        byte[] b = Encoding.UTF8.GetBytes(text);
        s.Write(BitConverter.GetBytes(b.Length), 0, 4); s.Write(b, 0, b.Length); s.Flush();
    }
}

public class IpcServer {
    readonly Node node;
    readonly string dataDir, cursorFile;
    readonly object cursorLock = new object();
    public IpcServer(Node n, string data) { node = n; dataDir = data; cursorFile = Path.Combine(data, "cursors.json"); }

    public void Start() { for (int i = 0; i < 2; i++) { Thread t = new Thread(Loop); t.IsBackground = true; t.Start(); } }

    void Loop() {
        PipeSecurity sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (true) {
            NamedPipeServerStream p = null;
            try {
                p = new NamedPipeServerStream(Ipc.PipeName(dataDir), PipeDirection.InOut, 64, PipeTransmissionMode.Byte, PipeOptions.None, 65536, 65536, sec);
                p.WaitForConnection();
            } catch (Exception) { if (p != null) p.Dispose(); Thread.Sleep(200); continue; }
            NamedPipeServerStream client = p;
            Thread t = new Thread(delegate () { // own thread: a remote command may run for hours
                try { using (client) Serve(client); } catch (Exception) { } // client went away
            });
            t.IsBackground = true; t.Start();
        }
    }
    void Serve(NamedPipeServerStream p) {
        string reply;
        try {
            Dictionary<string, object> q = Util.FromJson<Dictionary<string, object>>(Ipc.ReadAll(p));
            string cmd = Util.Str(q, "cmd");
            if (cmd == "run" || cmd == "put" || cmd == "get") {
                string to = Target(Util.Str(q, "to"));
                if (Node.IsGroup(to)) throw Lang.Err("要用 --to 指明一台电脑", "name one PC with --to");
                Dictionary<string, object> req = new Dictionary<string, object>(); req["op"] = cmd;
                foreach (string k in new string[] { "cmd", "cwd", "path", "timeout", "force" }) if (q.ContainsKey("r_" + k)) req[k] = q["r_" + k];
                node.Control(to, req, Agent(q), Util.Str(q, "local"), delegate (object o) { Ipc.WriteAll(p, Util.ToJson(o)); });
                p.WaitForPipeDrain();
                return;
            }
            reply = Util.ToJson(Handle(q));
        } catch (InvalidOperationException e) { reply = Util.ToJson(new { ok = false, err = e.Message }); }
        catch (ArgumentException e) { reply = Util.ToJson(new { ok = false, err = e.Message }); }
        Ipc.WriteAll(p, reply);
        p.WaitForPipeDrain();
    }

    object Handle(Dictionary<string, object> q) {
        switch (Util.Str(q, "cmd")) {
            case "list":
                return new { ok = true, me = node.Cfg.Name, id = node.Cfg.Id, saveDir = node.Cfg.SaveDir,
                    peers = node.Peers().Select(delegate (Peer p) { return new { name = p.Name, id = p.Id, ip = p.Ip, online = p.Online }; }).ToArray() };
            case "send": return Result(node.SendText(Target(Util.Str(q, "to")), Util.Str(q, "text"), Agent(q)));
            case "file": return Result(node.SendPath(Target(Util.Str(q, "to")), Util.Str(q, "path"), Agent(q)));
            case "status":
                ChatMsg s = node.Get(Util.Str(q, "id"));
                if (s == null) throw Lang.Err("没有这条消息", "no such message");
                return Result(s);
            case "read":
                int n = (int)Util.Num(q, "n");
                return new { ok = true, more = 0, messages = node.History(Target(Util.Str(q, "to")), n <= 0 ? 20 : Math.Min(n, 500)).Select(View).ToArray() };
            case "inbox": return Inbox(Util.Str(q, "reader"), Util.Flag(q, "peek"));
        }
        throw Lang.Err("未知命令", "unknown command");
    }
    static string Agent(Dictionary<string, object> q) { string a = Util.Str(q, "agent").Trim(); return a.Length == 0 ? "Claude" : Util.Cut(a, 20); }

    string Target(string to) {
        if (to == "" || to == Node.Ai) return Node.Ai;
        if (to == Node.All) return Node.All;
        List<Peer> hit = node.Peers().Where(delegate (Peer p) { return p.Id == to || string.Equals(p.Name, to, StringComparison.OrdinalIgnoreCase); }).ToList();
        if (hit.Count == 0) throw Lang.Err("没有叫“{0}”的电脑，用 lt list 查看", "no PC called \"{0}\", see lt list", to);
        if (hit.Count > 1) throw Lang.Err("有几台电脑都叫“{0}”，请改用 lt list 里的 id", "several PCs are called \"{0}\", use the id from lt list", to);
        return hit[0].Id;
    }
    object Result(ChatMsg m) {
        string[] waiting;
        lock (node.L) waiting = m.Pending.Select(delegate (string id) { Peer p = node.Find(id); return p == null ? id : p.Name; }).ToArray();
        return new { ok = true, id = m.Id, state = m.State, waiting = waiting, error = m.ErrText };
    }
    object View(ChatMsg m) {
        return new { id = m.Id, time = m.LocalTime.ToString("yyyy-MM-dd HH:mm:ss"), from = m.FromName, agent = m.Agent ?? "", mine = m.From == node.Cfg.Id,
            channel = Node.IsGroup(m.Conv) ? m.Conv : "direct", kind = m.Kind, text = m.Text, path = m.Kind == "file" ? m.Path ?? "" : "", state = m.State };
    }
    object Inbox(string reader, bool peek) {
        if (reader == "") reader = "claude";
        lock (cursorLock) {
            Dictionary<string, object> c = new Dictionary<string, object>();
            if (File.Exists(cursorFile)) try { c = Util.FromJson<Dictionary<string, object>>(File.ReadAllText(cursorFile, Encoding.UTF8)); } catch (ArgumentException) { }
            List<ChatMsg> all = node.Inbox(Util.Num(c, reader));
            List<ChatMsg> page = all.Take(50).ToList();
            if (!peek && page.Count > 0) { c[reader] = page[page.Count - 1].Seq; File.WriteAllText(cursorFile, Util.ToJson(c), Encoding.UTF8); }
            return new { ok = true, more = all.Count - page.Count, messages = page.Select(View).ToArray() };
        }
    }
}
}
