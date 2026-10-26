using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Own voice lines: a .wav or .ogg in the voices folder whose file name is a line's name replaces that
/// line (speech_kp_funfact.ogg, speech_kp_delay_comment_3.wav, ...). Show lines repeat their names in
/// every show, so a file inside a folder named "show12" only replaces the line of show 12; files outside
/// such a folder apply everywhere. Files are loaded once at start (and on "reload"); the game waits for
/// and lip-syncs a replacement of any length. "Export list" writes every line name to the folder.
/// </summary>
public sealed class CustomVoiceFeature : FeatureBase
{
    public const string FeatureId = "CustomVoice";
    private const string ReadmeName = "LIESMICH.txt";
    private const string ListName = "_alle-saetze.txt";

    private static CustomVoiceFeature? _current;
    private readonly Dictionary<string, AudioClip> _global = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioClip> _perShow = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pending> _loading = new();
    private IConfigValue<bool> _enabled = null!;

    public override string Id => FeatureId;

    public override string DisplayName => "Own voice lines";

    public override string Description => "Replace any host, captain or contestant line with your own .wav/.ogg file from the voices folder.";

    protected override string LogCategoryName => "Mods";

    public string Folder { get; private set; } = string.Empty;

    /// <summary>Number of replacement lines loaded (global and per show).</summary>
    public int Count => _global.Count + _perShow.Count;

    public bool Loading => _loading.Count > 0;

    protected override void OnStart()
    {
        _enabled = Context.Config.Bind("Voice", "OwnLines", true, "Use own voice lines from BepInEx/wwds/voices (file name = line name, folder showN = only that show).");
        var dir = Context.Host?.DataDirectory;
        if (dir == null) return;
        Folder = Path.Combine(dir, "voices");
        _current = this;
        Context.Provide(this);
        var ui = ModUi.For(Context);
        var none = ModCore.Capabilities.GameCapability.None;
        var t = typeof(SSoundManager);
        ui.Patch(new PatchSpec("mods.own-voice.bundle", "Play an own file instead of a show voice line.",
            () => AccessTools.Method(t, nameof(SSoundManager.PlayVoiceFileFromAssetBundleAndWait), new[] { typeof(EVoiceType), typeof(string) }), none)
        {
            Prefix = AccessTools.Method(typeof(CustomVoiceFeature), nameof(BundlePrefix)),
        });
        ui.Patch(new PatchSpec("mods.own-voice.contains", "Own files count as existing show lines (captain comments, ...).",
            () => AccessTools.Method(t, nameof(SSoundManager.AssetBundleContainsSound), new[] { typeof(EVoiceType), typeof(string) }), none)
        {
            Postfix = AccessTools.Method(typeof(CustomVoiceFeature), nameof(ContainsPostfix)),
        });
        ui.Patch(new PatchSpec("mods.own-voice.resource", "Play an own file instead of a named generic line.",
            () => AccessTools.Method(t, nameof(SSoundManager.PlaySoundFileFromResource)), none)
        {
            Prefix = AccessTools.Method(typeof(CustomVoiceFeature), nameof(ResourcePrefix)),
        });
        ui.Patch(new PatchSpec("mods.own-voice.pool", "Swap a randomly picked generic line for its own file.",
            () => AccessTools.Method(t, "GetSoundFromAudioPool"), none)
        {
            Postfix = AccessTools.Method(typeof(CustomVoiceFeature), nameof(PoolPostfix)),
        });
        Reload();
    }

    protected override void OnStop()
    {
        _current = null;
        Clear();
    }

    public override void Tick()
    {
        for (var i = _loading.Count - 1; i >= 0; i--)
        {
            var p = _loading[i];
            if (!p.Request.isDone) continue;
            _loading.RemoveAt(i);
            try
            {
                if (p.Request.isNetworkError || p.Request.isHttpError) throw new IOException(p.Request.error);
                var clip = DownloadHandlerAudioClip.GetContent(p.Request);
                if (clip == null || clip.length <= 0f) throw new IOException("no audio");
                clip.name = p.Name;
                (p.Show > 0 ? _perShow : _global)[Key(p.Show, p.Name)] = clip;
            }
            catch (Exception ex)
            {
                Log.Warn("Own voice line " + p.Path + " could not be loaded: " + ex.Message);
            }
            finally
            {
                p.Request.Dispose();
            }

            if (_loading.Count == 0) Log.Info("Own voice lines: " + Count + " loaded from " + Folder);
        }
    }

    /// <summary>Reads the voices folder again.</summary>
    public void Reload()
    {
        Clear();
        try
        {
            Directory.CreateDirectory(Folder);
            WriteReadme();
            foreach (var path in Directory.GetFiles(Folder, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var type = ext == ".wav" ? AudioType.WAV : ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.UNKNOWN;
                if (type == AudioType.UNKNOWN)
                {
                    if (ext == ".mp3") Log.Warn("Own voice line " + path + ": mp3 is not supported, use .ogg or .wav");
                    continue;
                }

                var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
                request.SendWebRequest();
                _loading.Add(new Pending(path, Path.GetFileNameWithoutExtension(path), ShowOf(path), request));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Voices folder could not be read: " + ex.Message);
        }
    }

    /// <summary>Writes the name of every voice line the game has (generic lines and the loaded show's lines).</summary>
    public string ExportList()
    {
        var lines = new List<string> { "# Generische Sätze (gelten in jeder Show)" };
        var clips = Resources.LoadAll<AudioClip>("Sounds/voice");
        lines.AddRange(clips.Select(c => c.name).Distinct().OrderBy(n => n, StringComparer.Ordinal));
        var show = SDataAccess.ActiveSession?.ActiveShowIndex ?? 0;
        foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles().Where(b => b.name.IndexOf("voice", StringComparison.OrdinalIgnoreCase) >= 0).ToList())
        {
            lines.Add(string.Empty);
            lines.Add("# Show-Sätze aus " + bundle.name + (show > 0 ? " (Ordner show" + show + ")" : string.Empty));
            lines.AddRange(bundle.GetAllAssetNames().Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal));
        }

        Resources.UnloadUnusedAssets();
        var file = Path.Combine(Folder, ListName);
        File.WriteAllLines(file, lines.ToArray());
        return file;
    }

    private void Clear()
    {
        foreach (var p in _loading) p.Request.Dispose();
        _loading.Clear();
        foreach (var clip in _global.Values.Concat(_perShow.Values)) UnityEngine.Object.Destroy(clip);
        _global.Clear();
        _perShow.Clear();
    }

    private void WriteReadme()
    {
        var path = Path.Combine(Folder, ReadmeName);
        if (File.Exists(path)) return;
        File.WriteAllLines(path, new[]
        {
            "Eigene Sprachausgabe fuer Wer weiss denn sowas",
            string.Empty,
            "Lege .ogg- oder .wav-Dateien in diesen Ordner. Der Dateiname ist der Name des Satzes, der ersetzt wird,",
            "z. B. speech_kp_funfact.ogg. Alle Namen: Mod-Menue > Inhalte > Satzliste exportieren (" + ListName + ").",
            string.Empty,
            "Saetze einer Show (speech_kp_question_..., speech_kp_category_..., Kapitaene, Kandidaten) heissen in jeder",
            "Show gleich. In einen Unterordner show12 gelegt, gelten sie nur fuer Show 12; ausserhalb fuer alle Shows.",
            "Andere Unterordner zum Sortieren sind erlaubt.",
            string.Empty,
            "Nach dem Ablegen: Mod-Menue > Inhalte > Eigene Saetze neu laden (oder Spiel neu starten).",
        });
    }

    private static int ShowOf(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("show", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.Substring(4), out var id) && id > 0) return id;
            dir = Path.GetDirectoryName(dir);
        }

        return 0;
    }

    private static string Key(int show, string name) => show > 0 ? show + "/" + name : name;

    private AudioClip? Find(string name, bool showLine)
    {
        if (!_enabled.Value || string.IsNullOrEmpty(name)) return null;
        if (showLine && SDataAccess.ActiveSession is { } s && _perShow.TryGetValue(Key(s.ActiveShowIndex, name), out var clip)) return clip;
        return _global.TryGetValue(name, out var any) ? any : null;
    }

    /// <summary>An own file replaces this show line.</summary>
    public bool HasLine(string name) => Find(name, showLine: true) != null;

    private static bool BundlePrefix(EVoiceType voiceType, string variation, ref IEnumerator __result, bool __runOriginal)
    {
        if (!__runOriginal) return false;
        var clip = _current?.Find(variation, showLine: true);
        if (clip == null) return true;
        __result = Play(voiceType, clip);
        return false;
    }

    private static IEnumerator Play(EVoiceType speaker, AudioClip clip)
    {
        var source = SSoundManager.Instance.GetVoiceAudioSource(speaker);
        source.loop = false;
        source.clip = clip;
        source.Play();
        yield return new WaitForSeconds(clip.length);
    }

    private static void ContainsPostfix(string variation, ref bool __result)
    {
        if (!__result && _current?.Find(variation, showLine: true) != null) __result = true;
    }

    private static bool ResourcePrefix(string fileName, ref AudioSource source, bool loop, ref bool __result)
    {
        var clip = _current?.Find(fileName, showLine: false);
        if (clip == null || source == null) return true;
        source.clip = clip;
        source.loop = loop;
        source.Play();
        __result = true;
        return false;
    }

    private static void PoolPostfix(ref AudioClip audioClip, bool __result)
    {
        if (!__result || audioClip == null) return;
        var clip = _current?.Find(audioClip.name, showLine: false);
        if (clip != null) audioClip = clip;
    }

    private sealed class Pending
    {
        public Pending(string path, string name, int show, UnityWebRequest request)
        {
            Path = path;
            Name = name;
            Show = show;
            Request = request;
        }

        public string Path { get; }

        public string Name { get; }

        public int Show { get; }

        public UnityWebRequest Request { get; }
    }
}
