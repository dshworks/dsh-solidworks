using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SwBridge.Com;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

static class SessionTools
{
    public static Obj Status(Bridge b, object app, Args a)
    {
        var rev = Str(Call(app, "RevisionNumber"));
        var (year, sp) = Session.Release(rev);
        var o = new Obj()
            .Add("bridge", new Obj().Add("version", Bridge.Version).Add("pid", Process.GetCurrentProcess().Id)
                .Add("machine", Environment.MachineName).Add("user", Environment.UserName)
                .Add("output_folder", b.Options.OutDir).Add("save_enabled", b.Options.AllowSave))
            .Add("solidworks", new Obj()
                .Add("pid", b.Session.Pid)
                .Add("release", year > 0 ? $"SOLIDWORKS {year} SP{sp}" : $"SOLIDWORKS revision {rev}")
                .Add("revision", rev)
                .Add("visible", Bool(Get(app, "Visible")))
                .Add("command_in_progress", Bool(Get(app, "CommandInProgress")))
                .Opt("started", Session.ProcessStart(b.Session.Pid)));

        var all = Session.Instances();
        try
        {
            var list = new List<object>();
            foreach (var i in all)
                list.Add(new Obj().Add("pid", i.Pid).Add("revision", i.Revision).Add("visible", i.Visible)
                    .Add("selected", i.Pid == b.Session.Pid));
            o.Add("instances", list);
        }
        finally
        {
            Session.Release(all, app);
        }

        // GetDocuments also returns every part an open assembly has loaded; a large assembly
        // would bury the user's documents under hundreds of entries. List the ones with a
        // window, and count the rest.
        var active = Get(app, "ActiveDoc");
        var docs = new List<object>();
        var hidden = 0;
        foreach (var d in Objects(Call(app, "GetDocuments")))
        {
            if (!Bool(Get(d, "Visible")) && !ReferenceEquals(d, active)) { hidden++; continue; }
            var brief = Docs.Brief(d, b);
            brief.Add("active", ReferenceEquals(d, active));
            docs.Add(brief);
        }
        o.Add("documents", docs);
        if (hidden > 0) o.Add("loaded_without_window", hidden);
        var dialogs = Dialogs.Open(b.Session.Pid);
        if (dialogs.Count > 0) o.Add("dialogs", dialogs);
        return o;
    }

    public static Obj Open(Bridge b, object app, Args a)
    {
        var path = Json.Str(a, "path");
        if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("BAD_REQUEST", "path is required");
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception) { throw new BridgeException("BAD_REQUEST", $"not a usable Windows path: {path}"); }
        if (!File.Exists(full)) throw new BridgeException("FILE_NOT_FOUND", $"{full} does not exist on {Environment.MachineName}");

        var type = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".sldprt" or ".prt" => K.DocPart,
            ".sldasm" or ".asm" => K.DocAssembly,
            ".slddrw" or ".drw" => K.DocDrawing,
            _ => throw new BridgeException("BAD_REQUEST",
                $"sw_open opens SOLIDWORKS documents (.sldprt, .sldasm, .slddrw), not {Path.GetExtension(full)}"),
        };
        var readOnly = Json.Bool(a, "read_only", true);
        var config = Json.Str(a, "configuration", "");

        var existing = Call(app, "GetOpenDocumentByName", full);
        if (existing != null)
        {
            Activate(app, existing);
            return Docs.Brief(existing, b).Add("already_open", true);
        }

        var clock = Stopwatch.StartNew();
        var args = new object[] { full, type, K.OpenSilent | (readOnly ? K.OpenReadOnly : 0), config, 0, 0 };
        var doc = CallRef(app, "OpenDoc6", args, 4, 5);
        var errors = Convert.ToInt32(args[4]);
        var warnings = Convert.ToInt32(args[5]);

        if (doc == null)
        {
            var reasons = K.Bits(errors, K.LoadErrors);
            if ((errors & 8192) != 0)
            {
                var (year, sp) = Session.Release(Str(Call(app, "RevisionNumber")));
                reasons = new List<string> { $"the file was saved by a newer SOLIDWORKS than this one ({year} SP{sp}); files do not open in older releases" };
            }
            throw new BridgeException("OPEN_FAILED", $"SOLIDWORKS could not open {full}: {(reasons.Count == 0 ? "no reason given" : string.Join("; ", reasons))}",
                new Dictionary<string, object> { ["errors"] = reasons, ["warnings"] = K.Bits(warnings, K.LoadWarnings) });
        }

        // With another file of the same name already open, OpenDoc6 hands back that one.
        var got = Str(Call(doc, "GetPathName"));
        if (!string.Equals(Docs.Normalize(got), Docs.Normalize(full), StringComparison.Ordinal))
            throw new BridgeException("OPEN_FAILED",
                $"a different file with the same name is already open ({got}); SOLIDWORKS allows one document per file name. Close it first");

        b.MarkOpened(full);
        Activate(app, doc);
        return Docs.Brief(doc, b)
            .Add("warnings", K.Bits(warnings, K.LoadWarnings))
            .Add("seconds", Math.Round(clock.Elapsed.TotalSeconds, 2));
    }

    public static Obj Close(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var path = Str(Call(doc, "GetPathName")) ?? "";
        var title = Str(Call(doc, "GetTitle"));
        var dirty = Bool(Call(doc, "GetSaveFlag"));
        if (dirty && !(Json.Bool(a, "discard", false) && b.OpenedByBridge(path)))
            throw new BridgeException("DIRTY", b.OpenedByBridge(path)
                ? $"{title} has unsaved changes; pass discard: true to throw them away, or save or export first"
                : $"{title} has unsaved changes and was not opened by this bridge process; the bridge will not discard work it did not create. Save or close it in SOLIDWORKS");

        // CloseDoc by title occasionally leaves the document open; check and retry by path.
        Call(app, "CloseDoc", title);
        if (path.Length > 0 && Call(app, "GetOpenDocumentByName", path) != null) Call(app, "CloseDoc", path);
        if (path.Length > 0 && Call(app, "GetOpenDocumentByName", path) != null)
            throw new BridgeException("COM_ERROR", $"SOLIDWORKS did not close {title}");
        b.MarkClosed(path);
        return new Obj().Add("closed", title).Add("path", path).Add("discarded_changes", dirty);
    }

    public static void Activate(object app, object doc)
    {
        var active = Get(app, "ActiveDoc");
        if (ReferenceEquals(active, doc)) return;
        var args = new object[] { Str(Call(doc, "GetTitle")), false, K.DontRebuildActiveDoc, 0 };
        CallRef(app, "ActivateDoc3", args, 3);
    }
}
