using System.Text;
using WWDS.Protocol;
using Xunit;

namespace WWDS.Tests.Twitch;

public class ProtocolTests
{
    [Fact]
    public void Framer_ReassemblesLinesSplitAcrossReads_IncludingMultiByteCharacters()
    {
        var text = Codec.Encode(new ChatMessage { DisplayName = "Größenwahn", Text = "!name Straße \U0001F600" });
        var bytes = LineFramer.Frame(text).Concat(Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"seq\":7}\r\n")).ToArray();
        var framer = new LineFramer();
        var lines = new List<string>();

        // Feed one byte at a time: splits every multi-byte sequence.
        for (var i = 0; i < bytes.Length; i++) lines.AddRange(framer.Push(bytes, i, 1));

        Assert.Equal(2, lines.Count);
        var chat = Assert.IsType<ChatMessage>(Codec.Decode(lines[0]));
        Assert.Equal("Größenwahn", chat.DisplayName);
        Assert.Equal("!name Straße \U0001F600", chat.Text);
        Assert.Equal(7, Assert.IsType<Ping>(Codec.Decode(lines[1])).Seq);
    }

    [Fact]
    public void Framer_DropsOversizedLine_AndRecoversOnNextLine()
    {
        var framer = new LineFramer();
        var huge = Encoding.UTF8.GetBytes(new string('x', ProtocolInfo.MaxLineBytes + 10) + "\n");
        var ok = LineFramer.Frame("{\"type\":\"pong\",\"seq\":1}");

        Assert.Empty(framer.Push(huge, 0, huge.Length));
        var lines = framer.Push(ok, 0, ok.Length);

        Assert.Equal(1, framer.DroppedOversizedLines);
        Assert.IsType<Pong>(Codec.Decode(Assert.Single(lines)));
    }

    [Theory]
    [InlineData("{\"type\":\"twitch.chat\",\"bits\":\"lots\"}")]
    [InlineData("{\"type\":")]
    [InlineData("{\"text\":\"no type\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void Decode_RejectsMalformedInput_WithoutThrowing(string line) => Assert.Null(Codec.Decode(line));

    [Fact]
    public void Decode_ToleratesMessagesFromNewerPeers()
    {
        var unknown = Codec.Decode("{\"type\":\"twitch.hypetrain\",\"level\":3}");
        Assert.Equal("twitch.hypetrain", Assert.IsType<UnknownMessage>(unknown).RawType);

        var withExtraField = Codec.Decode("{\"type\":\"twitch.raid\",\"fromLogin\":\"bob\",\"viewers\":42,\"futureField\":true}");
        Assert.Equal(42, Assert.IsType<Raid>(withExtraField).Viewers);
    }
}
