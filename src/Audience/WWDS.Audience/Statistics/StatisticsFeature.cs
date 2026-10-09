using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WWDS.ModCore.Serialization;
using WWDS.Audience.Engine;
using WWDS.Audience.Naming;
using WWDS.Audience.Voting;
using WWDS.ModCore.Commands;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;

namespace WWDS.Audience.Statistics;

public sealed class ViewerStats
{
    public string Name { get; set; } = string.Empty;

    public int Votes { get; set; }

    public int CorrectVotes { get; set; }

    public int Interactions { get; set; }

    public int NamesChosen { get; set; }

    public int GuessWins { get; set; }
}

public sealed class StatsData
{
    public Dictionary<string, ViewerStats> Viewers { get; set; } = new();

    /// <summary>Questions where chat voted and the majority was right / wrong.</summary>
    public int AudienceMajorityCorrect { get; set; }

    public int AudienceMajorityWrong { get; set; }

    /// <summary>Votes per option key over all questions ("A", "B", "C").</summary>
    public Dictionary<string, int> AnswerDistribution { get; set; } = new();

    /// <summary>Per team slot: correct / wrong answers of contestants.</summary>
    public Dictionary<string, int> ContestantResults { get; set; } = new();

    public int ShowsPlayed { get; set; }

    public int AudienceOpponentWins { get; set; }

    public int AudienceOpponentLosses { get; set; }
}

/// <summary>
/// Local statistics: participation, vote accuracy, answer distribution, most active viewers, contestant
/// and audience-opponent results. Saved to BepInEx/wwds/statistics.json only if enabled; never sent anywhere.
/// </summary>
public sealed class StatisticsFeature : FeatureBase
{
    public const string FeatureId = "Statistics";

    private readonly Dictionary<string, List<KeyValuePair<Viewer, int>>> _pendingVotes = new();
    private IConfigValue<bool> _persist = null!;
    private string _path = string.Empty;

    public override string Id => FeatureId;

    public override string DisplayName => "Statistics";

    public override string Description => "Local audience and game statistics (optional persistence, never transmitted).";

    public override IEnumerable<string> Dependencies => new[] { AudienceFeature.FeatureId };

    public StatsData Data { get; private set; } = new();

    protected override void OnStart()
    {
        _persist = Context.Config.Bind("Statistics", "SaveToDisk", true, "Keep statistics across sessions in BepInEx/wwds/statistics.json (local only).");
        var dir = Context.Host?.DataDirectory;
        _path = dir == null ? string.Empty : Path.Combine(dir, "statistics.json");
        Load();
        Context.Provide(this);

        Track(Context.Bus.Subscribe<InteractionCompleted>(OnCompleted));
        Track(Context.Bus.Subscribe<AnswerRevealed>(OnRevealed));
        Track(Context.Bus.Subscribe<QuestionFinished>(OnQuestionFinished));
        Track(Context.Bus.Subscribe<GameFinished>(OnGameFinished));
        Track(Context.Commands.Register(new CommandDefinition("stats", Authority.Viewer, "Your audience statistics", (_, who) =>
        {
            if (!Data.Viewers.TryGetValue(who.UserId, out var s)) return new CommandResult(CommandOutcome.Handled, who.DisplayName + ": noch keine Stimmen");
            var acc = s.Votes > 0 ? (int)Math.Round(100.0 * s.CorrectVotes / s.Votes) : 0;
            return new CommandResult(CommandOutcome.Handled, who.DisplayName + ": " + s.Votes + " Stimmen, " + acc + "% richtig, " + s.NamesChosen + " Namen, " + s.GuessWins + " Tipps gewonnen");
        }) { UserCooldown = 30 }));
    }

    public IList<KeyValuePair<string, int>> MostActive(int count) => Data.Viewers.Values
        .OrderByDescending(v => v.Interactions).Take(count)
        .Select(v => new KeyValuePair<string, int>(v.Name, v.Interactions)).ToList();

    private ViewerStats Get(Viewer v)
    {
        if (!Data.Viewers.TryGetValue(v.Id, out var s)) Data.Viewers[v.Id] = s = new ViewerStats();
        s.Name = v.DisplayName;
        return s;
    }

    private void OnCompleted(InteractionCompleted e)
    {
        switch (e.Interaction)
        {
            case VoteInteraction vote:
                var ballots = vote.Ballots.ToList();
                foreach (var b in ballots)
                {
                    var s = Get(b.Key);
                    s.Votes++;
                    s.Interactions++;
                    var key = vote.Choices[b.Value].Key;
                    Data.AnswerDistribution[key] = (Data.AnswerDistribution.TryGetValue(key, out var n) ? n : 0) + 1;
                }

                if (vote.Subject.Length > 0) _pendingVotes[vote.Subject] = ballots;
                if (vote.Subject.Length > 0 && e.Result.Success) _pendingVotes[vote.Subject + "#winner"] = new List<KeyValuePair<Viewer, int>> { new(new Viewer("-", "-", "-", Authority.Viewer, "-"), e.Result.WinningIndex) };
                break;
            case NamingInteraction naming:
                foreach (var entry in naming.Entries)
                {
                    foreach (var _ in entry.Supporters) Get(entry.Author).Interactions++;
                }

                if (e.Result.Success && e.Result.Winners.Count > 0) Get(e.Result.Winners[0]).NamesChosen++;
                break;
            case Guessing.GuessInteraction:
                foreach (var w in e.Result.Winners) Get(w).GuessWins++;
                break;
        }
    }

    private void OnRevealed(AnswerRevealed e)
    {
        if (e.Question == null || e.CorrectIndex < 0) return;
        if (_pendingVotes.TryGetValue(e.Question.Id, out var ballots))
        {
            foreach (var b in ballots.Where(b => b.Value == e.CorrectIndex)) Get(b.Key).CorrectVotes++;
            _pendingVotes.Remove(e.Question.Id);
        }

        if (_pendingVotes.TryGetValue(e.Question.Id + "#winner", out var winner))
        {
            if (winner[0].Value == e.CorrectIndex) Data.AudienceMajorityCorrect++;
            else Data.AudienceMajorityWrong++;
            _pendingVotes.Remove(e.Question.Id + "#winner");
        }
    }

    private void OnQuestionFinished(QuestionFinished e)
    {
        if (e.Team == null) return;
        var key = e.Team.Value + (e.Correct ? ".correct" : ".wrong");
        Data.ContestantResults[key] = (Data.ContestantResults.TryGetValue(key, out var n) ? n : 0) + 1;
    }

    private void OnGameFinished(GameFinished e)
    {
        if (!e.Aborted) Data.ShowsPlayed++;
        var opponent = Context.Get<Opponents.OpponentFeature>()?.Team;
        if (opponent != null && e.Winner != null)
        {
            if (e.Winner == opponent) Data.AudienceOpponentWins++;
            else Data.AudienceOpponentLosses++;
        }

        Save();
    }

    private void Load()
    {
        if (!_persist.Value || _path.Length == 0 || !File.Exists(_path)) return;
        try
        {
            Data = Json.Deserialize<StatsData>(File.ReadAllText(_path)) ?? new StatsData();
        }
        catch (Exception ex)
        {
            Log.Warn("statistics.json unreadable, starting fresh: " + ex.Message);
            Data = new StatsData();
        }
    }

    public void Save()
    {
        if (!_persist.Value || _path.Length == 0) return;
        try
        {
            File.WriteAllText(_path, Json.Serialize(Data, indent: true), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("statistics not saved: " + ex.Message);
        }
    }

    protected override void OnStop() => Save();
}
