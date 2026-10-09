using System.Collections.Generic;

namespace WWDS.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;
    public const int DefaultPort = 47321;

    /// <summary>Lines longer than this are dropped (protects both sides from garbage input).</summary>
    public const int MaxLineBytes = 64 * 1024;
}

/// <summary>Base of every message. <see cref="Type"/> is the wire discriminator.</summary>
public abstract class Message
{
    public abstract string Type { get; }
}

/// <summary>First message in both directions. The game sends the shared key from the key file.</summary>
public sealed class Hello : Message
{
    public override string Type => "hello";

    public int Protocol { get; set; } = ProtocolInfo.Version;

    /// <summary>"game" or "bridge".</summary>
    public string Role { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Game { get; set; } = string.Empty;
}

public sealed class Ping : Message
{
    public override string Type => "ping";

    public long Seq { get; set; }
}

public sealed class Pong : Message
{
    public override string Type => "pong";

    public long Seq { get; set; }
}

/// <summary>Twitch connection state as seen by the bridge.</summary>
public sealed class TwitchStatus : Message
{
    public override string Type => "twitch.status";

    /// <summary>Disconnected | Authenticating | Connecting | Connected | Error | Mock.</summary>
    public string State { get; set; } = "Disconnected";

    public string Channel { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public bool DryRun { get; set; }
}

public sealed class ChatMessage : Message
{
    public override string Type => "twitch.chat";

    public string MessageId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public bool IsBroadcaster { get; set; }

    public bool IsModerator { get; set; }

    public bool IsVip { get; set; }

    public bool IsSubscriber { get; set; }

    public int Bits { get; set; }

    /// <summary>"twitch" or "mock".</summary>
    public string Source { get; set; } = "twitch";
}

public sealed class Redemption : Message
{
    public override string Type => "twitch.redemption";

    public string Id { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string RewardId { get; set; } = string.Empty;

    public string RewardTitle { get; set; } = string.Empty;

    public int Cost { get; set; }

    public string Input { get; set; } = string.Empty;
}

public sealed class Subscription : Message
{
    public override string Type => "twitch.sub";

    public string UserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"1000", "2000", "3000".</summary>
    public string Tier { get; set; } = "1000";

    public int Months { get; set; }

    public bool IsGift { get; set; }

    /// <summary>Number of gifted subs for a gift bomb (channel.subscription.gift), else 0.</summary>
    public int GiftCount { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class Cheer : Message
{
    public override string Type => "twitch.cheer";

    public string UserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int Bits { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool Anonymous { get; set; }
}

public sealed class Raid : Message
{
    public override string Type => "twitch.raid";

    public string FromUserId { get; set; } = string.Empty;

    public string FromLogin { get; set; } = string.Empty;

    public string FromDisplayName { get; set; } = string.Empty;

    public int Viewers { get; set; }
}

public sealed class Follow : Message
{
    public override string Type => "twitch.follow";

    public string UserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

public sealed class PollChoice
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Votes { get; set; }
}

public sealed class PollUpdate : Message
{
    public override string Type => "twitch.poll";

    public string PollId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>begin | progress | end.</summary>
    public string Status { get; set; } = string.Empty;

    public List<PollChoice> Choices { get; set; } = new();
}

public sealed class ChannelUpdate : Message
{
    public override string Type => "twitch.channel";

    public string Title { get; set; } = string.Empty;

    public string CategoryName { get; set; } = string.Empty;
}

/// <summary>Game asks the bridge to post in chat. Ignored by the bridge in dry-run mode.</summary>
public sealed class SendChat : Message
{
    public override string Type => "chat.send";

    public string Text { get; set; } = string.Empty;
}

/// <summary>Game asks the bridge to start a native Twitch poll.</summary>
public sealed class CreatePoll : Message
{
    public override string Type => "poll.create";

    public string Title { get; set; } = string.Empty;

    public List<string> Choices { get; set; } = new();

    public int DurationSeconds { get; set; } = 30;
}

/// <summary>Compact game/audience state for the bridge console and future overlays.</summary>
public sealed class GameState : Message
{
    public override string Type => "game.state";

    public string Game { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public List<string> Answers { get; set; } = new();

    public int ScoreA { get; set; }

    public int ScoreB { get; set; }

    public string TeamA { get; set; } = string.Empty;

    public string TeamB { get; set; } = string.Empty;

    public string Interaction { get; set; } = string.Empty;

    public List<string> Tally { get; set; } = new();
}

// ---- account and bridge control (game -> bridge unless noted) --------------------------------------

/// <summary>Start the OAuth device code login; the bridge answers with <see cref="LoginCode"/> and later <see cref="LoginResult"/>.</summary>
public sealed class LoginStart : Message
{
    public override string Type => "login.start";
}

/// <summary>Bridge -> game: show this code to the streamer.</summary>
public sealed class LoginCode : Message
{
    public override string Type => "login.code";

    public string UserCode { get; set; } = string.Empty;

    public string VerificationUri { get; set; } = string.Empty;

    public int ExpiresInSeconds { get; set; }
}

/// <summary>Bridge -> game: login finished (or failed).</summary>
public sealed class LoginResult : Message
{
    public override string Type => "login.result";

    public bool Ok { get; set; }

    public string Login { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}

public sealed class Logout : Message
{
    public override string Type => "logout";
}

/// <summary>Game -> bridge: change bridge settings (persisted in bridge.json). Bridge -> game: current settings.</summary>
public sealed class BridgeSettings : Message
{
    public override string Type => "bridge.settings";

    public string ClientId { get; set; } = string.Empty;

    public bool DryRun { get; set; } = true;

    /// <summary>Bridge -> game only: logged-in account (empty if none).</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>Bridge -> game only: true if a client id is configured or built in.</summary>
    public bool HasClientId { get; set; }

    /// <summary>Bridge -> game only: the release build's Client ID is used (bridge.json has none).</summary>
    public bool ClientIdBuiltIn { get; set; }
}

/// <summary>Game -> bridge: stop (sent when the game exits and started the bridge itself).</summary>
public sealed class Shutdown : Message
{
    public override string Type => "shutdown";
}

// ---- predictions ------------------------------------------------------------------------------------

/// <summary>Game -> bridge: start a channel-points prediction (affiliate/partner).</summary>
public sealed class CreatePrediction : Message
{
    public override string Type => "prediction.create";

    public string Title { get; set; } = string.Empty;

    public List<string> Outcomes { get; set; } = new();

    public int WindowSeconds { get; set; } = 120;
}

/// <summary>
/// Game -> bridge: resolve the running prediction with the outcome at this index, cancel it (index -1), or
/// only lock it (<see cref="LockOnly"/>: no more channel points, resolved later).
/// </summary>
public sealed class ResolvePrediction : Message
{
    public override string Type => "prediction.resolve";

    public int WinningIndex { get; set; } = -1;

    public bool LockOnly { get; set; }
}

public sealed class PredictionOutcome
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Users { get; set; }

    public int Points { get; set; }
}

/// <summary>Bridge -> game: prediction lifecycle (begin, progress, lock, end, error).</summary>
public sealed class PredictionUpdate : Message
{
    public override string Type => "twitch.prediction";

    public string PredictionId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public List<PredictionOutcome> Outcomes { get; set; } = new();
}

public sealed class ErrorMessage : Message
{
    public override string Type => "error";

    public string Message { get; set; } = string.Empty;
}

/// <summary>A well-formed message of a type this side does not know (newer peer). Ignored safely.</summary>
public sealed class UnknownMessage : Message
{
    public UnknownMessage(string type) => RawType = type;

    public override string Type => "unknown";

    public string RawType { get; }
}
