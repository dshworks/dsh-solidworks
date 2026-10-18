using System;
using System.Collections.Generic;
using System.IO;
using SwBridge.Sw;
using static SwBridge.Com.Dispatch;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Tools;

/// A drawing of a model: standard views laid out in projection, at a standard scale that fits
/// the sheet, with the model's dimensions brought in.
static class DrawingTool
{
    static readonly string[] Known = { "front", "top", "right", "back", "left", "bottom", "isometric", "trimetric", "dimetric" };

    public static Obj Drawing(Bridge b, object app, Args a)
    {
        var model = Docs.Require(Docs.Resolve(app, Json.Str(a, "doc")), K.DocPart, K.DocAssembly);
        var modelPath = Str(Call(model, "GetPathName"));
        if (string.IsNullOrEmpty(modelPath))
            throw new BridgeException("BAD_REQUEST", $"{Docs.Label(model)} has no file yet; drawings reference files, so sw_save it with a path first");
        var views = Json.List(a, "views");
        if (views.Count == 0) views = new List<string> { "front", "top", "right", "isometric" };
        views = views.ConvertAll(v => v.Trim().ToLowerInvariant());
        foreach (var v in views)
            if (Array.IndexOf(Known, v) < 0) throw new BridgeException("BAD_REQUEST", $"unknown view {v}; use {string.Join(", ", Known)}");
        if (new HashSet<string>(views).Count != views.Count) throw new BridgeException("BAD_REQUEST", "views lists a view twice");
        var size = ModelSize(model);
        // Every argument is checked before the drawing exists, so a bad one leaves nothing open.
        var projection = Json.Str(a, "projection");
        if (projection != null && projection != "first" && projection != "third") throw new BridgeException("BAD_REQUEST", "projection must be first or third");
        var scaleArg = Json.Str(a, "scale");
        (int num, int den)? forced = null;
        if (scaleArg != null) forced = SheetLayout.ParseScale(scaleArg) ?? throw new BridgeException("BAD_REQUEST", $"scale must look like 1:2 or 2:1, not {scaleArg}");
        var template = ModelTools.Template(app, Json.Str(a, "template"), K.DocDrawing, "drawing");

        var drawing = Call(app, "NewDocument", template, 0, 0.0, 0.0)
            ?? throw new BridgeException("COM_NULL", $"SOLIDWORKS did not create a drawing from {template}");
        var key = Docs.Key(drawing);
        b.MarkCreated(drawing);
        try
        {
            return Lay(b, drawing, template, modelPath, size, views, projection, forced, Json.Bool(a, "dimensions", true));
        }
        catch (BridgeException e) when (e.Code != "SW_GONE")
        {
            // A drawing half made is closed again rather than left open for the user to find.
            try { Call(app, "CloseDoc", Str(Call(drawing, "GetTitle"))); b.Forget(key); }
            catch (BridgeException) { /* report the first failure */ }
            throw;
        }
    }

    static Obj Lay(Bridge b, object drawing, string template, string modelPath, double[] size, List<string> views,
        string projection, (int num, int den)? forced, bool dimensions)
    {
        if (Docs.Type(drawing) != K.DocDrawing) throw new BridgeException("BAD_REQUEST", $"{template} is not a drawing template");
        var sheet = Call(drawing, "GetCurrentSheet") ?? throw new BridgeException("COM_NULL", "the new drawing has no sheet");
        // GetProperties2: paper size, template, scale numerator, denominator, first angle, width, height.
        var props = Doubles(Call(sheet, "GetProperties2"));
        if (props.Length < 7) throw new BridgeException("COM_NULL", "SOLIDWORKS did not describe the new sheet");
        var firstAngle = projection == null ? props[4] != 0 : projection == "first";
        var sheetW = props[5];
        var sheetH = props[6];
        var layout = SheetLayout.Plan(sheetW, sheetH, firstAngle, size, views, forced);
        // Scale and projection go in together through the sheet's own properties; ISheet.SetScale
        // returned without changing anything on 2019 (measured).
        Call(sheet, "SetProperties2", (int)props[0], (int)props[1], (double)layout.Num, (double)layout.Den, firstAngle, sheetW, sheetH, true);
        var now = Doubles(Call(sheet, "GetProperties2"));
        if (now.Length < 5 || Math.Abs(now[2] / now[3] - (double)layout.Num / layout.Den) > 1e-9 || (now[4] != 0) != firstAngle)
            throw new BridgeException("COM_ERROR", $"SOLIDWORKS kept the sheet at {now[2]}:{now[3]}, {(now[4] != 0 ? "first" : "third")} angle");
        var scale = (double)layout.Num / layout.Den;

        var made = new List<object>();
        var warnings = new List<string>();
        foreach (var v in views)
        {
            var (x, y) = layout.Centers[v];
            var name = "*" + char.ToUpperInvariant(v[0]) + v.Substring(1);
            var view = Call(drawing, "CreateDrawViewFromModelView3", modelPath, name, x, y, 0.0);
            if (view == null) { warnings.Add($"SOLIDWORKS did not create the {v} view"); continue; }
            // A new view takes the template's default scale, not the sheet's: follow the sheet.
            Set(view, "UseSheetScale", 1);
            if (Math.Abs(Convert.ToDouble(Get(view, "ScaleDecimal")) - scale) > 1e-9) Set(view, "ScaleDecimal", scale);
            made.Add(new Obj().Add("view", Str(Call(view, "GetName2"))).Add("orientation", v)
                .Add("center_mm", new[] { Units.Mm(x), Units.Mm(y) })
                .Add("scale", SheetLayout.Ratio(Convert.ToDouble(Get(view, "ScaleDecimal")))));
        }
        if (made.Count == 0) throw new BridgeException("COM_NULL", $"SOLIDWORKS made no views of {modelPath}");

        var dims = 0;
        if (dimensions)
            // AllViews, then "eliminate duplicates" (true), no hidden-feature dimensions, own placement.
            dims = Objects(Call(drawing, "InsertModelAnnotations3", K.ImportFromEntireModel, K.InsertDimensionsMarkedForDrawing, true, true, false, false)).Length;
        Call(drawing, "EditRebuild3");
        Call(drawing, "ViewZoomtofit2");
        if (!layout.Fits) warnings.Add($"at {layout.Num}:{layout.Den} the views do not fit the sheet; pass a smaller scale or a larger template");

        var o = new Obj().Add("doc", Docs.Brief(drawing, b)).Add("model", Path.GetFileName(modelPath))
            .Add("sheet", new Obj().Add("name", Str(Call(sheet, "GetName"))).Add("size_mm", new[] { Units.Mm(sheetW), Units.Mm(sheetH) })
                .Add("projection", firstAngle ? "first angle" : "third angle").Add("scale", $"{layout.Num}:{layout.Den}"))
            .Add("views", made).Add("dimensions_imported", dims).Add("template", template);
        if (warnings.Count > 0) o.Add("warnings", warnings);
        return o.Add("next", "sw_view shows the sheet; sw_export with a .pdf path writes it; sw_save with a .slddrw path keeps it");
    }

    /// The model's extent along X, Y and Z in metres.
    static double[] ModelSize(object model)
    {
        var box = Doubles(Docs.Type(model) == K.DocPart ? Call(model, "GetPartBox", true) : Call(model, "GetBox", 0));
        if (box.Length < 6) throw new BridgeException("COM_NULL", $"{Docs.Label(model)} has no geometry to draw");
        var s = new[] { box[3] - box[0], box[4] - box[1], box[5] - box[2] };
        if (s[0] <= 0 && s[1] <= 0 && s[2] <= 0) throw new BridgeException("BAD_REQUEST", $"{Docs.Label(model)} is empty");
        return s;
    }
}

/// Where views go on a sheet. Pure arithmetic, covered by the self-test.
///
/// Views sit in a grid around the front view the way the projection says (third angle: top
/// above, right to the right; first angle: top below, right to the left). Pictorial views take
/// the free corner that keeps the grid smallest. The scale is the largest standard one at which
/// the grid, with gaps, fits the sheet above a band left for the title block.
static class SheetLayout
{
    public sealed class Layout
    {
        public int Num, Den;
        public bool Fits;
        public readonly Dictionary<string, (double x, double y)> Centers = new Dictionary<string, (double, double)>();
    }

    static readonly (int num, int den)[] Scales =
    {
        (50, 1), (20, 1), (10, 1), (5, 1), (2, 1), (1, 1), (1, 2), (1, 5), (1, 10), (1, 20), (1, 50), (1, 100), (1, 200), (1, 500), (1, 1000),
    };

    public const double Gap = 0.015;
    static readonly string[] Pictorial = { "isometric", "trimetric", "dimetric" };

    public static Layout Plan(double sheetW, double sheetH, bool firstAngle, double[] size, List<string> views, (int, int)? forced)
    {
        var cells = Cells(firstAngle, views);
        int minC = int.MaxValue, maxC = int.MinValue, minR = int.MaxValue, maxR = int.MinValue;
        foreach (var (c, r) in cells.Values) { minC = Math.Min(minC, c); maxC = Math.Max(maxC, c); minR = Math.Min(minR, r); maxR = Math.Max(maxR, r); }
        var colW = new double[maxC - minC + 1];
        var rowH = new double[maxR - minR + 1];
        foreach (var kv in cells)
        {
            var (w, h) = Extent(kv.Key, size);
            colW[kv.Value.c - minC] = Math.Max(colW[kv.Value.c - minC], w);
            rowH[kv.Value.r - minR] = Math.Max(rowH[kv.Value.r - minR], h);
        }
        double Sum(double[] a) { var t = 0.0; foreach (var x in a) t += x; return t; }
        int Used(double[] a) { var n = 0; foreach (var x in a) if (x > 0) n++; return n; }

        // Usable area: 4% side margins and top margin; the bottom quarter is the title block's.
        var left = sheetW * 0.04;
        var right = sheetW * 0.96;
        var top = sheetH * 0.96;
        var bottom = sheetH * 0.25;
        var availW = right - left - Gap * Math.Max(0, Used(colW) - 1);
        var availH = top - bottom - Gap * Math.Max(0, Used(rowH) - 1);
        var fitScale = Math.Min(availW / Math.Max(Sum(colW), 1e-9), availH / Math.Max(Sum(rowH), 1e-9));

        var plan = new Layout();
        (plan.Num, plan.Den) = forced ?? Choose(fitScale);
        var s = (double)plan.Num / plan.Den;
        plan.Fits = s <= fitScale * (1 + 1e-9);

        var totalW = s * Sum(colW) + Gap * Math.Max(0, Used(colW) - 1);
        var totalH = s * Sum(rowH) + Gap * Math.Max(0, Used(rowH) - 1);
        var x0 = left + Math.Max(0, (right - left - totalW) / 2);
        var y0 = top - Math.Max(0, (top - bottom - totalH) / 2);
        var colX = new double[colW.Length];
        var rowY = new double[rowH.Length];
        var x = x0;
        for (var i = 0; i < colW.Length; i++)
        {
            colX[i] = x + s * colW[i] / 2;
            if (colW[i] > 0) x += s * colW[i] + Gap;
        }
        var y = y0;
        for (var i = 0; i < rowH.Length; i++)
        {
            rowY[i] = y - s * rowH[i] / 2;
            if (rowH[i] > 0) y -= s * rowH[i] + Gap;
        }
        foreach (var kv in cells) plan.Centers[kv.Key] = (colX[kv.Value.c - minC], rowY[kv.Value.r - minR]);
        return plan;
    }

    /// The largest standard scale not above the one that just fits.
    static (int, int) Choose(double fit)
    {
        foreach (var sc in Scales)
            if ((double)sc.num / sc.den <= fit * (1 + 1e-9)) return sc;
        return Scales[Scales.Length - 1];
    }

    /// Grid cell (column, row; row 0 at the top) for each view.
    public static Dictionary<string, (int c, int r)> Cells(bool firstAngle, List<string> views)
    {
        var cells = new Dictionary<string, (int c, int r)>();
        var hasFront = views.Contains("front");
        if (!hasFront)
        {
            for (var i = 0; i < views.Count; i++) cells[views[i]] = (i, 0);
            return cells;
        }
        var place = firstAngle
            ? new Dictionary<string, (int, int)> { ["front"] = (1, 1), ["top"] = (1, 2), ["bottom"] = (1, 0), ["right"] = (0, 1), ["left"] = (2, 1), ["back"] = (3, 1) }
            : new Dictionary<string, (int, int)> { ["front"] = (1, 1), ["top"] = (1, 0), ["bottom"] = (1, 2), ["right"] = (2, 1), ["left"] = (0, 1), ["back"] = (3, 1) };
        foreach (var v in views) if (place.TryGetValue(v, out var cell)) cells[v] = cell;
        var corners = new[] { (2, 0), (0, 0), (2, 2), (0, 2), (3, 0), (3, 2) };
        foreach (var v in views)
        {
            if (Array.IndexOf(Pictorial, v) < 0) continue;
            (int, int)? best = null;
            var bestArea = int.MaxValue;
            foreach (var corner in corners)
            {
                if (cells.ContainsValue(corner)) continue;
                var area = GridArea(cells.Values, corner);
                if (area < bestArea) { bestArea = area; best = corner; }
            }
            cells[v] = best ?? (4, 1);
        }
        return cells;
    }

    static int GridArea(IEnumerable<(int c, int r)> cells, (int c, int r) extra)
    {
        int minC = extra.c, maxC = extra.c, minR = extra.r, maxR = extra.r;
        foreach (var (c, r) in cells) { minC = Math.Min(minC, c); maxC = Math.Max(maxC, c); minR = Math.Min(minR, r); maxR = Math.Max(maxR, r); }
        return (maxC - minC + 1) * (maxR - minR + 1);
    }

    /// A view's width and height on paper at 1:1, from the model's X, Y, Z extent.
    /// Pictorial views use the isometric projection of the bounding box.
    public static (double w, double h) Extent(string view, double[] s) => view switch
    {
        "front" or "back" => (s[0], s[1]),
        "top" or "bottom" => (s[0], s[2]),
        "right" or "left" => (s[2], s[1]),
        _ => (0.7071 * (s[0] + s[2]), 0.8165 * s[1] + 0.4082 * (s[0] + s[2])),
    };

    public static (int, int)? ParseScale(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0].Trim(), out var n) || !int.TryParse(parts[1].Trim(), out var d) || n <= 0 || d <= 0) return null;
        return (n, d);
    }

    /// A scale factor as a ratio string: 0.5 -> "1:2", 2 -> "2:1".
    public static string Ratio(double s)
    {
        if (s <= 0) return "?";
        return s >= 1 ? $"{Math.Round(s, 3)}:1" : $"1:{Math.Round(1 / s, 3)}";
    }
}
