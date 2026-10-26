using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Beardshaker.BeardBase.AssetBundleManager;
using HarmonyLib;
using Microsoft.Win32;
using Steamworks;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Plays the other game's 30 shows as shows 31-60, read
/// straight from the other game's install, never copied. Offered only when the player has that game:
/// Steam ownership plus install when Steam answers, otherwise Steam's local library data (app manifest)
/// plus the installed files. Question and voice bundles and question videos are redirected while such a
/// show runs; progress of shows 31-60 lives in its own save file, so the game's own save never contains
/// show ids it cannot load.
/// </summary>
public sealed class CatalogueFeature : FeatureBase
{
    public const string FeatureId = "Catalogue";
    public const int HostShows = 30;

    private static CatalogueFeature? _current;
    private static Func<AssetBundleRecord, IAssetBundleLoad>? _originalLoad;
    private IConfigValue<bool> _enabled = null!;
    private ModUi _ui = null!;
    private bool _registerExtended;
    private bool _foreignShow;

    public override string Id => FeatureId;

    public override string DisplayName => "Other game's questions";

    public override string Description => "The other game's 30 shows as shows 31-60, if you own and installed it.";

    protected override string LogCategoryName => "Mods";

    /// <summary>Steam app id and display name of the other game (null until detected).</summary>
    public OtherGame? Other { get; private set; }

    /// <summary>Why the other catalogue is not offered (empty when it is).</summary>
    public string Status { get; private set; } = "Wird beim Start geprüft.";

    public bool Active => _enabled.Value && Other != null && _registerExtended;

    public IConfigValue<bool> Enabled => _enabled;

    /// <summary>A show of the other game is loaded right now.</summary>
    public bool ForeignShowActive => _foreignShow;

    /// <summary>Question videos played from the other game since start (diagnostics, self-test).</summary>
    public int ForeignVideos { get; private set; }

    /// <summary>File name of the separate progress file (without .bin).</summary>
    public string CatalogueSaveFile => SessionsFile;

    private string SessionsFile => "Sessions_Catalogue" + (Other?.AppId ?? 0);

    protected override void OnStart()
    {
        _enabled = Context.Config.Bind("Catalogue", "OtherGameShows", true, "Offer the other game's 30 shows as shows 31-60 when you own and installed it (takes effect at the next start).");
        _ui = ModUi.For(Context);
        _current = this;
        Context.Provide(this);
        var none = ModCore.Capabilities.GameCapability.None;
        _ui.Patch(new PatchSpec("mods.catalogue.detect", "Check ownership after Steam started; route show bundles of shows 31-60 to the other game.",
            () => AccessTools.Method(typeof(SAssetBundleManager), "InitializeAOCContent"), none)
        {
            Postfix = AccessTools.Method(typeof(CatalogueFeature), nameof(InitializeAocPostfix)),
        });
        _ui.Patch(new PatchSpec("mods.catalogue.variants", "Map shows 31-60 to the other game's bundle variants 1-30.",
            () => AccessTools.Method(typeof(AssetBundleManager), "SetVariants"), none)
        {
            Prefix = AccessTools.Method(typeof(CatalogueFeature), nameof(SetVariantsPrefix)),
        });
        _ui.Patch(new PatchSpec("mods.catalogue.video", "Question videos of shows 31-60 come from the other game.",
            () => AccessTools.Method(typeof(VideoPlayerPC), "Play"), none)
        {
            Prefix = AccessTools.Method(typeof(CatalogueFeature), nameof(VideoPrefix)),
        });
        SessionFiles.Register(_ui, HostShows + 1, 2 * HostShows, () => SessionsFile, () => Other != null);
    }

    protected override void OnStop() => _current = null;

    public override void Tick()
    {
        if (_registerExtended || Other == null || !_enabled.Value) return;
        var register = SAssetBundleManager.Instance != null ? SAssetBundleManager.Instance.GetShowRegister() : null;
        if (register == null || register.dataArray == null) return;
        ExtendRegister(register);
    }

    // ---- detection ----------------------------------------------------------------------------------

    public sealed class OtherGame
    {
        public OtherGame(uint appId, string name, string streamingAssets)
        {
            AppId = appId;
            Name = name;
            StreamingAssets = streamingAssets;
        }

        public uint AppId { get; }

        public string Name { get; }

        /// <summary>Absolute StreamingAssets folder of the other install (forward slashes, like Unity's).</summary>
        public string StreamingAssets { get; }
    }

    private void Detect()
    {
        var thisIsGame2 = Path.GetFileName(Application.dataPath).Equals("WWDS2_Data", StringComparison.OrdinalIgnoreCase);
        uint appId = thisIsGame2 ? 782130u : 1112380u;
        var name = thisIsGame2 ? "Spiel 1" : "Spiel 2";
        var dataFolder = thisIsGame2 ? "Wer Wei\u00DF Denn Sowas_Data" : "WWDS2_Data";
        bool? owned = null;
        string? dir = null;
        try
        {
            owned = SteamApps.BIsSubscribedApp(new AppId_t(appId));
            if (owned == true && SteamApps.GetAppInstallDir(new AppId_t(appId), out var steamDir, 1024) > 0) dir = steamDir;
        }
        catch (Exception ex)
        {
            Log.Info("Steam ownership check unavailable (" + ex.GetType().Name + "): using Steam's library data");
        }

        if (owned == false)
        {
            Status = name + " ist nicht in deiner Steam-Bibliothek.";
            return;
        }

        // Without a Steam answer the game must be installed through Steam (app manifest in a library).
        dir ??= SteamLibraryInstallDir(appId);
        if (dir == null)
        {
            Status = owned == true ? name + " ist nicht installiert." : name + " wurde nicht in deinen Steam-Bibliotheken gefunden.";
            return;
        }

        var streaming = Path.Combine(Path.Combine(dir, dataFolder), "StreamingAssets");
        if (!File.Exists(Path.Combine(Path.Combine(Path.Combine(streaming, "Bundles"), "show"), "data.1")))
        {
            Status = name + ": Spieldateien unvollständig (" + streaming + ").";
            return;
        }

        Other = new OtherGame(appId, name, streaming.Replace('\\', '/'));
        Status = string.Empty;
        Log.Info("Other catalogue available: " + name + " (" + (owned == true ? "Steam ownership confirmed" : "Steam library data") + ") at " + streaming);
    }

    /// <summary>Install folder of a Steam app from Steam's local library data (registry, libraryfolders.vdf, appmanifest).</summary>
    internal static string? SteamLibraryInstallDir(uint appId)
    {
        var roots = new List<string>();
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
        {
            foreach (var value in new[] { "SteamPath", "InstallPath" })
            {
                try
                {
                    if (Registry.GetValue(key, value, null) is string s && s.Length > 0) roots.Add(s.Replace('/', '\\'));
                }
                catch (Exception)
                {
                    // Registry not readable: try the next key.
                }
            }
        }

        var libraries = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var vdf = Path.Combine(Path.Combine(root, "steamapps"), "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            libraries.Add(root);
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var acf = Path.Combine(Path.Combine(lib, "steamapps"), "appmanifest_" + appId + ".acf");
            if (!File.Exists(acf)) continue;
            var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
            if (!m.Success) continue;
            var dir = Path.Combine(Path.Combine(Path.Combine(lib, "steamapps"), "common"), m.Groups[1].Value);
            if (Directory.Exists(dir)) return dir;
        }

        return null;
    }

    private static void InitializeAocPostfix()
    {
        PatchGuard.Run("mods.catalogue.detect", () =>
        {
            var self = _current;
            if (self == null) return;
            if (!self._enabled.Value)
            {
                self.Status = "Ausgeschaltet.";
                return;
            }

            self.Detect();
            if (self.Other == null || _originalLoad != null) return;
            _originalLoad = AssetBundleRecord.LoadInjection;
            AssetBundleRecord.LoadInjection = Load;
        });
    }

    // ---- register: shows 31-60 ----------------------------------------------------------------------

    private void ExtendRegister(ShowRegister register)
    {
        _registerExtended = true;
        if (register.dataArray.Any(r => r.ID > HostShows)) return;
        var rows = register.dataArray.ToList();
        for (var n = 1; n <= HostShows; n++)
        {
            rows.Add(new ShowRegisterData { ID = HostShows + n, Datafilename = "Show" + n, Aocsteam = 0 });
        }

        register.dataArray = rows.ToArray();
        Log.Info("Shows " + (HostShows + 1) + "-" + (2 * HostShows) + " come from " + Other!.Name);
    }

    // ---- loading redirects --------------------------------------------------------------------------

    private static IAssetBundleLoad Load(AssetBundleRecord record)
    {
        var self = _current;
        var original = _originalLoad!;
        if (self == null || !self._foreignShow || self.Other == null || !record.BaseName.StartsWith("show/", StringComparison.Ordinal)) return original(record);
        var load = new ForeignBundleLoad(record, self.Other.StreamingAssets);
        return File.Exists(load.Path) ? load : original(record);
    }

    private static void SetVariantsPrefix(ref string[] variants)
    {
        var self = _current;
        if (self == null || variants == null || variants.Length == 0) return;
        if (!int.TryParse(variants[0], out var show)) return;
        self._foreignShow = self.Active && show > HostShows && show <= 2 * HostShows;
        if (!self._foreignShow) return;
        variants = new[] { (show - HostShows).ToString() }.Concat(variants.Skip(1)).ToArray();
        self.Log.Info("Show " + show + " = " + self.Other!.Name + " show " + variants[0]);
    }

    private static void VideoPrefix(ref string moviePath)
    {
        var self = _current;
        if (self == null || !self._foreignShow || self.Other == null || moviePath == null) return;
        var file = Path.GetFileName(moviePath);
        if (!file.StartsWith("video_questionanswer_", StringComparison.OrdinalIgnoreCase)) return;
        var foreign = self.Other.StreamingAssets + "/" + file;
        if (!File.Exists(foreign)) return;
        moviePath = foreign;
        self.ForeignVideos++;
        self.Log.Info("Video from " + self.Other.Name + ": " + file);
    }

    /// <summary>Same as AssetBundleLoadFile, rooted at the other game's StreamingAssets.</summary>
    private sealed class ForeignBundleLoad : IAssetBundleLoad
    {
        private readonly AssetBundleRecord _record;
        private AssetBundleCreateRequest? _load;

        public ForeignBundleLoad(AssetBundleRecord record, string streamingAssets)
        {
            _record = record;
            Path = streamingAssets + "/Bundles/" + record.BaseName + (record.IsVariantBundle ? "." + record.Variant : string.Empty);
        }

        public string Path { get; }

        public object? Current => null;

        public bool MoveNext()
        {
            if (_record.AssetBundle != null) return false;
            if (_load == null)
            {
                _load = AssetBundle.LoadFromFileAsync(Path);
                return true;
            }

            if (!_load.isDone) return true;
            if (_record.AssetBundle != null || _load.assetBundle == null) return false;
            _record.AssetBundle = _load.assetBundle;
            _record.IsLoaded = true;
            return false;
        }

        public void Reset()
        {
        }
    }
}
