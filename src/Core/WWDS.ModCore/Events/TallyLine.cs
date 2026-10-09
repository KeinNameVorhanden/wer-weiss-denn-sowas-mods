using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WWDS.ModCore.Events;

/// <summary>
/// One row of an audience interaction as shown by a vote window, parsed from the engine's tally lines
/// (<see cref="InteractionStatusChanged.Lines"/>): votes "A  52% (13)  label", naming "#2 Elton Fan (3)",
/// option guesses "A: 4", or plain text (numeric rounds). Shares are filled in from the counts when a line
/// has none.
/// </summary>
public sealed class TallyLine
{
    private static readonly Regex Vote = new(@"^(\S+)\s{2}(\d+)% \((\d+)\)(?:\s{2}(.*))?$");
    private static readonly Regex Name = new(@"^#(\d+) (.*) \((\d+)\)(?: \[pending\])?$");
    private static readonly Regex Option = new(@"^(\S+): (\d+)$");

    private TallyLine(string key, string label, int? count, double? share, bool pending)
    {
        Key = key;
        Label = label;
        Count = count;
        Share = share;
        Pending = pending;
    }

    /// <summary>Chat key ("A", "#2"); empty for plain text.</summary>
    public string Key { get; }

    public string Label { get; }

    public int? Count { get; }

    /// <summary>0..1, null for plain text.</summary>
    public double? Share { get; private set; }

    /// <summary>Naming suggestion still waiting for moderator approval.</summary>
    public bool Pending { get; }

    public bool IsPlain => Count == null;

    public static TallyLine Parse(string line)
    {
        var text = (line ?? string.Empty).Trim();
        var m = Vote.Match(text);
        if (m.Success)
        {
            return new TallyLine(m.Groups[1].Value, m.Groups[4].Value.Trim(), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0, false);
        }

        m = Name.Match(text);
        if (m.Success) return new TallyLine("#" + m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), null, text.EndsWith("[pending]", StringComparison.Ordinal));
        m = Option.Match(text);
        if (m.Success) return new TallyLine(m.Groups[1].Value, string.Empty, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), null, false);
        return new TallyLine(string.Empty, text, null, null, false);
    }

    /// <summary>Parses all lines and fills missing shares from the counts of the counted lines.</summary>
    public static IList<TallyLine> ParseAll(IEnumerable<string> lines)
    {
        var parsed = lines.Select(Parse).ToList();
        var counted = parsed.Where(l => l.Count != null).ToList();
        var total = counted.Sum(l => l.Count!.Value);
        foreach (var l in counted.Where(l => l.Share == null)) l.Share = total > 0 ? (double)l.Count!.Value / total : 0;
        return parsed;
    }

    /// <summary>The chat command hint for these rows: "!vote A, B oder C", "!name ... oder !vote 2", or "!vote Zahl".</summary>
    public static string CommandHint(IList<TallyLine> rows, string prefix = "!")
    {
        var keys = rows.Where(r => r.Key.Length > 0).Select(r => r.Key).ToList();
        if (keys.Count > 0 && keys.All(k => k.StartsWith("#", StringComparison.Ordinal)))
        {
            return prefix + "name <Name> oder " + prefix + "vote " + keys[0].Substring(1);
        }

        if (keys.Count > 1) return prefix + "vote " + string.Join(", ", keys.Take(keys.Count - 1).ToArray()) + " oder " + keys[keys.Count - 1];
        if (keys.Count == 1) return prefix + "vote " + keys[0];
        return prefix + "vote <Zahl>";
    }
}
