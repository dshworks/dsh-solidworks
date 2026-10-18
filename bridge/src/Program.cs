using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using SwBridge.Mcp;

namespace SwBridge;

static class Program
{
    const string Usage = @"swbridge - lets dsh (and any MCP client) drive SOLIDWORKS on this PC.

  swbridge mcp [options]          serve MCP on stdin/stdout. dsh on this PC starts this itself;
                                  other MCP clients can too.
  swbridge connect <addresses> <token> [options] [--install]
                                  dial dsh on another machine (your Mac, from a VM) and serve it,
                                  reconnecting forever. <addresses> is host:port, or several
                                  separated by commas; the first that proves the token wins.
                                  --install keeps it: copies the bridge to this user's AppData,
                                  starts it at every sign-in, and runs it in the background now.
                                  dsh writes a pair.cmd that runs this for you.
  swbridge connect                run the saved pairing.
  swbridge status [options]       print what the bridge sees, once, and exit.
  swbridge uninstall              stop the background bridge, remove it and its pairing.
  swbridge version

options:
  --pid <n>              drive this SOLIDWORKS process (default: the visible one in front)
  --out <folder>         where exports go (default: Documents\dsh-solidworks)
  --allow-write <folder> also allow exports into this folder (repeatable)
  --allow-save           allow sw_save to overwrite source documents (off by default)

The bridge runs as you, in your desktop session, next to a running SOLIDWORKS. It never starts
or closes SOLIDWORKS and never needs administrator rights.";

    static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-solidworks");
    static readonly string SettingsFile = Path.Combine(Home, "bridge.json");
    static readonly string LogFile = Path.Combine(Home, "bridge.log");
    static readonly string InstalledExe = Path.Combine(Home, "swbridge.exe");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "dsh-solidworks";
    const string MutexName = @"Local\dsh-solidworks-bridge-connect";

    // MTA on purpose: COM objects live only on the bridge's own STA thread (Com/Sta.cs).
    static int Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        try
        {
            return Dispatch(new List<string>(argv));
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"swbridge: {e.Message}\n\n{Usage}");
            return 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"swbridge: {e.Message}");
            return 1;
        }
    }

    static int Dispatch(List<string> args)
    {
        var verb = args.Count == 0 ? "help" : args[0];
        if (args.Count > 0) args.RemoveAt(0);
        switch (verb)
        {
            case "mcp": return Mcp(args);
            case "connect": return Connect(args);
            case "status": return Status(args);
            case "uninstall": return Uninstall();
            case "selftest": return SelfTest.Run();
            case "version": case "--version": Console.WriteLine(Bridge.Version); return 0;
            case "help": case "--help": case "-h": Console.WriteLine(Usage); return 0;
            default: throw new UsageException($"unknown command {verb}");
        }
    }

    static int Mcp(List<string> args)
    {
        var options = ParseOptions(args, out var rest, out _, out _);
        if (rest.Count > 0) throw new UsageException($"unexpected {rest[0]}");
        using var bridge = new Bridge(options);
        var table = new ToolTable();
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        new Server(bridge, table, stdout, Log(toFile: false)).Run(stdin).GetAwaiter().GetResult();
        return 0;
    }

    static int Connect(List<string> args)
    {
        var options = ParseOptions(args, out var rest, out var install, out var detach);
        Settings settings;
        if (rest.Count == 0)
        {
            settings = Settings.Load(SettingsFile)
                ?? throw new UsageException("nothing is paired yet; run the pair.cmd dsh wrote, or swbridge connect <host:port> <token>");
            settings.Options = settings.Merge(options);
        }
        else if (rest.Count == 2)
        {
            settings = new Settings { Address = rest[0], Token = rest[1], Options = options };
        }
        else throw new UsageException("connect takes <addresses> <token>");

        List<(string host, int port)> addresses;
        try { addresses = Link.ParseAddresses(settings.Address); }
        catch (FormatException e) { throw new UsageException(e.Message); }

        if (install) return Install(settings);
        if (detach) return Detach();

        using var mutex = new Mutex(false, MutexName, out _);
        if (!mutex.WaitOne(0))
        {
            Console.Error.WriteLine("swbridge: a bridge is already connected for this user (see swbridge uninstall to stop it)");
            return 3;
        }
        var log = Log(toFile: true);
        log($"swbridge {Bridge.Version} dialing {settings.Address}; exports go to {settings.Options.OutDir}; save {(settings.Options.AllowSave ? "enabled" : "disabled")}");
        using var bridge = new Bridge(settings.Options);
        Link.Run(addresses, settings.Token, bridge, new ToolTable(), log, onPaired: address =>
        {
            if (rest.Count == 0) return;
            Settings.Save(SettingsFile, settings);
            log($"paired with {address}; saved to {SettingsFile}");
        }).GetAwaiter().GetResult();
        return 0;
    }

    /// Copy the bridge into this user's AppData, save the pairing, start it at sign-in through
    /// the per-user Run key, and launch it in the background. No administrator rights involved.
    static int Install(Settings settings)
    {
        Directory.CreateDirectory(Home);
        StopOthers();
        var self = Assembly.GetExecutingAssembly().Location;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, InstalledExe, true);
        Settings.Save(SettingsFile, settings);
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(RunValue, $"\"{InstalledExe}\" connect --detach");
        Launch(InstalledExe, "connect");
        Console.WriteLine($"Installed for {Environment.UserName}: {InstalledExe}");
        Console.WriteLine($"It dials {settings.Address}, runs in the background, and starts at every sign-in.");
        Console.WriteLine($"Log: {LogFile}");
        Console.WriteLine("Keep SOLIDWORKS running in this desktop session; the bridge attaches to it.");
        return 0;
    }

    /// `connect --detach`, the sign-in entry: relaunch without a console window and exit.
    static int Detach()
    {
        Launch(Assembly.GetExecutingAssembly().Location, "connect");
        return 0;
    }

    /// Start the background bridge through the shell, with its window hidden. Not
    /// UseShellExecute=false: that passes this process's handles on, and a caller reading our
    /// output (a script, an agent's shell tool) would then wait on the pipe until the
    /// background bridge exits, which is never.
    static void Launch(string exe, string arguments) =>
        Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe) });

    static int Uninstall()
    {
        var stopped = StopOthers();
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.DeleteValue(RunValue, false);
        if (File.Exists(SettingsFile)) File.Delete(SettingsFile);
        Console.WriteLine($"Stopped {stopped} bridge process(es); removed the sign-in entry and the pairing.");
        if (File.Exists(InstalledExe)) Console.WriteLine($"Delete {Home} to remove the bridge itself and its log.");
        return 0;
    }

    /// Stop other swbridge processes of this user. Never touches SOLIDWORKS.
    static int StopOthers()
    {
        var me = Process.GetCurrentProcess();
        var count = 0;
        foreach (var p in Process.GetProcessesByName("swbridge"))
        {
            if (p.Id == me.Id || p.SessionId != me.SessionId) continue;
            try { p.Kill(); p.WaitForExit(5000); count++; } catch (Exception) { /* exited or not ours */ }
        }
        return count;
    }

    static int Status(List<string> args)
    {
        var options = ParseOptions(args, out var rest, out _, out _);
        if (rest.Count > 0) throw new UsageException($"unexpected {rest[0]}");
        using var bridge = new Bridge(options);
        var table = new ToolTable();
        try
        {
            var result = table.Call(bridge, table.Find("sw_status"), new Dictionary<string, object>(), CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(Json.Write(result.Facts));
            return 0;
        }
        catch (BridgeException e)
        {
            Console.WriteLine($"{e.Code}: {e.Message}");
            return 1;
        }
    }

    static Options ParseOptions(List<string> args, out List<string> rest, out bool install, out bool detach)
    {
        var o = new Options();
        rest = new List<string>();
        install = detach = false;
        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new UsageException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--pid": o.Pid = int.TryParse(Next(), out var p) ? p : throw new UsageException("--pid takes a number"); break;
                case "--out": o.OutDir = Path.GetFullPath(Next()); break;
                case "--allow-write": o.WriteRoots.Add(Path.GetFullPath(Next())); break;
                case "--allow-save": o.AllowSave = true; break;
                case "--install": install = true; break;
                case "--detach": detach = true; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"unknown option {args[i]}");
                    rest.Add(args[i]);
                    break;
            }
        }
        return o;
    }

    /// stderr always; in connect mode also a small log file, since nobody watches that console.
    static Action<string> Log(bool toFile)
    {
        var gate = new object();
        return line =>
        {
            var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}";
            lock (gate)
            {
                try { Console.Error.WriteLine(stamped); } catch (IOException) { /* no console */ }
                if (!toFile) return;
                try
                {
                    Directory.CreateDirectory(Home);
                    if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1_000_000) File.Delete(LogFile);
                    File.AppendAllText(LogFile, stamped + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { /* logging must never stop the bridge */ }
            }
        };
    }

    sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }
}

/// The saved pairing, in %LOCALAPPDATA%\dsh-solidworks\bridge.json (a folder only this user
/// can read). Saving it keeps the token off later command lines and out of the Run key.
sealed class Settings
{
    public string Address;
    public string Token;
    public Options Options;

    public static Settings Load(string file)
    {
        if (!File.Exists(file)) return null;
        var d = Json.ParseObject(File.ReadAllText(file, Encoding.UTF8));
        var o = new Options();
        if (Json.Str(d, "out") is string outDir) o.OutDir = outDir;
        o.WriteRoots.AddRange(Json.List(d, "allow_write"));
        o.AllowSave = Json.Bool(d, "allow_save", false);
        if (d.TryGetValue("pid", out var pid) && pid != null) o.Pid = Convert.ToInt32(pid);
        return new Settings { Address = Json.Str(d, "connect"), Token = Json.Str(d, "token"), Options = o };
    }

    public static void Save(string file, Settings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var d = new Obj().Add("connect", s.Address).Add("token", s.Token).Add("out", s.Options.OutDir)
            .Add("allow_write", s.Options.WriteRoots).Add("allow_save", s.Options.AllowSave).Opt("pid", s.Options.Pid);
        File.WriteAllText(file, Json.Write(d), new UTF8Encoding(false));
    }

    /// Saved options, with anything given on this command line winning.
    public Options Merge(Options cli)
    {
        var defaults = new Options();
        var o = new Options
        {
            Pid = cli.Pid ?? Options.Pid,
            OutDir = cli.OutDir != defaults.OutDir ? cli.OutDir : Options.OutDir,
            AllowSave = cli.AllowSave || Options.AllowSave,
        };
        o.WriteRoots.AddRange(Options.WriteRoots);
        o.WriteRoots.AddRange(cli.WriteRoots);
        return o;
    }
}
