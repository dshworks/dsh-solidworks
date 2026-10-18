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

    public static int Int(Dictionary<string, object> args, string key, int fallback) =>
        args != null && args.TryGetValue(key, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : fallback;

    public static double? Num(object v) => v switch
    {
        null => null,
        int i => i,
        long l => l,
        decimal m => (double)m,
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public static Dictionary<string, object> Map(Dictionary<string, object> args, string key) =>
        args != null && args.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

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
