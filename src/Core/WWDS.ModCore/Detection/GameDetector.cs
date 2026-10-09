using System;
using System.Collections.Generic;
using WWDS.ModCore.Model;

namespace WWDS.ModCore.Detection;

/// <summary>A game build the adapter was developed and validated against.</summary>
public sealed class KnownBuild
{
    public KnownBuild(string label, string unityVersion, string assemblySha256, int steamBuildId)
    {
        Label = label;
        UnityVersion = unityVersion;
        AssemblySha256 = assemblySha256.ToUpperInvariant();
        SteamBuildId = steamBuildId;
    }

    public string Label { get; }

    public string UnityVersion { get; }

    /// <summary>SHA-256 of Assembly-CSharp.dll (upper-case hex).</summary>
    public string AssemblySha256 { get; }

    public int SteamBuildId { get; }
}

public sealed class GameProfile
{
    public GameProfile(GameId id, string displayName, int steamAppId, string dataFolderName, string processName, string saveFolderName, IList<KnownBuild> knownBuilds)
    {
        Id = id;
        DisplayName = displayName;
        SteamAppId = steamAppId;
        DataFolderName = dataFolderName;
        ProcessName = processName;
        SaveFolderName = saveFolderName;
        KnownBuilds = knownBuilds;
    }

    public GameId Id { get; }

    public string DisplayName { get; }

    public int SteamAppId { get; }

    public string DataFolderName { get; }

    public string ProcessName { get; }

    /// <summary>Folder under %USERPROFILE%\AppData\LocalLow\BitComposer.</summary>
    public string SaveFolderName { get; }

    public IList<KnownBuild> KnownBuilds { get; }
}

public enum Compatibility
{
    /// <summary>Not one of the supported games.</summary>
    Unsupported,

    /// <summary>Supported game, but this exact build was never validated: features degrade per patch.</summary>
    UnknownBuild,

    /// <summary>Exact build the harness was validated against.</summary>
    Verified,
}

public sealed class DetectionResult
{
    public DetectionResult(GameProfile? profile, KnownBuild? build, Compatibility compatibility, string message)
    {
        Profile = profile;
        Build = build;
        Compatibility = compatibility;
        Message = message;
    }

    public GameProfile? Profile { get; }

    public KnownBuild? Build { get; }

    public Compatibility Compatibility { get; }

    public string Message { get; }

    public GameId Game => Profile?.Id ?? GameId.Unknown;
}

public static class WwdsProfiles
{
    public static readonly GameProfile Game1 = new(
        GameId.Wwds1,
        "Wer weiß denn sowas? - Das Spiel",
        782130,
        "Wer Weiß Denn Sowas_Data",
        "Wer Weiß Denn Sowas",
        "Wer Weiß Denn Sowas",
        new List<KnownBuild>
        {
            new("Steam build 4288409 (x86)", "2018.2.11f1", "7875990E540CB8E0872D9B7708513E6C447A524889496C03A7361899236645E4", 4288409),
        });

    public static readonly GameProfile Game2 = new(
        GameId.Wwds2,
        "Wer weiß denn sowas? - Das 2. Spiel",
        1112380,
        "WWDS2_Data",
        "WWDS2",
        "WWDS2",
        new List<KnownBuild>
        {
            new("Steam build 4278910 (x64)", "2018.2.20f1", "19ED10B06B76CD5D205F6A200AE6D5E97F14C4FC7421C8854D4BE304DFCAC02F", 4278910),
        });

    public static IList<GameProfile> All => new[] { Game1, Game2 };
}

/// <summary>
/// Decides which game the harness runs in and whether the exact build is validated. Identity comes
/// from the process / data folder name; compatibility from the Assembly-CSharp hash, so a game update
/// is reported as <see cref="Compatibility.UnknownBuild"/> instead of being silently assumed to work.
/// </summary>
public static class GameDetector
{
    public static DetectionResult Detect(string processName, string dataFolderName, string? assemblySha256, string unityVersion, IList<GameProfile>? profiles = null)
    {
        profiles ??= WwdsProfiles.All;
        GameProfile? profile = null;
        foreach (var p in profiles)
        {
            if (Same(p.ProcessName, processName) || Same(p.DataFolderName, dataFolderName))
            {
                profile = p;
                break;
            }
        }

        if (profile == null)
        {
            return new DetectionResult(null, null, Compatibility.Unsupported, "Unsupported game: process '" + processName + "', data '" + dataFolderName + "'.");
        }

        var hash = (assemblySha256 ?? string.Empty).ToUpperInvariant();
        foreach (var build in profile.KnownBuilds)
        {
            if (build.AssemblySha256 == hash)
            {
                return new DetectionResult(profile, build, Compatibility.Verified, profile.DisplayName + ", " + build.Label + ", Unity " + unityVersion + ": verified.");
            }
        }

        var expected = profile.KnownBuilds.Count > 0 ? profile.KnownBuilds[0] : null;
        var message = profile.DisplayName + ": this harness was developed against "
            + (expected == null ? "no recorded build" : expected.Label + " (Unity " + expected.UnityVersion + ")")
            + ". Detected Unity " + unityVersion + ", Assembly-CSharp " + (hash.Length >= 16 ? hash.Substring(0, 16) : "unknown")
            + ". Compatibility: unknown. Each patch is checked individually; features whose patches fail are disabled.";
        return new DetectionResult(profile, null, Compatibility.UnknownBuild, message);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
