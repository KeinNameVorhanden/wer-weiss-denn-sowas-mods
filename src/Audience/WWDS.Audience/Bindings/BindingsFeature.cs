using System;
using System.Collections.Generic;
using System.IO;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Bindings;

/// <summary>
/// Data-driven "event + conditions -> action" rules from BepInEx/wwds/bindings.json. Rules react to any
/// published event (Twitch events, game events, interaction events); actions are registered by
/// features. "!wwds reload" style reloading via the "bindings" command.
/// </summary>
public sealed class BindingsFeature : FeatureBase
{
    public const string FeatureId = "Bindings";

    private BindingEngine _engine = null!;
    private string _path = string.Empty;

    public override string Id => FeatureId;

    public override string DisplayName => "Event bindings";

    public override string Description => "Rules mapping Twitch/game events to audience actions (bindings.json).";

    public override IEnumerable<string> Dependencies => new[] { AudienceFeature.FeatureId };

    protected override string LogCategoryName => LogCategory.Bindings;

    public IList<BindingRule> Rules => _engine.Rules;

    protected override void OnStart()
    {
        var actions = Context.Get<BindingActions>()!;
        _engine = new BindingEngine(actions);
        var dir = Context.Host?.DataDirectory;
        _path = dir == null ? string.Empty : Path.Combine(dir, "bindings.json");
        Reload();

        Track(actions.Register("chat.say", (args, _) =>
        {
            if (!args.TryGetValue("text", out var text) || text.Length == 0) return ActionResult.Fail("text missing");
            Context.Bus.Publish(new ChatSendRequested(text));
            return ActionResult.Success();
        }));
        Track(Context.Bus.SubscribeAll(OnEvent));
        Track(Context.Commands.Register(new CommandDefinition("bindings", Authority.Moderator, "Reload bindings.json", (_, _) =>
            new CommandResult(CommandOutcome.Handled, Reload()))));
    }

    public string Reload()
    {
        if (_path.Length == 0) return "no data directory";
        try
        {
            var file = BindingEngine.LoadOrCreate(_path);
            _engine.Load(file.Rules);
            var msg = file.Rules.Count + " binding rules loaded from " + _path;
            Log.Info(msg);
            return msg;
        }
        catch (Exception ex)
        {
            Log.Error("bindings.json invalid, rules unchanged", ex);
            return "bindings.json invalid: " + ex.Message;
        }
    }

    private void OnEvent(object evt)
    {
        if (evt is InteractionStatusChanged || evt is GameStateChanged) return;
        foreach (var fired in _engine.Handle(evt, Context.Clock.Now))
        {
            Log.Info("Rule " + fired.Key + " on " + evt.GetType().Name + ": " + fired.Value);
        }
    }
}
