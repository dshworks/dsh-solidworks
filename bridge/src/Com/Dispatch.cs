using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SwBridge.Com;

/// Late-bound calls through IDispatch::GetIDsOfNames + Invoke.
///
/// Why not `dynamic` or PowerShell's adapter: both ask the object for ITypeInfo first, and
/// SOLIDWORKS objects reached out of process answer TYPE_E_ELEMENTNOTFOUND for it (measured on
/// SOLIDWORKS 2016 and 2019). Type.InvokeMember on a __ComObject goes straight to IDispatch.
///
/// Every COM object returned by a call is registered with the current ComScope, so a request
/// releases what it touched. Out-of-process references otherwise live until the GC runs, and a
/// document SOLIDWORKS still holds a reference to is not unloaded by CloseDoc.
static class Dispatch
{
    public static object Call(object target, string member, params object[] args) =>
        Invoke(target, member, BindingFlags.InvokeMethod, args, null);

    public static object Get(object target, string member, params object[] args) =>
        Invoke(target, member, BindingFlags.GetProperty, args, null);

    public static void Set(object target, string member, params object[] args) =>
        Invoke(target, member, BindingFlags.SetProperty, args, null);

    /// Assign an object-valued property (DISPATCH_PROPERTYPUTREF), e.g. a MathTransform.
    /// Some members accept a plain put as well; the by-reference put is the documented one.
    public static void SetRef(object target, string member, params object[] args) =>
        Invoke(target, member, BindingFlags.PutRefDispProperty, args, null);

    /// Call with by-reference arguments. `byRef` lists the indexes SOLIDWORKS writes back into;
    /// their new values are read from `args` after the call.
    public static object CallRef(object target, string member, object[] args, params int[] byRef)
    {
        var mod = new ParameterModifier(args.Length);
        foreach (var i in byRef) mod[i] = true;
        return Invoke(target, member, BindingFlags.InvokeMethod, args, new[] { mod });
    }

    /// The VB `Nothing` for an object parameter: VT_DISPATCH with a null pointer. A plain null
    /// marshals as VT_EMPTY, which some SOLIDWORKS members reject for object arguments.
    public static object Nothing => new DispatchWrapper(null);

    static object Invoke(object target, string member, BindingFlags kind, object[] args, ParameterModifier[] mods)
    {
        if (target == null) throw new BridgeException("COM_NULL", $"cannot call {member} on a missing object");
        try
        {
            var result = target.GetType().InvokeMember(member, kind, null, target, args ?? new object[0], mods, null, null);
            return ComScope.Track(result);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw Translate(member, e.InnerException);
        }
        catch (COMException e)
        {
            throw Translate(member, e);
        }
        catch (MissingMethodException)
        {
            throw new BridgeException("MEMBER_MISSING", $"{member} is not available on this SOLIDWORKS version");
        }
    }

    static Exception Translate(string member, Exception e)
    {
        if (e is BridgeException) return e;
        var hr = (uint)e.HResult;
        switch (hr)
        {
            case 0x80020006: // DISP_E_UNKNOWNNAME
                return new BridgeException("MEMBER_MISSING", $"{member} is not available on this SOLIDWORKS version");
            case 0x80010001: // RPC_E_CALL_REJECTED
            case 0x8001010A: // RPC_E_SERVERCALL_RETRYLATER
                return new BridgeException("SW_BUSY", $"SOLIDWORKS rejected {member}: it is busy or showing a dialog");
            case 0x80010108: // RPC_E_DISCONNECTED
            case 0x800706BA: // RPC_S_SERVER_UNAVAILABLE
            case 0x800706BE: // RPC_S_CALL_FAILED
                return new BridgeException("SW_GONE", $"SOLIDWORKS stopped answering during {member}");
        }
        return new BridgeException("COM_ERROR", $"{member} failed: {e.Message} (0x{hr:X8})");
    }

    // --- value helpers. Late binding gives no type guarantee: a Boolean member can come back as
    // VT_BOOL, VT_I2 or VT_I4 depending on the member and release, so nothing unboxes blindly. ---

    public static bool Bool(object v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        short s => s != 0,
        long l => l != 0,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
        _ => Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    public static string Str(object v) => v == null ? null : v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);

    // --- shape helpers: SOLIDWORKS returns VARIANT arrays as object[], double[], string[] or null ---

    public static object[] Objects(object v) => v switch
    {
        null => new object[0],
        object[] a => a,
        Array a => Copy(a),
        _ => new[] { v },
    };

    public static double[] Doubles(object v)
    {
        if (v is double[] d) return d;
        var o = Objects(v);
        var r = new double[o.Length];
        for (var i = 0; i < o.Length; i++) r[i] = Convert.ToDouble(o[i]);
        return r;
    }

    public static string[] Strings(object v)
    {
        if (v is string[] s) return s;
        var o = Objects(v);
        var r = new string[o.Length];
        for (var i = 0; i < o.Length; i++) r[i] = o[i]?.ToString() ?? "";
        return r;
    }

    static object[] Copy(Array a)
    {
        var r = new object[a.Length];
        var i = 0;
        foreach (var x in a) r[i++] = x;
        return r;
    }
}

/// Collects the COM objects a request touched and releases them when the request ends.
/// One scope per request, owned by the STA thread; `Keep` exempts long-lived objects.
sealed class ComScope : IDisposable
{
    [ThreadStatic] static ComScope current;
    static readonly HashSet<object> kept = new HashSet<object>(ReferenceEqualityComparer.Instance);

    readonly List<object> objects = new List<object>();
    readonly ComScope outer;

    public ComScope()
    {
        outer = current;
        current = this;
    }

    public static void Keep(object o)
    {
        if (o != null) kept.Add(o);
    }

    public static void Forget(object o)
    {
        if (o != null) kept.Remove(o);
    }

    public static object Track(object o)
    {
        if (current != null && o != null && Marshal.IsComObject(o) && !kept.Contains(o)) current.objects.Add(o);
        else if (current != null && o is object[] arr)
            foreach (var x in arr)
                if (x != null && Marshal.IsComObject(x) && !kept.Contains(x)) current.objects.Add(x);
        return o;
    }

    public void Dispose()
    {
        current = outer;
        for (var i = objects.Count - 1; i >= 0; i--)
        {
            var o = objects[i];
            if (kept.Contains(o)) continue;
            try { Marshal.FinalReleaseComObject(o); } catch (Exception) { /* already released */ }
        }
        objects.Clear();
    }
}

sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
    public new bool Equals(object a, object b) => ReferenceEquals(a, b);
    public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
}
