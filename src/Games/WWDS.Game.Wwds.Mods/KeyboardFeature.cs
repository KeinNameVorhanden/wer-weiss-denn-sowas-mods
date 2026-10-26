using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using InControl;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Input;
using WWDS.ModCore.Model;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Keyboard shortcuts on top of the game's two keyboard layouts (see <see cref="KeyboardLayout"/>): in a
/// show Enter/Space confirm, digits answer and bet, 0 calls the joker; with two keyboard players each gets
/// keys that never touch the other's layout. Outside a show Enter confirms on the screens that ignore it
/// (player slots, tutorial question, Tech room, archive). Every shortcut becomes a press of the game's own
/// buttons, so its turn and confirm rules stay intact.
/// </summary>
public sealed class KeyboardFeature : FeatureBase
{
    public const string FeatureId = "Keyboard";

    /// <summary>Menu screens that read the A button but not Enter (the others poll Enter themselves).</summary>
    private static readonly string[] EnterStates = { "StateScreenSelectPlayers", "StateScreenTechMenu", "StateScreenArchiveMenu" };

    private static readonly KeyCode[] Codes = KeyboardLayout.Keys.Select(k => (KeyCode)Enum.Parse(typeof(KeyCode), k)).ToArray();
    private readonly HashSet<string> _down = new();
    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<bool> _twoPlayerKeys = null!;
    private IConfigValue<bool> _menuEnter = null!;
    private ModUi _ui = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Keyboard shortcuts";

    public override string Description => "Enter/Space confirm, digits answer and bet, 0 = joker; two keyboard players get separate keys; Enter in every menu.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        var c = Context.Config;
        _enabled = c.Bind("Keyboard", "ShowShortcuts", true, "In a show: Enter/Space = A, 1/2/3 = answer A/B/C, 1-5 = master bet, 0 = audience joker.");
        _twoPlayerKeys = c.Bind("Keyboard", "TwoPlayerKeys", true, "Two keyboard players: player 1 uses Enter and the keypad, player 2 Space and X/C/V (top-row digits are player 2's buttons).");
        _menuEnter = c.Bind("Keyboard", "EnterInMenus", true, "Enter confirms on the screens that only react to J (player slots, tutorial question, Tech room, archive).");
        _ui = ModUi.For(Context);
    }

    public override void Tick()
    {
        if (_ui.AnyOpen || _ui.Blocker.Active) return;
        var keyboards = InputManager.Devices.OfType<DeviceKeyboard>().ToList();
        if (keyboards.Count == 0 || keyboards.Any(k => k.TextMode)) return;
        if (_ui.InShow)
        {
            if (_enabled.Value) ShowKeys(keyboards);
        }
        else if (_menuEnter.Value && MenuEnterAllowed() && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
        {
            Press(keyboards, new KeyboardPress(0, PadButton.A));
        }
    }

    private void ShowKeys(List<DeviceKeyboard> keyboards)
    {
        _down.Clear();
        for (var i = 0; i < Codes.Length; i++)
        {
            if (Input.GetKeyDown(Codes[i])) _down.Add(KeyboardLayout.Keys[i]);
        }

        if (_down.Count == 0) return;
        var two = _twoPlayerKeys.Value && TwoKeyboardPlayers();
        var phase = Context.Adapter?.GetSnapshot().Phase ?? GamePhase.Unknown;
        foreach (var press in KeyboardLayout.Map(phase, two, _down)) Press(keyboards, press);
    }

    private void Press(List<DeviceKeyboard> keyboards, KeyboardPress press)
    {
        var device = keyboards.FirstOrDefault(k => k.DeviceId == press.Keyboard);
        if (device != null) _ui.Pad.Press(device, ToControl(press.Button));
    }

    internal static InputControlType ToControl(PadButton button) => button switch
    {
        PadButton.A => InputControlType.Action1,
        PadButton.B => InputControlType.Action2,
        PadButton.X => InputControlType.Action3,
        PadButton.Y => InputControlType.Action4,
        PadButton.LB => InputControlType.LeftBumper,
        _ => InputControlType.RightBumper,
    };

    /// <summary>True when both keyboard layouts play in the current show.</summary>
    internal static bool TwoKeyboardPlayers()
    {
        var session = SDataAccess.ActiveSession;
        if (session == null) return false;
        var ids = new HashSet<int>();
        foreach (var team in new[] { session.TeamA, session.TeamB })
        {
            if (team == null) continue;
            foreach (var agent in team)
            {
                if (agent is PlayerAgentLocal && agent.Input is InputPlayer { Device: DeviceKeyboard k }) ids.Add(k.DeviceId);
            }
        }

        return ids.Count >= 2;
    }

    private bool MenuEnterAllowed()
    {
        if (Array.IndexOf(EnterStates, _ui.State) < 0) return false;
        if (_ui.State != "StateScreenSelectPlayers") return true;
        // The load-savegame hint already reads Enter itself.
        var view = UnityEngine.Object.FindObjectOfType<ViewSelectPlayers>();
        return view == null || !(bool)AccessTools.Field(typeof(ViewSelectPlayers), "loadSavegameHintActive").GetValue(view);
    }
}
