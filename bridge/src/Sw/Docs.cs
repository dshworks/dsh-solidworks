using System;
using System.Collections.Generic;
using System.IO;
using SwBridge.Com;
using static SwBridge.Com.Dispatch;

namespace SwBridge.Sw;

/// Finding and describing open documents.
static class Docs
{
    /// The document a tool means: the named one, else the active one.
    /// A name matches the full path, the file name, or the title, case-insensitively, with or
    /// without extension. GetTitle includes the extension or not depending on Explorer's
    /// "hide extensions" setting, so titles are compared both ways.
    public static object Resolve(object app, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var active = Get(app, "ActiveDoc");
            if (active == null) throw new BridgeException("NO_ACTIVE_DOC", "no document is active in SOLIDWORKS; open one with sw_open or name one with doc");
            return active;
        }
        var want = name.Trim();
        var docs = Objects(Call(app, "GetDocuments"));
        object loose = null;
        foreach (var d in docs)
        {
            var m = Match(Str(Call(d, "GetPathName")) ?? "", Str(Call(d, "GetTitle")) ?? "", Type(d), want);
            if (m == 2) return d;
            if (m == 1 && loose == null) loose = d;
        }
        if (loose != null) return loose;

        var open = new List<string>();
        foreach (var d in docs)
        {
            if (!Bool(Get(d, "Visible"))) continue; // parts an assembly loaded, not documents a person opened
            if (open.Count == 12) { open.Add("..."); break; }
            open.Add(Label(d));
        }
        throw new BridgeException("DOC_NOT_OPEN",
            $"\"{want}\" is not open in SOLIDWORKS; open it with sw_open first. Open: {(open.Count == 0 ? "none" : string.Join(", ", open))}");
    }

    /// How well a document answers to a name: 2 exactly (its path, file name or title), 1 loosely
    /// (the same name without extension, since titles follow Explorer's "hide extensions"), 0 not.
    /// A name with a SOLIDWORKS extension only ever matches a document of that type: flange.slddrw
    /// is never the part flange.sldprt (measured: an export of "flange.slddrw" wrote the part).
    public static int Match(string path, string title, int type, string want)
    {
        if (Same(path, want) || Same(title, want)) return 2;
        if (path.Length > 0 && Same(Path.GetFileName(path), want)) return 2;
        var wantType = Path.GetExtension(want).ToLowerInvariant() switch
        {
            ".sldprt" => K.DocPart, ".sldasm" => K.DocAssembly, ".slddrw" => K.DocDrawing, _ => 0,
        };
        if (wantType != 0 && wantType != type) return 0;
        var stem = wantType != 0 ? Path.GetFileNameWithoutExtension(want) : want;
        if (path.Length > 0 && Same(Path.GetFileNameWithoutExtension(path), stem)) return 1;
        // A drawing's title carries its sheet: "flange - Sheet1".
        var bare = Path.GetFileNameWithoutExtension(title);
        var sheet = type == K.DocDrawing ? title.LastIndexOf(" - ", StringComparison.Ordinal) : -1;
        if (sheet > 0 && Same(title.Substring(0, sheet), stem)) return 1;
        return Same(bare, stem) || Same(title, stem) ? 1 : 0;
    }

    static bool Same(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string Label(object doc)
    {
        var path = Str(Call(doc, "GetPathName"));
        return string.IsNullOrEmpty(path) ? Str(Call(doc, "GetTitle")) : Path.GetFileName(path);
    }

    public static int Type(object doc) => Convert.ToInt32(Call(doc, "GetType"));

    public static object Require(object doc, params int[] types)
    {
        var t = Type(doc);
        foreach (var ok in types) if (t == ok) return doc;
        var names = new List<string>();
        foreach (var ok in types) names.Add(K.DocType(ok));
        throw new BridgeException("WRONG_DOC_TYPE", $"{Label(doc)} is {Article(K.DocType(t))}; this needs {string.Join(" or ", names.ConvertAll(Article))}");
    }

    static string Article(string noun) => ("aeiou".IndexOf(noun[0]) >= 0 ? "an " : "a ") + noun;

    public static string ActiveConfiguration(object doc)
    {
        var mgr = Get(doc, "ConfigurationManager");
        var cfg = mgr == null ? null : Get(mgr, "ActiveConfiguration");
        return cfg == null ? null : Str(Get(cfg, "Name"));
    }

    /// The facts every document answer starts with.
    public static Obj Brief(object doc, Bridge bridge)
    {
        var path = Str(Call(doc, "GetPathName")) ?? "";
        var type = Type(doc);
        return new Obj()
            .Add("title", Str(Call(doc, "GetTitle")))
            .Add("path", path)
            .Add("type", K.DocType(type))
            .Opt("configuration", type == K.DocDrawing ? null : ActiveConfiguration(doc))
            .Add("read_only", Bool(Call(doc, "IsOpenedReadOnly")))
            .Add("dirty", Bool(Call(doc, "GetSaveFlag")))
            .Add("opened_by_bridge", bridge.Owns(doc));
    }

    public static string Normalize(string path) =>
        string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();

    /// What identifies a document across calls: its file, or its title until it has one.
    public static string Key(object doc)
    {
        var path = Str(Call(doc, "GetPathName"));
        return string.IsNullOrEmpty(path) ? "new:" + Str(Call(doc, "GetTitle")) : FileKey(path);
    }

    public static string FileKey(string path) => "file:" + Normalize(path);
}
