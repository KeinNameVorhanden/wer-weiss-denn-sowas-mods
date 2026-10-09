using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Model;

namespace WWDS.Game.Wwds;

/// <summary>
/// Replacement coroutines for the game's bot decision routines. Each one publishes a
/// <see cref="DecisionRequest"/>, waits until it is resolved or times out, then performs exactly the
/// UI calls the original routine would have made with the chosen value. Timeouts fall back to the
/// game's own bot rule where it is a pure function, otherwise to a random choice.
/// </summary>
internal static class WwdsDecisions
{
    private static List<DecisionOption> AnswerOptions(WwdsAdapter a)
    {
        var options = new List<DecisionOption>();
        var s = SDataAccess.ActiveSession;
        var q = s == null ? null : WwdsMapping.Question(a.Game, s, s.ActiveQuestionIndex);
        for (var i = 0; i < ShowFormat.OptionCount; i++)
        {
            var label = q != null && i < q.Answers.Count ? q.Answers[i].Text : AnswerOption.KeyOf(i);
            options.Add(new DecisionOption(i, AnswerOption.KeyOf(i), label));
        }

        return options;
    }

    private static int Chosen(DecisionRequest r, int fallback) =>
        r.Status == DecisionStatus.Resolved || r.Status == DecisionStatus.TimedOut ? (r.ChosenIndex >= 0 ? r.ChosenIndex : fallback) : fallback;

    public static IEnumerator NormalAnswer(WwdsAdapter a, ViewQuestionUI ui, APlayerAgent player, TeamSlot slot)
    {
        var fallback = UnityEngine.Random.Range(0, ShowFormat.OptionCount);
        var request = a.OpenDecision(DecisionKind.Answer, slot, player, AnswerOptions(a), a.AnswerTimeout.Value, fallback);
        yield return new WaitForSeconds(1f);
        while (request.IsPending) yield return null;

        var index = Chosen(request, fallback);
        var buttons = (ViewAnswerButton[])a.Members.QuestionAnswerButtons!.GetValue(ui);

        // ShowQuestionResult dereferences selectedAnswerButton (ChangeToWrongSprite), so it must be set.
        a.Members.QuestionSelectedButton!.SetValue(ui, buttons[index]);
        a.Log.Info("Team " + slot + " answers " + AnswerOption.KeyOf(index) + " (" + request.Status + ", " + request.ResolvedBy + ")");
        ui.SendAnswer(index);
    }

    public static IEnumerator MasterAnswer(WwdsAdapter a, ViewQuestionUI ui, APlayerAgent player, TeamSlot slot)
    {
        var fallback = UnityEngine.Random.Range(0, ShowFormat.OptionCount);
        var timeout = Math.Min(a.MasterTimeout.Value, (float)ShowFormat.MasterClockSeconds - 2f);
        var request = a.OpenDecision(DecisionKind.MasterAnswer, slot, player, AnswerOptions(a), timeout, fallback);
        while (request.IsPending) yield return null;

        var index = Chosen(request, fallback);
        a.Log.Info("Team " + slot + " master answer " + AnswerOption.KeyOf(index) + " (" + request.Status + ")");
        a.Members.QuestionConfirmMaster!.Invoke(ui, new object[] { player.ParentTeam, index });
    }

    public static IEnumerator Bet(WwdsAdapter a, ViewMasterBetAnswerFrame frame, APlayerAgent player, Team enemyTeam, TeamSlot slot)
    {
        var m = a.Members;
        var team = player.ParentTeam;

        // Same UI preparation as the original NPC routine.
        m.BetUpdateButtonTexts!.Invoke(frame, new object[] { team });
        frame.SetControlIconVisibility(player.Input as InputPlayer, false);
        foreach (var button in (ViewAnswerButton[])m.BetAnswerButtons!.GetValue(frame)) button.gameObject.SetActive(false);

        var values = (float[])m.BetValues!.GetValue(frame);
        var score = team.GetScore();
        var options = new List<DecisionOption>();
        for (var i = 0; i < values.Length; i++)
        {
            var amount = Math.Max((int)(score * values[i]), 1);
            options.Add(new DecisionOption(i, (i + 1).ToString(CultureInfo.InvariantCulture), ((int)Math.Round(values[i] * 100)).ToString(CultureInfo.InvariantCulture) + "% (" + amount + ")"));
        }

        var fallback = (int)m.BetRandomIndex!.Invoke(frame, new object[] { score, enemyTeam.GetScore() });
        var request = a.OpenDecision(DecisionKind.Bet, slot, player, options, a.BetTimeout.Value, fallback);
        yield return new WaitForSeconds(2f);
        while (request.IsPending) yield return null;

        var index = Chosen(request, fallback);
        a.Log.Info("Team " + slot + " bets option " + index + " (" + request.Status + ")");
        m.BetConfirm!.Invoke(frame, new object[] { team, index });
    }

    public static IEnumerator Estimate(WwdsAdapter a, ViewBonusQuestionUI ui, Team team, TeamSlot slot)
    {
        var m = a.Members;
        var session = SDataAccess.ActiveSession!;
        var answer = (int)session.GetActiveQuestionData().Antwort;
        var isTeamA = ReferenceEquals(session.TeamA, team);

        // Both teams already locked an estimate: this pass only confirms (same as the game's bot routine).
        if ((bool)m.BonusTeamAVoted!.GetValue(ui) && (bool)m.BonusTeamBVoted!.GetValue(ui))
        {
            yield return new WaitForSeconds(1f);
            m.BonusConfirm!.Invoke(ui, new object[0]);
            yield break;
        }

        var agent = team.PlayerAgents.Count > 0 ? team.PlayerAgents[0] : null;
        var request = a.OpenDecision(DecisionKind.Estimate, slot, agent, new List<DecisionOption>(), a.EstimateTimeout.Value, -1);
        request.EstimateMin = 0;
        request.EstimateMax = 1000000000;
        yield return new WaitForSeconds(2f);
        while (request.IsPending) yield return null;

        var value = request.Status == DecisionStatus.Resolved && request.ChosenNumber.HasValue
            ? (int)Math.Round(request.ChosenNumber.Value)
            : (int)UnityEngine.Random.Range(answer / 2f, answer * 1.5f);

        // Humans may not enter the other team's number; keep the same rule for audience estimates.
        var otherVoted = (bool)(isTeamA ? m.BonusTeamBVoted : m.BonusTeamAVoted)!.GetValue(ui);
        var otherBet = (int)(isTeamA ? m.BonusTeamBBet : m.BonusTeamABet)!.GetValue(ui);
        if (otherVoted && value == otherBet) value += 1;
        (isTeamA ? m.BonusTeamABet : m.BonusTeamBBet)!.SetValue(ui, value);
        a.Log.Info("Team " + slot + " estimates " + value + " (" + request.Status + ")");

        if ((bool)m.BonusTeamAVoted!.GetValue(ui) && (bool)m.BonusTeamBVoted!.GetValue(ui))
        {
            m.BonusConfirm!.Invoke(ui, new object[0]);
            yield break;
        }

        m.BonusSetTeamBetText!.Invoke(ui, new object[] { value, isTeamA });
        yield return m.BonusLockBet!.Invoke(ui, new object[] { isTeamA });
    }

    public static IEnumerator Category(WwdsAdapter a, ViewCategoryUI ui, APlayerAgent chooser, TeamSlot slot)
    {
        var m = a.Members;
        var options = a.GetOpenCategories();
        if (options.Count == 0) yield break;
        var fallback = UnityEngine.Random.Range(0, options.Count);
        var request = a.OpenDecision(DecisionKind.Category, slot, chooser, options, a.CategoryTimeout.Value, fallback);
        yield return new WaitForSeconds(1f);
        while (request.IsPending) yield return null;

        var option = Chosen(request, fallback);
        var categoryIndex = options[Math.Min(option, options.Count - 1)].Index;
        m.CategorySelectedIndex!.SetValue(ui, categoryIndex);
        var buttons = (HardwareButtonAddition[])m.CategoryButtons!.GetValue(ui);
        ((HardwareButtonNavigationController)m.CategoryNavigation!.GetValue(ui)).Select(buttons[categoryIndex]);
        a.Log.Info("Team " + slot + " picks category " + (categoryIndex + 1) + " (" + request.Status + ")");
        yield return new WaitForSeconds(1f);
        yield return m.CategoryShowBig!.Invoke(ui, new object[] { true, false, false });
        m.CategoryIndexSetter!.Invoke(ui, new object[] { categoryIndex });
        m.CategoryHasNew!.SetValue(ui, true);
    }
}
