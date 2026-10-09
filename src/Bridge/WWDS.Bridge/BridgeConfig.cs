using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WWDS.Bridge;

/// <summary>Bridge settings in %LOCALAPPDATA%\WWDS\bridge.json. Contains no secrets.</summary>
public sealed class BridgeConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Client ID of the streamer's own Twitch application (dev.twitch.tv/console, public client).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Localhost port the game connects to.</summary>
    public int Port { get; set; } = WWDS.Protocol.ProtocolInfo.DefaultPort;

    /// <summary>Never post in chat; log what would have been sent.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Start without Twitch and only serve the mock audience.</summary>
    public bool MockOnly { get; set; }

    /// <summary>EventSub subscriptions to create (Twitch type names). Unknown types are ignored.</summary>
    public List<string> Events { get; set; } = new()
    {
        "channel.chat.message",
        "channel.channel_points_custom_reward_redemption.add",
        "channel.subscribe",
        "channel.subscription.gift",
        "channel.subscription.message",
        "channel.cheer",
        "channel.raid",
        "channel.follow",
        "channel.poll.begin",
        "channel.poll.progress",
        "channel.poll.end",
        "channel.update",
        "channel.prediction.begin",
        "channel.prediction.progress",
        "channel.prediction.lock",
        "channel.prediction.end",
    };

    public static BridgeConfig Load(BridgePaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
        {
            var fresh = new BridgeConfig();
            fresh.Save(paths);
            return fresh;
        }

        return JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(paths.ConfigFile), Json) ?? new BridgeConfig();
    }

    public void Save(BridgePaths paths) => File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(this, Json));
}

public sealed class BridgePaths
{
    public BridgePaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WWDS");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, "bridge.json");

    /// <summary>Shared secret the game must present (read by the in-game harness).</summary>
    public string KeyFile => Path.Combine(Root, "bridge.key");

    /// <summary>OAuth tokens, encrypted with Windows DPAPI for the current user.</summary>
    public string TokenFile => Path.Combine(Root, "tokens.dat");

    public string EnsureKey()
    {
        if (File.Exists(KeyFile))
        {
            var existing = File.ReadAllText(KeyFile).Trim();
            if (existing.Length >= 32) return existing;
        }

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(KeyFile, key);
        return key;
    }
}
