# Player mods

Comfort mods that ship with the harness. All of them work in both games, can be switched off one by
one (mod menu, page "Features", or `[Features]` in `BepInEx/config/wwds.harness.cfg`) and never touch
the game files.

## Welcome window (first start)

On the first start with the mods, the main menu shows a welcome window: what the mods do and which keys
open what, every module with a one-line explanation and an on/off switch (switching one on also starts
the modules it needs), Twitch in two sentences with a button to the login, and a "Los geht's" page.
It appears once (`[FirstRun] Shown`); `ShowAtEveryStart` keeps it, and the mod menu (Komfort,
"Willkommensfenster anzeigen") opens it any time. After an update a short notice names the new version.

## Mod menu (F10)

Opens with **F10** (`[ModMenu] Hotkey`) or **Back + Start** on a gamepad. The game is paused while the
menu is open during a show; in the main menu the game simply ignores input until the menu closes.

| Control | Keyboard | Gamepad | Mouse |
|---|---|---|---|
| Move | arrows / WASD | stick / D-pad | hover |
| Change value | left / right | left / right | click (arrows), drag (sliders) |
| Confirm, start editing | Enter / Space / J | A | click |
| Switch page | Q / E, Tab, PageUp / PageDown | LB / RB | click the page |
| Back / close | Esc / Backspace / K | B / Start | right click |
| Scroll | | | wheel |

Text fields (Client ID, viewer names, numbers): Enter starts editing, Enter saves, Esc cancels,
Ctrl+V pastes.

Pages:

- **Spiel**: overall volume (on top of the game's settings) and "Stumm im Hintergrund" (silence while the
  game window is not in front); music, effects and voice volume in the game's own 10 steps plus a real mute for each;
  audience and answer video volume (see Sound);
  resolution, window mode (fullscreen, borderless, windowed), frame rate limit (the game caps at 60),
  VSync, quality level; keep running in the background. Display settings are only managed by the mod
  after you change one of them here; then they are restored at every start and the Tech room no longer
  forces fullscreen. Saved volumes are applied at start (the game forgets this until a menu opens).
- **Twitch**: connection and account status, Client ID, login (shows the device code and opens
  twitch.tv/activate), logout, connection mode, IRC channel, test mode, bridge dry run. See
  [twitch-setup.md](twitch-setup.md).
- **Publikum**: Twitch as opponent (on/off, mode, fixed viewer, next viewer, pick a candidate, AFK
  rules), answer votes and polls, how `!vote 1500` numbers are combined, who-wins bets.
- **Schnellstart**: the quickplay line-up (below).
- **Komfort**: skip intro, mouse, ESC menu and hotkeys.
- **Inhalte**: long show, own question packs, own voice lines.
- **Features**: every module on or off.
- **Alle Einstellungen**: every harness setting, grouped by section, edited in place; each section can be
  reset to its defaults.

The menu reopens on the page it was closed on; the hotkey closes it again.

Every mod window has a **Schließen** button at the top right (mouse). Opened from the ESC menu
(Einstellungen, Mod-Menü) the button reads **< Zurück** and Escape, B or right click also return to the
ESC menu instead of the game.

## Vote window (F9)

Shows itself while chat votes, bets or names (`[UI] AutoShowHud`), or on **F9**. It looks like the
game's dialogs: title and countdown, one row per option with its text, share bar and vote count, and the
chat command to use. When the round ends the winning row is highlighted for a few seconds
(`[Hud] ResultSeconds`).

- **Move it**: drag it with the mouse; the position is saved (`[Hud] X`, `Y`). Mod menu, Komfort,
  "Position festlegen" shows it with sample content to place it; "Position zurücksetzen" puts it back to
  the top right. `[Hud] Locked` prevents accidental moves.
- **Size and look**: `[UI] HudScale`, `[Hud] Opacity`, `[Hud] ShowCommandHint`.
- Clicks on the window never reach the game.

## Scale

`[UI] MenuScale` (mod menus, dialogs, notifications; capped so a menu always fits), `[UI] HudScale`
(vote window) and `[UI] PanelScale` (F8 control panel). All three are sliders on the Komfort page and
apply live.

## Notifications

Short game-styled messages at the top (`[UI] Toasts`): quickplay result, show saved, Twitch connected or
disconnected, the Twitch login code until the login finishes, and which viewer now plays for Twitch.

## ESC menu

**Escape** (`[EscMenu] Hotkey`) during a show, or the **Start** button (which no longer opens the game's
pause menu while `[EscMenu] ReplacePauseMenu = true`), and Escape on the main menu's door screen.

- **Fortsetzen**: back to the game.
- **Schnellstart** (main menu): starts a show with the quickplay line-up.
- **Speichern** (show): saves the show at the last completed round. The game also saves after every round.
- **Einstellungen** / **Mod-Menü**: opens the mod menu.
- **Zurück zum Hauptmenü** (show): ends the show like the game's pause menu; the last round stays saved
  and the Main door offers to resume it.
- **Spiel beenden**: quits (with a confirmation).

In the main menu's rooms (player selection, Tech room, archive, make-up) Escape keeps its game meaning
(back).

The main menu's **Quit door** asks before closing the game (`[EscMenu] ConfirmQuitDoor`); the game
itself quits at once.

## Skip intro

`[SkipIntro]`:

- `SkipLogos` (default on): the logo videos at start end immediately (the game's own skip, the menu still
  loads normally). The loading part after the logos cannot be skipped: it loads the shows and characters.
- `SkipShowIntro` (default off): fast-forwards the show intro like pressing A. Off by default because the
  intro shows the audience distribution, which matters for the audience joker.
- `NoTutorialPrompt` (default off): marks the tutorial as played so the first show does not ask.

## Questions from the other game (shows 31-60)

If you own **both** games, each game also offers the other game's 30 shows as shows **31-60**: in the
Mediathek, as "next show" after your own 30, and in quickplay (`[Quickplay] ShowId` up to 60). The
questions, host voices and question videos are read directly from the other game's install folder;
nothing is copied. Everything else (studio, characters, rules) is this game's.

- Ownership: when Steam is running, Steam must confirm that you own the other game and it must be
  installed. Without a Steam answer (offline), the other game must be installed through Steam on this PC
  (Steam's library data) with its files present.
- Progress of shows 31-60 is kept in its own save file next to the game's (`Sessions_Catalogue<appid>.bin`),
  so the game's own save never contains shows it cannot load without the mod. Statistics and Steam
  achievements count as usual.
- Switch it off with `[Catalogue] OtherGameShows` (mod menu, Komfort); takes effect at the next start.

## Quickplay

With `[Quickplay] Enabled = true` the player selection fills itself from the line-up, skips the
tutorial question and starts after a short countdown (`CountdownSeconds`, game default 3). Start it from
the Main door as usual, from the ESC menu ("Schnellstart") or from the mod menu.

Line-up: four seats, `TeamA1`, `TeamA2` (Team Höcker) and `TeamB1`, `TeamB2` (Team Elton). Each seat is
`Empty`, `Keyboard` (the mouse belongs to it), `Gamepad1` to `Gamepad4` (in connection order), `Bot`
(game AI) or `Twitch` (a bot played by the audience, see the Twitch opponent). `TeamA1Character` etc. pick
a character profile id (the mod menu lists them by name), -1 takes the first free one. A team with two
seats replaces its celebrity captain, like in the game.

- Every team needs at least one seat; a device can only sit once; only one seat can be Twitch.
- If a configured gamepad is missing, the normal selection screen stays usable and the mod menu shows why.
- `ShowId` plays a specific show (1 to 30, 31 to 60 from the other game, 101 and up from own packs), -1
  the game's next unplayed one.
- Resuming a saved show: `QuickResume` reconnects the saved players to the line-up's devices.
- The tutorial from the archive always uses the normal selection.

## Keyboard shortcuts

The game has two keyboard layouts, so two people can share one keyboard: player 1 plays with the arrows,
J K I L and U O, player 2 with W A S D, 1 2 3 4 and Q E (the on-screen button symbols show each
player's keys). Join with U or O (player 1) and Q or E (player 2) on the selection screen. Both keyboard
players can open the menu with Escape.

Extra keys (`[Keyboard]`):

| | One keyboard player | Two keyboard players (`TwoPlayerKeys`) |
|---|---|---|
| Confirm (A) | Enter, Space | Player 1: Enter, player 2: Space |
| Answer A / B / C | 1 2 3 (top row or keypad) | Player 1: keypad 1 2 3, player 2: X C V |
| Master bet 1 to 5 | 1 to 5 | Player 1: keypad 1 to 5, player 2: its own keys |
| Audience joker | 0 | Player 1: keypad 0, player 2: Q or E |

Answers keep the game's two-step confirm (press once to select, again to log in). The top-row digits
are player 2's buttons, so in two-player games they never act for player 1.
Outside a show, Enter confirms on the screens that only reacted to J (player slots, tutorial question,
Tech room, archive; `EnterInMenus`). While a name is typed, both keyboards only react to Enter and
Escape, so letters can no longer press buttons.

## Gamepads

The game recognizes pads by their name only and ignored many (for example a GuliKit in X mode, named
"Controller (GuliKit X)", or any pad in Switch or DInput mode). With `[Input]`:

- `AnyXInputName` (default on): every pad Windows names "Controller (...)" (XInput pads, GuliKit,
  8BitDo in X mode, ...) plays as an Xbox controller.
- `GenericControllers` (default on): any other pad gets a standard layout: bottom face button = A,
  right = B, left = X, top = Y, shoulders, triggers, Select/Start, left stick and D-pad.
  `GenericSwapAB` swaps A and B if your pad reports them the other way round (restart needed).
- The log (`BepInEx/LogOutput.log`) lists every input device at start and each pad that connects.

Without the mods, Steam Input (game properties > Controller > enable Steam Input) also helps: it
presents the pad as an Xbox 360 controller, which the game knows (that is why Remote Play worked).

## Mouse

`[Mouse] Enabled` (default on). The cursor stays visible (`HideAfterSeconds` hides it after a pause).
Clicks become controller presses of the player the mouse belongs to (`Owner`: the keyboard player, or
`ActivePlayer` = whoever's turn it is), so the game's own rules apply everywhere.

- Click anything that shows a button symbol to press that button: answers, bets, joker, dialogs,
  confirm and back prompts. Answers and categories keep the game's two-step confirm: the first click
  selects, the second click on the same one logs in.
- Main menu: hover selects a door, click enters it.
- Player selection: left click on the left half joins Team Höcker, on the right half Team Elton; then click
  to confirm, wheel to change the character, right click to go back.
- Category grid: hover moves the focus, click proposes, a second click logs in.
- Menus (Tech room, archive, make-up): hover moves the focus, click selects, right click goes back.
- Bonus estimate: wheel changes the value (about a tenth of its size per notch, Shift for steps of 1),
  digit keys type it, Backspace deletes a digit; click the confirm prompt to log in.
- Make-up room: the wheel browses the characters. Resolution (Tech room): click the left or right half
  of the box, or turn the wheel over it. Audience joker: the wheel picks the audience member.
- Archive and other lists scroll with the wheel.
- Show intro and end screen: click to continue.
- The game's own pointer handling of buttons is switched off while mouse support is on: it ignored turn
  and category rules.

## Sound

Page **Spiel** of the mod menu, `[Game]` and `[Voice]`:

- **Publikum** (`AudienceVolume`): applause and the audience's aah/ooh, relative to the other effects.
  While someone speaks, the audience is lowered further (`[Voice] AudienceDuck`, 0.5 = half).
- **Antwortvideos** (`VideoVolume`): the explanation videos after each answer, which the game plays at a
  fixed level outside its volume settings (100 % = as in the game, up to 250 %).
- **Voice lines** (`[Voice]`): a new line of the same speaker waits until the running one is over
  (`WaitForSameSpeaker`, at most `MaxWaitSeconds`) instead of cutting it off; where the game waits for a
  line it now waits until it is really over (`WaitForRealEnd`, streamed lines start late); moving the
  selection silences contestants and captains but lets the host finish (`KeepHostOnInput`).
- **Normalization** (`[Voice] Normalize`): every voice line is brought to the same loudness
  (`TargetLevelDb`, quiet lines raised by up to `MaxBoostDb`) and peaks are limited. The game streams its
  voice files, so each line is measured the first time it plays and remembered in
  `BepInEx/wwds/voice-levels.txt`; from then on it plays with a constant level.

## Own questions and voice lines

Own question packs (CSV from Excel or JSON, optional videos) become extra shows from number 101; own
recordings replace any host, captain or contestant line. See [own-content.md](own-content.md).

## Long show

Page **Inhalte** of the mod menu, `[LongGame]`: `Boards` = 2 to 4 plays that many boards of 12
categories before the final and the estimate. After each board Kai sums up the scores, the studio wall
shows the 12 new categories and the game goes on.

- `Source`: where the questions of the next boards come from: `NextUnplayed` (default: the next show you
  have not played, also from the other game when imported), `Random`, `OwnPacks` (your question packs
  first) or `OtherGame` (shows 31-60 first).
- `JokerPerBoard` (default on): both teams get their audience joker back on every board.
- Saving and resuming work as usual: a resumed long show continues on the board it was left on (the
  boards of each show are kept in `BepInEx/wwds/long-games.txt`).
- The money trophies are not unlocked in long shows (their amounts are meant for one board).
