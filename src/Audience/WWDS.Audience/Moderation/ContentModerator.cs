using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WWDS.Audience.Moderation;

public sealed class ModerationResult
{
    private ModerationResult(bool accepted, string text, string reason)
    {
        Accepted = accepted;
        Text = text;
        Reason = reason;
    }

    public bool Accepted { get; }

    /// <summary>Normalized text (only meaningful when accepted).</summary>
    public string Text { get; }

    public string Reason { get; }

    public static ModerationResult Ok(string text) => new(true, text, string.Empty);

    public static ModerationResult Reject(string reason) => new(false, string.Empty, reason);
}

public sealed class ModerationRules
{
    public int MinLength { get; set; } = 2;

    public int MaxLength { get; set; } = 14;

    /// <summary>Longest run of the same character ("aaaa").</summary>
    public int MaxRepeatedChars { get; set; } = 3;

    /// <summary>Extra allowed characters besides letters and digits.</summary>
    public string AllowedSymbols { get; set; } = " -_.'!?&";

    /// <summary>Blocked words or fragments, matched case-insensitively after leetspeak folding.</summary>
    public IList<string> Denylist { get; set; } = new List<string>();

    /// <summary>Reject text that contains a URL-like fragment.</summary>
    public bool BlockLinks { get; set; } = true;

    /// <summary>Title-case names ("quiz master" -> "Quiz Master").</summary>
    public bool TitleCase { get; set; }
}

/// <summary>
/// Validates and normalizes user-generated text (names, labels) before it may reach the game UI.
/// Strips rich-text markup and control characters, collapses whitespace, enforces a character
/// whitelist, length and repetition limits, and a denylist that also catches common leetspeak.
/// </summary>
public sealed class ContentModerator
{
    private readonly ModerationRules _rules;
    private readonly List<string> _deny = new();

    public ContentModerator(ModerationRules rules)
    {
        _rules = rules;
        foreach (var d in rules.Denylist)
        {
            var folded = Fold(d);
            if (folded.Length > 0) _deny.Add(folded);
        }
    }

    public ModerationResult Check(string? raw)
    {
        if (raw == null) return ModerationResult.Reject("empty");
        var text = StripMarkup(raw);
        text = CollapseWhitespace(text);
        if (text.Length < _rules.MinLength) return ModerationResult.Reject("too short");
        if (text.Length > _rules.MaxLength) return ModerationResult.Reject("too long (max " + _rules.MaxLength + ")");

        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c) && _rules.AllowedSymbols.IndexOf(c) < 0) return ModerationResult.Reject("character '" + c + "' not allowed");
        }

        if (!HasLetter(text)) return ModerationResult.Reject("needs a letter");
        if (MaxRun(text) > _rules.MaxRepeatedChars) return ModerationResult.Reject("too many repeated characters");

        var folded = Fold(text);
        if (_rules.BlockLinks && (folded.Contains("http") || folded.Contains("www") || LooksLikeDomain(text))) return ModerationResult.Reject("links not allowed");
        foreach (var d in _deny)
        {
            if (folded.Contains(d)) return ModerationResult.Reject("blocked word");
        }

        return ModerationResult.Ok(_rules.TitleCase ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()) : text);
    }

    /// <summary>Key used to detect duplicates: case-insensitive, whitespace- and leetspeak-insensitive.</summary>
    public static string DuplicateKey(string text) => Fold(text).Replace(" ", string.Empty);

    /// <summary>Lower-case, map leetspeak digits/symbols to letters, drop separators.</summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            var c = ch switch
            {
                '0' => 'o',
                '1' or '!' or '|' => 'i',
                '3' => 'e',
                '4' or '@' => 'a',
                '5' or '$' => 's',
                '7' => 't',
                '8' => 'b',
                _ => ch,
            };
            if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
        }

        return sb.ToString();
    }

    private static string StripMarkup(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var depth = 0;
        foreach (var c in raw)
        {
            if (c == '<')
            {
                depth++;
                continue;
            }

            if (c == '>' && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth > 0 || char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool HasLetter(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetter(c)) return true;
        }

        return false;
    }

    private static int MaxRun(string text)
    {
        var max = 0;
        var run = 0;
        var prev = '\0';
        foreach (var c in text.ToLowerInvariant())
        {
            run = c == prev ? run + 1 : 1;
            prev = c;
            if (run > max) max = run;
        }

        return max;
    }

    private static bool LooksLikeDomain(string text)
    {
        var dot = text.LastIndexOf('.');
        if (dot <= 0 || dot >= text.Length - 2) return false;
        var tld = text.Substring(dot + 1).ToLowerInvariant();
        return tld == "com" || tld == "de" || tld == "tv" || tld == "net" || tld == "org" || tld == "gg" || tld == "io" || tld == "ly";
    }
}

/// <summary>Per-viewer flood protection across all interactions (sliding window).</summary>
public sealed class SpamGuard
{
    private readonly Dictionary<string, Queue<double>> _hits = new();

    public SpamGuard(int maxPerWindow, double windowSeconds)
    {
        MaxPerWindow = maxPerWindow;
        WindowSeconds = windowSeconds;
    }

    public int MaxPerWindow { get; set; }

    public double WindowSeconds { get; set; }

    public bool Allow(string userId, double now)
    {
        if (MaxPerWindow <= 0) return true;
        if (!_hits.TryGetValue(userId, out var q)) _hits[userId] = q = new Queue<double>();
        while (q.Count > 0 && now - q.Peek() > WindowSeconds) q.Dequeue();
        if (q.Count >= MaxPerWindow) return false;
        q.Enqueue(now);
        return true;
    }
}
