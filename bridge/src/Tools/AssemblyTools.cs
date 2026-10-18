using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

/// Putting parts together and checking the result: sw_assemble, sw_interference, sw_bom.
static class AssemblyTools
{
    static readonly Dictionary<string, int> MateTypes = new Dictionary<string, int>
    {
        ["coincident"] = 0, ["concentric"] = 1, ["perpendicular"] = 2, ["parallel"] = 3, ["tangent"] = 4,
        ["distance"] = 5, ["angle"] = 6,
    };

    static readonly Dictionary<string, int> Alignments = new Dictionary<string, int>
    {
        ["aligned"] = 0, ["anti_aligned"] = 1, ["closest"] = 2,
    };

    // swAddMateError_e
    static readonly Dictionary<int, string> MateErrors = new Dictionary<int, string>
    {
        [0] = "unknown error", [2] = "this mate type does not fit these entities", [3] = "the alignment does not fit these entities",
        [4] = "these entities cannot be mated this way", [5] = "the assembly would be over-defined", [6] = "bad gear ratio",
    };

    public static Obj Assemble(Bridge b, object app, Args a)
    {
        var asm = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocAssembly);
        var steps = Json.Records(a, "steps");
        if (steps.Count == 0) throw new BridgeException("BAD_REQUEST", "steps is empty");
        if (steps.Count > 60) throw new BridgeException("BAD_REQUEST", "at most 60 steps per call");
        SessionTools.Activate(app, asm);

        var results = new List<object>();
        Obj failure = null;
        for (var i = 0; i < steps.Count && failure == null; i++)
        {
            var s = steps[i];
            var op = Json.Str(s, "op");
            var what = $"step {i + 1} ({op})";
            try
            {
                var r = new Obj().Add("step", i + 1);
                var done = op switch
                {
                    "insert" => Insert(b, app, asm, s, what),
                    "mate" => Mate(app, asm, s, what),
                    "fix" => Fix(asm, s, what, true),
                    "float" => Fix(asm, s, what, false),
                    _ => throw new BridgeException("BAD_REQUEST", $"{what}: op must be insert, mate, fix or float"),
                };
                foreach (var kv in done) r.Add(kv.Key, kv.Value);
                results.Add(r);
            }
            catch (BridgeException e) when (e.Code != "SW_GONE" && e.Code != "SW_BUSY")
            {
                failure = new Obj().Add("step", i + 1).Add("op", op).Add("code", e.Code).Add("error", e.Message);
            }
        }
        Call(asm, "ClearSelection2", true);
        Call(asm, "EditRebuild3");
        var rebuild = WriteTools.WhatsWrong(asm);

        var o = new Obj().Add("ok", failure == null && (bool)rebuild["ok"]).Add("doc", Docs.Brief(asm, b)).Add("steps", results);
        if (failure != null) o.Add("failed", failure).Add("note", "steps before the failed one stay applied");
        var comps = new List<object>();
        foreach (var c in Objects(Call(asm, "GetComponents", true)))
            comps.Add(new Obj().Add("name", Str(Get(c, "Name2"))).Add("file", Path.GetFileName(Str(Call(c, "GetPathName")) ?? ""))
                .Add("fixed", Bool(Call(c, "IsFixed"))).Add("origin_mm", Origin(c)));
        return o.Add("components", comps).Add("rebuild", rebuild);
    }

    static double[] Origin(object comp)
    {
        var m = Doubles(Get(Get(comp, "Transform2"), "ArrayData"));
        return m.Length >= 12 ? new[] { Units.Mm(m[9]), Units.Mm(m[10]), Units.Mm(m[11]) } : null;
    }

    /// Add a part or sub-assembly with its origin at `at`. The file is loaded without a window
    /// first if it is not open, since SOLIDWORKS adds only documents it has in memory.
    static Obj Insert(Bridge b, object app, object asm, Args s, string what)
    {
        var path = Json.Str(s, "path") ?? throw new BridgeException("BAD_REQUEST", $"{what} needs path");
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception) { throw new BridgeException("BAD_REQUEST", $"{what}: not a usable Windows path: {path}"); }
        if (!File.Exists(full)) throw new BridgeException("FILE_NOT_FOUND", $"{what}: {full} does not exist on {Environment.MachineName}; a new part needs sw_save with a path first");
        var type = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".sldprt" => K.DocPart,
            ".sldasm" => K.DocAssembly,
            _ => throw new BridgeException("BAD_REQUEST", $"{what}: components are .sldprt or .sldasm files"),
        };
        var config = Json.Str(s, "configuration", "");
        var at = Json.Triple(s, "at", $"{what}: at") ?? new double[3];

        // Loaded here without a window: closed again once the assembly holds it, so the
        // assembly alone decides how long it stays in memory.
        string loadedHere = null;
        if (Call(app, "GetOpenDocumentByName", full) == null)
        {
            Call(app, "DocumentVisible", false, type);
            try
            {
                var args = new object[] { full, type, K.OpenSilent | K.OpenReadOnly, config, 0, 0 };
                var loaded = CallRef(app, "OpenDoc6", args, 4, 5)
                    ?? throw new BridgeException("OPEN_FAILED", $"{what}: SOLIDWORKS could not load {full}: {string.Join("; ", K.Bits(Convert.ToInt32(args[4]), K.LoadErrors))}");
                loadedHere = Str(Call(loaded, "GetTitle"));
            }
            finally { Call(app, "DocumentVisible", true, type); }
        }
        SessionTools.Activate(app, asm);

        var existing = Objects(Call(asm, "GetComponents", true)).Length;
        var p = Vec.Metres(at);
        object comp;
        try
        {
            comp = Call(asm, "AddComponent4", full, config, p[0], p[1], p[2])
                ?? throw new BridgeException("COM_NULL", $"{what}: SOLIDWORKS did not add {Path.GetFileName(full)}");
        }
        finally
        {
            if (loadedHere != null) Call(app, "CloseDoc", loadedHere);
        }

        // AddComponent places the component's bounding box, not its origin, at the point.
        // Move it so the origin lands where asked: that is the point every other tool reports.
        // A fixed component does not move, so it is floated for the move and fixed after.
        SetFixed(asm, comp, false);
        var math = Call(app, "GetMathUtility");
        var xform = Call(math, "CreateTransform", new object[] { new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, p[0], p[1], p[2], 1, 0, 0, 0 } });
        SetRef(comp, "Transform2", xform);
        SetFixed(asm, comp, s.ContainsKey("fixed") ? Json.Bool(s, "fixed", false) : existing == 0);
        return new Obj().Add("op", "insert").Add("ok", true).Add("component", Str(Get(comp, "Name2")))
            .Add("file", full).Add("origin_mm", Origin(comp)).Add("fixed", Bool(Call(comp, "IsFixed")));
    }

    static Obj Fix(object asm, Args s, string what, bool fix)
    {
        var name = Json.Str(s, "component") ?? throw new BridgeException("BAD_REQUEST", $"{what} needs component");
        var comp = Component(asm, name, what);
        SetFixed(asm, comp, fix);
        if (Bool(Call(comp, "IsFixed")) != fix) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS did not {(fix ? "fix" : "float")} {name}");
        return new Obj().Add("op", fix ? "fix" : "float").Add("ok", true).Add("component", name);
    }

    static void SetFixed(object asm, object comp, bool fix)
    {
        if (Bool(Call(comp, "IsFixed")) == fix) return;
        Call(asm, "ClearSelection2", true);
        Call(comp, "Select4", false, Nothing, false);
        if (fix) Call(asm, "FixComponent");
        else Call(asm, "UnfixComponent");
        Call(asm, "ClearSelection2", true);
    }

    static object Component(object asm, string name, string what) =>
        Call(asm, "GetComponentByName", name)
        ?? throw new BridgeException("BAD_REQUEST", $"{what}: no component named {name}; sw_components lists them (names look like bracket-1)");

    static Obj Mate(object app, object asm, Args s, string what)
    {
        var typeName = Json.Str(s, "type") ?? throw new BridgeException("BAD_REQUEST", $"{what} needs type");
        if (!MateTypes.TryGetValue(typeName, out var type))
            throw new BridgeException("BAD_REQUEST", $"{what}: type must be one of {string.Join(", ", MateTypes.Keys)}");
        var align = Alignments.TryGetValue(Json.Str(s, "align", "closest"), out var al) ? al
            : throw new BridgeException("BAD_REQUEST", $"{what}: align must be aligned, anti_aligned or closest");
        var distance = type == 5 ? Json.Need(s, "distance", what) / 1000 : 0;
        var angle = type == 6 ? Json.Need(s, "angle", what) * Math.PI / 180 : 0;

        Call(asm, "ClearSelection2", true);
        var data = Call(Get(asm, "SelectionManager"), "CreateSelectData");
        Set(data, "Mark", 1);
        var ra = Select(app, asm, Json.Map(s, "a"), data, $"{what}: a");
        var rb = Select(app, asm, Json.Map(s, "b"), data, $"{what}: b");

        var before = LastMate(asm);
        var args = new object[] { type, align, Json.Bool(s, "flip", false), distance, distance, distance, 0.0, 0.0, angle, angle, angle, false, false, 0, 0 };
        var mate = CallRef(asm, "AddMate5", args, 14);
        var status = Convert.ToInt32(args[14]);
        Call(asm, "ClearSelection2", true);
        var reason = MateErrors.TryGetValue(status, out var m) ? m : $"error {status}";
        if (mate == null)
            throw new BridgeException("BAD_REQUEST", $"{what}: SOLIDWORKS refused the {typeName} mate between {ra} and {rb}: {reason}");
        // A mate that exists is reported as made, whatever the status says; if it does not
        // solve, the rebuild result names it.
        var after = LastMate(asm);
        return new Obj().Add("op", "mate").Add("ok", true).Add("type", typeName)
            .Opt("mate", after != before ? after : null).Add("a", ra).Add("b", rb)
            .Opt("warning", status == 1 ? null : $"SOLIDWORKS made the mate but reported: {reason}");
    }

    /// The newest mate's name, from the Mates folder.
    static string LastMate(object asm)
    {
        for (var f = Call(asm, "FirstFeature"); f != null; f = Call(f, "GetNextFeature"))
        {
            if (Str(Call(f, "GetTypeName2")) != "MateGroup") continue;
            string last = null;
            for (var m = Call(f, "GetFirstSubFeature"); m != null; m = Call(m, "GetNextSubFeature")) last = Str(Get(m, "Name"));
            return last;
        }
        return null;
    }

    /// Select what a mate reference names, with mark 1: a component's or the assembly's plane,
    /// or the face, edge or vertex at a point in assembly space.
    static string Select(object app, object asm, Args r, object data, string what)
    {
        if (r == null) throw new BridgeException("BAD_REQUEST", $"{what} is required: {{\"component\": \"bracket-1\", \"plane\": \"top\"}} or {{\"at\": [x, y, z]}}");
        var at = Json.Triple(r, "at", $"{what}.at");
        var plane = Json.Str(r, "plane");
        var compName = Json.Str(r, "component");
        if (at != null)
        {
            var kind = Json.Str(r, "kind", "face");
            if (kind != "face" && kind != "edge" && kind != "vertex") throw new BridgeException("BAD_REQUEST", $"{what}.kind must be face, edge or vertex");
            var hit = Pick.Require(app, asm, at, kind, what);
            if (!Bool(Call(hit.Entity, "Select4", true, data))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select the {kind} at [{Pick.Fmt(at)}]");
            return $"the {kind} at [{Pick.Fmt(at)}]" + (hit.Component == null ? "" : $" ({Str(Get(hit.Component, "Name2"))})");
        }
        if (plane == null) throw new BridgeException("BAD_REQUEST", $"{what} needs at, or plane (with component for a component's plane)");
        var owner = compName == null ? asm : Component(asm, compName, what);
        var feature = Planes.Find(owner, plane, what);
        if (!Bool(Call(feature, "Select2", true, 1))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select {plane}");
        return compName == null ? $"the assembly's {plane} plane" : $"{compName}'s {plane} plane";
    }

    public static Obj Interference(Bridge b, object app, Args a)
    {
        var asm = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocAssembly);
        var limit = Math.Max(1, Math.Min(500, Json.Int(a, "limit", 100)));
        SessionTools.Activate(app, asm);
        var mgr = Get(asm, "InterferenceDetectionManager") ?? throw new BridgeException("COM_NULL", "SOLIDWORKS has no interference detection for this assembly");
        try
        {
            Set(mgr, "TreatCoincidenceAsInterference", Json.Bool(a, "touching", false));
            Set(mgr, "TreatSubAssembliesAsComponents", true);
            Set(mgr, "IncludeMultibodyPartInterferences", false);
            Set(mgr, "IgnoreHiddenBodies", true);
            Set(mgr, "MakeInterferingPartsTransparent", false);
            Set(mgr, "CreateFastenersFolder", false);
            Set(mgr, "NonInterferingComponentDisplay", K.NonInterferingCurrent);
            var found = Objects(Call(mgr, "GetInterferences"));
            var items = new List<(double volume, Obj o)>();
            var total = 0.0;
            foreach (var i in found)
            {
                var v = Convert.ToDouble(Get(i, "Volume")) * 1e9;
                total += v;
                var names = new List<string>();
                foreach (var c in Objects(Get(i, "Components"))) names.Add(Str(Get(c, "Name2")));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                items.Add((v, new Obj().Add("components", names).Add("volume_mm3", Units.Round(v))
                    .Add("possible", Bool(Get(i, "IsPossibleInterference")))));
            }
            items.Sort((x, y) => y.volume.CompareTo(x.volume));
            var list = new List<object>();
            for (var i = 0; i < items.Count && i < limit; i++) list.Add(items[i].o);
            return new Obj().Add("doc", Docs.Label(asm)).Add("interferences", items.Count)
                .Add("total_volume_mm3", Units.Round(total)).Add("touching_counts", Json.Bool(a, "touching", false))
                .Add("items", list).Add("truncated", items.Count > limit);
        }
        finally
        {
            try { Call(mgr, "Done"); } catch (BridgeException) { /* closed already */ }
        }
    }

    /// The bill of materials as SOLIDWORKS counts it: suppressed, envelope and excluded-from-BOM
    /// components are left out. parts flattens to part files; top counts top-level components.
    public static Obj Bom(Bridge b, object app, Args a)
    {
        var asm = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocAssembly);
        var mode = Json.Str(a, "mode", "parts");
        if (mode != "parts" && mode != "top") throw new BridgeException("BAD_REQUEST", "mode must be parts or top");
        var wanted = Json.List(a, "properties");
        var limit = Math.Max(1, Math.Min(5000, Json.Int(a, "limit", 1000)));

        var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var skipped = 0;
        // Walk the tree from the top: a suppressed, envelope or excluded sub-assembly takes its
        // whole subtree out of the BOM, as SOLIDWORKS' own BOM does.
        void Walk(object[] comps)
        {
            foreach (var c in comps)
            {
                if (Convert.ToInt32(Call(c, "GetSuppression")) == 0 || Bool(Get(c, "ExcludeFromBOM")) || Bool(Call(c, "IsEnvelope"))) { skipped++; continue; }
                var path = Str(Call(c, "GetPathName")) ?? "";
                var isPart = path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase);
                if (mode == "parts" && !isPart)
                {
                    Walk(Objects(Call(c, "GetChildren")));
                    continue;
                }
                var config = Str(Get(c, "ReferencedConfiguration")) ?? "";
                var key = BomTable.Key(path, config);
                if (!rows.TryGetValue(key, out var row))
                {
                    row = new Row { Path = path, Configuration = config, Properties = Properties(c, config, wanted) };
                    rows[key] = row;
                    order.Add(key);
                }
                row.Quantity++;
            }
        }
        using (new CommandInProgress(app))
            Walk(Objects(Call(asm, "GetComponents", true)));
        var sorted = BomTable.Sort(order, k => rows[k].Path, k => rows[k].Configuration);
        var list = new List<object>();
        var columns = new List<string>(wanted);
        var item = 0;
        foreach (var k in sorted)
        {
            var r = rows[k];
            if (wanted.Count == 0) foreach (var name in r.Properties.Keys) if (!columns.Contains(name)) columns.Add(name);
            if (item++ < limit)
                list.Add(new Obj().Add("item", item).Add("file", Path.GetFileName(r.Path)).Add("configuration", r.Configuration)
                    .Add("quantity", r.Quantity).Add("properties", r.Properties).Add("path", r.Path));
        }
        var o = new Obj().Add("doc", Docs.Label(asm)).Add("configuration", Docs.ActiveConfiguration(asm)).Add("mode", mode)
            .Add("rows", sorted.Count).Add("components_counted", Total(rows)).Add("left_out", skipped).Add("items", list)
            .Add("truncated", sorted.Count > limit);

        var csv = Json.Str(a, "csv");
        if (!string.IsNullOrWhiteSpace(csv))
        {
            var target = b.OutputPath(csv);
            if (!target.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("BAD_REQUEST", "csv must name a .csv file");
            if (File.Exists(target) && !Json.Bool(a, "overwrite", false)) throw new BridgeException("FILE_EXISTS", $"{target} exists; pass overwrite: true to replace it");
            var table = new List<string[]>();
            var header = new List<string> { "Item", "File", "Configuration", "Quantity" };
            header.AddRange(columns);
            table.Add(header.ToArray());
            item = 0;
            foreach (var k in sorted)
            {
                var r = rows[k];
                var line = new List<string> { (++item).ToString(), Path.GetFileName(r.Path), r.Configuration, r.Quantity.ToString() };
                foreach (var col in columns) line.Add(r.Properties.TryGetValue(col, out var v) ? Convert.ToString(v) : "");
                table.Add(line.ToArray());
            }
            File.WriteAllText(target, BomTable.Csv(table), new UTF8Encoding(true));
            var facts = WriteTools.FileFacts(target);
            b.MarkExported((string)facts["sha256"], target);
            o.Add("csv", facts);
        }
        return o;
    }

    sealed class Row
    {
        public string Path, Configuration;
        public int Quantity;
        public Obj Properties;
    }

    static int Total(Dictionary<string, Row> rows)
    {
        var n = 0;
        foreach (var r in rows.Values) n += r.Quantity;
        return n;
    }

    /// A component's custom properties, resolved: the configuration's override the file's.
    /// Lightweight components have no document loaded and report none.
    static Obj Properties(object comp, string config, List<string> wanted)
    {
        var o = new Obj();
        var doc = Call(comp, "GetModelDoc2");
        if (doc == null) return o.Add("_note", "not loaded (lightweight); resolve it to read properties");
        var ext = Get(doc, "Extension");
        foreach (var scope in new[] { "", config })
        {
            var mgr = Get(ext, "CustomPropertyManager", scope);
            if (mgr == null) continue;
            foreach (var name in Strings(Call(mgr, "GetNames")))
            {
                if (wanted.Count > 0 && !wanted.Exists(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase))) continue;
                var (raw, resolved) = ReadTools.Property(mgr, name);
                if (raw != null) o.Add(name, resolved ?? raw);
            }
        }
        return o;
    }
}

/// The pure parts of the BOM, which the self-test covers.
static class BomTable
{
    public static string Key(string path, string config) => (path ?? "").ToLowerInvariant() + "|" + (config ?? "");

    public static List<string> Sort(List<string> keys, Func<string, string> path, Func<string, string> config)
    {
        var r = new List<string>(keys);
        r.Sort((x, y) =>
        {
            var c = string.Compare(Path.GetFileName(path(x)), Path.GetFileName(path(y)), StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(config(x), config(y), StringComparison.OrdinalIgnoreCase);
        });
        return r;
    }

    /// RFC 4180: fields with a comma, quote or line break are quoted, quotes doubled; CRLF rows.
    public static string Csv(List<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var f = row[i] ?? "";
                if (f.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
                else sb.Append(f);
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
