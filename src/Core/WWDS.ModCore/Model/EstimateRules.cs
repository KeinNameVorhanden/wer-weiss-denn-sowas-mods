using System;

namespace WWDS.ModCore.Model;

/// <summary>Who wins an estimate when both teams are equally far off.</summary>
public enum EstimateTie
{
    /// <summary>The team that logged in its final number first.</summary>
    FirstLoggedIn,

    /// <summary>Always Team Elton, as the game does.</summary>
    TeamB,
}

public static class EstimateRules
{
    /// <summary>Team A wins when it is closer, or on an exact tie when the rule gives it to the earlier login.</summary>
    public static bool TeamAWins(float answer, int betA, int betB, EstimateTie rule, float? lockedA, float? lockedB)
    {
        var a = Math.Abs(answer - betA);
        var b = Math.Abs(answer - betB);
        if (Math.Abs(a - b) > 1e-4f) return a < b;
        return rule == EstimateTie.FirstLoggedIn && lockedA.HasValue && (!lockedB.HasValue || lockedA.Value < lockedB.Value);
    }

    /// <summary>Both teams are equally far off.</summary>
    public static bool IsTie(float answer, int betA, int betB) => Math.Abs(Math.Abs(answer - betA) - Math.Abs(answer - betB)) <= 1e-4f;
}
