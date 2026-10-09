using System;
using System.Collections.Generic;
using System.Text;
using WWDS.Protocol;

namespace WWDS.Twitch.Irc;

/// <summary>One parsed IRC line (IRCv3 message tags supported).</summary>
public sealed class IrcLine
{
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    public string Prefix { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public List<string> Params { get; } = new();

    /// <summary>The trailing parameter (after " :"), or empty.</summary>
    public string Trailing { get; set; } = string.Empty;

    public string Tag(string key) => Tags.TryGetValue(key, out var v) ? v : string.Empty;

    /// <summary>Nick from ":nick!user@host".</summary>
    public string Nick
    {
        get
        {
            var bang = Prefix.IndexOf('!');
            return bang > 0 ? Prefix.Substring(0, bang) : Prefix;
        }
    }
}

/// <summary>Parses Twitch IRC lines and maps PRIVMSG to protocol chat messages.</summary>
public static class IrcParser
{
    public static IrcLine? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var line = raw!.TrimEnd('\r', '\n');
        var pos = 0;
        var result = new IrcLine();

        if (line.StartsWith("@", StringComparison.Ordinal))
        {
            var end = line.IndexOf(' ');
            if (end < 0) return null;
            foreach (var pair in line.Substring(1, end - 1).Split(';'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                if (eq < 0) result.Tags[pair] = string.Empty;
                else result.Tags[pair.Substring(0, eq)] = UnescapeTag(pair.Substring(eq + 1));
            }

            pos = end + 1;
        }

        while (pos < line.Length && line[pos] == ' ') pos++;
        if (pos < line.Length && line[pos] == ':')
        {
            var end = line.IndexOf(' ', pos);
            if (end < 0) return null;
            result.Prefix = line.Substring(pos + 1, end - pos - 1);
            pos = end + 1;
        }

        var trailingAt = line.IndexOf(" :", pos, StringComparison.Ordinal);
        var middle = trailingAt >= 0 ? line.Substring(pos, trailingAt - pos) : line.Substring(pos);
        if (trailingAt >= 0) result.Trailing = line.Substring(trailingAt + 2);

        var parts = middle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        result.Command = parts[0].ToUpperInvariant();
        for (var i = 1; i < parts.Length; i++) result.Params.Add(parts[i]);
        return result;
    }

    /// <summary>IRCv3 tag value escaping: \s space, \: semicolon, \\ backslash, \r, \n.</summary>
    public static string UnescapeTag(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                if (c != '\\') sb.Append(c);
                continue;
            }

            var n = value[++i];
            switch (n)
            {
                case 's': sb.Append(' '); break;
                case ':': sb.Append(';'); break;
                case '\\': sb.Append('\\'); break;
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                default: sb.Append(n); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Maps a PRIVMSG to a chat message, or null for anything else.</summary>
    public static ChatMessage? ToChat(IrcLine line)
    {
        if (line.Command != "PRIVMSG") return null;
        var badges = line.Tag("badges");
        var text = line.Trailing;

        // "/me" actions arrive as CTCP ACTION.
        const char ctcp = (char)1;
        if (text.Length > 8 && text[0] == ctcp && text.StartsWith(ctcp + "ACTION ", StringComparison.Ordinal))
        {
            text = text.Substring(8).TrimEnd(ctcp);
        }

        var display = line.Tag("display-name");
        int.TryParse(line.Tag("bits"), out var bits);
        return new ChatMessage
        {
            MessageId = line.Tag("id"),
            UserId = line.Tag("user-id"),
            Login = line.Nick,
            DisplayName = display.Length > 0 ? display : line.Nick,
            Text = text,
            IsBroadcaster = HasBadge(badges, "broadcaster"),
            IsModerator = HasBadge(badges, "moderator") || line.Tag("mod") == "1",
            IsVip = HasBadge(badges, "vip") || line.Tags.ContainsKey("vip"),
            IsSubscriber = HasBadge(badges, "subscriber") || HasBadge(badges, "founder") || line.Tag("subscriber") == "1",
            Bits = bits,
            Source = "irc",
        };
    }

    private static bool HasBadge(string badges, string name)
    {
        foreach (var b in badges.Split(','))
        {
            var slash = b.IndexOf('/');
            var key = slash >= 0 ? b.Substring(0, slash) : b;
            if (key == name) return true;
        }

        return false;
    }
}
