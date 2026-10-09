# Architecture

```
                 WWDS.Bridge  (.NET 8, separate process, optional)
                 TwitchLib EventSub + Helix, OAuth device flow, DPAPI tokens, mock audience
                          |  127.0.0.1 TCP, newline-delimited JSON (WWDS.Protocol), shared key
------------------------- game process (Unity 2018.2, legacy Mono, net35) --------------------------
 WWDS.Host.BepInEx   BaseUnityPlugin: config/log backends, Update/OnGUI pump, game detection,
       |             adapter selection, feature registration
 WWDS.ModCore        EventBus, FeatureRegistry, CapabilitySet, CommandRegistry, Scheduler,
       |             MainThreadQueue, GameDetector, IGameAdapter, DecisionRequest, GameActionGate
 WWDS.Runtime        PatchRegistry (isolated, documented, switchable Harmony patches), UnityClock
 WWDS.Game.Wwds      shared adapter for both games (WwdsAdapter, WwdsHooks, WwdsDecisions)
 WWDS.Twitch         TwitchFeature: transports (bridge client, anonymous IRC fallback, loopback),
                     TwitchEventAdapter (badges -> authority), chat command dispatch, chat output
 WWDS.Audience       AudienceFeature (engine, moderation, !vote/!name/!guess/!wwds), Voting, Naming,
                     Guessing, TwitchOpponent, Bindings, Statistics, AudienceSimulator
 WWDS.UI             OverlayFeature: control panel (F8); the vote window (F9) is VoteHud in WWDS.Game.Wwds.Mods
 WWDS.Features       DebugToolsFeature: programmatic shows, auto decisions, console commands
 WWDS.Diagnostics    standalone probe plugin (JSONL traces, bot-vs-bot autopilot), not shipped by default
```

## Layering rules

| Rule | Where it is enforced |
|---|---|
| Twitch knows nothing about the game | `WWDS.Twitch` references only ModCore and Protocol |
| Audience features see the game only through `IGameAdapter` + capabilities | `WWDS.Audience` has no Unity/game reference |
| Audience input never mutates game state directly | every audience-driven adapter call goes through `GameActionGate.Allow` (test mode) and capability checks |
| Game state is touched only on the Unity main thread | transports post into `MainThreadQueue`; features tick from the host's `Update` |
| One failing module never takes others down | `FeatureRegistry` isolates Start/Tick exceptions, `PatchGuard` wraps patch bodies, `EventBus` isolates handlers |
| Loader-specific code is in one assembly | only `WWDS.Host.BepInEx` references BepInEx config/logging APIs (Harmony is used via `WWDS.Runtime`) |

## Game adapter

Both games share one engine (`Assembly-CSharp` identical except two types), so `WwdsAdapter` serves
both; `GameProfile` (ModCore `WwdsProfiles`) carries per-game identity: Steam app id, data folder,
save folder, validated Assembly-CSharp SHA-256 and Steam build id. A different hash is reported as
"compatibility unknown" and every patch is still verified individually.

Capabilities are granted only when the patches and private members backing them were found; the
list and reasons are logged at startup and shown in the overlay. See `docs/compatibility.md`.

### External control (Twitch opponent)

The game's only programmable contestant is its single bot (`PlayerAgentBot`). When a team is under
external control, the adapter replaces the bot's decision coroutines (category, answer, master answer,
bet, estimate) with coroutines that publish a `DecisionRequest` and wait. Whoever resolves it (the
opponent feature's audience vote, a moderator override, debug tools) decides; on timeout the request's
fallback or the game's own bot rule applies. The master question deadline (20 s game clock) is
respected by shortening the vote.

## Twitch path

```
Twitch EventSub/Helix --(TwitchLib, bridge)--> protocol messages --TCP--> BridgeClientTransport
anonymous IRC (fallback) -----------------------------------------------> IrcFallbackTransport
                                   (background threads)  --> MainThreadQueue
TwitchFeature: TwitchEventAdapter -> ChatMessageReceived / ChannelPointRedeemed / ... on EventBus
             -> CommandRegistry (authority from badges, trusted users) -> audience features
audience features -> InteractionEngine -> result -> GameActionGate -> IGameAdapter -> game
```

Secrets stay in the bridge (DPAPI-encrypted token file); the game process only knows a random local
key that authenticates it to the bridge.

## Audience interaction engine

`Collecting -> Locked -> Evaluating -> Applying -> Complete` (or `Cancelled`/`Failed`), driven by the
host tick. Supports duration, early completion, quorum with retries, cooldown per kind, moderator
override (`force`), cancel, lock, reopen, extend, clear, allow-lists (selected viewer), per-viewer
flood limits and weighted votes. Kinds: `VoteInteraction` (options), `NumberVoteInteraction` (median),
`NamingInteraction` (moderated suggestions), `GuessInteraction` (scored against a later outcome).

## Extending

See `docs/extending.md` for adding a feature, an opponent behaviour, a binding action or a new game.
A MelonLoader host would implement the same bootstrap as `HarnessPlugin` (config store, log sink,
`Update`/`OnGUI` pump) in a separate project; no other assembly needs to change.
