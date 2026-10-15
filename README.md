# WWDS Mods

Comfort mods and Twitch audience play for the PC games **"Wer weiß denn sowas? - Das Spiel"** and
**"Wer weiß denn sowas? - Das 2. Spiel"** (Steam). One install serves both games.

> Unofficial fan project. Not affiliated with or endorsed by the game's developers or publishers, or
> by the TV show and its broadcaster. You need your own copy of the game(s); no game files are included.

## Features

**Playing**
- **Mod menu (F10)**: game settings, Twitch, audience, quickplay, comfort options, every module on or off,
  every setting editable in place. Game-styled, usable with mouse, keyboard and gamepad.
- **ESC menu**: resume, save, settings, back to the main menu, quit (replaces the pause menu).
- **Quickplay**: the player selection fills itself from a configurable line-up (keyboard, gamepads,
  game AI, Twitch) with fixed characters, and starts right away.
- **Mouse support** on every screen: click answers, categories, bets, prompts; wheel for estimates.
- **Keyboard shortcuts** in a show: Enter confirms, 1/2/3 answer, 1-5 master bets.
- **Skip intro**: logo videos, optionally the show intro and the tutorial question.
- **Game settings**: master volume, mute in background, real mute per channel, resolution, window mode,
  frame rate, VSync, quality, UI scale.
- **Both catalogues**: own both games and each offers the other game's 30 shows as shows 31-60.
- **Welcome window** on the first start: what is installed, keys, modules on or off.

**Streaming (Twitch)**
- In-game Twitch login (device code; tokens stay on your PC, encrypted).
- Chat votes on every question (`!vote A`), native Twitch polls, number votes (average or median).
- "Who wins?" bets: chat bet and channel-points prediction, closed when the first question appears.
- **Twitch as opponent**: the whole chat, a chosen viewer (with AFK replacement) or rotating viewers play
  against you.
- Community names, guess rounds, channel point rewards, subs, bits and raids as triggers.
- Game-styled, movable vote window (F9), notifications, moderator controls, test mode and dry run.

## Install

1. Download the latest zip from [Releases](https://github.com/KeinNameVorhanden/wer-weiss-denn-sowas-mods/releases) and unpack it anywhere.
2. Close the games and double-click **`Install.cmd`**.

The setup finds both games through Steam, installs [BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.5
where it is missing (official release, checksum verified) and installs the mods. Running it again
updates them; **`Uninstall.cmd`** removes everything it added. Game files and saves are never changed.
Details and offline install: [docs/installation.md](docs/installation.md).

Start the game: the welcome window explains the rest. **F10** opens the mod menu.

For Twitch, see [docs/twitch-setup.md](docs/twitch-setup.md).

## Documentation

| Document | Content |
|---|---|
| [docs/mods.md](docs/mods.md) | Every mod and how to use it |
| [docs/twitch-setup.md](docs/twitch-setup.md) | Twitch app, login, chat commands, bets, streamer vs viewer |
| [docs/configuration.md](docs/configuration.md) | All settings (`BepInEx/config/wwds.harness.cfg`) |
| [docs/installation.md](docs/installation.md) | Install, offline install, uninstall, building |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Symptoms and fixes |
| [docs/compatibility.md](docs/compatibility.md) | Supported game builds and capabilities |
| [docs/architecture.md](docs/architecture.md) | Assemblies, layering, game adapter, Twitch bridge |
| [docs/extending.md](docs/extending.md) | Writing your own features and actions |

## Building from source

Requirements: Windows, .NET SDK 8 or newer, PowerShell 7, Python 3, and the game(s) installed (the build
compiles against the game's own assemblies, which are never committed).

```bash
pwsh tools/Find-Games.ps1
```

```bash
pwsh build.ps1
```

`Find-Games.ps1` writes the game paths to `build/local.props` (machine-specific, not committed).
`build.ps1` builds, runs the unit tests and stages the payload; `tools/Deploy-ToGames.ps1` installs it,
`tools/Package-Release.ps1` builds the release zip (optionally with your Twitch app's Client ID built in,
see [docs/twitch-setup.md](docs/twitch-setup.md)) and `tools/Publish-Release.ps1` publishes a version
to GitHub. `tools/Run-ModsSelfTest.ps1` runs an unattended,
silent in-game test of the mods.

```
src/Core/WWDS.ModCore             core: events, config, features, model (no Unity)
src/Core/WWDS.Runtime             Harmony patch registry and reflection helpers
src/Games/WWDS.Game.Wwds          game adapter (both games)
src/Games/WWDS.Game.Wwds.Mods     player mods: menus, UI kit, quickplay, mouse, catalogue, ...
src/Audience/WWDS.Audience        audience engine: votes, names, guesses, opponent, bets
src/Twitch/WWDS.Twitch            in-game Twitch feature and transports
src/Twitch/WWDS.Protocol          game <-> bridge protocol
src/Bridge/WWDS.Bridge            Twitch companion app (.NET 8, TwitchLib)
src/UI/WWDS.UI                    control panel (F8)
src/Loader/WWDS.Host.BepInEx      BepInEx plugin entry point
src/Features, src/Tools           debug tools, diagnostics probe
tests/                            unit tests (xUnit) and runtime test configs
tools/                            install, build, release and test scripts
```

## Credits

[BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX) for
loading and patching, [TwitchLib](https://github.com/TwitchLib) for the Twitch APIs. All game names,
content and trademarks belong to their respective owners.

## License

[MIT](LICENSE)
