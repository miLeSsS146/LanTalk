// Tests for Work.cs and Mcp.cs, part of Test.cs. A asks, B is controlled.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LanTalk {
static partial class Test {
    static void WorkTests(Node A, Node B, string da, string db) {
        int rc = 0; string o, ib = B.Cfg.Id, ia = A.Cfg.Id, work = Path.Combine(root, "会话 目录");
        Directory.CreateDirectory(work);
        Func<string, string, string> lt = delegate (string args, string stdin) { return LtErr(da, out rc, args + " --to PC-B", stdin); };
        B.SetGrant(ia, "PC-A", A.PublicKey, "allow");

        // ---- sessions ----
        o = lt("run --session s1 -", "$x = '记住 😀'; $env:LT_T = 'env'; Set-Location '" + work + "'; function hi { 'hi ' + $x }"); Check(rc == 0, "session: first command " + Err.Trim());
        o = lt("run --session s1 -", "hi; $env:LT_T; (Get-Location).Path"); Check(rc == 0 && o.Contains("hi 记住 😀") && o.Contains("env") && o.Contains(work), "session: variables, functions, environment and folder stay");
        o = lt("run --session s1 -", "[Console]::Error.WriteLine('错'); cmd /c 'echo native & exit 7'"); Check(rc == 7 && o.Contains("native") && Err.Contains("错"), "session: output, errors and exit code (" + rc + ")");
        o = lt("run --session s1 -", "Get-Item C:\\nope-nope"); Check(rc == 1 && Err.Contains("nope-nope"), "session: a failing cmdlet exits 1");
        o = lt("run --session s1 -", "throw '停下'; 'never'"); Check(rc == 1 && Err.Contains("停下") && !o.Contains("never"), "session: an exception is reported, the session goes on");
        o = lt("run --session s1 -", "if ("); Check(rc == 1 && Err.Length > 0, "session: a syntax error too");
        o = lt("run --session s1 -", "foreach ($i in 1..3) { if ($i -eq 2) { break }; $i }; break"); Check(rc == 0 && o.Trim() == "1", "session: a stray break doesn't end it");
        o = lt("run --session s1 -", "Get-Process -Id $PID | Select-Object Id, ProcessName | Format-Table; 'after'"); Check(rc == 0 && o.Contains("powershell") && o.Contains("after"), "session: tables come out with their own command");
        o = lt("run --session s1 -", "[Console]::In.ReadLine(); 'read'"); Check(rc == 0 && o.Contains("read"), "session: a program reading input doesn't eat the next command");
        o = lt("run --session s1 -", "1..20000 | % { 'zeile ' + $_ }"); Check(rc == 0 && o.Split('\n').Length == 20001 && o.Contains("zeile 20000"), "session: a lot of output arrives completely (" + o.Split('\n').Length + ")");
        o = lt("run --session s1 hi", null); Check(o.Contains("记住"), "session: still the same one");
        o = lt("run --session s2 -", "$x; 'other'"); Check(o.Trim() == "other", "session: another name is another session");
        o = lt("sessions", null); Check(rc == 0 && o.Contains("s1  空闲") && o.Contains("s2  空闲"), "session: list");
        Thread t = new Thread(delegate () { int r2; LtErr(da, out r2, "run --session s1 --to PC-B -", "Start-Sleep 3"); }); t.Start(); Thread.Sleep(1500);
        int rc1; string e1; { string o1 = LtErr(da, out rc1, "run --session s1 --to PC-B 1", null); e1 = Err; } Check(rc1 == 125 && e1.Contains("另一条"), "session: one command at a time"); t.Join();
        o = lt("run --session s1 --timeout 2 \"Start-Sleep 60\"", null); Check(rc == 124 && Err.Contains("会话 s1 已结束"), "session: a timeout ends it, and says so");
        o = lt("run --session s1 -", "\"[$x]\""); Check(o.Trim() == "[]", "session: a new one starts empty");
        o = lt("run --session s1 \"exit 5\"", null); Check(rc == 5 && Err.Contains("已结束"), "session: exit closes it");
        o = lt("end s2", null); Check(rc == 0, "session: end"); o = lt("end s2", null); Check(rc == 125 && Err.Contains("没有叫"), "session: no such session");
        o = lt("run --session \"../x\" 1", null); Check(rc == 125 && Err.Contains("会话名"), "session: odd names are refused");
        Check(B.History(ia, 30).Exists(delegate (ChatMsg m) { return m.Op == "shell" && m.Text.StartsWith("[会话 s1]"); }), "session: recorded in the chat");
        lt("run --session s3 -", "$y = 1"); B.PauseControl(ia); B.ResumeControl(ia); o = lt("run --session s3 -", "\"[$y]\""); Check(o.Trim() == "[]", "session: Stop closes sessions");

        // ---- ports ----
        TcpListener web = new TcpListener(IPAddress.Loopback, 0); web.Start(); int port = ((IPEndPoint)web.LocalEndpoint).Port;
        byte[] blob = Crypto.Random(3000000);
        Thread ws = new Thread(delegate () {
            while (true) {
                TcpClient c; try { c = web.AcceptTcpClient(); } catch (Exception) { return; }
                TcpClient k = c;
                new Thread(delegate () {
                    try { using (k) using (NetworkStream n = k.GetStream()) { StreamReader r = new StreamReader(n); string first = r.ReadLine(); while (r.ReadLine() != "") { } byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + blob.Length + "\r\nConnection: close\r\nX-Path: " + first.Split(' ')[1] + "\r\n\r\n"); n.Write(h, 0, h.Length); n.Write(blob, 0, blob.Length); } } catch (Exception) { }
                }) { IsBackground = true }.Start();
            }
        }); ws.IsBackground = true; ws.Start();
        ProcessStartInfo si = new ProcessStartInfo(ltExe, "tunnel " + port + " --json --to PC-B --data \"" + da + "\""); si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardOutput = true; si.StandardOutputEncoding = Encoding.UTF8;
        using (Process p = Process.Start(si)) {
            string line = p.StandardOutput.ReadLine(); int local = (int)Util.Num(Util.FromJson<Dictionary<string, object>>(line), "local");
            Check(local > 0 && local != port, "tunnel: another local port when the same one is taken: " + line);
            byte[] got = null; string path = "";
            for (int i = 0; i < 2; i++) using (WebClient w = new WebClient()) { got = w.DownloadData("http://127.0.0.1:" + local + "/a?b=" + i); path = w.ResponseHeaders["X-Path"]; }
            Check(got.SequenceEqual(blob) && path == "/a?b=1", "tunnel: 3 MB come through unchanged");
            int ok = 0; Thread[] ts = Enumerable.Range(0, 12).Select(delegate (int i) { Thread x = new Thread(delegate () { try { using (WebClient w = new WebClient()) if (w.DownloadData("http://127.0.0.1:" + local + "/" + i).Length == blob.Length) Interlocked.Increment(ref ok); } catch (WebException) { } }); x.Start(); return x; }).ToArray(); foreach (Thread x in ts) x.Join();
            Check(ok == 12, "tunnel: twelve connections at once: " + ok);
            ChatMsg m = B.History(ia, 1)[0]; Check(m.Op == "tunnel" && m.State == "run" && B.Controlling().Count == 1, "tunnel: visible while open");
            p.Kill(); p.WaitForExit();
            Check(Until(delegate { return m.State == "ok"; }, 12), "tunnel: ends with the command");
            bool dead = false; try { using (TcpClient c = new TcpClient("127.0.0.1", local)) { } } catch (SocketException) { dead = true; }
            Check(dead, "tunnel: the local port is closed again");
        }
        using (Process p = Process.Start(si)) {
            int local = (int)Util.Num(Util.FromJson<Dictionary<string, object>>(p.StandardOutput.ReadLine()), "local");
            using (TcpClient c = new TcpClient("127.0.0.1", local)) {
                byte[] hello = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"); c.GetStream().Write(hello, 0, hello.Length); Thread.Sleep(500);
                B.PauseControl(ia); B.ResumeControl(ia); c.ReceiveTimeout = 5000; int n = -1; try { n = c.GetStream().Read(new byte[1], 0, 1); } catch (IOException) { n = 0; }
                Check(n == 0 && p.WaitForExit(8000) && p.ExitCode == 125, "tunnel: Stop over there closes the port and its connections");
            }
        }
        web.Stop();
        Dictionary<string, object> fr = ForgeX(A, B, Util.ToJson(new { op = "conn", tunnel = Util.NewId() })); Check(!Util.Flag(fr, "ok"), "tunnel: no connection without an open tunnel");
        o = lt("tunnel 0", null); Check(rc == 125, "tunnel: bad port");
        B.SetGrant(ia, "PC-A", A.PublicKey, Grant.Of("allow", "allow", "allow", "")); o = lt("tunnel 80", null); Check(rc == 125 && Err.Contains("运行命令"), "tunnel: needs 'run commands'");
        o = lt("claude hi", null); Check(rc == 125 && Err.Contains("运行命令"), "claude: needs 'run commands'");
        o = lt("update --from \"" + Environment.CurrentDirectory + "\"", null); Check(rc == 125 && Err.Contains("运行命令"), "update: needs 'run commands'");
        B.SetGrant(ia, "PC-A", A.PublicKey, "allow");

        // ---- Claude ----
        string fake = Path.Combine(root, "fake claude.cmd"), said = Path.Combine(work, "said.txt"), lines = Path.Combine(root, "claude.jsonl"), sid = "67079880-0daf-4133-96f8-05d73f5f2593";
        Environment.SetEnvironmentVariable("LANTALK_CLAUDE", Path.Combine(root, "none.exe"));
        o = lt("claude hi", null); Check(rc == 125 && Err.Contains("没有装 Claude Code"), "claude: says when it isn't installed");
        File.WriteAllText(fake, "@echo off\r\nfindstr \"^\" > said.txt\r\necho %* hop=%LANTALK_HOP% > args.txt\r\ntype \"" + lines + "\"\r\n", Encoding.Default);
        Environment.SetEnvironmentVariable("LANTALK_CLAUDE", fake);
        File.WriteAllText(lines, "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + sid + "\"}\n{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"看一下\"},{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"npm test\"}}]},\"session_id\":\"" + sid + "\"}\nnot json\n{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"做完了\"}]}}\n{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"做完了\",\"permission_denials\":[{\"tool_name\":\"Write\"}],\"session_id\":\"" + sid + "\"}\n", new UTF8Encoding(false));
        o = lt("claude --cwd \"" + work + "\" --mode acceptEdits --wait -", "修一下 \"测试\" $x\n第二行");
        Check(rc == 0 && o.Contains("看一下") && o.Contains("· Bash: npm test") && o.Contains("not json") && o.Contains("做完了") && o.Contains("1 次操作没有权限") && Err.Contains(sid), "claude: readable progress and session (" + rc + "): " + o.Replace("\n", "|"));
        string gotArgs = File.ReadAllText(Path.Combine(work, "args.txt"));
        Check(File.ReadAllText(said, Encoding.UTF8).Contains("修一下 \"测试\" $x") && gotArgs.Contains("-p --output-format stream-json --verbose --permission-mode acceptEdits") && gotArgs.Contains("hop=1"), "claude: gets the task as written: " + gotArgs.Trim());
        o = lt("claude --cwd \"" + work + "\" --resume " + sid + " --wait go", null); Check(rc == 0 && File.ReadAllText(Path.Combine(work, "args.txt")).Contains("--resume " + sid), "claude: goes on with a session");
        Check(B.History(ia, 1)[0].Text.StartsWith("派活给 Claude · 任务 ") && lt("jobs", null).Contains("[Claude]"), "claude: a background job like others");
        File.WriteAllText(lines, "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Not logged in · Please run /login\"}]}}\n{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"Not logged in · Please run /login\"}\n", new UTF8Encoding(false));
        o = lt("claude --cwd \"" + work + "\" --wait go", null); Check(rc == 1 && o.Contains("claude auth login"), "claude: says when it isn't logged in (" + rc + ")");
        o = lt("claude --mode bypassPermissions go", null); Check(rc == 125 && Err.Contains("--mode"), "claude: no bypassing of permissions");
        o = lt("claude --resume \"x & calc\" go", null); Check(rc == 125, "claude: odd session ids are refused"); o = lt("claude --model \"x & calc\" go", null); Check(rc == 125, "claude: odd model names are refused");
        Environment.SetEnvironmentVariable("LANTALK_HOP", "1"); o = lt("claude go", null); Environment.SetEnvironmentVariable("LANTALK_HOP", null); Check(rc == 125 && Err.Contains("不能再往外派"), "claude: one sent by another PC doesn't hand work on");
        Environment.SetEnvironmentVariable("LANTALK_CLAUDE", null);

        // ---- MCP ----
        File.WriteAllText(Path.Combine(work, "m.txt"), "one\ntwo\n");
        si = new ProcessStartInfo(ltExe, "mcp --data \"" + da + "\""); si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardOutput = si.RedirectStandardInput = true; si.StandardOutputEncoding = Encoding.UTF8;
        using (Process p = Process.Start(si)) {
            Dictionary<string, Dictionary<string, object>> replies = new Dictionary<string, Dictionary<string, object>>();
            Thread rd = new Thread(delegate () { string l; while ((l = p.StandardOutput.ReadLine()) != null) { Dictionary<string, object> d = Util.FromJson<Dictionary<string, object>>(l); lock (replies) replies[Util.Str(d, "id")] = d; } }); rd.IsBackground = true; rd.Start();
            Action<object> say = delegate (object m) { byte[] b = Encoding.UTF8.GetBytes(Util.ToJson(m) + "\n"); p.StandardInput.BaseStream.Write(b, 0, b.Length); p.StandardInput.BaseStream.Flush(); };
            Func<string, Dictionary<string, object>> wait = delegate (string id) { Dictionary<string, object> d = null; Until(delegate { lock (replies) return replies.TryGetValue(id, out d); }, 40); return d; };
            int next = 10; bool bad = false;
            Func<string, object, string> call = delegate (string tool, object a) {
                string id = "c" + next++; say(new { jsonrpc = "2.0", id = id, method = "tools/call", @params = new { name = tool, arguments = a } });
                Dictionary<string, object> res = (Dictionary<string, object>)wait(id)["result"]; bad = Util.Flag(res, "isError");
                return string.Join("\n", Cont(res).Select(delegate (Dictionary<string, object> c) { return Util.Str(c, "type") == "text" ? Util.Str(c, "text") : "<" + Util.Str(c, "mimeType") + " " + Util.Str(c, "data").Length + ">"; }).ToArray());
            };
            say(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26", capabilities = new { } } });
            Dictionary<string, object> init = (Dictionary<string, object>)wait("1")["result"]; say(new { jsonrpc = "2.0", method = "notifications/initialized" });
            Check(Util.Str(init, "protocolVersion") == "2025-03-26" && Util.Str(init, "instructions").Contains("LanTalk"), "mcp: initialize");
            say(new { jsonrpc = "2.0", id = 2, method = "tools/list" }); string list = Util.ToJson(wait("2")); Check(list.Contains("\"name\":\"screenshot\"") && list.Contains("\"name\":\"ask_claude\"") && list.Contains("\"inputSchema\"") && !Chinese(list), "mcp: tools/list, " + Regex.Matches(list, "\"inputSchema\"").Count + " tools");
            o = call("list_pcs", new { }); Check(!bad && o.Contains("online   PC-B"), "mcp: list_pcs");
            o = call("run", new { pc = "PC-B", command = "'中文 \"引号\" $x \\ 反斜杠'\ncmd /c 'exit 3'" }); Check(!bad && o.Contains("中文 \"引号\"") && o.Contains("\\ 反斜杠") && o.Contains("exit code 3"), "mcp: run, nothing needs quoting: " + o.Replace("\n", "|"));
            call("run", new { pc = "PC-B", command = "$z = 41", session = "m" }); o = call("run", new { pc = "PC-B", command = "$z + 1", session = "m" }); Check(o.StartsWith("42"), "mcp: run in a session");
            o = call("read_file", new { pc = "PC-B", path = Path.Combine(work, "m.txt") }); Check(o.Contains("     2\ttwo"), "mcp: read_file");
            o = call("edit_file", new { pc = "PC-B", path = Path.Combine(work, "m.txt"), edits = new object[] { new { old = "two", @new = "zwei\n\"drei\"" } } }); Check(!bad && File.ReadAllText(Path.Combine(work, "m.txt")) == "one\nzwei\n\"drei\"\n", "mcp: edit_file");
            o = call("edit_file", new { pc = "PC-B", path = Path.Combine(work, "m.txt"), edits = new object[] { new { old = "nope", @new = "x" } } }); Check(bad && o.Contains("找不到"), "mcp: a refused edit is an error");
            o = call("run", new { pc = "PC-B", command = "'job out'; exit 4", background = true }); string job = Regex.Match(o, "Job (\\d+)").Groups[1].Value;
            o = call("job_log", new { pc = "PC-B", job = int.Parse(job), wait = 20 }); Check(o.Contains("job out") && o.Contains("ended"), "mcp: background job and job_log: " + o.Replace("\n", "|"));
            o = call("jobs", new { pc = "PC-B" }); Check(o.Contains("job " + job + "  ") && o.Contains("session m"), "mcp: jobs and sessions");
            o = call("job_stop", new { pc = "PC-B", session = "m" }); Check(!bad, "mcp: close a session");
            o = call("sync_folder", new { pc = "PC-B", local = work, remote = work + " kopie", dry_run = true }); Check(!bad && o.Contains("只比较"), "mcp: sync_folder");
            o = call("pc_info", new { pc = "PC-B" }); Check(o.Contains("Windows: Windows") && o.Contains("Claude Code: "), "mcp: pc_info");
            if (!LanTalk.Shot.Locked) { o = call("screenshot", new { pc = "PC-B", screen = 1 }); Check(!bad && Regex.IsMatch(o, "<image/(png|jpeg) \\d{4,}>") && o.Contains("Saved as "), "mcp: screenshot comes as a picture"); }
            web = new TcpListener(IPAddress.Loopback, 0); web.Start(); int wp = ((IPEndPoint)web.LocalEndpoint).Port;
            o = call("forward_port", new { pc = "PC-B", port = wp }); int lp = int.Parse(Regex.Match(o, "localhost:(\\d+)").Groups[1].Value);
            using (TcpClient c = new TcpClient("127.0.0.1", lp)) Check(Until(delegate { return web.Pending(); }, 5), "mcp: forward_port");
            o = call("close_port", new { local_port = lp }); Check(!bad && Until(delegate { return B.Controlling().Count == 0; }, 10), "mcp: close_port"); web.Stop();
            string beat = Path.Combine(work, "beat.txt");
            say(new { jsonrpc = "2.0", id = "long", method = "tools/call", @params = new { name = "run", arguments = new { pc = "PC-B", command = "while(1){[IO.File]::WriteAllText('" + beat + "',[DateTime]::Now.Ticks);Start-Sleep -m 100}" } } });
            Until(delegate { return File.Exists(beat); }, 10); o = call("list_pcs", new { }); Check(!bad, "mcp: other tools work meanwhile");
            say(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = "long" } }); Check(Until(delegate { return B.Controlling().Count == 0; }, 12) && Frozen(beat), "mcp: cancelling stops the command over there");
            o = call("run", new { pc = "nobody", command = "1" }); Check(bad && o.Contains("没有叫"), "mcp: unknown PC is an error"); o = call("run", new { pc = "PC-B" }); Check(bad && o.Contains("command"), "mcp: a missing argument is an error");
            say(new { jsonrpc = "2.0", id = 3, method = "nope" }); Check(wait("3").ContainsKey("error"), "mcp: unknown method");
            p.StandardInput.Close(); Check(p.WaitForExit(5000), "mcp: ends with its client");
        }

        // ---- update: a real LanTalk.exe of its own, in a folder of its own ----
        o = lt("update --from \"" + Environment.CurrentDirectory + "\"", null); Check(rc == 125 && Err.Contains("不是 LanTalk.exe"), "update: only a real LanTalk is updated");
        string inst = Path.Combine(root, "安装 目录"), nd = Path.Combine(root, "PC-E"), v2 = Path.Combine(root, "v2"), v3 = Path.Combine(root, "v3"), v4 = Path.Combine(root, "v4");
        foreach (string d in new string[] { inst, v2, v3, v4 }) Directory.CreateDirectory(d);
        foreach (string f in new string[] { "LanTalk.exe", "lt.exe" }) { File.Copy(Path.Combine(Environment.CurrentDirectory, f), Path.Combine(inst, f)); byte[] b = File.ReadAllBytes(f).Concat(new byte[] { 1, 2, 3 }).ToArray(); File.WriteAllBytes(Path.Combine(v2, f), b); }
        File.Copy("lt.exe", Path.Combine(v3, "LanTalk.exe")); File.Copy("lt.exe", Path.Combine(v3, "lt.exe")); // starts, but isn't LanTalk
        File.WriteAllBytes(Path.Combine(v4, "LanTalk.exe"), new byte[5000]); File.WriteAllBytes(Path.Combine(v4, "lt.exe"), new byte[5000]); // doesn't even start
        Node E = Make("PC-E", 47907, B.Code, 47901); E.Cfg.Control.Clear(); E.SetGrant(ia, "PC-A", A.PublicKey, "allow"); string ie = E.Cfg.Id; E.SaveConfig();
        Process lanTalk = Process.Start(new ProcessStartInfo(Path.Combine(inst, "LanTalk.exe"), "--tray --data \"" + nd + "\" --bind 127.0.0.1") { UseShellExecute = false });
        Func<string> build = delegate { Peer p = A.Find(ie); return p == null || !p.Online ? "" : p.Build ?? ""; };
        string b1 = Node.ShaHex("LanTalk.exe").Substring(0, 8), b2 = Node.ShaHex(Path.Combine(v2, "LanTalk.exe")).Substring(0, 8);
        Check(Until(delegate { return build() == b1; }, 20), "update: the other PC announces its version " + build());
        o = LtErr(da, out rc, "list", null); Check(o.Contains("v" + Node.Version + " (" + b1 + ")"), "update: lt list shows it");
        o = LtErr(da, out rc, "update --to PC-E --from \"" + Environment.CurrentDirectory + "\"", null); Check(rc == 125 && Err.Contains("已经是这个版本"), "update: the same version is left alone");
        o = LtErr(da, out rc, "update --to PC-E --from \"" + v4 + "\"", null); Check(rc == 125 && build() == b1 && Directory.GetFiles(inst, "*.old-*").Length == 0, "update: a build that doesn't run is never put in place: " + Err.Trim());
        LtErr(da, out rc, "run --to PC-E --bg \"Start-Sleep 60\"", null);
        o = LtErr(da, out rc, "update --to PC-E --from \"" + v2 + "\"", null); Check(rc == 125 && Err.Contains("1 项正在进行") && build() == b1, "update: doesn't interrupt what is running");
        LtErr(da, out rc, "stop 1 --to PC-E", null);
        o = LtErr(da, out rc, "update --to PC-E --from \"" + v2 + "\"", null); Check(rc == 0 && build() == b2 && Node.ShaHex(Path.Combine(inst, "lt.exe")) == Node.ShaHex(Path.Combine(v2, "lt.exe")) && Directory.GetFiles(inst, "*.old-*.exe").Length == 2 && Until(delegate { return Directory.GetFiles(nd, "updated-*").Length == 0; }, 10), "update: new version in place and running, old one kept (" + rc + " " + Err.Trim() + ")");
        o = LtErr(da, out rc, "run --to PC-E \"'works'\"", null); Check(rc == 0 && o.Contains("works"), "update: access survives");
        o = LtErr(da, out rc, "update --to PC-E --from \"" + v3 + "\"", null); Check(rc == 125 && Err.Contains("换回") && Until(delegate { return build() == b2; }, 20) && Node.ShaHex(Path.Combine(inst, "LanTalk.exe")).StartsWith(b2), "update: a version that doesn't come up is taken back: " + Err.Trim());
        o = LtErr(nd, out rc, "update --self --from \"" + Environment.CurrentDirectory + "\"", null); Check(rc == 0 && Until(delegate { return build() == b1; }, 30) && Until(delegate { return Directory.GetFiles(nd, "updated-*").Length == 0; }, 10), "update: --self");
        Thread.Sleep(4000); // killed before its script has seen it running, it would be started again
        foreach (Process p in Process.GetProcessesByName("LanTalk")) try { if (p.MainModule.FileName.StartsWith(inst, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } } catch (System.ComponentModel.Win32Exception) { }
        Thread.Sleep(500); A.ForgetPeer(ie); B.ForgetPeer(ie); B.ClearGrants();
        o = Lt(da, out rc, "version"); Check(o.Trim() == "LanTalk " + Node.Version, "lt version");
    }
    static IEnumerable<Dictionary<string, object>> Cont(Dictionary<string, object> res) { foreach (object c in (System.Collections.IEnumerable)res["content"]) yield return (Dictionary<string, object>)c; }
}
}
