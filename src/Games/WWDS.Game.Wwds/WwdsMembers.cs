using System.Collections;
using System.Reflection;
using InControl;
using WWDS.Runtime.Reflection;

namespace WWDS.Game.Wwds;

/// <summary>
/// Private game members the adapter touches, resolved once. A null entry withholds the capability
/// that needs it (see <see cref="WwdsAdapter"/>.ComputeCapabilities).
/// </summary>
internal sealed class WwdsMembers
{
    public readonly MemberBinder Binder = new();

    // ShowController
    public readonly FieldInfo? ShowViewQuestionUi;
    public readonly FieldInfo? ShowViewCategoryUi;
    public readonly FieldInfo? ShowViewBonusUi;
    public readonly FieldInfo? ShowActivePlayer;
    public readonly MethodInfo? ShowWaitForCategorySelection;
    public readonly MethodInfo? ShowWaitForPlayerBets;
    public readonly MethodInfo? ShowWaitForPlayerEstimations;

    // ViewQuestionUI
    public readonly FieldInfo? QuestionActivePlayer;
    public readonly FieldInfo? QuestionSelectedButton;
    public readonly FieldInfo? QuestionAnswerButtons;
    public readonly MethodInfo? QuestionConfirmMaster;
    public readonly MethodInfo? QuestionNpcSequence;
    public readonly MethodInfo? QuestionNpcMaster;

    // ViewMasterBetAnswerFrame
    public readonly MethodInfo? BetNpc;
    public readonly MethodInfo? BetUpdateButtonTexts;
    public readonly FieldInfo? BetAnswerButtons;
    public readonly FieldInfo? BetValues;
    public readonly MethodInfo? BetConfirm;
    public readonly MethodInfo? BetRandomIndex;

    // ViewBonusQuestionUI
    public readonly MethodInfo? BonusNpc;
    public readonly FieldInfo? BonusTeamABet;
    public readonly FieldInfo? BonusTeamBBet;
    public readonly FieldInfo? BonusTeamAVoted;
    public readonly FieldInfo? BonusTeamBVoted;
    public readonly MethodInfo? BonusConfirm;
    public readonly MethodInfo? BonusSetTeamBetText;
    public readonly MethodInfo? BonusLockBet;

    // ViewCategoryUI
    public readonly MethodInfo? CategoryAuto;
    public readonly FieldInfo? CategorySelectedIndex;
    public readonly FieldInfo? CategoryNavigation;
    public readonly FieldInfo? CategoryButtons;
    public readonly FieldInfo? CategoryHasNew;
    public readonly MethodInfo? CategoryShowBig;
    public readonly MethodInfo? CategoryIndexSetter;

    // ViewPlayerFrame
    public readonly FieldInfo? FrameTeamNameText;

    public WwdsMembers()
    {
        var b = Binder;
        ShowViewQuestionUi = b.Field(typeof(ShowController), "viewQuestionUI");
        ShowViewCategoryUi = b.Field(typeof(ShowController), "viewCategoryUI");
        ShowViewBonusUi = b.Field(typeof(ShowController), "viewBonusQuestionUI");
        ShowActivePlayer = b.Field(typeof(ShowController), "activePlayer");
        ShowWaitForCategorySelection = b.Method(typeof(ShowController), "WaitForCategorySelection");
        ShowWaitForPlayerBets = b.Method(typeof(ShowController), "WaitForPlayerBets");
        ShowWaitForPlayerEstimations = b.Method(typeof(ShowController), "WaitForPlayerEstiamations");

        QuestionActivePlayer = b.Field(typeof(ViewQuestionUI), "activePlayer");
        QuestionSelectedButton = b.Field(typeof(ViewQuestionUI), "selectedAnswerButton");
        QuestionAnswerButtons = b.Field(typeof(ViewQuestionUI), "answerButtons");
        QuestionConfirmMaster = b.Method(typeof(ViewQuestionUI), "ConfirmAnswerButton", new[] { typeof(Team), typeof(int) });
        QuestionNpcSequence = b.Method(typeof(ViewQuestionUI), "NPCSelectQuestionSequence");
        QuestionNpcMaster = b.Method(typeof(ViewQuestionUI), "OnWaitForNPCAnswer", new[] { typeof(APlayerAgent) });

        BetNpc = b.Method(typeof(ViewMasterBetAnswerFrame), "OnWaitForNPCAnswer", new[] { typeof(APlayerAgent), typeof(Team) });
        BetUpdateButtonTexts = b.Method(typeof(ViewMasterBetAnswerFrame), "UpdateButtonTexts", new[] { typeof(Team) });
        BetAnswerButtons = b.Field(typeof(ViewMasterBetAnswerFrame), "answerButtons");
        BetValues = b.Field(typeof(ViewMasterBetAnswerFrame), "betValues");
        BetConfirm = b.Method(typeof(ViewMasterBetAnswerFrame), "ConfirmAnswerButton", new[] { typeof(Team), typeof(int) });
        BetRandomIndex = b.Method(typeof(ViewMasterBetAnswerFrame), "GetRandomScoreIndex", new[] { typeof(int), typeof(int) });

        BonusNpc = b.Method(typeof(ViewBonusQuestionUI), "OnWaitForNPCAnswer", new[] { typeof(Team) });
        BonusTeamABet = b.Field(typeof(ViewBonusQuestionUI), "teamABet");
        BonusTeamBBet = b.Field(typeof(ViewBonusQuestionUI), "teamBBet");
        BonusTeamAVoted = b.Field(typeof(ViewBonusQuestionUI), "teamAVoted");
        BonusTeamBVoted = b.Field(typeof(ViewBonusQuestionUI), "teamBVoted");
        BonusConfirm = b.Method(typeof(ViewBonusQuestionUI), "ConfirmAnswerButton", new System.Type[0]);
        BonusSetTeamBetText = b.Method(typeof(ViewBonusQuestionUI), "SetTeamBetText", new[] { typeof(int), typeof(bool) });
        BonusLockBet = b.Method(typeof(ViewBonusQuestionUI), "LockBet", new[] { typeof(bool) });

        CategoryAuto = b.Method(typeof(ViewCategoryUI), "ProcessAutoSelection", new[] { typeof(bool), typeof(bool) });
        CategorySelectedIndex = b.Field(typeof(ViewCategoryUI), "selectedCategroyIndex");
        CategoryNavigation = b.Field(typeof(ViewCategoryUI), "navigationController");
        CategoryButtons = b.Field(typeof(ViewCategoryUI), "categoryButtons");
        CategoryHasNew = b.Field(typeof(ViewCategoryUI), "hasNewCategory");
        CategoryShowBig = b.Method(typeof(ViewCategoryUI), "ShowCategoryBig", new[] { typeof(bool), typeof(bool), typeof(bool) });
        CategoryIndexSetter = b.PropertySetter(typeof(ViewCategoryUI), "CategoryIndex");

        FrameTeamNameText = b.Field(typeof(ViewPlayerFrame), "teamNameText");
    }

    public ViewQuestionUI? QuestionUi(ShowController? c) => c == null ? null : ShowViewQuestionUi?.GetValue(c) as ViewQuestionUI;

    public ViewCategoryUI? CategoryUi(ShowController? c) => c == null ? null : ShowViewCategoryUi?.GetValue(c) as ViewCategoryUI;

    public APlayerAgent? ActivePlayerOf(ViewQuestionUI ui) => QuestionActivePlayer?.GetValue(ui) as APlayerAgent;

    public IEnumerator Invoke(MethodInfo method, object target, params object[] args) => (IEnumerator)method.Invoke(target, args);
}
