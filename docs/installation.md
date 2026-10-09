# Installation

Supported: Steam PC versions of "Wer weiß denn sowas? - Das Spiel" (build 4288409, x86) and
"Wer weiß denn sowas? - Das 2. Spiel" (build 4278910, x64). Other builds start with a
"compatibility unknown" warning; each patch is still checked and failing features switch off.

## Players: one double-click

1. Unpack the release zip anywhere.
2. Close the games, then double-click **`Install.cmd`**.

The setup finds both games through Steam (all Steam libraries), installs BepInEx 5.4.23.5 where it is
missing (x86 for Game 1, x64 for Game 2, downloaded from the official GitHub release and checked
against a pinned SHA-256; a mismatch installs nothing) and then installs or updates the mods. A game
that is not installed is skipped; a game Steam does not know asks for its folder. Running it again
updates the mods. Nothing that already exists in a game folder is overwritten.

- **Offline**: put `BepInEx_win_x86_5.4.23.5.zip` / `BepInEx_win_x64_5.4.23.5.zip` into a `downloads`
  folder next to `Install.cmd` (or pass `-BepInExX86Zip` / `-BepInExX64Zip`), and `-NoDownload`.
- **Without questions**: `powershell -ExecutionPolicy Bypass -File Setup.ps1 -Unattended`.
- **Other folders**: `-Game1Dir` / `-Game2Dir`, one game only: `-Game Game1`.
- **Uninstall**: double-click **`Uninstall.cmd`** (asks first; removes the mods and BepInEx if the
  setup installed it, never game files or saves).

Uses Windows PowerShell, which is part of Windows; nothing else to install.

## 1. Build (developers)

Requirements: Windows, .NET SDK 8 or newer, PowerShell 7, Python 3 (text check), the games installed.

```bash
pwsh tools/Find-Games.ps1
```

```bash
pwsh build.ps1
```

`Find-Games.ps1` locates both games through Steam (`libraryfolders.vdf` + app manifests 782130 and
1112380) and writes `build/local.props`. Pass `-Game1Dir`/`-Game2Dir` for non-Steam paths.
`build.ps1` builds, runs the tests, checks text encoding and stages:

- `artifacts/payload/BepInEx/plugins/WWDS/` (in-game harness)
- `artifacts/bridge/` (companion app)

## 2. Install BepInEx (once per game)

The installer never overwrites an existing game file and records everything it adds in
`<game>\.wwds-install-manifest.txt`.

```bash
pwsh tools/Install-ToGame.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Wer weiß denn sowas" -Zip "downloads\BepInEx_win_x86_5.4.23.5.zip","downloads\RuntimeUnityEditor.Bepin5_v6.3.2.1.zip"
```

```bash
pwsh tools/Install-ToGame.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Wer weiß denn sowas - Das 2. Spiel" -Zip "downloads\BepInEx_win_x64_5.4.23.5.zip","downloads\RuntimeUnityEditor.Bepin5_v6.3.2.1.zip"
```

Game 1 needs the **x86** BepInEx build, Game 2 the **x64** build. Sources:
github.com/BepInEx/BepInEx/releases (v5.4.23.5) and github.com/ManlyMarco/RuntimeUnityEditor/releases.

## 3. Install the harness

```bash
pwsh tools/Deploy-ToGames.ps1 -Game Both
```

Start the game once; BepInEx creates `BepInEx/config/wwds.harness.cfg` with every setting and its
description (see `docs/configuration.md`). The log is `BepInEx/LogOutput.log`, harness lines start
with `[WWDS Harness]`.

## 4. Optional: Twitch bridge

See `docs/twitch-setup.md`.

## Uninstall

```bash
pwsh tools/Uninstall-FromGame.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Wer weiß denn sowas"
```

This removes exactly the files and folders listed in the manifest (BepInEx, its config and logs,
the harness). Game files and savegames (`%USERPROFILE%\AppData\LocalLow\BitComposer\...`) are not
touched. Bridge data lives in `%LOCALAPPDATA%\WWDS` (delete it to remove tokens and settings).

## What the harness changes

Nothing on disk inside the game except the files listed in the manifest. At runtime it patches
methods in memory (Harmony). Renamed contestants are never written into the game's character save.
Shows started by the debug tools suppress savegames and achievements.
