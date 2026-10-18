using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SwBridge.Mcp;

/// A Model Context Protocol server over one line-delimited JSON-RPC stream.
///
/// The same loop serves stdio (an MCP client spawns swbridge.exe) and the dial-out link (the
/// bridge connects to dsh). Requests run concurrently so sw_status answers while a long call
/// holds SOLIDWORKS; the STA thread still executes SOLIDWORKS calls one at a time.
sealed class Server
{
    static readonly string[] KnownVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    const string Instructions =
        "Drives the SOLIDWORKS instance on the user's Windows desktop. Call sw_status first. " +
        "Lengths are mm, angles degrees, mass kg. Paths are Windows paths on that machine. " +
        "Documents open read-only by default; sw_edit changes memory only; sw_export writes copies; " +
        "sw_save overwrites the source and is off unless the user enabled it. After an edit, read " +
        "the rebuild result: a call that succeeded can still leave features failing.";

    readonly Bridge bridge;
    readonly ToolTable table;
    readonly TextWriter output;
    readonly Action<string> log;
    readonly object writeLock = new object();
    readonly ConcurrentDictionary<string, CancellationTokenSource> inflight = new ConcurrentDictionary<string, CancellationTokenSource>();

    public Server(Bridge bridge, ToolTable table, TextWriter output, Action<string> log)
    {
        this.bridge = bridge;
        this.table = table;
        this.output = output;
        this.log = log;
    }

    /// Serve until the stream ends. In-flight calls are cancelled when it does.
    public async Task Run(TextReader input)
    {
        var pending = new List<Task>();
        string line;
        while ((line = await input.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (line.Trim().Length == 0) continue;
            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(Task.Run(() => Handle(line)));
        }
        foreach (var cts in inflight.Values) cts.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    async Task Handle(string line)
    {
        Dictionary<string, object> msg;
        try { msg = Json.ParseObject(line); }
        catch (Exception) { Send(Error(null, -32700, "parse error")); return; }

        msg.TryGetValue("id", out var id);
        var method = msg.TryGetValue("method", out var m) ? m as string : null;
        var parameters = msg.TryGetValue("params", out var p) ? p as Dictionary<string, object> : null;
        if (method == null) return; // a response to something we never send

        try
        {
            switch (method)
            {
                case "initialize": Reply(id, Initialize(parameters)); return;
                case "ping": Reply(id, new Obj()); return;
                case "tools/list": Reply(id, new Obj().Add("tools", ListTools())); return;
                case "tools/call": Reply(id, await CallTool(id, parameters).ConfigureAwait(false)); return;
                case "resources/list": Reply(id, new Obj().Add("resources", new List<object>())); return;
                case "resources/read": ReadResource(id, parameters); return;
                case "notifications/initialized": return;
                case "notifications/cancelled":
                    var target = Json.Str(parameters, "requestId");
                    if (target != null && inflight.TryGetValue(target, out var cts)) cts.Cancel();
                    return;
                default:
                    if (id != null) Send(Error(id, -32601, $"method not found: {method}"));
                    return;
            }
        }
        catch (Exception e)
        {
            log($"{method} failed: {e}");
            if (id != null) Send(Error(id, -32603, e.Message));
        }
    }

    Obj Initialize(Dictionary<string, object> p)
    {
        var asked = Json.Str(p, "protocolVersion");
        var version = Array.IndexOf(KnownVersions, asked) >= 0 ? asked : KnownVersions[0];
        return new Obj()
            .Add("protocolVersion", version)
            .Add("capabilities", new Obj().Add("tools", new Obj().Add("listChanged", false)).Add("resources", new Obj()))
            .Add("serverInfo", new Obj().Add("name", "dsh-solidworks-bridge").Add("version", Bridge.Version))
            .Add("instructions", Instructions);
    }

    List<object> ListTools()
    {
        var list = new List<object>();
        foreach (var t in table.Tools)
            list.Add(new Obj().Add("name", t.Name).Add("description", t.Description).Add("inputSchema", t.Input)
                .Add("annotations", new Obj().Add("readOnlyHint", !t.Write).Add("destructiveHint", t.Name == "sw_save")
                    .Add("idempotentHint", !t.Write).Add("openWorldHint", false)));
        return list;
    }

    async Task<Obj> CallTool(object id, Dictionary<string, object> p)
    {
        var name = Json.Str(p, "name");
        var args = Json.Map(p, "arguments") ?? new Dictionary<string, object>();
        var tool = table.Find(name ?? "");
        if (tool == null) return Failure("BAD_REQUEST", $"unknown tool {name}", null);

        var key = Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture) ?? Guid.NewGuid().ToString();
        using var cts = new CancellationTokenSource();
        inflight[key] = cts;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await table.Call(bridge, tool, args, cts.Token).ConfigureAwait(false);
            log($"{name} ok {clock.ElapsedMilliseconds} ms");
            var content = new List<object> { new Obj().Add("type", "text").Add("text", Json.Write(result.Facts)) };
            if (result.PngBase64 != null)
                content.Add(new Obj().Add("type", "image").Add("data", result.PngBase64).Add("mimeType", "image/png"));
            return new Obj().Add("content", content).Add("structuredContent", result.Facts);
        }
        catch (BridgeException e)
        {
            log($"{name} {e.Code} {clock.ElapsedMilliseconds} ms: {e.Message}");
            return Failure(e.Code, e.Message, e.Data2);
        }
        catch (Exception e)
        {
            log($"{name} INTERNAL: {e}");
            return Failure("INTERNAL", $"{e.GetType().Name}: {e.Message}", null);
        }
        finally
        {
            inflight.TryRemove(key, out _);
        }
    }

    /// An exported file, by the hash sw_export reported: `swbridge:export/<sha256>`.
    /// Only files this bridge wrote resolve, and the hash is checked again before sending.
    void ReadResource(object id, Dictionary<string, object> p)
    {
        const string prefix = "swbridge:export/";
        var uri = Json.Str(p, "uri") ?? "";
        var path = uri.StartsWith(prefix, StringComparison.Ordinal) ? bridge.ExportedPath(uri.Substring(prefix.Length)) : null;
        if (path == null || !File.Exists(path)) { Send(Error(id, -32002, $"resource not found: {uri}")); return; }
        var length = new FileInfo(path).Length;
        if (length > Bridge.MaxSendBytes)
        {
            Send(Error(id, -32603, $"{Path.GetFileName(path)} is {length / (1024 * 1024)} MB, over the {Bridge.MaxSendBytes / (1024 * 1024)} MB the link sends; it stays at {path}"));
            return;
        }
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
        }
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var now = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (now != uri.Substring(prefix.Length).ToLowerInvariant()) { Send(Error(id, -32603, $"{path} changed since it was exported")); return; }
        }
        Reply(id, new Obj().Add("contents", new List<object>
        {
            new Obj().Add("uri", uri).Add("mimeType", "application/octet-stream").Add("blob", Convert.ToBase64String(bytes)),
        }));
    }

    /// A tool failure the model reads: "CODE: sentence", plus the facts as structured content.
    static Obj Failure(string code, string message, Dictionary<string, object> facts)
    {
        var error = new Obj().Add("code", code).Add("message", message);
        if (facts != null) foreach (var kv in facts) error.Add(kv.Key, kv.Value);
        return new Obj()
            .Add("content", new List<object> { new Obj().Add("type", "text").Add("text", $"{code}: {message}") })
            .Add("structuredContent", new Obj().Add("error", error))
            .Add("isError", true);
    }

    void Reply(object id, object result)
    {
        if (id == null) return;
        Send(new Obj().Add("jsonrpc", "2.0").Add("id", id).Add("result", result));
    }

    static Obj Error(object id, int code, string message) =>
        new Obj().Add("jsonrpc", "2.0").Add("id", id).Add("error", new Obj().Add("code", code).Add("message", message));

    void Send(Obj message)
    {
        var text = Json.Write(message);
        lock (writeLock)
        {
            output.Write(text);
            output.Write('\n');
            output.Flush();
        }
    }
}
