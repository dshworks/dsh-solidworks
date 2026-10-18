using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwBridge.Mcp;

namespace SwBridge;

/// The dial-out link: the bridge connects to dsh, then serves MCP over that connection.
///
/// Dialing out is the point. Windows Firewall blocks inbound connections by default and opening
/// a port needs an administrator; an outbound connection needs neither, and it works from a VM
/// behind NAT. dsh listens; the bridge reconnects whenever the link drops.
///
/// Both ends prove they hold the pairing token without sending it (HMAC-SHA256 over two fresh
/// nonces, one from each side), before a single tool call is accepted. The bridge checks the
/// server too: a listener that cannot prove the token never gets to drive SOLIDWORKS.
/// The link is not encrypted. Use it on a host-only or local network; across untrusted
/// networks, carry it through SSH or a VPN.
static class Link
{
    public const string Label = "dsh-solidworks/1";

    /// Dial each address in turn until one proves the token, serve it, and start over when
    /// the link drops. The address that worked last is tried first next time.
    public static async Task Run(IReadOnlyList<(string host, int port)> addresses, string token, Bridge bridge, ToolTable table,
        Action<string> log, Action<string> onPaired)
    {
        var order = new List<(string host, int port)>(addresses);
        var backoff = TimeSpan.FromSeconds(1);
        var paired = false;
        // While dsh is away the bridge retries every 30 s for as long as it takes; log a failure
        // when it changes or every ten minutes, not every attempt.
        string lastProblem = null;
        var lastLogged = DateTime.MinValue;
        void Problem(string text)
        {
            if (text == lastProblem && DateTime.UtcNow - lastLogged < TimeSpan.FromMinutes(10)) return;
            lastProblem = text;
            lastLogged = DateTime.UtcNow;
            log(text);
        }
        while (true)
        {
            var began = DateTime.UtcNow;
            var refused = false;
            foreach (var (host, port) in order.ToArray())
            {
                try
                {
                    using var tcp = new TcpClient { NoDelay = true };
                    var connect = tcp.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(5_000)).ConfigureAwait(false) != connect)
                        throw new IOException($"no answer from {host}:{port}");
                    await connect.ConfigureAwait(false);
                    tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                    using var stream = tcp.GetStream();
                    var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1 << 16);
                    var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16) { AutoFlush = false, NewLine = "\n" };

                    await Handshake(reader, writer, token).ConfigureAwait(false);
                    log($"connected to dsh at {host}:{port}");
                    lastProblem = null;
                    order.Remove((host, port));
                    order.Insert(0, (host, port));
                    if (!paired) { paired = true; onPaired?.Invoke($"{host}:{port}"); }
                    await new Server(bridge, table, writer, log).Run(reader).ConfigureAwait(false);
                    log("dsh closed the link");
                    break;
                }
                catch (AuthException e)
                {
                    Problem($"{host}:{port} refused the pairing: {e.Message}");
                    refused = true;
                }
                catch (Exception e)
                {
                    // The loop outlives every failure: a bridge at the desktop must keep trying.
                    Problem($"{host}:{port}: {e.Message} (retrying)");
                }
            }

            // Retrying is cheap and dsh restarts often, so the wait tops out at 10 s; a refused
            // pairing (wrong token, or another bridge holds the link) waits 30 s.
            if (DateTime.UtcNow - began > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(1);
            if (refused) backoff = TimeSpan.FromSeconds(30);
            await Task.Delay(backoff).ConfigureAwait(false);
            backoff = TimeSpan.FromSeconds(Math.Min(10, backoff.TotalSeconds * 2));
        }
    }

    /// "a:1,b:2" or "a,b:2" (a host without a port takes the next port given). Order is kept:
    /// it is the preference the pairing kit wrote.
    public static List<(string host, int port)> ParseAddresses(string text)
    {
        var parts = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) parts.Add(raw.Trim());
        var result = new List<(string host, int port)>();
        int? port = null;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            var colon = parts[i].LastIndexOf(':');
            if (colon > 0 && int.TryParse(parts[i].Substring(colon + 1), out var p))
            {
                port = p;
                result.Insert(0, (parts[i].Substring(0, colon), p));
            }
            else if (port.HasValue) result.Insert(0, (parts[i], port.Value));
            else throw new FormatException($"{text}: give a port, as host:port");
        }
        if (result.Count == 0) throw new FormatException("no address given");
        return result;
    }

    static async Task Handshake(StreamReader reader, StreamWriter writer, string token)
    {
        var hello = Json.ParseObject(await ReadLine(reader).ConfigureAwait(false));
        if (Json.Str(hello, "t") != "hello" || Json.Int(hello, "v", 0) != 1)
            throw new AuthException("the other end does not speak the dsh-solidworks link protocol (is the port right?)");
        var serverNonce = Json.Str(hello, "nonce") ?? throw new AuthException("hello without a nonce");

        var bridgeNonce = Nonce();
        var auth = new Obj()
            .Add("t", "auth").Add("v", 1).Add("nonce", bridgeNonce)
            .Add("mac", Mac(token, $"{Label} bridge {serverNonce} {bridgeNonce}"))
            .Add("bridge", new Obj().Add("version", Bridge.Version).Add("machine", Environment.MachineName).Add("user", Environment.UserName));
        await writer.WriteLineAsync(Json.Write(auth)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        var answer = Json.ParseObject(await ReadLine(reader).ConfigureAwait(false));
        if (Json.Str(answer, "t") != "ok")
            throw new AuthException(Json.Str(answer, "reason") ?? "dsh refused the token; pair again with the command dsh shows");
        var expected = Mac(token, $"{Label} server {bridgeNonce} {serverNonce}");
        if (!Same(expected, Json.Str(answer, "mac") ?? ""))
            throw new AuthException("the listener could not prove it holds the token; not dsh, or a different pairing");
    }

    static async Task<string> ReadLine(StreamReader reader)
    {
        var read = reader.ReadLineAsync();
        if (await Task.WhenAny(read, Task.Delay(10_000)).ConfigureAwait(false) != read) throw new IOException("handshake timed out");
        return await read.ConfigureAwait(false) ?? throw new IOException("closed during handshake");
    }

    public static string Nonce()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static string Mac(string token, string message)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(message))).Replace("-", "").ToLowerInvariant();
    }

    static bool Same(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    sealed class AuthException : Exception
    {
        public AuthException(string message) : base(message) { }
    }
}
