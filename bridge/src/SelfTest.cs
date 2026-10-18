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
        Check("tools in contract", table.Tools.Count == 13, $"{table.Tools.Count}");
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
