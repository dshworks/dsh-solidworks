using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SwBridge.Com;

namespace SwBridge.Sw;

/// The SOLIDWORKS instance this bridge drives, and how it is chosen.
///
/// Every running SOLIDWORKS registers `SolidWorks_PID_<pid>` in the Running Object Table of
/// its desktop session; several can run at once (we have seen four on one machine, three of
/// them invisible leftovers). GetActiveObject would hand back an arbitrary one, so the bridge
/// enumerates them and picks, in order: the pinned pid; else the visible instance whose window
/// is highest in the desktop's Z-order (the one the user is looking at); else the only one.
///
/// The bridge attaches only. It never starts SOLIDWORKS and never closes it.
sealed class Session
{
    object app;
    int pid;
    public int? PinnedPid;

    public int Pid => pid;

    /// Called on the STA thread only.
    public object App()
    {
        if (app != null && PinnedPid.HasValue && PinnedPid.Value != pid) Drop();
        if (app != null)
        {
            try
            {
                Dispatch.Call(app, "RevisionNumber");
                return app;
            }
            catch (BridgeException e) when (e.Code == "SW_GONE" || e.Code == "COM_ERROR")
            {
                Drop();
            }
        }
        var all = Instances();
        Instance chosen = null;
        try { chosen = Choose(all, PinnedPid); }
        finally { Release(all, chosen?.App); }
        app = chosen.App;
        pid = chosen.Pid;
        ComScope.Keep(app);
        return app;
    }

    /// Release the instance handles a listing took, except the one being kept.
    public static void Release(List<Instance> all, object keep)
    {
        foreach (var i in all)
        {
            if (i.App == null || ReferenceEquals(i.App, keep)) continue;
            try { Marshal.FinalReleaseComObject(i.App); } catch (Exception) { /* gone already */ }
        }
    }

    public void Drop()
    {
        if (app == null) return;
        ComScope.Forget(app);
        try { Marshal.FinalReleaseComObject(app); } catch (Exception) { /* gone already */ }
        app = null;
        pid = 0;
    }

    public sealed class Instance
    {
        public int Pid;
        public object App;
        public bool Visible;
        public int ZOrder = int.MaxValue;
        public string Revision;
    }

    /// Every SOLIDWORKS in this desktop session, with its window Z-order.
    public static List<Instance> Instances()
    {
        var found = new List<Instance>();
        if (GetRunningObjectTable(0, out var rot) != 0) return found;
        rot.EnumRunning(out var monikers);
        CreateBindCtx(0, out var ctx);
        var one = new IMoniker[1];
        while (monikers.Next(1, one, IntPtr.Zero) == 0)
        {
            string name;
            try { one[0].GetDisplayName(ctx, null, out name); } catch (COMException) { continue; }
            if (!name.StartsWith("SolidWorks_PID_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(name.Substring("SolidWorks_PID_".Length), out var id)) continue;
            if (rot.GetObject(one[0], out var obj) != 0 || obj == null) continue;
            var inst = new Instance { Pid = id, App = obj };
            try
            {
                inst.Visible = (bool)Dispatch.Get(obj, "Visible");
                inst.Revision = (string)Dispatch.Call(obj, "RevisionNumber");
            }
            catch (BridgeException)
            {
                continue; // registered but not answering: a dying or hung instance
            }
            found.Add(inst);
        }

        var z = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            foreach (var i in found)
                if (i.Pid == (int)owner && i.ZOrder == int.MaxValue) i.ZOrder = z;
            z++;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static Instance Choose(List<Instance> all, int? pinned)
    {
        if (all.Count == 0)
            throw new BridgeException("SW_NOT_RUNNING",
                "no SOLIDWORKS is running in this Windows desktop session; start it, then try again " +
                "(the bridge attaches to a running SOLIDWORKS and never starts one)");
        if (pinned.HasValue)
        {
            foreach (var i in all) if (i.Pid == pinned.Value) return i;
            throw new BridgeException("SW_NOT_RUNNING", $"no SOLIDWORKS with pid {pinned.Value}; running: {Pids(all)}");
        }
        Instance best = null;
        foreach (var i in all)
        {
            if (best == null) { best = i; continue; }
            if (i.Visible != best.Visible) { if (i.Visible) best = i; continue; }
            if (i.ZOrder < best.ZOrder) best = i;
        }
        return best;
    }

    static string Pids(List<Instance> all)
    {
        var sb = new StringBuilder();
        foreach (var i in all) sb.Append(sb.Length == 0 ? "" : ", ").Append(i.Pid);
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    /// "24.1.0" -> (2016, 1). Major revision 24 is SOLIDWORKS 2016; each release adds one.
    public static (int year, int sp) Release(string revision)
    {
        var parts = (revision ?? "").Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return (0, 0);
        return (major + 1992, minor);
    }

    public static string ProcessStart(int pid)
    {
        try { return Process.GetProcessById(pid).StartTime.ToUniversalTime().ToString("o"); }
        catch (Exception) { return null; }
    }

    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
