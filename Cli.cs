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
    const string Usage =
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
        "选项：--json 输出 JSON；--ascii 非 ASCII 字符转成 \\uXXXX；--as 名字 读取/发送身份（默认 claude）";

    static string data = Ipc.DefaultData;
    static bool json, ascii;
    static TextWriter Out;

    static int Main(string[] args) {
        Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        try { return Go(args); }
        catch (TimeoutException) { Print("错误：LanTalk 没有运行，请先启动 LanTalk.exe"); return 2; }
        catch (InvalidOperationException e) { Print("错误：" + e.Message); return 1; }
        catch (UnauthorizedAccessException) { Print("错误：没有权限连接 LanTalk。请在运行 LanTalk 的那个 Windows 账户的桌面会话里用，不能通过 SSH 远程调用"); return 2; }
        catch (IOException e) { Print("错误：" + e.Message); return 2; }
        finally { Out.Flush(); }
    }
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
                if (++i >= args.Length) throw new InvalidOperationException(a + " 后面缺少内容");
                if (a == "--to") to = args[i];
                else if (a == "--as") who = args[i];
                else if (a == "--cwd") cwd = args[i];
                else if (a == "--data") data = args[i];
                else { int v; if (!int.TryParse(args[i], out v) || v < 0) throw new InvalidOperationException(a + " 后面要是数字"); if (a == "-n") n = v; else timeout = v; }
            }
            else if (!literal && a.StartsWith("--")) throw new InvalidOperationException("未知选项 " + a);
            else if (rest == null) rest = a;
            else if (rest2 == null && (args[0] == "put" || args[0] == "get")) rest2 = a;
            else throw new InvalidOperationException("参数太多，文字请用引号括起来");
        }
        Dictionary<string, object> q = new Dictionary<string, object>();
        q["cmd"] = args[0]; q["to"] = to; q["agent"] = who; q["reader"] = who.ToLowerInvariant(); q["peek"] = peek; q["n"] = n;
        switch (args[0]) {
            case "list": {
                Dictionary<string, object> r = Ask(q);
                if (json) { Print(Util.ToJson(r)); return 0; }
                Print("本机：" + Util.Str(r, "me") + "    收到的文件存到：" + Util.Str(r, "saveDir"));
                foreach (Dictionary<string, object> p in Items(r, "peers")) Print((Util.Flag(p, "online") ? "[在线] " : "[离线] ") + Util.Str(p, "name") + "  " + Util.Str(p, "ip") + "  id=" + Util.Str(p, "id"));
                return 0;
            }
            case "send":
                if (rest == null) throw new InvalidOperationException("缺少消息内容");
                if (rest == "-") rest = StdIn();
                q["text"] = rest;
                return Deliver(q, timeout < 0 ? 5 : timeout);
            case "file":
                if (rest == null) throw new InvalidOperationException("缺少路径");
                q["path"] = Path.GetFullPath(rest);
                return Deliver(q, timeout < 0 ? 600 : timeout);
            case "status":
                q["id"] = rest ?? "";
                return Report(Ask(q));
            case "run":
                if (rest == null) throw new InvalidOperationException("缺少命令");
                if (rest == "-") rest = StdIn();
                q["r_cmd"] = rest; q["r_cwd"] = cwd; q["r_timeout"] = timeout < 0 ? 120 : timeout;
                return Stream(q);
            case "put": case "get":
                if (rest2 == null) throw new InvalidOperationException("要写两个路径：从哪里、到哪里");
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
                    if (DateTime.UtcNow >= end) { Print(json ? "{\"ok\":true,\"more\":0,\"messages\":[]}" : "（等待超时，没有新消息）"); return 3; }
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
        if (Util.Str(q, "to") == "") throw new InvalidOperationException("要用 --to 指明一台电脑");
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
                    else { Out.Flush(); err.Write("错误：" + Util.Str(r, "err") + "\n"); err.Flush(); }
                    return 125;
                }
                if (k == "o" && json) so.Append(Util.Str(r, "d"));
                else if (k == "e" && json) se.Append(Util.Str(r, "d"));
                else if (k == "o") { Out.Write(Esc(Util.Str(r, "d"))); Out.Flush(); }
                else if (k == "e") { err.Write(Esc(Util.Str(r, "d"))); err.Flush(); }
                else if (k == "wait" && !json) { err.Write("（等那台电脑上的人确认，最多 60 秒…）\n"); err.Flush(); }
                else if (k == "x") {
                    int code = (int)Util.Num(r, "code");
                    if (json) Print(Util.ToJson(new { ok = true, code = code, stdout = so.ToString(), stderr = se.ToString() }));
                    else if (Util.Str(q, "cmd") != "run") Print("完成");
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
            string t = st == "ok" ? "已送达" : st == "fail" ? "失败：" + Util.Str(r, "error") : st == "cancel" ? "已取消" : "未送达，等待：" + string.Join("、", w.ToArray());
            Print(t + "  id=" + Util.Str(r, "id"));
        }
        return st == "ok" ? 0 : st == "fail" || st == "cancel" ? 1 : 4;
    }
    static int Show(Dictionary<string, object> r) {
        if (json) { Print(Util.ToJson(r)); return 0; }
        List<Dictionary<string, object>> l = Items(r, "messages");
        if (l.Count == 0) Print("（没有消息）");
        foreach (Dictionary<string, object> m in l) {
            string agent = Util.Str(m, "agent"), ch = Util.Str(m, "channel");
            Print("[" + Util.Str(m, "time") + "] " + (agent != "" ? agent + "@" : "用户@") + Util.Str(m, "from") + (Util.Flag(m, "mine") ? "（本机）" : "")
                + " #" + ch + "  " + (Util.Str(m, "kind") == "file" ? "文件：" + Util.Str(m, "text") + "  状态=" + Util.Str(m, "state") + "  路径=" + Util.Str(m, "path") : ""));
            if (Util.Str(m, "kind") != "file") Print("    " + Util.Str(m, "text").Replace("\r", "").Replace("\n", "\n    "));
        }
        if (Util.Num(r, "more") > 0) Print("（还有 " + Util.Num(r, "more") + " 条，再运行一次 lt inbox）");
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
