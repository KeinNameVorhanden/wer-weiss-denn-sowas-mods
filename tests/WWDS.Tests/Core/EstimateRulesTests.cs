using WWDS.ModCore.Model;
using Xunit;

namespace WWDS.Tests.Core;

public class EstimateRulesTests
{
    [Fact]
    public void Closer_Wins_RegardlessOfLoginOrder()
    {
        Assert.True(EstimateRules.TeamAWins(1665, 1600, 1800, EstimateTie.FirstLoggedIn, 9f, 2f));
        Assert.False(EstimateRules.TeamAWins(1665, 1800, 1600, EstimateTie.FirstLoggedIn, 2f, 9f));
    }

    [Fact]
    public void ExactTie_GoesToTheEarlierLogin_OrAlwaysToTeamB_WithTheGamesRule()
    {
        // 90 and 110 for 100: both 10 off.
        Assert.True(EstimateRules.TeamAWins(100, 90, 110, EstimateTie.FirstLoggedIn, 3f, 7f));
        Assert.False(EstimateRules.TeamAWins(100, 90, 110, EstimateTie.FirstLoggedIn, 7f, 3f));
        Assert.False(EstimateRules.TeamAWins(100, 90, 110, EstimateTie.TeamB, 3f, 7f));
        Assert.True(EstimateRules.IsTie(100, 110, 90));
    }
}
