using System;
using System.Collections.Generic;
using System.Linq;
using WWDS.Audience.Engine;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Opponents;

/// <summary>How the audience is represented as a contestant (see docs/architecture.md, Twitch Opponent).</summary>
public enum OpponentMode
{
    /// <summary>A: the entire chat acts as one collective contestant (majority vote).</summary>
    Crowd,

    /// <summary>B: chat acts as a team; additionally chat names the team.</summary>
    ChatTeam,

    /// <summary>C: one selected viewer makes every decision.</summary>
    SelectedViewer,

    /// <summary>D: a different volunteer decides each round.</summary>
    RotatingViewers,

    /// <summary>F: chat votes, a virtual contestant makes the final call using the vote as input.</summary>
    Hybrid,
}

/// <summary>Where a new decider comes from when the current one is AFK or a moderator forces a change.</summary>
public enum RetargetSource
{
    Off,
    Volunteers,
    ActiveChatters,
    VolunteersThenChatters,
}

public enum OpponentBehaviorKind
{
    AudienceMajority,
    AudienceWeighted,
    Random,
    AlwaysA,
    RuleBased,
    HumanControlled,
    Hybrid,
}

public sealed class OpponentStatistics
{
    public int Decisions { get; set; }

    public int Correct { get; set; }

    public int Wrong { get; set; }

    public int Timeouts { get; set; }

    public int NoVotes { get; set; }

    public override string ToString() => Correct + " correct, " + Wrong + " wrong, " + Timeouts + " timeouts, " + NoVotes + " without votes";
}

/// <summary>An audience-driven contestant. Several can be defined; one is active per bot slot.</summary>
public sealed class OpponentDefinition
{
    public string Id { get; set; } = "twitch";

    public string DisplayName { get; set; } = "Twitch Chat";

    /// <summary>"config", "redemption", "raid", "command".</summary>
    public string Source { get; set; } = "config";

    public string TwitchUserId { get; set; } = string.Empty;

    public string TwitchUsername { get; set; } = string.Empty;

    /// <summary>Fixed team, or null to take whichever team has the bot.</summary>
    public TeamSlot? Team { get; set; }

    public OpponentMode Mode { get; set; } = OpponentMode.Crowd;

    public OpponentBehaviorKind Behavior { get; set; } = OpponentBehaviorKind.AudienceMajority;

    /// <summary>For rule-based/hybrid behaviour: probability (0..1) that the virtual contestant knows the answer.</summary>
    public double Difficulty { get; set; } = 0.6;

    /// <summary>Weight of this opponent's audience votes (multiplies subscriber weighting).</summary>
    public double VotingWeight { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public OpponentStatistics Statistics { get; } = new();
}

/// <summary>Summary of an audience vote handed to a behavior.</summary>
public sealed class VoteSummary
{
    public VoteSummary(IList<double> weights, int participants)
    {
        Weights = weights;
        Participants = participants;
    }

    /// <summary>Weight per option index.</summary>
    public IList<double> Weights { get; }

    public int Participants { get; }

    public int Leader => Participants == 0 ? -1 : Weights.IndexOf(Weights.Max());

    public double Share(int index)
    {
        var total = Weights.Sum();
        return total > 0 ? Weights[index] / total : 0;
    }
}

/// <summary>Decides for an opponent. Add new behaviours by implementing this and registering them.</summary>
public interface IOpponentBehavior
{
    OpponentBehaviorKind Kind { get; }

    /// <summary>True if the behaviour needs an audience vote before deciding.</summary>
    bool UsesVotes { get; }

    /// <summary>Returns the option index (or -1 to use the request's fallback).</summary>
    int Choose(DecisionRequest request, VoteSummary? votes, OpponentDefinition opponent, Random random);

    /// <summary>Numeric decisions (bonus estimate); null = use the game's own logic.</summary>
    double? Estimate(DecisionRequest request, double? audienceMedian, OpponentDefinition opponent, Random random);
}

public static class OpponentBehaviors
{
    private static readonly Dictionary<OpponentBehaviorKind, IOpponentBehavior> Registry = new()
    {
        [OpponentBehaviorKind.AudienceMajority] = new Majority(),
        [OpponentBehaviorKind.AudienceWeighted] = new Weighted(),
        [OpponentBehaviorKind.Random] = new RandomPick(),
        [OpponentBehaviorKind.AlwaysA] = new AlwaysFirst(),
        [OpponentBehaviorKind.RuleBased] = new RuleBased(),
        [OpponentBehaviorKind.HumanControlled] = new Majority(OpponentBehaviorKind.HumanControlled),
        [OpponentBehaviorKind.Hybrid] = new Hybrid(),
    };

    public static IOpponentBehavior Get(OpponentBehaviorKind kind) => Registry[kind];

    public static void Register(IOpponentBehavior behavior) => Registry[behavior.Kind] = behavior;

    /// <summary>Correct option if the adapter exposes it (answers only), else -1.</summary>
    internal static int Correct(DecisionRequest r) => r.Kind is DecisionKind.Answer or DecisionKind.MasterAnswer ? r.Question?.CorrectIndex ?? -1 : -1;

    private sealed class Majority : IOpponentBehavior
    {
        public Majority(OpponentBehaviorKind kind = OpponentBehaviorKind.AudienceMajority) => Kind = kind;

        public OpponentBehaviorKind Kind { get; }

        public bool UsesVotes => true;

        public int Choose(DecisionRequest request, VoteSummary? votes, OpponentDefinition opponent, Random random)
        {
            if (votes == null || votes.Participants == 0) return -1;
            var best = votes.Weights.Max();
            var leaders = Enumerable.Range(0, votes.Weights.Count).Where(i => Math.Abs(votes.Weights[i] - best) < 1e-9).ToList();
            return leaders[random.Next(leaders.Count)];
        }

        public double? Estimate(DecisionRequest request, double? audienceMedian, OpponentDefinition opponent, Random random) => audienceMedian;
    }

    private sealed class Weighted : IOpponentBehavior
    {
        public OpponentBehaviorKind Kind => OpponentBehaviorKind.AudienceWeighted;

        public bool UsesVotes => true;

        public int Choose(DecisionRequest request, VoteSummary? votes, OpponentDefinition opponent, Random random)
        {
            if (votes == null || votes.Participants == 0) return -1;
            var roll = random.NextDouble() * votes.Weights.Sum();
            for (var i = 0; i < votes.Weights.Count; i++)
            {
                roll -= votes.Weights[i];
                if (roll < 0) return i;
            }

            return votes.Weights.Count - 1;
        }

        public double? Estimate(DecisionRequest request, double? audienceMedian, OpponentDefinition opponent, Random random) => audienceMedian;
    }

    private sealed class RandomPick : IOpponentBehavior
    {
        public OpponentBehaviorKind Kind => OpponentBehaviorKind.Random;

        public bool UsesVotes => false;

        public int Choose(DecisionRequest request, VoteSummary? votes, OpponentDefinition opponent, Random random) => random.Next(request.Options.Count);

        public double? Estimate(DecisionRequest request, double? audienceMedian, OpponentDefinition opponent, Random random) => null;
    }

    private sealed class AlwaysFirst : IOpponentBehavior
    {
        public OpponentBehaviorKind Kind => OpponentBehaviorKind.AlwaysA;

        public bool UsesVotes => false;

        public int Choose(DecisionRequest request, VoteSummary? votes, OpponentDefinition opponent, Random random) => 0;

        public double? Estimate(DecisionRequest request, double? audienceMedian, OpponentDefinition opponent, Random random) => null;
    }

    /// <summary>Virtual contestant: knows the answer with probability <see cref="OpponentDefinition.Difficulty"/>.</summary>
    private sealed class RuleBased : IOpponentBehavior
    {
        public OpponentBehaviorKind Kind => OpponentBehaviorKind.RuleBased;

        public bool UsesVotes => false;

        public int Choose(DecisionRequest request, VoteSummary? votes, OpponentDefinition opponent, Random random)
        {
            var correct = Correct(request);
            var n = request.Options.Count;
            if (request.Kind == DecisionKind.Bet) return Math.Min(n - 1, (int)Math.Round(opponent.Difficulty * (n - 1)));
            if (correct < 0) return random.Next(n);
            if (random.NextDouble() < opponent.Difficulty) return correct;
            var wrong = Enumerable.Range(0, n).Where(i => i != correct).ToList();
            return wrong[random.Next(wrong.Count)];
        }

        public double? Estimate(DecisionRequest request, double? audienceMedian, OpponentDefinition opponent, Random random)
        {
            var target = request.Question?.NumericAnswer;
            if (target == null) return audienceMedian;
            var spread = (1 - Math.Max(0, Math.Min(1, opponent.Difficulty))) * 0.6;
            return Math.Round(target.Value * (1 + ((random.NextDouble() * 2) - 1) * spread));
        }
    }

    /// <summary>Chat votes; a clear majority wins, otherwise the virtual contestant decides.</summary>
    private sealed class Hybrid : IOpponentBehavior
    {
        public const double ClearMajority = 0.5;

        public OpponentBehaviorKind Kind => OpponentBehaviorKind.Hybrid;

        public bool UsesVotes => true;

        public int Choose(DecisionRequest request, VoteSummary? votes, OpponentDefinition opponent, Random random)
        {
            if (votes != null && votes.Participants >= 3 && votes.Leader >= 0 && votes.Share(votes.Leader) > ClearMajority) return votes.Leader;
            return Get(OpponentBehaviorKind.RuleBased).Choose(request, votes, opponent, random);
        }

        public double? Estimate(DecisionRequest request, double? audienceMedian, OpponentDefinition opponent, Random random) =>
            audienceMedian ?? Get(OpponentBehaviorKind.RuleBased).Estimate(request, null, opponent, random);
    }
}
