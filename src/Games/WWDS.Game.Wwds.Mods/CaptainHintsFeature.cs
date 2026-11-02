using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Fair captains. The question-specific remarks of Elton and Hoecker were recorded knowing the answer, so
/// "nonsense" usually means right; players learn to listen instead of thinking. With hints off the
/// captains comment with their general lines in a random mood (still lively, no information). The game's
/// AI could also praise two answers and then take the second: it now settles on one praised answer, so
/// what it says and what it picks match. The AI's accuracy is unchanged.
/// </summary>
public sealed class CaptainHintsFeature : FeatureBase
{
    public const string FeatureId = "CaptainHints";

    private static CaptainHintsFeature? _current;
    private static readonly System.Random Random = new();
    private IConfigValue<bool> _noHints = null!;
    private IConfigValue<bool> _aiConsistent = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Fair captains";

    public override string Description => "Captain remarks no longer give the answer away; the AI's spoken choice matches its pick.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _noHints = Context.Config.Bind("Captains", "NoAnswerHints", true, "Captains comment answers with general lines in a random mood instead of the recorded remarks that reveal the answer.");
        _aiConsistent = Context.Config.Bind("Captains", "AiSaysWhatItPicks", true, "The game's AI praises only the answer it takes.");
        _current = this;
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        var t = typeof(NPCCategoryWeight);
        ui.Patch(new PatchSpec("mods.captains.attitude", "No recorded answer remarks (they reveal the answer).",
            () => AccessTools.Method(t, nameof(NPCCategoryWeight.GetQuestionAttitude), new[] { typeof(int) }), none)
        {
            Prefix = AccessTools.Method(typeof(CaptainHintsFeature), nameof(AttitudePrefix)),
        });
        ui.Patch(new PatchSpec("mods.captains.random-mood", "General remarks in a random mood, independent of the answer.",
            () => AccessTools.Method(t, nameof(NPCCategoryWeight.GetRandomAttitude), new[] { typeof(int) }), none)
        {
            Prefix = AccessTools.Method(typeof(CaptainHintsFeature), nameof(RandomMoodPrefix)),
        });
        ui.Patch(new PatchSpec("mods.captains.ai-one-choice", "The AI settles on one praised answer.",
            () => AccessTools.Method(t, nameof(NPCCategoryWeight.GetNPCSelectionList)), none)
        {
            Postfix = AccessTools.Method(typeof(CaptainHintsFeature), nameof(SelectionPostfix)),
        });
    }

    protected override void OnStop() => _current = null;

    private static bool AttitudePrefix(ref EMood __result)
    {
        if (_current == null || !_current._noHints.Value) return true;
        __result = EMood.None;
        return false;
    }

    private static bool RandomMoodPrefix(ref string __result)
    {
        if (_current == null || !_current._noHints.Value) return true;
        var result = string.Empty;
        PatchGuard.Run("mods.captains.random-mood", () => result = NPCCategoryWeight.GetQuestionAttitude(Random.Next(2) == 0 ? EMood.Positive : EMood.Negative));
        if (result.Length == 0) return true;
        __result = result;
        return false;
    }

    /// <summary>Keeps one of the praised answers (chosen at random, like the game's last-in-random-order), the others become dismissed.</summary>
    internal static Dictionary<int, EMood> OnePositive(Dictionary<int, EMood> list, int pick)
    {
        var positives = list.Where(p => p.Value == EMood.Positive).Select(p => p.Key).ToList();
        if (positives.Count < 2) return list;
        var keep = positives[pick % positives.Count];
        foreach (var key in positives.Where(k => k != keep)) list[key] = EMood.Negative;
        return list;
    }

    private static void SelectionPostfix(Dictionary<int, EMood> __result)
    {
        if (_current == null || !_current._aiConsistent.Value || __result == null) return;
        PatchGuard.Run("mods.captains.ai-one-choice", () => OnePositive(__result, Random.Next(3)));
    }
}
