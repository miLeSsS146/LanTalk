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
        "远程控制（要先由人在那台电脑的 LanTalk 里给本机“控制权限”；都要用 --to 指明电脑，远程路径要写完整）：\n" +
        "lt run [--cwd 文件夹] [--timeout 秒] <PowerShell 命令|->   在那台电脑上运行，默认 120 秒\n" +
        "lt run --bg [--cwd 文件夹] [--timeout 秒] <命令|->   在后台运行，立刻返回任务号；默认最长 24 小时，断线也不停\n" +
        "lt run --session 名字 [--cwd 文件夹] <命令|->   在一个会话里运行，下次用同名会话时目录、变量、环境都还在\n" +
        "lt sessions / lt end <会话名>                看、关会话（2 小时不用自动关）\n" +
        "lt jobs                                   本机在那边的后台任务\n" +
        "lt log <任务号> [-n 行数] [--from 位置] [--follow [--timeout 秒]]   看任务输出，默认最后 100 行；--follow 等到它结束\n" +
        "lt stop <任务号>                           停掉后台任务\n" +
        "lt shot [--window 标题或程序名] [--screen 号] [--full] [图片.png]   截图，默认缩到 1568 像素；--list 列出窗口\n" +
        "lt input [--no-look] [--timeout 秒] <步骤|-> [图片.png]   用那边的鼠标键盘，一行一步，从标准输入可以有多步，做完自动截图：\n" +
        "        click 640 380        double / triple / right / middle 同样写法，后面可加 ctrl、shift、alt\n        drag 10 10 200 200   move 640 380   scroll 640 400 down 3\n        type 要输入的字      key ctrl+s     key down 5     wait 500     focus 窗口标题或程序名\n" +
        "    坐标是最近一张截图里的像素，所以先 lt shot；那边有人动鼠标键盘就让开（最多等 --timeout 秒，默认 60），没做完退出码 1；密码框不输入\n" +
        "lt cat [--from 行] [-n 行数] [--number] <远程文件>   看文本文件\n" +
        "lt edit [--all] <远程文件> -               改文本文件，从标准输入读，可以有多处：\n" +
        "        <<<<<<<\n        原文\n        =======\n        新文\n        >>>>>>>\n" +
        "    或 lt edit <远程文件> --old 本机文件 --new 本机文件\n" +
        "lt put [--force] <本机文件> <远程文件>       写文件过去\n" +
        "lt get [--force] <远程文件> <本机文件>       取文件回来\n" +
        "lt sync [--pull] [--dry] [--delete] [--force] [--skip 名字]... <来源文件夹> <目标文件夹>   只传有变化的文件\n" +
        "    默认从本机到那边，--pull 反过来；--dry 只比较；--delete 把来源里已删除的也删掉（进回收站）\n" +
        "    目标那边改过的文件不动，退出码 1，并说明是哪种情况；.git、node_modules、__pycache__ 默认跳过，--no-skip 不跳过\n" +
        "lt tunnel <端口> [--local 本机端口]          那边的网页服务在本机用 localhost 打开，命令结束就关\n" +
        "lt claude [--cwd 文件夹] [--mode plan|acceptEdits] [--resume 会话id] [--model 模型] [--wait] <任务|->   派活给那台的 Claude Code，返回任务号\n" +
        "lt update [--from 文件夹] [--force]          把本机的 LanTalk（或文件夹里的新版）装到那台；--self 装到本机\n" +
        "lt info                                   那台电脑的系统、硬件、磁盘、装了哪些工具\n" +
        "    退出码：run、log --follow 是远程命令的退出码；124 远程超时；125 没能执行（没授权、被拒绝、断线等）\n" +
        "lt mcp                                   作为 MCP 服务器运行（给 Claude Code 用）；lt version 看版本\n" +
        "选项：--json 输出 JSON；--ascii 非 ASCII 字符转成 \\uXXXX；--as 名字 读取/发送身份（默认 claude）",
        "lt list                              this PC, the other PCs, who is online\n" +
        "lt send [--to PC|ai|all] <text|->    send a message; goes to ai (the Claude channel) by default, - reads stdin\n" +
        "lt file [--to PC|ai|all] <path>      send a file or folder and wait until it has arrived\n" +
        "lt inbox [--peek]                    unread messages (--peek leaves them unread)\n" +
        "lt wait [--timeout SECONDS]          return when a message arrives, 300 seconds by default, exit code 3 on timeout\n" +
        "lt read [--to PC|ai|all] [-n COUNT]  chat history\n" +
        "lt status <message id>               delivery state\n" +
        "Remote control (someone at that PC must first give this PC \"Control access\" in LanTalk; all need --to PC, remote paths must be complete):\n" +
        "lt run [--cwd FOLDER] [--timeout SECONDS] <PowerShell command|->   run it there, 120 seconds by default\n" +
        "lt run --bg [--cwd FOLDER] [--timeout SECONDS] <command|->   run it in the background, returns a job number at once; up to 24 hours, survives a lost connection\n" +
        "lt run --session NAME [--cwd FOLDER] <command|->   run in a session: folder, variables and environment are still there next time\n" +
        "lt sessions / lt end <name>          list and close sessions (closed after 2 idle hours)\n" +
        "lt jobs                              background jobs of this PC over there\n" +
        "lt log <job> [-n LINES] [--from POSITION] [--follow [--timeout SECONDS]]   its output, the last 100 lines by default; --follow waits until it ends\n" +
        "lt stop <job>                        stop a background job\n" +
        "lt shot [--window TITLE OR PROGRAM] [--screen N] [--full] [picture.png]   screenshot, scaled to 1568 pixels by default; --list lists the windows\n" +
        "lt input [--no-look] [--timeout SECONDS] <step|-> [picture.png]   use the mouse and keyboard there, one step per line, several from stdin, then a screenshot:\n" +
        "        click 640 380        double / triple / right / middle alike, ctrl, shift or alt may follow\n        drag 10 10 200 200   move 640 380   scroll 640 400 down 3\n        type some text       key ctrl+s     key down 5     wait 500     focus window title or program\n" +
        "    Positions are pixels of the last picture, so lt shot comes first; it steps aside when someone there uses the mouse (waits up to --timeout seconds, 60 by default), exit code 1 when not all steps were done; never types into password boxes\n" +
        "lt cat [--from LINE] [-n LINES] [--number] <remote file>   show a text file\n" +
        "lt edit [--all] <remote file> -      edit a text file, reads stdin, several edits are fine:\n" +
        "        <<<<<<<\n        old text\n        =======\n        new text\n        >>>>>>>\n" +
        "    or lt edit <remote file> --old LOCALFILE --new LOCALFILE\n" +
        "lt put [--force] <local file> <remote file>   write a file there\n" +
        "lt get [--force] <remote file> <local file>   fetch a file\n" +
        "lt sync [--pull] [--dry] [--delete] [--force] [--skip NAME]... <source folder> <target folder>   copy only what changed\n" +
        "    From here to there, --pull the other way; --dry only compares; --delete also deletes what is gone from the source (into the recycle bin)\n" +
        "    Files changed at the destination are left alone, exit code 1, with the reason; .git, node_modules and __pycache__ are skipped unless --no-skip\n" +
        "lt tunnel <port> [--local PORT]      use a web server over there as localhost, until this command ends\n" +
        "lt claude [--cwd FOLDER] [--mode plan|acceptEdits] [--resume SESSION] [--model MODEL] [--wait] <task|->   hand work to the Claude Code there, returns a job number\n" +
        "lt update [--from FOLDER] [--force]  put this PC's LanTalk (or the build in a folder) on that PC; --self installs it here\n" +
        "lt info                              Windows, hardware, disks and tools of that PC\n" +
        "    Exit codes: run and log --follow return the remote exit code; 124 timed out there; 125 couldn't run (no access, declined, disconnected)\n" +
        "lt mcp                               run as an MCP server (for Claude Code); lt version prints the version\n" +
        "Options: --json prints JSON; --ascii turns non-ASCII into \\uXXXX; --as NAME who reads/sends (default claude)"); } }

    static string data = Ipc.DefaultData;
    public static string Data { get { return data; } }
    public static int Hop { get { int n; return int.TryParse(Environment.GetEnvironmentVariable("LANTALK_HOP") ?? "", out n) ? n : 0; } } // set for a Claude that another PC started
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
        string to = "", who = "", cwd = "", rest = null, rest2 = null, window = "", oldFile = null, newFile = null, session = "", mode = "", resume = "", model = "", fromDir = ""; bool wait = false, self = false; int localPort = 0; int timeout = -1, n = -1, screen = 0, max = 1568; long from = -1; bool peek = false, force = false, literal = false;
        bool bg = false, follow = false, list = false, all = false, pull = false, delete = false, dry = false, number = false, look = true;
        List<string> skip = new List<string> { ".git", "node_modules", "__pycache__" };
        if (args.Length == 0) { Print(Usage); return 1; }
        for (int i = 1; i < args.Length; i++) {
            string a = args[i];
            if (!literal && a == "--") literal = true;
            else if (!literal && a == "--json") json = true;
            else if (!literal && a == "--ascii") ascii = true;
            else if (!literal && a == "--peek") peek = true;
            else if (!literal && a == "--force") force = true;
            else if (!literal && a == "--bg") bg = true;
            else if (!literal && a == "--wait") wait = true;
            else if (!literal && a == "--self") self = true;
            else if (!literal && a == "--follow") follow = true;
            else if (!literal && a == "--list") list = true;
            else if (!literal && a == "--full") max = 0;
            else if (!literal && a == "--all") all = true;
            else if (!literal && a == "--pull") pull = true;
            else if (!literal && a == "--delete") delete = true;
            else if (!literal && a == "--dry") dry = true;
            else if (!literal && a == "--number") number = true;
            else if (!literal && a == "--no-look") look = false;
            else if (!literal && a == "--no-skip") skip.Clear();
            else if (!literal && (a == "--session" || a == "--mode" || a == "--resume" || a == "--model" || a == "--local" || a == "--window" || a == "--old" || a == "--new" || a == "--skip" || a == "--screen" || a == "--max" || a == "--from" || a == "--to" || a == "--as" || a == "--cwd" || a == "--timeout" || a == "-n" || a == "--data")) {
                if (++i >= args.Length) throw Lang.Err("{0} 后面缺少内容", "{0} needs a value", a);
                if (a == "--to") to = args[i];
                else if (a == "--as") who = args[i];
                else if (a == "--cwd") cwd = args[i];
                else if (a == "--data") data = args[i];
                else if (a == "--window") window = args[i];
                else if (a == "--session") session = args[i];
                else if (a == "--mode") mode = args[i];
                else if (a == "--resume") resume = args[i];
                else if (a == "--model") model = args[i];
                else if (a == "--from" && args[0] == "update") fromDir = args[i];
                else if (a == "--old") oldFile = args[i];
                else if (a == "--new") newFile = args[i];
                else if (a == "--skip") skip.Add(args[i]);
                else { int v; if (!int.TryParse(args[i], out v) || v < 0) throw Lang.Err("{0} 后面要是数字", "{0} needs a number", a); if (a == "-n") n = v; else if (a == "--screen") screen = v; else if (a == "--max") max = v; else if (a == "--from") from = v; else if (a == "--local") localPort = v; else timeout = v; }
            }
            else if (!literal && a.StartsWith("--")) throw Lang.Err("未知选项 {0}", "unknown option {0}", a);
            else if (rest == null) rest = a;
            else if (rest2 == null && (args[0] == "put" || args[0] == "get" || args[0] == "sync" || args[0] == "edit" || args[0] == "input")) rest2 = a;
            else throw Lang.Err("参数太多，文字请用引号括起来", "too many arguments, put the text in quotes");
        }
        if (args[0] == "version") { Print("LanTalk " + Node.Version); return 0; }
        if (args[0] == "mcp") return Mcp.Serve();
        Dictionary<string, object> q = new Dictionary<string, object>();
        q["cmd"] = args[0]; q["to"] = to; q["agent"] = who; q["reader"] = who.ToLowerInvariant(); q["peek"] = peek; q["n"] = n < 0 ? 20 : n;
        switch (args[0]) {
            case "list": {
                Dictionary<string, object> r = Ask(q);
                if (json) { Print(Util.ToJson(r)); return 0; }
                Print(Lang.T("本机：{0}    收到的文件存到：{1}", "This PC: {0}    Received files go to: {1}", Util.Str(r, "me"), Util.Str(r, "saveDir")));
                foreach (Dictionary<string, object> p in Items(r, "peers")) Print((Util.Flag(p, "online") ? Lang.T("[在线] ", "[online]  ") : Lang.T("[离线] ", "[offline] ")) + Util.Str(p, "name") + "  " + Util.Str(p, "ip") + "  id=" + Util.Str(p, "id") + (Util.Str(p, "ver") == "" ? "" : "  v" + Util.Str(p, "ver") + " (" + Util.Str(p, "build") + ")"));
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
                if (bg && session != "") throw Lang.Err("--bg 和 --session 不能一起用", "--bg and --session don't go together");
                if (session != "") { q["cmd"] = "shell"; q["r_session"] = session; }
                if (bg) q["cmd"] = "bg";
                q["r_cmd"] = rest; q["r_cwd"] = cwd; q["r_timeout"] = timeout >= 0 ? timeout : bg ? 86400 : 120;
                return Stream(q);
            case "jobs": case "info": case "sessions": return Stream(q);
            case "end":
                if (rest == null) throw Lang.Err("要写会话名，用 lt sessions 查看", "the session name is missing, see lt sessions");
                q["r_session"] = rest;
                return Stream(q);
            case "tunnel": {
                int port;
                if (!int.TryParse(rest ?? "", out port)) throw Lang.Err("要写端口号", "the port is missing");
                q["r_port"] = port; q["r_local"] = localPort;
                return Stream(q);
            }
            case "claude": {
                if (rest == null) throw Lang.Err("缺少任务内容", "the task is missing");
                if (rest == "-") rest = StdIn();
                q["r_cmd"] = rest; q["r_cwd"] = cwd; q["r_mode"] = mode; q["r_resume"] = resume; q["r_model"] = model; q["r_hop"] = Hop; q["r_timeout"] = timeout < 0 ? 86400 : timeout;
                int rc = Stream(q);
                if (rc != 0 || !wait) return rc;
                Dictionary<string, object> w = new Dictionary<string, object>();
                w["cmd"] = "log"; w["to"] = to; w["agent"] = who; w["r_job"] = lastJob; w["r_n"] = 100; w["r_from"] = 0; w["r_follow"] = true; w["r_timeout"] = timeout < 0 ? 3600 : timeout;
                return Stream(w);
            }
            case "update":
                if (self) {
                    if (fromDir == "") throw Lang.Err("装到本机要用 --from 指明新版所在的文件夹", "--self needs --from with the folder of the new build");
                    q["cmd"] = "selfupdate"; q["path"] = Path.GetFullPath(fromDir); q["force"] = force;
                    Ask(q);
                    Print(Lang.T("已换上，LanTalk 正在重启", "In place, LanTalk is restarting"));
                    return 0;
                }
                q["local"] = fromDir == "" ? "" : Path.GetFullPath(fromDir); q["r_force"] = force;
                return Stream(q);
            case "log": case "stop": {
                int job;
                if (!int.TryParse(rest ?? "", out job)) throw Lang.Err("要写任务号，用 lt jobs 查看", "the job number is missing, see lt jobs");
                q["r_job"] = job; q["r_n"] = n < 0 ? 100 : n; q["r_from"] = from; q["r_follow"] = follow; q["r_timeout"] = timeout < 0 ? 600 : timeout;
                return Stream(q);
            }
            case "shot":
                if (list) q["cmd"] = "windows";
                q["r_window"] = window; q["r_screen"] = screen; q["r_max"] = max; q["r_force"] = force;
                q["local"] = rest == null ? "" : Path.GetFullPath(rest);
                return Stream(q);
            case "input":
                if (rest == null) throw Lang.Err("要写步骤，例如 \"click 640 380\"，或用 - 从标准输入读", "the steps are missing, like \"click 640 380\", or - to read them from stdin");
                if (rest == "-") rest = StdIn();
                q["r_steps"] = InputLines(rest); q["r_look"] = look; q["r_timeout"] = timeout < 0 ? 60 : timeout; q["r_force"] = force;
                q["local"] = rest2 == null ? "" : Path.GetFullPath(rest2);
                return Stream(q);
            case "cat":
                if (rest == null) throw Lang.Err("缺少路径", "the path is missing");
                q["r_path"] = rest; q["r_from"] = from < 0 ? 1 : from; q["r_n"] = n < 0 ? 2000 : n; q["r_number"] = number;
                return Stream(q);
            case "edit":
                if (rest == null) throw Lang.Err("缺少路径", "the path is missing");
                if ((oldFile == null) != (newFile == null)) throw Lang.Err("--old 和 --new 要一起用", "--old and --new belong together");
                if (oldFile == null && rest2 != "-") throw Lang.Err("要改的内容从标准输入读：lt edit 文件 -", "the edits come from stdin: lt edit FILE -");
                q["r_path"] = rest; q["r_all"] = all;
                q["r_edits"] = oldFile != null ? new object[] { new { o = File.ReadAllText(oldFile), n = File.ReadAllText(newFile) } } : Edits(StdIn());
                return Stream(q);
            case "sync":
                if (rest2 == null) throw Lang.Err("要写两个路径：从哪里、到哪里", "two paths are needed: from where, to where");
                q["cmd"] = pull ? "pull" : "push";
                q["r_path"] = (pull ? rest : rest2).TrimEnd('/', '\\'); q["local"] = Path.GetFullPath(pull ? rest2 : rest);
                q["r_force"] = force; q["r_delete"] = delete; q["r_dry"] = dry; q["r_skip"] = skip;
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
    static int lastJob;
    static string[] InputLines(string s) { List<string> l = new List<string>(); foreach (string x in s.Replace("\r\n", "\n").Split('\n')) if (x.Trim() != "" && !x.TrimStart().StartsWith("#")) l.Add(x); return l.ToArray(); }
    // Blocks of <<<<<<< old ======= new >>>>>>>, each mark on a line of its own.
    public static object[] Edits(string s) {
        List<object> l = new List<object>();
        List<string> part = null; string old = null;
        foreach (string line in s.Replace("\r\n", "\n").Split('\n')) {
            string t = line.TrimEnd();
            if (part == null) {
                if (t == "<<<<<<<") part = new List<string>();
                else if (t.Length > 0) throw Lang.Err("看不懂这一行：{0}（每处改动以 <<<<<<< 开头）", "can't read this line: {0} (every edit starts with <<<<<<<)", Util.Cut(t, 60));
            }
            else if (old == null && t == "=======") { old = string.Join("\n", part.ToArray()); part = new List<string>(); }
            else if (old != null && t == ">>>>>>>") { l.Add(new { o = old, n = string.Join("\n", part.ToArray()) }); part = null; old = null; }
            else part.Add(line);
        }
        if (part != null) throw Lang.Err("最后一处改动没有写完（缺 ======= 或 >>>>>>>）", "the last edit is incomplete (======= or >>>>>>> is missing)");
        return l.ToArray();
    }
    // Remote control: prints what the other PC sends as it comes. If this process is killed the pipe breaks, and that stops the remote command.
    static int Stream(Dictionary<string, object> q) {
        string cmd = Util.Str(q, "cmd");
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
                else if (k == "tunnel") { if (json) Print(Util.ToJson(new { ok = true, local = Util.Num(r, "local"), port = Util.Num(r, "port") })); else Print("http://localhost:" + Util.Num(r, "local") + "  →  " + Util.Str(q, "to") + ":" + Util.Num(r, "port") + Lang.T("（结束这个命令就关闭）", "  (closed when this command ends)")); Out.Flush(); }
                else if (k == "wait" && !json) { err.Write(Lang.T("（等那台电脑上的人确认，最多 60 秒…）", "(Waiting for someone at that PC to approve, up to 60 seconds...)") + "\n"); err.Flush(); }
                else if (k == "x") {
                    int code = (int)Util.Num(r, "code");
                    if (cmd == "claude") lastJob = (int)Util.Num(r, "job");
                    if (json) {
                        foreach (string drop in new string[] { "k", "done", "err", "err_en" }) r.Remove(drop);
                        r["stdout"] = so.ToString(); r["stderr"] = se.ToString();
                        Print(Util.ToJson(r));
                    } else { Ending(cmd, r, err); err.Flush(); }
                    return code;
                }
            }
        }
    }

    static void Ending(string cmd, Dictionary<string, object> r, TextWriter err) {
        string end = Msg.Wire(r, "end") ?? "";
        switch (cmd) {
            case "run": case "shell": case "cat": case "push": case "pull": return; // all said
            case "claude": Print(Lang.T("已交给那台的 Claude：任务 {0}（lt log {0} --follow 看进展，lt stop {0} 停止）", "Handed to the Claude there: job {0} (lt log {0} --follow, lt stop {0})", Util.Num(r, "job"))); return;
            case "end": Print(Lang.T("已关闭", "Closed")); return;
            case "update": Print(Lang.T("已更新，那台电脑已重新上线", "Updated, that PC is back online")); return;
            case "sessions":
                if (Items(r, "sessions").Count == 0) Print(Lang.T("（没有会话）", "(No sessions)"));
                foreach (Dictionary<string, object> s in Items(r, "sessions")) Print(Util.Str(s, "name") + "  " + (Util.Flag(s, "busy") ? Lang.T("正在运行", "busy") : Lang.T("空闲 {0}", "idle for {0}", (string)Msg.Wire(s, "idle"))));
                return;
            case "bg": Print(Lang.T("已在后台开始：任务 {0}（lt log / lt stop 加任务号）", "Started in the background: job {0} (lt log / lt stop with the number)", Util.Num(r, "job"))); return;
            case "edit": Print(end); return;
            case "input": {
                int i = 0;
                object v; if (r.TryGetValue("steps", out v) && v is IEnumerable) foreach (object o in (IEnumerable)v) { IList p = o as IList; if (p != null && p.Count > 1) Print((++i) + "  " + Convert.ToString(p[Lang.En ? 1 : 0])); }
                if (Util.Str(r, "path") != "") Print(Util.Str(r, "path") + "\n" + (string)Msg.Wire(r, "what"));
                if (Util.Str(r, "note") != "") err.Write(Esc(Lang.T("没做完：", "Not finished: ") + (string)Msg.Wire(r, "note")) + "\n");
                return;
            }
            case "stop": Print(Lang.T("已停止", "Stopped")); return;
            case "shot": Print(Util.Str(r, "path") + "\n" + Util.Str(r, "what")); return;
            case "log": err.Write(Esc(Lang.T("（{0} · 下次接着看用 --from {1}）", "({0} · continue with --from {1})", end, Util.Num(r, "from")) + (Util.Str(r, "session") == "" ? "" : Lang.T("（Claude 会话 {0}，接着派用 --resume）", " (Claude session {0}, go on with --resume)", Util.Str(r, "session"))) + "\n")); return;
            case "info": foreach (Dictionary<string, object> f in Items(r, "info")) Print(Msg.Wire(f, "name") + Lang.T("：", ": ") + ((string)Msg.Wire(f, "value") ?? "").Replace("\n", "\n    ")); return;
            case "jobs":
                if (Items(r, "jobs").Count == 0) Print(Lang.T("（没有后台任务）", "(No background jobs)"));
                foreach (Dictionary<string, object> j in Items(r, "jobs")) Print(Util.Num(j, "id") + "  " + (Util.Flag(j, "running") ? Lang.T("运行中 · {0}", "running · {0}", (string)Msg.Wire(j, "span")) : (string)Msg.Wire(j, "end")) + " · " + Util.SizeText(Util.Num(j, "size")) + "  " + (Util.Flag(j, "claude") ? "[Claude] " : "") + Util.Cut(Util.Str(j, "cmd").Replace("\r", "").Replace("\n", " ; "), 120));
                return;
            case "windows": {
                int i = 0;
                foreach (Dictionary<string, object> m in Items(r, "monitors")) Print(Lang.T("显示器 {0}", "Monitor {0}", ++i) + "  " + Util.Num(m, "width") + "x" + Util.Num(m, "height") + "  (" + Util.Num(m, "x") + "," + Util.Num(m, "y") + ")");
                foreach (Dictionary<string, object> w in Items(r, "windows")) Print(Util.Str(w, "id") + "  " + Util.Str(w, "process") + "  " + (Util.Flag(w, "minimized") ? Lang.T("最小化", "minimized") : Util.Num(w, "width") + "x" + Util.Num(w, "height")) + "  " + Util.Str(w, "title"));
                return;
            }
        }
        Print(Lang.T("完成", "Done"));
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
    public static List<Dictionary<string, object>> Items(Dictionary<string, object> r, string k) {
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
