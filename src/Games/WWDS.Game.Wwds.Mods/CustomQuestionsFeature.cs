using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Beardshaker.BeardBase.AssetBundleManager;
using DoozyUI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;
using WWDS.ModCore.Config;
using WWDS.ModCore.Content;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Own question packs as extra shows (ids 101 and up, in the Mediathek and for Quickplay). Each folder
/// in BepInEx/wwds/questions with a pack.json and/or questions.csv is a pack; every 12 questions make one
/// show (plus a final and an estimate). The show rides on the game's show 1 for scenery and generic
/// lines, but its questions are replaced and its show-specific voice lines are muted (with reading
/// pauses instead), so nothing of show 1 is heard. Answer videos come from the pack folder; a question
/// without video skips the video. Progress lives in its own save file, Steam achievements are not
/// unlocked in these shows, statistics count.
/// </summary>
public sealed class CustomQuestionsFeature : FeatureBase
{
    public const string FeatureId = "CustomQuestions";
    public const int FirstShowId = 101;
    private const string Marker = "wwdscustom_";
    private const string TemplateFolder = "_vorlage";

    private static CustomQuestionsFeature? _current;
    private readonly List<LoadedPack> _packs = new();
    private readonly Dictionary<int, ShowRef> _shows = new();
    private IConfigValue<bool> _enabled = null!;
    private ModUi _ui = null!;
    private string _idFile = string.Empty;
    private bool _registerDone;
    private ShowRef? _session;
    private ShowContent? _content;

    public override string Id => FeatureId;

    public override string DisplayName => "Own questions";

    public override string Description => "Your own question packs (CSV or JSON, optional videos) as extra shows from 101 on.";

    protected override string LogCategoryName => "Mods";

    public string Folder { get; private set; } = string.Empty;

    public IList<LoadedPack> Packs => _packs.AsReadOnly();

    /// <summary>Show ids of all playable pack shows, in order.</summary>
    public IEnumerable<int> ShowIds => _shows.Keys.OrderBy(k => k);

    /// <summary>The running show is a pack show.</summary>
    public bool Active => _session != null;

    public bool HasShow(int id) => _shows.ContainsKey(id);

    public string ShowTitle(int id) => _shows.TryGetValue(id, out var s) ? s.Title : string.Empty;

    protected override void OnStart()
    {
        _enabled = Context.Config.Bind("Questions", "OwnPacks", true, "Offer own question packs from BepInEx/wwds/questions as shows 101 and up.");
        var dir = Context.Host?.DataDirectory;
        if (dir == null) return;
        Folder = Path.Combine(dir, "questions");
        _idFile = Path.Combine(dir, "question-packs.txt");
        _ui = ModUi.For(Context);
        _current = this;
        Context.Provide(this);
        var none = ModCore.Capabilities.GameCapability.None;
        var sound = typeof(SSoundManager);
        _ui.Patch(new PatchSpec("mods.questions.variants", "Pack shows load the game's show 1 for scenery.",
            () => AccessTools.Method(typeof(AssetBundleManager), "SetVariants"), none)
        {
            Prefix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(SetVariantsPrefix)),
        });
        _ui.Patch(new PatchSpec("mods.questions.content", "Pack shows get their own questions.",
            () => AccessTools.Method(typeof(AssetBundleGetAsset), nameof(AssetBundleGetAsset.GetAsset)), none)
        {
            Postfix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(GetAssetPostfix)),
        });
        _ui.Patch(new PatchSpec("mods.questions.voice", "Pack shows: no voice lines of show 1, reading pauses instead.",
            () => AccessTools.Method(sound, nameof(SSoundManager.PlayVoiceFileFromAssetBundleAndWait), new[] { typeof(EVoiceType), typeof(string) }), none)
        {
            Prefix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(VoicePrefix)),
        });
        _ui.Patch(new PatchSpec("mods.questions.voice-exists", "Pack shows have no show-specific voice lines.",
            () => AccessTools.Method(sound, nameof(SSoundManager.AssetBundleContainsSound), new[] { typeof(EVoiceType), typeof(string) }), none)
        {
            Postfix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(VoiceExistsPostfix)),
        });
        _ui.Patch(new PatchSpec("mods.questions.video-file", "Answer videos of pack shows come from the pack folder.",
            () => AccessTools.Method(typeof(VideoPlayerPC), "Play"), none)
        {
            Prefix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(VideoFilePrefix)),
        });
        _ui.Patch(new PatchSpec("mods.questions.video-skip", "A pack question without (working) video skips the video instead of waiting forever.",
            () => AccessTools.Method(typeof(SVideoController), nameof(SVideoController.PlayAndWait)), none)
        {
            Prefix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(PlayAndWaitPrefix)),
            Postfix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(PlayAndWaitPostfix)),
        });
        _ui.Patch(new PatchSpec("mods.questions.trophies", "No Steam achievements in pack shows.",
            () => AccessTools.Method(typeof(SPlayerStatistics), nameof(SPlayerStatistics.UnlockTrophy)), none)
        {
            Prefix = AccessTools.Method(typeof(CustomQuestionsFeature), nameof(TrophyPrefix)),
        });
        SessionFiles.Register(_ui, FirstShowId, int.MaxValue, () => "Sessions_Custom", () => _enabled.Value);
        Track(_ui.OnState(state =>
        {
            if (state == "StateLoadMainMenu") EndSession();
        }));
        Reload();
    }

    protected override void OnStop() => _current = null;

    public override void Tick()
    {
        if (_registerDone || !_enabled.Value) return;
        var register = SAssetBundleManager.Instance != null ? SAssetBundleManager.Instance.GetShowRegister() : null;
        if (register?.dataArray == null) return;
        _registerDone = true;
        var rows = register.dataArray.Where(r => r.ID < FirstShowId).ToList();
        rows.AddRange(ShowIds.Select(id => new ShowRegisterData { ID = id, Datafilename = "Custom" + id, Aocsteam = 0 }));
        register.dataArray = rows.ToArray();
        if (_shows.Count > 0) Log.Info("Own question shows " + string.Join(", ", ShowIds.Select(i => i.ToString()).ToArray()));
    }

    // ---- packs --------------------------------------------------------------------------------------

    public sealed class LoadedPack
    {
        public LoadedPack(string folder, QuestionPack pack)
        {
            Folder = folder;
            Pack = pack;
        }

        public string Folder { get; }

        public QuestionPack Pack { get; }

        public List<int> ShowIds { get; } = new();

        public string Name => Pack.Title.Length > 0 ? Pack.Title : Pack.Id;
    }

    private sealed class ShowRef
    {
        public ShowRef(int id, LoadedPack pack, int part)
        {
            Id = id;
            Pack = pack;
            Part = part;
        }

        public int Id { get; }

        public LoadedPack Pack { get; }

        public int Part { get; }

        public string Title => Pack.Name + (Pack.Pack.ShowCount > 1 ? " " + (Part + 1) : string.Empty);
    }

    /// <summary>Reads every pack folder again (show ids stay stable through question-packs.txt).</summary>
    public void Reload()
    {
        _packs.Clear();
        _shows.Clear();
        _registerDone = false;
        if (!_enabled.Value) return;
        try
        {
            Directory.CreateDirectory(Folder);
            WriteTemplate();
            var ids = ReadIds();
            foreach (var dir in Directory.GetDirectories(Folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                var pack = new QuestionPack { Id = name };
                var json = Path.Combine(dir, "pack.json");
                var csv = Directory.GetFiles(dir, "*.csv").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (File.Exists(json)) pack.ReadJson(QuestionPack.Decode(File.ReadAllBytes(json)), "pack.json");
                foreach (var file in csv) pack.ReadCsv(QuestionPack.Decode(File.ReadAllBytes(file)), Path.GetFileName(file));
                if (!File.Exists(json) && csv.Count == 0) pack.Errors.Add("no pack.json and no .csv file in the folder");
                pack.Finish();
                foreach (var q in pack.Questions.Where(q => q.Video.Length > 0 && !File.Exists(Path.Combine(dir, q.Video))))
                {
                    pack.Warnings.Add(q.Source + ": video " + q.Video + " not found, the question plays without video");
                }

                var loaded = new LoadedPack(dir, pack);
                _packs.Add(loaded);
                for (var part = 0; part < pack.ShowCount; part++)
                {
                    var key = name + "/" + part;
                    if (!ids.TryGetValue(key, out var id))
                    {
                        id = Math.Max(FirstShowId - 1, ids.Values.DefaultIfEmpty(0).Max()) + 1;
                        ids[key] = id;
                    }

                    _shows[id] = new ShowRef(id, loaded, part);
                    loaded.ShowIds.Add(id);
                }

                if (pack.Playable) Log.Info("Question pack " + name + ": " + pack.ShowCount + " show(s), " + pack.Warnings.Count + " hint(s)");
                else Log.Warn("Question pack " + name + " is not playable: " + string.Join("; ", pack.Errors.Take(5).ToArray()));
            }

            File.WriteAllLines(_idFile, ids.OrderBy(p => p.Value).Select(p => p.Key + "\t" + p.Value).ToArray());
        }
        catch (Exception ex)
        {
            Log.Warn("Question packs could not be read: " + ex.Message);
        }
    }

    private Dictionary<string, int> ReadIds()
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_idFile)) return ids;
        foreach (var line in File.ReadAllLines(_idFile))
        {
            var tab = line.LastIndexOf('\t');
            if (tab > 0 && int.TryParse(line.Substring(tab + 1), out var id) && id >= FirstShowId) ids[line.Substring(0, tab)] = id;
        }

        return ids;
    }

    private void WriteTemplate()
    {
        var dir = Path.Combine(Folder, TemplateFolder);
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "questions.csv"), new[]
        {
            "Typ;Kategorie;Thema;Frage;A;B;C;Richtig;Video;Reaktion",
            "Frage;Tierisch;Tiere;Wie viele Herzen hat ein Oktopus?;Eins;Zwei;Drei;C;;",
            "Frage;\"Rund ums\nEssen\";Essen;Woraus wird Marzipan hauptsaechlich gemacht?;Mandeln;Erdnuessen;Kokos;A;videos/marzipan.mp4;6",
            "Finale;Geografie;Weltweit;Welches Land hat die meisten Zeitzonen?;Russland;USA;Frankreich;C;;",
            "Schaetzfrage;;;Wie viele Stufen hat der Eiffelturm bis zur Spitze?;;;;1665;;",
        }, new System.Text.UTF8Encoding(true));
        File.WriteAllLines(Path.Combine(Folder, "LIESMICH.txt"), new[]
        {
            "Eigene Fragen fuer Wer weiss denn sowas",
            string.Empty,
            "Jeder Unterordner ist ein Fragenpaket (Ordner mit _ am Anfang werden ignoriert, z. B. _vorlage).",
            "Darin: questions.csv (Excel/LibreOffice, Trennzeichen ; ) und/oder pack.json, dazu optional Videos.",
            string.Empty,
            "Spalten: Typ (Frage, Finale, Schaetzfrage), Kategorie, Thema, Frage, A, B, C, Richtig (A/B/C,",
            "bei der Schaetzfrage die Zahl), Video (Pfad im Paketordner, .mp4 H.264 am besten), Reaktion (Sekunde im",
            "Video, zu der das Studio reagiert). Zeilenumbruch in Kategorie und Frage: in der Zelle oder \\n.",
            string.Empty,
            "Eine Show braucht 12 Fragen, 1 Finale und 1 Schaetzfrage. Je 12 weitere Fragen ergeben eine weitere",
            "Show (Finale und Schaetzfragen werden reihum verwendet). Die Shows erscheinen ab Nummer 101 in der",
            "Mediathek. Kopiere _vorlage als Startpunkt und benenne den Ordner um.",
            string.Empty,
            "pack.json: { \"title\": \"...\", \"questions\": [ { \"category\": \"...\", \"topic\": \"Tiere\", \"question\": \"...\",",
            "  \"answers\": [\"...\", \"...\", \"...\"], \"correct\": \"B\", \"video\": \"videos/q1.mp4\", \"reaction\": 5 } ],",
            "  \"master\": [ wie questions ], \"estimate\": [ { \"question\": \"...\", \"answer\": 1234 } ] }",
            string.Empty,
            "Thema (wie die Kapitaene reagieren): Allgemein, Alltag, Brisant, Clever, Damals, Essen, Fragezeichen,",
            "Genial, Gesellschaft, Natur, Recht, Sport, Tiere, Technik, Weltweit, Wissenschaft.",
            "Fehler und Hinweise zeigt das Mod-Menue unter Inhalte.",
        });
    }

    // ---- show content -------------------------------------------------------------------------------

    /// <summary>The game's question rows for a pack show (12 board questions, final, estimate).</summary>
    internal ShowContent? BuildContent(int showId)
    {
        if (!_shows.TryGetValue(showId, out var show)) return null;
        var data = show.Pack.Pack.Show(show.Part);
        var rows = new List<ShowContentData>();
        for (var i = 0; i < data.Board.Length; i++) rows.Add(Row(showId, i, i + 1, EQuestionType.Question, data.Board[i]));
        rows.Add(Row(showId, 12, 13, EQuestionType.Master, data.Master));
        rows.Add(Row(showId, 13, 14, EQuestionType.Bonus, data.Estimate));
        var content = ScriptableObject.CreateInstance<ShowContent>();
        content.name = "Custom" + showId;
        content.hideFlags = HideFlags.DontUnloadUnusedAsset;
        content.dataArray = rows.ToArray();
        return content;
    }

    private static ShowContentData Row(int showId, int index, int id, EQuestionType type, PackQuestion q)
    {
        var topic = Enum.IsDefined(typeof(EInternalCategories), q.Topic) ? (EInternalCategories)Enum.Parse(typeof(EInternalCategories), q.Topic) : EInternalCategories.Allgemein;
        return new ShowContentData
        {
            ID = id,
            EQUESTIONTYPE = type,
            Kategoriename = type == EQuestionType.Bonus && q.Category.Length == 0 ? "Schätzfrage" : q.Category,
            EINTERNALCATEGORIES = topic,
            Kategorievoicefile = "custom_" + showId + "_" + index,
            Frage = q.Question,
            Antwort = (float)q.Correct,
            Antwortvideofile = q.Video.Length > 0 ? Marker + showId + "_" + index : string.Empty,
            ReactionTime = q.Reaction > 0 ? q.Reaction : -1,
            A = q.Answers.Length > 0 ? q.Answers[0] : string.Empty,
            B = q.Answers.Length > 1 ? q.Answers[1] : string.Empty,
            C = q.Answers.Length > 2 ? q.Answers[2] : string.Empty,
        };
    }

    /// <summary>Absolute video file of a pack question marker ("wwdscustom_101_3"), or null.</summary>
    private string? VideoFile(string marker)
    {
        var parts = marker.Substring(Marker.Length).Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var showId) || !int.TryParse(parts[1], out var index)) return null;
        if (!_shows.TryGetValue(showId, out var show)) return null;
        var data = show.Pack.Pack.Show(show.Part);
        var q = index < 12 ? data.Board[index] : index == 12 ? data.Master : data.Estimate;
        if (q.Video.Length == 0) return null;
        var path = Path.Combine(show.Pack.Folder, q.Video);
        return File.Exists(path) ? path : null;
    }

    private void EndSession()
    {
        _session = null;
        if (_content != null) UnityEngine.Object.Destroy(_content);
        _content = null;
    }

    // ---- patches ------------------------------------------------------------------------------------

    private static void SetVariantsPrefix(ref string[] variants)
    {
        var self = _current;
        if (self == null || variants == null || variants.Length == 0 || !int.TryParse(variants[0], out var show)) return;
        if (show < FirstShowId || !self._enabled.Value || !self._shows.TryGetValue(show, out var target))
        {
            if (show > 0) self.EndSession();
            return;
        }

        self.EndSession();
        self._session = target;
        self._content = self.BuildContent(show);
        variants = new[] { "1" }.Concat(variants.Skip(1)).ToArray();
        self.Log.Info("Show " + show + " = question pack " + target.Title);
    }

    private static void GetAssetPostfix(ref UnityEngine.Object __result)
    {
        var self = _current;
        if (self?._content != null && __result is ShowContent) __result = self._content;
    }

    private static bool VoicePrefix(string variation, ref IEnumerator __result, bool __runOriginal)
    {
        var self = _current;
        if (!__runOriginal || self?._session == null) return __runOriginal;
        if (self.Context.Get<CustomVoiceFeature>()?.HasLine(variation) == true) return true;
        __result = Pause(Delay(variation));
        return false;
    }

    /// <summary>Time a silent line takes instead: reading the question, a moment per answer and category.</summary>
    internal static float Delay(string line)
    {
        line ??= string.Empty;
        var question = line.IndexOf("_question", StringComparison.Ordinal) >= 0;
        if (question && (line.EndsWith("_a", StringComparison.Ordinal) || line.EndsWith("_b", StringComparison.Ordinal) || line.EndsWith("_c", StringComparison.Ordinal))) return 1f;
        if (question)
        {
            var text = SDataAccess.ActiveSession?.GetActiveQuestionData()?.Frage ?? string.Empty;
            return Mathf.Clamp(text.Length / 16f, 2.5f, 8f);
        }

        return line.StartsWith("speech_kp_category", StringComparison.Ordinal) || line.StartsWith("speech_kp_master_category", StringComparison.Ordinal) ? 0.8f : 0f;
    }

    private static IEnumerator Pause(float seconds)
    {
        if (seconds > 0f) yield return new WaitForSeconds(seconds);
    }

    private static void VoiceExistsPostfix(string variation, ref bool __result)
    {
        var self = _current;
        if (self?._session != null) __result = self.Context.Get<CustomVoiceFeature>()?.HasLine(variation) == true;
    }

    private static void VideoFilePrefix(ref string moviePath)
    {
        var self = _current;
        if (self == null || moviePath == null) return;
        var name = Path.GetFileNameWithoutExtension(moviePath);
        if (!name.StartsWith(Marker, StringComparison.Ordinal)) return;
        var file = self.VideoFile(name);
        if (file != null) moviePath = file.Replace('\\', '/');
    }

    private static bool PlayAndWaitPrefix(string path, ref IEnumerator __result)
    {
        var self = _current;
        if (self?._session == null) return true;
        if (!string.IsNullOrEmpty(path) && (!path.StartsWith(Marker, StringComparison.Ordinal) || self.VideoFile(path) != null)) return true;
        __result = Pause(0f);
        return false;
    }

    private static void PlayAndWaitPostfix(string path, ref IEnumerator __result)
    {
        var self = _current;
        if (self?._session != null && path != null && path.StartsWith(Marker, StringComparison.Ordinal)) __result = self.Watchdog(__result, path);
    }

    /// <summary>A video that never starts (broken file, unsupported codec) is stopped after a few seconds.</summary>
    private IEnumerator Watchdog(IEnumerator original, string path)
    {
        var start = Time.realtimeSinceStartup;
        var started = false;
        while (original.MoveNext())
        {
            if (!started)
            {
                var player = Player();
                started = player != null && (player.isPlaying && player.time > 0.05);
                if (!started && Time.realtimeSinceStartup - start > 6f && !(UIManager.Instance != null && UIManager.Instance.gamePaused))
                {
                    Log.Warn("Video " + path + " did not start, skipped (use H.264 .mp4)");
                    SVideoController.Instance.Stop();
                }
            }

            yield return original.Current;
        }
    }

    private static VideoPlayer? Player()
    {
        var controller = SVideoController.Instance;
        var pc = controller == null ? null : AccessTools.Field(typeof(SVideoController), "videoPlayer").GetValue(controller);
        return pc == null ? null : AccessTools.Field(typeof(VideoPlayerPC), "player").GetValue(pc) as VideoPlayer;
    }

    private static bool TrophyPrefix() => _current?._session == null;
}
