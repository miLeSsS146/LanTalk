// Languages. Every text is written as a Chinese/English pair right where it is used: Lang.T("中文", "English").
// Texts that travel to another PC or are stored go as a Msg, which carries both, so each side reads its own language.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LanTalk {

public static class Lang {
    public static bool En; // current language
    public static string T(string zh, string en) { return En ? en : zh; }
    public static string T(string zh, string en, params object[] a) { return string.Format(CultureInfo.InvariantCulture, En ? en : zh, a); }
    public static Msg M(string zh, string en) { return new Msg(zh, en); }
    public static Msg M(string zh, string en, params object[] a) { return new Msg(string.Format(CultureInfo.InvariantCulture, zh, a), string.Format(CultureInfo.InvariantCulture, en, a)); }
    public static LtException Err(string zh, string en) { return new LtException(M(zh, en)); }
    public static LtException Err(string zh, string en, params object[] a) { return new LtException(M(zh, en, a)); }

    // setting: zh | en | auto (follow Windows)
    public static bool IsEnglish(string setting) {
        return setting == "en" || setting != "zh" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh";
    }
    public static void Set(string setting) { En = IsEnglish(setting); }
    // For lt.exe. A config without a language was written before there was a choice: Chinese.
    public static void Load(string dataDir) {
        string f = Path.Combine(dataDir, "config.json"), s = "auto";
        try { if (File.Exists(f)) s = Util.FromJson<Settings>(File.ReadAllText(f, Encoding.UTF8)).Lang ?? "zh"; }
        catch (Exception) { } // being rewritten, or damaged: only the language is at stake
        Set(s);
    }
}

public class Msg {
    public readonly string Zh, En;
    public Msg(string zh, string en) { Zh = zh; En = en; }
    public override string ToString() { return Lang.En ? En : Zh; }
    public static implicit operator string(Msg m) { return m == null ? null : m.ToString(); }
    public static Msg Of(Exception e) {
        LtException l = e as LtException; PermanentException p = e as PermanentException;
        return l != null ? l.M : p != null ? p.M : new Msg(e.Message, e.Message);
    }
    // On the wire: key = Chinese (all that older versions know), key_en = English.
    public static Msg Wire(Dictionary<string, object> d, string key) {
        string zh = Util.Str(d, key), en = Util.Str(d, key + "_en");
        return zh == "" ? null : new Msg(zh, en == "" ? zh : en);
    }
}

public class LtException : InvalidOperationException {
    public readonly Msg M;
    public LtException(Msg m) { M = m; }
    public override string Message { get { return M; } }
}
}
