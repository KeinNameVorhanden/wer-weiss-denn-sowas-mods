# Extending the harness

## A new feature ("NewFeature.cs")

```csharp
public sealed class QuestionStatsFeature : FeatureBase
{
    public override string Id => "QuestionStats";               // config key [Features] QuestionStats
    public override string DisplayName => "Question stats";
    public override GameCapability RequiredCapabilities => GameCapability.ReadQuestion;
    public override IEnumerable<string> Dependencies => new[] { "Audience" };   // optional

    protected override void OnStart()
    {
        Track(Context.Bus.Subscribe<QuestionStarted>(e => Log.Info("Q: " + e.Question.Text)));
        Track(Context.Bus.Subscribe<ChatMessageReceived>(e => { /* ... */ }));
        Track(Context.Bus.Subscribe<InteractionCompleted>(e => { /* VoteCompleted etc. */ }));
        Track(Context.Bus.Subscribe<RoundFinished>(e => { /* ... */ }));
    }
}
```

Register it from your own BepInEx plugin:

```csharp
[BepInPlugin("my.wwds.addon", "My WWDS addon", "1.0.0")]
[BepInDependency(WWDS.Host.BepInEx.HarnessPlugin.Guid)]
public sealed class MyAddon : BaseUnityPlugin
{
    private void Awake() => WWDS.Host.BepInEx.HarnessPlugin.AddFeature(new QuestionStatsFeature());
}
```

The feature appears in the overlay's Features tab and in `[Features]` of `wwds.harness.cfg`.

Rules for features:
- Main thread only. Use `Context.Scheduler.After(seconds, action)` for delays, never threads.
- Change the game only through `Context.Adapter` and only if `Context.Capabilities.Has(...)`.
- Audience-driven changes must call `Context.Get<GameActionGate>().Allow("what")` first.
- Chat output: `Context.Bus.Publish(new ChatSendRequested("text"))` (rate limited, test-mode aware).
- Chat commands: `Track(Context.Commands.Register(new CommandDefinition("cmd", Authority.Viewer, "help", handler)))`.

## Events you can subscribe to

Game: `GameLoaded, GameStateChanged, SessionCreated, GameStarted, RoundStarted, QuestionStarted,
AnswersPresented, AnswerLocked, BetLocked, AnswerRevealed, QuestionFinished, ScoreChanged,
PlayerNameChanged, RoundFinished, GameFinished, DecisionRequested, DecisionCompleted`.

Audience/Twitch: `AudienceConnectionChanged, ChatMessageReceived, ChatCommandReceived,
ChannelPointRedeemed, SubscriptionReceived, CheerReceived, RaidReceived, FollowReceived, PollUpdated,
InteractionOpened, InteractionLocked, InteractionCompleted, InteractionCancelled,
InteractionStatusChanged`.

## A new opponent behaviour

Implement `IOpponentBehavior` (Kind, UsesVotes, Choose, Estimate) and call
`OpponentBehaviors.Register(...)` with a new `OpponentBehaviorKind` value (extend the enum).

## A new binding action

```csharp
Track(Context.Get<BindingActions>()!.Register("mything.do", (args, evt) => ActionResult.Success()));
```

Then use it in `BepInEx/wwds/bindings.json`: `{ "on": "CheerReceived", "when": { "Bits": ">=100" }, "do": "mything.do" }`.
Conditions: `=x` / plain value (equals, case-insensitive), `!=x`, `~part` (contains), `>`, `>=`, `<`, `<=`.
Placeholders in args: `{Viewer.DisplayName}`, `{Bits}`, any public property path of the event.

## A new game

1. Add a `GameProfile` to `WwdsProfiles` style list (process name, data folder, validated hash).
2. Implement `IGameAdapter` in a new `WWDS.Game.<Name>` project using `PatchRegistry` for patches.
3. Select it in `HarnessPlugin.Boot` by `detection.Profile.Id`.

## Custom questions (extension point, not built)

Static analysis found a clean injection point: postfix on
`Beardshaker.BeardBase.AssetBundleManager.AssetBundleGetAsset.GetAsset()` replacing
`ShowContent.dataArray` (14 rows: 12 normal, master, bonus; 3 options; `ID == index + 1`). Constraints:
reuse an existing show slot 1..30 and give every normal question a playable answer video, otherwise
the reveal waits forever. The `ReplaceQuestions` capability stays withheld until such a pack is
validated in-game.
