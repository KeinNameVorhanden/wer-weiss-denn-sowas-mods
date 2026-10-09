using WWDS.ModCore;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.ModCore.Time;
using WWDS.Protocol;
using WWDS.Twitch;
using WWDS.Twitch.Irc;
using Xunit;

namespace WWDS.Tests.Twitch;

public class IrcParserTests
{
    [Fact]
    public void ParsesTaggedPrivmsg_WithEscapedValuesAndBadges()
    {
        const string raw = "@badge-info=subscriber/14;badges=moderator/1,subscriber/12;color=#FF0000;display-name=Quiz\\sFan;emotes=;id=b34ccfc7-4977-403a-8a94-33c6bac34fb8;mod=1;room-id=1337;subscriber=1;tmi-sent-ts=1507246572675;user-id=99887766;user-type=mod :quizfan!quizfan@quizfan.tmi.twitch.tv PRIVMSG #streamer :!vote b\r\n";

        var line = IrcParser.Parse(raw)!;
        var chat = IrcParser.ToChat(line)!;

        Assert.Equal("PRIVMSG", line.Command);
        Assert.Equal("#streamer", line.Params[0]);
        Assert.Equal("Quiz Fan", chat.DisplayName);
        Assert.Equal("quizfan", chat.Login);
        Assert.Equal("99887766", chat.UserId);
        Assert.Equal("!vote b", chat.Text);
        Assert.True(chat.IsModerator);
        Assert.True(chat.IsSubscriber);
        Assert.False(chat.IsBroadcaster);
    }

    [Fact]
    public void ActionMessages_AreUnwrapped_AndNonChatLinesIgnored()
    {
        var action = IrcParser.ToChat(IrcParser.Parse("@badges=broadcaster/1;user-id=1 :me!me@me.tmi.twitch.tv PRIVMSG #me :" + (char)1 + "ACTION waves" + (char)1)!)!;
        Assert.Equal("waves", action.Text);
        Assert.True(action.IsBroadcaster);

        var ping = IrcParser.Parse("PING :tmi.twitch.tv")!;
        Assert.Equal("PING", ping.Command);
        Assert.Equal("tmi.twitch.tv", ping.Trailing);
        Assert.Null(IrcParser.ToChat(ping));
        Assert.Null(IrcParser.Parse("@badges=broken-no-space"));
    }
}

public class TwitchFeatureTests
{
    private static (ModContext ctx, TwitchFeature feature, ManualClock clock) Start(string mode, bool testMode = true)
    {
        var clock = new ManualClock();
        var ctx = TestContext.Create(clock);
        ctx.Config.Bind("Twitch", "Mode", TwitchMode.Off, "").Value = (TwitchMode)Enum.Parse(typeof(TwitchMode), mode);
        ctx.Config.Bind("Twitch", "TestMode", true, "").Value = testMode;
        ctx.Config.Bind("Twitch", "TrustedUsers", "", "").Value = "helper";
        var feature = new TwitchFeature();
        ctx.Features.Register(feature);
        ctx.Features.StartAll(ctx, new CapabilitySet());
        return (ctx, feature, clock);
    }

    [Fact]
    public void ChatCommand_IsDispatchedWithBadgeAuthority_TrustedUsersElevated()
    {
        var (ctx, feature, _) = Start("Mock");
        var seen = new List<(string, Authority)>();
        ctx.Commands.Register(new CommandDefinition("cancel", Authority.Moderator, "", (_, who) =>
        {
            seen.Add((who.DisplayName, who.Authority));
            return new CommandResult(CommandOutcome.Handled);
        }));
        var outcomes = new List<CommandOutcome>();
        ctx.Bus.Subscribe<ChatCommandReceived>(e => outcomes.Add(e.Outcome));

        feature.InjectForTesting(new ChatMessage { UserId = "1", Login = "viewer", DisplayName = "Viewer", Text = "!cancel" });
        feature.InjectForTesting(new ChatMessage { UserId = "2", Login = "helper", DisplayName = "Helper", Text = "!cancel" });
        feature.InjectForTesting(new ChatMessage { UserId = "3", Login = "boss", DisplayName = "Boss", Text = "!CANCEL", IsBroadcaster = true });
        feature.InjectForTesting(new ChatMessage { UserId = "4", Login = "x", DisplayName = "X", Text = "!unknowncommand" });

        Assert.Equal(new[] { CommandOutcome.Denied, CommandOutcome.Handled, CommandOutcome.Handled }, outcomes);
        Assert.Equal(new[] { ("Helper", Authority.Moderator), ("Boss", Authority.Broadcaster) }, seen);
    }

    [Fact]
    public void TestMode_GatesGameActionsForRealTwitch_ButNotForMock()
    {
        var (bridgeCtx, _, _) = Start("Bridge", testMode: true);
        Assert.False(bridgeCtx.Get<GameActionGate>()!.Allow("submit answer"));

        var (mockCtx, _, _) = Start("Mock", testMode: true);
        Assert.True(mockCtx.Get<GameActionGate>()!.Allow("submit answer"));

        var (liveCtx, _, _) = Start("Bridge", testMode: false);
        Assert.True(liveCtx.Get<GameActionGate>()!.Allow("submit answer"));
    }

    [Fact]
    public void ChatReplies_AreRateLimited()
    {
        var (ctx, feature, clock) = Start("Mock", testMode: false);
        ctx.Bus.Publish(new ChatSendRequested("first"));
        ctx.Bus.Publish(new ChatSendRequested("second"));

        feature.Tick();
        clock.Advance(1);
        feature.Tick();
        Assert.Equal(new[] { "first" }, feature.Mock!.Sent.OfType<SendChat>().Select(s => s.Text));

        clock.Advance(1.1);
        feature.Tick();
        Assert.Equal(new[] { "first", "second" }, feature.Mock!.Sent.OfType<SendChat>().Select(s => s.Text));
    }
}
