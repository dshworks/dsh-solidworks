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
        foreach (var d in docs)
            if (Matches(d, want)) return d;

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

    static bool Matches(object doc, string want)
    {
        var path = Str(Call(doc, "GetPathName")) ?? "";
        var title = Str(Call(doc, "GetTitle")) ?? "";
        if (Same(path, want) || Same(title, want)) return true;
        if (path.Length > 0)
        {
            var file = Path.GetFileName(path);
            if (Same(file, want) || Same(Path.GetFileNameWithoutExtension(path), want)) return true;
        }
        return Same(Path.GetFileNameWithoutExtension(title), Path.GetFileNameWithoutExtension(want));
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
            .Add("opened_by_bridge", bridge.OpenedByBridge(path));
    }

    public static string Normalize(string path) =>
        string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
}
