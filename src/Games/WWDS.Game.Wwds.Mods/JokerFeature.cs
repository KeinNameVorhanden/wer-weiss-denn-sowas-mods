using System;
using System.Collections;
using System.Collections.Generic;
using DoozyUI;
using HarmonyLib;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// The audience joker as designed. The game rates every audience member's reliability (50 to 100 %),
/// but a bug always makes the joker answer correctly, so it is a free pass. Now the chosen member answers
/// correctly with their own reliability (or a fixed percentage; "always right" keeps the shipped
/// behaviour). The picker also no longer waits forever for a player without a usable controller: after a
/// few seconds the member is picked as for the game's AI.
/// </summary>
public sealed class JokerFeature : FeatureBase
{
    public const string FeatureId = "Joker";

    /// <summary>Accuracy value for "each audience member's own reliability".</summary>
    public const int PerMember = 0;

    /// <summary>Accuracy value for the game as shipped (always right).</summary>
    public const int AlwaysRight = 100;

    private static JokerFeature? _current;
    private static readonly System.Random Random = new();
    private IConfigValue<int> _accuracy = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Audience joker";

    public override string Description => "The audience joker can be wrong (each audience member's own reliability, or a fixed percentage); no picker softlock.";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<int> Accuracy => _accuracy;

    protected override void OnStart()
    {
        _accuracy = Context.Config.Bind("Joker", "Accuracy", PerMember, "How often the audience joker is right: 0 = each audience member's own reliability (50-100 %, the game's design), 1-99 = that percentage, 100 = always (the game as shipped).");
        _current = this;
        Context.Provide(this);
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        var t = typeof(ViewAudienceJoker);
        ui.Patch(new PatchSpec("mods.joker.answer", "The chosen audience member answers with their reliability.",
            () => AccessTools.Method(t, "AudienceConfirmAnswer"), none)
        {
            Prefix = AccessTools.Method(typeof(JokerFeature), nameof(ConfirmPrefix)),
        });
        ui.Patch(new PatchSpec("mods.joker.picker", "No endless wait for a picker without a usable controller.",
            () => AccessTools.Method(t, "WaitForPlayerInput"), none)
        {
            Postfix = AccessTools.Method(typeof(JokerFeature), nameof(PickerPostfix)),
        });
    }

    protected override void OnStop() => _current = null;

    private static bool ConfirmPrefix(ViewAudienceJoker __instance, ref IEnumerator __result)
    {
        var self = _current;
        if (self == null || self._accuracy.Value >= AlwaysRight) return true;
        __result = self.Confirm(__instance);
        return false;
    }

    /// <summary>The game's AudienceConfirmAnswer with a working "wrong answer" branch.</summary>
    private IEnumerator Confirm(ViewAudienceJoker joker)
    {
        var t = typeof(ViewAudienceJoker);
        T F<T>(string name) => (T)AccessTools.Field(t, name).GetValue(joker);
        UIManager.HideUiElement("AudienceJoker", "GameUI");
        var questionUi = F<ViewQuestionUI>("viewQuestionUi");
        var activePlayer = F<APlayerAgent>("activePlayer");
        var members = F<int[]>("selectedAudienceMembers");
        var index = F<int>("audienceIndex");
        var probabilities = F<double[]>("audienceAnswerProbability");
        var reliability = _accuracy.Value > 0 ? _accuracy.Value / 100.0 : probabilities[Mathf.Clamp(index, 0, probabilities.Length - 1)];
        var correct = questionUi.GetCorrectAnswerIndex();
        var selected = JokerOdds.Answer(correct, reliability, Random.NextDouble(), Random.Next(2));
        Log.Info("Audience joker: member reliability " + (int)(reliability * 100) + " %, answers " + "ABC"[selected] + (selected == correct ? " (right)" : " (wrong)"));
        void Animate(AnimationStateAudience state, int member) => AccessTools.Method(t, "SetSpecialAudienceAnimationState", new[] { typeof(AnimationStateAudience), typeof(int) }).Invoke(joker, new object[] { state, member });
        foreach (var member in members)
        {
            if (member != members[index]) Animate(AnimationStateAudience.Idle, member);
        }

        var extension = NPCCategoryWeight.GetAnswerExtentionByIndex(selected);
        yield return SSoundManager.Instance.PlayVoiceFileAndWait(EVoiceType.Pflaume, string.Empty, "askaudience", "part5");
        questionUi.SetControlIconVisibility(activePlayer, show: false);
        UIManager.ShowUiElement("QuestionUI", "GameUI");
        var teamA = activePlayer.IsMemberOfTeamA();
        var gender = SStageController.Instance.GetSpecialAudienceGender(teamA, members[index]);
        var memberPath = (gender != Gender.Male ? "f" : "m") + (teamA ? "1" : "2");
        var genderPath = gender != Gender.Male ? "female" : "male";
        Animate(AnimationStateAudience.StandUpTalk, members[index]);
        yield return SSoundManager.Instance.PlayVoiceFileAndWait(EVoiceType.Guest, string.Empty, "askaudience", "part8", genderPath, memberPath);
        yield return SSoundManager.Instance.PlayVoiceFileAndWait(EVoiceType.Guest, string.Empty, "askaudience", "answer", genderPath, memberPath, extension);
        yield return SSoundManager.Instance.PlayVoiceFileAndWait(EVoiceType.Guest, string.Empty, "askaudience", "part10", genderPath, memberPath);
        Animate(AnimationStateAudience.StandUpIdle, members[index]);
        yield return new WaitForSeconds(0.5f);
        SStageController.Instance.DeselectSpecialAudience();
        SStageController.Instance.EndAudienceJokerState(teamA);
        var captain = F<RepresentationNPC>("activeTeamCaptain");
        if (captain != null) captain.ContinueMainMachine();
        activePlayer.ParentTeam.ContinueStatemachineForAllPlayers();
        yield return new WaitForSeconds(1f);
        questionUi.SendAnswer(selected, isAudience: true);
        yield return new WaitForEndOfFrame();
        Animate(AnimationStateAudience.Idle, members[index]);
    }

    private static void PickerPostfix(ViewAudienceJoker __instance, ref IEnumerator __result)
    {
        if (_current != null) __result = _current.Guard(__instance, __result);
    }

    /// <summary>Runs the player's picker; if nobody can control it for 5 s, picks like the game's AI.</summary>
    private IEnumerator Guard(ViewAudienceJoker joker, IEnumerator original)
    {
        var player = AccessTools.Field(typeof(ViewAudienceJoker), "activePlayer").GetValue(joker) as APlayerAgent;
        var stuck = 0f;
        while (true)
        {
            var usable = player?.Input is InputPlayer { Device: not null };
            var paused = UIManager.Instance != null && UIManager.Instance.gamePaused;
            stuck = usable || paused ? 0f : stuck + Time.unscaledDeltaTime;
            if (stuck > 5f)
            {
                Log.Warn("Audience joker: no controller for the picker, the member is picked automatically");
                yield return (IEnumerator)AccessTools.Method(typeof(ViewAudienceJoker), "WaitForNPCInput").Invoke(joker, null);
                yield break;
            }

            bool more;
            try
            {
                more = original.MoveNext();
            }
            catch (Exception ex)
            {
                Log.Error("Audience joker picker failed", ex);
                yield break;
            }

            if (!more) yield break;
            yield return original.Current;
        }
    }
}
