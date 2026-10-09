using WWDS.ModCore.Logging;

namespace WWDS.ModCore.Adapters;

/// <summary>
/// Single switch between "audience decides" and "audience is only observed". In test mode every
/// audience-driven game action is logged as NOT APPLIED instead of reaching the adapter. Features must
/// call <see cref="Allow"/> before any audience-driven adapter call.
/// </summary>
public sealed class GameActionGate
{
    private readonly ModLog _log;

    public GameActionGate(ModLog log) => _log = log;

    /// <summary>True while real-Twitch test mode is active (set by the Twitch feature).</summary>
    public bool DryRun { get; set; }

    public string Reason { get; set; } = "test mode enabled";

    public bool Allow(string action)
    {
        if (!DryRun) return true;
        _log.Info("Game Action: NOT APPLIED (" + Reason + "): " + action);
        return false;
    }
}
