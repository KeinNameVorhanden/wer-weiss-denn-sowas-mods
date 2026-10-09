using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace WWDS.ModCore.Serialization;

/// <summary>
/// Minimal JSON reader/writer with a reflection mapper (camelCase property names).
/// Exists because Newtonsoft.Json's net35 build needs System.Runtime.Serialization and System.Xml,
/// which the games' Mono profile does not ship (runtime-verified FileNotFoundException), and
/// System.Text.Json does not support .NET 3.5. Values: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;,
/// string, double, bool, null.
/// </summary>
public static class Json
{
    public const int MaxDepth = 32;

    // ---- parsing ---------------------------------------------------------------------------------

    /// <summary>Parses JSON text. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static object? Parse(string text)
    {
        if (text == null) throw new FormatException("null input");
        var p = new Parser(text);
        p.SkipWhitespace();
        var value = p.ReadValue(0);
        p.SkipWhitespace();
        if (!p.AtEnd) throw new FormatException("trailing characters at " + p.Position);
        return value;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public int Position => _i;

        public bool AtEnd => _i >= _s.Length;

        public void SkipWhitespace()
        {
            while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
        }

        public object? ReadValue(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nesting too deep");
            if (AtEnd) throw new FormatException("unexpected end");
            var c = _s[_i];
            switch (c)
            {
                case '{': return ReadObject(depth);
                case '[': return ReadArray(depth);
                case '"': return ReadString();
                case 't': return ReadLiteral("true", true);
                case 'f': return ReadLiteral("false", false);
                case 'n': return ReadLiteral("null", null);
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw new FormatException("unexpected '" + c + "' at " + _i);
            }
        }

        private object? ReadLiteral(string word, object? value)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw new FormatException("bad literal at " + _i);
            _i += word.Length;
            return value;
        }

        private Dictionary<string, object?> ReadObject(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            _i++;
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == '}')
            {
                _i++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd || _s[_i] != '"') throw new FormatException("expected property name at " + _i);
                var key = ReadString();
                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                result[key] = ReadValue(depth + 1);
                SkipWhitespace();
                if (AtEnd) throw new FormatException("unterminated object");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }

                Expect('}');
                return result;
            }
        }

        private List<object?> ReadArray(int depth)
        {
            var result = new List<object?>();
            _i++;
            SkipWhitespace();
            if (_i < _s.Length && _s[_i] == ']')
            {
                _i++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                result.Add(ReadValue(depth + 1));
                SkipWhitespace();
                if (AtEnd) throw new FormatException("unterminated array");
                if (_s[_i] == ',')
                {
                    _i++;
                    continue;
                }

                Expect(']');
                return result;
            }
        }

        private void Expect(char c)
        {
            if (AtEnd || _s[_i] != c) throw new FormatException("expected '" + c + "' at " + _i);
            _i++;
        }

        private string ReadString()
        {
            _i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw new FormatException("unterminated string");
                var c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new FormatException("control character in string");
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (AtEnd) throw new FormatException("bad escape");
                var e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length) throw new FormatException("bad unicode escape");
                        if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new FormatException("bad unicode escape");
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default:
                        throw new FormatException("bad escape '" + e + "'");
                }
            }
        }

        private double ReadNumber()
        {
            var start = _i;
            if (_s[_i] == '-') _i++;
            while (_i < _s.Length && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
            var text = _s.Substring(start, _i - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsInfinity(d) || double.IsNaN(d)) throw new FormatException("bad number '" + text + "'");
            return d;
        }
    }

    // ---- writing ---------------------------------------------------------------------------------

    /// <summary>Serializes a value graph (dictionaries, lists, primitives, or plain objects via <see cref="ToValue"/>).</summary>
    public static string Write(object? value, bool indent = false)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, indent, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? value, bool indent, int level)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case double d:
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : ((double)f).ToString("R", CultureInfo.InvariantCulture));
                return;
            case int or long or short or byte or uint or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case Enum e:
                WriteString(sb, e.ToString());
                return;
            case IDictionary dict:
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                    sb.Append(indent ? ": " : ":");
                    WriteValue(sb, kv.Value, indent, level + 1);
                }

                if (!first) NewLine(sb, indent, level);
                sb.Append('}');
                return;
            case IEnumerable list:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in list)
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    NewLine(sb, indent, level + 1);
                    WriteValue(sb, item, indent, level + 1);
                }

                if (!firstItem) NewLine(sb, indent, level);
                sb.Append(']');
                return;
            default:
                WriteValue(sb, ToValue(value), indent, level);
                return;
        }
    }

    private static void NewLine(StringBuilder sb, bool indent, int level)
    {
        if (!indent) return;
        sb.Append('\n');
        sb.Append(' ', level * 2);
    }

    private static void WriteString(StringBuilder sb, string s)
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
                    if (c < 0x20)
                    {
                        sb.Append('\\').Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    // ---- reflection mapping ---------------------------------------------------------------------

    /// <summary>Plain object to a dictionary of its public readable+writable properties (camelCase keys).</summary>
    public static Dictionary<string, object?> ToValue(object obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || !p.CanWrite) continue;
            result[Camel(p.Name)] = p.GetValue(obj, null);
        }

        return result;
    }

    /// <summary>
    /// Builds <paramref name="type"/> from a parsed value. Unknown properties are ignored; a property of
    /// the wrong JSON type throws <see cref="FormatException"/>.
    /// </summary>
    public static object? FromValue(Type type, object? value)
    {
        if (value == null) return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null;
        var inner = Nullable.GetUnderlyingType(type) ?? type;
        if (inner == typeof(string)) return value as string ?? throw new FormatException("expected string");
        if (inner == typeof(bool)) return value is bool b ? b : throw new FormatException("expected bool");
        if (inner.IsEnum)
        {
            if (value is string es) return Enum.Parse(inner, es, true);
            if (value is double ed) return Enum.ToObject(inner, (int)ed);
            throw new FormatException("expected enum");
        }

        if (inner == typeof(int) || inner == typeof(long) || inner == typeof(double) || inner == typeof(float))
        {
            if (!(value is double d)) throw new FormatException("expected number");
            if (inner == typeof(int))
            {
                if (d > int.MaxValue || d < int.MinValue) throw new FormatException("number out of range");
                return (int)d;
            }

            if (inner == typeof(long)) return (long)d;
            if (inner == typeof(float)) return (float)d;
            return d;
        }

        if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (!(value is List<object?> items)) throw new FormatException("expected array");
            var element = inner.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(inner);
            foreach (var item in items) list.Add(FromValue(element, item));
            return list;
        }

        if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            if (!(value is Dictionary<string, object?> map)) throw new FormatException("expected object");
            var args = inner.GetGenericArguments();
            if (args[0] != typeof(string)) throw new FormatException("only string keys are supported");
            var dict = (IDictionary)Activator.CreateInstance(inner);
            foreach (var kv in map) dict[kv.Key] = FromValue(args[1], kv.Value);
            return dict;
        }

        if (!(value is Dictionary<string, object?> obj)) throw new FormatException("expected object for " + inner.Name);
        var instance = Activator.CreateInstance(inner);
        foreach (var p in inner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            if (!obj.TryGetValue(Camel(p.Name), out var raw) && !obj.TryGetValue(p.Name, out raw)) continue;
            p.SetValue(instance, FromValue(p.PropertyType, raw), null);
        }

        return instance;
    }

    public static T Deserialize<T>(string text) => (T)FromValue(typeof(T), Parse(text))!;

    public static string Serialize(object value, bool indent = false) => Write(value, indent);

    private static string Camel(string name) => name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
