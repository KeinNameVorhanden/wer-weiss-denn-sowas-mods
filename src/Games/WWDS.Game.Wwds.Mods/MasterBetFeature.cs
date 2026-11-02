using System;
using System.Collections;
using System.Collections.Generic;
using DoozyUI;
using HarmonyLib;
using InControl;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Free amount for the final bet. The game only offers 1 EUR, 25, 50, 75 and 100 % of the score; the
/// middle choice (B) becomes a free amount: it starts at 50 %, up and down on the stick or the arrow
/// keys (and the mouse wheel for the keyboard player) change it in steps (faster when held), B logs it
/// in. The other four choices stay as they are.
/// </summary>
public sealed class MasterBetFeature : FeatureBase
{
    public const string FeatureId = "MasterBet";
    private const int FreeSlot = 2;

    private static MasterBetFeature? _current;
    private readonly Dictionary<Team, int> _amounts = new();
    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<int> _step = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Free final bet";

    public override string Description => "The middle final bet (B) can be set to any amount with up/down or the mouse wheel.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _enabled = Context.Config.Bind("MasterBet", "FreeAmount", true, "The middle final bet (B) is a free amount: up/down (stick, arrows, wheel) changes it.");
        _step = Context.Config.Bind("MasterBet", "Step", 50, "Step of the free amount in EUR (held longer: five times as much).");
        _current = this;
        Context.Provide(this);
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        ui.Patch(new PatchSpec("mods.bet.loop", "Up/down change the free bet while a player bets.",
            () => AccessTools.Method(typeof(ViewMasterBetAnswerFrame), nameof(ViewMasterBetAnswerFrame.OnWaitForAnswerUpdated)), none)
        {
            Postfix = AccessTools.Method(typeof(MasterBetFeature), nameof(LoopPostfix)),
        });
        ui.Patch(new PatchSpec("mods.bet.confirm", "B logs in the free amount.",
            () => AccessTools.Method(typeof(ViewMasterBetAnswerFrame), "ConfirmAnswerButton"), none)
        {
            Prefix = AccessTools.Method(typeof(MasterBetFeature), nameof(ConfirmPrefix)),
        });
    }

    protected override void OnStop() => _current = null;

    /// <summary>Rounded start value and limits: 1 EUR up to the whole score.</summary>
    internal static int Clamp(int amount, int score) => Math.Max(1, Math.Min(amount, Math.Max(1, score)));

    private static void LoopPostfix(ViewMasterBetAnswerFrame __instance, APlayerAgent player, ref IEnumerator __result)
    {
        var self = _current;
        if (self == null || !self._enabled.Value || player is PlayerAgentBot) return;
        __result = self.Loop(__instance, player, __result);
    }

    private IEnumerator Loop(ViewMasterBetAnswerFrame frame, APlayerAgent player, IEnumerator original)
    {
        var team = player.ParentTeam;
        var score = team.GetScore();
        var step = Math.Max(1, _step.Value);
        _amounts[team] = Clamp((int)Math.Round(score * 0.5 / step) * step, score);
        var heldSince = -1f;
        var nextRepeat = 0f;
        var first = true;
        while (original.MoveNext())
        {
            var current = original.Current;
            PatchGuard.Run("mods.bet.loop", () =>
            {
                var input = player.Input as InputPlayer;
                var direction = 0;
                if (input?.Device != null && !(UIManager.Instance != null && UIManager.Instance.gamePaused))
                {
                    var y = input.Device.LeftStick.Y;
                    direction = y > 0.5f ? 1 : y < -0.5f ? -1 : 0;
                }

                var now = Time.unscaledTime;
                var amount = _amounts[team];
                if (direction == 0) heldSince = -1f;
                else if (heldSince < 0f || now >= nextRepeat)
                {
                    if (heldSince < 0f) heldSince = now;
                    var big = now - heldSince > 1.2f;
                    amount = Clamp(amount + (direction * step * (big ? 5 : 1)), score);
                    nextRepeat = now + (heldSince == now ? 0.35f : 0.08f);
                }

                var wheel = Input.mouseScrollDelta.y;
                if (Math.Abs(wheel) > 0.01f && input?.Device is DeviceKeyboard) amount = Clamp(amount + (Math.Sign(wheel) * step), score);
                if (first || amount != _amounts[team])
                {
                    _amounts[team] = amount;
                    Show(frame, amount);
                }

                first = false;
            });
            yield return current;
        }
    }

    /// <summary>Self-test: the free amount of a team while it bets (null = not betting).</summary>
    internal int? AmountFor(Team team) => _amounts.TryGetValue(team, out var a) ? a : (int?)null;

    internal void TestAdjust(Team team, int steps)
    {
        if (_amounts.TryGetValue(team, out var a)) _amounts[team] = Clamp(a + (steps * Math.Max(1, _step.Value)), team.GetScore());
    }

    private static void Show(ViewMasterBetAnswerFrame frame, int amount)
    {
        var buttons = (ViewAnswerButton[])AccessTools.Field(typeof(ViewMasterBetAnswerFrame), "answerButtons").GetValue(frame);
        if (buttons != null && buttons.Length > FreeSlot) buttons[FreeSlot].SetAnswerText("< " + SUtility.FormatStringForCurrency(amount) + " >");
    }

    private static bool ConfirmPrefix(ViewMasterBetAnswerFrame __instance, Team team, int index)
    {
        var self = _current;
        if (self == null || !self._enabled.Value || index != FreeSlot || !self._amounts.TryGetValue(team, out var amount)) return true;
        var handled = false;
        PatchGuard.Run("mods.bet.confirm", () =>
        {
            team.SetBet(Clamp(amount, team.GetScore()));
            foreach (var button in (ViewAnswerButton[])AccessTools.Field(typeof(ViewMasterBetAnswerFrame), "answerButtons").GetValue(__instance)) button.SelectAnswer();
            ((AGameEventHandler)AccessTools.Field(typeof(ViewMasterBetAnswerFrame), "eventHandler").GetValue(__instance)).SendBet(team);
            self._amounts.Remove(team);
            self.Log.Info("Free final bet: " + amount + " EUR");
            handled = true;
        });
        return !handled;
    }
}
