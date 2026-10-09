using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;
using WWDS.Audience;
using WWDS.Audience.Bindings;
using WWDS.Audience.Guessing;
using WWDS.Audience.Naming;
using WWDS.Audience.Opponents;
using WWDS.Audience.Statistics;
using WWDS.Audience.Voting;
using WWDS.Features.Debug;
using WWDS.Game.Wwds;
using WWDS.Game.Wwds.Mods;
using WWDS.ModCore;
using WWDS.ModCore.Detection;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Threading;
using WWDS.Runtime;
using WWDS.Twitch;
using WWDS.UI;

namespace WWDS.Host.BepInEx;

/// <summary>
/// Entry point. Builds the <see cref="ModContext"/>, detects the game, attaches the adapter,
/// registers features and pumps them every frame. Any failure here leaves the game untouched.
/// </summary>
[BepInPlugin(Guid, "WWDS Harness", ModContext.HarnessVersion)]
public sealed class HarnessPlugin : BaseUnityPlugin, IHostControl
{
    public const string Guid = "wwds.harness";

    private static readonly List<IFeature> PendingFeatures = new();
    private static HarnessPlugin? _instance;

    private ModContext? _ctx;
    private ModLog? _log;

    public bool RunInBackground
    {
        get => Application.runInBackground;
        set => Application.runInBackground = value;
    }

    public string DataDirectory => Path.Combine(Paths.BepInExRootPath, "wwds");

    public void QuitGame() => Application.Quit();

    /// <summary>
    /// Adds a feature from another plugin (declare [BepInDependency(HarnessPlugin.Guid)] and call this in
    /// Awake). Starts it immediately if the harness already runs. See docs/extending.md.
    /// </summary>
    public static void AddFeature(IFeature feature)
    {
        var ctx = _instance?._ctx;
        if (ctx == null)
        {
            PendingFeatures.Add(feature);
            return;
        }

        ctx.Features.TryStart(ctx.Features.Register(feature), ctx, ctx.Capabilities);
    }

    private void Awake()
    {
        try
        {
            Boot();
        }
        catch (Exception ex)
        {
            Logger.LogError("[Core] Harness failed to start, game continues unmodified: " + ex);
            _ctx = null;
        }
    }

    private void Boot()
    {
        Directory.CreateDirectory(DataDirectory);
        var store = new BepInExConfigStore(Config);
        var debug = Config.Bind("General", "DebugMode", false, "Verbose logging and extra diagnostics.");
        _log = new ModLog(new BepInExLogSink(Logger), LogCategory.Core, () => debug.Value);

        var dataFolder = Path.GetFileName(Application.dataPath);
        var acs = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");
        var detection = GameDetector.Detect(Paths.ProcessName, dataFolder, Fingerprint.Sha256(acs), Application.unityVersion);
        var loader = "BepInEx " + typeof(BaseUnityPlugin).Assembly.GetName().Version;
        _log.Info("WWDS Harness " + ModContext.HarnessVersion + " on " + loader + ", Unity " + Application.unityVersion + ", CLR " + Environment.Version);
        if (detection.Compatibility == Compatibility.Verified) _log.Info(detection.Message);
        else _log.Warn(detection.Message);

        _ctx = new ModContext(_log, store, new UnityClock(), loader, detection) { Host = this };
        var runInBackground = Config.Bind("General", "RunInBackground", false, "Keep the game running while its window is unfocused (useful while streaming).");
        if (runInBackground.Value) RunInBackground = true;

        if (detection.Profile != null)
        {
            var adapter = new WwdsAdapter(detection.Profile);
            try
            {
                adapter.Initialize(_ctx);
                _ctx.AttachAdapter(adapter);
            }
            catch (Exception ex)
            {
                _log.For(LogCategory.GameAdapter).Error("Adapter failed to initialize; features needing the game stay off", ex);
            }
        }

        _ctx.Features.Register(new TwitchFeature());
        _ctx.Features.Register(new AudienceFeature());
        _ctx.Features.Register(new VotingFeature());
        _ctx.Features.Register(new NamingFeature());
        _ctx.Features.Register(new GuessingFeature());
        _ctx.Features.Register(new OpponentFeature());
        _ctx.Features.Register(new WWDS.Audience.Predictions.WhoWinsFeature());
        _ctx.Features.Register(new BindingsFeature());
        _ctx.Features.Register(new StatisticsFeature());
        _ctx.Features.Register(new OverlayFeature());
        _ctx.Features.Register(new DebugToolsFeature());
        _ctx.Features.Register(new GameSettingsFeature());
        _ctx.Features.Register(new CatalogueFeature());
        _ctx.Features.Register(new SkipIntroFeature());
        _ctx.Features.Register(new QuickplayFeature());
        _ctx.Features.Register(new MouseFeature());
        _ctx.Features.Register(new KeyboardFeature());
        _ctx.Features.Register(new VoteHudFeature());
        _ctx.Features.Register(new NotificationsFeature());
        _ctx.Features.Register(new EscMenuFeature());
        _ctx.Features.Register(new ModMenuFeature());
        _ctx.Features.Register(new FirstRunFeature());
        _ctx.Features.Register(new ModsSelfTestFeature());
        foreach (var feature in PendingFeatures)
        {
            try
            {
                _ctx.Features.Register(feature);
            }
            catch (Exception ex)
            {
                _log.Error("Feature " + feature.Id + " could not be registered", ex);
            }
        }

        PendingFeatures.Clear();
        _ctx.Features.StartAll(_ctx, _ctx.Capabilities);
        _instance = this;
    }

    private void Update()
    {
        var ctx = _ctx;
        if (ctx == null) return;
        ctx.MainThread.Drain();
        ctx.Scheduler.Tick();
        try
        {
            ctx.Adapter?.Tick();
        }
        catch (Exception ex)
        {
            _log?.For(LogCategory.GameAdapter).Error("Adapter tick failed", ex);
        }

        ctx.Features.TickAll();
    }

    private void OnGUI()
    {
        var ctx = _ctx;
        if (ctx == null || ctx.GuiLayers.Count == 0) return;
        foreach (var layer in ctx.GuiLayers.OrderBy(l => l.Order).ToList())
        {
            try
            {
                layer.Draw();
            }
            catch (Exception ex)
            {
                _log?.For(LogCategory.UI).Error("GUI layer failed", ex);
                ctx.GuiLayers.Remove(layer);
            }
        }
    }

    private void OnDestroy()
    {
        if (_ctx == null) return;
        _ctx.Features.StopAll();
        _ctx.Adapter?.Shutdown();
    }
}

internal static class Fingerprint
{
    public static string Sha256(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }
}
