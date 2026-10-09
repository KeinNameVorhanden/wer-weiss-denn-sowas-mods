using WWDS.Audience.Opponents;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.Twitch;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Turns Twitch and audience happenings the streamer would otherwise only find in the log into toasts:
/// connection up/down, the login code (until login finishes), login result, and who plays as the Twitch
/// opponent after a pick or an AFK change.
/// </summary>
public sealed class NotificationsFeature : FeatureBase
{
    public const string FeatureId = "Notifications";

    private ModUi _ui = null!;
    private AudienceLinkState? _link;
    private string _code = string.Empty;
    private string _loginMessage = string.Empty;
    private string _decider = string.Empty;

    public override string Id => FeatureId;

    public override string DisplayName => "Notifications";

    public override string Description => "Toasts for Twitch connection, login code and the current Twitch opponent.";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _ui = ModUi.For(Context);
        Track(Context.Bus.Subscribe<AudienceConnectionChanged>(OnLink));
    }

    private void OnLink(AudienceConnectionChanged e)
    {
        if (_link == e.State) return;
        var first = _link == null;
        _link = e.State;
        if (e.State == AudienceLinkState.Connected) _ui.Toast("Twitch verbunden (" + e.Transport + (e.Channel.Length > 0 ? ", " + e.Channel : string.Empty) + ")" + (e.DryRun ? " - Testmodus" : string.Empty), 5, "twitch-link");
        else if (!first && e.State == AudienceLinkState.Disconnected) _ui.Toast("Twitch getrennt" + (e.Detail.Length > 0 ? ": " + e.Detail : string.Empty), 6, "twitch-link");
    }

    public override void Tick()
    {
        _ui.Tick();
        var tw = Context.Get<TwitchFeature>();
        if (tw != null)
        {
            var code = tw.PendingLogin?.UserCode ?? string.Empty;
            if (code != _code)
            {
                _code = code;
                if (code.Length > 0) _ui.Toast("Twitch-Code " + code + " auf twitch.tv/activate eingeben", tw.PendingLogin!.ExpiresInSeconds > 0 ? tw.PendingLogin.ExpiresInSeconds : 600, "twitch-code");
                else _ui.DismissToast("twitch-code");
            }

            if (tw.LoginMessage != _loginMessage)
            {
                _loginMessage = tw.LoginMessage;
                if (_loginMessage.Length > 0 && code.Length == 0) _ui.Toast(_loginMessage, 6, "twitch-login");
            }
        }

        var op = Context.Get<OpponentFeature>();
        var decider = op?.Team != null && op.CurrentDecider != null ? op.CurrentDecider.DisplayName : string.Empty;
        if (decider != _decider)
        {
            _decider = decider;
            if (decider.Length > 0) _ui.Toast("@" + decider + " spielt jetzt für Twitch", 5, "decider");
        }
    }
}
