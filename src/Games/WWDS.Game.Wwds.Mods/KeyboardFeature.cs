using System.Collections.Generic;
using InControl;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Keyboard shortcuts for the keyboard player during a show, on top of the game's J/K/I/L layout:
/// Enter or Space confirms (A), 1/2/3 answer A/B/C, 1-5 pick the master bet. They become presses of the
/// game's own buttons (the game shows Y/B/A for answers A/B/C and LB/A/B/Y/RB for the bets), so its turn
/// and confirm rules stay intact. Menus are left alone: they already read Enter themselves.
/// </summary>
public sealed class KeyboardFeature : FeatureBase
{
    public const string FeatureId = "Keyboard";

    private static readonly InputControlType[] Answers = { InputControlType.Action4, InputControlType.Action2, InputControlType.Action1 };
    private static readonly InputControlType[] Bets =
    {
        InputControlType.LeftBumper, InputControlType.Action1, InputControlType.Action2, InputControlType.Action4, InputControlType.RightBumper,
    };

    private IConfigValue<bool> _enabled = null!;
    private ModUi _ui = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Keyboard shortcuts";

    public override string Description => "Enter/Space confirm, 1-3 answers, 1-5 master bets for the keyboard player in a show.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _enabled = Context.Config.Bind("Keyboard", "ShowShortcuts", true, "Enter/Space = A, 1/2/3 = answer A/B/C, 1-5 = master bet, during a show.");
        _ui = ModUi.For(Context);
    }

    public override void Tick()
    {
        if (!_enabled.Value || !_ui.InShow || _ui.AnyOpen || _ui.Blocker.Active) return;
        var keyboard = VirtualPad.Keyboard;
        if (keyboard == null || (keyboard is DeviceKeyboard k && k.TextMode)) return;
        foreach (var control in Controls(Context.Adapter?.GetSnapshot().Phase ?? GamePhase.Unknown, Digit(), Confirm()))
        {
            _ui.Pad.Press(keyboard, control);
        }
    }

    /// <summary>Game buttons for the pressed shortcut in a phase (digit 1-based, 0 = none).</summary>
    internal static IEnumerable<InputControlType> Controls(GamePhase phase, int digit, bool confirm)
    {
        if (confirm) yield return InputControlType.Action1;
        if (digit <= 0) yield break;
        switch (phase)
        {
            case GamePhase.QuestionPresented or GamePhase.WaitingForAnswer when digit <= Answers.Length:
                yield return Answers[digit - 1];
                break;
            case GamePhase.MasterBet when digit <= Bets.Length:
                yield return Bets[digit - 1];
                break;
        }
    }

    private static bool Confirm() => Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space);

    private static int Digit()
    {
        for (var i = 1; i <= 5; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha0 + i) || Input.GetKeyDown(KeyCode.Keypad0 + i)) return i;
        }

        return 0;
    }
}
