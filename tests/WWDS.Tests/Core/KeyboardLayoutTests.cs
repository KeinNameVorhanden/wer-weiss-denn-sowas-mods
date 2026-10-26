using System.Linq;
using WWDS.ModCore.Input;
using WWDS.ModCore.Model;
using Xunit;

namespace WWDS.Tests.Core;

public class KeyboardLayoutTests
{
    private static string Map(GamePhase phase, bool two, params string[] keys) =>
        string.Join(" ", KeyboardLayout.Map(phase, two, keys).Select(p => p.ToString()));

    [Fact]
    public void TwoKeyboards_TopRowDigitsNeverPressForPlayerOne_InTheParallelMasterPhases()
    {
        // 1-4 are player 2's own A/B/X/Y; a P1 shortcut on them would answer for both teams at once.
        Assert.Equal("", Map(GamePhase.WaitingForAnswer, true, "Alpha1"));
        Assert.Equal("", Map(GamePhase.MasterBet, true, "Alpha2"));
        Assert.Equal("K0:B", Map(GamePhase.MasterBet, true, "Keypad3"));
    }

    [Fact]
    public void TwoKeyboards_SpaceAndXCV_GoToPlayerTwo_EnterToPlayerOne()
    {
        Assert.Equal("K0:A K1:A", Map(GamePhase.WaitingForAnswer, true, "Return", "Space"));
        Assert.Equal("K1:Y K1:A", Map(GamePhase.WaitingForAnswer, true, "X", "V"));
        Assert.Equal("", Map(GamePhase.CategorySelection, true, "C"));
    }

    [Fact]
    public void OneKeyboard_KeepsTopRowAndSpace_AndZeroCallsTheJokerOnlyDuringQuestions()
    {
        Assert.Equal("K0:A", Map(GamePhase.Reveal, false, "Space"));
        Assert.Equal("K0:Y", Map(GamePhase.QuestionPresented, false, "Alpha1"));
        Assert.Equal("K0:RB", Map(GamePhase.MasterBet, false, "Alpha5"));
        Assert.Equal("K0:LB", Map(GamePhase.WaitingForAnswer, false, "Alpha0"));
        Assert.Equal("", Map(GamePhase.MasterBet, false, "Keypad0"));
    }
}
