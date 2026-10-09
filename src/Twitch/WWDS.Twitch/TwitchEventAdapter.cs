using System;
using System.Collections.Generic;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Events;
using WWDS.Protocol;

namespace WWDS.Twitch;

/// <summary>
/// Maps transport messages (protocol DTOs) to platform-neutral audience events. Authority comes from
/// Twitch badges; configured "trusted users" are raised to moderator level.
/// </summary>
public sealed class TwitchEventAdapter
{
    private readonly HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);

    public void SetTrustedUsers(IEnumerable<string> logins)
    {
        _trusted.Clear();
        foreach (var l in logins) _trusted.Add(l.Trim().TrimStart('@'));
    }

    public Authority AuthorityOf(string login, bool broadcaster, bool moderator, bool vip, bool subscriber)
    {
        if (broadcaster) return Authority.Broadcaster;
        if (moderator || _trusted.Contains(login)) return Authority.Moderator;
        if (vip) return Authority.Vip;
        return subscriber ? Authority.Subscriber : Authority.Viewer;
    }

    public AudienceEvent? Map(Message message) => message switch
    {
        ChatMessage m => new ChatMessageReceived(
            new Viewer(Id(m.UserId, m.Login), m.Login, m.DisplayName, AuthorityOf(m.Login, m.IsBroadcaster, m.IsModerator, m.IsVip, m.IsSubscriber), m.Source),
            m.Text, m.MessageId),
        Redemption r => new ChannelPointRedeemed(Plain(r.UserId, r.Login, r.DisplayName), r.RewardId, r.RewardTitle, r.Cost, r.Input),
        Subscription s => new SubscriptionReceived(Plain(s.UserId, s.Login, s.DisplayName), s.Tier, s.Months, s.IsGift, s.GiftCount),
        Cheer c => new CheerReceived(c.Anonymous ? Plain("anonymous", "anonymous", "Anonymous") : Plain(c.UserId, c.Login, c.DisplayName), c.Bits, c.Message, c.Anonymous),
        Raid r => new RaidReceived(Plain(r.FromUserId, r.FromLogin, r.FromDisplayName), r.Viewers),
        Follow f => new FollowReceived(Plain(f.UserId, f.Login, f.DisplayName)),
        PollUpdate p => new PollUpdated(p.PollId, p.Title, p.Status, ToPairs(p.Choices)),
        _ => null,
    };

    private Viewer Plain(string id, string login, string display) =>
        new(Id(id, login), login, display, _trusted.Contains(login) ? Authority.Moderator : Authority.Viewer, "twitch");

    /// <summary>Anonymous IRC always carries user-id; mock users may not, so fall back to the login.</summary>
    private static string Id(string userId, string login) => string.IsNullOrEmpty(userId) ? "login:" + login.ToLowerInvariant() : userId;

    private static IList<KeyValuePair<string, int>> ToPairs(List<PollChoice> choices)
    {
        var list = new List<KeyValuePair<string, int>>();
        foreach (var c in choices) list.Add(new KeyValuePair<string, int>(c.Title, c.Votes));
        return list;
    }
}
