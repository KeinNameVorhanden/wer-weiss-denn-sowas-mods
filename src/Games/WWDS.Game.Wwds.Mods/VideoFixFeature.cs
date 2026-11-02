using System.Collections;
using DoozyUI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Reliable answer videos. The game starts a video without waiting for its first picture, so the sound
/// can run ahead of a black screen; a video that never starts left the show waiting until a restart.
/// Videos now wait for their first frame and drop late frames instead of falling behind; a video that
/// has not started after a few seconds is restarted once and then skipped, and a decoder error skips it
/// right away.
/// </summary>
public sealed class VideoFixFeature : FeatureBase
{
    public const string FeatureId = "VideoFix";

    private static VideoFixFeature? _current;
    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<float> _timeout = null!;
    private bool _error;

    public override string Id => FeatureId;

    public override string DisplayName => "Reliable videos";

    public override string Description => "Answer videos wait for their first picture; a stuck video is restarted once, then skipped.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _enabled = Context.Config.Bind("Video", "Watchdog", true, "Answer videos wait for their first picture; a video that does not start is restarted once and then skipped.");
        _timeout = Context.Config.Bind("Video", "StartTimeoutSeconds", 6f, "Seconds a video may take to start before it is restarted (and the same again before it is skipped).");
        _current = this;
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        ui.Patch(new PatchSpec("mods.video.init", "Wait for the first frame, drop late frames, report decoder errors.",
            () => AccessTools.Method(typeof(VideoPlayerPC), nameof(VideoPlayerPC.Init)), none)
        {
            Postfix = AccessTools.Method(typeof(VideoFixFeature), nameof(InitPostfix)),
        });
        ui.Patch(new PatchSpec("mods.video.watchdog", "Restart, then skip a video that does not start.",
            () => AccessTools.Method(typeof(SVideoController), nameof(SVideoController.PlayAndWait)), none)
        {
            Postfix = AccessTools.Method(typeof(VideoFixFeature), nameof(PlayAndWaitPostfix)),
        });
    }

    protected override void OnStop() => _current = null;

    private static void InitPostfix(VideoPlayer ___player)
    {
        var self = _current;
        if (self == null || !self._enabled.Value || ___player == null) return;
        ___player.waitForFirstFrame = true;
        ___player.skipOnDrop = true;
        ___player.errorReceived += (_, message) =>
        {
            self._error = true;
            self.Log.Warn("Video error: " + message);
        };
    }

    private static void PlayAndWaitPostfix(string path, ref IEnumerator __result)
    {
        var self = _current;
        if (self != null && self._enabled.Value) __result = self.Watch(__result, path);
    }

    private IEnumerator Watch(IEnumerator original, string path)
    {
        _error = false;
        var waited = 0f;
        var restarted = false;
        var started = false;
        while (original.MoveNext())
        {
            if (!started)
            {
                var player = Player();
                started = player != null && player.isPlaying && player.frame > 0;
                var paused = UIManager.Instance != null && UIManager.Instance.gamePaused;
                if (!started && !paused) waited += Time.unscaledDeltaTime;
                if (_error)
                {
                    Log.Warn("Video " + path + " skipped after a decoder error");
                    SVideoController.Instance.Stop();
                }
                else if (!started && waited > _timeout.Value)
                {
                    if (!restarted && player != null)
                    {
                        restarted = true;
                        waited = 0f;
                        Log.Warn("Video " + path + " did not start, restarting it");
                        player.Stop();
                        player.Play();
                    }
                    else
                    {
                        Log.Warn("Video " + path + " did not start, skipped");
                        SVideoController.Instance.Stop();
                    }
                }
            }

            yield return original.Current;
        }
    }

    private static VideoPlayer? Player()
    {
        var controller = SVideoController.Instance;
        var pc = controller == null ? null : AccessTools.Field(typeof(SVideoController), "videoPlayer").GetValue(controller);
        return pc == null ? null : AccessTools.Field(typeof(VideoPlayerPC), "player").GetValue(pc) as VideoPlayer;
    }
}
