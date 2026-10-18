using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SwBridge.Sw;

/// Modal dialogs SOLIDWORKS has open on the desktop, with their message text.
///
/// A dialog blocks the user and, for most calls, the API until a person answers it, so naming
/// it is the most useful thing a stuck bridge can say. The title is usually just "SOLIDWORKS";
/// the message is in the dialog's static text. A real case: a second SOLIDWORKS session stops
/// at startup on "A journal file could not be created. Auto recover will not work. Another
/// session of SOLIDWORKS may already be running on this machine." and waits for OK, forever.
///
/// The bridge reports dialogs and never answers them: that is a person's decision.
/// Window enumeration needs no COM, so this runs off the STA thread, even while it is stuck.
static class Dialogs
{
    public static List<object> Open(int? onlyPid = null)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("SLDWORKS"))
            if (onlyPid == null || p.Id == onlyPid) pids.Add((uint)p.Id);
        var found = new List<object>();
        if (pids.Count == 0) return found;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (!pids.Contains(owner) || Class(hwnd) != "#32770") return true; // the standard dialog class
            var message = new List<string>();
            EnumChildWindows(hwnd, (child, __) =>
            {
                if (Class(child) == "Static")
                {
                    var t = Text(child).Trim();
                    if (t.Length > 0) message.Add(t);
                }
                return true;
            }, IntPtr.Zero);
            var text = string.Join(" ", message);
            if (text.Length > 400) text = text.Substring(0, 400) + "...";
            found.Add(new Obj().Add("pid", (int)owner).Add("title", Text(hwnd)).Add("message", text));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string Class(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string Text(IntPtr hwnd)
    {
        var sb = new StringBuilder(1024);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
