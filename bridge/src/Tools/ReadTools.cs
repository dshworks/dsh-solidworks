using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SwBridge.Com;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

static class ReadTools
{
    public static Obj Inspect(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var type = Docs.Type(doc);
        var o = Docs.Brief(doc, b);

        var units = Objects(Call(doc, "GetUnits"));
        if (units.Length > 0)
        {
            var u = Convert.ToInt32(units[0]);
            o.Add("document_units", u >= 0 && u < K.LengthUnits.Length ? K.LengthUnits[u] : $"unit {u}");
        }

        if (type == K.DocDrawing)
        {
            var sheet = Call(doc, "GetCurrentSheet");
            o.Add("sheets", Strings(Call(doc, "GetSheetNames")))
             .Opt("active_sheet", sheet == null ? null : Str(Call(sheet, "GetName")));
        }
        else
        {
            var active = Docs.ActiveConfiguration(doc);
            o.Add("configurations", Strings(Call(doc, "GetConfigurationNames")));
            if (type == K.DocPart)
            {
                var args = new object[] { active ?? "", "" };
                var material = Str(CallRef(doc, "GetMaterialPropertyName2", args, 1));
                o.Add("material", string.IsNullOrEmpty(material) ? null : material);
            }
            o.Add("box_mm", Box(doc, type));
        }

        var ext = Get(doc, "Extension");
        o.Add("properties", new Obj()
            .Add("file", Properties(Get(ext, "CustomPropertyManager", "")))
            .Opt("configuration", type == K.DocDrawing ? null : Properties(Get(ext, "CustomPropertyManager", Docs.ActiveConfiguration(doc) ?? ""))));

        // No feature count here: GetFeatureCount includes folders and scene items, so it
        // disagreed with sw_features (20 against 7 on the box sample) and the model quoted it.
        var counts = new Obj();
        if (type == K.DocPart) counts.Add("solid_bodies", Objects(Call(doc, "GetBodies2", 0, true)).Length);
        if (type == K.DocAssembly)
            counts.Add("components_top_level", Convert.ToInt32(Call(doc, "GetComponentCount", true)))
                  .Add("components_total", Convert.ToInt32(Call(doc, "GetComponentCount", false)));
        o.Add("counts", counts);
        return o;
    }

    static Obj Box(object doc, int type)
    {
        var box = Doubles(type == K.DocPart ? Call(doc, "GetPartBox", true) : Call(doc, "GetBox", 0));
        if (box.Length < 6) return null;
        double[] min = { Units.Mm(box[0]), Units.Mm(box[1]), Units.Mm(box[2]) };
        double[] max = { Units.Mm(box[3]), Units.Mm(box[4]), Units.Mm(box[5]) };
        return new Obj().Add("min", min).Add("max", max)
            .Add("size", new[] { Units.Round(max[0] - min[0]), Units.Round(max[1] - min[1]), Units.Round(max[2] - min[2]) });
    }

    /// Custom properties as name -> { value (as typed), resolved (evaluated) }.
    public static Obj Properties(object manager)
    {
        var o = new Obj();
        if (manager == null) return o;
        foreach (var name in Strings(Call(manager, "GetNames")))
        {
            var (raw, resolved) = Property(manager, name);
            o.Add(name, raw == resolved ? (object)raw : new Obj().Add("value", raw).Add("resolved", resolved));
        }
        return o;
    }

    public static (string raw, string resolved) Property(object manager, string name)
    {
        var args = new object[] { name, false, "", "", false };
        var status = Convert.ToInt32(CallRef(manager, "Get5", args, 2, 3, 4));
        return status == 1 ? (null, null) : ((string)args[2], (string)args[3]); // 1 = swCustomInfoGetResult_NotPresent
    }

    public static Obj Features(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var offset = Math.Max(0, Json.Int(a, "offset", 0));
        var limit = Clamp(Json.Int(a, "limit", 200), 1, 2000);
        var folders = Json.Bool(a, "include_folders", false);

        var list = new List<object>();
        var seen = 0;
        var errors = 0;
        using (new CommandInProgress(app))
        {
            for (var f = Call(doc, "FirstFeature"); f != null; f = Call(f, "GetNextFeature"))
            {
                var type = Str(Call(f, "GetTypeName2"));
                if (!folders && K.FolderFeatureTypes.Contains(type)) continue;
                var entry = Feature(f, type);
                if (entry.ContainsKey("error")) errors++;
                if (seen++ < offset || list.Count >= limit) continue;

                var subs = new List<object>();
                for (var s = Call(f, "GetFirstSubFeature"); s != null; s = Call(s, "GetNextSubFeature"))
                    subs.Add(Feature(s, Str(Call(s, "GetTypeName2"))));
                if (subs.Count > 0) entry.Add("children", subs);
                list.Add(entry);
            }
        }
        return new Obj().Add("doc", Docs.Label(doc)).Add("total", seen).Add("offset", offset)
            .Add("returned", list.Count).Add("with_errors", errors).Add("features", list);
    }

    static Obj Feature(object f, string type)
    {
        var o = new Obj().Add("name", Str(Get(f, "Name"))).Add("type", type);
        var suppressed = Objects(Call(f, "IsSuppressed2", K.ThisConfiguration, null));
        if (suppressed.Length > 0 && Bool(suppressed[0])) o.Add("suppressed", true);
        var args = new object[] { false };
        var code = Convert.ToInt32(CallRef(f, "GetErrorCode2", args, 0));
        if (code != 0) o.Add("error", FeatureErrors.Name(code)).Add("warning_only", Bool(args[0]));
        return o;
    }

    public static Obj Parameters(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var filter = Json.Str(a, "filter", "");
        var limit = Clamp(Json.Int(a, "limit", 500), 1, 5000);

        var dims = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        using (new CommandInProgress(app))
        {
            for (var f = Call(doc, "FirstFeature"); f != null; f = Call(f, "GetNextFeature"))
            {
                var owner = Str(Get(f, "Name"));
                for (var dd = Call(f, "GetFirstDisplayDimension"); dd != null; dd = Call(f, "GetNextDisplayDimension", dd))
                {
                    var dim = Call(dd, "GetDimension2", 0);
                    if (dim == null) continue;
                    var name = $"{Str(Get(dim, "Name"))}@{owner}";
                    if (!seen.Add(name)) continue;
                    if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    total++;
                    if (dims.Count < limit) dims.Add(Dimension(dim, name));
                }
            }
        }

        var equations = new List<object>();
        var mgr = Call(doc, "GetEquationMgr");
        var count = mgr == null ? 0 : Convert.ToInt32(Call(mgr, "GetCount"));
        for (var i = 0; i < count; i++)
        {
            var text = Str(Get(mgr, "Equation", i));
            var variable = Equations.VariableName(text);
            if (filter.Length > 0 && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            equations.Add(new Obj().Add("index", i).Add("equation", text)
                .Add("value", Units.Round(Convert.ToDouble(Get(mgr, "Value", i))))
                .Opt("global_variable", variable));
        }

        return new Obj().Add("doc", Docs.Label(doc)).Add("configuration", Docs.ActiveConfiguration(doc))
            .Add("dimensions_total", total).Add("dimensions", dims).Add("equations", equations);
    }

    public static Obj Dimension(object dim, string name)
    {
        var kind = Convert.ToInt32(Call(dim, "GetType"));
        var si = Doubles(Call(dim, "GetSystemValue3", K.ThisConfiguration, null));
        var o = new Obj().Add("name", name);
        if (si.Length > 0) o.Add("value", Units.FromSi(si[0], kind)).Add("unit", Units.Label(kind));
        if (Convert.ToInt32(Get(dim, "DrivenState")) == K.DimDriven) o.Add("driven", true);
        if (Bool(Get(dim, "ReadOnly"))) o.Add("read_only", true);
        return o;
    }

    public static Obj Components(Bridge b, object app, Args a)
    {
        var doc = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocAssembly);
        var depth = Clamp(Json.Int(a, "depth", 1), 1, 20);
        var limit = Clamp(Json.Int(a, "limit", 500), 1, 10000);
        var budget = new[] { limit };
        List<object> tree;
        using (new CommandInProgress(app))
            tree = Level(Objects(Call(doc, "GetComponents", true)), depth, budget);
        return new Obj().Add("doc", Docs.Label(doc)).Add("configuration", Docs.ActiveConfiguration(doc))
            .Add("truncated", budget[0] <= 0).Add("components", tree);
    }

    static List<object> Level(object[] comps, int depth, int[] budget)
    {
        var list = new List<object>();
        foreach (var c in comps)
        {
            if (budget[0]-- <= 0) break;
            var state = Convert.ToInt32(Call(c, "GetSuppression"));
            var o = new Obj()
                .Add("name", Str(Get(c, "Name2")))
                .Add("file", Str(Call(c, "GetPathName")))
                .Add("configuration", Str(Get(c, "ReferencedConfiguration")))
                .Add("state", K.ComponentState.TryGetValue(state, out var s) ? s : $"state {state}")
                .Add("fixed", Bool(Call(c, "IsFixed")));
            var xf = Get(c, "Transform2");
            if (xf != null)
            {
                var m = Doubles(Get(xf, "ArrayData"));
                if (m.Length >= 12) o.Add("position_mm", new[] { Units.Mm(m[9]), Units.Mm(m[10]), Units.Mm(m[11]) });
            }
            if (depth > 1 && state != 0)
            {
                var kids = Objects(Call(c, "GetChildren"));
                if (kids.Length > 0) o.Add("children", Level(kids, depth - 1, budget));
            }
            list.Add(o);
        }
        return list;
    }

    public static Obj Mass(Bridge b, object app, Args a)
    {
        var doc = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocPart, K.DocAssembly);
        var mp = Call(Get(doc, "Extension"), "CreateMassProperty");
        if (mp == null) throw new BridgeException("COM_NULL", $"SOLIDWORKS returned no mass properties for {Docs.Label(doc)}; does it have a solid body?");
        Set(mp, "UseSystemUnits", true);
        var com = Doubles(Get(mp, "CenterOfMass"));
        return new Obj()
            .Add("doc", Docs.Label(doc))
            .Add("configuration", Docs.ActiveConfiguration(doc))
            .Add("mass_kg", Units.Round(Convert.ToDouble(Get(mp, "Mass"))))
            .Add("volume_mm3", Units.Round(Convert.ToDouble(Get(mp, "Volume")) * 1e9))
            .Add("surface_area_mm2", Units.Round(Convert.ToDouble(Get(mp, "SurfaceArea")) * 1e6))
            .Add("density_kg_m3", Units.Round(Convert.ToDouble(Get(mp, "Density"))))
            .Add("center_of_mass_mm", com.Length >= 3 ? new[] { Units.Mm(com[0]), Units.Mm(com[1]), Units.Mm(com[2]) } : null);
    }

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}

/// Global variables are equations whose left side is a quoted name with no "@"; dimension
/// equations name a dimension ("D1@Sketch1"). SOLIDWORKS 2016 before SP4 has no
/// IEquationMgr.GlobalVariable, so the text is the only portable test.
static class Equations
{
    static readonly Regex Lhs = new Regex("^\\s*\"([^\"@]+)\"\\s*=", RegexOptions.CultureInvariant);

    public static string VariableName(string equation)
    {
        if (string.IsNullOrEmpty(equation)) return null;
        var m = Lhs.Match(equation);
        return m.Success ? m.Groups[1].Value : null;
    }
}

/// Speeds up out-of-process traversal: while CommandInProgress is true SOLIDWORKS skips UI
/// updates between calls. Always reset, so a failed traversal cannot leave it stuck.
sealed class CommandInProgress : IDisposable
{
    readonly object app;
    readonly bool wasSet;

    public CommandInProgress(object app)
    {
        this.app = app;
        wasSet = Bool(Get(app, "CommandInProgress"));
        if (!wasSet) Set(app, "CommandInProgress", true);
    }

    public void Dispose()
    {
        if (wasSet) return;
        try { Set(app, "CommandInProgress", false); } catch (BridgeException) { /* SOLIDWORKS gone */ }
    }
}
