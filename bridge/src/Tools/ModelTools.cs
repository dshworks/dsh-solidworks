using System;
using System.Collections.Generic;
using System.IO;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

/// Making geometry: new documents, and sw_build's modeling steps on a part.
///
/// Every coordinate a step takes is a model-space point in millimetres, the same space
/// sw_probe and box_mm report in, so nobody has to know how SOLIDWORKS orients a sketch on a
/// face. The bridge maps each point into the sketch with the sketch's own transform.
static class ModelTools
{
    public static Obj New(Bridge b, object app, Args a)
    {
        var kind = Json.Str(a, "type", "part");
        var type = kind switch
        {
            "part" => K.DocPart,
            "assembly" => K.DocAssembly,
            _ => throw new BridgeException("BAD_REQUEST", "type must be part or assembly; sw_drawing makes drawings"),
        };
        var template = Template(app, Json.Str(a, "template"), type, kind);
        var doc = Call(app, "NewDocument", template, 0, 0.0, 0.0)
            ?? throw new BridgeException("COM_NULL", $"SOLIDWORKS did not create a {kind} from {template}");
        if (Docs.Type(doc) != type)
        {
            Call(app, "CloseDoc", Str(Call(doc, "GetTitle")));
            throw new BridgeException("BAD_REQUEST", $"{template} is not a {kind} template");
        }
        b.MarkCreated(doc);
        var units = Objects(Call(doc, "GetUnits"));
        var u = units.Length > 0 ? Convert.ToInt32(units[0]) : -1;
        return Docs.Brief(doc, b).Add("template", template)
            .Add("document_units", u >= 0 && u < K.LengthUnits.Length ? K.LengthUnits[u] : $"unit {u}");
    }

    /// The template to use: the one asked for; else SOLIDWORKS' default for the kind (an install
    /// can leave that preference empty, measured on 2019); else the template SOLIDWORKS itself
    /// would pick; else the first of that kind in its template folders.
    public static string Template(object app, string asked, int docType, string kind)
    {
        if (!string.IsNullOrWhiteSpace(asked))
        {
            var path = asked.Trim().Trim('"');
            if (!File.Exists(path)) throw new BridgeException("FILE_NOT_FOUND", $"the {kind} template {path} does not exist on {Environment.MachineName}");
            return path;
        }
        var preference = docType switch { K.DocPart => K.DefaultTemplatePart, K.DocAssembly => K.DefaultTemplateAssembly, _ => K.DefaultTemplateDrawing };
        var tried = new List<string>();
        foreach (var candidate in new[]
        {
            Str(Call(app, "GetUserPreferenceStringValue", preference)),
            Str(Call(app, "GetDocumentTemplate", docType, "", 0, 0.0, 0.0)),
        })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (File.Exists(candidate)) return candidate;
            tried.Add(candidate);
        }
        var ext = docType switch { K.DocPart => "*.prtdot", K.DocAssembly => "*.asmdot", _ => "*.drwdot" };
        var folders = Str(Call(app, "GetUserPreferenceStringValue", K.FileLocationsTemplates)) ?? "";
        foreach (var folder in folders.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var found = Directory.GetFiles(folder.Trim(), ext);
                Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                if (found.Length > 0) return found[0];
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { /* a stale folder */ }
            tried.Add(Path.Combine(folder.Trim(), ext));
        }
        throw new BridgeException("FILE_NOT_FOUND",
            $"SOLIDWORKS has no {kind} template (looked for {string.Join("; ", tried)}); set one in Tools > Options > Default Templates, or pass template");
    }

    static readonly HashSet<string> Ops = new HashSet<string> { "sketch", "extrude", "revolve", "fillet", "chamfer", "shell", "plane", "delete" };

    /// Run modeling steps on a part, in order. With atomic (the default) a failing step undoes
    /// the steps before it, so the part is either fully built or as it was.
    public static Obj Build(Bridge b, object app, Args a)
    {
        var doc = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocPart);
        var steps = Json.Records(a, "steps");
        if (steps.Count == 0) throw new BridgeException("BAD_REQUEST", "steps is empty");
        if (steps.Count > 60) throw new BridgeException("BAD_REQUEST", "at most 60 steps per call");
        var atomic = Json.Bool(a, "atomic", true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < steps.Count; i++)
        {
            var op = Json.Str(steps[i], "op");
            if (op == null || !Ops.Contains(op))
                throw new BridgeException("BAD_REQUEST", $"steps[{i + 1}].op must be one of {string.Join(", ", Ops)}");
            // A delete cannot be undone, so an atomic call cannot promise to undo it.
            if (op == "delete" && atomic && steps.Count > 1)
                throw new BridgeException("BAD_REQUEST", "delete cannot be undone, so it runs in a call of its own (or pass atomic: false)");
            // Names are checked before anything is made: a feature that exists and then fails to
            // take its name would be left behind.
            var name = Json.Str(steps[i], "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!names.Add(name)) throw new BridgeException("BAD_REQUEST", $"steps[{i + 1}]: two steps are named {name}");
            if (Call(doc, "FeatureByName", name) != null) throw new BridgeException("BAD_REQUEST", $"steps[{i + 1}]: a feature named {name} already exists");
        }

        SessionTools.Activate(app, doc);
        var sketches = Get(doc, "SketchManager");
        if (Get(sketches, "ActiveSketch") != null)
            throw new BridgeException("SW_BUSY", $"a sketch is open for editing in {Docs.Label(doc)}; it must be closed in SOLIDWORKS first");
        // New features land at the rollback bar. Above the end of the tree they would land among
        // the user's features, and "the newest feature" would no longer be the one just made.
        var last = Call(doc, "FeatureByPositionReverse", 0);
        if (last != null && Bool(Call(last, "IsRolledBack")))
            throw new BridgeException("BAD_REQUEST", $"the rollback bar in {Docs.Label(doc)} is above the end of the feature tree; roll it to the end in SOLIDWORKS first");

        var run = new Run(b, app, doc);
        var results = new List<object>();
        Obj failure = null;
        BridgeException busy = null;
        using (new CommandInProgress(app))
        using (new SketchSettings(sketches))
        {
            for (var i = 0; i < steps.Count && failure == null; i++)
            {
                var step = steps[i];
                var op = Json.Str(step, "op");
                var what = $"step {i + 1} ({op})";
                try
                {
                    var r = new Obj().Add("step", i + 1);
                    foreach (var kv in run.Step(op, step, what)) r.Add(kv.Key, kv.Value);
                    results.Add(r);
                }
                catch (BridgeException e) when (e.Code == "SW_GONE")
                {
                    throw new BridgeException(e.Code, $"{e.Message}; this call had made: {string.Join(", ", run.Created)}",
                        new Dictionary<string, object> { ["created"] = run.Created });
                }
                catch (BridgeException e)
                {
                    if (e.Code == "SW_BUSY") busy = e;
                    failure = new Obj().Add("step", i + 1).Add("op", op).Add("code", e.Code).Add("error", e.Message)
                        .Opt("partial_sketch", run.LeaveSketch());
                }
                catch (Exception e)
                {
                    failure = new Obj().Add("step", i + 1).Add("op", op).Add("code", "INTERNAL").Add("error", $"{e.GetType().Name}: {e.Message}")
                        .Opt("partial_sketch", run.LeaveSketch());
                }
            }
            if (busy == null) Call(doc, "ClearSelection2", true);
        }

        (List<string> removed, List<string> kept)? undo = null;
        if (failure != null && atomic)
        {
            // Past the budget, SOLIDWORKS' rejected calls stop being retried; give the undo its own.
            Com.MessageFilter.Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(busy == null ? 120 : 10);
            undo = run.RollBack();
        }
        if (busy != null)
        {
            var facts = new Dictionary<string, object> { ["created"] = run.Created };
            if (undo.HasValue) { facts["rolled_back"] = undo.Value.removed; facts["not_rolled_back"] = undo.Value.kept; }
            throw new BridgeException("SW_BUSY", $"{busy.Message}; " + (undo.HasValue && undo.Value.kept.Count == 0
                ? $"this call's features were removed ({string.Join(", ", undo.Value.removed)})"
                : $"features this call made may remain: {string.Join(", ", undo?.kept ?? run.Created)}"), facts);
        }
        Call(doc, "EditRebuild3");
        var rebuild = WriteTools.WhatsWrong(doc);

        var o = new Obj().Add("ok", failure == null && (bool)rebuild["ok"]).Add("doc", Docs.Brief(doc, b)).Add("steps", results);
        if (failure != null)
        {
            o.Add("failed", failure);
            if (undo.HasValue)
            {
                o.Add("rolled_back", undo.Value.removed);
                if (undo.Value.kept.Count > 0) o.Add("not_rolled_back", undo.Value.kept);
            }
        }
        return o.Add("model", Summary(doc)).Add("rebuild", rebuild);
    }

    /// What a part is now: solid bodies, volume and bounding box. Enough for a caller to check a
    /// build against what it meant to make.
    public static Obj Summary(object doc)
    {
        var bodies = Objects(Call(doc, "GetBodies2", Pick.SolidBody, true)).Length;
        var o = new Obj().Add("solid_bodies", bodies);
        if (bodies == 0) return o;
        var mp = Call(Get(doc, "Extension"), "CreateMassProperty");
        if (mp != null)
        {
            Set(mp, "UseSystemUnits", true);
            o.Add("volume_mm3", Units.Round(Convert.ToDouble(Get(mp, "Volume")) * 1e9))
             .Add("surface_area_mm2", Units.Round(Convert.ToDouble(Get(mp, "SurfaceArea")) * 1e6));
        }
        var box = Doubles(Call(doc, "GetPartBox", true));
        if (box.Length >= 6)
        {
            double[] min = { Units.Mm(box[0]), Units.Mm(box[1]), Units.Mm(box[2]) };
            double[] max = { Units.Mm(box[3]), Units.Mm(box[4]), Units.Mm(box[5]) };
            o.Add("box_mm", new Obj().Add("min", min).Add("max", max)
                .Add("size", new[] { Units.Round(max[0] - min[0]), Units.Round(max[1] - min[1]), Units.Round(max[2] - min[2]) }));
        }
        return o;
    }

    /// One sw_build call: the part, what it created so far, and the sketch the next step uses.
    sealed class Run
    {
        readonly Bridge bridge;
        readonly object app, doc, features, sketches, ext, selection;
        readonly Space model;
        readonly List<string> created = new List<string>();
        string lastSketch;
        string newestBeforeSketch;

        public List<string> Created => new List<string>(created);

        public Run(Bridge bridge, object app, object doc)
        {
            this.bridge = bridge;
            this.app = app;
            this.doc = doc;
            features = Get(doc, "FeatureManager");
            sketches = Get(doc, "SketchManager");
            ext = Get(doc, "Extension");
            selection = Get(doc, "SelectionManager");
            model = Space.Identity(app);
        }

        public Obj Step(string op, Args s, string what) => op switch
        {
            "sketch" => Sketch(s, what),
            "extrude" => Extrude(s, what),
            "revolve" => Revolve(s, what),
            "fillet" => Fillet(s, what),
            "chamfer" => Chamfer(s, what),
            "shell" => Shell(s, what),
            "plane" => Plane(s, what),
            _ => Delete(s, what),
        };

        Obj Sketch(Args s, string what)
        {
            var entities = Json.Records(s, "entities");
            if (entities.Count == 0) throw new BridgeException("BAD_REQUEST", $"{what} needs entities");
            var before = Call(doc, "FeatureByPositionReverse", 0);
            newestBeforeSketch = before == null ? null : Str(Get(before, "Name"));
            Call(doc, "ClearSelection2", true);
            var target = SelectSketchTarget(s, what);
            Call(sketches, "InsertSketch", true);
            var sketch = Get(sketches, "ActiveSketch")
                ?? throw new BridgeException("COM_NULL", $"{what}: SOLIDWORKS did not open a sketch on {target}");
            var toSketch = Get(sketch, "ModelToSketchTransform");
            var warnings = new List<string>();
            var count = 0;
            var e = 0;
            foreach (var ent in entities)
            {
                e++;
                count += Entity(ent, toSketch, $"{what}, entity {e}", warnings);
            }
            Call(sketches, "InsertSketch", true);
            var feature = Newest("ProfileFeature", what);
            var name = Name(feature, s, what);
            lastSketch = name;
            var o = new Obj().Add("op", "sketch").Add("ok", true).Add("feature", name).Add("on", target).Add("segments", count);
            if (warnings.Count > 0) o.Add("warnings", warnings);
            return o;
        }

        string SelectSketchTarget(Args s, string what)
        {
            var at = Json.Triple(s, "face_at", $"{what}: face_at");
            var on = Json.Str(s, "on");
            if (at != null && on != null) throw new BridgeException("BAD_REQUEST", $"{what}: give on or face_at, not both");
            if (at != null)
            {
                var hit = Pick.Require(app, doc, at, "face", what);
                var face = Describe.Face(hit.Entity, hit.Space);
                if ((string)face["surface"] != "plane") throw new BridgeException("BAD_REQUEST", $"{what}: the face at [{Pick.Fmt(at)}] is a {face["surface"]}, and sketches need a flat face");
                if (!Bool(Call(hit.Entity, "Select4", false, Nothing))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select the face at [{Pick.Fmt(at)}]");
                return $"the face at [{Pick.Fmt(at)}]";
            }
            var plane = PlaneFeature(on ?? "front", what);
            if (!Bool(Call(plane, "Select2", false, 0))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select {Str(Get(plane, "Name"))}");
            return Str(Get(plane, "Name"));
        }

        /// front, top and right are the first three planes in the tree, whatever the language of
        /// the template calls them; any other name is a plane feature's own name.
        object PlaneFeature(string on, string what) => Planes.Find(doc, on, what);

        /// Draw one entity; returns the number of sketch segments it made.
        int Entity(Args e, object toSketch, string what, List<string> warnings)
        {
            var type = Json.Str(e, "type") ?? throw new BridgeException("BAD_REQUEST", $"{what} needs type");
            double[] P(string key)
            {
                var p = Json.Triple(e, key, $"{what}: {key}") ?? throw new BridgeException("BAD_REQUEST", $"{what} ({type}) needs {key}");
                return ToSketch(p, toSketch, what, warnings);
            }
            object made;
            int segments;
            switch (type)
            {
                case "line":
                case "centerline":
                {
                    var p1 = P("from");
                    var p2 = P("to");
                    made = type == "line"
                        ? Call(sketches, "CreateLine", p1[0], p1[1], 0.0, p2[0], p2[1], 0.0)
                        : Call(sketches, "CreateCenterLine", p1[0], p1[1], 0.0, p2[0], p2[1], 0.0);
                    segments = 1;
                    break;
                }
                case "polyline":
                {
                    var points = Json.Triples(e, "points", what);
                    var closed = Json.Bool(e, "closed", true);
                    if (points.Count < (closed ? 3 : 2))
                        throw new BridgeException("BAD_REQUEST", $"{what} (polyline) needs at least {(closed ? 3 : 2)} points{(closed ? " to close" : "")}");
                    var sk = points.ConvertAll(p => ToSketch(p, toSketch, what, warnings));
                    segments = 0;
                    made = null;
                    var n = closed ? sk.Count : sk.Count - 1;
                    for (var i = 0; i < n; i++)
                    {
                        var p1 = sk[i];
                        var p2 = sk[(i + 1) % sk.Count];
                        made = Call(sketches, "CreateLine", p1[0], p1[1], 0.0, p2[0], p2[1], 0.0);
                        if (made == null) break;
                        segments++;
                    }
                    break;
                }
                case "rectangle":
                {
                    var p1 = P("corner");
                    var p2 = P("opposite");
                    made = Call(sketches, "CreateCornerRectangle", p1[0], p1[1], 0.0, p2[0], p2[1], 0.0);
                    segments = 4;
                    break;
                }
                case "circle":
                {
                    var c = P("center");
                    var r = Radius(e, what);
                    made = Call(sketches, "CreateCircleByRadius", c[0], c[1], 0.0, r);
                    segments = 1;
                    break;
                }
                case "arc":
                {
                    var p1 = P("from");
                    var p2 = P("to");
                    var p3 = P("through");
                    made = Call(sketches, "Create3PointArc", p1[0], p1[1], 0.0, p2[0], p2[1], 0.0, p3[0], p3[1], 0.0);
                    segments = 1;
                    break;
                }
                default:
                    throw new BridgeException("BAD_REQUEST", $"{what}: type must be line, centerline, polyline, rectangle, circle or arc, not {type}");
            }
            if (made == null) throw new BridgeException("COM_NULL", $"{what}: SOLIDWORKS did not draw the {type}; is it degenerate (zero length or radius)?");
            return segments;
        }

        static double Radius(Args e, string what)
        {
            var d = Json.Opt(e, "diameter");
            var r = Json.Opt(e, "radius");
            var radius = d.HasValue ? d.Value / 2 : r ?? throw new BridgeException("BAD_REQUEST", $"{what} (circle) needs diameter or radius");
            if (radius <= 0) throw new BridgeException("BAD_REQUEST", $"{what}: the circle's size must be positive");
            return radius / 1000;
        }

        /// A model-space point (mm) as sketch coordinates (m). A point off the sketch plane is
        /// projected onto it, and the caller is told how far off it was.
        double[] ToSketch(double[] mm, object toSketch, string what, List<string> warnings)
        {
            var s = model.Through(toSketch, Vec.Metres(mm));
            if (Math.Abs(s[2]) > 1e-6)
                warnings.Add($"{what}: [{Pick.Fmt(mm)}] is {Units.Mm(Math.Abs(s[2]))} mm off the sketch plane and was projected onto it");
            return s;
        }

        Obj Extrude(Args s, string what)
        {
            var sketch = SelectSketch(s, what);
            var cut = Json.Bool(s, "cut", false);
            var end = Json.Str(s, "end", "blind");
            var cond = end switch
            {
                "blind" => K.EndBlind, "through_all" => K.EndThroughAll, "mid_plane" => K.EndMidPlane,
                "through_all_both" => K.EndThroughAllBoth,
                _ => throw new BridgeException("BAD_REQUEST", $"{what}: end must be blind, through_all, through_all_both or mid_plane"),
            };
            var depth = cond == K.EndBlind || cond == K.EndMidPlane ? Positive(s, "depth", what) / 1000 : 0.01;
            var reverse = Json.Bool(s, "reverse", false);
            var draft = Json.Opt(s, "draft") ?? 0;
            var draftRad = Math.Abs(draft) * Math.PI / 180;
            var twoWay = cond == K.EndThroughAllBoth;
            var t1 = twoWay ? K.EndThroughAll : cond;
            var t2 = twoWay ? K.EndThroughAll : K.EndBlind;
            var feature = cut
                ? Call(features, "FeatureCut3", !twoWay, false, reverse, t1, t2, depth, depth, draft != 0, false, draft < 0, false,
                    draftRad, 0.0, false, false, false, false, false, true, true, false, false, false, K.StartSketchPlane, 0.0, false)
                : Call(features, "FeatureExtrusion2", !twoWay, false, reverse, t1, t2, depth, depth, draft != 0, false, draft < 0, false,
                    draftRad, 0.0, false, false, false, false, Json.Bool(s, "merge", true), true, true, K.StartSketchPlane, 0.0, false);
            return Made(feature, s, what, cut ? "cut" : "extrude", $"{(cut ? "cut" : "extrude")} {sketch}");
        }

        Obj Revolve(Args s, string what)
        {
            var sketch = SelectSketch(s, what);
            var cut = Json.Bool(s, "cut", false);
            var angle = Json.Opt(s, "angle") ?? 360;
            if (angle <= 0 || angle > 360) throw new BridgeException("BAD_REQUEST", $"{what}: angle must be in (0, 360] degrees");
            var feature = Call(features, "FeatureRevolve2", true, true, false, cut, Json.Bool(s, "reverse", false), false,
                K.EndBlind, K.EndBlind, angle * Math.PI / 180, 0.0, false, false, 0.0, 0.0, 0, 0.0, 0.0, true, true, true);
            return Made(feature, s, what, "revolve", $"revolve {sketch}; its sketch needs one centerline as the axis and a closed profile on one side of it");
        }

        Obj Fillet(Args s, string what)
        {
            var radius = Positive(s, "radius", what) / 1000;
            var picked = SelectEdgesOrFaces(s, what);
            var feature = Call(features, "FeatureFillet3", K.FilletOptions, radius, 0.0, 0.0, K.FilletSimple, K.FilletOverflowDefault, 0,
                null, null, null, null, null, null, null);
            return Made(feature, s, what, "fillet", $"fillet {picked} at r={Units.Mm(radius)} mm; the radius may be too big for the geometry around it")
                .Add("picked", picked);
        }

        Obj Chamfer(Args s, string what)
        {
            var distance = Positive(s, "distance", what) / 1000;
            var angle = Json.Opt(s, "angle") ?? 45;
            if (angle <= 0 || angle >= 90) throw new BridgeException("BAD_REQUEST", $"{what}: angle must be between 0 and 90 degrees");
            var picked = SelectEdgesOrFaces(s, what);
            var feature = Call(features, "InsertFeatureChamfer", K.ChamferTangentPropagation, K.ChamferAngleDistance, distance,
                angle * Math.PI / 180, 0.0, 0.0, 0.0, 0.0);
            return Made(feature, s, what, "chamfer", $"chamfer {picked}").Add("picked", picked);
        }

        Obj Shell(Args s, string what)
        {
            var thickness = Positive(s, "thickness", what) / 1000;
            Call(doc, "ClearSelection2", true);
            var faces = Json.Triples(s, "faces_at", what);
            var data = MarkData(1);
            foreach (var p in faces)
            {
                var hit = Pick.Require(app, doc, p, "face", what);
                if (!Bool(Call(hit.Entity, "Select4", true, data))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select the face at [{Pick.Fmt(p)}]");
            }
            var before = Newest(null, what, false);
            Call(doc, "InsertFeatureShell", thickness, Json.Bool(s, "outward", false));
            var after = Newest(null, what, false);
            var feature = after != null && Str(Get(after, "Name")) != (before == null ? null : Str(Get(before, "Name"))) ? after : null;
            return Made(feature, s, what, "shell", $"shell {faces.Count} open face(s) at {Units.Mm(thickness)} mm; a wall thicker than a fillet radius often fails")
                .Add("open_faces", faces.Count);
        }

        Obj Plane(Args s, string what)
        {
            var offset = Json.Need(s, "offset", what);
            Call(doc, "ClearSelection2", true);
            var basePlane = PlaneFeature(Json.Str(s, "on") ?? "front", what);
            if (!Bool(Call(basePlane, "Select2", false, 0))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select the base plane");
            var flags = K.RefPlaneDistance | (offset < 0 ? K.RefPlaneFlip : 0);
            var plane = Call(features, "InsertRefPlane", flags, Math.Abs(offset) / 1000, 0, 0.0, 0, 0.0);
            var feature = plane == null ? null : Newest("RefPlane", what, false);
            return Made(feature, s, what, "plane", $"offset {Str(Get(basePlane, "Name"))} by {offset} mm");
        }

        Obj Delete(Args s, string what)
        {
            var names = Json.List(s, "features");
            if (names.Count == 0) throw new BridgeException("BAD_REQUEST", $"{what} needs features");
            foreach (var name in names)
            {
                if (Call(doc, "FeatureByName", name) == null) throw new BridgeException("BAD_REQUEST", $"{what}: no feature named {name}");
                if (!bridge.Built(doc, name))
                    throw new BridgeException("BAD_REQUEST", $"{what}: {name} was not made by sw_build in this bridge session; the bridge deletes only what it built (suppress it with sw_edit instead)");
            }
            var deleted = new List<string>();
            var kept = new List<string>();
            foreach (var name in names)
            {
                var f = Call(doc, "FeatureByName", name);
                if (f == null) continue; // went with a feature deleted before it
                var (gone, stayed) = Remove(f, name);
                deleted.AddRange(gone);
                kept.AddRange(stayed);
            }
            var o = new Obj().Add("op", "delete").Add("ok", true).Add("deleted", deleted);
            if (kept.Count > 0) o.Add("kept", kept).Add("note", "sketches the bridge did not make stay in the part");
            return o;
        }

        /// Delete one feature this bridge built. The sketches it absorbed go with it only if the
        /// bridge built them too: a user's sketch that an extrude here consumed stays, unabsorbed.
        /// Returns what was deleted and which absorbed features were kept.
        (List<string> deleted, List<string> kept) Remove(object feature, string name)
        {
            var absorbed = new List<string>();
            for (var sub = Call(feature, "GetFirstSubFeature"); sub != null; sub = Call(sub, "GetNextSubFeature"))
                absorbed.Add(Str(Get(sub, "Name")));
            DeleteOne(feature, name);
            var deleted = new List<string> { name };
            var kept = new List<string>();
            foreach (var a in absorbed)
            {
                if (!bridge.Built(doc, a)) { kept.Add(a); continue; }
                var f = Call(doc, "FeatureByName", a);
                if (f != null) DeleteOne(f, a);
                deleted.Add(a);
            }
            return (deleted, kept);
        }

        void DeleteOne(object feature, string name)
        {
            Call(doc, "ClearSelection2", true);
            Call(feature, "Select2", false, 0);
            if (!Bool(Call(ext, "DeleteSelection2", K.DeleteNothingElse)))
                throw new BridgeException("COM_ERROR", $"SOLIDWORKS would not delete {name}");
            bridge.Unbuilt(doc, name);
            created.Remove(name);
        }

        /// The sketch an extrude or revolve consumes: the one named, else the newest from this call.
        string SelectSketch(Args s, string what)
        {
            var name = Json.Str(s, "sketch") ?? lastSketch ?? throw new BridgeException("BAD_REQUEST", $"{what} needs sketch (no sketch step came before it)");
            var f = Call(doc, "FeatureByName", name) ?? throw new BridgeException("BAD_REQUEST", $"{what}: no sketch named {name}");
            Call(doc, "ClearSelection2", true);
            if (!Bool(Call(f, "Select2", false, 0))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select {name}");
            return name;
        }

        string SelectEdgesOrFaces(Args s, string what)
        {
            Call(doc, "ClearSelection2", true);
            var edges = Json.Triples(s, "edges_at", what);
            var faces = Json.Triples(s, "faces_at", what);
            if (edges.Count + faces.Count == 0) throw new BridgeException("BAD_REQUEST", $"{what} needs edges_at or faces_at: points on the edges or faces");
            var data = MarkData(1);
            foreach (var (points, kind) in new[] { (edges, "edge"), (faces, "face") })
                foreach (var p in points)
                {
                    var hit = Pick.Require(app, doc, p, kind, what);
                    if (!Bool(Call(hit.Entity, "Select4", true, data))) throw new BridgeException("COM_ERROR", $"{what}: SOLIDWORKS would not select the {kind} at [{Pick.Fmt(p)}]");
                }
            var parts = new List<string>();
            if (edges.Count > 0) parts.Add($"{edges.Count} edge(s)");
            if (faces.Count > 0) parts.Add($"{faces.Count} face(s)");
            return string.Join(" and ", parts);
        }

        object MarkData(int mark)
        {
            var data = Call(selection, "CreateSelectData");
            Set(data, "Mark", mark);
            return data;
        }

        /// Record a created feature, name it if asked, and describe it with its dimensions, which
        /// sw_edit and sw_study can then drive by name.
        Obj Made(object feature, Args s, string what, string op, string attempt)
        {
            if (feature == null) throw new BridgeException("COM_NULL", $"{what}: SOLIDWORKS could not {attempt}");
            var name = Name(feature, s, what);
            var o = new Obj().Add("op", op).Add("ok", true).Add("feature", name).Add("type", Str(Call(feature, "GetTypeName2")));
            var args = new object[] { false };
            var code = Convert.ToInt32(CallRef(feature, "GetErrorCode2", args, 0));
            if (code != 0) o.Add("problem", FeatureErrors.Name(code)).Add("warning_only", Bool(args[0]));
            var dims = new List<object>();
            for (var dd = Call(feature, "GetFirstDisplayDimension"); dd != null; dd = Call(feature, "GetNextDisplayDimension", dd))
            {
                var dim = Call(dd, "GetDimension2", 0);
                if (dim == null) continue;
                var full = ReadTools.DimensionName(dim, name);
                if (full.EndsWith("@" + name, StringComparison.OrdinalIgnoreCase)) dims.Add(ReadTools.Dimension(dim, full));
            }
            if (dims.Count > 0) o.Add("dimensions", dims);
            return o;
        }

        /// Track a feature this step made, then give it the asked name. Tracking comes first, so
        /// a rename SOLIDWORKS refuses cannot leave an untracked feature behind (names that exist
        /// were refused before the call made anything).
        string Name(object feature, Args s, string what)
        {
            var actual = Str(Get(feature, "Name"));
            created.Add(actual);
            bridge.MarkBuilt(doc, actual);
            var want = Json.Str(s, "name");
            if (string.IsNullOrWhiteSpace(want) || want == actual) return actual;
            Set(feature, "Name", want);
            var now = Str(Get(feature, "Name"));
            if (now == actual)
                throw new BridgeException("BAD_REQUEST", $"{what}: SOLIDWORKS would not name {actual} \"{want}\" (names cannot contain @, \" or /)");
            created[created.IndexOf(actual)] = now;
            bridge.Unbuilt(doc, actual);
            bridge.MarkBuilt(doc, now);
            return now;
        }

        /// The last feature in the tree, checked to be of the type just made.
        object Newest(string type, string what, bool required = true)
        {
            var f = Call(doc, "FeatureByPositionReverse", 0);
            if (f != null && (type == null || Str(Call(f, "GetTypeName2")) == type)) return f;
            if (!required) return null;
            throw new BridgeException("COM_NULL", $"{what}: SOLIDWORKS made no {type}");
        }

        /// After a failed step: close a sketch left open. Closing commits it as a feature, half
        /// drawn; it is tracked like any other feature this call made, so a rollback removes it.
        /// Returns its name, or null when no sketch was open.
        public string LeaveSketch()
        {
            try
            {
                if (Get(sketches, "ActiveSketch") == null) return null;
                Call(sketches, "InsertSketch", true);
                var f = Call(doc, "FeatureByPositionReverse", 0);
                var name = f == null ? null : Str(Get(f, "Name"));
                if (name == null || name == newestBeforeSketch || Str(Call(f, "GetTypeName2")) != "ProfileFeature" || created.Contains(name)) return null;
                created.Add(name);
                bridge.MarkBuilt(doc, name);
                return name;
            }
            catch (BridgeException) { return null; /* nothing more to do */ }
        }

        /// Delete, newest first, what this call created. Returns the names removed, and the ones
        /// SOLIDWORKS would not delete. A user's sketch that one of them had absorbed is not this
        /// call's to remove: it stays, and is in neither list.
        public (List<string> removed, List<string> kept) RollBack()
        {
            var removed = new List<string>();
            var kept = new List<string>();
            for (var i = created.Count - 1; i >= 0; i--)
            {
                if (i >= created.Count) continue; // a delete above took more than one entry
                var name = created[i];
                var f = Call(doc, "FeatureByName", name);
                if (f == null) { created.RemoveAt(i); bridge.Unbuilt(doc, name); removed.Add(name); continue; }
                try { removed.AddRange(Remove(f, name).deleted); }
                catch (BridgeException) { kept.Add(name); }
            }
            return (removed, kept);
        }

        static double Positive(Args s, string key, string what)
        {
            var v = Json.Need(s, key, what);
            if (v <= 0) throw new BridgeException("BAD_REQUEST", $"{what}: {key} must be positive");
            return v;
        }
    }

    /// Sketch entities go straight into the sketch: no snapping to nearby geometry and no
    /// automatic relations, so what is drawn is exactly what was asked. Restored afterwards.
    sealed class SketchSettings : IDisposable
    {
        readonly object sketches;
        readonly bool addToDb, display;

        public SketchSettings(object sketches)
        {
            this.sketches = sketches;
            addToDb = Bool(Get(sketches, "AddToDB"));
            display = Bool(Get(sketches, "DisplayWhenAdded"));
            Set(sketches, "AddToDB", true);
            Set(sketches, "DisplayWhenAdded", false);
        }

        public void Dispose()
        {
            try
            {
                Set(sketches, "AddToDB", addToDb);
                Set(sketches, "DisplayWhenAdded", display);
            }
            catch (BridgeException) { /* SOLIDWORKS gone */ }
        }
    }
}

/// Reference planes by role or name.
static class Planes
{
    static readonly string[] Roles = { "front", "top", "right" };

    /// front, top and right are the first three planes in the tree, whatever the template's
    /// language calls them; anything else is a plane feature's own name. `owner` is a part,
    /// an assembly, or a component (whose features are in the assembly's context).
    public static object Find(object owner, string on, string what)
    {
        var role = Array.IndexOf(Roles, on.Trim().ToLowerInvariant());
        if (role < 0)
        {
            var f = Call(owner, "FeatureByName", on);
            if (f == null || Str(Call(f, "GetTypeName2")) != "RefPlane")
                throw new BridgeException("BAD_REQUEST", $"{what}: no plane named {on}; use front, top, right or a plane's name");
            return f;
        }
        var seen = 0;
        for (var f = Call(owner, "FirstFeature"); f != null; f = Call(f, "GetNextFeature"))
            if (Str(Call(f, "GetTypeName2")) == "RefPlane" && seen++ == role) return f;
        throw new BridgeException("COM_NULL", $"{what}: the document has fewer than three reference planes");
    }
}
