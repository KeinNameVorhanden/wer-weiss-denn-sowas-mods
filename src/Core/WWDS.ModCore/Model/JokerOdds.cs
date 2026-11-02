using System;
using System.Collections.Generic;

namespace WWDS.ModCore.Model;

/// <summary>The audience joker's answer: right with the member's reliability, otherwise one of the two wrong answers.</summary>
public static class JokerOdds
{
    /// <param name="correct">Correct answer 0..2.</param>
    /// <param name="reliability">Chance to answer correctly, 0..1.</param>
    /// <param name="roll">Random number 0..1.</param>
    /// <param name="wrongPick">Random number choosing between the two wrong answers.</param>
    public static int Answer(int correct, double reliability, double roll, int wrongPick)
    {
        if (roll < reliability) return correct;
        var wrong = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            if (i != correct) wrong.Add(i);
        }

        return wrong[Math.Abs(wrongPick) % wrong.Count];
    }
}
