// lt.exe: command line for Claude Code. Talks to the LanTalk app running on this PC.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace LanTalk {

static class Cli {
    static string Usage { get { return Lang.T(
        "lt list                                  本机和其他电脑、是否在线\n" +
        "lt send [--to 电脑名|ai|all] <文字|->     发消息；默认发到 ai（Claude 协作频道），- 表示从标准输入读\n" +
        "lt file [--to 电脑名|ai|all] <路径>       发文件或文件夹，等到送达为止\n" +
        "lt inbox [--peek]                        取未读消息（--peek 不标记已读）\n" +
        "lt wait [--timeout 秒]                   等到有新消息再返回，默认 300 秒，超时退出码 3\n" +
        "lt read [--to 电脑名|ai|all] [-n 条数]     看聊天记录\n" +
        "lt status <消息id>                       查发送状态\n" +
        "远程控制（要先由人在那台电脑的 LanTalk 里给本机“控制权限”）：\n" +
        "lt run --to 电脑名 [--cwd 文件夹] [--timeout 秒] <PowerShell 命令|->   在那台电脑上运行，默认 120 秒\n" +
        "lt put --to 电脑名 [--force] <本机文件> <远程完整路径>   写文件过去\n" +
        "lt get --to 电脑名 [--force] <远程完整路径> <本机文件>   取文件回来\n" +
        "    退出码：run 是远程命令的退出码；124 远程超时；125 没能执行（没授权、被拒绝、断线等）\n" +
        "选项：--json 输出 JSON；--ascii 非 ASCII 字符转成 \\uXXXX；--as 名字 读取/发送身份（默认 claude）",
        "lt list                              this PC, the other PCs, who is online\n" +
        "lt send [--to PC|ai|all] <text|->    send a message; goes to ai (the Claude channel) by default, - reads stdin\n" +
        "lt file [--to PC|ai|all] <path>      send a file or folder and wait until it has arrived\n" +
        "lt inbox [--peek]                    unread messages (--peek leaves them unread)\n" +
        "lt wait [--timeout SECONDS]          return when a message arrives, 300 seconds by default, exit code 3 on timeout\n" +
        "lt read [--to PC|ai|all] [-n COUNT]  chat history\n" +
        "lt status <message id>               delivery state\n" +
        "Remote control (someone at that PC must first give this PC \"Control access\" in LanTalk):\n" +
        "lt run --to PC [--cwd FOLDER] [--timeout SECONDS] <PowerShell command|->   run it there, 120 seconds by default\n" +
        "lt put --to PC [--force] <local file> <complete remote path>   write a file there\n" +
        "lt get --to PC [--force] <complete remote path> <local file>   fetch a file\n" +
        "    Exit codes: run returns the remote exit code; 124 timed out there; 125 couldn't run (no access, declined, disconnected)\n" +
        "Options: --json prints JSON; --ascii turns non-ASCII into \\uXXXX; --as NAME who reads/sends (default claude)"); } }

    static string data = Ipc.DefaultData;
    static bool json, ascii;
    static TextWriter Out;

    static int Main(string[] args) {
        Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--data") Lang.Load(args[i + 1]);
        if (Array.IndexOf(args, "--data") < 0) Lang.Load(data);
        try { return Go(args); }
        catch (TimeoutException) { Print(Fail(Lang.T("LanTalk 没有运行，请先启动 LanTalk.exe", "LanTalk isn't running, start LanTalk.exe first"))); return 2; }
        catch (InvalidOperationException e) { Print(Fail(e.Message)); return 1; }
        catch (UnauthorizedAccessException) { Print(Fail(Lang.T("没有权限连接 LanTalk。请在运行 LanTalk 的那个 Windows 账户的桌面会话里用，不能通过 SSH 远程调用", "not allowed to connect to LanTalk. Use lt in the desktop session of the Windows account that runs LanTalk, not over SSH"))); return 2; }
        catch (IOException e) { Print(Fail(e.Message)); return 2; }
        finally { Out.Flush(); }
    }
    static string Fail(string s) { return Lang.T("错误：", "Error: ") + s; }
    static int Go(string[] args) {
        string to = "", who = "", cwd = "", rest = null, rest2 = null; int timeout = -1, n = 20; bool peek = false, force = false, literal = false;
        if (args.Length == 0) { Print(Usage); return 1; }
        for (int i = 1; i < args.Length; i++) {
            string a = args[i];
            if (!literal && a == "--") literal = true;
            else if (!literal && a == "--json") json = true;
            else if (!literal && a == "--ascii") ascii = true;
            else if (!literal && a == "--peek") peek = true;
            else if (!literal && a == "--force") force = true;
            else if (!literal && (a == "--to" || a == "--as" || a == "--cwd" || a == "--timeout" || a == "-n" || a == "--data")) {
                if (++i >= args.Length) throw Lang.Err("{0} 后面缺少内容", "{0} needs a value", a);
                if (a == "--to") to = args[i];
                else if (a == "--as") who = args[i];
                else if (a == "--cwd") cwd = args[i];
                else if (a == "--data") data = args[i];
                else { int v; if (!int.TryParse(args[i], out v) || v < 0) throw Lang.Err("{0} 后面要是数字", "{0} needs a number", a); if (a == "-n") n = v; else timeout = v; }
            }
            else if (!literal && a.StartsWith("--")) throw Lang.Err("未知选项 {0}", "unknown option {0}", a);
            else if (rest == null) rest = a;
            else if (rest2 == null && (args[0] == "put" || args[0] == "get")) rest2 = a;
            else throw Lang.Err("参数太多，文字请用引号括起来", "too many arguments, put the text in quotes");
        }
        Dictionary<string, object> q = new Dictionary<string, object>();
        q["cmd"] = args[0]; q["to"] = to; q["agent"] = who; q["reader"] = who.ToLowerInvariant(); q["peek"] = peek; q["n"] = n;
        switch (args[0]) {
            case "list": {
                Dictionary<string, object> r = Ask(q);
                if (json) { Print(Util.ToJson(r)); return 0; }
                Print(Lang.T("本机：{0}    收到的文件存到：{1}", "This PC: {0}    Received files go to: {1}", Util.Str(r, "me"), Util.Str(r, "saveDir")));
                foreach (Dictionary<string, object> p in Items(r, "peers")) Print((Util.Flag(p, "online") ? Lang.T("[在线] ", "[online]  ") : Lang.T("[离线] ", "[offline] ")) + Util.Str(p, "name") + "  " + Util.Str(p, "ip") + "  id=" + Util.Str(p, "id"));
                return 0;
            }
            case "send":
                if (rest == null) throw Lang.Err("缺少消息内容", "the message is missing");
                if (rest == "-") rest = StdIn();
                q["text"] = rest;
                return Deliver(q, timeout < 0 ? 5 : timeout);
            case "file":
                if (rest == null) throw Lang.Err("缺少路径", "the path is missing");
                q["path"] = Path.GetFullPath(rest);
                return Deliver(q, timeout < 0 ? 600 : timeout);
            case "status":
                q["id"] = rest ?? "";
                return Report(Ask(q));
            case "run":
                if (rest == null) throw Lang.Err("缺少命令", "the command is missing");
                if (rest == "-") rest = StdIn();
                q["r_cmd"] = rest; q["r_cwd"] = cwd; q["r_timeout"] = timeout < 0 ? 120 : timeout;
                return Stream(q);
            case "put": case "get":
                if (rest2 == null) throw Lang.Err("要写两个路径：从哪里、到哪里", "two paths are needed: from where, to where");
                q["r_path"] = args[0] == "put" ? rest2 : rest; q["r_force"] = force;
                q["local"] = Path.GetFullPath(args[0] == "put" ? rest : rest2);
                return Stream(q);
            case "read": return Show(Ask(q));
            case "inbox": return Show(Ask(q));
            case "wait": {
                q["cmd"] = "inbox";
                DateTime end = DateTime.UtcNow.AddSeconds(timeout < 0 ? 300 : timeout);
                while (true) {
                    Dictionary<string, object> r = Ask(q);
                    if (Items(r, "messages").Count > 0) return Show(r);
                    if (DateTime.UtcNow >= end) { Print(json ? "{\"ok\":true,\"more\":0,\"messages\":[]}" : Lang.T("（等待超时，没有新消息）", "(Timed out, no new messages)")); return 3; }
                    Thread.Sleep(1000);
                }
            }
        }
        Print(Usage);
        return 1;
    }

    static string StdIn() { using (StreamReader sr = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) return sr.ReadToEnd().TrimEnd('\r', '\n'); }
    // Remote control: prints what the other PC sends as it comes. If this process is killed the pipe breaks, and that stops the remote command.
    static int Stream(Dictionary<string, object> q) {
        if (Util.Str(q, "to") == "") throw Lang.Err("要用 --to 指明一台电脑", "name one PC with --to");
        TextWriter err = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
        StringBuilder so = new StringBuilder(), se = new StringBuilder();
        using (NamedPipeClientStream p = new NamedPipeClientStream(".", Ipc.PipeName(data), PipeDirection.InOut)) {
            p.Connect(3000);
            Ipc.WriteAll(p, Util.ToJson(q));
            while (true) {
                Dictionary<string, object> r = Util.FromJson<Dictionary<string, object>>(Ipc.ReadAll(p));
                string k = Util.Str(r, "k");
                if (!Util.Flag(r, "ok")) {
                    if (json) Print(Util.ToJson(new { ok = false, err = Util.Str(r, "err"), stdout = so.ToString(), stderr = se.ToString() }));
                    else { Out.Flush(); err.Write(Fail(Msg.Wire(r, "err")) + "\n"); err.Flush(); }
                    return 125;
                }
                string d = Msg.Wire(r, "d");
                if (k == "o" && json) so.Append(d);
                else if (k == "e" && json) se.Append(d);
                else if (k == "o") { Out.Write(Esc(d)); Out.Flush(); }
                else if (k == "e") { err.Write(Esc(d)); err.Flush(); }
                else if (k == "wait" && !json) { err.Write(Lang.T("（等那台电脑上的人确认，最多 60 秒…）", "(Waiting for someone at that PC to approve, up to 60 seconds...)") + "\n"); err.Flush(); }
                else if (k == "x") {
                    int code = (int)Util.Num(r, "code");
                    if (json) Print(Util.ToJson(new { ok = true, code = code, stdout = so.ToString(), stderr = se.ToString() }));
                    else if (Util.Str(q, "cmd") != "run") Print(Lang.T("完成", "Done"));
                    return code;
                }
            }
        }
    }

    // Sends, then waits until everyone who is online has it. Exit code 0 delivered, 4 queued for an offline PC, 1 failed.
    static int Deliver(Dictionary<string, object> q, int seconds) {
        Dictionary<string, object> r = Ask(q);
        Dictionary<string, object> s = new Dictionary<string, object>(); s["cmd"] = "status"; s["id"] = Util.Str(r, "id");
        DateTime end = DateTime.UtcNow.AddSeconds(seconds), quiet = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end) {
            string st = Util.Str(r, "state");
            if (st == "ok" || st == "fail" || st == "cancel") break;
            if (st == "queued" && DateTime.UtcNow > quiet && !AnyOnline(r)) break;
            Thread.Sleep(200);
            r = Ask(s);
        }
        return Report(r);
    }
    static bool AnyOnline(Dictionary<string, object> r) {
        Dictionary<string, object> q = new Dictionary<string, object>(); q["cmd"] = "list";
        HashSet<string> online = new HashSet<string>();
        foreach (Dictionary<string, object> p in Items(Ask(q), "peers")) if (Util.Flag(p, "online")) online.Add(Util.Str(p, "name"));
        foreach (object o in (IEnumerable)r["waiting"]) if (online.Contains(o.ToString())) return true;
        return false;
    }
    static int Report(Dictionary<string, object> r) {
        string st = Util.Str(r, "state");
        if (json) Print(Util.ToJson(r));
        else {
            List<string> w = new List<string>();
            foreach (object o in (IEnumerable)r["waiting"]) w.Add(o.ToString());
            string t = st == "ok" ? Lang.T("已送达", "Delivered") : st == "fail" ? Lang.T("失败：", "Failed: ") + Util.Str(r, "error") : st == "cancel" ? Lang.T("已取消", "Cancelled") : Lang.T("未送达，等待：", "Not delivered yet, waiting for: ") + string.Join(Lang.T("、", ", "), w.ToArray());
            Print(t + "  id=" + Util.Str(r, "id"));
        }
        return st == "ok" ? 0 : st == "fail" || st == "cancel" ? 1 : 4;
    }
    static int Show(Dictionary<string, object> r) {
        if (json) { Print(Util.ToJson(r)); return 0; }
        List<Dictionary<string, object>> l = Items(r, "messages");
        if (l.Count == 0) Print(Lang.T("（没有消息）", "(No messages)"));
        foreach (Dictionary<string, object> m in l) {
            string agent = Util.Str(m, "agent"), ch = Util.Str(m, "channel");
            Print("[" + Util.Str(m, "time") + "] " + (agent != "" ? agent + "@" : Lang.T("用户@", "user@")) + Util.Str(m, "from") + (Util.Flag(m, "mine") ? Lang.T("（本机）", " (this PC)") : "")
                + " #" + ch + "  " + (Util.Str(m, "kind") == "file" ? Lang.T("文件：{0}  状态={1}  路径={2}", "file: {0}  state={1}  path={2}", Util.Str(m, "text"), Util.Str(m, "state"), Util.Str(m, "path")) : ""));
            if (Util.Str(m, "kind") != "file") Print("    " + Util.Str(m, "text").Replace("\r", "").Replace("\n", "\n    "));
        }
        if (Util.Num(r, "more") > 0) Print(Lang.T("（还有 {0} 条，再运行一次 lt inbox）", "({0} more, run lt inbox again)", Util.Num(r, "more")));
        return 0;
    }
    static List<Dictionary<string, object>> Items(Dictionary<string, object> r, string k) {
        List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
        if (r.ContainsKey(k)) foreach (object o in (IEnumerable)r[k]) l.Add((Dictionary<string, object>)o);
        return l;
    }
    static Dictionary<string, object> Ask(Dictionary<string, object> q) {
        using (NamedPipeClientStream p = new NamedPipeClientStream(".", Ipc.PipeName(data), PipeDirection.InOut)) {
            p.Connect(3000);
            Ipc.WriteAll(p, Util.ToJson(q));
            Dictionary<string, object> r = Util.FromJson<Dictionary<string, object>>(Ipc.ReadAll(p));
            if (!Util.Flag(r, "ok")) throw new InvalidOperationException(Util.Str(r, "err"));
            return r;
        }
    }
    static void Print(string s) {
        Out.Write(Esc(s) + "\n");
    }
    static string Esc(string s) {
        if (!ascii) return s;
        StringBuilder b = new StringBuilder();
        foreach (char c in s) if (c < 127) b.Append(c); else b.Append("\\u" + ((int)c).ToString("x4"));
        return b.ToString();
    }
}
}
