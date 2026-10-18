using System;
using System.Collections.Generic;
using System.IO;
using SwBridge.Mcp;
using SwBridge.Sw;
using SwBridge.Tools;

namespace SwBridge;

/// `swbridge selftest`: the parts that need no SOLIDWORKS, run on every CI build on Windows.
/// The HMAC vector is shared with the plugin's tests, so both ends of the link agree byte for byte.
static class SelfTest
{
    static int failures;

    public static int Run()
    {
        var table = new ToolTable(); // throws if tools.json and the handlers disagree
        Check("tools in contract", table.Tools.Count == 21, $"{table.Tools.Count}");
        Check("error codes include TIMEOUT", new List<string>(table.ErrorCodes).Contains("TIMEOUT"), "");

        Check("hmac vector", Link.Mac("test-token", "dsh-solidworks/1 bridge AAAA BBBB") ==
            "4ff47302c3f3c645618457d02ce8b167f0ec61c149f20f21553e44f7b3a08389", Link.Mac("test-token", "dsh-solidworks/1 bridge AAAA BBBB"));

        Check("json escapes", Json.Write(new Obj().Add("p", "C:\\a\"b\n\u4e2d")) == "{\"p\":\"C:\\\\a\\\"b\\n\u4e2d\"}", Json.Write(new Obj().Add("p", "C:\\a\"b\n\u4e2d")));
        Check("json doubles round-trip", Json.Write(0.1 + 0.2) == "0.30000000000000004", Json.Write(0.1 + 0.2));
        Check("json NaN is null", Json.Write(double.NaN) == "null", "");
        Check("obj keeps key order on update", Json.Write(new Obj().Add("a", 1).Add("b", 2).Add("a", 3)) == "{\"a\":3,\"b\":2}", "");

        Check("global variable", Equations.VariableName("\"Width\" = 60") == "Width", "");
        Check("global variable with spaces", Equations.VariableName("  \"Bolt Hole\"= \"Width\" / 2") == "Bolt Hole", "");
        Check("dimension equation is not a variable", Equations.VariableName("\"D1@Sketch1\" = \"Width\"") == null, "");

        Check("mm from m", Units.FromSi(0.05, K.DimLinear) == 50, $"{Units.FromSi(0.05, K.DimLinear)}");
        Check("deg from rad", Units.FromSi(Math.PI / 2, K.DimAngular) == 90, $"{Units.FromSi(Math.PI / 2, K.DimAngular)}");
        Check("m from mm", Math.Abs(Units.ToSi(60, K.DimLinear) - 0.06) < 1e-12, "");
        Check("release 2016 SP1", Session.Release("24.1.0") == (2016, 1), "");
        Check("release 2019 SP0", Session.Release("27.0.0") == (2019, 0), "");
        Check("load error bits", string.Join(",", K.Bits(2 | 8192, K.LoadErrors)) == "file not found,saved by a newer SOLIDWORKS", string.Join(",", K.Bits(2 | 8192, K.LoadErrors)));

        var parsed = Link.ParseAddresses("10.211.55.2,192.168.1.5:47321");
        Check("addresses share a port", parsed.Count == 2 && parsed[0] == ("10.211.55.2", 47321) && parsed[1] == ("192.168.1.5", 47321), $"{parsed.Count}");
        Check("explicit ports", Link.ParseAddresses("a:1,b:2")[1] == ("b", 2), "");

        Check("dimension owner from full name", ReadTools.NameFromFull("D1@Above@Part2.SLDPRT") == "D1@Above", "");
        Check("odd full name falls back", ReadTools.NameFromFull("D1") == null, "");
        Check("angle between axes", Math.Abs(Vec.AngleDeg(new double[] { 1, 0, 0 }, new double[] { 0, 2, 0 }) - 90) < 1e-12, "");
        Check("opposite normals are 180", Math.Abs(Vec.AngleDeg(new double[] { 0, 1, 0 }, new double[] { 0, -1, 0 }) - 180) < 1e-9, "");
        Check("letterbox wide window", ViewTool.Letterbox(1000, 400, 2.0) == (800, 400), $"{ViewTool.Letterbox(1000, 400, 2.0)}");
        Check("letterbox tall request", ViewTool.Letterbox(500, 400, 2.0) == (500, 250), $"{ViewTool.Letterbox(500, 400, 2.0)}");
        Check("csv quoting", BomTable.Csv(new List<string[]> { new[] { "a,b", "say \"hi\"", "x" } }) == "\"a,b\",\"say \"\"hi\"\"\",x\r\n", "");
        Check("scale parse", SheetLayout.ParseScale("1:2") == (1, 2) && SheetLayout.ParseScale("1/2") == null && SheetLayout.ParseScale("0:2") == null, "");
        Check("scale ratio", SheetLayout.Ratio(0.5) == "1:2" && SheetLayout.Ratio(2) == "2:1", SheetLayout.Ratio(0.5));
        Layouts();
        Names();

        var root = Path.Combine(Path.GetTempPath(), "swbridge-selftest");
        var b = new Bridge(new Options { OutDir = root });
        Check("bare name goes to out", b.OutputPath("a.step") == Path.Combine(root, "a.step"), b.OutputPath("a.step"));
        Check("relative stays inside", b.OutputPath("sub\\a.step") == Path.Combine(root, "sub", "a.step"), "");
        Denied(b, "..\\escape.step");
        Denied(b, "C:\\Windows\\a.step");
        Denied(b, root + "-sibling\\a.step");
        b.Dispose();

        Console.WriteLine(failures == 0 ? "selftest ok" : $"selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    /// The drawing layout on an ANSI A sheet (279.4 x 215.9 mm): projection placement, alignment
    /// of projected views, and the scale chosen for a small and a large model.
    static void Layouts()
    {
        var views = new List<string> { "front", "top", "right", "isometric" };
        var third = SheetLayout.Cells(false, views);
        Check("third angle: top above, right to the right, iso in the free corner",
            third["front"] == (1, 1) && third["top"] == (1, 0) && third["right"] == (2, 1) && third["isometric"] == (2, 0), "");
        var first = SheetLayout.Cells(true, views);
        Check("first angle: top below, right to the left, iso where the grid stays 2 x 2",
            first["front"] == (1, 1) && first["top"] == (1, 2) && first["right"] == (0, 1) && first["isometric"] == (0, 2), "");
        var plate = SheetLayout.Plan(0.2794, 0.2159, false, new[] { 0.1, 0.02, 0.06 }, views, null);
        Check("a 100 x 20 x 60 plate fits A at 1:1", plate.Num == 1 && plate.Den == 1 && plate.Fits, $"{plate.Num}:{plate.Den}");
        Check("projected views line up", Math.Abs(plate.Centers["front"].x - plate.Centers["top"].x) < 1e-12 &&
            Math.Abs(plate.Centers["front"].y - plate.Centers["right"].y) < 1e-12, "");
        Check("top view is above front in third angle", plate.Centers["top"].y > plate.Centers["front"].y, "");
        var big = SheetLayout.Plan(0.2794, 0.2159, false, new[] { 2.0, 0.5, 1.0 }, views, null);
        Check("a 2 m model goes to 1:20", big.Num == 1 && big.Den == 20 && big.Fits, $"{big.Num}:{big.Den}");
        var forced = SheetLayout.Plan(0.2794, 0.2159, false, new[] { 2.0, 0.5, 1.0 }, views, (1, 1));
        Check("a forced scale that cannot fit says so", !forced.Fits, "");
        var row = SheetLayout.Cells(false, new List<string> { "isometric", "top" });
        Check("without a front view, views go in a row", row["isometric"] == (0, 0) && row["top"] == (1, 0), "");
    }

    /// Which open document a name means. The case that went wrong: a part "flange" and its drawing
    /// "flange - Sheet1" open together, and "flange.slddrw" resolved to the part.
    static void Names()
    {
        const string part = @"C:\out\flange.sldprt", drawing = @"C:\out\flange.slddrw";
        Check("a drawing's file name never means the part", Docs.Match(part, "flange", K.DocPart, "flange.slddrw") == 0, "");
        Check("a drawing's file name means the drawing", Docs.Match(drawing, "flange - Sheet1", K.DocDrawing, "flange.slddrw") == 2, "");
        Check("a path is exact", Docs.Match(part, "flange", K.DocPart, @"c:\OUT\flange.SLDPRT") == 2, "");
        Check("a bare name is the part titled so, and only loosely its drawing", Docs.Match(part, "flange", K.DocPart, "flange") == 2 &&
            Docs.Match(drawing, "flange - Sheet1", K.DocDrawing, "flange") == 1, "");
        Check("a title with its extension hidden still matches", Docs.Match(part, "flange", K.DocPart, "flange.sldprt") == 2, "");
        Check("a new document matches its title", Docs.Match("", "Part7", K.DocPart, "part7") == 2, "");
        Check("other names do not match", Docs.Match(part, "flange", K.DocPart, "shaft") == 0, "");
    }

    static void Denied(Bridge b, string path)
    {
        try
        {
            b.OutputPath(path);
            Check($"denied {path}", false, "allowed");
        }
        catch (BridgeException e)
        {
            Check($"denied {path}", e.Code == "PATH_DENIED", e.Code);
        }
    }

    static void Check(string name, bool ok, string got)
    {
        if (ok) return;
        failures++;
        Console.WriteLine($"FAIL {name}: {got}");
    }
}
