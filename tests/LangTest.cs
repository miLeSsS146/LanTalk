// Language tests, part of Test.cs. Everything else in the suite runs in Chinese.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LanTalk {
static partial class Test {
    static bool Chinese(string s) { return s.Any(delegate (char c) { return c >= 0x4E00 && c <= 0x9FFF; }); }
    static void LangTests(Node A, Node B, string da) {
        int rc; string o, ib = B.Cfg.Id, ia = A.Cfg.Id;
        string old = Path.Combine(root, "old-config"); Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "config.json"), "{\"Name\":\"x\",\"Port\":47999}");
        Check(new Node(old, IPAddress.Loopback).Cfg.Lang == "zh" && new Node(Path.Combine(root, "new-config"), IPAddress.Loopback).Cfg.Lang == "auto", "lang: an existing setup stays Chinese, a new one follows Windows");
        Check(Lang.IsEnglish("en") && !Lang.IsEnglish("zh"), "lang: explicit choice wins");

        B.SetGrant(ia, "PC-A", "", ""); // refusals travel in both languages
        Dictionary<string, object> r;
        using (Channel ch = Channel.Connect("127.0.0.1", B.Cfg.Port, Crypto.Master(B.Code))) { ch.SendJson(new { t = "control", id = Util.NewId(), from = ia, name = "PC-A", port = A.Cfg.Port, to = ib }); r = ch.ReceiveJson(); }
        Check(Util.Str(r, "err").Contains("控制权限") && Util.Str(r, "err_en").Contains("Control access") && !Chinese(Util.Str(r, "err_en")), "lang: a refusal carries both languages");
        r = new Dictionary<string, object>(); r["err"] = "旧版本的消息";
        Check(Msg.Wire(r, "err").En == "旧版本的消息" && Msg.Wire(r, "none") == null, "lang: a message from an older version still shows");

        A.Cfg.Lang = "en"; A.SaveConfig(); Lang.Set("en");
        try {
            o = LtErr(da, out rc, "list", null); Check(rc == 0 && o.Contains("This PC: PC-A") && o.Contains("[online]  PC-B") && !Chinese(o.Replace(A.Cfg.SaveDir, "")), "lang: lt list in English");
            o = LtErr(da, out rc, "", null); Check(rc == 1, "lang: usage");
            o = LtErr(da, out rc, "help", null); Check(o.Contains("lt run --to PC") && !Chinese(o), "lang: usage in English");
            o = LtErr(da, out rc, "run --to PC-B 1", null); Check(rc == 125 && Err.StartsWith("Error: ") && Err.Contains("Control access") && !Chinese(Err), "lang: refusal in English");
            o = LtErr(da, out rc, "send --to nobody hi", null); Check(rc == 1 && o.Contains("no PC called \"nobody\"") && !Chinese(o), "lang: error in English");
            B.SetGrant(ia, "PC-A", A.PublicKey, "allow");
            o = LtErr(da, out rc, "run --to PC-B -", "'中文输出'"); Check(rc == 0 && o.Contains("中文输出"), "lang: what a command prints is never touched");
            o = LtErr(da, out rc, "run --to PC-B --timeout 1 \"Start-Sleep 9\"", null); ChatMsg m = A.History(ib, 1)[0];
            Check(rc == 124 && m.ErrText.StartsWith("timed out"), "lang: result in English");
            Lang.Set("zh"); Check(m.ErrText.StartsWith("超时"), "lang: the same record in Chinese after switching");
        } finally { Lang.Set("zh"); A.Cfg.Lang = "zh"; A.SaveConfig(); B.SetGrant(ia, "PC-A", "", ""); }

        // Every pair in the source: same placeholders on both sides, no Chinese left in the English one.
        int pairs = 0; List<string> bad = new List<string>();
        foreach (string f in Directory.GetFiles(Environment.CurrentDirectory, "*.cs"))
            foreach (Match p in Regex.Matches(File.ReadAllText(f, Encoding.UTF8), "Lang\\.(?:T|M|Err)\\(\"((?:[^\"\\\\]|\\\\.)*)\", \"((?:[^\"\\\\]|\\\\.)*)\"")) {
                pairs++;
                Func<string, string> holes = delegate (string s) { return string.Join(",", Regex.Matches(s, "\\{\\d[^}]*\\}").Cast<Match>().Select(delegate (Match x) { return x.Value; }).Distinct().OrderBy(delegate (string x) { return x; }).ToArray()); };
                if (holes(p.Groups[1].Value) != holes(p.Groups[2].Value) || Chinese(p.Groups[2].Value) || p.Groups[2].Value.Length == 0) bad.Add(Path.GetFileName(f) + ": " + p.Groups[2].Value);
            }
        Check(pairs > 150 && bad.Count == 0, "lang: " + pairs + " text pairs match " + string.Join(" | ", bad.ToArray()));
    }
}
}
