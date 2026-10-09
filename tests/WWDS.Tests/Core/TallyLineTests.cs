using WWDS.ModCore.Events;
using Xunit;

namespace WWDS.Tests.Core;

public class TallyLineTests
{
    [Fact]
    public void VoteLine_KeepsShareCountAndLabel_IncludingSpacesInTheLabel()
    {
        var l = TallyLine.Parse("B  52% (13)  Bernd das Brot");
        Assert.Equal(("B", 0.52, 13, "Bernd das Brot"), (l.Key, l.Share!.Value, l.Count!.Value, l.Label));
    }

    [Fact]
    public void NamingLines_GetSharesFromTheirSupporterCounts_AndKeepPendingFlag()
    {
        var rows = TallyLine.ParseAll(new[] { "#2 Elton Fan (3)", "#1 Quiz (1) [pending]" });
        Assert.Equal(0.75, rows[0].Share!.Value, 6);
        Assert.Equal(("#1", "Quiz", true), (rows[1].Key, rows[1].Label, rows[1].Pending));
        Assert.Equal("!name <Name> oder !vote 2", TallyLine.CommandHint(rows));
    }

    [Fact]
    public void OptionGuesses_AndPlainNumericLines_MixWithoutBreakingShares()
    {
        var rows = TallyLine.ParseAll(new[] { "A: 0", "B: 4", "median 1500" });
        Assert.Equal(0, rows[0].Share!.Value);
        Assert.Equal(1, rows[1].Share!.Value);
        Assert.True(rows[2].IsPlain);
        Assert.Equal("!vote A oder B", TallyLine.CommandHint(rows));
    }

    [Fact]
    public void NoVotesYet_GivesZeroSharesAndANumberHintForNumericRounds()
    {
        var rows = TallyLine.ParseAll(new[] { "0 entries", "Schätzung -" });
        Assert.All(rows, r => Assert.True(r.IsPlain));
        Assert.Equal("!vote <Zahl>", TallyLine.CommandHint(rows));
        Assert.Equal("!vote A, B oder C", TallyLine.CommandHint(TallyLine.ParseAll(new[] { "A  0% (0)  x", "B  0% (0)  y", "C  0% (0)  z" })));
    }
}
