using System.Collections.Generic;
using WWDS.ModCore.Model;

namespace WWDS.Game.Wwds;

/// <summary>Translates game objects into the normalized harness model.</summary>
internal static class WwdsMapping
{
    public static QuestionInfo Question(GameId game, RuntimeSession session, int index)
    {
        var data = session.CurrentShow.dataArray[index];
        var kind = data.EQUESTIONTYPE switch
        {
            EQuestionType.Master => QuestionKind.Master,
            EQuestionType.Bonus => QuestionKind.Bonus,
            _ => QuestionKind.Normal,
        };

        var answers = new List<AnswerOption>();
        double? numeric = null;
        if (kind == QuestionKind.Bonus)
        {
            numeric = data.Antwort;
        }
        else
        {
            var correct = (int)data.Antwort;
            answers.Add(new AnswerOption(0, CleanText(data.A), correct == 1));
            answers.Add(new AnswerOption(1, CleanText(data.B), correct == 2));
            answers.Add(new AnswerOption(2, CleanText(data.C), correct == 3));
        }

        var q = new QuestionInfo(game, session.ActiveShowIndex, index, kind, CleanText(data.Kategoriename), CleanText(data.Frage), answers, numeric);
        q.Metadata["internalCategory"] = data.EINTERNALCATEGORIES.ToString();
        if (!string.IsNullOrEmpty(data.Antwortvideofile)) q.Metadata["video"] = data.Antwortvideofile;
        return q;
    }

    /// <summary>The sheets use a literal backslash-n for line breaks.</summary>
    public static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text!.Replace("\\n", " ").Replace("\n", " ").Replace("  ", " ").Trim();
    }

    public static TeamSlot? Slot(RuntimeSession? session, Team? team)
    {
        if (session == null || team == null) return null;
        if (ReferenceEquals(team, session.TeamA)) return TeamSlot.A;
        if (ReferenceEquals(team, session.TeamB)) return TeamSlot.B;
        return null;
    }

    public static Team? TeamOf(RuntimeSession? session, TeamSlot slot) => session == null ? null : slot == TeamSlot.A ? session.TeamA : session.TeamB;

    public static ContestantInfo Contestant(APlayerAgent agent, TeamSlot slot, int index, bool externallyControlled)
    {
        var isBot = agent is PlayerAgentBot;
        return new ContestantInfo(slot.ToString() + index, agent.Name, slot, isBot ? ContestantKind.Bot : ContestantKind.Human, isBot && externallyControlled);
    }

    public static ContestantInfo? Contestant(RuntimeSession? session, APlayerAgent? agent, bool[] externalControl)
    {
        if (session == null || agent == null) return null;
        var slot = Slot(session, agent.ParentTeam);
        if (slot == null) return null;
        var index = agent.ParentTeam.PlayerAgents.IndexOf(agent);
        return Contestant(agent, slot.Value, index < 0 ? 0 : index, externalControl[(int)slot.Value]);
    }

    public static TeamInfo Team(RuntimeSession session, TeamSlot slot, string displayName, bool externallyControlled)
    {
        var team = TeamOf(session, slot)!;
        var members = new List<ContestantInfo>();
        for (var i = 0; i < team.PlayerAgents.Count; i++) members.Add(Contestant(team.PlayerAgents[i], slot, i, externallyControlled));
        return new TeamInfo(slot, displayName, team.GetScore(), members);
    }

    public static GamePhase PhaseForState(string state) => state switch
    {
        "StateInit" or "StateLogoMovies" or "StateTeaser" => GamePhase.Boot,
        "StateIntro" or "StateScreenMainMenu" or "StateScreenArchiveMenu" or "StateScreenTechMenu" or "StateScreenMakeUpMenu" or "StateMachineMainMenu" => GamePhase.MainMenu,
        "StateScreenSelectPlayers" or "StateMachineCreateSession" or "StateCreateDebugSession" => GamePhase.PlayerSelection,
        "StateLoadShowContentScene" or "StateUnloadMainMenu" or "StateShowUnload" or "StateLoadMainMenu" or "StateDestroy" => GamePhase.Loading,
        "StateShowIntro" => GamePhase.ShowIntro,
        "StateShowTutorial" => GamePhase.Tutorial,
        "StateShowLoop" or "StateMachineGame" => GamePhase.CategorySelection,
        "StateShowOutro" => GamePhase.Outro,
        _ => GamePhase.Unknown,
    };

    /// <summary>Decorative behaviours on parallel layers; they never represent the main flow.</summary>
    public static bool IsHelperState(string state) =>
        state == "StateLightshow" || state == "StateCameraFade" || state == "StateTriggerTimeline" || state == "StateAnimVariationBlend";
}
