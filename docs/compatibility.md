# Compatibility matrix

Status values: **Verified** (tested in the running game), **Static** (patch target and
logic confirmed in decompiled code and the capability is granted, runtime test pending), **Withheld**
(capability deliberately not granted), **n/a**.

## Environment

| Item | Game 1 | Game 2 |
|---|---|---|
| Steam app / build | 782130 / 4288409 | 1112380 / 4278910 |
| Architecture | x86 | x64 |
| Unity | 2018.2.11f1 | 2018.2.20f1 |
| Scripting runtime | legacy Mono, .NET 3.5 profile | legacy Mono, .NET 3.5 profile |
| Assembly-CSharp SHA-256 | 7875990E...6645E4 | 19ED10B0...DFCAC02F |
| BepInEx 5.4.23.5 | Verified (x86 build) | Verified (x64 build) |
| BepInEx 6 / MelonLoader | not tested (see research report E) | not tested |
| RuntimeUnityEditor 6.3.2.1 | Verified (15/15 features) | Verified (15/15 features) |
| UnityExplorer 4.13.6 | not tested (optional alternative) | not tested |

## Capabilities

| Capability | Game 1 | Game 2 | Backing patches / members |
|---|---|---|---|
| ReadPhase | Verified | Verified | `state.*` (OnStateEnter of every flow state) |
| ReadQuestion / ReadAnswers / ReadCorrectAnswer | Verified | Verified | `question.selected`, `answers.normal`, `answers.master` |
| ReadScore | Verified | Verified | `score.add`, `score.bet`, `score.clear` |
| ReadContestants | Verified | Verified | `session.created` |
| ObserveAnswers | Verified | Verified | `answer.sent`, `bet.sent`, `reveal.*`, `question.finished` |
| RenameContestant (session only, never saved) | Verified | Verified | `session.created`, `name.persist-guard` |
| ControlBotAnswer | Verified | Verified | `control.answer` |
| ControlBotMasterAnswer | Verified | Verified | `control.master` |
| ControlBotBet | Verified | Verified | `control.bet` |
| ControlBotCategory | Verified | Verified | `control.category` |
| ControlBotEstimate | Static (same code) | Verified | `control.estimate` (needs a tie to occur) |
| SubmitAnswerForHuman | Static (same code) | Verified | `StopTimer` + `SendAnswer`, `ConfirmAnswerButton(Team,int)` |
| SelectCategoryForHuman | Static (same code) | Verified | `round.category`, `ViewCategoryUI.SelectCategory` |
| StartShow (bot shows, persistence suppressed) | Verified | Verified | `suppress.*`, `MainMenuController.Submit` |
| RenameTeam (player frames) | Verified (applied) | Verified (applied) | `team.label` |
| SecondBotInTeam | Withheld | Withheld | game creates at most one bot; untested |
| ReplaceQuestions | Withheld | Withheld | extension point only (answer video required per question) |

## Twitch

| Path | Status |
|---|---|
| Bridge handshake, protocol, chat commands, chat output (mock audience) | Verified |
| Opponent votes, naming via channel-point binding, guess rounds | Verified |
| EventSub via TwitchLib 0.8.0 (chat, redemptions, subs, gifts, resubs, cheers, raids, follows, polls, channel update) | Static (compiled against the real library, mapping unit-tested); live test needs the streamer's Client ID |
| OAuth device code flow, refresh, validation | Static (implemented per Twitch docs); live test needs a Client ID |
| Anonymous IRC fallback | Verified live (real channel: join, messages, badges) |
