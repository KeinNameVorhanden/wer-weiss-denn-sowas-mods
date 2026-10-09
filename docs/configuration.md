# Configuration

All in-game settings live in `BepInEx/config/wwds.harness.cfg` (created on first start, every entry
documented inline; `docs/reference-config.cfg` is a full copy with defaults). Changes to most values
apply on the next show; feature switches apply after a restart or via the overlay's Features tab.
Bridge settings are in `%LOCALAPPDATA%\WWDS\bridge.json` (see `docs/twitch-setup.md`).

| Section | Purpose | Key settings |
|---|---|---|
| `[General]` | Harness basics | `DebugMode` (verbose log), `RunInBackground` |
| `[Features]` | One switch per module | `Twitch`, `Audience`, `Voting`, `Naming`, `Guessing`, `TwitchOpponent`, `Bindings`, `Statistics`, `Overlay`, `DebugTools` |
| `[Twitch]` | Transport and safety | `Mode` (Off/Bridge/Irc/BridgeWithIrcFallback/Mock), `Channel` (IRC), `BridgePort`, `CommandPrefix`, `TrustedUsers`, `TestMode`, `ChatMinIntervalSeconds`, `AnnounceInChat`, `AutoStartBridge` (start the bundled bridge hidden), `BridgePath` (empty = bundled) |
| `[Audience]` | Engine, commands, moderation | `VoteCommand`, `NameCommand`, `GuessCommand`, `ControlCommand`, `AcceptBareVotes`, `ModeratorOverride`, `SpamMaxPerWindow`, `SpamWindowSeconds`, `SubscriberVoteWeight`, `NameMinLength`, `NameMaxLength` (14 = game limit), `NameMaxRepeatedChars`, `NameAllowedSymbols`, `NameTitleCase`, `Denylist` (+ `BepInEx/wwds/denylist.txt`), `NumberAggregation` (Average/Median/TrimmedAverage for `!vote 1500`) |
| `[Voting]` | Answer votes on questions | `AutoOpen` (Off/HumanTurns/AllQuestions), `Mode` (ShowOnly/Assist), `VoteDuration`, `MasterVoteDuration`, `VoteChanging`, `MinimumVotes`, `TieBreakMethod` (Random/First/Fallback/Moderator/Revote), `Resolution` (Majority/WeightedRandom), `UseTwitchPolls`, `ShowBreakdownInChat` |
| `[Naming]` | Community names | `AutoOpen` (Off/BotContestant/BotTeam), `NameSubmissionDuration`, `MinimumSubmissions`, `SelectionMode` (Random/MostSubmitted/Weighted/Moderator), `ModeratorApproval`, `AllowChanges` |
| `[Guessing]` | Guess rounds | `GuessAnswers`, `GuessContestantCorrect`, `GuessWinner`, `GuessPrize`, `GuessDuration`, `AllowChanges`, `CloseWinnerAtFirstQuestion` |
| `[Opponent]` | Twitch as contestant | `Active`, `Mode` (Crowd/ChatTeam/SelectedViewer/RotatingViewers/Hybrid), `Behavior` (AudienceMajority/AudienceWeighted/Random/AlwaysA/RuleBased/HumanControlled/Hybrid), `DisplayName`, `TeamLabel`, `SelectedViewer`, `AnswerVoteSeconds`, `CategoryVoteSeconds`, `VirtualContestantSkill`, `MinimumVotes`, `AfkStrikes`, `AfkRetarget` (Off/Volunteers/ActiveChatters/VolunteersThenChatters), `ActiveChatterMinutes` |
| `[WhoWins]` | Bet on the winner at show start | `ChatBets`, `TwitchPrediction` (channel points), `WindowSeconds`, `StreamerLabel`, `PlayerLabel`, `AiLabel`, `TwitchLabel` |
| `[Adapter]` | Game-side deadlines for audience decisions | `AnswerDecisionTimeout`, `MasterDecisionTimeout` (keep below 20 s), `BetDecisionTimeout`, `EstimateDecisionTimeout`, `CategoryDecisionTimeout` |
| `[Statistics]` | Local statistics | `SaveToDisk` (BepInEx/wwds/statistics.json) |
| `[Game]` | General game settings (see [mods.md](mods.md)) | `ManageDisplay`, `Resolution` (e.g. 1920x1080), `WindowMode` (Fullscreen/Borderless/Windowed), `FrameRateLimit` (-1 = unlimited), `VSync`, `QualityLevel` (-1 = keep), `ApplySavedVolumesAtStart`, `MuteMusic`, `MuteSfx`, `MuteVoice`, `MasterVolume` (0-1), `MuteInBackground` |
| `[SkipIntro]` | Intro skipping | `SkipLogos`, `SkipShowIntro`, `NoTutorialPrompt` |
| `[Catalogue]` | Other game's shows as 31-60 | `OtherGameShows` (next start) |
| `[Quickplay]` | Configured line-up instead of the selection | `Enabled`, `TeamA1`, `TeamA2`, `TeamB1`, `TeamB2` (Empty/Keyboard/Gamepad1-4/Bot/Twitch), `TeamA1Character` ... (-1 = first free), `ShowId`, `CountdownSeconds`, `QuickResume` |
| `[Mouse]` | Mouse support | `Enabled`, `Owner` (KeyboardPlayer/ActivePlayer), `HideAfterSeconds` |
| `[EscMenu]` | ESC menu | `ReplacePauseMenu`, `Hotkey` (Escape), `ConfirmQuitDoor` |
| `[ModMenu]` | Mod menu | `Hotkey` (F10), `PadCombo` (Back + Start) |
| `[FirstRun]` | Welcome window | `Shown`, `ShowAtEveryStart`, `LastVersion` (update notice) |
| `[UI]` | Overlay, vote window, scale | `PanelHotkey` (F8), `PanelScale`, `MenuScale`, `HudScale`, `Toasts`, `HudHotkey` (F9), `AutoShowHud`, `ShowCorrectAnswerInPanel` (spoiler!), `StartOpen`, `DebugCycleTabsSeconds` (test aid) |
| `[Hud]` | Vote window | `X`, `Y` (position as share of the screen, -1 = top right), `Locked`, `Opacity`, `ResultSeconds`, `ShowCommandHint` |
| `[Keyboard]` | Show shortcuts | `ShowShortcuts` (Enter/Space = A, 1-3 answers, 1-5 master bets) |
| `[Debug]` | Test actions | `AutoStartShow`, `ShowId`, `TeamAHuman`, `TeamABotName`, `TeamBBotName`, `TeamBLabel`, `ExternalControlTeams`, `AutoResolve`, `AutoResolveDelay`, `AutoBetOption`, `AutoAssistHumans`, `QuitAfterQuestions`, `QuitAfterShow` |
| `[Patches]` | One switch per Harmony patch (troubleshooting after game updates) | e.g. `control.answer`, `name.persist-guard` |

## Event bindings

`BepInEx/wwds/bindings.json` maps events to actions without code (example file created on first
start). Format and available actions: `docs/extending.md`.

## Typical setups

Streamer plays alone, chat is the opponent:
```ini
[Twitch]
Mode = BridgeWithIrcFallback
TestMode = false
[Opponent]
Active = true
Mode = Crowd
[Naming]
AutoOpen = BotContestant
```

Chat helps the streamer (vote shown, streamer decides):
```ini
[Voting]
AutoOpen = HumanTurns
Mode = ShowOnly
```

Chat answers for the streamer:
```ini
[Voting]
AutoOpen = HumanTurns
Mode = Assist
```
