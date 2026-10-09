using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Skips the startup logo videos through the game's own skip (ViewIntroVideos.EndPlayback, which still
/// lets the menu scene finish loading) and optionally the show intro through its fast-forward
/// (StateShowIntro.ToggleFastForward). The teaser state only loads data and is never skipped
/// </summary>
public sealed class SkipIntroFeature : FeatureBase
{
    public const string FeatureId = "SkipIntro";

    private static SkipIntroFeature? _current;
    private IConfigValue<bool> _logos = null!;
    private IConfigValue<bool> _showIntro = null!;
    private IConfigValue<bool> _noTutorialPrompt = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Skip intro";

    public override string Description => "Skip the startup logos and (optionally) the show intro and the tutorial question.";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<bool> SkipLogos => _logos;

    public IConfigValue<bool> SkipShowIntro => _showIntro;

    public IConfigValue<bool> NoTutorialPrompt => _noTutorialPrompt;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "SkipIntro";
        _logos = c.Bind(s, "SkipLogos", true, "Skip the logo videos at game start.");
        _showIntro = c.Bind(s, "SkipShowIntro", false, "Fast-forward the show intro (host welcome, intro video, audience distribution) like pressing A.");
        _noTutorialPrompt = c.Bind(s, "NoTutorialPrompt", false, "Mark the tutorial as played so the first show does not ask for it (still available from the archive).");
        _current = this;
        Context.Provide(this);
        var ui = ModUi.For(Context);
        var args = new[] { typeof(Animator), typeof(AnimatorStateInfo), typeof(int) };
        ui.Patch(new PatchSpec("mods.skip-logos", "End the logo videos right after they start (the game's own any-key skip).",
            () => AccessTools.Method(typeof(StateLogoMovies), "OnStateEnter", args), ModCore.Capabilities.GameCapability.None)
        {
            Postfix = AccessTools.Method(typeof(SkipIntroFeature), nameof(LogosPostfix)),
        });
        ui.Patch(new PatchSpec("mods.skip-show-intro", "Fast-forward the show intro like the A button.",
            () => AccessTools.Method(typeof(StateShowIntro), "OnStateEnter", args), ModCore.Capabilities.GameCapability.None)
        {
            Postfix = AccessTools.Method(typeof(SkipIntroFeature), nameof(ShowIntroPostfix)),
        });
        Track(ui.OnState(state =>
        {
            if (state == "StateScreenMainMenu" && _noTutorialPrompt.Value) MarkTutorialPlayed();
        }));
    }

    protected override void OnStop() => _current = null;

    private void MarkTutorialPlayed()
    {
        var store = SDataStore.Instance;
        if (store == null || store.SettingsData.PlayedTutorial1P) return;
        store.SettingsData.PlayedTutorial1P = true;
        store.SaveSettings();
        Log.Info("Tutorial marked as played (no tutorial question before the first show)");
    }

    private static void LogosPostfix()
    {
        PatchGuard.Run("mods.skip-logos", () =>
        {
            if (_current == null || !_current._logos.Value) return;
            var view = UnityEngine.Object.FindObjectOfType<ViewIntroVideos>();
            var end = AccessTools.Method(typeof(ViewIntroVideos), "EndPlayback");
            if (view == null || end == null) return;
            end.Invoke(view, null);
            _current.Log.Info("Logo videos skipped");
        });
    }

    private static void ShowIntroPostfix(StateShowIntro __instance)
    {
        PatchGuard.Run("mods.skip-show-intro", () =>
        {
            var self = _current;
            if (self == null || !self._showIntro.Value) return;
            // One frame later: the state's own enter logic (intro coroutine, UI) must be fully set up.
            self.Context.MainThread.Post(() => PatchGuard.Run("mods.skip-show-intro.run", () => FastForward(self, __instance)));
        });
    }

    private static void FastForward(SkipIntroFeature self, StateShowIntro state)
    {
        if (self.Context.Get<ModUi>()?.State != "StateShowIntro") return;
        var toggle = AccessTools.Method(typeof(StateShowIntro), "ToggleFastForward");
        if (toggle == null) return;
        toggle.Invoke(state, new object?[] { null });
        // The game's own skip leaves the intro video and the applause running.
        if (SVideoController.Instance != null) SVideoController.Instance.Stop();
        if (SStageController.Instance != null) SStageController.Instance.StopApplause();
        self.Log.Info("Show intro fast-forwarded");
    }
}
