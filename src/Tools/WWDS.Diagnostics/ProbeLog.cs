using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace WWDS.Diagnostics;

/// <summary>Writes one JSON object per line (JSONL) and mirrors a short form into the BepInEx log.</summary>
internal sealed class ProbeLog : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly StreamWriter _writer;
    private readonly DateTime _start = DateTime.UtcNow;

    public ProbeLog(ManualLogSource log, string directory, string gameTag)
    {
        _log = log;
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, gameTag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
        _writer = new StreamWriter(file, false, new UTF8Encoding(false)) { AutoFlush = true };
        FilePath = file;
    }

    public string FilePath { get; }

    public void Event(string kind, params object?[] keyValues)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"t\":").Append(((DateTime.UtcNow - _start).TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture));
        sb.Append(",\"kind\":");
        AppendValue(sb, kind);
        var text = new StringBuilder(kind);
        for (var i = 0; i + 1 < keyValues.Length; i += 2)
        {
            var key = Convert.ToString(keyValues[i], CultureInfo.InvariantCulture) ?? "?";
            sb.Append(',');
            AppendValue(sb, key);
            sb.Append(':');
            AppendValue(sb, keyValues[i + 1]);
            text.Append(' ').Append(key).Append('=').Append(Short(keyValues[i + 1]));
        }

        sb.Append('}');
        try
        {
            _writer.WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            _log.LogError("probe write failed: " + ex.Message);
        }

        _log.LogInfo(text.ToString());
    }

    private static string Short(object? value)
    {
        var s = value switch
        {
            null => "null",
            string str => str,
            System.Collections.IEnumerable list => "[" + string.Join(",", ToStrings(list)) + "]",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
        return s.Length > 120 ? s.Substring(0, 117) + "..." : s;
    }

    private static string[] ToStrings(System.Collections.IEnumerable list)
    {
        var result = new List<string>();
        foreach (var item in list) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture) ?? "null");
        return result.ToArray();
    }

    private static void AppendValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int or long or short or byte:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case string s:
                AppendString(sb, s);
                break;
            case System.Collections.IDictionary dict:
                sb.Append('{');
                var firstD = true;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    if (!firstD) sb.Append(',');
                    firstD = false;
                    AppendString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "");
                    sb.Append(':');
                    AppendValue(sb, e.Value);
                }

                sb.Append('}');
                break;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendValue(sb, item);
                }

                sb.Append(']');
                break;
            default:
                AppendString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static void AppendString(StringBuilder sb, string s)
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
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }

    public void Dispose() => _writer.Dispose();
}
