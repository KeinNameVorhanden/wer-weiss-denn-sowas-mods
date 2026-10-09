using System.Collections.Generic;
using WWDS.ModCore.Commands;

namespace WWDS.ModCore.Events;

/// <summary>A viewer as reported by the streaming platform. <see cref="Id"/> is stable across renames.</summary>
public sealed class Viewer
{
    public Viewer(string id, string login, string displayName, Authority authority, string platform)
    {
        Id = id ?? string.Empty;
        Login = login ?? string.Empty;
        DisplayName = string.IsNullOrEmpty(displayName) ? Login : displayName;
        Authority = authority;
        Platform = platform ?? string.Empty;
    }

    public string Id { get; }

    public string Login { get; }

    public string DisplayName { get; }

    public Authority Authority { get; }

    /// <summary>"twitch", "irc", "mock".</summary>
    public string Platform { get; }

    public Invoker AsInvoker() => new(Id, DisplayName, Authority, Platform, Login);

    public override string ToString() => DisplayName;
}

/// <summary>Base of everything coming from the audience platform (Twitch today).</summary>
public abstract class AudienceEvent
{
}

public enum AudienceLinkState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

/// <summary>Connection state of the audience transport (bridge, IRC fallback or mock).</summary>
public sealed class AudienceConnectionChanged : AudienceEvent
{
    public AudienceConnectionChanged(AudienceLinkState state, string transport, string channel, string detail, bool dryRun)
    {
        State = state;
        Transport = transport;
        Channel = channel;
        Detail = detail;
        DryRun = dryRun;
    }

    public AudienceLinkState State { get; }

    public string Transport { get; }

    public string Channel { get; }

    public string Detail { get; }

    public bool DryRun { get; }

    public override string ToString() => "AudienceConnectionChanged " + Transport + " " + State + (Detail.Length > 0 ? " (" + Detail + ")" : string.Empty);
}

public sealed class ChatMessageReceived : AudienceEvent
{
    public ChatMessageReceived(Viewer viewer, string text, string messageId)
    {
        Viewer = viewer;
        Text = text ?? string.Empty;
        MessageId = messageId ?? string.Empty;
    }

    public Viewer Viewer { get; }

    public string Text { get; }

    public string MessageId { get; }

    /// <summary>Do not log message text by default (privacy); the type name is enough for the journal.</summary>
    public override string ToString() => "ChatMessageReceived from " + Viewer.DisplayName;
}

/// <summary>A chat message that parsed as a command and was dispatched.</summary>
public sealed class ChatCommandReceived : AudienceEvent
{
    public ChatCommandReceived(Viewer viewer, ParsedCommand command, CommandOutcome outcome)
    {
        Viewer = viewer;
        Command = command;
        Outcome = outcome;
    }

    public Viewer Viewer { get; }

    public ParsedCommand Command { get; }

    public CommandOutcome Outcome { get; }

    public override string ToString() => "ChatCommand !" + Command.Name + " by " + Viewer.DisplayName + " -> " + Outcome;
}

public sealed class ChannelPointRedeemed : AudienceEvent
{
    public ChannelPointRedeemed(Viewer viewer, string rewardId, string rewardTitle, int cost, string input)
    {
        Viewer = viewer;
        RewardId = rewardId;
        RewardTitle = rewardTitle;
        Cost = cost;
        Input = input ?? string.Empty;
    }

    public Viewer Viewer { get; }

    public string RewardId { get; }

    public string RewardTitle { get; }

    public int Cost { get; }

    public string Input { get; }

    public override string ToString() => "ChannelPointRedeemed '" + RewardTitle + "' by " + Viewer.DisplayName;
}

public sealed class SubscriptionReceived : AudienceEvent
{
    public SubscriptionReceived(Viewer viewer, string tier, int months, bool isGift, int giftCount)
    {
        Viewer = viewer;
        Tier = tier;
        Months = months;
        IsGift = isGift;
        GiftCount = giftCount;
    }

    public Viewer Viewer { get; }

    public string Tier { get; }

    public int Months { get; }

    public bool IsGift { get; }

    /// <summary>Number of subs in a gift bomb, else 0.</summary>
    public int GiftCount { get; }

    public override string ToString() => "SubscriptionReceived " + Viewer.DisplayName + (GiftCount > 0 ? " gifted " + GiftCount : string.Empty);
}

public sealed class CheerReceived : AudienceEvent
{
    public CheerReceived(Viewer viewer, int bits, string message, bool anonymous)
    {
        Viewer = viewer;
        Bits = bits;
        Message = message ?? string.Empty;
        Anonymous = anonymous;
    }

    public Viewer Viewer { get; }

    public int Bits { get; }

    public string Message { get; }

    public bool Anonymous { get; }

    public override string ToString() => "CheerReceived " + Bits + " bits";
}

public sealed class RaidReceived : AudienceEvent
{
    public RaidReceived(Viewer from, int viewers)
    {
        From = from;
        Viewers = viewers;
    }

    public Viewer From { get; }

    public int Viewers { get; }

    public override string ToString() => "RaidReceived from " + From.DisplayName + " (" + Viewers + ")";
}

public sealed class FollowReceived : AudienceEvent
{
    public FollowReceived(Viewer viewer) => Viewer = viewer;

    public Viewer Viewer { get; }
}

public sealed class PollUpdated : AudienceEvent
{
    public PollUpdated(string pollId, string title, string status, IList<KeyValuePair<string, int>> choices)
    {
        PollId = pollId;
        Title = title;
        Status = status;
        Choices = choices;
    }

    public string PollId { get; }

    public string Title { get; }

    /// <summary>begin | progress | end.</summary>
    public string Status { get; }

    /// <summary>Choice title and votes, in poll order.</summary>
    public IList<KeyValuePair<string, int>> Choices { get; }

    public override string ToString() => "PollUpdated " + Title + " " + Status;
}

/// <summary>Summary of the currently running audience interaction (overlay, bridge console).</summary>
public sealed class InteractionStatusChanged : AudienceEvent
{
    public InteractionStatusChanged(string title, string state, IList<string> lines, double secondsLeft)
    {
        Title = title;
        State = state;
        Lines = lines;
        SecondsLeft = secondsLeft;
    }

    public static InteractionStatusChanged Idle { get; } = new(string.Empty, "Idle", new List<string>(), 0);

    public string Title { get; }

    public string State { get; }

    /// <summary>Pre-formatted tally lines, e.g. "A  52% (13)".</summary>
    public IList<string> Lines { get; }

    public double SecondsLeft { get; }

    public override string ToString() => "Interaction " + Title + " " + State;
}

/// <summary>Request to start a channel-points prediction (affiliate/partner channels only).</summary>
public sealed class PredictionCreateRequested : AudienceEvent
{
    public PredictionCreateRequested(string title, IList<string> outcomes, int windowSeconds)
    {
        Title = title;
        Outcomes = outcomes;
        WindowSeconds = windowSeconds;
    }

    public string Title { get; }

    public IList<string> Outcomes { get; }

    public int WindowSeconds { get; }
}

/// <summary>Resolve the running prediction with the outcome at this index; -1 cancels (points refunded).</summary>
public sealed class PredictionResolveRequested : AudienceEvent
{
    public PredictionResolveRequested(int winningIndex) => WinningIndex = winningIndex;

    public int WinningIndex { get; }
}

/// <summary>Request to lock the running prediction (no more channel points in; resolved later).</summary>
public sealed class PredictionLockRequested : AudienceEvent
{
}

/// <summary>Prediction lifecycle from Twitch: begin, progress, lock, end, or error.</summary>
public sealed class PredictionUpdated : AudienceEvent
{
    public PredictionUpdated(string id, string title, string status, string detail, IList<KeyValuePair<string, int>> outcomeUsers, IList<int> outcomePoints)
    {
        Id = id;
        Title = title;
        Status = status;
        Detail = detail;
        OutcomeUsers = outcomeUsers;
        OutcomePoints = outcomePoints;
    }

    public string Id { get; }

    public string Title { get; }

    public string Status { get; }

    public string Detail { get; }

    /// <summary>Outcome title and number of predictors.</summary>
    public IList<KeyValuePair<string, int>> OutcomeUsers { get; }

    public IList<int> OutcomePoints { get; }

    public override string ToString() => "PredictionUpdated " + Title + " " + Status + (Detail.Length > 0 ? " (" + Detail + ")" : string.Empty);
}

/// <summary>Request from a feature to start a native Twitch poll (affiliate/partner channels only).</summary>
public sealed class PollCreateRequested : AudienceEvent
{
    public PollCreateRequested(string title, IList<string> choices, int durationSeconds)
    {
        Title = title;
        Choices = choices;
        DurationSeconds = durationSeconds;
    }

    public string Title { get; }

    public IList<string> Choices { get; }

    public int DurationSeconds { get; }
}

/// <summary>Request from a feature to post text in chat. The Twitch feature handles dry run and rate limits.</summary>
public sealed class ChatSendRequested : AudienceEvent
{
    public ChatSendRequested(string text) => Text = text ?? string.Empty;

    public string Text { get; }
}
