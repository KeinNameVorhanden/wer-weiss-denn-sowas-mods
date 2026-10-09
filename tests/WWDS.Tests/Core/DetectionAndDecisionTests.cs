using WWDS.ModCore.Adapters;
using WWDS.ModCore.Detection;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;
using Xunit;

namespace WWDS.Tests.Core;

public class DetectionTests
{
    [Fact]
    public void KnownHash_IsVerified_ForEachGame()
    {
        var g1 = GameDetector.Detect("Wer Weiß Denn Sowas", "Wer Weiß Denn Sowas_Data", WwdsProfiles.Game1.KnownBuilds[0].AssemblySha256.ToLowerInvariant(), "2018.2.11f1");
        var g2 = GameDetector.Detect("WWDS2", "WWDS2_Data", WwdsProfiles.Game2.KnownBuilds[0].AssemblySha256, "2018.2.20f1");

        Assert.Equal((GameId.Wwds1, Compatibility.Verified), (g1.Game, g1.Compatibility));
        Assert.Equal((GameId.Wwds2, Compatibility.Verified), (g2.Game, g2.Compatibility));
    }

    [Fact]
    public void UpdatedGame_IsIdentifiedButFlaggedUnknownBuild()
    {
        // Game 2's hash inside Game 1's process means Game 1 got a new Assembly-CSharp.
        var r = GameDetector.Detect("Wer Weiß Denn Sowas", "Wer Weiß Denn Sowas_Data", WwdsProfiles.Game2.KnownBuilds[0].AssemblySha256, "2018.2.12f1");

        Assert.Equal(GameId.Wwds1, r.Game);
        Assert.Equal(Compatibility.UnknownBuild, r.Compatibility);
        Assert.Contains("4288409", r.Message);
        Assert.Contains("2018.2.12f1", r.Message);
        Assert.Contains("unknown", r.Message);
    }

    [Fact]
    public void OtherUnityGame_IsUnsupported()
    {
        var r = GameDetector.Detect("SomeGame", "SomeGame_Data", "ABC", "2019.4.1f1");
        Assert.Equal(Compatibility.Unsupported, r.Compatibility);
        Assert.Equal(GameId.Unknown, r.Game);
    }
}

public class DecisionRequestTests
{
    private static DecisionRequest Answer() => new(DecisionKind.Answer, TeamSlot.B, null, null,
        new List<DecisionOption> { new(0, "A", "x"), new(1, "B", "y"), new(2, "C", "z") }, 10, 25);

    [Fact]
    public void Resolve_RejectsOutOfRange_AndSecondResolution()
    {
        var r = Answer();
        var completed = 0;
        r.Completed += _ => completed++;

        Assert.False(r.TryResolve(3, "vote"));
        Assert.True(r.TryResolve(1, "vote"));
        Assert.False(r.TryResolve(2, "mod"));
        Assert.False(r.MarkTimedOut());

        Assert.Equal((DecisionStatus.Resolved, 1, "vote"), (r.Status, r.ChosenIndex, r.ResolvedBy));
        Assert.Equal(1, completed);
    }

    [Fact]
    public void Timeout_AppliesFallback()
    {
        var r = Answer();
        r.FallbackIndex = 2;
        Assert.True(r.MarkTimedOut());
        Assert.Equal((DecisionStatus.TimedOut, 2), (r.Status, r.ChosenIndex));
    }

    [Fact]
    public void Estimate_ClampsToRange_AndRejectsNaN()
    {
        var r = new DecisionRequest(DecisionKind.Estimate, TeamSlot.A, null, null, new List<DecisionOption>(), 0, null)
        {
            EstimateMin = 1,
            EstimateMax = 1000,
        };

        Assert.False(r.TryResolveNumber(double.NaN, "chat"));
        Assert.True(r.TryResolveNumber(250000, "chat"));
        Assert.Equal(1000, r.ChosenNumber);
    }
}

public class SmallParsingTests
{
    [Theory]
    [InlineData("a", 0)]
    [InlineData(" B ", 1)]
    [InlineData("3", 2)]
    [InlineData("D", -1)]
    [InlineData("4", -1)]
    [InlineData("AB", -1)]
    [InlineData("", -1)]
    public void AnswerKeys(string text, int expected)
    {
        var ok = AnswerOption.TryParseKey(text, 3, out var index);
        Assert.Equal(expected >= 0, ok);
        Assert.Equal(expected, index);
    }

    [Theory]
    [InlineData("connect PASS oauth:abcdefghijklmnop123 now", "oauth:***")]
    [InlineData("{\"access_token\":\"xyz123secretvalue\",\"x\":1}", "\"access_token\":\"***\"")]
    [InlineData("Authorization: Bearer abc.def-ghi_jkl123", "Bearer ***")]
    [InlineData("refresh_token=r3fr3shT0ken&client_id=abc", "refresh_token=***&client_id=abc")]
    public void Redactor_MasksSecrets(string input, string expectedFragment)
    {
        var redacted = Redactor.Redact(input);
        Assert.Contains(expectedFragment, redacted);
        Assert.DoesNotContain("secretvalue", redacted);
        Assert.DoesNotContain("abcdefghijklmnop123", redacted);
        Assert.DoesNotContain("r3fr3shT0ken", redacted);
    }
}
