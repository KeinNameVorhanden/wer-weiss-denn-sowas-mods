# Troubleshooting

| Symptom | Check | Fix |
|---|---|---|
| No `[WWDS Harness]` lines in `BepInEx/LogOutput.log` | Is BepInEx loading at all (`Chainloader startup complete`)? | Game 1 needs the x86 BepInEx, Game 2 x64. Reinstall with `tools/Install-ToGame.ps1`. |
| "compatibility unknown" warning | The game was updated (Assembly-CSharp hash changed). | Check the capability list in the log/overlay; disabled features list the failing patch. Report the new hash to add it to `WwdsProfiles`. |
| A feature shows `MissingCapabilities` | Overlay > Features, or the log line `Feature X: MissingCapabilities (game adapter lacks ...)` | A patch failed or a game member is missing; see Debug tab patch list. Individual patches can be switched off in `[Patches]`. |
| Game stops when alt-tabbing | Unity pauses unfocused games. | `[General] RunInBackground = true`. |
| Twitch: bridge says `rejected a connection: bad handshake` | Key mismatch | Start the bridge once so `%LOCALAPPDATA%\WWDS\bridge.key` exists, then restart the game. |
| Twitch: `No bridge key` warning | Bridge never started | Start `WWDS.Bridge.exe` once. |
| Twitch: `Could not subscribe to channel.subscribe` | Non-affiliate channel or missing scope | Expected for subs/bits on non-affiliates; other events still work. Run `login` again after scope changes. |
| Chat votes are counted but nothing happens in the game | `Game Action: NOT APPLIED (Twitch test mode)` in the log | Set `[Twitch] TestMode = false`. |
| Chat replies missing | `Chat: NOT SENT` lines | TestMode on, bridge `DryRun = true`, or IRC fallback (cannot send). |
| Audience opponent never takes over | Log: `No bot contestant in this show` | The game adds a bot only when a team is empty: play 1 human vs bot (or 2 humans in one team). |
| Votes on the master question are short | By design: the game clock is 20 s, the vote must close before. | `[Adapter] MasterDecisionTimeout`, `[Opponent] CategoryVoteSeconds`. |
| Renamed contestant shows the old name briefly | The game copies names into the UI at refresh points. | Names apply from the next screen (category frame). |
| Overlay does not open | Hotkey conflict | `[UI] PanelHotkey` (UnityEngine.KeyCode name, default F8). RuntimeUnityEditor uses F12. |
| Need to inspect game objects | | RuntimeUnityEditor (F12): object browser, inspector, REPL. |
| Need a raw trace of game events | | Install the diagnostics plugin (`tools/Deploy-Diagnostics.ps1`), traces go to `BepInEx/wwds-diagnostics/*.jsonl`. |

Logs never contain OAuth tokens (all log text passes a redactor) and do not log chat message text.
