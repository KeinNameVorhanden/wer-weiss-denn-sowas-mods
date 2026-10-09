using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Helix.Models.Channels.SendChatMessage;
using TwitchLib.Api.Helix.Models.Polls.CreatePoll;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs;
using WWDS.Bridge.Auth;
using WWDS.Protocol;

namespace WWDS.Bridge.Twitch;

/// <summary>
/// Connects to Twitch with the stored broadcaster token: validates/refreshes it, opens the EventSub
/// WebSocket, (re)creates subscriptions after every fresh session, forwards events as protocol
/// messages, and performs Helix calls requested by the game (chat, polls). Reconnects with backoff.
/// </summary>
public sealed class TwitchService : IAsyncDisposable
{
    private readonly BridgeConfig _config;
    private readonly TokenStore _store;
    private readonly TwitchAuth _auth;
    private readonly Action<Message> _emit;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private EventSubWebsocketClient? _eventSub;
    private TwitchAPI? _api;
    private TwitchTokens? _tokens;
    private DateTimeOffset _lastValidated;

    public TwitchService(BridgeConfig config, TokenStore store, TwitchAuth auth, Action<Message> emit, Action<string> log)
    {
        _config = config;
        _store = store;
        _auth = auth;
        _emit = emit;
        _log = log;
    }

    public TwitchStatus Status { get; private set; } = new() { State = "Disconnected" };

    public async Task StartAsync()
    {
        _tokens = _store.Load();
        if (_tokens == null)
        {
            SetStatus("Disconnected", "not logged in: run 'login'");
            return;
        }

        if (!await EnsureTokenAsync().ConfigureAwait(false)) return;

        _api = new TwitchAPI();
        _api.Settings.ClientId = _auth.ClientId;
        _api.Settings.AccessToken = _tokens.AccessToken;

        _eventSub = new EventSubWebsocketClient();
        _eventSub.WebsocketConnected += OnConnected;
        _eventSub.WebsocketDisconnected += OnDisconnected;
        _eventSub.WebsocketReconnected += (_, _) =>
        {
            SetStatus("Connected", "reconnected");
            return Task.CompletedTask;
        };
        _eventSub.ErrorOccurred += (_, e) =>
        {
            _log("EventSub error: " + e.Message);
            return Task.CompletedTask;
        };
        _eventSub.Revocation += (_, e) =>
        {
            _log("Subscription revoked: " + e.Payload?.Subscription?.Type + " (" + e.Payload?.Subscription?.Status + ")");
            return Task.CompletedTask;
        };
        _eventSub.ChannelChatMessage += (_, e) => Emit(TwitchMapping.Chat(e.Payload.Event));
        _eventSub.ChannelPointsCustomRewardRedemptionAdd += (_, e) => Emit(TwitchMapping.Redemption(e.Payload.Event));
        _eventSub.ChannelSubscribe += (_, e) => Emit(TwitchMapping.Sub(e.Payload.Event));
        _eventSub.ChannelSubscriptionGift += (_, e) => Emit(TwitchMapping.Gift(e.Payload.Event));
        _eventSub.ChannelSubscriptionMessage += (_, e) => Emit(TwitchMapping.Resub(e.Payload.Event));
        _eventSub.ChannelCheer += (_, e) => Emit(TwitchMapping.Cheer(e.Payload.Event));
        _eventSub.ChannelRaid += (_, e) => Emit(TwitchMapping.Raid(e.Payload.Event));
        _eventSub.ChannelFollow += (_, e) => Emit(TwitchMapping.Follow(e.Payload.Event));
        _eventSub.ChannelPollBegin += (_, e) => Emit(TwitchMapping.Poll(e.Payload.Event, "begin"));
        _eventSub.ChannelPollProgress += (_, e) => Emit(TwitchMapping.Poll(e.Payload.Event, "progress"));
        _eventSub.ChannelPollEnd += (_, e) => Emit(TwitchMapping.Poll(e.Payload.Event, "end"));
        _eventSub.ChannelUpdate += (_, e) => Emit(new ChannelUpdate { Title = e.Payload.Event.Title, CategoryName = e.Payload.Event.CategoryName });
        _eventSub.ChannelPredictionBegin += (_, e) => Emit(TwitchMapping.Prediction(e.Payload.Event, "begin"));
        _eventSub.ChannelPredictionProgress += (_, e) => Emit(TwitchMapping.Prediction(e.Payload.Event, "progress"));
        _eventSub.ChannelPredictionLock += (_, e) => Emit(TwitchMapping.Prediction(e.Payload.Event, "lock"));
        _eventSub.ChannelPredictionEnd += (_, e) => Emit(TwitchMapping.Prediction(e.Payload.Event, "end"));

        SetStatus("Connecting", "EventSub");
        if (!await _eventSub.ConnectAsync().ConfigureAwait(false))
        {
            SetStatus("Error", "EventSub connect failed, retrying");
            _ = RetryConnectAsync();
        }

        _ = ValidateLoopAsync(_cts.Token);
    }

    private Task Emit(Message message)
    {
        _emit(message);
        return Task.CompletedTask;
    }

    private async Task OnConnected(object? sender, WebsocketConnectedArgs e)
    {
        if (e.IsRequestedReconnect)
        {
            SetStatus("Connected", "session migrated");
            return;
        }

        var created = 0;
        foreach (var type in _config.Events)
        {
            var sub = TwitchMapping.Subscription(type, _tokens!.UserId, _tokens.UserId);
            if (sub == null)
            {
                _log("Unknown event type in config, skipped: " + type);
                continue;
            }

            try
            {
                await _api!.Helix.EventSub.CreateEventSubSubscriptionAsync(type, sub.Value.Version, sub.Value.Condition,
                    EventSubTransportMethod.Websocket, _eventSub!.SessionId, accessToken: _tokens.AccessToken).ConfigureAwait(false);
                created++;
            }
            catch (Exception ex)
            {
                // Typical: missing scope, or the channel is not affiliate (subs/bits).
                _log("Could not subscribe to " + type + ": " + ex.GetType().Name + " " + ex.Message);
            }
        }

        SetStatus("Connected", created + "/" + _config.Events.Count + " event subscriptions as " + _tokens!.Login);
    }

    private async Task OnDisconnected(object? sender, EventArgs e)
    {
        SetStatus("Disconnected", "EventSub connection lost, reconnecting");
        await RetryConnectAsync().ConfigureAwait(false);
    }

    private async Task RetryConnectAsync()
    {
        var delays = new[] { 1, 2, 5, 10, 30, 60 };
        for (var attempt = 0; !_cts.IsCancellationRequested; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(delays[Math.Min(attempt, delays.Length - 1)]), _cts.Token).ConfigureAwait(false);
            if (!await EnsureTokenAsync().ConfigureAwait(false)) return;
            _api!.Settings.AccessToken = _tokens!.AccessToken;
            try
            {
                if (await _eventSub!.ReconnectAsync().ConfigureAwait(false)) return;
            }
            catch (Exception ex)
            {
                _log("reconnect failed: " + ex.Message);
            }
        }
    }

    /// <summary>Validates hourly (Twitch requirement for long sessions) and refreshes when needed.</summary>
    private async Task ValidateLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow - _lastValidated > TimeSpan.FromMinutes(55) || _tokens!.ExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(10))
            {
                if (await EnsureTokenAsync().ConfigureAwait(false) && _api != null) _api.Settings.AccessToken = _tokens!.AccessToken;
            }
        }
    }

    private async Task<bool> EnsureTokenAsync()
    {
        try
        {
            var info = await _auth.ValidateAsync(_tokens!.AccessToken, _cts.Token).ConfigureAwait(false);
            if (info == null || info.Value.ExpiresIn < 600)
            {
                _tokens = await _auth.RefreshAsync(_tokens, _cts.Token).ConfigureAwait(false);
                _store.Save(_tokens);
                _log("Access token refreshed for " + _tokens.Login);
            }

            _lastValidated = DateTimeOffset.UtcNow;
            return true;
        }
        catch (AuthException ex)
        {
            SetStatus("Error", ex.Message);
            return false;
        }
        catch (HttpRequestException ex)
        {
            SetStatus("Error", "network: " + ex.Message);
            return false;
        }
    }

    public async Task SendChatAsync(string text)
    {
        if (_config.DryRun || _api == null || _tokens == null)
        {
            _log("chat NOT SENT (" + (_config.DryRun ? "dry run" : "not connected") + "): " + text);
            return;
        }

        try
        {
            await _api.Helix.Chat.SendChatMessage(new SendChatMessageRequest { BroadcasterId = _tokens.UserId, SenderId = _tokens.UserId, Message = text }, _tokens.AccessToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("chat send failed: " + ex.Message);
        }
    }

    private string? _predictionId;
    private string[] _predictionOutcomeIds = Array.Empty<string>();

    /// <summary>Starts a channel-points prediction (affiliate/partner). Outcomes: 2..10, titles max 25 chars.</summary>
    public async Task CreatePredictionAsync(CreatePrediction prediction)
    {
        if (_config.DryRun || _api == null || _tokens == null)
        {
            _log("prediction NOT CREATED (" + (_config.DryRun ? "dry run" : "not connected") + "): " + prediction.Title);
            _emit(new PredictionUpdate { Title = prediction.Title, Status = "error", Detail = _config.DryRun ? "dry run" : "not connected" });
            return;
        }

        try
        {
            var response = await _api.Helix.Predictions.CreatePredictionAsync(new TwitchLib.Api.Helix.Models.Predictions.CreatePrediction.CreatePredictionRequest
            {
                BroadcasterId = _tokens.UserId,
                Title = prediction.Title.Length > 45 ? prediction.Title[..45] : prediction.Title,
                Outcomes = prediction.Outcomes.Take(10).Select(o => new TwitchLib.Api.Helix.Models.Predictions.CreatePrediction.Outcome { Title = o.Length > 25 ? o[..25] : o }).ToArray(),
                PredictionWindowSeconds = Math.Clamp(prediction.WindowSeconds, 30, 1800),
            }, _tokens.AccessToken).ConfigureAwait(false);
            var created = response.Data.FirstOrDefault();
            _predictionId = created?.Id;
            _predictionOutcomeIds = created?.Outcomes.Select(o => o.Id).ToArray() ?? Array.Empty<string>();
            _log("prediction created: " + prediction.Title);
        }
        catch (Exception ex)
        {
            _log("prediction failed (affiliate/partner only, one at a time): " + ex.Message);
            _emit(new PredictionUpdate { Title = prediction.Title, Status = "error", Detail = ex.Message });
        }
    }

    /// <summary>Resolves the running prediction with the outcome at <paramref name="winningIndex"/>, or cancels it (-1, points refunded).</summary>
    /// <summary>Locks the running prediction (betting closed, outcome still open).</summary>
    public async Task LockPredictionAsync()
    {
        if (_api == null || _tokens == null || _predictionId == null) return;
        try
        {
            await _api.Helix.Predictions.EndPredictionAsync(_tokens.UserId, _predictionId, PredictionEndStatus.LOCKED, accessToken: _tokens.AccessToken).ConfigureAwait(false);
            _log("prediction locked");
        }
        catch (Exception ex)
        {
            _log("prediction lock failed: " + ex.Message);
        }
    }

    public async Task ResolvePredictionAsync(int winningIndex)
    {
        if (_api == null || _tokens == null || _predictionId == null) return;
        try
        {
            if (winningIndex >= 0 && winningIndex < _predictionOutcomeIds.Length)
            {
                await _api.Helix.Predictions.EndPredictionAsync(_tokens.UserId, _predictionId, PredictionEndStatus.RESOLVED, _predictionOutcomeIds[winningIndex], _tokens.AccessToken).ConfigureAwait(false);
            }
            else
            {
                await _api.Helix.Predictions.EndPredictionAsync(_tokens.UserId, _predictionId, PredictionEndStatus.CANCELED, accessToken: _tokens.AccessToken).ConfigureAwait(false);
            }

            _log("prediction " + (winningIndex >= 0 ? "resolved" : "cancelled"));
            _predictionId = null;
        }
        catch (Exception ex)
        {
            _log("prediction resolve failed: " + ex.Message);
        }
    }

    public async Task CreatePollAsync(CreatePoll poll)
    {
        if (_config.DryRun || _api == null || _tokens == null)
        {
            _log("poll NOT CREATED (" + (_config.DryRun ? "dry run" : "not connected") + "): " + poll.Title);
            return;
        }

        try
        {
            await _api.Helix.Polls.CreatePollAsync(new CreatePollRequest
            {
                BroadcasterId = _tokens.UserId,
                Title = poll.Title.Length > 60 ? poll.Title[..60] : poll.Title,
                Choices = poll.Choices.Take(5).Select(c => new Choice { Title = c.Length > 25 ? c[..25] : c }).ToArray(),
                DurationSeconds = Math.Clamp(poll.DurationSeconds, 15, 1800),
            }, _tokens.AccessToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("poll creation failed (affiliate/partner only): " + ex.Message);
        }
    }

    private void SetStatus(string state, string detail)
    {
        Status = new TwitchStatus { State = state, Channel = _tokens?.Login ?? string.Empty, Login = _tokens?.Login ?? string.Empty, Detail = detail, DryRun = _config.DryRun };
        _log("Twitch " + state + ": " + detail);
        _emit(Status);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_eventSub != null)
        {
            try
            {
                await _eventSub.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
