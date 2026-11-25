// More remote control: background jobs, screenshots, reading and editing text files, folder sync, facts about a PC.
// Control.cs checks who may do what before anything in here runs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace LanTalk {

public class BgJob {
    public int Id, Code;
    public string Peer, Cmd, Cwd, File, MsgId, Session; // Session: of a Claude job
    public bool Claude; public volatile bool Failed;
    public DateTime Start, End;
    public volatile bool Running;
    public Msg Result;
    public double Seconds { get { return ((Running ? DateTime.UtcNow : End) - Start).TotalSeconds; } }
    public static Msg Span(double s) {
        int n = (int)s;
        return n < 60 ? Lang.M("{0:0.0} 秒", "{0:0.0} s", s) : n < 3600 ? Lang.M("{0} 分 {1} 秒", "{0} min {1} s", n / 60, n % 60) : Lang.M("{0} 小时 {1} 分", "{0} h {1} min", n / 3600, n / 60 % 60);
    }
}

public partial class Node {
    const int MaxJobs = 8, KeepJobs = 50, MaxLog = 16 * 1024 * 1024, MaxText20 = 20 * 1024 * 1024;
    readonly List<BgJob> jobs = new List<BgJob>(); // what other PCs started here in the background, newest last
    int lastJob;

    static string DescribeMore(string op, Dictionary<string, object> b, string local) {
        string path = Util.Str(b, "path"), cwd = Util.Str(b, "cwd"), into = local == null ? "" : "\n" + Lang.T("存到 {0}", "into {0}", local);
        switch (op) {
            case "bg": return (b.ContainsKey("job") ? Lang.T("后台任务 {0}", "Background job {0}", Util.Num(b, "job")) : Lang.T("后台任务", "Background job")) + "\n" + (cwd != "" ? "[" + cwd + "]\n" : "") + Util.Str(b, "cmd");
            case "claude": return (b.ContainsKey("job") ? Lang.T("派活给 Claude · 任务 {0}", "Work for Claude · job {0}", Util.Num(b, "job")) : Lang.T("派活给 Claude", "Work for Claude")) + "\n" + (cwd != "" ? "[" + cwd + "]\n" : "") + (Util.Str(b, "mode") != "" ? "(" + Util.Str(b, "mode") + ")\n" : "") + Util.Str(b, "cmd");
            case "shell": return Lang.T("[会话 {0}]", "[session {0}]", Util.Str(b, "session")) + (cwd != "" ? " [" + cwd + "]" : "") + "\n" + Util.Str(b, "cmd");
            case "tunnel": return Lang.T("打开端口 {0}", "Open port {0}", Util.Num(b, "port"));
            case "update": return Lang.T("更新 LanTalk", "Update LanTalk") + (local != null ? "\n" + Lang.T("来自 {0}", "from {0}", local) : "");
            case "shot": return Lang.T("看屏幕：", "See the screen: ") + (b.ContainsKey("what") ? Util.Str(b, "what") : Util.Str(b, "window") != "" ? Lang.T("窗口“{0}”", "window \"{0}\"", Util.Str(b, "window")) : Util.Num(b, "screen") > 0 ? Lang.T("显示器 {0}", "monitor {0}", Util.Num(b, "screen")) : Lang.T("整个屏幕", "the whole screen"));
            case "windows": return Lang.T("列出打开的窗口", "List the open windows");
            case "cat": return Lang.T("读取文件 {0}", "Read file {0}", path);
            case "edit": {
                StringBuilder s = new StringBuilder(Lang.T("修改文件 {0}", "Edit file {0}", path) + (Util.Flag(b, "all") ? Lang.T("（每一处都换）", " (every occurrence)") : ""));
                if (Util.Str(b, "sum") != "") return s.Append('\n').Append(Util.Str(b, "sum")).ToString();
                foreach (Dictionary<string, object> e in Dicts(b, "edits")) {
                    foreach (string l in Util.Str(e, "o").Replace("\r\n", "\n").Split('\n')) s.Append("\n- ").Append(l);
                    foreach (string l in Util.Str(e, "n").Replace("\r\n", "\n").Split('\n')) s.Append("\n+ ").Append(l);
                }
                return s.ToString();
            }
            case "push": return Lang.T("同步文件夹 {0}", "Sync folder {0}", path) + (local != null ? "\n" + Lang.T("来自 {0}", "from {0}", local) : "") + (Util.Str(b, "sum") != "" ? "\n" + Util.Str(b, "sum") : "");
            case "pull": return Lang.T("读取文件夹 {0}", "Read folder {0}", path) + into;
            case "input": return Lang.T("鼠标键盘", "Mouse and keyboard");
            case "info": return Lang.T("查看电脑信息", "Look up facts about this PC");
        }
        return op;
    }
    static IEnumerable<Dictionary<string, object>> Dicts(Dictionary<string, object> b, string k) {
        object v;
        if (b.TryGetValue(k, out v) && v is IEnumerable) foreach (object o in (IEnumerable)v) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) yield return d; }
    }
    internal static List<IList> Rows(Dictionary<string, object> b, string k) {
        List<IList> l = new List<IList>(); object v;
        if (b.TryGetValue(k, out v) && v is IEnumerable) foreach (object o in (IEnumerable)v) { IList r = o as IList; if (r != null) l.Add(r); }
        return l;
    }
    internal static List<string> Strings(Dictionary<string, object> b, string k) {
        List<string> l = new List<string>(); object v;
        if (b.TryGetValue(k, out v) && v is IEnumerable && !(v is string)) foreach (object o in (IEnumerable)v) l.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
        return l;
    }

    // ---- background jobs: end when they end, when someone stops them, when the grant goes, or with LanTalk ----
    string JobDir { get { return Path.Combine(dir, "jobs"); } }
    void CleanJobs() { // left by an earlier run; nothing refers to them any more
        if (Directory.Exists(JobDir)) foreach (string f in Directory.GetFiles(JobDir, "*.log")) try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    BgJob NewJob(string from, Dictionary<string, object> b) {
        BgJob j = new BgJob(); j.Peer = from; j.Cmd = Util.Str(b, "cmd"); j.Cwd = Util.Str(b, "cwd");
        lock (L) {
            if (jobs.Count(delegate (BgJob x) { return x.Running; }) >= MaxJobs) throw Lang.Err("那台电脑上的后台任务太多（{0} 个），先停掉一些", "too many background jobs there ({0}), stop some first", MaxJobs);
            j.Id = ++lastJob;
        }
        b["job"] = j.Id;
        return j;
    }
    void StartJob(BgJob j, ControlSession s, Dictionary<string, object> b, ChatMsg m) {
        long limit = Math.Max(1, Math.Min(7 * 86400, Util.Num(b, "timeout")));
        j.Claude = Util.Str(b, "op") == "claude";
        ProcessStartInfo si = j.Claude ? ClaudeStart(b) : ShellStart(b);
        Directory.CreateDirectory(JobDir);
        j.File = Path.Combine(JobDir, Util.NewId() + ".log"); j.MsgId = m.Id;
        FileStream log = new FileStream(j.File, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Job box = new Job(true);
        Process p;
        try { p = Process.Start(si); } catch { log.Dispose(); box.Dispose(); throw; }
        box.Add(p);
        j.Start = DateTime.UtcNow; j.Running = true;
        s.Background = true;
        lock (L) {
            jobs.Add(j);
            foreach (BgJob old in jobs.Where(delegate (BgJob x) { return !x.Running; }).Take(Math.Max(0, jobs.Count - KeepJobs)).ToArray()) { jobs.Remove(old); try { File.Delete(old.File); } catch (IOException) { } }
        }
        Run(delegate {
            Msg end = Lang.M("中断", "interrupted");
            try {
                Thread[] readers = new Thread[] { j.Claude ? PourClaude(p.StandardOutput, log, j) : Pour(p.StandardOutput.BaseStream, log), Pour(p.StandardError.BaseStream, log) };
                try { if (j.Claude) { byte[] say = new UTF8Encoding(false).GetBytes(j.Cmd); p.StandardInput.BaseStream.Write(say, 0, say.Length); p.StandardInput.BaseStream.Flush(); } } catch (IOException) { }
                p.StandardInput.Close();
                Msg why = null;
                while (!p.WaitForExit(200)) {
                    if (s.Stopped || stopping) why = Stopped; else if (j.Seconds > limit) why = Lang.M("超时（{0} 秒）", "timed out ({0} s)", limit); else continue;
                    box.Kill();
                    p.WaitForExit(5000);
                    break;
                }
                foreach (Thread t in readers) t.Join(1500);
                j.Code = why != null ? 124 : j.Failed && p.ExitCode == 0 ? 1 : p.ExitCode;
                Msg span = BgJob.Span(j.Seconds);
                end = why ?? new Msg("退出码 " + j.Code + " · " + span.Zh, "exit code " + j.Code + " · " + span.En);
                if (why == null) box.LetGo(); // what it started on purpose goes on
            } catch (Exception e) { j.Code = 1; end = Msg.Of(e); }
            finally {
                lock (log) log.Dispose();
                box.Dispose(); p.Dispose();
                j.End = DateTime.UtcNow; j.Result = end; j.Running = false;
                Finish(m, end);
            }
        });
    }
    static Thread Pour(Stream from, FileStream log) {
        Thread t = new Thread(delegate () {
            byte[] buf = new byte[16384]; int n;
            try {
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                    lock (log) if (log.Length < MaxLog) {
                        log.Write(buf, 0, n);
                        if (log.Length >= MaxLog) { byte[] x = Encoding.UTF8.GetBytes(Lang.T("\n（输出太长，后面的不再记录）\n", "\n(Output too long, the rest is not recorded.)\n")); log.Write(x, 0, x.Length); }
                        log.Flush();
                    }
            } catch (Exception) { } // process gone
        });
        t.IsBackground = true; t.Start();
        return t;
    }
    BgJob FindJob(string from, long id) {
        BgJob j;
        lock (L) j = jobs.Find(delegate (BgJob x) { return x.Id == id && x.Peer == from; });
        if (j == null) throw Lang.Err("没有后台任务 {0}（LanTalk 重启过的话，之前的任务已经结束），用 lt jobs 查看", "no background job {0} (jobs end when LanTalk restarts), see lt jobs", id);
        return j;
    }
    public bool HasJob(string msgId) { lock (L) return jobs.Exists(delegate (BgJob x) { return x.MsgId == msgId; }); }
    // For the window: the recorded output of a job, the end of it when it is long. null when it is gone.
    public string JobOutput(string msgId) {
        BgJob j;
        lock (L) j = jobs.Find(delegate (BgJob x) { return x.MsgId == msgId; });
        if (j == null) return null;
        using (FileStream f = new FileStream(j.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            byte[] b = new byte[(int)Math.Min(f.Length, 200000)];
            f.Position = f.Length - b.Length;
            int n = 0, r; while (n < b.Length && (r = f.Read(b, n, b.Length - n)) > 0) n += r;
            return Encoding.UTF8.GetString(b, 0, n);
        }
    }
    void DoJobs(ControlSession s, string from) {
        BgJob[] mine;
        lock (L) mine = jobs.Where(delegate (BgJob j) { return j.Peer == from; }).ToArray();
        s.Last(new { k = "x", code = 0, err = "", end = "", jobs = mine.Select(delegate (BgJob j) {
            Msg span = BgJob.Span(j.Seconds), end = j.Result ?? new Msg("", "");
            return new { id = j.Id, claude = j.Claude, session = j.Session ?? "", cmd = j.Cmd, cwd = j.Cwd, running = j.Running, code = j.Code, seconds = (int)j.Seconds, span = span.Zh, span_en = span.En, end = end.Zh, end_en = end.En, size = new FileInfo(j.File).Length };
        }).ToArray() });
    }
    void DoStop(ControlSession s, Dictionary<string, object> b, string from) {
        BgJob j = FindJob(from, Util.Num(b, "job"));
        if (!j.Running) throw Lang.Err("任务 {0} 已经结束了", "job {0} has already ended", j.Id);
        ControlSession run;
        lock (L) sessions.TryGetValue(j.MsgId, out run);
        if (run != null) run.Stop();
        for (int i = 0; i < 80 && j.Running; i++) Thread.Sleep(100);
        if (j.Running) throw Lang.Err("任务 {0} 没能停下来", "job {0} would not stop", j.Id);
        s.Last(Done(null, Stopped, 0));
    }
    // from: byte offset to go on at, -1 = the last n lines. follow: until it ends (exit code = the job's) or the timeout (124).
    void DoLog(ControlSession s, Dictionary<string, object> b, string from) {
        BgJob j = FindJob(from, Util.Num(b, "job"));
        long pos = Util.Num(b, "from"); bool follow = Util.Flag(b, "follow");
        long limit = Math.Max(1, Util.Num(b, "timeout"));
        Stopwatch sw = Stopwatch.StartNew();
        int left = MaxOutput, code = 0;
        byte[] buf = new byte[65536];
        using (FileStream f = new FileStream(j.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (pos < 0) pos = TailStart(f, (int)Math.Max(1, Math.Min(5000, Util.Num(b, "n"))));
            f.Position = Math.Min(pos, f.Length);
            while (true) {
                bool running = j.Running; // before reading, so nothing written last is missed
                int n;
                while (left > 0 && (n = f.Read(buf, 0, buf.Length)) > 0) {
                    bool more = running || f.Position < f.Length;
                    int use = more ? WholeUtf8(buf, n) : n;
                    f.Position -= n - use;
                    if (use == 0) break;
                    left -= use;
                    s.SendSafe(new { k = "o", d = Encoding.UTF8.GetString(buf, 0, use) });
                }
                if (left <= 0 || !follow) break;
                if (!running) { code = j.Code; break; }
                if (s.Stopped || stopping) return;
                if (sw.Elapsed.TotalSeconds > limit) { code = 124; break; }
                Thread.Sleep(200);
            }
            Msg span = BgJob.Span(j.Seconds), end = j.Running ? new Msg("运行中 · " + span.Zh, "running · " + span.En) : j.Result;
            s.Last(new { k = "x", code = code, err = "", end = end.Zh, end_en = end.En, from = f.Position, size = f.Length, running = j.Running, session = j.Session ?? "" });
        }
    }
    public static int WholeUtf8(byte[] b, int n) { // how many of the n bytes end on a whole character
        for (int i = n - 1; i >= 0 && i >= n - 4; i--) {
            if ((b[i] & 0xC0) == 0x80) continue;
            int need = b[i] < 0x80 ? 1 : b[i] >= 0xF0 ? 4 : b[i] >= 0xE0 ? 3 : b[i] >= 0xC0 ? 2 : 1;
            return i + need <= n ? n : i;
        }
        return n;
    }
    static long TailStart(FileStream f, int lines) {
        long start = Math.Max(0, f.Length - 1024 * 1024);
        byte[] b = new byte[(int)(f.Length - start)];
        f.Position = start;
        int n = 0, r; while (n < b.Length && (r = f.Read(b, n, b.Length - n)) > 0) n += r;
        int i = n - 1;
        if (i >= 0 && b[i] == 10) i--; // the last line's own end
        for (; i >= 0; i--) if (b[i] == 10 && --lines == 0) return start + i + 1;
        if (start == 0) return 0;
        i = 0; while (i < n && (b[i] & 0xC0) == 0x80) i++; // not in the middle of a character
        return start + i;
    }

    // ---- screenshots ----
    Msg DoShot(ControlSession s, Channel ch, Shot shot) {
        byte[] png = shot.Take();
        string sha = Sha(new MemoryStream(png));
        s.Last(new { k = "data", size = png.Length, sha = sha, what = shot.What.Zh, what_en = shot.What.En });
        for (int i = 0; i < png.Length; i += Chunk) {
            if (s.Stopped || stopping) throw new LtException(Stopped);
            byte[] part = new byte[Math.Min(Chunk, png.Length - i)];
            Buffer.BlockCopy(png, i, part, 0, part.Length);
            ch.Send(part, part.Length);
        }
        return Lang.M("已截图 · {0}", "taken · {0}", Util.SizeText(png.Length));
    }
    Msg DoWindows(ControlSession s) {
        s.Last(new { k = "x", code = 0, err = "", end = "", windows = Shot.Windows().Select(delegate (Shot.Win w) { return new { id = "0x" + w.H.ToInt64().ToString("X"), process = w.Process, title = w.Title, width = w.R.Width, height = w.R.Height, minimized = w.Minimized }; }).ToArray(),
            monitors = Shot.Monitors().Select(delegate (Rectangle r) { return new { width = r.Width, height = r.Height, x = r.X, y = r.Y }; }).ToArray() });
        return Lang.M("已列出", "listed");
    }

    // ---- text files ----
    class TextFile {
        public string Path, Text;
        public Encoding Enc;
        public int Bom;
        byte[] raw;
        public TextFile(string path) {
            Path = path;
            if (!File.Exists(path)) throw Lang.Err("那台电脑上没有这个文件：{0}", "no such file on that PC: {0}", path);
            if (new FileInfo(path).Length > MaxText20) throw Lang.Err("文件太大，请用 lt get 取回", "the file is too big, fetch it with lt get");
            raw = ReadAll(path);
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) { Enc = new UTF8Encoding(false, true); Bom = 3; }
            else if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) { Enc = new UnicodeEncoding(false, false, true); Bom = 2; }
            else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) { Enc = new UnicodeEncoding(true, false, true); Bom = 2; }
            else if (Array.IndexOf(raw, (byte)0, 0, Math.Min(raw.Length, 8000)) >= 0) throw Lang.Err("这不是文本文件：{0}", "this is not a text file: {0}", path);
            else Enc = new UTF8Encoding(false, true);
            try { Text = Enc.GetString(raw, Bom, raw.Length - Bom); }
            catch (DecoderFallbackException) {
                Enc = Encoding.GetEncoding(Encoding.Default.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                bool ok = false;
                try { Text = Enc.GetString(raw); ok = Enc.GetBytes(Text).SequenceEqual(raw); } catch (DecoderFallbackException) { } catch (EncoderFallbackException) { } // must survive the way back
                if (!ok) throw Lang.Err("认不出这个文件的编码：{0}", "can't tell the encoding of this file: {0}", path);
            }
        }
        static byte[] ReadAll(string path) {
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                byte[] b = new byte[f.Length]; int n = 0, r;
                while (n < b.Length && (r = f.Read(b, n, b.Length - n)) > 0) n += r;
                return b;
            }
        }
        public void Save() {
            byte[] body;
            try { body = Enc.GetBytes(Text); } catch (EncoderFallbackException) { throw Lang.Err("这个文件是 {0} 编码，存不下新内容里的一些字符", "this file is {0}, which can't hold some of the new characters", Enc.WebName); }
            string tmp = Path + ".lt-" + Util.NewId().Substring(0, 8) + ".tmp";
            try {
                using (FileStream f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(raw, 0, Bom); f.Write(body, 0, body.Length); }
                if (!ReadAll(Path).SequenceEqual(raw)) throw Lang.Err("文件刚被别的程序改了，没有动它：{0}", "another program has just changed the file, left alone: {0}", Path);
                Util.Replace(tmp, Path);
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
    Msg DoCat(ControlSession s, Dictionary<string, object> b) {
        TextFile t = new TextFile(RemotePath(b));
        string[] lines = t.Text.Replace("\r\n", "\n").Split('\n');
        int total = lines.Length > 0 && lines[lines.Length - 1].Length == 0 ? lines.Length - 1 : lines.Length;
        int first = (int)Math.Max(1, Math.Min(int.MaxValue, Util.Num(b, "from"))), n = (int)Math.Max(1, Math.Min(100000, Util.Num(b, "n"))), last = (int)Math.Min(total, (long)first + n - 1), left = MaxOutput;
        bool number = Util.Flag(b, "number");
        StringBuilder o = new StringBuilder();
        for (int i = first; i <= last; i++) {
            if (number) o.Append(i.ToString().PadLeft(6)).Append('\t');
            o.Append(lines[i - 1]).Append('\n');
            if (o.Length > left) { last = i - 1; break; }
            if (o.Length > 100000) { left -= o.Length; s.Send(new { k = "o", d = o.ToString() }); o.Clear(); }
        }
        if (o.Length > 0) s.Send(new { k = "o", d = Util.Cut(o.ToString(), Math.Max(0, left)) });
        bool utf8 = t.Enc is UTF8Encoding;
        if (first > 1 || last < total || !utf8) s.Send(new { k = "e", d = "（第 " + first + "–" + last + " 行，共 " + total + " 行" + (utf8 ? "" : "，编码 " + t.Enc.WebName) + "）\n", d_en = "(Lines " + first + "-" + last + " of " + total + (utf8 ? "" : ", encoding " + t.Enc.WebName) + ")\n" });
        Msg end = Lang.M("已读取 · {0} 行", "read · {0} lines", Math.Max(0, last - first + 1));
        s.Last(Done(null, end, 0));
        return end;
    }
    // What an edit does, worked out before anyone is asked: whole lines with their numbers, one line of context around them.
    class EditPlan {
        public readonly string Text;
        public readonly int Count;
        public readonly List<int> At = new List<int>();
        readonly TextFile file;
        readonly string result;
        public EditPlan(Dictionary<string, object> b) {
            file = new TextFile(RemotePath(b));
            bool all = Util.Flag(b, "all");
            string text = file.Text;
            StringBuilder show = new StringBuilder();
            foreach (Dictionary<string, object> e in Dicts(b, "edits")) {
                Count++;
                string o = Util.Str(e, "o").Replace("\r\n", "\n"), n = Util.Str(e, "n").Replace("\r\n", "\n");
                if (o.Length == 0) throw Lang.Err("第 {0} 处的原文是空的", "edit {0} has no old text", Count);
                if (text.Contains("\r\n")) { string oc = o.Replace("\n", "\r\n"); if (text.Contains(oc) || !text.Contains(o)) { o = oc; n = n.Replace("\n", "\r\n"); } }
                int first = text.IndexOf(o, StringComparison.Ordinal), hits = 0;
                for (int i = first; i >= 0; i = text.IndexOf(o, i + o.Length, StringComparison.Ordinal)) hits++;
                if (hits == 0) throw Lang.Err("第 {0} 处的原文在文件里找不到（空格和缩进要完全一致），什么都没改", "edit {0}: the old text isn't in the file (spaces and indentation must match exactly). Nothing was changed", Count);
                if (hits > 1 && !all) throw Lang.Err("第 {0} 处的原文出现了 {1} 次，多带几行上下文，或加 --all。什么都没改", "edit {0}: the old text appears {1} times. Include more lines around it, or use --all. Nothing was changed", Count, hits);
                At.Add(Line(text, first));
                for (int i = first; i >= 0; i = text.IndexOf(o, i + o.Length, StringComparison.Ordinal)) Show(show, text, i, o, n);
                text = text.Replace(o, n);
            }
            if (Count == 0) throw Lang.Err("没有要改的内容", "nothing to edit");
            result = text; Text = show.ToString();
        }
        static int Line(string text, int at) { int n = 1; for (int i = 0; i < at; i++) if (text[i] == '\n') n++; return n; }
        static void Show(StringBuilder s, string text, int at, string o, string n) {
            int from = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1, end = text.IndexOf('\n', at + o.Length);
            if (o.EndsWith("\n") && at + o.Length <= text.Length) end = at + o.Length - 1; // ends with its line: the next one isn't touched
            if (end < 0) end = text.Length;
            string head = text.Substring(from, at - from), tail = end > at + o.Length ? text.Substring(at + o.Length, end - at - o.Length) : "";
            List<string> a = Lines(head + o + tail), z = Lines(head + n + tail);
            int line = Line(text, from), top = 0, low = 0;
            while (top < a.Count && top < z.Count && a[top] == z[top]) top++;
            while (low < a.Count - top && low < z.Count - top && a[a.Count - 1 - low] == z[z.Count - 1 - low]) low++;
            string[] every = text.Replace("\r\n", "\n").Split('\n');
            int l = line + top;
            if (s.Length > 0) s.Append('\n');
            if (l >= 2) One(s, ' ', l - 1, every[l - 2]);
            for (int i = top; i < a.Count - low; i++) One(s, '-', line + i, a[i]);
            for (int i = top; i < z.Count - low; i++) One(s, '+', line + i, z[i]);
            int next = line + a.Count - low;
            if (next <= every.Length && !(next == every.Length && every[next - 1].Length == 0)) One(s, ' ', next, every[next - 1]);
        }
        static List<string> Lines(string t) { t = t.Replace("\r\n", "\n"); List<string> l = new List<string>(t.TrimEnd('\r').Split('\n')); if (l.Count > 0 && t.EndsWith("\n")) l.RemoveAt(l.Count - 1); if (t.Length == 0) l.Clear(); return l; }
        static void One(StringBuilder s, char sign, int line, string text) { s.Append(s.Length > 0 && s[s.Length - 1] != '\n' ? "\n" : "").Append(sign).Append(line.ToString().PadLeft(5)).Append("  ").Append(text); }
        public void Save() { if (result != file.Text) { file.Text = result; file.Save(); } }
    }
    Msg DoEdit(ControlSession s, EditPlan p) {
        p.Save();
        string where = string.Join(", ", p.At.Select(delegate (int i) { return i.ToString(); }).ToArray());
        Msg end = Lang.M("已修改 · {0} 处 · 第 {1} 行起", "edited · {0} · from line {1}", p.Count, where);
        s.Last(Done(null, end, 0));
        return end;
    }

    // ---- folder sync: receiving side of a push, sending side of a pull ----
    Msg DoPush(ControlSession s, Channel ch, PushPlan p) {
        if (!p.Dry) {
            s.StopPing();
            s.Send(new { k = "need", list = p.Need });
            foreach (int i in p.Need) {
                SyncFile f = p.Files[i];
                string dest = p.Dest(f.P), seen = p.Stamp[f.P];
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                ReceiveTo(dest, true, f.S, f.H, delegate { if (s.Stopped || stopping) throw new LtException(Stopped); return ch.Receive(); }, delegate { if (Sync.Stamp(dest) != seen) throw Lang.Err("{0} 刚被别的程序改了，同步停在这里", "another program has just changed {0}, the sync stops here", f.P); });
            }
            foreach (string f in p.Del) Sync.Recycle(p.Dest(f));
        }
        Msg end = Sync.Summary(p.Add.Count, p.Upd.Count, p.Del.Count, p.Conf.Count, p.Same.Count, p.Dry);
        s.Last(new { k = "x", code = 0, err = "", end = end.Zh, end_en = end.En, add = p.Add, upd = p.Upd, del = p.Del, absent = p.Absent, conf = p.Conf, lost = p.Lost });
        return end;
    }
    Msg DoPull(ControlSession s, Channel ch, Dictionary<string, object> b) {
        string root = RemotePath(b);
        if (!Directory.Exists(root)) throw Lang.Err("没有这个文件夹：{0}", "no such folder: {0}", root);
        List<SyncFile> files = Sync.Scan(root, Strings(b, "skip"), delegate { return s.Stopped || stopping; });
        s.StopPing();
        s.Send(new { k = "list", files = files.Select(delegate (SyncFile f) { return new object[] { f.P, f.S, f.H }; }).ToArray() });
        List<string> need = Strings(ch.ReceiveJson(), "need");
        long sum = 0;
        foreach (string n in need) {
            int i = int.Parse(n, CultureInfo.InvariantCulture);
            Sync.SendFile(ch, Path.Combine(root, files[i].P.Replace('/', '\\')), files[i], delegate { if (s.Stopped || stopping) throw new LtException(Stopped); });
            sum += files[i].S;
        }
        Msg end = Lang.M("已读取 · {0} 个文件 · {1}", "read · {0} files · {1}", need.Count, Util.SizeText(sum));
        s.Last(Done(null, end, 0));
        return end;
    }

    // Asking side of both directions. Remembers what both sides had in common after the last sync, so that a file changed
    // over there since is never overwritten silently.
    class SyncJob {
        readonly string local, stateFile;
        readonly bool push, force, del, dry;
        readonly List<SyncFile> mine;
        readonly Dictionary<string, string> common = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> skip;
        List<string> add = new List<string>(), upd = new List<string>(), gone = new List<string>(), conf = new List<string>(), absent = new List<string>(), lost = new List<string>(); // lost: in conf, because the destination deleted it
        List<SyncFile> theirs;
        int same;
        public Msg Summary = Lang.M("中断", "interrupted");
        public SyncJob(Node node, string peer, Dictionary<string, object> b, string dir) {
            local = Path.GetFullPath(dir).TrimEnd('\\'); push = Util.Str(b, "op") == "push"; force = Util.Flag(b, "force"); del = Util.Flag(b, "delete"); dry = Util.Flag(b, "dry");
            if (local.Length < 3) throw Lang.Err("不能同步整个磁盘", "can't sync a whole drive");
            if (push && !Directory.Exists(local)) throw Lang.Err("没有这个文件夹：{0}", "no such folder: {0}", local);
            skip = Strings(b, "skip");
            stateFile = Path.Combine(Path.Combine(node.dir, "sync"), BitConverter.ToString(Crypto.Hmac(new byte[1], Encoding.UTF8.GetBytes(peer + "|" + local.ToLowerInvariant() + "|" + Util.Str(b, "path").Replace('/', '\\').TrimEnd('\\').ToLowerInvariant())), 0, 12).Replace("-", "") + ".json");
            if (File.Exists(stateFile)) try { foreach (KeyValuePair<string, string> kv in Util.FromJson<Dictionary<string, string>>(File.ReadAllText(stateFile, Encoding.UTF8))) common[kv.Key] = kv.Value; } catch (ArgumentException) { }
            mine = Directory.Exists(local) ? Sync.Scan(local, skip, delegate { return false; }) : new List<SyncFile>();
            if (push) {
                HashSet<string> here = new HashSet<string>(mine.Select(delegate (SyncFile f) { return f.P; }), StringComparer.OrdinalIgnoreCase);
                b["files"] = mine.Select(delegate (SyncFile f) { return new object[] { f.P, f.S, f.H, Common(f.P) }; }).ToArray();
                b["gone"] = del ? common.Where(delegate (KeyValuePair<string, string> kv) { return !here.Contains(kv.Key) && !Sync.SkippedPath(kv.Key, skip); }).Select(delegate (KeyValuePair<string, string> kv) { return new object[] { kv.Key, kv.Value }; }).ToArray() : new object[0];
            }
        }
        string Common(string p) { string h; return common.TryGetValue(p, out h) ? h : ""; }
        public void Send(Channel ch, Dictionary<string, object> f, Action<object> emit) {
            foreach (string n in Strings(f, "list")) {
                SyncFile x = mine[int.Parse(n, CultureInfo.InvariantCulture)];
                Sync.SendFile(ch, Path.Combine(local, x.P.Replace('/', '\\')), x, delegate { emit(new { ok = true, k = "p" }); });
            }
        }
        public void Choose(Channel ch, Dictionary<string, object> f, Action<object> emit) {
            Dictionary<string, SyncFile> here = new Dictionary<string, SyncFile>(StringComparer.OrdinalIgnoreCase);
            foreach (SyncFile x in mine) here[x.P] = x;
            theirs = Sync.Files(Rows(f, "files"));
            HashSet<string> there = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<int> need = new List<int>();
            Dictionary<string, string> stamp = new Dictionary<string, string>();
            for (int i = 0; i < theirs.Count; i++) {
                SyncFile x = theirs[i], own;
                there.Add(x.P);
                here.TryGetValue(x.P, out own);
                int d = Sync.Decide(x.H, own == null ? null : own.H, Common(x.P), force);
                if (d == 0) { same++; continue; }
                if (d == 3) { conf.Add(x.P); if (own == null) lost.Add(x.P); continue; }
                (d == 1 ? add : upd).Add(x.P); need.Add(i);
                stamp[x.P] = Sync.Stamp(Path.Combine(local, x.P.Replace('/', '\\')));
            }
            if (del) foreach (KeyValuePair<string, string> kv in common) {
                SyncFile own;
                if (there.Contains(kv.Key) || Sync.SkippedPath(kv.Key, skip)) continue;
                if (!here.TryGetValue(kv.Key, out own)) absent.Add(kv.Key);
                else if (own.H == kv.Value) gone.Add(kv.Key);
                else conf.Add(kv.Key);
            }
            if (dry) need.Clear();
            ch.SendJson(new { need = need });
            foreach (int i in need) {
                SyncFile x = theirs[i];
                string dest = Path.Combine(local, x.P.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                ReceiveTo(dest, true, x.S, x.H, delegate { byte[] d = ch.Receive(); emit(new { ok = true, k = "p" }); return d; }, delegate { if (Sync.Stamp(dest) != stamp[x.P]) throw Lang.Err("{0} 刚被别的程序改了，同步停在这里", "another program has just changed {0}, the sync stops here", x.P); });
            }
            if (!dry) foreach (string p in gone) Sync.Recycle(Path.Combine(local, p.Replace('/', '\\')));
        }
        // Why each file was left alone. Only --force on a file changed at the destination loses somebody's work, so it says whose.
        string Conflicts(List<SyncFile> source) {
            Dictionary<string, string> src = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SyncFile x in source) src[x.P] = x.H;
            HashSet<string> gonethere = new HashSet<string>(lost, StringComparer.OrdinalIgnoreCase);
            List<string>[] g = new List<string>[5];
            for (int i = 0; i < g.Length; i++) g[i] = new List<string>();
            foreach (string p in conf) {
                string h; src.TryGetValue(p, out h);
                g[h == null ? 4 : gonethere.Contains(p) ? 3 : Common(p) == "" ? 2 : h == Common(p) ? 0 : 1].Add(p);
            }
            string[] t = {
                push ? Lang.T("没有动，只有那边改过（取回来用 --pull；--force 会盖掉那边的改动）：", "Left alone, changed only over there (--pull brings it here; --force would lose that change):")
                     : Lang.T("没有动，只有本机改过（推过去就不加 --pull；--force 会盖掉本机的改动）：", "Left alone, changed only here (without --pull it goes there; --force would lose that change):"),
                push ? Lang.T("没有动，两边都改过（先看两边的内容；--force 会盖掉那边的改动）：", "Left alone, changed on both sides (look at both; --force would lose the change over there):")
                     : Lang.T("没有动，两边都改过（先看两边的内容；--force 会盖掉本机的改动）：", "Left alone, changed on both sides (look at both; --force would lose the change here):"),
                push ? Lang.T("没有动，以前没同步过，两边内容不同（先看两边的内容；--force 会盖掉那边的文件）：", "Left alone, never synced before and different (look at both; --force would replace the file over there):")
                     : Lang.T("没有动，以前没同步过，两边内容不同（先看两边的内容；--force 会盖掉本机的文件）：", "Left alone, never synced before and different (look at both; --force would replace the file here):"),
                push ? Lang.T("没有传，那边已经删掉（要重新放回去就加 --force）：", "Not sent, deleted over there (--force puts it back):")
                     : Lang.T("没有取，本机已经删掉（要重新取回来就加 --force）：", "Not fetched, deleted here (--force brings it back):"),
                push ? Lang.T("没有删，本机已经删掉，但那边改过：", "Not deleted: gone here, but changed over there:")
                     : Lang.T("没有删，那边已经删掉，但本机改过：", "Not deleted: gone over there, but changed here:"),
            };
            StringBuilder o = new StringBuilder();
            for (int i = 0; i < g.Length; i++) {
                if (g[i].Count == 0) continue;
                o.Append(t[i]).Append('\n').Append(string.Join("\n", g[i].Take(200).ToArray())).Append('\n');
                if (g[i].Count > 200) o.Append(Lang.T("…还有 {0} 个", "...and {0} more", g[i].Count - 200)).Append('\n');
            }
            return o.ToString();
        }
        // Prints what happened and remembers the common state. Exit code 1 when something was left alone, see Conflicts.
        public int Finish(Dictionary<string, object> f, Action<object> emit) {
            List<SyncFile> source = push ? mine : theirs;
            if (push) { add = Strings(f, "add"); upd = Strings(f, "upd"); gone = Strings(f, "del"); absent = Strings(f, "absent"); conf = Strings(f, "conf"); lost = Strings(f, "lost"); same = source.Count(delegate (SyncFile x) { return !add.Contains(x.P) && !upd.Contains(x.P) && !conf.Contains(x.P); }); }
            StringBuilder o = new StringBuilder();
            foreach (KeyValuePair<string, List<string>> g in new Dictionary<string, List<string>> { { Lang.T("新增 ", "new      "), add }, { Lang.T("更新 ", "updated  "), upd }, { Lang.T("删除 ", "deleted  "), gone } }) {
                foreach (string p in g.Value.Take(200)) o.Append(g.Key).Append(p).Append('\n');
                if (g.Value.Count > 200) o.Append(g.Key).Append(Lang.T("…还有 {0} 个", "...and {0} more", g.Value.Count - 200)).Append('\n');
            }
            Summary = Sync.Summary(add.Count, upd.Count, gone.Count, conf.Count, same, dry);
            o.Append(Summary.ToString()).Append('\n');
            emit(new { ok = true, k = "o", d = o.ToString() });
            if (conf.Count > 0) emit(new { ok = true, k = "e", d = Conflicts(source) });
            if (!dry) {
                HashSet<string> skipped = new HashSet<string>(conf, StringComparer.OrdinalIgnoreCase);
                foreach (SyncFile x in source) if (!skipped.Contains(x.P)) common[x.P] = x.H;
                foreach (string p in gone.Concat(absent)) common.Remove(p);
                Directory.CreateDirectory(Path.GetDirectoryName(stateFile));
                File.WriteAllText(stateFile, Util.ToJson(common), new UTF8Encoding(false));
            }
            return conf.Count > 0 ? 1 : 0;
        }
    }

    // What a push would do here, found out before anyone is asked.
    class PushPlan {
        public readonly string Root;
        public readonly bool Dry;
        public readonly List<SyncFile> Files;
        public readonly List<int> Need = new List<int>();
        public readonly List<string> Add = new List<string>(), Upd = new List<string>(), Del = new List<string>(), Conf = new List<string>(), Same = new List<string>(), Absent = new List<string>(), Lost = new List<string>();
        public readonly Dictionary<string, string> Stamp = new Dictionary<string, string>();
        public string Dest(string p) { return Path.Combine(Root, p.Replace('/', '\\')); }
        public PushPlan(Dictionary<string, object> b, Func<bool> stop) {
            Root = RemotePath(b).TrimEnd('\\'); Dry = Util.Flag(b, "dry");
            if (Root.Length < 3) throw Lang.Err("不能同步整个磁盘", "can't sync a whole drive");
            if (File.Exists(Root)) throw Lang.Err("那里是一个文件：{0}", "that is a file: {0}", Root);
            List<IList> rows = Rows(b, "files");
            Files = Sync.Files(rows);
            bool force = Util.Flag(b, "force");
            long size = 0;
            for (int i = 0; i < Files.Count; i++) {
                if (stop()) throw new LtException(Stopped);
                SyncFile f = Files[i];
                string dest = Dest(f.P), now = File.Exists(dest) ? Sync.Hash(dest) : null;
                if (Directory.Exists(dest)) throw Lang.Err("那里是一个文件夹：{0}", "that is a folder: {0}", dest);
                int d = Sync.Decide(f.H, now, rows[i].Count > 3 ? Convert.ToString(rows[i][3]) : "", force);
                (d == 0 ? Same : d == 1 ? Add : d == 2 ? Upd : Conf).Add(f.P);
                if (d == 3 && now == null) Lost.Add(f.P);
                if (d == 1 || d == 2) { Need.Add(i); Stamp[f.P] = Sync.Stamp(dest); size += f.S; }
            }
            foreach (IList r in Rows(b, "gone")) {
                string p = Sync.Check(Convert.ToString(r[0])), dest = Dest(p);
                if (!File.Exists(dest)) Absent.Add(p);
                else if (Sync.Hash(dest) == Convert.ToString(r[1])) Del.Add(p);
                else Conf.Add(p);
            }
            StringBuilder o = new StringBuilder(Sync.Summary(Add.Count, Upd.Count, Del.Count, Conf.Count, Same.Count, Dry).ToString() + " · " + Util.SizeText(size));
            foreach (KeyValuePair<string, List<string>> g in new Dictionary<string, List<string>> { { "+ ", Add }, { "~ ", Upd }, { "- ", Del } }) {
                foreach (string p in g.Value.Take(100)) o.Append('\n').Append(g.Key).Append(p);
                if (g.Value.Count > 100) o.Append('\n').Append(g.Key).Append(Lang.T("…还有 {0} 个", "...and {0} more", g.Value.Count - 100));
            }
            b["sum"] = o.ToString();
        }
    }

    // ---- facts about this PC ----
    Msg DoInfo(ControlSession s) {
        s.Last(new { k = "x", code = 0, err = "", end = "", info = Facts.All().Select(delegate (Fact f) { return new { key = f.Key, name = f.Name.Zh, name_en = f.Name.En, value = f.Value.Zh, value_en = f.Value.En }; }).ToArray() });
        return Lang.M("已查看", "looked up");
    }
}

class SyncFile { public string P, H; public long S; } // P: relative, with /

static class Sync {
    public const int Max = 20000;
    // source, destination and last common content: 0 same, 1 new, 2 update, 3 left alone, the destination changed or deleted it, or was never in sync
    public static int Decide(string src, string dst, string common, bool force) {
        if (dst == src) return 0;
        if (dst == null) return common == "" || force ? 1 : 3; // deleted over there since
        return dst == common || force ? 2 : 3;
    }
    public static Msg Summary(int add, int upd, int del, int conf, int same, bool dry) {
        return new Msg((dry ? "只比较：" : "") + "新增 " + add + "，更新 " + upd + "，删除 " + del + "，没变 " + same + (conf > 0 ? "，冲突 " + conf : ""),
            (dry ? "Compare only: " : "") + add + " new, " + upd + " updated, " + del + " deleted, " + same + " unchanged" + (conf > 0 ? ", " + conf + " conflicts" : ""));
    }
    static bool Match(string name, string pattern) { return Regex.IsMatch(name, "\\A" + Regex.Escape(pattern).Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "\\z", RegexOptions.IgnoreCase); }
    static bool Skipped(string rel, List<string> skip) {
        string name = rel.Substring(rel.LastIndexOf('/') + 1);
        if (Regex.IsMatch(name, "\\.lt-[0-9a-f]{8}\\.tmp\\z")) return true;
        return skip.Exists(delegate (string s) { string p = s.Replace('\\', '/').Trim('/'); return Match(p.Contains("/") ? rel : name, p); });
    }
    public static bool SkippedPath(string rel, List<string> skip) {
        for (int i = rel.IndexOf('/'); i >= 0; i = rel.IndexOf('/', i + 1)) if (Skipped(rel.Substring(0, i), skip)) return true;
        return Skipped(rel, skip);
    }
    public static List<SyncFile> Scan(string root, List<string> skip, Func<bool> stop) {
        List<SyncFile> l = new List<SyncFile>();
        Walk(root, "", skip, stop, l);
        return l;
    }
    static void Walk(string dir, string rel, List<string> skip, Func<bool> stop, List<SyncFile> l) {
        foreach (string f in Directory.GetFiles(dir)) {
            string p = rel + Path.GetFileName(f);
            if (Skipped(p, skip)) continue;
            if (stop()) throw Lang.Err("已停止", "stopped");
            if (l.Count >= Max) throw Lang.Err("文件太多（超过 {0} 个），请缩小范围或用 --skip", "too many files (over {0}), pick a smaller folder or use --skip", Max);
            SyncFile x = new SyncFile(); x.P = p; x.S = new FileInfo(f).Length; x.H = Hash(f); l.Add(x);
        }
        foreach (string d in Directory.GetDirectories(dir)) {
            string p = rel + Path.GetFileName(d);
            if (Skipped(p, skip) || (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; // junctions could loop
            Walk(d, p + "/", skip, stop, l);
        }
    }
    public static string Hash(string f) {
        using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
        using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan)) return Convert.ToBase64String(h.ComputeHash(fs));
    }
    public static string Stamp(string f) { FileInfo i = new FileInfo(f); return i.Exists ? i.Length + "|" + i.LastWriteTimeUtc.Ticks : ""; }
    // A path from the other PC: must stay inside the folder.
    public static string Check(string p) {
        if (p.Length == 0 || p.Contains("\\") || Util.SafeRelative(p).Replace('\\', '/') != p) throw Lang.Err("不支持这个文件名：{0}", "can't handle this file name: {0}", p);
        return p;
    }
    public static List<SyncFile> Files(List<IList> rows) {
        List<SyncFile> l = new List<SyncFile>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count > Max) throw Lang.Err("文件列表无效", "invalid file list");
        foreach (IList r in rows) {
            SyncFile f = new SyncFile();
            if (r.Count < 3) throw Lang.Err("文件列表无效", "invalid file list");
            f.P = Check(Convert.ToString(r[0])); f.S = Convert.ToInt64(r[1]); f.H = Convert.ToString(r[2]);
            if (f.S < 0 || !seen.Add(f.P)) throw Lang.Err("文件列表无效", "invalid file list");
            l.Add(f);
        }
        return l;
    }
    public static void SendFile(Channel ch, string path, SyncFile f, Action tick) {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan)) {
            byte[] buf = new byte[256 * 1024]; long left = f.S;
            while (left > 0) {
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) throw Lang.Err("同步过程中文件变了：{0}", "the file changed during the sync: {0}", f.P);
                ch.Send(buf, n); left -= n;
                tick();
            }
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct FileOp { public IntPtr Wnd; public uint Func; public string From, To; public ushort Flags; public bool Aborted; public IntPtr Map; public string Title; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref FileOp op);
    public static void Recycle(string path) {
        FileOp op = new FileOp(); op.Func = 3; op.From = path + "\0"; op.Flags = 0x40 | 0x10 | 0x4 | 0x400; // delete; undoable, no questions, silent, no error boxes
        int rc = SHFileOperation(ref op);
        if (rc != 0 || File.Exists(path)) throw Lang.Err("删不掉：{0}", "can't delete: {0}", path);
    }
}

// What to take a picture of. Works in real pixels on every monitor.
class Shot {
    public class Win { public IntPtr H; public string Title, Process; public Rectangle R; public bool Minimized; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; public Rectangle Rect { get { return Rectangle.FromLTRB(L, T, R, B); } } }
    delegate bool EnumWin(IntPtr h, IntPtr l);
    delegate bool EnumMon(IntPtr m, IntPtr dc, ref RECT r, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWin f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMon f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr d);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int n);

    public static T RealPixels<T>(Func<T> f) {
        IntPtr old = IntPtr.Zero;
        try { old = SetThreadDpiAwarenessContext((IntPtr)(-4)); if (old == IntPtr.Zero) old = SetThreadDpiAwarenessContext((IntPtr)(-3)); } catch (EntryPointNotFoundException) { } // Windows 10 before 1703
        try { return f(); } finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }
    public static bool Locked { get { IntPtr d = OpenInputDesktop(0, false, 1); if (d == IntPtr.Zero) return true; CloseDesktop(d); return false; } } // lock screen, UAC question, signed out
    static Rectangle Virtual() { return new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); }
    public static List<Rectangle> Monitors() {
        return RealPixels(delegate {
            List<Rectangle> l = new List<Rectangle>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr m, IntPtr dc, ref RECT r, IntPtr p) { l.Add(r.Rect); return true; }, IntPtr.Zero);
            return l.OrderBy(delegate (Rectangle r) { return r.X == 0 && r.Y == 0 ? 0 : 1; }).ThenBy(delegate (Rectangle r) { return r.X; }).ToList(); // the main one first
        });
    }
    static Rectangle Frame(IntPtr h) { RECT r; if (DwmGetWindowAttribute(h, 9, out r, 16) != 0 || r.R <= r.L) GetWindowRect(h, out r); return r.Rect; } // without the invisible border
    public static List<Win> Windows() {
        return RealPixels(delegate {
            List<Win> l = new List<Win>();
            EnumWindows(delegate (IntPtr h, IntPtr p) {
                int cloaked; uint pid;
                if (!IsWindowVisible(h) || DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                StringBuilder t = new StringBuilder(300); GetWindowText(h, t, 300);
                if (t.Length == 0) return true;
                Win w = new Win(); w.H = h; w.Title = t.ToString(); w.Minimized = IsIconic(h); w.R = Frame(h);
                if (w.R.Width < 8 || w.R.Height < 8 || w.Title == "Program Manager") return true;
                GetWindowThreadProcessId(h, out pid);
                try { using (Process q = Process.GetProcessById((int)pid)) w.Process = q.ProcessName; } catch (ArgumentException) { w.Process = ""; } catch (InvalidOperationException) { w.Process = ""; }
                l.Add(w);
                return true;
            }, IntPtr.Zero);
            return l;
        });
    }

    public static Rectangle FrameOf(IntPtr h) { return RealPixels(delegate { return IsWindow(h) && !IsIconic(h) ? Frame(h) : Rectangle.Empty; }); }
    public static Rectangle Outer(IntPtr h) { return RealPixels(delegate { RECT r; return GetWindowRect(h, out r) ? r.Rect : Rectangle.Empty; }); }
    public static int MonitorOf(Rectangle r) { // number of the monitor most of it is on
        List<Rectangle> m = Monitors(); int best = 0;
        for (int i = 1; i < m.Count; i++) { Rectangle a = Rectangle.Intersect(m[i], r), b = Rectangle.Intersect(m[best], r); if ((long)a.Width * a.Height > (long)b.Width * b.Height) best = i; }
        return best + 1;
    }
    public static Win Find(string w) {
        List<Win> all = Windows();
        Win hit = all.Find(delegate (Win x) { return "0x" + x.H.ToInt64().ToString("X") == w.ToUpperInvariant().Replace("0X", "0x"); })
            ?? all.Find(delegate (Win x) { return string.Equals(x.Process, Regex.Replace(w, "\\.exe$", "", RegexOptions.IgnoreCase), StringComparison.OrdinalIgnoreCase) && !x.Minimized; })
            ?? all.Find(delegate (Win x) { return x.Title.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 && !x.Minimized; })
            ?? all.Find(delegate (Win x) { return x.Title.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(x.Process, w, StringComparison.OrdinalIgnoreCase); });
        if (hit == null) throw Lang.Err("没有标题或程序名是“{0}”的窗口，用 lt shot --list 查看", "no window with the title or program \"{0}\", see lt shot --list", w);
        return hit;
    }

    public Msg What;
    public PicView View; // of the last Take
    Rectangle area;
    readonly IntPtr window;
    readonly int screen, max;
    public Shot(Dictionary<string, object> b) {
        string w = Util.Str(b, "window"); screen = (int)Util.Num(b, "screen"); max = (int)Util.Num(b, "max");
        if (Locked) throw Lang.Err("那台电脑锁屏了（或正在等待管理员确认），截不到画面", "that PC is locked (or showing an administrator prompt), there is nothing to see");
        if (w != "") {
            Win hit = Find(w);
            if (hit.Minimized) throw Lang.Err("窗口“{0}”最小化了，截不到", "the window \"{0}\" is minimized, there is nothing to see", hit.Title);
            window = hit.H;
            What = Lang.M("窗口“{0}”（{1}），{2} × {3}", "window \"{0}\" ({1}), {2} x {3}", hit.Title, hit.Process, hit.R.Width, hit.R.Height);
        } else {
            List<Rectangle> m = Monitors();
            if (screen > m.Count) throw Lang.Err("那台电脑只有 {0} 台显示器", "that PC has only {0} monitor(s)", m.Count);
            Rectangle r = screen > 0 ? m[screen - 1] : RealPixels(delegate { return Virtual(); });
            What = screen > 0 ? Lang.M("显示器 {0}，{1} × {2}", "monitor {0}, {1} x {2}", screen, r.Width, r.Height) : Lang.M("整个屏幕（{0} 台显示器），{1} × {2}", "the whole screen ({0} monitor(s)), {1} x {2}", m.Count, r.Width, r.Height);
        }
    }
    public byte[] Take() {
        if (Locked) throw Lang.Err("那台电脑锁屏了（或正在等待管理员确认），截不到画面", "that PC is locked (or showing an administrator prompt), there is nothing to see");
        long at = Util.Now();
        using (Bitmap full = RealPixels(delegate { return Grab(); })) {
            bool black = window != IntPtr.Zero && AllBlack(full);
            int w = full.Width, h = full.Height, side = Math.Max(w, h);
            if (max > 0 && side > max) { double k = max / (double)side; w = Math.Max(1, (int)(w * k)); h = Math.Max(1, (int)(h * k)); }
            using (MemoryStream s = new MemoryStream()) {
                if (w == full.Width) full.Save(s, ImageFormat.Png);
                else using (Bitmap small = new Bitmap(w, h, PixelFormat.Format24bppRgb)) {
                    using (Graphics g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(full, new Rectangle(0, 0, w, h)); }
                    small.Save(s, ImageFormat.Png);
                }
                if (w != full.Width) What = new Msg(What.Zh + "，缩小到 " + w + " × " + h, What.En + ", scaled to " + w + " x " + h);
                if (black) What = new Msg(What.Zh + "（全黑：这个程序不让截，可以试整个屏幕）", What.En + " (all black: this program can't be captured this way, try the whole screen)");
                View = new PicView(); View.Area = area; View.W = w; View.H = h; View.Window = window; View.Screen = screen; View.At = at;
                return s.ToArray();
            }
        }
    }
    Bitmap Grab() {
        if (window == IntPtr.Zero) {
            Rectangle r = screen > 0 ? Monitors()[screen - 1] : Virtual();
            area = r;
            Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            try { using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size); }
            catch (System.ComponentModel.Win32Exception e) { b.Dispose(); throw Lang.Err("截不到画面：{0}", "can't capture the screen: {0}", e.Message); }
            return b;
        }
        RECT o;
        if (!IsWindow(window) || !GetWindowRect(window, out o)) throw Lang.Err("那个窗口已经关了", "that window has been closed");
        Rectangle outer = o.Rect, inner = Frame(window);
        using (Bitmap b = new Bitmap(Math.Max(1, outer.Width), Math.Max(1, outer.Height), PixelFormat.Format24bppRgb)) {
            using (Graphics g = Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); try { PrintWindow(window, dc, 2); } finally { g.ReleaseHdc(dc); } } // 2: with what the graphics card drew, even when covered
            Rectangle cut = Rectangle.Intersect(new Rectangle(inner.X - outer.X, inner.Y - outer.Y, inner.Width, inner.Height), new Rectangle(0, 0, b.Width, b.Height));
            area = cut.Width < 1 || cut.Height < 1 ? outer : new Rectangle(outer.X + cut.X, outer.Y + cut.Y, cut.Width, cut.Height);
            return cut.Width < 1 || cut.Height < 1 ? new Bitmap(b) : b.Clone(cut, PixelFormat.Format24bppRgb);
        }
    }
    static bool AllBlack(Bitmap b) {
        for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 25)) for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 25)) { Color c = b.GetPixel(x, y); if (c.R + c.G + c.B > 0) return false; }
        return true;
    }
}

class Fact { public string Key; public Msg Name, Value; }

static class Facts {
    [StructLayout(LayoutKind.Sequential)] class MemStatus { public int Size = Marshal.SizeOf(typeof(MemStatus)); public int Load; public ulong Total, Free, PageTotal, PageFree, VirtTotal, VirtFree, Ext; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MemStatus m);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    static readonly string[] Tools = { "git", "node", "npm", "pnpm", "python", "py", "uv", "dotnet", "go", "cargo", "java", "gcc", "cmake", "docker", "ffmpeg", "pwsh", "code", "nvidia-smi" };

    static object Reg(string path, string name) { using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path)) return k == null ? null : k.GetValue(name); }
    static string Gb(double bytes) { return (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB"; }

    public static List<Fact> All() {
        List<Fact> l = new List<Fact>();
        Action<string, string, string, Func<Msg>> add = delegate (string key, string zh, string en, Func<Msg> f) {
            Fact x = new Fact(); x.Key = key; x.Name = new Msg(zh, en);
            try { x.Value = f(); } catch (Exception e) { x.Value = new Msg("?（" + e.Message + "）", "? (" + e.Message + ")"); } // one missing fact mustn't cost the others
            if (x.Value != null) l.Add(x);
        };
        Func<string, Msg> same = delegate (string s) { return string.IsNullOrEmpty(s) ? null : new Msg(s, s); };
        List<string> tools = null;
        Thread t = new Thread(delegate () { tools = FindTools(); }); t.IsBackground = true; t.Start(); // the slow part
        add("name", "电脑名", "Computer", delegate { return same(Environment.MachineName); });
        add("user", "账户", "Account", delegate { bool adm = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); return new Msg(Environment.UserName + (adm ? "（以管理员身份运行）" : "（没有管理员权限）"), Environment.UserName + (adm ? " (running as administrator)" : " (no administrator rights)")); });
        add("os", "系统", "Windows", delegate {
            const string cv = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
            int build; int.TryParse(Convert.ToString(Reg(cv, "CurrentBuild")), out build);
            string name = Convert.ToString(Reg(cv, "ProductName"));
            return same((build >= 22000 ? name.Replace("Windows 10", "Windows 11") : name) + " " + Reg(cv, "DisplayVersion") + " (" + build + "." + Reg(cv, "UBR") + ")"); // the registry still says 10 on 11
        });
        add("locked", "屏幕", "Screen", delegate {
            string m = string.Join(" + ", Shot.Monitors().Select(delegate (Rectangle r) { return r.Width + "x" + r.Height; }).ToArray());
            return Shot.Locked ? new Msg(m + "，已锁屏", m + ", locked") : new Msg(m + "，没锁屏", m + ", not locked");
        });
        add("cpu", "处理器", "CPU", delegate { return new Msg(Convert.ToString(Reg("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", "ProcessorNameString")).Trim() + "，" + Environment.ProcessorCount + " 线程", Convert.ToString(Reg("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", "ProcessorNameString")).Trim() + ", " + Environment.ProcessorCount + " threads"); });
        add("memory", "内存", "Memory", delegate { MemStatus m = new MemStatus(); GlobalMemoryStatusEx(m); return new Msg(Gb(m.Total) + "，空闲 " + Gb(m.Free), Gb(m.Total) + ", " + Gb(m.Free) + " free"); });
        add("gpu", "显卡", "GPU", delegate {
            HashSet<string> seen = new HashSet<string>();
            using (RegistryKey c = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                if (c != null) foreach (string n in c.GetSubKeyNames().Where(delegate (string x) { return Regex.IsMatch(x, "^\\d{4}$"); }))
                    using (RegistryKey k = c.OpenSubKey(n)) {
                        if (k == null || k.GetValue("DriverDesc") == null) continue;
                        object mem = k.GetValue("HardwareInformation.qwMemorySize");
                        seen.Add(k.GetValue("DriverDesc") + (mem is long && (long)mem > 0 ? " " + Gb((long)mem) : ""));
                    }
            return same(string.Join("; ", seen.ToArray()));
        });
        add("disks", "磁盘", "Disks", delegate {
            List<string> zh = new List<string>(), en = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives()) if (d.DriveType == DriveType.Fixed && d.IsReady) { zh.Add(d.Name.Substring(0, 2) + " 空闲 " + Gb(d.AvailableFreeSpace) + " / " + Gb(d.TotalSize)); en.Add(d.Name.Substring(0, 2) + " " + Gb(d.AvailableFreeSpace) + " free of " + Gb(d.TotalSize)); }
            return new Msg(string.Join("；", zh.ToArray()), string.Join("; ", en.ToArray()));
        });
        add("power", "供电", "Power", delegate {
            System.Windows.Forms.PowerStatus p = System.Windows.Forms.SystemInformation.PowerStatus;
            if (p.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) return null;
            bool ac = p.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online; string pc = (int)(p.BatteryLifePercent * 100) + "%";
            return new Msg((ac ? "接着电源，电池 " : "用电池，剩 ") + pc, (ac ? "plugged in, battery " : "on battery, ") + pc);
        });
        add("uptime", "开机", "Up for", delegate { return BgJob.Span(GetTickCount64() / 1000.0); });
        add("clock", "时间", "Clock", delegate { return same(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)); });
        add("lantalk", "LanTalk", "LanTalk", delegate { string f = typeof(Facts).Assembly.Location; return same(Node.Version + " (" + Node.Build + ")  " + f + "  " + File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)); });
        t.Join(12000);
        add("claude", "Claude Code", "Claude Code", delegate { string c = Node.FindClaude(); return c == null ? new Msg("没有装", "not installed") : new Msg(c, c); });
        add("tools", "工具", "Tools", delegate { return tools == null ? new Msg("没来得及查完", "not found out in time") : same(tools.Count == 0 ? "-" : string.Join("\n", tools.ToArray())); });
        return l;
    }
    static List<string> FindTools() {
        string[] found = new string[Tools.Length];
        Thread[] ts = new Thread[Tools.Length];
        for (int i = 0; i < Tools.Length; i++) {
            int k = i;
            ts[i] = new Thread(delegate () { try { found[k] = Version(Tools[k]); } catch (Exception) { } }); // can't be started: as good as missing
            ts[i].IsBackground = true; ts[i].Start();
        }
        DateTime end = DateTime.UtcNow.AddSeconds(8);
        foreach (Thread t in ts) t.Join(Math.Max(1, (int)(end - DateTime.UtcNow).TotalMilliseconds));
        return found.Where(delegate (string s) { return s != null; }).ToList();
    }
    static string Version(string tool) {
        string path = null;
        foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            foreach (string ext in new string[] { ".exe", ".cmd", ".bat" })
                if (path == null && d.Trim().Length > 2) try { string p = Path.Combine(d.Trim().Trim('"'), tool + ext); if (File.Exists(p)) path = p; } catch (ArgumentException) { }
        if (path == null) return null;
        if (path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0 && new FileInfo(path).Length == 0 && (tool == "python" || tool == "py")) return null; // the placeholder that opens the Store
        bool script = !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        string arg = tool == "go" ? "version" : tool == "java" ? "-version" : tool == "nvidia-smi" ? "--query-gpu=name,memory.total,memory.used,driver_version --format=csv,noheader" : "--version";
        ProcessStartInfo si = new ProcessStartInfo(script ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : path, script ? "/d /c \"\"" + path + "\" " + arg + "\"" : arg);
        si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardOutput = si.RedirectStandardError = si.RedirectStandardInput = true; si.StandardOutputEncoding = Encoding.UTF8;
        using (Process p = Process.Start(si)) {
            StringBuilder o = new StringBuilder();
            DataReceivedEventHandler h = delegate (object s, DataReceivedEventArgs e) { if (e.Data != null && e.Data.Trim().Length > 0) lock (o) if (o.Length == 0) o.Append(e.Data.Trim()); };
            p.OutputDataReceived += h; p.ErrorDataReceived += h;
            p.StandardInput.Close(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } return tool + "  " + path; }
            p.WaitForExit(500);
            lock (o) return tool + "  " + (p.ExitCode != 0 ? "" : Util.Cut(o.ToString(), 100) + "  ") + path;
        }
    }
}
}
