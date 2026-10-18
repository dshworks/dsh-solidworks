using System;
using System.Collections.Generic;
using static SwBridge.Com.Dispatch;

namespace SwBridge.Sw;

/// Small vector arithmetic on [x, y, z] arrays. Pure, so the self-test covers it.
static class Vec
{
    public static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    public static double[] Cross(double[] a, double[] b) =>
        new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    public static double Length(double[] a) => Math.Sqrt(Dot(a, a));
    public static double Distance(double[] a, double[] b) => Length(Sub(a, b));
    public static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };

    public static double[] Unit(double[] a)
    {
        var l = Length(a);
        return l < 1e-15 ? a : Scale(a, 1 / l);
    }

    /// The angle between two directions in degrees, 0..180.
    public static double AngleDeg(double[] a, double[] b)
    {
        var c = Dot(Unit(a), Unit(b));
        return Math.Acos(c > 1 ? 1 : c < -1 ? -1 : c) * 180 / Math.PI;
    }

    public static double[] Metres(double[] mm) => Scale(mm, 0.001);
    public static double[] Mm(double[] m) => new[] { Units.Mm(m[0]), Units.Mm(m[1]), Units.Mm(m[2]) };
    public static double[] Round(double[] v) => new[] { Units.Round(v[0]), Units.Round(v[1]), Units.Round(v[2]) };
    public static double[] Take(double[] a, int from) => new[] { a[from], a[from + 1], a[from + 2] };
}

/// A face, edge or vertex found at a point, and the component it belongs to in an assembly.
/// The entity's own geometry is in its part's coordinates; `Space` maps it into the model's.
sealed class Hit
{
    public object Entity;
    public string Kind;
    public double DistanceM;
    public object Component;
    public Space Space;
}

/// The coordinates a part's geometry is reported in, relative to the model a tool speaks of.
/// Identity in a part; a component's transform in an assembly. Points and directions go through
/// SOLIDWORKS' own MathUtility, so the bridge never guesses the matrix convention.
sealed class Space
{
    readonly object math;
    readonly object toModel;
    object toLocal;

    public static Space Identity(object app) => new Space(Call(app, "GetMathUtility"), null);

    public Space(object math, object toModel)
    {
        this.math = math;
        this.toModel = toModel;
    }

    public bool IsIdentity => toModel == null;

    public double[] PointToModel(double[] p) => toModel == null ? p : Through(toModel, p);

    public double[] DirectionToModel(double[] v)
    {
        if (toModel == null) return v;
        var o = Call(math, "CreateVector", new object[] { v });
        return Doubles(Get(Call(o, "MultiplyTransform", toModel), "ArrayData"));
    }

    public double[] PointToLocal(double[] p)
    {
        if (toModel == null) return p;
        toLocal ??= Call(toModel, "Inverse");
        return Through(toLocal, p);
    }

    /// Map a point through any MathTransform, e.g. a sketch's model-to-sketch transform.
    public double[] Through(object xform, double[] p)
    {
        var o = Call(math, "CreatePoint", new object[] { p });
        return Doubles(Get(Call(o, "MultiplyTransform", xform), "ArrayData"));
    }
}

/// Finding faces, edges and vertices by a point on them, without touching the user's selection.
///
/// Every candidate is asked for its closest point to the target (IFace2/IEdge.GetClosestPointOn),
/// so the answer does not depend on the view, the zoom, or what is in front.
static class Pick
{
    /// A point counts as on an entity within this distance (metres): 0.001 mm.
    public const double OnM = 1e-6;
    /// A named-kind pick (edges_at, faces_at) refuses anything farther than this: 0.5 mm.
    public const double NearM = 5e-4;

    public const int SolidBody = 0;

    /// The entity of `kind` (face, edge, vertex) nearest to `pointMm`, or with kind "any" the
    /// lowest-dimension entity the point lies on: a vertex, else an edge, else the nearest face.
    public static Hit At(object app, object doc, double[] pointMm, string kind)
    {
        var target = Vec.Metres(pointMm);
        Hit best = null;
        Hit onEdge = null, onVertex = null;
        foreach (var (body, comp, space) in Bodies(app, doc, target))
        {
            var local = space.PointToLocal(target);
            if (kind == "face" || kind == "any")
                foreach (var f in Objects(Call(body, "GetFaces")))
                    best = Closer(best, f, "face", local, comp, space);
            if (kind == "edge" || kind == "any")
                foreach (var e in Objects(Call(body, "GetEdges")))
                {
                    if (kind == "edge") best = Closer(best, e, "edge", local, comp, space);
                    else onEdge = Closer(onEdge, e, "edge", local, comp, space);
                }
            if (kind == "vertex" || kind == "any")
                foreach (var v in Objects(Call(body, "GetVertices")))
                {
                    var d = Vec.Distance(Vec.Take(Doubles(Call(v, "GetPoint")), 0), local);
                    var hit = new Hit { Entity = v, Kind = "vertex", DistanceM = d, Component = comp, Space = space };
                    if (kind == "vertex") best = best == null || d < best.DistanceM ? hit : best;
                    else if (onVertex == null || d < onVertex.DistanceM) onVertex = hit;
                }
        }
        if (kind == "any")
        {
            if (onVertex != null && onVertex.DistanceM <= OnM) return onVertex;
            if (onEdge != null && onEdge.DistanceM <= OnM) return onEdge;
        }
        return best;
    }

    /// Like At, for a step that needs that kind of entity: refuses when nothing is close enough,
    /// naming the nearest candidate so the caller can correct the point.
    public static Hit Require(object app, object doc, double[] pointMm, string kind, string what)
    {
        var hit = At(app, doc, pointMm, kind);
        if (hit == null)
            throw new BridgeException("BAD_REQUEST", Docs.Type(doc) == K.DocAssembly
                ? $"{what}: nothing at [{Fmt(pointMm)}]: no component comes within {NearM * 1000} mm of it (components move when mated; use where they are now)"
                : $"{what}: the part has no solid body to pick a {kind} on");
        if (hit.DistanceM > NearM)
            throw new BridgeException("BAD_REQUEST",
                $"{what}: no {kind} within {NearM * 1000} mm of [{Fmt(pointMm)}]; the nearest is {Units.Mm(hit.DistanceM)} mm away: " +
                Json.Write(Describe.One(hit)));
        return hit;
    }

    static Hit Closer(Hit best, object entity, string kind, double[] local, object comp, Space space)
    {
        var p = Doubles(Call(entity, "GetClosestPointOn", local[0], local[1], local[2]));
        if (p.Length < 3) return best;
        var d = Vec.Distance(Vec.Take(p, 0), local);
        return best == null || d < best.DistanceM ? new Hit { Entity = entity, Kind = kind, DistanceM = d, Component = comp, Space = space } : best;
    }

    /// The solid bodies a point could be on: a part's own, or those of the assembly components
    /// whose bounding box is near it (assembly bodies are in their part's coordinates).
    public static IEnumerable<(object body, object component, Space space)> Bodies(object app, object doc, double[] nearM)
    {
        var math = Call(app, "GetMathUtility");
        var type = Docs.Type(doc);
        if (type == K.DocPart)
        {
            var identity = new Space(math, null);
            foreach (var b in Objects(Call(doc, "GetBodies2", SolidBody, true))) yield return (b, null, identity);
            yield break;
        }
        if (type != K.DocAssembly) throw new BridgeException("WRONG_DOC_TYPE", $"{Docs.Label(doc)} is a drawing; points name geometry in parts and assemblies");
        foreach (var c in Objects(Call(doc, "GetComponents", false)))
        {
            if (Convert.ToInt32(Call(c, "GetSuppression")) == 0 || Bool(Call(c, "IsHidden", true))) continue;
            if (nearM != null)
            {
                var box = Doubles(Call(c, "GetBox", false, false));
                if (box.Length >= 6 && !Inside(nearM, box, NearM)) continue;
            }
            var space = new Space(math, Get(c, "Transform2"));
            foreach (var b in Objects(Call(c, "GetBodies2", SolidBody))) yield return (b, c, space);
        }
    }

    static bool Inside(double[] p, double[] box, double margin) =>
        p[0] >= box[0] - margin && p[0] <= box[3] + margin &&
        p[1] >= box[1] - margin && p[1] <= box[4] + margin &&
        p[2] >= box[2] - margin && p[2] <= box[5] + margin;

    public static string Fmt(double[] p) =>
        string.Join(", ", Array.ConvertAll(p, v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
}

/// What a face, edge or vertex is, in model-space millimetres and degrees.
static class Describe
{
    // swSurfaceTypes_e, swCurveTypes_e
    const int Plane = 4001, Cylinder = 4002, Cone = 4003, Sphere = 4004, Torus = 4005;
    const int Line = 3001, Circle = 3002;

    public static Obj One(Hit h)
    {
        var o = h.Kind switch
        {
            "face" => Face(h.Entity, h.Space),
            "edge" => Edge(h.Entity, h.Space),
            _ => new Obj().Add("kind", "vertex").Add("point_mm", Vec.Mm(h.Space.PointToModel(Vec.Take(Doubles(Call(h.Entity, "GetPoint")), 0)))),
        };
        if (h.Component != null) o.Add("component", Str(Get(h.Component, "Name2")));
        return o;
    }

    public static Obj Face(object face, Space space)
    {
        var o = new Obj().Add("kind", "face");
        var surface = Call(face, "GetSurface");
        var id = surface == null ? 0 : Convert.ToInt32(Call(surface, "Identity"));
        o.Add("surface", id switch
        {
            Plane => "plane", Cylinder => "cylinder", Cone => "cone", Sphere => "sphere", Torus => "torus", _ => "freeform",
        });
        o.Add("area_mm2", Units.Round(Convert.ToDouble(Call(face, "GetArea")) * 1e6));
        if (id == Plane)
        {
            o.Add("normal", Vec.Round(Vec.Unit(space.DirectionToModel(Vec.Take(Doubles(Get(face, "Normal")), 0)))));
            var p = Doubles(Get(surface, "PlaneParams"));
            if (p.Length >= 6)
            {
                // The plane through the face: its offset along the face normal from the origin.
                var n = Vec.Unit(space.DirectionToModel(Vec.Take(Doubles(Get(face, "Normal")), 0)));
                o.Add("offset_mm", Units.Mm(Vec.Dot(n, space.PointToModel(Vec.Take(p, 3)))));
            }
        }
        else if (id == Cylinder || id == Cone)
        {
            var p = Doubles(id == Cylinder ? Get(surface, "CylinderParams") : Get(surface, "ConeParams"));
            if (p.Length >= 7)
            {
                o.Add("axis_point_mm", Vec.Mm(space.PointToModel(Vec.Take(p, 0))))
                 .Add("axis", Vec.Round(Vec.Unit(space.DirectionToModel(Vec.Take(p, 3)))))
                 .Add(id == Cylinder ? "diameter_mm" : "base_diameter_mm", Units.Mm(2 * p[6]));
                if (id == Cone && p.Length >= 8) o.Add("half_angle_deg", Units.Round(p[7] * 180 / Math.PI));
            }
        }
        else if (id == Sphere)
        {
            var p = Doubles(Get(surface, "SphereParams"));
            if (p.Length >= 4) o.Add("center_mm", Vec.Mm(space.PointToModel(Vec.Take(p, 0)))).Add("diameter_mm", Units.Mm(2 * p[3]));
        }
        var box = Doubles(Call(face, "GetBox"));
        if (box.Length >= 6) o.Add("box_mm", Box(space, box));
        o.Add("edges", Objects(Call(face, "GetEdges")).Length);
        var feature = Call(face, "GetFeature");
        if (feature != null) o.Add("feature", Str(Get(feature, "Name")));
        return o;
    }

    public static Obj Edge(object edge, Space space)
    {
        var o = new Obj().Add("kind", "edge");
        var curve = Call(edge, "GetCurve");
        var id = curve == null ? 0 : Convert.ToInt32(Call(curve, "Identity"));
        o.Add("curve", id switch { Line => "line", Circle => "circle", _ => "freeform" });
        var cp = Doubles(Call(edge, "GetCurveParams2"));
        if (curve != null && cp.Length >= 8)
            o.Add("length_mm", Units.Mm(Convert.ToDouble(Call(curve, "GetLength3", cp[6], cp[7]))));
        if (id == Line && cp.Length >= 6)
            o.Add("direction", Vec.Round(Vec.Unit(space.DirectionToModel(Vec.Sub(Vec.Take(cp, 3), Vec.Take(cp, 0))))));
        if (id == Circle)
        {
            var c = Doubles(Get(curve, "CircleParams"));
            if (c.Length >= 7)
                o.Add("center_mm", Vec.Mm(space.PointToModel(Vec.Take(c, 0))))
                 .Add("axis", Vec.Round(Vec.Unit(space.DirectionToModel(Vec.Take(c, 3)))))
                 .Add("diameter_mm", Units.Mm(2 * c[6]));
        }
        var start = Call(edge, "GetStartVertex");
        var end = Call(edge, "GetEndVertex");
        if (start != null) o.Add("start_mm", Vec.Mm(space.PointToModel(Vec.Take(Doubles(Call(start, "GetPoint")), 0))));
        if (end != null) o.Add("end_mm", Vec.Mm(space.PointToModel(Vec.Take(Doubles(Call(end, "GetPoint")), 0))));
        if (start == null && end == null) o.Add("closed", true);
        return o;
    }

    /// A local box's corners mapped into model space, re-boxed. Exact for identity and for
    /// the axis-aligned rotations components usually have; a bound otherwise.
    static Obj Box(Space space, double[] b)
    {
        var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
        var max = new[] { double.MinValue, double.MinValue, double.MinValue };
        for (var i = 0; i < 8; i++)
        {
            var p = space.PointToModel(new[] { (i & 1) == 0 ? b[0] : b[3], (i & 2) == 0 ? b[1] : b[4], (i & 4) == 0 ? b[2] : b[5] });
            for (var k = 0; k < 3; k++) { min[k] = Math.Min(min[k], p[k]); max[k] = Math.Max(max[k], p[k]); }
        }
        return new Obj().Add("min", Vec.Mm(min)).Add("max", Vec.Mm(max));
    }

    /// How two entities relate: closest distance and points, and for two directions the angle.
    public static Obj Between(object doc, Obj a, Obj b, object ea, object eb)
    {
        var o = new Obj();
        var args = new object[] { ea, eb, null, null };
        var d = Convert.ToDouble(CallRef(doc, "ClosestDistance", args, 2, 3));
        if (d >= 0)
        {
            o.Add("distance_mm", Units.Mm(d));
            var p1 = Doubles(args[2]);
            var p2 = Doubles(args[3]);
            if (p1.Length >= 3 && p2.Length >= 3) o.Add("closest_points_mm", new object[] { Vec.Mm(Vec.Take(p1, 0)), Vec.Mm(Vec.Take(p2, 0)) });
        }
        var da = Direction(a);
        var db = Direction(b);
        if (da != null && db != null)
        {
            var angle = Vec.AngleDeg(da, db);
            o.Add("angle_deg", Units.Round(angle))
             .Add("parallel", angle < 1e-4 || angle > 180 - 1e-4)
             .Add("perpendicular", Math.Abs(angle - 90) < 1e-4);
        }
        return o;
    }

    static double[] Direction(Obj o) =>
        (o.Find("normal") ?? o.Find("axis") ?? o.Find("direction")) as double[];
}
