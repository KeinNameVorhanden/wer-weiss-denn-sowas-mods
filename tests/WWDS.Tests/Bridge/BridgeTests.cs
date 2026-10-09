using System.Net.Sockets;
using TwitchLib.EventSub.Core.Models.Chat;
using ChatMessage = WWDS.Protocol.ChatMessage;
using TwitchChatText = TwitchLib.EventSub.Core.Models.Chat.ChatMessage;
using TwitchLib.EventSub.Core.SubscriptionTypes.Channel;
using WWDS.Bridge;
using WWDS.Bridge.Twitch;
using WWDS.Protocol;
using Xunit;

namespace WWDS.Tests.Bridge;

public class GameServerTests
{
    private static async Task<(TcpClient, NetworkStream, LineFramer)> Connect(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        return (tcp, tcp.GetStream(), new LineFramer());
    }

    private static async Task<Message?> Read(NetworkStream s, LineFramer framer, Queue<string> pending)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (pending.Count == 0)
        {
            var n = await s.ReadAsync(buffer, cts.Token);
            if (n <= 0) return null;
            foreach (var l in framer.Push(buffer, 0, n)) pending.Enqueue(l);
        }

        return Codec.Decode(pending.Dequeue());
    }

    [Fact]
    public async Task WrongKey_IsRejected_AndNeverReceivesBroadcasts_RightKeyDoes()
    {
        var log = new List<string>();
        await using var server = new GameServer(0, "secret-key-0123456789", s => { lock (log) log.Add(s); });
        server.Start();

        var (badTcp, bad, badFramer) = await Connect(server.Port);
        var badPending = new Queue<string>();
        await bad.WriteAsync(LineFramer.Frame(Codec.Encode(new Hello { Role = "game", Key = "guess" })));
        Assert.IsType<ErrorMessage>(await Read(bad, badFramer, badPending));
        Assert.Null(await Read(bad, badFramer, badPending));

        var (goodTcp, good, goodFramer) = await Connect(server.Port);
        var goodPending = new Queue<string>();
        await good.WriteAsync(LineFramer.Frame(Codec.Encode(new Hello { Role = "game", Key = "secret-key-0123456789", Game = "Wwds2" })));
        Assert.IsType<Hello>(await Read(good, goodFramer, goodPending));
        Assert.IsType<TwitchStatus>(await Read(good, goodFramer, goodPending));

        var received = new TaskCompletionSource<Message>();
        server.FromGame += (m, _) => received.TrySetResult(m);
        server.Broadcast(new ChatMessage { Login = "viewer", Text = "!vote A" });
        Assert.Equal("!vote A", Assert.IsType<ChatMessage>(await Read(good, goodFramer, goodPending)).Text);

        await good.WriteAsync(LineFramer.Frame(Codec.Encode(new SendChat { Text = "A wins" })));
        var fromGame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("A wins", Assert.IsType<SendChat>(fromGame).Text);
        Assert.Equal(1, server.ConnectedGames);

        badTcp.Dispose();
        goodTcp.Dispose();
    }
}

public class BridgeHostTests
{
    [Fact]
    public async Task GameCanConfigureTheBridge_AndLoginWithoutClientIdFailsClearly()
    {
        var root = Path.Combine(Path.GetTempPath(), "wwds-bridge-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new BridgePaths(root);
            File.WriteAllText(paths.ConfigFile, "{\"Port\":0}");
            await using var host = new BridgeHost(paths, mockOnly: false, _ => { }, builtInClientId: string.Empty);
            await host.StartAsync();

            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", host.Server.Port);
            var stream = tcp.GetStream();
            var framer = new LineFramer();
            var pending = new Queue<string>();
            async Task Send(Message m) => await stream.WriteAsync(LineFramer.Frame(Codec.Encode(m)));
            async Task<T> Next<T>() where T : Message
            {
                var buffer = new byte[4096];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    while (pending.Count > 0)
                    {
                        if (Codec.Decode(pending.Dequeue()) is T hit) return hit;
                    }

                    var n = await stream.ReadAsync(buffer, cts.Token);
                    foreach (var l in framer.Push(buffer, 0, n)) pending.Enqueue(l);
                }
            }

            await Send(new Hello { Role = "game", Key = File.ReadAllText(paths.KeyFile).Trim(), Game = "Wwds1" });
            Assert.False((await Next<BridgeSettings>()).HasClientId);

            await Send(new LoginStart());
            Assert.Contains("No Client ID", (await Next<LoginResult>()).Error);

            await Send(new BridgeSettings { ClientId = "  abc123  ", DryRun = false });
            var echoed = await Next<BridgeSettings>();
            Assert.Equal((true, "abc123", false), (echoed.HasClientId, echoed.ClientId, echoed.DryRun));
            Assert.Contains("\"ClientId\": \"abc123\"", File.ReadAllText(paths.ConfigFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class BuiltInClientIdTests
{
    [Fact]
    public void ReleaseClientId_IsUsedUntilTheStreamerEntersTheirOwn_AndIsNeverWrittenToBridgeJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "wwds-bridge-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new BridgePaths(root);
            File.WriteAllText(paths.ConfigFile, "{\"Port\":0}");
            var host = new BridgeHost(paths, mockOnly: true, _ => { }, builtInClientId: "abcdefghij0123456789abcdefghij");

            var s = host.Settings();
            Assert.Equal((true, true, string.Empty), (s.HasClientId, s.ClientIdBuiltIn, s.ClientId));
            Assert.Equal("abcdefghij0123456789abcdefghij", host.ClientId);

            host.Config.ClientId = "ownappid0123456789ownapp";
            host.Config.Save(paths);
            Assert.Equal(("ownappid0123456789ownapp", false), (host.ClientId, host.Settings().ClientIdBuiltIn));
            Assert.DoesNotContain("abcdefghij", File.ReadAllText(paths.ConfigFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class MockAudienceTests
{
    [Fact]
    public void Votes_ProducesExactDistribution_FromDistinctViewers()
    {
        var mock = new MockAudience();
        var msgs = mock.Run("votes A=12 B=5 C=0 answer")!.Cast<ChatMessage>().ToList();

        Assert.Equal(17, msgs.Count);
        Assert.Equal(12, msgs.Count(m => m.Text == "!answer A"));
        Assert.Equal(5, msgs.Count(m => m.Text == "!answer B"));
        Assert.Equal(17, msgs.Select(m => m.UserId).Distinct().Count());
    }

    [Fact]
    public void Crowd_IsDeterministicPerSeed_AndQuotedArgumentsWork()
    {
        var first = new MockAudience(7).Run("crowd 50 name QuizMaster \"Elton Fan\" Bob")!.Cast<ChatMessage>().Select(m => m.Text).ToList();
        var second = new MockAudience(7).Run("crowd 50 name QuizMaster \"Elton Fan\" Bob")!.Cast<ChatMessage>().Select(m => m.Text).ToList();

        Assert.Equal(first, second);
        Assert.Contains("!name Elton Fan", first);
        Assert.Null(new MockAudience().Run("votes A=x"));
        Assert.Null(new MockAudience().Run("dance"));
        var redeem = Assert.IsType<Redemption>(Assert.Single(new MockAudience().Run("redeem bob \"Name the Opponent\" Captain Quiz")!));
        Assert.Equal(("Name the Opponent", "Captain Quiz"), (redeem.RewardTitle, redeem.Input));
    }
}

public class TwitchMappingTests
{
    [Fact]
    public void ChatBadges_MapToFlags_BroadcasterAlsoByUserId()
    {
        var e = new ChannelChatMessage
        {
            BroadcasterUserId = "42",
            ChatterUserId = "42",
            ChatterUserLogin = "streamer",
            ChatterUserName = "Streamer",
            Message = new TwitchChatText { Text = "!crowd" },
            Badges = new[] { new ChatBadge { SetId = "founder", Id = "0" } },
        };

        var m = TwitchMapping.Chat(e);

        Assert.True(m.IsBroadcaster);
        Assert.True(m.IsSubscriber);
        Assert.False(m.IsModerator);
        Assert.Equal("!crowd", m.Text);
    }

    [Fact]
    public void SubscriptionConditions_UseTheFieldsTwitchRequiresPerType()
    {
        Assert.Equal("42", TwitchMapping.Subscription("channel.raid", "42", "42")!.Value.Condition["to_broadcaster_user_id"]);
        var follow = TwitchMapping.Subscription("channel.follow", "42", "7")!.Value;
        Assert.Equal(("2", "7"), (follow.Version, follow.Condition["moderator_user_id"]));
        Assert.Equal("7", TwitchMapping.Subscription("channel.chat.message", "42", "7")!.Value.Condition["user_id"]);
        Assert.Null(TwitchMapping.Subscription("channel.hype_train.begin", "42", "7"));
    }
}
