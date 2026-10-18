using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace SwBridge;

/// JSON in and out with only what .NET Framework ships.
/// Parse uses JavaScriptSerializer (objects become Dictionary<string, object>, arrays ArrayList
/// or object[]). Write is our own, so doubles round-trip and non-ASCII paths stay readable.
static class Json
{
    static readonly JavaScriptSerializer reader = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

    public static Dictionary<string, object> ParseObject(string text) =>
        reader.DeserializeObject(text) as Dictionary<string, object>
        ?? throw new BridgeException("BAD_REQUEST", "expected a JSON object");

    public static string Write(object value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value);
        return sb.ToString();
    }

    static void WriteValue(StringBuilder sb, object v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: WriteString(sb, s); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case double d: WriteNumber(sb, d); break;
            case float f: WriteNumber(sb, f); break;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case IDictionary<string, object> map:
                sb.Append('{');
                var first = true;
                foreach (var kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                break;
            case IEnumerable list:
                sb.Append('[');
                var firstItem = true;
                foreach (var x in list)
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    WriteValue(sb, x);
                }
                sb.Append(']');
                break;
            default: WriteString(sb, v.ToString()); break;
        }
    }

    static void WriteNumber(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x2028 || c == 0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // --- argument readers: tool arguments arrive as Dictionary<string, object> ---

    public static string Str(Dictionary<string, object> args, string key, string fallback = null) =>
        args != null && args.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;

    public static bool Bool(Dictionary<string, object> args, string key, bool fallback) =>
        args != null && args.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    /// An integer argument; numbers beyond int's range are clamped (every caller clamps to a
    /// sane range anyway), anything else is BAD_REQUEST rather than a crash.
    public static int Int(Dictionary<string, object> args, string key, int fallback)
    {
        if (args == null || !args.TryGetValue(key, out var v) || v == null) return fallback;
        var n = Num(v) ?? throw new BridgeException("BAD_REQUEST", $"{key} must be a number");
        return n >= int.MaxValue ? int.MaxValue : n <= int.MinValue ? int.MinValue : (int)Math.Round(n);
    }

    public static double? Num(object v) => v switch
    {
        null => null,
        int i => i,
        long l => l,
        decimal m => (double)m,
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && !double.IsNaN(p) && !double.IsInfinity(p) => p,
        _ => null,
    };

    public static Dictionary<string, object> Map(Dictionary<string, object> args, string key) =>
        args != null && args.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    /// An array of objects, e.g. sw_build's steps. Anything that is not an object is refused,
    /// so a malformed step fails loudly instead of being skipped.
    public static List<Dictionary<string, object>> Records(Dictionary<string, object> args, string key)
    {
        var r = new List<Dictionary<string, object>>();
        if (args == null || !args.TryGetValue(key, out var v) || v == null) return r;
        if (!(v is IEnumerable e) || v is string || v is IDictionary) throw new BridgeException("BAD_REQUEST", $"{key} must be an array of objects");
        var i = 0;
        foreach (var x in e)
        {
            i++;
            r.Add(x as Dictionary<string, object> ?? throw new BridgeException("BAD_REQUEST", $"{key}[{i}] must be an object"));
        }
        return r;
    }

    /// A number argument that must be present.
    public static double Need(Dictionary<string, object> args, string key, string what)
    {
        var v = args != null && args.TryGetValue(key, out var raw) ? Num(raw) : null;
        return v ?? throw new BridgeException("BAD_REQUEST", $"{what} needs {key} (a number)");
    }

    public static double? Opt(Dictionary<string, object> args, string key) =>
        args != null && args.TryGetValue(key, out var raw) ? Num(raw) : null;

    /// [x, y, z] in millimetres, as given; null when absent.
    public static double[] Triple(object v, string what)
    {
        if (v == null) return null;
        if (v is IEnumerable e && !(v is string))
        {
            var r = new List<double>();
            foreach (var x in e) r.Add(Num(x) ?? double.NaN);
            if (r.Count == 3 && !r.Exists(double.IsNaN)) return r.ToArray();
        }
        throw new BridgeException("BAD_REQUEST", $"{what} must be [x, y, z] in mm");
    }

    public static double[] Triple(Dictionary<string, object> args, string key, string what) =>
        Triple(args != null && args.TryGetValue(key, out var v) ? v : null, what);

    /// A list of [x, y, z] points; a single point is accepted as a list of one.
    public static List<double[]> Triples(Dictionary<string, object> args, string key, string what)
    {
        var r = new List<double[]>();
        if (args == null || !args.TryGetValue(key, out var v) || v == null) return r;
        if (!(v is IEnumerable e) || v is string) throw new BridgeException("BAD_REQUEST", $"{what}: {key} must be a list of [x, y, z] points in mm");
        var items = new List<object>();
        foreach (var x in e) items.Add(x);
        if (items.Count == 3 && items.TrueForAll(x => Num(x) != null)) { r.Add(Triple(v, $"{what}: {key}")); return r; }
        foreach (var x in items) r.Add(Triple(x, $"{what}: each of {key}"));
        return r;
    }

    public static List<string> List(Dictionary<string, object> args, string key)
    {
        var r = new List<string>();
        if (args == null || !args.TryGetValue(key, out var v) || v == null) return r;
        if (v is string one) { r.Add(one); return r; }
        if (v is IEnumerable e) foreach (var x in e) if (x != null) r.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
        return r;
    }
}

/// Insertion-ordered JSON object builder, so answers read top-down the way the tool documents them.
sealed class Obj : IDictionary<string, object>
{
    readonly List<KeyValuePair<string, object>> items = new List<KeyValuePair<string, object>>();

    /// Set a key. An existing key keeps its position, so answers stay in documented order.
    public Obj Add(string key, object value)
    {
        var i = items.FindIndex(kv => kv.Key == key);
        if (i >= 0) items[i] = new KeyValuePair<string, object>(key, value);
        else items.Add(new KeyValuePair<string, object>(key, value));
        return this;
    }

    public object Find(string key) => TryGetValue(key, out var v) ? v : null;

    /// Add only when the value is present, so absent facts are absent rather than null.
    public Obj Opt(string key, object value) => value == null ? this : Add(key, value);

    public object this[string key]
    {
        get { foreach (var kv in items) if (kv.Key == key) return kv.Value; throw new KeyNotFoundException(key); }
        set => Add(key, value);
    }

    public ICollection<string> Keys { get { var k = new List<string>(); foreach (var kv in items) k.Add(kv.Key); return k; } }
    public ICollection<object> Values { get { var v = new List<object>(); foreach (var kv in items) v.Add(kv.Value); return v; } }
    public int Count => items.Count;
    public bool IsReadOnly => false;
    void IDictionary<string, object>.Add(string key, object value) => Add(key, value);
    public void Add(KeyValuePair<string, object> item) => Add(item.Key, item.Value);
    public void Clear() => items.Clear();
    public bool Contains(KeyValuePair<string, object> item) => items.Contains(item);
    public bool ContainsKey(string key) { foreach (var kv in items) if (kv.Key == key) return true; return false; }
    public void CopyTo(KeyValuePair<string, object>[] array, int index) => items.CopyTo(array, index);
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
    public bool Remove(string key) => items.RemoveAll(kv => kv.Key == key) > 0;
    public bool Remove(KeyValuePair<string, object> item) => items.Remove(item);
    public bool TryGetValue(string key, out object value)
    {
        foreach (var kv in items) if (kv.Key == key) { value = kv.Value; return true; }
        value = null;
        return false;
    }
}
