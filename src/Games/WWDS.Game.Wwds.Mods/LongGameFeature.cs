using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Beardshaker.BeardBase.AssetBundleManager;
using HarmonyLib;
using TMPro;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>Where the questions of board 2 and later come from.</summary>
public enum BoardSource
{
    /// <summary>The next show not played yet (this game, then the other game's shows if imported).</summary>
    NextUnplayed,

    /// <summary>Any other show at random.</summary>
    Random,

    /// <summary>Own question packs first, then the next unplayed show.</summary>
    OwnPacks,

    /// <summary>The other game's shows (31-60) first, then the next unplayed show.</summary>
    OtherGame,
}

/// <summary>
/// Longer shows: after the 12 categories of a board, Kai sums up the scores, the studio wall shows
/// 12 new categories from another show and the game goes on; the final and the estimate come after
/// the last board. The boards of a show are chosen at its start and remembered (long-games.txt), so a
/// saved show resumes on the board it was left on. Each board gives both teams their audience joker
/// back; the money trophies are not unlocked in long shows.
/// </summary>
public sealed class LongGameFeature : FeatureBase
{
    public const string FeatureId = "LongGame";
    public const int MaxBoards = 4;

    private static LongGameFeature? _current;
    private readonly Dictionary<int, Record> _records = new();
    private IConfigValue<int> _boards = null!;
    private IConfigValue<BoardSource> _source = null!;
    private IConfigValue<bool> _resetJoker = null!;
    private string _file = string.Empty;
    private bool _resuming;
    private Record? _active;

    public override string Id => FeatureId;

    public override string DisplayName => "Long game";

    public override string Description => "Shows with 2 to 4 boards of 12 categories before the final; resumable.";

    protected override string LogCategoryName => "Mods";

    public IConfigValue<int> Boards => _boards;

    public IConfigValue<BoardSource> Source => _source;

    public IConfigValue<bool> ResetJoker => _resetJoker;

    /// <summary>Board (1-based) the last resumed long show continued on, 0 = none (self-test).</summary>
    public int ResumedOn { get; private set; }

    /// <summary>"Board 2 of 3" while a long show runs, else empty.</summary>
    public string Status => _active == null ? string.Empty : "Brett " + (_active.Index + 1) + " von " + _active.Shows.Count;

    protected override void OnStart()
    {
        var c = Context.Config;
        _boards = c.Bind("LongGame", "Boards", 1, "Boards of 12 categories per show (1 = normal show, up to 4).");
        _source = c.Bind("LongGame", "Source", BoardSource.NextUnplayed, "Questions of board 2 and later: NextUnplayed | Random | OwnPacks | OtherGame.");
        _resetJoker = c.Bind("LongGame", "JokerPerBoard", true, "Both teams get their audience joker back on every board.");
        var dir = Context.Host?.DataDirectory;
        if (dir == null) return;
        _file = Path.Combine(dir, "long-games.txt");
        Load();
        _current = this;
        Context.Provide(this);
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        ui.Patch(new PatchSpec("mods.long.start-show", "Remember whether a show is new or resumed.",
            () => AccessTools.Method(typeof(MainMenuController), nameof(MainMenuController.StartShow)), none)
        {
            Prefix = AccessTools.Method(typeof(LongGameFeature), nameof(StartShowPrefix)),
        });
        ui.Patch(new PatchSpec("mods.long.resume-board", "A resumed long show loads the board it was left on.",
            () => AccessTools.Method(typeof(AssetBundleManager), "SetVariants"), none)
        {
            Prefix = AccessTools.Method(typeof(LongGameFeature), nameof(SetVariantsPrefix)),
        });
        ui.Patch(new PatchSpec("mods.long.logic", "Choose the boards when a show starts.",
            () => AccessTools.Method(typeof(ShowController), nameof(ShowController.StartLogic)), none)
        {
            Postfix = AccessTools.Method(typeof(LongGameFeature), nameof(StartLogicPostfix)),
        });
        ui.Patch(new PatchSpec("mods.long.board-end", "After the 12th question of a board, switch to the next board.",
            () => AccessTools.Method(typeof(ShowController), "ShowQuestionResult"), none)
        {
            Postfix = AccessTools.Method(typeof(LongGameFeature), nameof(QuestionResultPostfix)),
        });
        ui.Patch(new PatchSpec("mods.long.trophies", "No money trophies in long shows (their amounts are for one board).",
            () => AccessTools.Method(typeof(SPlayerStatistics), nameof(SPlayerStatistics.UnlockTrophy)), none)
        {
            Prefix = AccessTools.Method(typeof(LongGameFeature), nameof(TrophyPrefix)),
        });
        Track(ui.OnState(state =>
        {
            if (state == "StateShowOutro" && _active != null) Finish();
            if (state == "StateLoadMainMenu") _active = null;
        }));
    }

    protected override void OnStop() => _current = null;

    // ---- records ------------------------------------------------------------------------------------

    /// <summary>The boards of one long show: the show ids in order and the board being played.</summary>
    internal sealed class Record
    {
        public Record(int start, List<int> shows, int index)
        {
            Start = start;
            Shows = shows;
            Index = index;
        }

        public int Start { get; }

        public List<int> Shows { get; }

        public int Index { get; set; }

        public int Current => Shows[Math.Min(Index, Shows.Count - 1)];

        public override string ToString() => Start + "\t" + string.Join(",", Shows.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToArray()) + "\t" + Index;

        public static Record? Parse(string line)
        {
            var parts = line.Split('\t');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var start) || !int.TryParse(parts[2], out var index)) return null;
            var shows = parts[1].Split(',').Select(s => int.TryParse(s, out var id) ? id : 0).ToList();
            return shows.Count >= 2 && shows[0] == start && shows.All(s => s > 0) ? new Record(start, shows, Math.Max(0, index)) : null;
        }
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        foreach (var line in File.ReadAllLines(_file))
        {
            if (Record.Parse(line) is { } r) _records[r.Start] = r;
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllLines(_file, _records.Values.Select(r => r.ToString()).ToArray());
        }
        catch (Exception ex)
        {
            Log.Warn("Long games could not be saved: " + ex.Message);
        }
    }

    private void Finish()
    {
        _records.Remove(_active!.Start);
        Save();
        Log.Info("Long show " + _active.Start + " finished after " + _active.Shows.Count + " boards");
    }

    /// <summary>Show ids for boards 2..n, following the configured source.</summary>
    internal List<int> ChooseBoards(int start, int count)
    {
        var available = SAssetBundleManager.Instance.GetAvailableShowIDs();
        var catalogue = Context.Get<CatalogueFeature>()?.Active == true;
        var packs = Context.Get<CustomQuestionsFeature>();
        bool Normal(int id) => id <= CatalogueFeature.HostShows || (catalogue && id <= 2 * CatalogueFeature.HostShows);
        bool Played(int id) => SDataAccess.SessionHistory != null && SDataAccess.SessionHistory.ContainsKey(id);
        var used = new HashSet<int> { start };
        var result = new List<int> { start };
        var random = new System.Random();
        for (var b = 1; b < count; b++)
        {
            var free = available.Where(id => !used.Contains(id)).ToList();
            var next = _source.Value switch
            {
                BoardSource.OwnPacks => free.Where(id => packs?.HasShow(id) == true).Cast<int?>().FirstOrDefault(),
                BoardSource.OtherGame when catalogue => free.Where(id => id > CatalogueFeature.HostShows && id <= 2 * CatalogueFeature.HostShows && !Played(id)).Cast<int?>().FirstOrDefault(),
                BoardSource.Random => free.Where(Normal).OrderBy(_ => random.Next()).Cast<int?>().FirstOrDefault(),
                _ => null,
            };
            next ??= free.Where(id => Normal(id) && !Played(id)).Cast<int?>().FirstOrDefault() ?? free.Where(Normal).Cast<int?>().FirstOrDefault();
            if (next == null) break;
            used.Add(next.Value);
            result.Add(next.Value);
        }

        return result;
    }

    // ---- patches ------------------------------------------------------------------------------------

    private static void StartShowPrefix(bool recreate)
    {
        if (_current != null) _current._resuming = recreate;
    }

    /// <summary>Runs before the catalogue and pack redirects, which then apply to the board's show.</summary>
    [HarmonyPriority(Priority.First)]
    private static void SetVariantsPrefix(ref string[] variants)
    {
        var self = _current;
        if (self == null || variants == null || variants.Length == 0 || !self._resuming || !int.TryParse(variants[0], out var show)) return;
        if (!self._records.TryGetValue(show, out var record) || record.Index == 0) return;
        variants = new[] { record.Current.ToString(CultureInfo.InvariantCulture) }.Concat(variants.Skip(1)).ToArray();
        self.ResumedOn = record.Index + 1;
        self.Log.Info("Resuming long show " + show + " on board " + (record.Index + 1) + " (show " + record.Current + ")");
    }

    private static void StartLogicPostfix()
    {
        var self = _current;
        var session = SDataAccess.ActiveSession;
        if (self == null || session == null || session.PlayTutorial) return;
        PatchGuard.Run("mods.long.logic", () =>
        {
            var start = session.ActiveShowIndex;
            if (self._resuming)
            {
                self._active = self._records.TryGetValue(start, out var r) ? r : null;
                return;
            }

            self._records.Remove(start);
            self._active = null;
            var boards = Mathf.Clamp(self._boards.Value, 1, MaxBoards);
            if (boards > 1)
            {
                var shows = self.ChooseBoards(start, boards);
                if (shows.Count > 1)
                {
                    self._active = new Record(start, shows, 0);
                    self._records[start] = self._active;
                    self.Log.Info("Long show " + start + ": boards " + string.Join(", ", shows.Select(s => s.ToString()).ToArray()));
                }
            }

            self.Save();
        });
    }

    private static void QuestionResultPostfix(ShowController __instance, ref IEnumerator __result)
    {
        var self = _current;
        if (self?._active != null) __result = self.AfterResult(__instance, __result);
    }

    private IEnumerator AfterResult(ShowController controller, IEnumerator original)
    {
        while (original.MoveNext()) yield return original.Current;
        var record = _active;
        var session = SDataAccess.ActiveSession;
        if (record == null || session == null || record.Index >= record.Shows.Count - 1) yield break;
        if (session.GetQuestionCount() < SDataStore.Instance.normalShowQuestions) yield break;
        var transition = NextBoard(controller, record, session);
        while (true)
        {
            object? current;
            try
            {
                if (!transition.MoveNext()) break;
                current = transition.Current;
            }
            catch (Exception ex)
            {
                Log.Error("Board change failed", ex);
                break;
            }

            yield return current;
        }
    }

    /// <summary>Interim score, new questions and voices, fresh grid and studio wall, jokers back.</summary>
    private IEnumerator NextBoard(ShowController controller, Record record, RuntimeSession session)
    {
        record.Index++;
        Save();
        var show = record.Current;
        Log.Info("Long show " + record.Start + ": board " + (record.Index + 1) + " of " + record.Shows.Count + " = show " + show);
        session.ShowProgress.Clear();
        yield return AccessTools.Method(typeof(ShowController), "PlayBetInterImbalanceVoiceLines").Invoke(controller, null);
        SSoundManager.Instance.StopAllVoices();
        var bundles = SAssetBundleManager.Instance;
        yield return bundles.SetVariants(show.ToString(CultureInfo.InvariantCulture));
        var aoc = bundles.GetShowAocID(show);
        var loader = bundles.LoadAssetAsync("show/data", "ShowData", aoc);
        yield return loader;
        session.CurrentShow = (ShowContent)loader.GetAsset();
        foreach (var voice in new[] { "kp", "elton", "hoecker", "male", "female" }) yield return bundles.LoadAssetBundleAsync("show/voice/" + voice, aoc);
        ((ViewCategoryUI)AccessTools.Field(typeof(ShowController), "viewCategoryUI").GetValue(controller)).SetCategoryTexts();
        if (_resetJoker.Value)
        {
            var setter = AccessTools.PropertySetter(typeof(Team), nameof(Team.UsedJoker));
            setter.Invoke(session.TeamA, new object[] { false });
            setter.Invoke(session.TeamB, new object[] { false });
        }

        yield return StudioWall();
    }

    /// <summary>Kai presents the new categories on the studio wall, as in the show intro.</summary>
    private IEnumerator StudioWall()
    {
        ResetWallFonts();
        LightController.Instance.SwitchLightshow(ELightshowTypes.IntroCategory);
        SSoundManager.Instance.PlayMusic("music_wall_intro");
        SCameraController.Instance.PlayCinematic("WallCamera");
        SStageController.Instance.StartApplause();
        SSoundManager.Instance.PlayVoiceFile(EVoiceType.Pflaume, string.Empty, "category_intro");
        yield return SStageController.Instance.StartStudioScreenIntroSequence();
        SSoundManager.Instance.StopMusic();
        SSoundManager.Instance.StopAllVoices();
        SStageController.Instance.StopApplause();
        SCameraController.Instance.SwitchCameraMode(CameraMode.GameMode);
        SCameraController.Instance.StopCurrentCinematic();
        LightController.Instance.TriggerState(ELightshowTypes.GameDefault);
        SStageController.Instance.StopStudioScreenIntro();
    }

    /// <summary>The wall enlarges "?" tiles on every intro and never shrinks them back.</summary>
    private static void ResetWallFonts()
    {
        var wall = UnityEngine.Object.FindObjectOfType<ViewStudioScreen>();
        if (wall == null || AccessTools.Field(typeof(ViewStudioScreen), "categoryButtonTexts").GetValue(wall) is not TextMeshProUGUI[] texts || texts.Length == 0) return;
        var size = texts.Min(t => t.fontSize);
        foreach (var t in texts)
        {
            t.fontSize = size;
            t.margin = Vector4.zero;
        }
    }

    private static bool TrophyPrefix(int trophyID) => _current?._active == null || trophyID < 32 || trophyID > 37;
}
