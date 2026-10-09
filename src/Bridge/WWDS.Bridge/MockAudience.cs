using System.Globalization;
using WWDS.Protocol;

namespace WWDS.Bridge;

/// <summary>
/// Turns console commands into fake Twitch messages so the whole audience pipeline can be exercised
/// without a live channel. Deterministic for a given seed.
/// </summary>
public sealed class MockAudience
{
    public const string Help =
        "mock commands:\n" +
        "  chat <login> <text>             message from a viewer\n" +
        "  mod <login> <text>              message from a moderator\n" +
        "  crowd <n> <cmd> <opt> [opt..]   n viewers send '!cmd <random opt>'\n" +
        "  votes A=12 B=5 C=3 [cmd]         exact vote distribution (default cmd: vote)\n" +
        "  redeem <login> <title> [input]  channel point redemption (use quotes for spaces)\n" +
        "  sub <login> [tier] | gift <login> <count> | cheer <login> <bits> [msg]\n" +
        "  raid <login> <viewers> | follow <login>";

    private readonly Random _random;
    private int _viewerCounter;

    public MockAudience(int seed = 1234) => _random = new Random(seed);

    /// <summary>Returns the messages for a command line, or null if it is not a mock command.</summary>
    public IList<Message>? Run(string line)
    {
        var args = Tokenize(line);
        if (args.Count == 0) return null;
        var rest = args.Skip(1).ToList();
        switch (args[0].ToLowerInvariant())
        {
            case "chat" when rest.Count >= 2:
                return new Message[] { Chat(rest[0], string.Join(' ', rest.Skip(1)), mod: false) };
            case "mod" when rest.Count >= 2:
                return new Message[] { Chat(rest[0], string.Join(' ', rest.Skip(1)), mod: true) };
            case "crowd" when rest.Count >= 3 && int.TryParse(rest[0], out var n):
                return Enumerable.Range(0, Math.Clamp(n, 1, 5000))
                    .Select(_ => (Message)Chat(NextViewer(), "!" + rest[1] + " " + rest[2 + _random.Next(rest.Count - 2)], mod: false))
                    .ToList();
            case "votes":
                return Votes(rest);
            case "redeem" when rest.Count >= 2:
                return new Message[] { new Redemption { Id = Guid.NewGuid().ToString(), UserId = Id(rest[0]), Login = rest[0], DisplayName = rest[0], RewardId = "mock-" + rest[1].ToLowerInvariant().Replace(' ', '-'), RewardTitle = rest[1], Cost = 100, Input = rest.Count > 2 ? string.Join(' ', rest.Skip(2)) : string.Empty } };
            case "sub" when rest.Count >= 1:
                return new Message[] { new Subscription { UserId = Id(rest[0]), Login = rest[0], DisplayName = rest[0], Tier = rest.Count > 1 ? rest[1] : "1000", Months = 1 } };
            case "gift" when rest.Count >= 2 && int.TryParse(rest[1], out var count):
                return new Message[] { new Subscription { UserId = Id(rest[0]), Login = rest[0], DisplayName = rest[0], IsGift = true, GiftCount = count } };
            case "cheer" when rest.Count >= 2 && int.TryParse(rest[1], out var bits):
                return new Message[] { new Cheer { UserId = Id(rest[0]), Login = rest[0], DisplayName = rest[0], Bits = bits, Message = string.Join(' ', rest.Skip(2)) } };
            case "raid" when rest.Count >= 2 && int.TryParse(rest[1], out var viewers):
                return new Message[] { new Raid { FromUserId = Id(rest[0]), FromLogin = rest[0], FromDisplayName = rest[0], Viewers = viewers } };
            case "follow" when rest.Count >= 1:
                return new Message[] { new Follow { UserId = Id(rest[0]), Login = rest[0], DisplayName = rest[0] } };
            default:
                return null;
        }
    }

    private IList<Message>? Votes(List<string> rest)
    {
        var command = rest.FirstOrDefault(t => t.IndexOf('=') < 0)?.TrimStart('!') ?? "vote";
        var result = new List<Message>();
        foreach (var token in rest)
        {
            var eq = token.IndexOf('=');
            if (eq < 0) continue;
            if (!int.TryParse(token[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0) return null;
            var option = token[..eq];
            for (var i = 0; i < count; i++) result.Add(Chat(NextViewer(), "!" + command + " " + option, mod: false));
        }

        // Interleave like a real chat instead of all A's first.
        return result.OrderBy(_ => _random.Next()).ToList();
    }

    private string NextViewer() => "viewer" + (++_viewerCounter).ToString(CultureInfo.InvariantCulture);

    private static string Id(string login) => "mock-" + login.ToLowerInvariant();

    private static ChatMessage Chat(string login, string text, bool mod) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        UserId = Id(login),
        Login = login.ToLowerInvariant(),
        DisplayName = login,
        Text = text,
        IsModerator = mod,
        Source = "mock",
    };

    private static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
