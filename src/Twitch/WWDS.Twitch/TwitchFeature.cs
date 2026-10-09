using System;
using System.Collections.Generic;
using WWDS.ModCore;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;
using WWDS.Protocol;
using WWDS.Twitch.Transports;

namespace WWDS.Twitch;

public enum TwitchMode
{
    Off,
    Bridge,
    Irc,
    BridgeWithIrcFallback,
    Mock,
}

/// <summary>
/// Owns the audience transport, turns transport messages into audience events on the main thread,
/// dispatches chat commands and sends chat replies (rate limited, dry-run aware). Knows nothing about
/// the game.
/// </summary>
public sealed class TwitchFeature : FeatureBase
{
    public const string FeatureId = "Twitch";

    private readonly Queue<string> _outbox = new();
    private readonly TwitchEventAdapter _mapper = new();
    private IConfigValue<TwitchMode> _mode = null!;
    private IConfigValue<string> _channel = null!;
    private IConfigValue<int> _port = null!;
    private IConfigValue<string> _prefix = null!;
    private IConfigValue<string> _trusted = null!;
    private IConfigValue<bool> _testMode = null!;
    private IConfigValue<float> _chatInterval = null!;
    private IConfigValue<bool> _announce = null!;
    private IAudienceTransport? _primary;
    private IAudienceTransport? _fallback;
    private GameActionGate _gate = null!;
    private double _primaryDownSince;
    private double _lastChatSent = double.NegativeInfinity;
    private double _lastStatePush;
    private InteractionStatusChanged _interaction = InteractionStatusChanged.Idle;

    public override string Id => FeatureId;

    public override string DisplayName => "Twitch";

    public override string Description => "Twitch chat and events through the companion bridge or anonymous IRC; optional mock audience.";

    protected override string LogCategoryName => LogCategory.Twitch;

    /// <summary>The in-process loopback when Mode = Mock (used by the audience simulator).</summary>
    public LoopbackTransport? Mock => _primary as LoopbackTransport;

    public IAudienceTransport? Active => _fallback != null && _fallback.State == AudienceLinkState.Connected ? _fallback : _primary;

    public string CommandPrefix => _prefix.Value;

    public TwitchMode Mode => _mode.Value;

    protected override void OnStart()
    {
        var c = Context.Config;
        const string s = "Twitch";
        _mode = c.Bind(s, "Mode", TwitchMode.Off, "Off | Bridge | Irc | BridgeWithIrcFallback | Mock. Bridge needs WWDS.Bridge running; Irc is anonymous read-only chat.");
        _channel = c.Bind(s, "Channel", "", "Channel login for the IRC fallback (the bridge uses its own configuration).");
        _port = c.Bind(s, "BridgePort", ProtocolInfo.DefaultPort, "Localhost port of WWDS.Bridge.");
        _prefix = c.Bind(s, "CommandPrefix", "!", "Prefix of chat commands.");
        _trusted = c.Bind(s, "TrustedUsers", "", "Comma separated logins treated as moderators by the harness.");
        _testMode = c.Bind(s, "TestMode", true, "Real Twitch test mode: commands are parsed and logged, but audience-driven game actions and chat replies are NOT applied. Mock mode is never gated.");
        _chatInterval = c.Bind(s, "ChatMinIntervalSeconds", 2f, "Minimum seconds between two chat messages sent by the harness.");
        _announce = c.Bind(s, "AnnounceInChat", true, "Post interaction results (vote winners, chosen names) in chat.");
        _mapper.SetTrustedUsers(ConfigList.Parse(_trusted.Value));
        _trusted.Changed += v => _mapper.SetTrustedUsers(ConfigList.Parse(v));

        _gate = Context.Get<GameActionGate>() ?? new GameActionGate(Context.Log.For(LogCategory.Audience));
        Context.Provide(_gate);
        Context.Provide(this);
        UpdateGate();
        _testMode.Changed += _ => UpdateGate();

        Track(Context.Bus.Subscribe<ChatSendRequested>(e => QueueChat(e.Text)));
        Track(Context.Bus.Subscribe<PollCreateRequested>(OnPollRequested));
        Track(Context.Bus.Subscribe<InteractionStatusChanged>(e => _interaction = e));

        _autoStartBridge = c.Bind(s, "AutoStartBridge", true, "Start the bundled WWDS.Bridge invisibly when Mode uses the bridge (needed for in-game login).");
        _bridgePath = c.Bind(s, "BridgePath", "", "Path to WWDS.Bridge.exe (empty = BepInEx/plugins/WWDS/bridge/WWDS.Bridge.exe).");
        _launcher = new BridgeLauncher(Log);
        Track(Context.Bus.Subscribe<PredictionCreateRequested>(OnPredictionRequested));
        Track(Context.Bus.Subscribe<PredictionResolveRequested>(e => SendToBridge(new ResolvePrediction { WinningIndex = e.WinningIndex }, "prediction resolve")));
        Track(Context.Bus.Subscribe<PredictionLockRequested>(_ => SendToBridge(new ResolvePrediction { LockOnly = true }, "prediction lock")));
        _mode.Changed += _ => Context.MainThread.Post(Restart);

        Restart();
    }

    private IConfigValue<bool> _autoStartBridge = null!;
    private IConfigValue<string> _bridgePath = null!;
    private BridgeLauncher _launcher = null!;
    private readonly List<Message> _pendingToBridge = new();

    private string BridgePath => _bridgePath.Value.Trim().Length > 0 ? _bridgePath.Value.Trim() : BridgeLauncher.DefaultPath();

    // ---- account state (for the mod menu) -----------------------------------------------------

    public AudienceLinkState LinkState { get; private set; } = AudienceLinkState.Disconnected;

    public string LinkDetail { get; private set; } = string.Empty;

    /// <summary>Logged-in Twitch account reported by the bridge ("" if none).</summary>
    public string AccountLogin { get; private set; } = string.Empty;

    public bool HasClientId { get; private set; }

    public string ClientId { get; private set; } = string.Empty;

    /// <summary>The bridge uses the Client ID baked into this release (none entered).</summary>
    public bool ClientIdBuiltIn { get; private set; }

    public bool BridgeDryRun { get; private set; } = true;

    /// <summary>Device code to show while a login is pending.</summary>
    public LoginCode? PendingLogin { get; private set; }

    public string LoginMessage { get; private set; } = string.Empty;

    public bool TestMode
    {
        get => _testMode.Value;
        set => _testMode.Value = value;
    }

    public void SetMode(TwitchMode mode) => _mode.Value = mode;

    /// <summary>Rebuilds the transport for the current mode (called on start and when Mode changes).</summary>
    public void Restart()
    {
        _fallback?.Dispose();
        _primary?.Dispose();
        _fallback = null;
        _primaryDownSince = 0;
        UpdateGate();
        _primary = CreatePrimary();
        if (_primary != null) Attach(_primary);
        Log.Info("Mode " + _mode.Value + (_gate.DryRun ? " (TEST MODE: game actions and chat replies are not applied)" : string.Empty));
    }

    /// <summary>In-game login: makes sure the bridge runs, then asks it for a device code.</summary>
    public void Login()
    {
        if (_mode.Value != TwitchMode.Bridge && _mode.Value != TwitchMode.BridgeWithIrcFallback) _mode.Value = TwitchMode.BridgeWithIrcFallback;
        else if (_autoStartBridge.Value) Log.Info(_launcher.EnsureRunning(BridgePath, _port.Value));
        LoginMessage = "Waiting for the bridge...";
        SendToBridge(new LoginStart(), "login");
    }

    public void Logout()
    {
        PendingLogin = null;
        SendToBridge(new Logout(), "logout");
    }

    /// <summary>Stores the Twitch app client id and the bridge dry-run switch (persisted by the bridge).</summary>
    public void SaveBridgeSettings(string clientId, bool dryRun)
    {
        ClientId = clientId.Trim();
        BridgeDryRun = dryRun;
        SendToBridge(new BridgeSettings { ClientId = ClientId, DryRun = dryRun }, "settings");
    }

    private void SendToBridge(Message message, string what)
    {
        var bridge = _primary as BridgeClientTransport;
        if (bridge != null && bridge.State == AudienceLinkState.Connected)
        {
            bridge.Send(message);
            return;
        }

        _pendingToBridge.Add(message);
        Log.Info(what + " queued until the bridge is connected");
    }

    private void FlushPending()
    {
        if (!(_primary is BridgeClientTransport bridge) || bridge.State != AudienceLinkState.Connected) return;
        foreach (var m in _pendingToBridge) bridge.Send(m);
        _pendingToBridge.Clear();
    }

    private void OnPredictionRequested(PredictionCreateRequested e)
    {
        if (_gate.DryRun)
        {
            Log.Info("Prediction: NOT CREATED (test mode): " + e.Title);
            return;
        }

        SendToBridge(new CreatePrediction { Title = e.Title, Outcomes = new List<string>(e.Outcomes), WindowSeconds = e.WindowSeconds }, "prediction");
    }

    /// <summary>Opens the Twitch activation page in the default browser (only twitch.tv URLs).</summary>
    private void OpenVerificationPage(string uri)
    {
        try
        {
            var u = new Uri(uri);
            if (u.Scheme != "https" || !(u.Host == "twitch.tv" || u.Host.EndsWith(".twitch.tv", StringComparison.Ordinal)))
            {
                Log.Warn("Not opening unexpected login URL host " + u.Host);
                return;
            }

            System.Diagnostics.Process.Start(u.AbsoluteUri);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the browser: " + ex.Message);
        }
    }

    /// <summary>True while the connected bridge serves a mock audience (no real Twitch): never gated.</summary>
    private bool _bridgeIsMock;

    private void UpdateGate()
    {
        _gate.DryRun = _testMode.Value && _mode.Value != TwitchMode.Mock && _mode.Value != TwitchMode.Off && !_bridgeIsMock;
        _gate.Reason = "Twitch test mode";
    }

    private IAudienceTransport? CreatePrimary()
    {
        switch (_mode.Value)
        {
            case TwitchMode.Bridge:
            case TwitchMode.BridgeWithIrcFallback:
                if (_autoStartBridge.Value) Log.Info(_launcher.EnsureRunning(BridgePath, _port.Value));
                return new BridgeClientTransport(_port.Value, () => new Hello
                {
                    Role = "game",
                    Version = ModContext.HarnessVersion,
                    Key = BridgeClientTransport.ReadKey(BridgeClientTransport.DefaultKeyPath()),
                    Game = Context.Detection.Game.ToString(),
                });
            case TwitchMode.Irc:
                if (_channel.Value.Trim().Length == 0)
                {
                    Log.Warn("Mode Irc needs [Twitch] Channel");
                    return null;
                }

                return new IrcFallbackTransport(_channel.Value);
            case TwitchMode.Mock:
                return new LoopbackTransport();
            default:
                return null;
        }
    }

    private void Attach(IAudienceTransport transport)
    {
        transport.Received += m => Context.MainThread.Post(() => Handle(m));
        transport.StateChanged += (state, detail) => Context.MainThread.Post(() =>
        {
            if (!ReferenceEquals(transport, _primary) && !ReferenceEquals(transport, _fallback)) return; // replaced by Restart
            Log.Info(transport.Name + ": " + state + " " + detail);
            LinkState = state;
            LinkDetail = transport.Name + ": " + detail;
            if (state == AudienceLinkState.Connected) FlushPending();
            Context.Bus.Publish(new AudienceConnectionChanged(state, transport.Name, _channel.Value, detail, _gate.DryRun));
        });
        transport.Start();
    }

    public override void Tick()
    {
        var now = Context.Clock.Now;
        if (_mode.Value == TwitchMode.BridgeWithIrcFallback && _primary != null)
        {
            if (_primary.State == AudienceLinkState.Connected)
            {
                _primaryDownSince = 0;
                if (_fallback != null)
                {
                    Log.Info("Bridge back, stopping IRC fallback");
                    _fallback.Dispose();
                    _fallback = null;
                }
            }
            else
            {
                if (_primaryDownSince == 0) _primaryDownSince = now;
                if (_fallback == null && now - _primaryDownSince > 10 && _channel.Value.Trim().Length > 0)
                {
                    Log.Info("Bridge unavailable, starting anonymous IRC fallback for #" + _channel.Value);
                    _fallback = new IrcFallbackTransport(_channel.Value);
                    Attach(_fallback);
                }
            }
        }

        FlushChat(now);
        if (now - _lastStatePush >= 1.0)
        {
            _lastStatePush = now;
            PushState();
        }
    }

    /// <summary>Main thread: map, publish, dispatch commands.</summary>
    internal void Handle(Message message)
    {
        switch (message)
        {
            case LoginCode code:
                PendingLogin = code;
                LoginMessage = "Enter " + code.UserCode + " at " + code.VerificationUri;
                OpenVerificationPage(code.VerificationUri);
                return;
            case LoginResult result:
                PendingLogin = null;
                LoginMessage = result.Ok ? "Logged in as " + result.Login : "Login failed: " + result.Error;
                if (result.Ok) AccountLogin = result.Login;
                Log.Info(LoginMessage);
                return;
            case BridgeSettings settings:
                AccountLogin = settings.Login;
                HasClientId = settings.HasClientId;
                ClientId = settings.ClientId;
                ClientIdBuiltIn = settings.ClientIdBuiltIn;
                BridgeDryRun = settings.DryRun;
                return;
            case PredictionUpdate p:
                var users = new List<KeyValuePair<string, int>>();
                var points = new List<int>();
                foreach (var o in p.Outcomes)
                {
                    users.Add(new KeyValuePair<string, int>(o.Title, o.Users));
                    points.Add(o.Points);
                }

                Context.Bus.Publish(new PredictionUpdated(p.PredictionId, p.Title, p.Status, p.Detail, users, points));
                return;
        }

        if (message is TwitchStatus status)
        {
            _bridgeIsMock = status.State == "Mock";
            UpdateGate();
            LinkDetail = "Twitch " + status.State + (status.Detail.Length > 0 ? ": " + status.Detail : string.Empty);
            var state = status.State switch
            {
                "Connected" or "Mock" => AudienceLinkState.Connected,
                "Error" => AudienceLinkState.Error,
                "Disconnected" => AudienceLinkState.Disconnected,
                _ => AudienceLinkState.Connecting,
            };
            Context.Bus.Publish(new AudienceConnectionChanged(state, "twitch", status.Channel, status.Detail, _gate.DryRun || status.DryRun));
            return;
        }

        var evt = _mapper.Map(message);
        if (evt == null) return;
        Context.Bus.Publish(evt);
        if (evt is ChatMessageReceived chat) DispatchCommand(chat);
    }

    private void DispatchCommand(ChatMessageReceived chat)
    {
        var parsed = CommandParser.Parse(chat.Text, _prefix.Value);
        if (parsed == null) return;
        var result = Context.Commands.Execute(parsed, chat.Viewer.AsInvoker());
        if (result.Outcome == CommandOutcome.Unknown) return;
        if (_gate.DryRun)
        {
            // Test mode trace: what arrived, how it parsed, what happened (game actions are logged by the gate).
            Log.Info("Command: " + _prefix.Value + parsed.Name + " " + parsed.RawArgs + " | Parsed: " + parsed.Name + "(viewer=" + chat.Viewer.DisplayName
                + ", authority=" + chat.Viewer.Authority + ", args=[" + string.Join(", ", new List<string>(parsed.Args).ToArray()) + "]) -> " + result.Outcome);
        }
        else
        {
            Log.Debug("!" + parsed.Name + " by " + chat.Viewer.DisplayName + ": " + result.Outcome);
        }
        Context.Bus.Publish(new ChatCommandReceived(chat.Viewer, parsed, result.Outcome));
        if (result.Outcome == CommandOutcome.Handled && !string.IsNullOrEmpty(result.Reply)) QueueChat(result.Reply!);
    }

    private void OnPollRequested(PollCreateRequested e)
    {
        var transport = Active;
        if (_gate.DryRun)
        {
            Log.Info("Poll: NOT CREATED (test mode): " + e.Title);
            return;
        }

        if (transport == null || !transport.CanSend || transport.Name == "irc")
        {
            Log.Info("Poll: NOT CREATED (needs the bridge): " + e.Title);
            return;
        }

        transport.Send(new CreatePoll { Title = e.Title, Choices = new List<string>(e.Choices), DurationSeconds = e.DurationSeconds });
        Log.Info("Poll requested: " + e.Title);
    }

    /// <summary>Injects a message as if it came from the transport (audience simulator, tests).</summary>
    public void InjectForTesting(Message message) => Handle(message);

    private void QueueChat(string text)
    {
        if (!_announce.Value || string.IsNullOrEmpty(text)) return;
        if (_outbox.Count > 20) _outbox.Dequeue();
        _outbox.Enqueue(text.Length > 450 ? text.Substring(0, 450) : text);
    }

    private void FlushChat(double now)
    {
        if (_outbox.Count == 0 || now - _lastChatSent < _chatInterval.Value) return;
        var text = _outbox.Dequeue();
        _lastChatSent = now;
        var transport = Active;
        if (_gate.DryRun)
        {
            Log.Info("Chat: NOT SENT (test mode): " + text);
        }
        else if (transport == null || !transport.CanSend)
        {
            Log.Info("Chat: NOT SENT (" + (transport == null ? "no transport" : transport.Name + " cannot send") + "): " + text);
        }
        else
        {
            transport.Send(new SendChat { Text = text });
            Log.Info("Chat: " + text);
        }
    }

    private void PushState()
    {
        var transport = Active;
        if (transport == null || transport.Name != "bridge" || transport.State != AudienceLinkState.Connected) return;
        var snap = Context.Adapter?.GetSnapshot() ?? GameSnapshot.Empty;
        var state = new GameState
        {
            Game = Context.Detection.Game.ToString(),
            Phase = snap.Phase.ToString(),
            Question = snap.Question?.Text ?? string.Empty,
            ScoreA = snap.TeamA?.Score ?? 0,
            ScoreB = snap.TeamB?.Score ?? 0,
            TeamA = snap.TeamA?.DisplayName ?? string.Empty,
            TeamB = snap.TeamB?.DisplayName ?? string.Empty,
            Interaction = _interaction.Title.Length > 0 ? _interaction.Title + " (" + _interaction.State + ")" : string.Empty,
            Tally = new List<string>(_interaction.Lines),
        };
        if (snap.Question != null)
        {
            foreach (var a in snap.Question.Answers) state.Answers.Add(a.Key + ": " + a.Text);
        }

        transport.Send(state);
    }

    protected override void OnStop()
    {
        // A bridge this game started also exits when the game process ends; ask it explicitly too.
        if (_launcher.StartedByUs && _primary is BridgeClientTransport bridge && bridge.State == AudienceLinkState.Connected) bridge.Send(new Shutdown());
        _fallback?.Dispose();
        _primary?.Dispose();
        _fallback = null;
        _primary = null;
    }
}
