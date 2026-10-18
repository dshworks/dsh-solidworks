using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SwBridge.Tools;
using Args = System.Collections.Generic.Dictionary<string, object>;

namespace SwBridge.Mcp;

/// What a tool call produced: structured facts, plus a PNG for sw_view.
sealed class ToolResult
{
    public Obj Facts;
    public string PngBase64;
}

/// The tools: names, descriptions and schemas from protocol/tools.json (the contract shared
/// with the dsh plugin), handlers from this table. Construction fails if the two disagree.
sealed class ToolTable
{
    public sealed class Tool
    {
        public string Name;
        public string Description;
        public Dictionary<string, object> Input;
        public bool Write;
        public TimeSpan Budget;
        public Func<Bridge, object, Args, ToolResult> Run;
    }

    static readonly Dictionary<string, Func<Bridge, object, Args, ToolResult>> handlers =
        new Dictionary<string, Func<Bridge, object, Args, ToolResult>>
        {
            ["sw_status"] = Facts(SessionTools.Status),
            ["sw_open"] = Facts(SessionTools.Open),
            ["sw_new"] = Facts(ModelTools.New),
            ["sw_close"] = Facts(SessionTools.Close),
            ["sw_inspect"] = Facts(ReadTools.Inspect),
            ["sw_features"] = Facts(ReadTools.Features),
            ["sw_parameters"] = Facts(ReadTools.Parameters),
            ["sw_components"] = Facts(ReadTools.Components),
            ["sw_mass_properties"] = Facts(ReadTools.Mass),
            ["sw_probe"] = Facts(ProbeTool.Probe),
            ["sw_edit"] = Facts(WriteTools.Edit),
            ["sw_build"] = Facts(ModelTools.Build),
            ["sw_study"] = Facts(StudyTool.Study),
            ["sw_rebuild"] = Facts(WriteTools.Rebuild),
            ["sw_assemble"] = Facts(AssemblyTools.Assemble),
            ["sw_interference"] = Facts(AssemblyTools.Interference),
            ["sw_bom"] = Facts(AssemblyTools.Bom),
            ["sw_drawing"] = Facts(DrawingTool.Drawing),
            ["sw_export"] = Facts(WriteTools.Export),
            ["sw_save"] = Facts(WriteTools.Save),
            ["sw_view"] = (b, app, a) =>
            {
                var p = ViewTool.Render(b, app, a);
                return new ToolResult { Facts = p.Facts, PngBase64 = p.PngBase64 };
            },
        };

    public readonly List<Tool> Tools = new List<Tool>();
    public readonly Dictionary<string, object> Contract;
    readonly Dictionary<string, Tool> byName = new Dictionary<string, Tool>();

    public ToolTable()
    {
        Contract = Json.ParseObject(ReadContract());
        var seen = new HashSet<string>();
        foreach (Dictionary<string, object> t in (IEnumerable)Contract["tools"])
        {
            var name = (string)t["name"];
            if (!handlers.TryGetValue(name, out var run)) throw new InvalidOperationException($"tools.json lists {name}, which has no handler");
            seen.Add(name);
            var tool = new Tool
            {
                Name = name,
                Description = (string)t["description"],
                Input = (Dictionary<string, object>)t["input"],
                Write = (bool)t["write"],
                Budget = TimeSpan.FromSeconds(Convert.ToDouble(t["budget_s"])),
                Run = run,
            };
            Tools.Add(tool);
            byName[name] = tool;
        }
        foreach (var name in handlers.Keys)
            if (!seen.Contains(name)) throw new InvalidOperationException($"handler {name} is missing from tools.json");
    }

    public Tool Find(string name) => byName.TryGetValue(name, out var t) ? t : null;

    public IEnumerable<string> ErrorCodes => ((Dictionary<string, object>)Contract["errors"]).Keys;

    public static string ReadContract()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tools.json")
            ?? throw new InvalidOperationException("tools.json is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<ToolResult> Call(Bridge bridge, Tool tool, Args args, CancellationToken cancel)
    {
        if (tool.Name == "sw_status") return await Status(bridge, tool, args, cancel).ConfigureAwait(false);
        return await bridge.OnSw(tool.Name, tool.Budget, cancel, app => tool.Run(bridge, app, args)).ConfigureAwait(false);
    }

    /// Status is the call that must answer when nothing else can: if SOLIDWORKS is stuck on a
    /// job, it reports the bridge, the stuck job and any dialog SOLIDWORKS has open.
    async Task<ToolResult> Status(Bridge bridge, Tool tool, Args args, CancellationToken cancel)
    {
        if (args != null && args.TryGetValue("pid", out var pid) && pid != null) bridge.Session.PinnedPid = Convert.ToInt32(pid);
        try
        {
            return await bridge.OnSw(tool.Name, tool.Budget, cancel, app => tool.Run(bridge, app, args)).ConfigureAwait(false);
        }
        catch (BridgeException e) when (e.Code == "SW_BUSY" || e.Code == "TIMEOUT" || e.Code == "SW_NOT_RUNNING")
        {
            var dialogs = Sw.Dialogs.Open();
            var facts = new Dictionary<string, object> { ["dialogs"] = dialogs };
            var busy = bridge.Busy;
            if (busy.HasValue) facts["running"] = new Obj().Add("tool", busy.Value.name).Add("seconds", (int)busy.Value.elapsed.TotalSeconds);
            var hint = "";
            foreach (Obj d in dialogs)
                hint += $"; SOLIDWORKS (pid {d["pid"]}) is waiting on a dialog: \"{d["message"]}\"";
            if (dialogs.Count > 0) hint += " - someone at the Windows desktop must answer it";
            throw new BridgeException(e.Code, e.Message + hint, facts);
        }
    }

    static Func<Bridge, object, Args, ToolResult> Facts(Func<Bridge, object, Args, Obj> f) =>
        (b, app, a) => new ToolResult { Facts = f(b, app, a) };
}
