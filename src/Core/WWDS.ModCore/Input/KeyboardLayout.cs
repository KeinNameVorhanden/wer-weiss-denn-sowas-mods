using System.Collections.Generic;
using WWDS.ModCore.Model;

namespace WWDS.ModCore.Input;

/// <summary>Controller buttons of the game (InControl Action1..4 and the bumpers).</summary>
public enum PadButton
{
    A,
    B,
    X,
    Y,
    LB,
    RB,
}

/// <summary>A button press on one of the two keyboard "controllers" (0 = arrows/J-K-I-L, 1 = WASD/1-4).</summary>
public struct KeyboardPress
{
    public KeyboardPress(int keyboard, PadButton button)
    {
        Keyboard = keyboard;
        Button = button;
    }

    public int Keyboard { get; }

    public PadButton Button { get; }

    public override string ToString() => "K" + Keyboard + ":" + Button;
}

/// <summary>
/// Extra show keys on top of the game's own layouts. One keyboard player: Enter/Space confirm,
/// 1-3 (top row or keypad) answer A/B/C, 1-5 pick the master bet, 0 calls the audience joker.
/// Two keyboard players: the top-row digits and Space belong to player 2's own layout (1-4 are its
/// A/B/X/Y), so player 1 uses Enter and the keypad and player 2 gets Space plus X/C/V for the answers.
/// Answers A/B/C are the game's Y/B/A buttons, bets 1-5 are LB/A/B/Y/RB.
/// </summary>
public static class KeyboardLayout
{
    private static readonly PadButton[] Answers = { PadButton.Y, PadButton.B, PadButton.A };
    private static readonly PadButton[] Bets = { PadButton.LB, PadButton.A, PadButton.B, PadButton.Y, PadButton.RB };
    private static readonly string[] P2Answers = { "X", "C", "V" };

    /// <summary>Every key name (UnityEngine.KeyCode) the layout reads.</summary>
    public static readonly string[] Keys =
    {
        "Return", "KeypadEnter", "Space", "X", "C", "V",
        "Alpha0", "Alpha1", "Alpha2", "Alpha3", "Alpha4", "Alpha5",
        "Keypad0", "Keypad1", "Keypad2", "Keypad3", "Keypad4", "Keypad5",
    };

    /// <summary>Presses for the keys that went down this frame during a show.</summary>
    public static List<KeyboardPress> Map(GamePhase phase, bool twoKeyboards, ICollection<string> down)
    {
        var result = new List<KeyboardPress>();
        var question = phase is GamePhase.QuestionPresented or GamePhase.WaitingForAnswer;
        if (down.Contains("Return") || down.Contains("KeypadEnter") || (!twoKeyboards && down.Contains("Space"))) result.Add(new KeyboardPress(0, PadButton.A));
        if (twoKeyboards && down.Contains("Space")) result.Add(new KeyboardPress(1, PadButton.A));
        var digit = Digit(down, keypad: true, topRow: !twoKeyboards);
        if (digit >= 1 && question && digit <= Answers.Length) result.Add(new KeyboardPress(0, Answers[digit - 1]));
        if (digit >= 1 && phase == GamePhase.MasterBet && digit <= Bets.Length) result.Add(new KeyboardPress(0, Bets[digit - 1]));
        if (digit == 0 && question) result.Add(new KeyboardPress(0, PadButton.LB));
        if (twoKeyboards && question)
        {
            for (var i = 0; i < P2Answers.Length; i++)
            {
                if (down.Contains(P2Answers[i])) result.Add(new KeyboardPress(1, Answers[i]));
            }
        }

        return result;
    }

    /// <summary>The digit 0-5 that went down (keypad and/or top row), or -1.</summary>
    private static int Digit(ICollection<string> down, bool keypad, bool topRow)
    {
        for (var i = 0; i <= 5; i++)
        {
            if ((keypad && down.Contains("Keypad" + i)) || (topRow && down.Contains("Alpha" + i))) return i;
        }

        return -1;
    }
}
