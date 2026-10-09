# Twitch setup

The game's runtime (.NET 3.5 Mono) cannot speak modern Twitch APIs, so a small companion app,
`WWDS.Bridge`, connects to Twitch and forwards events to the game over `127.0.0.1`. Tokens never
enter the game process.

## 1. Register a Twitch application (once)

1. Open https://dev.twitch.tv/console/apps and create an application.
2. OAuth Redirect URL: `http://localhost` (not used by the device flow, but required by the form).
3. Category: Game Integration. **Client Type: Public** (no client secret needed).
4. Copy the Client ID.

### Building a release with your Client ID built in

So players do not have to register their own app, bake your app's Client ID into the release:

```bash
pwsh tools/Package-Release.ps1 -TwitchClientId yourclientid
```

or set `WwdsTwitchClientId` once (environment variable, or `<WwdsTwitchClientId>` in the
uncommitted `build/local.props`) and build as usual. The script prints `Twitch Client ID built in:`
with the first and last four characters; an ID that is not 20-40 lowercase letters and digits stops
the build.

- The app must be **Client Type: Public**: the device-code login needs no client secret. Never put a
  secret anywhere in the build.
- A Client ID is public by design (it appears in every Twitch login URL), so shipping it is safe. Each
  streamer still logs in with their own account; tokens stay on their PC.
- A Client ID entered in the mod menu or `bridge.json` takes precedence; the menu shows `eingebaut`
  while the built-in one is used.
- All Twitch API rate limits are per Client ID and per user token; requests made with user tokens (what
  the bridge does) are limited per user, so many streamers sharing one app is fine.

## 2. Log in

The bridge ships inside the mod (`BepInEx/plugins/WWDS/bridge/WWDS.Bridge.exe`, self-contained, no
.NET install needed). With `[Twitch] AutoStartBridge = true` (default) the game starts it hidden when
needed and stops it again on exit (it also exits when the game process ends). Its log is
`%LOCALAPPDATA%\WWDS\bridge.log`.

Log in from the game (mod menu, Twitch page): enter the Client ID, press Login, a browser opens
`twitch.tv/activate` and the menu shows the code. Log in **as the broadcaster** and confirm the code.
The game switches `[Twitch] Mode` to `BridgeWithIrcFallback` by itself.

Alternatively from a console: put the Client ID into `%LOCALAPPDATA%\WWDS\bridge.json`

```json
{
  "ClientId": "your-client-id",
  "Port": 47321,
  "DryRun": true,
  "MockOnly": false
}
```

and run

```bash
WWDS.Bridge.exe login
```

Requested scopes: `user:read:chat user:write:chat channel:read:redemptions
channel:read:subscriptions bits:read moderator:read:followers channel:manage:polls
channel:manage:predictions`. Tokens from an older version lack `channel:manage:predictions`: log out
and in again to use predictions. Tokens are stored encrypted with Windows DPAPI in `tokens.dat` (only
your Windows account can decrypt them). Logout (menu or `WWDS.Bridge.exe logout`) deletes them.

Access tokens are validated hourly and refreshed automatically (refresh tokens expire after 30 days
without use; then log in again).

## 3. Connect the game

In `BepInEx/config/wwds.harness.cfg`:

```ini
[Twitch]
Mode = BridgeWithIrcFallback
Channel = yourchannel
TestMode = true
AutoStartBridge = true
```

`AutoStartBridge = false` expects a bridge you started yourself (`WWDS.Bridge.exe` in a console:
`status` shows the Twitch state, `state` the game's current state). If port 47321 is already in use
the game connects to the running bridge instead of starting a second one.

## 4. Test mode first

With `TestMode = true` every chat command is parsed and logged, but audience decisions are
**not applied** to the game and chat replies are **not sent** (`Game Action: NOT APPLIED` in the log).
`DryRun = true` in `bridge.json` additionally blocks every chat message and poll at the bridge.
Switch both off when you are happy with what you see.

## Without Twitch: mock audience

- Bridge: `WWDS.Bridge.exe --mock`, then type e.g. `votes A=12 B=5 C=3`, `crowd 30 vote A B C`,
  `crowd 10 name QuizMaster "Elton Fan"`, `redeem bob "Name the Opponent"`, `cheer bob 500`,
  `raid friend 25` (`help` lists all). Mock mode is never gated by TestMode.
- In game: `[Twitch] Mode = Mock`, then use the overlay (F8) Debug tab buttons.

## Fallback

`Mode = Irc` (or the automatic fallback) reads chat anonymously over plain IRC (port 6667,
unencrypted, read-only). It delivers chat commands with badges (moderator/subscriber/broadcaster),
but no channel points, subs, cheers, raids or polls, and cannot post in chat.

## Chat commands

| Command | Who | Effect |
|---|---|---|
| `!vote A` / `!answer A` / plain `A` | everyone | vote in the running vote (one vote per viewer, changeable if enabled) |
| `!vote 1500` | everyone | number entry for the audience opponent's bonus estimate (`[Audience] NumberAggregation`: Average (default), Median or TrimmedAverage) |
| `!vote <x>` in other rounds | everyone | universal: with no vote open, `!vote` answers the running guess round (`!vote B` on a who-wins bet, `!vote 1500`) or supports naming suggestion number x (`!vote 2`) |
| `!name <text>` | everyone | suggest a name during a naming round |
| `!guess <value>` | everyone | guess round entry |
| `!join` | everyone | volunteer for RotatingViewers mode |
| `!stats`, `!top` | everyone | own statistics, guess leaderboard |
| `!crowd` | mods | audience takes over the bot contestant |
| `!opponent on/off/mode <m>/pick <login>/next/status` | mods | control the Twitch opponent; `pick` sets the viewer who plays (SelectedViewer mode), `next` retargets to the next volunteer or active chatter |
| `!bet` | mods | open the who-wins bet now (normally automatic at show start) |
| `!startvote`, `!startname <target> [s]`, `!startguess answer/winner/prize [s]` | mods | open interactions manually |
| `!wwds cancel/lock/reopen/clear/extend <s>/force <x>/approve <n>/ban <n>/pick <n>/off/on/status` | mods | moderate the running interaction |
| `!bindings` | mods | reload `bindings.json` |

## Bets and predictions

At every show start the `WhoWins` feature opens a bet on the winner. Each side is labelled by who
plays it: `Streamer (name)`, `Spieler 2 (name)`, `KI (name)` for the game's AI, `Twitch` or
`Twitch @viewer` for the audience opponent. `[WhoWins] ChatBets` (default on) uses `!vote A/B` with
leaderboard points; `[WhoWins] TwitchPrediction` (default off, affiliate/partner) additionally
starts a channel-points prediction, resolved with the winner or cancelled (points refunded) when
the show is aborted. Both close when the first question appears (`[Guessing] CloseWinnerAtFirstQuestion`;
the prediction is locked) and pay out at the end of the show. The vote window shows each option with
its text (who plays it). Answer polls stay in `[Voting] UseTwitchPolls`.

## Streamer against viewer

`[Opponent] Mode = SelectedViewer` lets one viewer play the bot contestant against you.
`[Opponent] SelectedViewer` fixes a login; otherwise the first volunteer (`!join`) plays. When that
viewer sends no vote for `[Opponent] AfkStrikes` decisions in a row, the opponent retargets
(`[Opponent] AfkRetarget`: Off, Volunteers, ActiveChatters, VolunteersThenChatters; active chatters
are those who wrote within `[Opponent] ActiveChatterMinutes`). Moderators force a change with
`!opponent next` or `!opponent pick <login>`.

Moderators are taken from Twitch badges; `[Twitch] TrustedUsers` adds more logins.
