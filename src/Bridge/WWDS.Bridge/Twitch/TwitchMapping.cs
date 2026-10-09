using TwitchLib.EventSub.Core.SubscriptionTypes.Channel;
using WWDS.Protocol;

namespace WWDS.Bridge.Twitch;

/// <summary>Pure conversions from TwitchLib EventSub payloads to protocol messages.</summary>
public static class TwitchMapping
{
    public static ChatMessage Chat(ChannelChatMessage e)
    {
        var badges = e.Badges ?? Array.Empty<TwitchLib.EventSub.Core.Models.Chat.ChatBadge>();
        bool Has(string id) => badges.Any(b => string.Equals(b.SetId, id, StringComparison.OrdinalIgnoreCase));
        return new ChatMessage
        {
            MessageId = e.MessageId,
            UserId = e.ChatterUserId,
            Login = e.ChatterUserLogin,
            DisplayName = string.IsNullOrEmpty(e.ChatterUserName) ? e.ChatterUserLogin : e.ChatterUserName,
            Text = e.Message?.Text ?? string.Empty,
            IsBroadcaster = Has("broadcaster") || e.ChatterUserId == e.BroadcasterUserId,
            IsModerator = Has("moderator"),
            IsVip = Has("vip"),
            IsSubscriber = Has("subscriber") || Has("founder"),
            Bits = e.Cheer?.Bits ?? 0,
            Source = "twitch",
        };
    }

    public static Redemption Redemption(ChannelPointsCustomRewardRedemption e) => new()
    {
        Id = e.Id,
        UserId = e.UserId,
        Login = e.UserLogin,
        DisplayName = e.UserName,
        RewardId = e.Reward?.Id ?? string.Empty,
        RewardTitle = e.Reward?.Title ?? string.Empty,
        Cost = e.Reward?.Cost ?? 0,
        Input = e.UserInput ?? string.Empty,
    };

    public static Subscription Sub(ChannelSubscribe e) => new()
    {
        UserId = e.UserId,
        Login = e.UserLogin,
        DisplayName = e.UserName,
        Tier = e.Tier,
        Months = 1,
        IsGift = e.IsGift,
    };

    public static Subscription Gift(ChannelSubscriptionGift e) => new()
    {
        UserId = e.IsAnonymous ? "anonymous" : e.UserId ?? string.Empty,
        Login = e.IsAnonymous ? "anonymous" : e.UserLogin ?? string.Empty,
        DisplayName = e.IsAnonymous ? "Anonymous" : e.UserName ?? string.Empty,
        Tier = e.Tier,
        IsGift = true,
        GiftCount = e.Total,
    };

    public static Subscription Resub(ChannelSubscriptionMessage e) => new()
    {
        UserId = e.UserId,
        Login = e.UserLogin,
        DisplayName = e.UserName,
        Tier = e.Tier,
        Months = e.CumulativeMonths,
        Message = e.Message?.Text ?? string.Empty,
    };

    public static Cheer Cheer(ChannelCheer e) => new()
    {
        UserId = e.UserId ?? string.Empty,
        Login = e.UserLogin ?? string.Empty,
        DisplayName = e.UserName ?? string.Empty,
        Bits = e.Bits,
        Message = e.Message,
        Anonymous = e.IsAnonymous,
    };

    public static Raid Raid(ChannelRaid e) => new()
    {
        FromUserId = e.FromBroadcasterUserId,
        FromLogin = e.FromBroadcasterUserLogin,
        FromDisplayName = e.FromBroadcasterUserName,
        Viewers = e.Viewers,
    };

    public static Follow Follow(ChannelFollow e) => new() { UserId = e.UserId, Login = e.UserLogin, DisplayName = e.UserName };

    public static PollUpdate Poll(TwitchLib.EventSub.Core.Models.Polls.ChannelPollBase e, string status) => new()
    {
        PollId = e.Id,
        Title = e.Title,
        Status = status,
        Choices = (e.Choices ?? Array.Empty<TwitchLib.EventSub.Core.Models.Polls.PollChoice>()).Select(c => new WWDS.Protocol.PollChoice { Id = c.Id, Title = c.Title, Votes = c.Votes ?? 0 }).ToList(),
    };

    public static PredictionUpdate Prediction(TwitchLib.EventSub.Core.Models.Predictions.ChannelPredictionBase e, string status) => new()
    {
        PredictionId = e.Id,
        Title = e.Title,
        Status = status,
        Outcomes = (e.Outcomes ?? Array.Empty<TwitchLib.EventSub.Core.Models.Predictions.PredictionOutcomes>()).Select(o => new PredictionOutcome { Id = o.Id, Title = o.Title, Users = o.Users ?? 0, Points = o.ChannelPoints ?? 0 }).ToList(),
    };

    /// <summary>EventSub subscription conditions for a broadcaster who authorized the bridge themself.</summary>
    public static (string Version, Dictionary<string, string> Condition)? Subscription(string type, string broadcasterId, string tokenUserId) => type switch
    {
        "channel.chat.message" => ("1", new() { ["broadcaster_user_id"] = broadcasterId, ["user_id"] = tokenUserId }),
        "channel.channel_points_custom_reward_redemption.add" => ("1", new() { ["broadcaster_user_id"] = broadcasterId }),
        "channel.subscribe" or "channel.subscription.gift" or "channel.subscription.message" or "channel.cheer"
            or "channel.poll.begin" or "channel.poll.progress" or "channel.poll.end"
            or "channel.prediction.begin" or "channel.prediction.progress" or "channel.prediction.lock" or "channel.prediction.end" => ("1", new() { ["broadcaster_user_id"] = broadcasterId }),
        "channel.raid" => ("1", new() { ["to_broadcaster_user_id"] = broadcasterId }),
        "channel.follow" => ("2", new() { ["broadcaster_user_id"] = broadcasterId, ["moderator_user_id"] = tokenUserId }),
        "channel.update" => ("2", new() { ["broadcaster_user_id"] = broadcasterId }),
        _ => null,
    };
}
