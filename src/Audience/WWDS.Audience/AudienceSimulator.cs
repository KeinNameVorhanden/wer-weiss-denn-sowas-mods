using System;
using System.Collections.Generic;
using System.Globalization;
using WWDS.ModCore;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;

namespace WWDS.Audience;

/// <summary>
/// In-game audience simulation (no Twitch needed): fabricates viewers whose chat lines go through the
/// same path as real chat (bus event + command registry). Used by the overlay's mock buttons and tests.
/// </summary>
public sealed class AudienceSimulator
{
    private readonly ModContext _ctx;
    private readonly Random _random;
    private int _counter;

    public AudienceSimulator(ModContext ctx, int seed = 7)
    {
        _ctx = ctx;
        _random = new Random(seed);
    }

    public string Prefix { get; set; } = "!";

    /// <summary>Sends one chat line from a simulated viewer.</summary>
    public void Say(string login, string text, Authority authority = Authority.Viewer)
    {
        var viewer = new Viewer("sim-" + login.ToLowerInvariant(), login.ToLowerInvariant(), login, authority, "mock");
        _ctx.Bus.Publish(new ChatMessageReceived(viewer, text, Guid.NewGuid().ToString()));
        var cmd = CommandParser.Parse(text, Prefix);
        if (cmd == null) return;
        var result = _ctx.Commands.Execute(cmd, viewer.AsInvoker());
        if (result.Outcome != CommandOutcome.Unknown) _ctx.Bus.Publish(new ChatCommandReceived(viewer, cmd, result.Outcome));
        if (result.Outcome == CommandOutcome.Handled && !string.IsNullOrEmpty(result.Reply)) _ctx.Bus.Publish(new ChatSendRequested(result.Reply!));
    }

    /// <summary>n new viewers each send "!command &lt;random option&gt;".</summary>
    public void Crowd(int n, string command, IList<string> options)
    {
        for (var i = 0; i < n; i++) Say("viewer" + (++_counter).ToString(CultureInfo.InvariantCulture), Prefix + command + " " + options[_random.Next(options.Count)]);
    }

    /// <summary>Exact distribution, e.g. ("A", 12), ("B", 5).</summary>
    public void Votes(string command, params KeyValuePair<string, int>[] counts)
    {
        foreach (var kv in counts)
        {
            for (var i = 0; i < kv.Value; i++) Say("viewer" + (++_counter).ToString(CultureInfo.InvariantCulture), Prefix + command + " " + kv.Key);
        }
    }
}
