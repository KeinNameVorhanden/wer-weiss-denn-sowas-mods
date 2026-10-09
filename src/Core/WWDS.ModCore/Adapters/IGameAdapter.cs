using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Model;

namespace WWDS.ModCore.Adapters;

/// <summary>
/// The only way features (Twitch, audience, UI) see or influence a game. Implementations translate
/// game internals into the normalized model and publish game events on the event bus. Every
/// mutating call is capability-gated and returns an <see cref="ActionResult"/> instead of throwing.
/// All members are main-thread only.
/// </summary>
public interface IGameAdapter
{
    GameId Game { get; }

    string DisplayName { get; }

    string AdapterVersion { get; }

    CapabilitySet Capabilities { get; }

    /// <summary>Applies patches and computes capabilities. Called once by the host.</summary>
    void Initialize(ModContext context);

    /// <summary>Per-frame work (deadline checks for pending decisions, state polling).</summary>
    void Tick();

    void Shutdown();

    GameSnapshot GetSnapshot();

    /// <summary>
    /// Hands the decisions of the bot in <paramref name="team"/> to the harness (published as
    /// <see cref="Events.DecisionRequested"/>). Turning it off returns control to the game's bot logic.
    /// </summary>
    ActionResult SetExternalControl(TeamSlot team, bool enabled);

    bool IsExternallyControlled(TeamSlot team);

    /// <summary>Renames a contestant for the current session only (never persisted to saves).</summary>
    ActionResult RenameContestant(TeamSlot team, int memberIndex, string name);

    /// <summary>Submits an answer for the contestant whose turn it is (audience assist for humans).</summary>
    ActionResult SubmitAnswer(TeamSlot team, int optionIndex);

    /// <summary>Categories that can still be picked in the current show (index = category slot).</summary>
    System.Collections.Generic.IList<DecisionOption> GetOpenCategories();

    /// <summary>Picks a category on behalf of the human whose turn it is (audience picks the category).</summary>
    ActionResult SelectCategory(TeamSlot team, int categoryIndex);

    /// <summary>Changes the team label shown in the player frames (session only).</summary>
    ActionResult RenameTeam(TeamSlot team, string? name);

    /// <summary>Starts a show from the main menu with the given line-up (debug, bot vs bot).</summary>
    ActionResult StartShow(ShowSetup setup);
}

public sealed class ShowSetup
{
    /// <summary>Show slot 1..30, or -1 for the game's next unplayed show.</summary>
    public int ShowId { get; set; } = -1;

    /// <summary>False = a local human on the active input device (debug / assist tests).</summary>
    public bool TeamAIsBot { get; set; } = true;

    public bool TeamBIsBot { get; set; } = true;

    public string? TeamABotName { get; set; }

    public string? TeamBBotName { get; set; }

    /// <summary>Do not write savegames / unlock achievements for this show.</summary>
    public bool SuppressPersistence { get; set; } = true;
}
