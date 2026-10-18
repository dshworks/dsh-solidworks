using System;
using System.Collections.Generic;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

/// A design study: try several values for the model's dimensions and variables, rebuild and
/// measure each, then put every value back. Each variant starts from the original values, so
/// variants are independent of their order. One call instead of an edit-and-measure loop, and
/// the model ends where it started.
static class StudyTool
{
    const int MaxVariants = 50;

    public static Obj Study(Bridge b, object app, Args a)
    {
        var doc = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocPart, K.DocAssembly);
        var variants = Json.Records(a, "variants");
        if (variants.Count == 0) throw new BridgeException("BAD_REQUEST", "variants is empty");
        if (variants.Count > MaxVariants) throw new BridgeException("BAD_REQUEST", $"at most {MaxVariants} variants per call");

        // Everything a variant touches must exist before anything changes.
        var dims = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var vars = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < variants.Count; i++)
        {
            var d = Json.Map(variants[i], "dimensions");
            var v = Json.Map(variants[i], "variables");
            if ((d == null || d.Count == 0) && (v == null || v.Count == 0))
                throw new BridgeException("BAD_REQUEST", $"variants[{i + 1}] changes nothing; give dimensions or variables");
            if (d != null)
                foreach (var kv in d)
                {
                    if (Json.Num(kv.Value) == null) throw new BridgeException("BAD_REQUEST", $"variants[{i + 1}]: {kv.Key} must be a number");
                    if (!dims.ContainsKey(kv.Key))
                        dims[kv.Key] = Call(doc, "Parameter", kv.Key) ?? throw new BridgeException("BAD_REQUEST", $"no dimension named {kv.Key}; sw_parameters lists them");
                }
            if (v != null)
                foreach (var kv in v)
                {
                    if (string.IsNullOrWhiteSpace(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture)))
                        throw new BridgeException("BAD_REQUEST", $"variants[{i + 1}]: {kv.Key} needs an expression");
                    vars.Add(kv.Key);
                }
        }
        var mgr = Call(doc, "GetEquationMgr");
        var eq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = mgr == null ? 0 : Convert.ToInt32(Call(mgr, "GetCount"));
        for (var i = 0; i < count; i++)
        {
            var name = Equations.VariableName(Str(Get(mgr, "Equation", i)));
            if (name != null && !eq.ContainsKey(name)) eq[name] = i;
        }
        foreach (var name in vars)
            if (!eq.ContainsKey(name)) throw new BridgeException("BAD_REQUEST", $"no global variable named {name}");

        var original = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in dims) original[kv.Key] = Doubles(Call(kv.Value, "GetSystemValue3", K.ThisConfiguration, null))[0];
        var originalEq = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in vars) originalEq[name] = Str(Get(mgr, "Equation", eq[name]));

        var rows = new List<object>();
        Exception broke = null;
        try
        {
            for (var i = 0; i < variants.Count; i++)
                rows.Add(Variant(doc, variants[i], i + 1, dims, mgr, eq, original, originalEq));
        }
        catch (Exception e) when (!(e is BridgeException gone && gone.Code == "SW_GONE"))
        {
            broke = e; // restore below either way
        }

        // Put every value back, whatever happened above, and check that it stuck. Past the
        // budget SOLIDWORKS' rejected calls stop being retried, so the restore gets its own
        // window, and each value is restored on its own: one failure does not skip the rest.
        Com.MessageFilter.Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        var mismatches = new List<object>();
        void Attempt(string name, Action action)
        {
            try { action(); }
            catch (BridgeException e) { mismatches.Add(new Obj().Add("name", name).Add("error", e.Message)); }
        }
        Reset(dims, original, originalEq, mgr, eq, null, Attempt);
        Attempt("rebuild", () => Call(doc, "EditRebuild3"));
        foreach (var kv in dims)
            Attempt(kv.Key, () =>
            {
                var now = Doubles(Call(kv.Value, "GetSystemValue3", K.ThisConfiguration, null))[0];
                if (Math.Abs(now - original[kv.Key]) > 1e-12 + 1e-9 * Math.Abs(original[kv.Key]))
                    mismatches.Add(new Obj().Add("name", kv.Key).Add("was", original[kv.Key]).Add("now", now));
            });
        foreach (var kv in originalEq)
            Attempt(kv.Key, () =>
            {
                if (Str(Get(mgr, "Equation", eq[kv.Key])) != kv.Value) mismatches.Add(new Obj().Add("name", kv.Key).Add("was", kv.Value));
            });

        var o = new Obj().Add("doc", Docs.Label(doc)).Add("configuration", Docs.ActiveConfiguration(doc))
            .Add("variants", rows).Add("restored", mismatches.Count == 0);
        if (mismatches.Count > 0) o.Add("not_restored", mismatches);
        if (broke != null) o.Add("stopped", $"variant {rows.Count + 1}: {broke.Message}");
        return o;
    }

    /// Set every studied value back to its original: all equations, and the dimensions not in
    /// skipDims (those are about to be set anyway).
    static void Reset(Dictionary<string, object> dims, Dictionary<string, double> original, Dictionary<string, string> originalEq,
        object mgr, Dictionary<string, int> eq, Dictionary<string, object> skipDims, Action<string, Action> attempt)
    {
        foreach (var kv in dims)
            if (skipDims == null || !skipDims.ContainsKey(kv.Key))
                attempt(kv.Key, () => Call(kv.Value, "SetSystemValue3", original[kv.Key], K.SetInThisConfiguration, null));
        foreach (var kv in originalEq)
            attempt(kv.Key, () => Set(mgr, "Equation", eq[kv.Key], kv.Value));
        if (originalEq.Count > 0) attempt("equations", () => Call(mgr, "EvaluateAll"));
    }

    /// Apply one variant on top of the original values (not on top of the previous variant),
    /// rebuild, and measure.
    static Obj Variant(object doc, Args v, int index, Dictionary<string, object> dims, object mgr, Dictionary<string, int> eq,
        Dictionary<string, double> original, Dictionary<string, string> originalEq)
    {
        var row = new Obj().Add("variant", index).Opt("label", Json.Str(v, "label"));
        var values = new Obj();
        var refused = new List<string>();
        var d = Json.Map(v, "dimensions");
        var vv = Json.Map(v, "variables");
        // Every value touched by any variant goes back to its original first, so a value this
        // variant names but SOLIDWORKS refuses is caught below rather than measured at the
        // previous variant's setting.
        Reset(dims, original, originalEq, mgr, eq, d, (name, action) => action());
        if (d != null)
            foreach (var kv in d)
            {
                var dim = dims[kv.Key];
                var kind = Convert.ToInt32(Call(dim, "GetType"));
                var status = Convert.ToInt32(Call(dim, "SetSystemValue3", Units.ToSi(Json.Num(kv.Value).Value, kind), K.SetInThisConfiguration, null));
                if (status != 0) refused.Add($"{kv.Key}: {WriteTools.SetValueError(status)}");
            }
        var vars = vv;
        if (vars != null)
            foreach (var kv in vars)
            {
                var expr = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
                var text = $"\"{kv.Key}\" = {expr}";
                Set(mgr, "Equation", eq[kv.Key], text);
                if (Str(Get(mgr, "Equation", eq[kv.Key])) == originalEq[kv.Key] && text != originalEq[kv.Key])
                    refused.Add($"{kv.Key}: SOLIDWORKS kept the original equation; check the expression");
            }
        if (originalEq.Count > 0) Call(mgr, "EvaluateAll");
        Call(doc, "EditRebuild3");
        var rebuild = WriteTools.WhatsWrong(doc);

        if (d != null)
            foreach (var kv in d) values.Add(kv.Key, ReadTools.Dimension(dims[kv.Key], kv.Key).Find("value"));
        if (vars != null)
            foreach (var kv in vars) values.Add(kv.Key, Units.Round(Convert.ToDouble(Get(mgr, "Value", eq[kv.Key]))));
        row.Add("values", values).Add("ok", (bool)rebuild["ok"] && refused.Count == 0);
        if (refused.Count > 0) row.Add("refused", refused);
        if (((List<object>)rebuild["problems"]).Count > 0) row.Add("problems", rebuild["problems"]);

        var mp = Call(Get(doc, "Extension"), "CreateMassProperty");
        if (mp != null)
        {
            Set(mp, "UseSystemUnits", true);
            var com = Doubles(Get(mp, "CenterOfMass"));
            row.Add("mass_kg", Units.Round(Convert.ToDouble(Get(mp, "Mass"))))
               .Add("volume_mm3", Units.Round(Convert.ToDouble(Get(mp, "Volume")) * 1e9))
               .Add("surface_area_mm2", Units.Round(Convert.ToDouble(Get(mp, "SurfaceArea")) * 1e6))
               .Opt("center_of_mass_mm", com.Length >= 3 ? Vec.Mm(Vec.Take(com, 0)) : null);
        }
        var box = Doubles(Docs.Type(doc) == K.DocPart ? Call(doc, "GetPartBox", true) : Call(doc, "GetBox", 0));
        if (box.Length >= 6) row.Add("size_mm", new[] { Units.Mm(box[3] - box[0]), Units.Mm(box[4] - box[1]), Units.Mm(box[5] - box[2]) });
        return row;
    }
}
