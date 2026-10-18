using System;
using System.Collections.Generic;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

/// Looking at specific geometry: what is at a point, or what the user has selected.
static class ProbeTool
{
    const int MaxPoints = 8;

    public static Obj Probe(Bridge b, object app, Args a)
    {
        var doc = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocPart, K.DocAssembly);
        var points = Json.Triples(a, "at", "sw_probe");
        if (points.Count > MaxPoints) throw new BridgeException("BAD_REQUEST", $"at most {MaxPoints} points per call");

        var described = new List<Obj>();
        var entities = new List<object>();
        string source;
        using var fast = new CommandInProgress(app);
        if (points.Count > 0)
        {
            source = "points";
            foreach (var p in points)
            {
                var hit = Pick.At(app, doc, p, "any");
                if (hit == null) { described.Add(new Obj().Add("at_mm", p).Add("kind", "nothing")); entities.Add(null); continue; }
                var d = Describe.One(hit);
                var o = new Obj().Add("at_mm", p);
                foreach (var kv in d) o.Add(kv.Key, kv.Value);
                if (hit.DistanceM > Pick.OnM) o.Add("off_by_mm", Units.Mm(hit.DistanceM));
                described.Add(o);
                entities.Add(hit.Entity);
            }
        }
        else
        {
            source = "selection";
            Selection(app, doc, described, entities);
        }

        var result = new Obj().Add("doc", Docs.Label(doc)).Add("source", source).Add("entities", described);
        if (described.Count == 2 && entities[0] != null && entities[1] != null)
            result.Add("between", Describe.Between(doc, described[0], described[1], entities[0], entities[1]));
        if (source == "selection" && described.Count == 0)
            result.Add("hint", "nothing is selected in SOLIDWORKS; ask the user to select faces, edges or features, or pass at points");
        return result;
    }

    /// What is selected in the document now, read without changing it.
    static void Selection(object app, object doc, List<Obj> described, List<object> entities)
    {
        var mgr = Get(doc, "SelectionManager");
        var count = Convert.ToInt32(Call(mgr, "GetSelectedObjectCount2", -1));
        var math = Call(app, "GetMathUtility");
        var assembly = Docs.Type(doc) == K.DocAssembly;
        for (var i = 1; i <= Math.Min(count, 50); i++)
        {
            var type = Convert.ToInt32(Call(mgr, "GetSelectedObjectType3", i, -1));
            var obj = Call(mgr, "GetSelectedObject6", i, -1);
            var comp = assembly ? Call(mgr, "GetSelectedObjectsComponent4", i, -1) : null;
            var space = new Space(math, comp == null ? null : Get(comp, "Transform2"));
            Obj o;
            object entity = null;
            switch (type)
            {
                case K.SelFaces: o = Describe.Face(obj, space); entity = obj; break;
                case K.SelEdges: o = Describe.Edge(obj, space); entity = obj; break;
                case K.SelVertices:
                    o = Describe.One(new Hit { Entity = obj, Kind = "vertex", Space = space });
                    entity = obj;
                    break;
                case K.SelComponents:
                    o = new Obj().Add("kind", "component").Add("name", Str(Get(obj, "Name2"))).Add("file", Str(Call(obj, "GetPathName")));
                    comp = null;
                    break;
                case K.SelDimensions:
                {
                    var dim = Call(obj, "GetDimension2", 0);
                    o = dim == null ? new Obj().Add("kind", "dimension") : ReadTools.Dimension(dim, Str(Get(dim, "FullName"))).Add("kind", "dimension");
                    break;
                }
                case K.SelBodyFeatures:
                case K.SelDatumPlanes:
                case K.SelDatumAxes:
                case K.SelSketches:
                case K.SelMates:
                    o = Feature(obj, type);
                    break;
                default:
                    o = new Obj().Add("kind", "other").Add("select_type", type);
                    break;
            }
            if (comp != null) o.Add("component", Str(Get(comp, "Name2")));
            var point = Doubles(Call(mgr, "GetSelectionPoint2", i, -1));
            if (point.Length >= 3 && Vec.Length(Vec.Take(point, 0)) > 0) o.Add("picked_at_mm", Vec.Mm(Vec.Take(point, 0)));
            described.Add(o);
            entities.Add(entity);
        }
        if (count > 50) described.Add(new Obj().Add("kind", "more").Add("count", count - 50));
    }

    /// A selected tree item. Planes, axes and sketches come back as their own interfaces, which
    /// have no name; their feature does, so ask for it and fall back to the type alone.
    static Obj Feature(object obj, int type)
    {
        var o = new Obj().Add("kind", type switch
        {
            K.SelDatumPlanes => "plane", K.SelDatumAxes => "axis", K.SelSketches => "sketch", K.SelMates => "mate", _ => "feature",
        });
        try { o.Add("name", Str(Get(obj, "Name"))).Add("type", Str(Call(obj, "GetTypeName2"))); }
        catch (BridgeException e) when (e.Code == "MEMBER_MISSING") { /* not a feature interface */ }
        return o;
    }
}
