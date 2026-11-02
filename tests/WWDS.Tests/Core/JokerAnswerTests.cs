using System;
using WWDS.ModCore.Model;
using Xunit;

namespace WWDS.Tests.Core;

public class JokerAnswerTests
{
    [Fact]
    public void WrongBranch_NeverReturnsTheCorrectAnswer_AndReliabilityHoldsOverManyRolls()
    {
        var rng = new Random(7);
        var right = 0;
        for (var n = 0; n < 20000; n++)
        {
            var correct = n % 3;
            var a = JokerOdds.Answer(correct, 0.7, rng.NextDouble(), rng.Next(2));
            if (a == correct) right++;
            Assert.InRange(a, 0, 2);
        }

        Assert.InRange(right / 20000.0, 0.68, 0.72);
        Assert.Equal(new[] { 0, 2 }, new[] { JokerOdds.Answer(1, 0.5, 0.9, 0), JokerOdds.Answer(1, 0.5, 0.9, 1) });
    }
}
