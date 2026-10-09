using WWDS.Bridge.Auth;
using WWDS.Bridge.Twitch;
using WWDS.Protocol;

namespace WWDS.Bridge;

/// <summary>
/// Everything the bridge does, independent of console or background mode: game server, Twitch service
/// lifecycle, login/logout requested from the game, settings, predictions, mock audience.
/// </summary>
public sealed class BridgeHost : IAsyncDisposable
{
    private readonly BridgePaths _paths;
    private readonly TokenStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Action<string> _log;
    private readonly bool _mockOnly;
    private readonly SemaphoreSlim _twitchLock = new(1, 1);
    private CancellationTokenSource? _loginCts;
    private readonly string _builtInClientId;
    private TwitchService? _twitch;

    /// <param name="builtInClientId">Client ID baked into this build (null = the build's, see BuiltInTwitch).</param>
    public BridgeHost(BridgePaths paths, bool mockOnly, Action<string> log, string? builtInClientId = null)
    {
        _paths = paths;
        _builtInClientId = builtInClientId ?? BuiltInTwitch.ClientId;
        _mockOnly = mockOnly;
        _log = log;
        Config = BridgeConfig.Load(paths);
        _store = new TokenStore(paths.TokenFile);
        Server = new GameServer(Config.Port, paths.EnsureKey(), log);
        Server.FromGame += (m, game) => _ = HandleFromGame(m);
        Server.GameConnected += game => Server.Broadcast(Settings());
    }

    public BridgeConfig Config { get; }

    /// <summary>bridge.json's Client ID, else the one baked into the release build.</summary>
    public string ClientId => Config.ClientId.Length > 0 ? Config.ClientId : _builtInClientId;

    public bool ClientIdBuiltIn => Config.ClientId.Length == 0 && _builtInClientId.Length > 0;

    public GameServer Server { get; }

    public MockAudience Mock { get; } = new();

    public GameState? LastState { get; private set; }

    public TwitchService? Twitch => _twitch;

    /// <summary>Raised when a game asks the bridge to stop (it started the bridge and is exiting).</summary>
    public event Action? ShutdownRequested;

    public async Task StartAsync()
    {
        Server.Start();
        _log("WWDS.Bridge " + BridgeInfo.Version + " listening on 127.0.0.1:" + Server.Port + " (key file " + _paths.KeyFile + ")");
        if (_mockOnly)
        {
            Server.LastStatus = new TwitchStatus { State = "Mock", Detail = "mock audience only", DryRun = true };
            _log("Mock mode: no Twitch connection.");
            return;
        }

        await RestartTwitchAsync().ConfigureAwait(false);
    }

    public BridgeSettings Settings() => new()
    {
        ClientId = Config.ClientId,
        HasClientId = ClientId.Length > 0,
        ClientIdBuiltIn = ClientIdBuiltIn,
        DryRun = Config.DryRun,
        Login = _store.Load()?.Login ?? string.Empty,
    };

    /// <summary>(Re)connects to Twitch with the current settings and stored tokens.</summary>
    public async Task RestartTwitchAsync()
    {
        await _twitchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_twitch != null)
            {
                await _twitch.DisposeAsync().ConfigureAwait(false);
                _twitch = null;
            }

            if (ClientId.Length == 0)
            {
                SetStatus("Disconnected", "no Client ID: enter it in the mod menu (Twitch) or bridge.json");
                return;
            }

            if (_store.Load() == null)
            {
                SetStatus("Disconnected", "not logged in");
                return;
            }

            _twitch = new TwitchService(Config, _store, new TwitchAuth(_http, ClientId), Emit, _log);
            await _twitch.StartAsync().ConfigureAwait(false);
        }
        finally
        {
            _twitchLock.Release();
        }
    }

    private void Emit(Message m)
    {
        if (m is TwitchStatus status) Server.LastStatus = status;
        Server.Broadcast(m);
    }

    private void SetStatus(string state, string detail) => Emit(new TwitchStatus { State = state, Detail = detail, DryRun = Config.DryRun });

    private async Task HandleFromGame(Message message)
    {
        try
        {
            switch (message)
            {
                case SendChat chat:
                    if (_twitch != null) await _twitch.SendChatAsync(chat.Text).ConfigureAwait(false);
                    else _log("[chat from game, not connected] " + chat.Text);
                    break;
                case CreatePoll poll:
                    if (_twitch != null) await _twitch.CreatePollAsync(poll).ConfigureAwait(false);
                    else _log("[poll from game, not connected] " + poll.Title);
                    break;
                case CreatePrediction prediction:
                    if (_twitch != null) await _twitch.CreatePredictionAsync(prediction).ConfigureAwait(false);
                    else Emit(new PredictionUpdate { Title = prediction.Title, Status = "error", Detail = "not connected to Twitch" });
                    break;
                case ResolvePrediction resolve:
                    if (_twitch != null && resolve.LockOnly) await _twitch.LockPredictionAsync().ConfigureAwait(false);
                    else if (_twitch != null) await _twitch.ResolvePredictionAsync(resolve.WinningIndex).ConfigureAwait(false);
                    break;
                case GameState state:
                    LastState = state;
                    break;
                case LoginStart:
                    _ = LoginAsync();
                    break;
                case Logout:
                    await LogoutAsync().ConfigureAwait(false);
                    break;
                case BridgeSettings settings:
                    await ApplySettingsAsync(settings).ConfigureAwait(false);
                    break;
                case Shutdown:
                    _log("game requested shutdown");
                    ShutdownRequested?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log("handling " + message.Type + " failed: " + ex.Message);
        }
    }

    private async Task ApplySettingsAsync(BridgeSettings s)
    {
        var clientId = (s.ClientId ?? string.Empty).Trim();
        var before = ClientId;
        Config.ClientId = clientId;
        var changed = ClientId != before;
        Config.DryRun = s.DryRun;
        Config.Save(_paths);
        _log("settings updated (client id " + (clientId.Length > 0 ? "set" : "empty") + ", dry run " + s.DryRun + ")");
        if (changed && !_mockOnly) await RestartTwitchAsync().ConfigureAwait(false);
        Server.Broadcast(Settings());
    }

    /// <summary>Device code flow driven from the game: code shown in game, browser opened by the game.</summary>
    public async Task LoginAsync()
    {
        _loginCts?.Cancel();
        var cts = _loginCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        if (ClientId.Length == 0)
        {
            Server.Broadcast(new LoginResult { Ok = false, Error = "No Client ID. Register an app at dev.twitch.tv/console (Client Type: Public) and enter its Client ID first." });
            return;
        }

        try
        {
            var auth = new TwitchAuth(_http, ClientId);
            var code = await auth.StartDeviceFlowAsync(cts.Token).ConfigureAwait(false);
            Server.Broadcast(new LoginCode { UserCode = code.UserCode, VerificationUri = code.VerificationUri, ExpiresInSeconds = code.ExpiresIn });
            _log("login: waiting for authorization (code shown in game)");
            var tokens = await auth.PollDeviceFlowAsync(code, cts.Token).ConfigureAwait(false);
            _store.Save(tokens);
            _log("logged in as " + tokens.Login);
            Server.Broadcast(new LoginResult { Ok = true, Login = tokens.Login });
            Server.Broadcast(Settings());
            await RestartTwitchAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Server.Broadcast(new LoginResult { Ok = false, Error = "login cancelled or timed out" });
        }
        catch (Exception ex)
        {
            _log("login failed: " + ex.Message);
            Server.Broadcast(new LoginResult { Ok = false, Error = ex.Message });
        }
    }

    public async Task LogoutAsync()
    {
        _loginCts?.Cancel();
        _store.Delete();
        await RestartTwitchAsync().ConfigureAwait(false);
        Server.Broadcast(Settings());
        _log("logged out, tokens deleted");
    }

    public async ValueTask DisposeAsync()
    {
        _loginCts?.Cancel();
        if (_twitch != null) await _twitch.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}
