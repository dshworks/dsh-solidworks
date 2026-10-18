using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SwBridge.Com;
using SwBridge.Sw;

namespace SwBridge;

/// What the user allowed when starting the bridge. The Windows side owns these: dsh can ask
/// for anything, but only the machine holding the files decides what may be written.
sealed class Options
{
    public int? Pid;
    public string OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "dsh-solidworks");
    public readonly List<string> WriteRoots = new List<string>();
    public bool AllowSave;
}

/// One SOLIDWORKS session behind the tools: the STA thread, the attached instance, the
/// documents this bridge opened, and the files it wrote.
sealed class Bridge : IDisposable
{
    public static readonly string Version =
        typeof(Bridge).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public readonly Options Options;
    public readonly Session Session = new Session();
    readonly Sta sta = new Sta();
    readonly HashSet<string> opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> exported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new object();

    /// Files larger than this are not sent over the link; their path is still reported.
    public const long MaxSendBytes = 64L * 1024 * 1024;

    public Bridge(Options options)
    {
        Options = options;
        Session.PinnedPid = options.Pid;
    }

    /// Run `work` on the STA thread with the attached SOLIDWORKS.
    public Task<T> OnSw<T>(string name, TimeSpan budget, CancellationToken cancel, Func<object, T> work) =>
        sta.Run(name, budget, cancel, () => work(Session.App()));

    public (string name, TimeSpan elapsed)? Busy => sta.Current;

    public bool OpenedByBridge(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        lock (gate) return opened.Contains(Docs.Normalize(path));
    }

    public void MarkOpened(string path)
    {
        lock (gate) opened.Add(Docs.Normalize(path));
    }

    public void MarkClosed(string path)
    {
        lock (gate) opened.Remove(Docs.Normalize(path));
    }

    /// Remember a file this bridge wrote, so it may be sent back by its hash. Nothing else on
    /// the machine can be read through the link.
    public void MarkExported(string sha256, string path)
    {
        lock (gate) exported[sha256] = path;
    }

    public string ExportedPath(string sha256)
    {
        lock (gate) return exported.TryGetValue(sha256 ?? "", out var p) ? p : null;
    }

    /// Where a requested output path lands, or PATH_DENIED.
    /// A bare name or a relative path goes under the output folder. An absolute path must be
    /// inside the output folder or a folder given with --allow-write.
    public string OutputPath(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) throw new BridgeException("BAD_REQUEST", "path is required");
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(Options.OutDir, requested));
        }
        catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
        {
            throw new BridgeException("BAD_REQUEST", $"not a usable Windows path: {requested}");
        }
        foreach (var root in Roots())
            if (Inside(full, root))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                return full;
            }
        throw new BridgeException("PATH_DENIED",
            $"{full} is outside the folders this bridge may write ({string.Join("; ", Roots())}); " +
            "write a bare file name to use the output folder, or restart the bridge with --allow-write <folder>");
    }

    IEnumerable<string> Roots()
    {
        yield return Path.GetFullPath(Options.OutDir);
        foreach (var r in Options.WriteRoots) yield return Path.GetFullPath(r);
    }

    static bool Inside(string path, string root)
    {
        var r = root.TrimEnd('\\') + "\\";
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => sta.Dispose();
}
