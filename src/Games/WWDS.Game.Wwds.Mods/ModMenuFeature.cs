using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using WWDS.Audience.Opponents;
using WWDS.Audience.Predictions;
using WWDS.Game.Wwds.Mods.Ui;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;
using WWDS.Twitch;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// The mod menu (hotkey, default F10): game settings, Twitch login and status, audience opponent and
/// bets, comfort mods, the quickplay line-up, feature switches and every harness setting generated
/// from the config. Game-styled uGUI, usable with mouse, keyboard and controller.
/// </summary>
public sealed class ModMenuFeature : FeatureBase
{
    public const string FeatureId = "ModMenu";
    public const string GamePage = "Spiel";

    private IConfigValue<string> _hotkey = null!;
    private IConfigValue<bool> _padCombo = null!;
    private ModUi _ui = null!;
    private MenuWindow? _window;
    private string? _section;
    private string _clientIdDraft = string.Empty;
    private string? _lastPage;
    private int _openedFrame = -1;

    public override string Id => FeatureId;

    public override string DisplayName => "Mod menu";

    public override string Description => "In-game menu for game settings, Twitch, audience and all mod settings (F10).";

    protected override string LogCategoryName => "Mods";

    protected override void OnStart()
    {
        _hotkey = Context.Config.Bind("ModMenu", "Hotkey", "F10", "Key that opens the mod menu (UnityEngine.KeyCode name).");
        _padCombo = Context.Config.Bind("ModMenu", "PadCombo", true, "Also open with Back + Start on a gamepad.");
        _ui = ModUi.For(Context);
        Context.Provide(this);
    }

    protected override void OnStop() => _window?.Close();

    public override void Tick()
    {
        _ui.Tick();
        var hotkey = Input.GetKeyDown(ModUi.Key(_hotkey.Value)) || PadCombo();
        if (_window != null && _window.IsOpen && hotkey && Time.frameCount != _openedFrame)
        {
            _window.Close();
            return;
        }

        if (!_ui.AnyOpen && !_ui.Blocker.Active && hotkey) Open(null);
    }

    private bool PadCombo() => _padCombo.Value
        && ((Input.GetKey(KeyCode.JoystickButton6) && Input.GetKeyDown(KeyCode.JoystickButton7)) || (Input.GetKey(KeyCode.JoystickButton7) && Input.GetKeyDown(KeyCode.JoystickButton6)));

    internal MenuWindow? Window => _window;

    /// <summary>Opens the menu, optionally on a page by title; with <paramref name="returnTo"/> "back" returns there.</summary>
    public void Open(string? page, MenuWindow? returnTo = null)
    {
        _window ??= Build();
        _section = null;
        _openedFrame = Time.frameCount;
        page ??= _lastPage;
        var index = page == null ? 0 : _window.Pages.ToList().FindIndex(p => p.Title == page);
        _ui.Show(_window, Math.Max(0, index), returnTo);
    }

    private MenuWindow Build()
    {
        var w = _ui.CreateWindow("Mod-Menü");
        w.Closed += () => _lastPage = w.CurrentPageTitle;
        w.Pages.Add(new MenuPage(GamePage, GameRows));
        w.Pages.Add(new MenuPage("Twitch", TwitchRows));
        w.Pages.Add(new MenuPage("Publikum", AudienceRows));
        w.Pages.Add(new MenuPage("Schnellstart", QuickplayRows));
        w.Pages.Add(new MenuPage("Komfort", ComfortRows));
        w.Pages.Add(new MenuPage("Inhalte", ContentRows));
        w.Pages.Add(new MenuPage("Features", FeatureRows));
        w.Pages.Add(new MenuPage("Alle Einstellungen", SettingsRows));
        return w;
    }

    // ---- own content --------------------------------------------------------------------------------

    private IEnumerable<Widget> ContentRows()
    {
        var longGame = Context.Get<LongGameFeature>();
        if (longGame != null)
        {
            yield return new HeaderWidget("Lange Show");
            var boards = new List<string> { "1 (normal)", "2 Bretter", "3 Bretter", "4 Bretter" };
            yield return new CycleWidget("Bretter pro Show", boards, () => Mathf.Clamp(longGame.Boards.Value, 1, LongGameFeature.MaxBoards) - 1, i => longGame.Boards.Value = i + 1)
            {
                Hint = "Nach 12 Kategorien kommt ein neues Brett mit 12 neuen Kategorien; Finale und Schätzfrage erst nach dem letzten. Gilt für neue Shows, Fortsetzen geht wie gewohnt.",
            };
            var sources = new List<string> { "Nächste ungespielte Show", "Zufällige Show", "Eigene Fragen zuerst", "Anderes Spiel zuerst" };
            yield return new CycleWidget("Fragen für weitere Bretter", sources, () => (int)longGame.Source.Value, i => longGame.Source.Value = (BoardSource)i);
            yield return new ToggleWidget("Publikumsjoker pro Brett", () => longGame.ResetJoker.Value, v => longGame.ResetJoker.Value = v)
            {
                Hint = "Beide Teams bekommen ihren Joker auf jedem Brett zurück.",
            };
            yield return new InfoWidget(() => longGame.Status.Length > 0 ? "Laufende Show: " + longGame.Status : "Keine lange Show aktiv.");
        }

        var questions = Context.Get<CustomQuestionsFeature>();
        if (questions != null)
        {
            yield return new HeaderWidget("Eigene Fragen");
            yield return new InfoWidget(() => questions.Packs.Count == 0
                ? "Noch keine Pakete. Vorlage und Anleitung liegen in " + questions.Folder
                : questions.Packs.Count + " Paket(e), " + questions.ShowIds.Count() + " Show(s) ab Nummer " + CustomQuestionsFeature.FirstShowId + " in der Mediathek.", 2);
            yield return new ButtonWidget("Pakete neu laden", () =>
            {
                questions.Reload();
                _ui.Toast(questions.ShowIds.Count() + " eigene Show(s) geladen", 4);
            })
            {
                Hint = "Liest den Ordner questions erneut ein. Neue Shows erscheinen in der Mediathek nach dem nächsten Betreten.",
            };
            yield return new ButtonWidget("Ordner öffnen", () => Application.OpenURL(new Uri(questions.Folder).AbsoluteUri)) { Hint = questions.Folder };
            foreach (var pack in questions.Packs)
            {
                var p = pack;
                var shows = p.ShowIds.Count == 0 ? "nicht spielbar" : "Show " + string.Join(", ", p.ShowIds.Select(i => i.ToString()).ToArray());
                yield return new InfoWidget(p.Name + ": " + shows, 1);
                foreach (var problem in p.Pack.Errors.Take(4)) yield return new InfoWidget("  Fehler: " + problem, 2);
                foreach (var hint in p.Pack.Warnings.Take(3)) yield return new InfoWidget("  Hinweis: " + hint, 2);
            }
        }

        var voices = Context.Get<CustomVoiceFeature>();
        if (voices != null)
        {
            yield return new HeaderWidget("Eigene Sätze");
            yield return new InfoWidget(() => voices.Loading ? "Lade eigene Sätze ..." : voices.Count + " eigene Sätze aus " + voices.Folder, 2);
            yield return new ButtonWidget("Eigene Sätze neu laden", voices.Reload) { Hint = "Liest den Ordner voices erneut ein (Dateiname = Name des Satzes, .ogg oder .wav)." };
            yield return new ButtonWidget("Satzliste exportieren", () => _ui.Toast("Gespeichert: " + voices.ExportList(), 6))
            {
                Hint = "Schreibt alle Satznamen in voices/_alle-saetze.txt. In einer Show auch die Sätze dieser Show.",
            };
        }

    }

    // ---- game ---------------------------------------------------------------------------------------

    private IEnumerable<Widget> GameRows()
    {
        var gs = Context.Get<GameSettingsFeature>();
        if (gs == null)
        {
            yield return new InfoWidget("Feature 'GameSettings' ist ausgeschaltet (Seite Features).");
            yield break;
        }

        yield return new HeaderWidget("Lautstärke");
        yield return new SliderWidget("Gesamt", 0f, 1f, 0.05f, () => gs.MasterVolume, v => gs.MasterVolume = v, Percent)
        {
            Hint = "Gesamtlautstärke über Musik, Effekten und Stimmen. Die Regler darunter bleiben wie im Spiel.",
        };
        yield return new ToggleWidget("Stumm im Hintergrund", () => gs.MuteInBackground, v => gs.MuteInBackground = v)
        {
            Hint = "Kein Ton, solange das Spielfenster nicht im Vordergrund ist (z. B. beim Streamen mit 'Im Hintergrund weiterlaufen').",
        };
        foreach (var ch in new[] { GameSettingsFeature.Channel.Music, GameSettingsFeature.Channel.Sfx, GameSettingsFeature.Channel.Voice })
        {
            var channel = ch;
            var name = channel switch
            {
                GameSettingsFeature.Channel.Music => "Musik",
                GameSettingsFeature.Channel.Sfx => "Effekte",
                _ => "Stimmen",
            };
            yield return new SliderWidget(name, 0.1f, 1f, 0.1f, () => gs.Volume(channel), v => gs.SetVolume(channel, v), Percent)
            {
                Hint = "Gleiche Stufen wie im Spiel (10 bis 100 %). Ganz still: Stumm.",
            };
            yield return new ToggleWidget(name + " stumm", () => gs.Muted(channel), m => gs.SetMuted(channel, m));
        }

        yield return new SliderWidget("Publikum", 0f, 1f, 0.05f, () => gs.AudienceVolume, v => gs.AudienceVolume = v, Percent)
        {
            Hint = "Applaus und Raunen des Studiopublikums, im Verhältnis zu den übrigen Effekten.",
        };
        yield return new SliderWidget("Antwortvideos", 0f, 2.5f, 0.05f, () => gs.VideoVolume, v => gs.VideoVolume = v, Percent)
        {
            Hint = "Die Erklärvideos nach jeder Frage. 100 % = wie im Spiel (es spielt sie fest leiser ab), bis 250 %.",
        };

        yield return new HeaderWidget("Anzeige");
        yield return new CycleWidget("Auflösung", gs.Resolutions, () => gs.Resolutions().IndexOf(gs.CurrentResolution), i => gs.SetResolution(gs.Resolutions()[i]))
        {
            Hint = "Wird sofort angewendet und beim Start wiederhergestellt.",
        };
        yield return CycleWidget.ForEnum("Fenstermodus", () => gs.CurrentWindowMode, gs.SetWindowMode);
        var rates = GameSettingsFeature.FrameRates.Select(r => r < 0 ? "Unbegrenzt" : r + " FPS").ToList();
        yield return new CycleWidget("Bildrate", rates, () => Array.IndexOf(GameSettingsFeature.FrameRates, gs.FrameRate), i => gs.SetFrameRate(GameSettingsFeature.FrameRates[i]))
        {
            Hint = "Das Spiel begrenzt auf 60 FPS. Mit VSync zählt die Bildwiederholrate des Monitors.",
        };
        yield return new ToggleWidget("VSync", () => gs.VSync, gs.SetVSync);
        yield return new CycleWidget("Qualität", () => QualitySettings.names, () => gs.Quality, gs.SetQuality);
        yield return new HeaderWidget("Allgemein");
        var background = Context.Config.Bind("General", "RunInBackground", false, "Keep the game running while its window is unfocused (useful while streaming).");
        yield return new ToggleWidget("Im Hintergrund weiterlaufen", () => background.Value, v =>
        {
            background.Value = v;
            if (Context.Host != null) Context.Host.RunInBackground = v;
        }) { Hint = "Das Spiel pausiert sonst, wenn das Fenster den Fokus verliert (z. B. beim Streamen)." };
    }

    private static string Percent(float v) => Mathf.RoundToInt(v * 100) + " %";

    // ---- twitch -------------------------------------------------------------------------------------

    private IEnumerable<Widget> TwitchRows()
    {
        var tw = Context.Get<TwitchFeature>();
        if (tw == null)
        {
            yield return new InfoWidget("Feature 'Twitch' ist ausgeschaltet (Seite Features).");
            yield break;
        }

        _clientIdDraft = tw.ClientId;
        yield return new InfoWidget(() => "Verbindung: " + tw.LinkState + (tw.LinkDetail.Length > 0 ? " (" + tw.LinkDetail + ")" : string.Empty)
            + "\nKonto: " + (tw.AccountLogin.Length > 0 ? tw.AccountLogin : "nicht angemeldet") + "   Modus: " + tw.Mode, 2);
        yield return new InfoWidget(() => tw.PendingLogin != null
            ? "Code: " + tw.PendingLogin.UserCode + "   auf " + tw.PendingLogin.VerificationUri + " eingeben (Browser ist geöffnet)."
            : tw.LoginMessage, 2) { VisibleWhen = () => tw.PendingLogin != null || tw.LoginMessage.Length > 0 };
        yield return new HeaderWidget("Konto");
        yield return new TextFieldWidget("Client ID", () => _clientIdDraft.Length == 0 && tw.ClientIdBuiltIn ? "eingebaut" : _clientIdDraft, v =>
        {
            _clientIdDraft = v == "eingebaut" ? string.Empty : v;
            tw.SaveBridgeSettings(_clientIdDraft, tw.BridgeDryRun);
        })
        {
            Hint = "Client ID deiner Twitch-Anwendung (dev.twitch.tv/console/apps, Typ Public). Strg+V fügt ein. "
                + "\"eingebaut\": diese Version bringt eine mit; leer lassen genügt.",
        };
        yield return new ButtonWidget("Anmelden", tw.Login, () => tw.AccountLogin.Length > 0 ? "neu anmelden" : ">")
        {
            Hint = "Startet die Anmeldung: Browser öffnet twitch.tv/activate, dort den angezeigten Code als Broadcaster bestätigen.",
            EnabledWhen = () => _clientIdDraft.Length > 0 || tw.HasClientId,
        };
        yield return new ButtonWidget("Abmelden", () => _window!.Confirm("Von Twitch abmelden? Die gespeicherten Tokens werden gelöscht.", "Abmelden", tw.Logout))
        {
            VisibleWhen = () => tw.AccountLogin.Length > 0,
        };
        yield return new HeaderWidget("Verbindung");
        yield return CycleWidget.ForEnum("Modus", () => tw.Mode, tw.SetMode);
        var channel = Setting("Twitch", "Channel", "IRC-Kanal");
        if (channel != null) yield return channel;
        yield return new ToggleWidget("Testmodus", () => tw.TestMode, v => tw.TestMode = v)
        {
            Hint = "Befehle werden nur protokolliert, nicht im Spiel angewendet und nicht im Chat beantwortet.",
        };
        yield return new ToggleWidget("Dry Run (Bridge)", () => tw.BridgeDryRun, v => tw.SaveBridgeSettings(tw.ClientId, v))
        {
            Hint = "Die Bridge sendet keine Chatnachrichten, Umfragen oder Vorhersagen.",
        };
    }

    // ---- audience -----------------------------------------------------------------------------------

    private IEnumerable<Widget> AudienceRows()
    {
        var op = Context.Get<OpponentFeature>();
        if (op != null)
        {
            yield return new HeaderWidget("Twitch als Gegner");
            yield return new ToggleWidget("Twitch spielt mit", () => op.IsActive, v => op.SetActive(v))
            {
                Hint = "Der Chat übernimmt den Bot-Kandidaten der nächsten Show (oder sofort, wenn eine läuft).",
            };
            yield return CycleWidget.ForEnum("Modus", () => op.Mode, m => op.Mode = m);
            yield return new InfoWidget(() => "Spielt gerade: " + (op.CurrentDecider != null ? "@" + op.CurrentDecider.DisplayName : "der ganze Chat"))
            {
                VisibleWhen = () => op.Mode is OpponentMode.SelectedViewer or OpponentMode.RotatingViewers,
            };
            var selected = Setting("Opponent", "SelectedViewer", "Fester Zuschauer");
            if (selected != null) yield return selected;
            yield return new ButtonWidget("Nächster Zuschauer", () => op.Retarget("vom Streamer gewechselt"))
            {
                Hint = "Wechselt zum nächsten Freiwilligen (!join) oder aktiven Chatter.",
                VisibleWhen = () => op.Mode == OpponentMode.SelectedViewer,
            };
            foreach (var viewer in op.Candidates().Take(8))
            {
                var login = viewer.Login;
                yield return new ButtonWidget("  @" + viewer.DisplayName, () => op.PickViewer(login), () => "auswählen")
                {
                    VisibleWhen = () => op.Mode == OpponentMode.SelectedViewer,
                };
            }

            foreach (var row in Settings("Opponent", "AfkStrikes", "AFK nach verpassten Runden", "Opponent", "AfkRetarget", "Ersatz bei AFK", "Opponent", "Behavior", "Entscheidung"))
            {
                yield return row;
            }
        }

        yield return new HeaderWidget("Abstimmungen");
        foreach (var row in Settings("Voting", "AutoOpen", "Antwort-Abstimmung", "Voting", "UseTwitchPolls", "Twitch-Umfragen",
                     "Voting", "Resolution", "Auswertung", "Audience", "NumberAggregation", "Zahlen (!vote 1500)"))
        {
            yield return row;
        }

        var ww = Context.Get<WhoWinsFeature>();
        if (ww != null)
        {
            yield return new HeaderWidget("Wetten: Wer gewinnt?");
            foreach (var row in Settings("WhoWins", "ChatBets", "Chat-Wette (!vote A/B)", "WhoWins", "TwitchPrediction", "Kanalpunkte-Vorhersage", "WhoWins", "WindowSeconds", "Offen (Sekunden)"))
            {
                yield return row;
            }

            yield return new ButtonWidget("Wette jetzt öffnen", ww.Open, () => ww.Labels.Length == 2 ? ww.Labels[0] + " / " + ww.Labels[1] : ">")
            {
                Hint = "Öffnet die Wette sofort (normalerweise automatisch zu Beginn jeder Show).",
            };
        }
    }

    // ---- quickplay ----------------------------------------------------------------------------------

    private IEnumerable<Widget> QuickplayRows()
    {
        var qp = Context.Get<QuickplayFeature>();
        if (qp == null)
        {
            yield return new InfoWidget("Feature 'Quickplay' ist ausgeschaltet (Seite Features).");
            yield break;
        }

        yield return new ToggleWidget("Schnellstart aktiv", () => qp.Enabled.Value, v => qp.Enabled.Value = v)
        {
            Hint = "Die Spielerauswahl füllt sich selbst mit dieser Aufstellung und startet nach dem Countdown.",
        };
        yield return new InfoWidget(() =>
        {
            var problem = qp.Validate();
            return problem.Length > 0 ? "Achtung: " + problem : qp.LastMessage.Length > 0 ? qp.LastMessage : "Geräte: " + string.Join(", ", QuickplayFeature.ConnectedDevices().ToArray());
        }, 2);
        var characters = SDataStore.Instance != null ? SDataStore.Instance.AvailableCharacters : new DataCharacterProfile[0];
        var names = new List<string> { "Automatisch" };
        names.AddRange(characters.Select(c => c.Name));
        var seatNames = Enum.GetNames(typeof(Seat));
        var labels = new[] { "Team Höcker, Platz 1", "Team Höcker, Platz 2", "Team Elton, Platz 1", "Team Elton, Platz 2" };
        for (var i = 0; i < 4; i++)
        {
            var seat = i;
            yield return new HeaderWidget(labels[seat]);
            yield return new CycleWidget("Spieler", SeatLabels(seatNames), () => (int)qp.GetSeat(seat), v => qp.SetSeat(seat, (Seat)v))
            {
                Hint = "Tastatur (auch Maus), Gamepad 1-4, Bot (Spiel-KI) oder Twitch (Bot, den der Chat spielt).",
            };
            yield return new CycleWidget("Figur", names, () =>
            {
                var id = qp.GetCharacter(seat);
                var index = Array.FindIndex(characters, c => c.Id == id);
                return index < 0 ? 0 : index + 1;
            }, v => qp.SetCharacter(seat, v == 0 ? -1 : characters[v - 1].Id))
            {
                VisibleWhen = () => QuickplayFeature.IsHuman(qp.GetSeat(seat)),
            };
        }

        yield return new HeaderWidget("Ablauf");
        foreach (var row in Settings("Quickplay", "ShowId", "Show (-1 = nächste)", "Quickplay", "CountdownSeconds", "Countdown (Sekunden)", "Quickplay", "QuickResume", "Fortsetzen automatisch verbinden"))
        {
            yield return row;
        }

        yield return new ButtonWidget("Jetzt starten", () =>
        {
            var message = qp.StartFromMainMenu();
            if (message.StartsWith("Show", StringComparison.Ordinal)) _ui.CloseForGood(_window!);
            else Log.Info(message);
        })
        {
            Hint = "Nur im Hauptmenü (Türen).",
            VisibleWhen = () => _ui.State == "StateScreenMainMenu",
        };
    }

    private static IList<string> SeatLabels(string[] names) => names.Select(n => n switch
    {
        "Empty" => "Leer",
        "Keyboard" => "Tastatur",
        "Bot" => "Bot (KI)",
        _ => n.Replace("Gamepad", "Gamepad "),
    }).ToList();

    // ---- comfort ------------------------------------------------------------------------------------

    private IEnumerable<Widget> ComfortRows()
    {
        var welcome = Context.Get<FirstRunFeature>();
        if (welcome != null)
        {
            yield return new ButtonWidget("Willkommensfenster anzeigen", () => welcome.Open()) { Hint = "Tasten, Mods an/aus und Twitch im Überblick." };
        }

        var catalogue = Context.Get<CatalogueFeature>();
        if (catalogue != null)
        {
            yield return new HeaderWidget("Fragenkatalog");
            yield return new InfoWidget(() => catalogue.Active
                ? "Shows 31-60 sind die Shows aus " + catalogue.Other!.Name + " (Mediathek und nächste Show)."
                : catalogue.Status, 2);
            yield return new ToggleWidget("Shows aus dem anderen Spiel", () => catalogue.Enabled.Value, v => catalogue.Enabled.Value = v)
            {
                Hint = "Nur wenn du das andere Spiel besitzt und installiert hast. Wirkt ab dem nächsten Start; der Fortschritt von 31-60 liegt in einer eigenen Datei.",
            };
        }

        yield return new HeaderWidget("Intro");
        foreach (var row in Settings("SkipIntro", "SkipLogos", "Logos überspringen", "SkipIntro", "SkipShowIntro", "Show-Intro überspringen", "SkipIntro", "NoTutorialPrompt", "Keine Tutorial-Frage"))
        {
            yield return row;
        }

        yield return new HeaderWidget("Maus");
        foreach (var row in Settings("Mouse", "Enabled", "Maussteuerung", "Mouse", "Owner", "Maus spielt für", "Mouse", "HideAfterSeconds", "Zeiger ausblenden nach (s)"))
        {
            yield return row;
        }

        yield return new HeaderWidget("Darstellung");
        yield return new SliderWidget("Menügröße", 0.6f, 1.4f, 0.05f, () => _ui.MenuScale.Value, v => _ui.MenuScale.Value = v, Percent)
        {
            Hint = "Größe der Mod-Menüs und Hinweise. Wird so begrenzt, dass das Menü immer auf den Bildschirm passt.",
        };
        yield return new SliderWidget("Abstimmungsfenster", 0.5f, 2f, 0.05f, () => _ui.HudScale.Value, v => _ui.HudScale.Value = v, Percent);
        foreach (var row in Settings("UI", "PanelScale", "Kontrollpanel (F8)", "UI", "Toasts", "Hinweise oben einblenden"))
        {
            yield return row;
        }

        var hud = Context.Get<VoteHudFeature>();
        if (hud != null)
        {
            yield return new HeaderWidget("Abstimmungsfenster");
            yield return new ButtonWidget("Position festlegen", () =>
            {
                _ui.CloseForGood(_window!);
                hud.Preview();
            }) { Hint = "Zeigt das Fenster mit Beispielinhalt: mit der Maus ziehen, Enter oder Klick daneben beendet." };
            yield return new ButtonWidget("Position zurücksetzen", hud.ResetPosition) { Hint = "Zurück in die Ecke oben rechts." };
            foreach (var row in Settings("Hud", "Locked", "Position sperren", "Hud", "Opacity", "Deckkraft", "Hud", "ResultSeconds", "Ergebnis zeigen (s)",
                         "Hud", "ShowCommandHint", "Chat-Befehl anzeigen", "UI", "AutoShowHud", "Automatisch einblenden", "UI", "HudHotkey", "Ein/Aus Taste"))
            {
                yield return row;
            }
        }

        yield return new HeaderWidget("Tastatur");
        foreach (var row in Settings("Keyboard", "ShowShortcuts", "Enter, 1-3, 1-5 in der Show", "EscMenu", "ConfirmQuitDoor", "Beenden-Tür fragt nach"))
        {
            yield return row;
        }

        yield return new HeaderWidget("Menüs");
        foreach (var row in Settings("EscMenu", "ReplacePauseMenu", "ESC-Menü statt Pausenmenü", "EscMenu", "Hotkey", "ESC-Menü Taste", "ModMenu", "Hotkey", "Mod-Menü Taste", "ModMenu", "PadCombo", "Gamepad: Back + Start"))
        {
            yield return row;
        }
    }

    // ---- features -----------------------------------------------------------------------------------

    private IEnumerable<Widget> FeatureRows()
    {
        yield return new InfoWidget("Schaltet Module ein oder aus. Abhängige Module folgen automatisch.", 1);
        foreach (var info in Context.Features.Features)
        {
            var f = info;
            if (f.Feature.Id == FeatureId) continue;
            yield return new ToggleWidget(f.Feature.DisplayName, () => f.Enabled.Value && f.State == FeatureState.Running, on => FirstRunFeature.SetFeature(Context, f, on)) { Hint = f.Feature.Description + (f.Detail != null ? " (" + f.State + ": " + f.Detail + ")" : string.Empty) };
        }
    }

    // ---- generated settings ---------------------------------------------------------------------------

    private IEnumerable<Widget> SettingsRows()
    {
        if (Context.Config is not IConfigCatalog catalog)
        {
            yield return new InfoWidget("Diese Konfiguration kann nicht aufgelistet werden.");
            yield break;
        }

        var entries = catalog.Entries();
        if (_section == null)
        {
            yield return new InfoWidget("Alle Einstellungen aus BepInEx/config/wwds.harness.cfg, nach Bereich.", 1);
            foreach (var section in entries.Select(e => e.Section).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = section;
                var count = entries.Count(e => e.Section == name);
                yield return new ButtonWidget(name, () =>
                {
                    _section = name;
                    _window!.Rebuild();
                }, () => count + " >");
            }

            yield break;
        }

        yield return new ButtonWidget("< Alle Bereiche", () =>
        {
            _section = null;
            _window!.Rebuild();
        });
        yield return new HeaderWidget(_section);
        var current = _section;
        yield return new ButtonWidget("Bereich auf Standard", () => _window!.Confirm("Alle Einstellungen in [" + current + "] auf ihre Standardwerte setzen?", "Zurücksetzen", () =>
        {
            foreach (var e in entries.Where(e => e.Section == current)) e.Value = e.DefaultValue;
            Context.Config.Save();
            _window!.Rebuild();
        })) { Hint = "Setzt jeden Wert dieses Bereichs auf den Standard zurück (mit Rückfrage)." };
        foreach (var entry in entries.Where(e => e.Section == _section))
        {
            var row = Editor(entry, entry.Key);
            if (row != null) yield return row;
        }
    }

    /// <summary>Rows for settings given as section, key, label triples (missing ones are skipped).</summary>
    private IEnumerable<Widget> Settings(params string[] sectionKeyLabel)
    {
        for (var i = 0; i + 2 < sectionKeyLabel.Length; i += 3)
        {
            var row = Setting(sectionKeyLabel[i], sectionKeyLabel[i + 1], sectionKeyLabel[i + 2]);
            if (row != null) yield return row;
        }
    }

    private Widget? Setting(string section, string key, string label)
    {
        if (Context.Config is not IConfigCatalog catalog) return null;
        var entry = catalog.Entries().FirstOrDefault(e => e.Section == section && e.Key == key);
        return entry == null ? null : Editor(entry, label);
    }

    /// <summary>Generic row for a setting: toggle, cycle, stepper with typing, or text.</summary>
    private static Widget? Editor(ConfigEntryInfo entry, string label)
    {
        var type = entry.ValueType;
        var hint = entry.Description + " (Standard: " + SettingText.Format(entry.DefaultValue) + ")";
        if (type == typeof(bool)) return new ToggleWidget(label, () => entry.Value is true, v => entry.Value = v) { Hint = hint };
        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            return new CycleWidget(label, names, () => Array.IndexOf(names, entry.Value?.ToString()), i => entry.Value = Enum.Parse(type, names[i])) { Hint = hint };
        }

        if (type == typeof(int) || type == typeof(float) || type == typeof(double))
        {
            return new TextFieldWidget(label, () => SettingText.Format(entry.Value), text =>
            {
                if (SettingText.TryParse(type, text, out var v)) entry.Value = v;
            }, 16)
            {
                Hint = hint + "  Links/Rechts ändert, Enter zum Eintippen.",
                Step = d => entry.Value = SettingText.Step(type, entry.Value, d),
            };
        }

        if (type == typeof(string))
        {
            return new TextFieldWidget(label, () => entry.Value as string ?? string.Empty, text => entry.Value = text, 400) { Hint = hint };
        }

        return null;
    }
}
