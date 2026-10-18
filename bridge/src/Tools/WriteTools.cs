using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

static class WriteTools
{
    static readonly HashSet<string> ExportFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".step", ".stp", ".igs", ".iges", ".x_t", ".x_b", ".sat", ".stl", ".pdf", ".dxf", ".dwg",
        ".eprt", ".easm", ".edrw", ".jpg", ".png", ".tif", ".sldprt", ".sldasm", ".slddrw",
    };

    /// Apply a batch of in-memory changes in a fixed order, rebuild once, report each change.
    /// One failing change does not stop the others; every one is reported with its own outcome.
    public static Obj Edit(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var type = Docs.Type(doc);
        var changes = new List<object>();

        var configuration = Json.Str(a, "configuration");
        if (!string.IsNullOrEmpty(configuration))
        {
            Docs.Require(doc, K.DocPart, K.DocAssembly);
            var before = Docs.ActiveConfiguration(doc);
            var ok = Bool(Call(doc, "ShowConfiguration2", configuration));
            changes.Add(Change("configuration", configuration, before, Docs.ActiveConfiguration(doc), ok,
                ok ? null : $"no configuration named {configuration}"));
        }

        foreach (var (key, action) in new[] { ("suppress", K.Suppress), ("unsuppress", K.Unsuppress) })
            foreach (var name in Json.List(a, key))
            {
                var feature = Call(doc, "FeatureByName", name);
                if (feature == null) { changes.Add(Change(key, name, null, null, false, "no feature with this name")); continue; }
                var ok = Bool(Call(feature, "SetSuppression2", action, K.ThisConfiguration, null));
                changes.Add(Change(key, name, null, null, ok, ok ? null : "SOLIDWORKS refused"));
            }

        var variables = Json.Map(a, "variables");
        if (variables != null && variables.Count > 0) EditVariables(doc, variables, changes);

        var dimensions = Json.Map(a, "dimensions");
        var dimensionNames = new List<string>();
        if (dimensions != null)
        {
            var scope = Json.Str(a, "dimension_scope", "this") == "all" ? K.SetInAllConfigurations : K.SetInThisConfiguration;
            foreach (var kv in dimensions)
            {
                var dim = Call(doc, "Parameter", kv.Key);
                if (dim == null) { changes.Add(Change("dimension", kv.Key, null, null, false, "no dimension with this name; sw_parameters lists them")); continue; }
                var value = Json.Num(kv.Value);
                if (value == null) { changes.Add(Change("dimension", kv.Key, null, null, false, "value must be a number")); continue; }
                var kind = Convert.ToInt32(Call(dim, "GetType"));
                var before = ReadTools.Dimension(dim, kv.Key).Find("value");
                var status = Convert.ToInt32(Call(dim, "SetSystemValue3", Units.ToSi(value.Value, kind), scope, null));
                changes.Add(Change("dimension", kv.Key, before, null, status == 0, SetValueError(status))
                    .Add("unit", Units.Label(kind)));
                dimensionNames.Add(kv.Key);
            }
        }

        var properties = Json.Map(a, "properties");
        if (properties != null && properties.Count > 0)
        {
            var config = Json.Str(a, "property_scope", "file") == "configuration" ? Docs.ActiveConfiguration(doc) ?? "" : "";
            var manager = Get(Get(doc, "Extension"), "CustomPropertyManager", config);
            foreach (var kv in properties)
            {
                var text = kv.Value == null ? "" : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                var (before, _) = ReadTools.Property(manager, kv.Key);
                Call(manager, "Add3", kv.Key, K.CustomInfoText, text, K.CustomPropertyReplaceValue);
                var (after, resolved) = ReadTools.Property(manager, kv.Key);
                changes.Add(Change("property", kv.Key, before, after, after == text, after == text ? null : "the value did not stick")
                    .Opt("resolved", resolved != after ? resolved : null)
                    .Add("scope", config.Length == 0 ? "file" : config));
            }
        }

        Obj rebuild = null;
        if (Json.Bool(a, "rebuild", true) && type != K.DocDrawing)
        {
            Call(doc, "EditRebuild3");
            rebuild = WhatsWrong(doc);
        }

        // Read dimensions back after the rebuild: what SOLIDWORKS holds now, not what was asked.
        foreach (Obj c in changes)
            if ((string)c["kind"] == "dimension" && (bool)c["ok"])
            {
                var dim = Call(doc, "Parameter", (string)c["name"]);
                if (dim != null) c["after"] = ReadTools.Dimension(dim, (string)c["name"]).Find("value");
            }

        var allOk = true;
        foreach (Obj c in changes) allOk &= (bool)c["ok"];
        if (rebuild != null) allOk &= (bool)rebuild["ok"];
        return new Obj().Add("ok", allOk).Add("doc", Docs.Brief(doc, b)).Add("changes", changes).Opt("rebuild", rebuild);
    }

    static void EditVariables(object doc, Dictionary<string, object> variables, List<object> changes)
    {
        var mgr = Call(doc, "GetEquationMgr");
        var count = Convert.ToInt32(Call(mgr, "GetCount"));
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = Equations.VariableName(Str(Get(mgr, "Equation", i)));
            if (name != null && !index.ContainsKey(name)) index[name] = i;
        }
        var touched = new List<(Obj change, int i)>();
        foreach (var kv in variables)
        {
            var expr = kv.Value == null ? "" : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture).Trim();
            if (!index.TryGetValue(kv.Key, out var i)) { changes.Add(Change("variable", kv.Key, null, null, false, "no global variable with this name")); continue; }
            if (expr.Length == 0) { changes.Add(Change("variable", kv.Key, null, null, false, "expression is empty")); continue; }
            var before = Str(Get(mgr, "Equation", i));
            Set(mgr, "Equation", i, $"\"{kv.Key}\" = {expr}");
            var c = Change("variable", kv.Key, before, null, true, null);
            changes.Add(c);
            touched.Add((c, i));
        }
        if (touched.Count == 0) return;
        Call(mgr, "EvaluateAll");
        foreach (var (c, i) in touched)
        {
            var now = Str(Get(mgr, "Equation", i));
            c["after"] = now;
            c.Add("value", Units.Round(Convert.ToDouble(Get(mgr, "Value", i))));
            if (now == (string)c["before"]) { c["ok"] = false; c.Add("error", "SOLIDWORKS kept the old equation; check the expression"); }
        }
    }

    static Obj Change(string kind, string name, object before, object after, bool ok, string error) =>
        new Obj().Add("kind", kind).Add("name", name).Add("ok", ok).Add("before", before).Add("after", after).Opt("error", error);

    static string SetValueError(int status) => status switch
    {
        0 => null,
        2 => "invalid value",
        3 => "the dimension is driven; change what drives it",
        4 => "the model is not loaded",
        5 => "the owning feature is frozen",
        _ => "SOLIDWORKS refused the value",
    };

    public static Obj Rebuild(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ran = Json.Bool(a, "force", true) ? Bool(Call(doc, "ForceRebuild3", false)) : Bool(Call(doc, "EditRebuild3"));
        var o = WhatsWrong(doc);
        if (!ran) o["ok"] = false;
        return o.Add("doc", Docs.Label(doc)).Add("seconds", Math.Round(clock.Elapsed.TotalSeconds, 2));
    }

    /// Every feature SOLIDWORKS flags after a rebuild. ok is false when any is an error;
    /// warnings alone keep ok true.
    public static Obj WhatsWrong(object doc)
    {
        var args = new object[] { null, null, null };
        CallRef(Get(doc, "Extension"), "GetWhatsWrong", args, 0, 1, 2);
        var features = Objects(args[0]);
        var codes = Objects(args[1]);
        var warnings = Objects(args[2]);
        var list = new List<object>();
        var ok = true;
        for (var i = 0; i < features.Length; i++)
        {
            var warning = i < warnings.Length && Bool(warnings[i]);
            if (!warning) ok = false;
            list.Add(new Obj()
                .Add("feature", features[i] == null ? null : Str(Get(features[i], "Name")))
                .Add("type", features[i] == null ? null : Str(Call(features[i], "GetTypeName2")))
                .Add("problem", i < codes.Length ? FeatureErrors.Name(Convert.ToInt32(codes[i])) : "unknown")
                .Add("warning_only", warning));
        }
        return new Obj().Add("ok", ok).Add("problems", list);
    }

    public static Obj Export(Bridge b, object app, Args a)
    {
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var target = b.OutputPath(Json.Str(a, "path"));
        var ext = Path.GetExtension(target);
        if (!ExportFormats.Contains(ext))
            throw new BridgeException("BAD_REQUEST", $"cannot export {ext}; supported: {string.Join(" ", ExportFormats)}");
        if (File.Exists(target))
        {
            if (!Json.Bool(a, "overwrite", false)) throw new BridgeException("FILE_EXISTS", $"{target} exists; pass overwrite: true to replace it");
            File.Delete(target);
        }

        var type = Docs.Type(doc);
        object data = Nothing;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && type == K.DocDrawing)
        {
            data = Call(app, "GetExportFileData", K.ExportPdfData);
            Call(data, "SetSheets", Json.Str(a, "sheets", "all") == "active" ? K.ExportCurrentSheet : K.ExportAllSheets, null);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var args = new object[] { target, K.SaveAsCurrentVersion, K.SaveSilent | K.SaveCopy, data, 0, 0 };
        var ok = Bool(CallRef(Get(doc, "Extension"), "SaveAs", args, 4, 5));
        var errors = Convert.ToInt32(args[4]);
        var warnings = Convert.ToInt32(args[5]);
        if (!ok || !File.Exists(target))
            throw new BridgeException("SAVE_FAILED", $"SOLIDWORKS did not write {target}: {Reasons(errors, K.SaveErrors)}",
                new Dictionary<string, object> { ["errors"] = K.Bits(errors, K.SaveErrors), ["warnings"] = K.Bits(warnings, K.SaveWarnings) });
        var facts = FileFacts(target);
        b.MarkExported((string)facts["sha256"], target);
        return facts.Add("doc", Docs.Label(doc)).Add("format", ext.TrimStart('.').ToLowerInvariant())
            .Add("warnings", K.Bits(warnings, K.SaveWarnings)).Add("seconds", Math.Round(clock.Elapsed.TotalSeconds, 2));
    }

    public static Obj Save(Bridge b, object app, Args a)
    {
        if (!b.Options.AllowSave)
            throw new BridgeException("SAVE_DISABLED",
                "this bridge does not save over source files; to keep the change, sw_export a native copy " +
                "(e.g. bracket-v2.sldprt), or restart the bridge with --allow-save");
        var doc = Docs.Resolve(app, Json.Str(a, "doc"));
        var path = Str(Call(doc, "GetPathName"));
        if (string.IsNullOrEmpty(path)) throw new BridgeException("SAVE_FAILED", "the document has never been saved; sw_export it with a native extension");
        if (Bool(Call(doc, "IsOpenedReadOnly")))
            throw new BridgeException("SAVE_FAILED", $"{Path.GetFileName(path)} is open read-only; sw_close it and sw_open with read_only: false");

        var before = new FileInfo(path);
        var stamp = (before.LastWriteTimeUtc, before.Length);
        var args = new object[] { K.SaveSilent, 0, 0 };
        var ok = Bool(CallRef(doc, "Save3", args, 1, 2));
        var errors = Convert.ToInt32(args[1]);
        var warnings = Convert.ToInt32(args[2]);
        if (!ok) throw new BridgeException("SAVE_FAILED", $"SOLIDWORKS did not save {path}: {Reasons(errors, K.SaveErrors)}");

        // Save3 reports success on a file with the read-only attribute and writes nothing.
        var after = new FileInfo(path);
        if ((after.LastWriteTimeUtc, after.Length) == stamp)
            throw new BridgeException("SAVE_FAILED", $"SOLIDWORKS reported a save but {path} did not change; is the file marked read-only in Windows?");
        return FileFacts(path).Add("doc", Docs.Label(doc)).Add("warnings", K.Bits(warnings, K.SaveWarnings));
    }

    static string Reasons(int errors, Dictionary<int, string> names)
    {
        var r = K.Bits(errors, names);
        return r.Count == 0 ? "no reason given" : string.Join("; ", r);
    }

    /// Path, size and SHA-256, read while SOLIDWORKS may still hold the file open.
    public static Obj FileFacts(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return new Obj().Add("path", path).Add("bytes", stream.Length)
            .Add("sha256", BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
    }
}
