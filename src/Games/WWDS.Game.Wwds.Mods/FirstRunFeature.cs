using System;
using System.Collections.Generic;
using System.Linq;
using WWDS.Audience.Opponents;
using WWDS.Game.Wwds.Mods.Ui;
using WWDS.ModCore;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Twitch;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Welcome window on the first start with the mods: what was installed and which keys open what, every
/// module with a short explanation and an on/off switch, Twitch in two sentences with a way to log in.
/// Shown once (can be reopened from the mod menu); after an update a short notice names the new version.
/// </summary>
public sealed class FirstRunFeature : FeatureBase
{
    public const string FeatureId = "FirstRun";
    public const string ModsPage = "Mods";

    /// <summary>Modules as players see them: group, name, what it does. Others (debug, menus) are not listed.</summary>
    private static readonly string[][] Modules =
    {
        new[] { "Komfort", SkipIntroFeature.FeatureId, "Intro überspringen", "Die Logo-Videos beim Start werden übersprungen (das Show-Intro auf Wunsch auch)." },
        new[] { "Komfort", QuickplayFeature.FeatureId, "Schnellstart", "Die Spielerauswahl füllt sich selbst: Tastatur gegen KI, Aufstellung im Mod-Menü." },
        new[] { "Komfort", MouseFeature.FeatureId, "Maussteuerung", "Klicken, Zeigen und Mausrad in allen Menüs und in der Show." },
        new[] { "Komfort", KeyboardFeature.FeatureId, "Tastenkürzel", "In der Show: Enter bestätigt, 1/2/3 wählt die Antwort, 1-5 den Einsatz." },
        new[] { "Komfort", EscMenuFeature.FeatureId, "ESC-Menü", "Escape: Fortsetzen, Speichern, Einstellungen, Hauptmenü, Beenden." },
        new[] { "Komfort", GameSettingsFeature.FeatureId, "Spieleinstellungen", "Lautstärke mit Stummschaltung, Auflösung, Fenstermodus, Bildrate." },
        new[] { "Komfort", CatalogueFeature.FeatureId, "Fragen aus dem anderen Spiel", "Besitzt du beide Spiele, gibt es die 30 Shows des anderen Spiels als Shows 31-60." },
        new[] { "Twitch und Chat", TwitchFeature.FeatureId, "Twitch-Verbindung", "Verbindet das Spiel mit deinem Twitch-Kanal (Anmeldung im Mod-Menü)." },
        new[] { "Twitch und Chat", WWDS.Audience.AudienceFeature.FeatureId, "Chat-Befehle", "Grundlage für alles mit dem Chat: !vote, !name, Moderation." },
        new[] { "Twitch und Chat", WWDS.Audience.Voting.VotingFeature.FeatureId, "Chat-Abstimmungen", "Der Chat tippt bei jeder Frage mit (!vote A/B/C)." },
        new[] { "Twitch und Chat", OpponentFeature.FeatureId, "Twitch als Gegner", "Der Chat oder ein Zuschauer spielt den Gegner statt der KI." },
        new[] { "Twitch und Chat", WWDS.Audience.Predictions.WhoWinsFeature.FeatureId, "Wer gewinnt?", "Wette zu Beginn jeder Show (Chat oder Kanalpunkte)." },
        new[] { "Twitch und Chat", WWDS.Audience.Guessing.GuessingFeature.FeatureId, "Tipp-Runden", "Tipps auf Gewinner und Antworten mit Punkten für den Chat." },
        new[] { "Twitch und Chat", WWDS.Audience.Naming.NamingFeature.FeatureId, "Namen vom Chat", "Der Chat benennt Gegner und Teams." },
        new[] { "Twitch und Chat", WWDS.Audience.Bindings.BindingsFeature.FeatureId, "Kanalpunkte und Events", "Belohnungen, Subs, Bits und Raids lösen Aktionen aus." },
        new[] { "Twitch und Chat", VoteHudFeature.FeatureId, "Abstimmungsfenster", "Zeigt laufende Abstimmungen im Spielstil, frei verschiebbar (F9)." },
        new[] { "Twitch und Chat", NotificationsFeature.FeatureId, "Hinweise", "Kurze Meldungen oben: verbunden, Anmeldecode, gespeichert." },
        new[] { "Weiteres", WWDS.Audience.Statistics.StatisticsFeature.FeatureId, "Statistiken", "Merkt sich Chat-Treffer und aktive Zuschauer." },
        new[] { "Weiteres", "Overlay", "Kontrollpanel (F8)", "Technische Übersicht und Moderation für Streamer." },
    };

    private IConfigValue<bool> _shown = null!;
    private IConfigValue<string> _lastVersion = null!;
    private IConfigValue<bool> _showAgain = null!;
    private ModUi _ui = null!;
    private MenuWindow? _window;
    private double _menuSince = -1;
    private bool _checkedVersion;

    public override string Id => FeatureId;

    public override string DisplayName => "First run";

    public override string Description => "Welcome window on the first start: keys, modules on/off, Twitch.";

    protected override string LogCategoryName => "Mods";

    internal MenuWindow? Window => _window;

    protected override void OnStart()
    {
        var c = Context.Config;
        _shown = c.Bind("FirstRun", "Shown", false, "The welcome window was shown (set to false to see it again at the next start).");
        _showAgain = c.Bind("FirstRun", "ShowAtEveryStart", false, "Show the welcome window at every start.");
        _lastVersion = c.Bind("FirstRun", "LastVersion", string.Empty, "Mod version of the last start (an update shows a short notice).");
        _ui = ModUi.For(Context);
        Context.Provide(this);
        Track(_ui.OnState(state => _menuSince = state == "StateScreenMainMenu" ? Context.Clock.Now : -1));
    }

    public override void Tick()
    {
        _ui.Tick();
        if (_menuSince < 0 || Context.Clock.Now - _menuSince < 1.5 || _ui.AnyOpen || _ui.Blocker.Active) return;
        var menu = MainMenuController.Instance;
        if (menu == null || menu.IsInputLocked()) return;
        _menuSince = -1;
        if (!_shown.Value || _showAgain.Value)
        {
            Open();
            return;
        }

        if (_checkedVersion) return;
        _checkedVersion = true;
        if (_lastVersion.Value != ModContext.HarnessVersion)
        {
            if (_lastVersion.Value.Length > 0) _ui.Toast("Mods aktualisiert auf " + ModContext.HarnessVersion + "  -  F10 öffnet das Mod-Menü", 7, "version");
            _lastVersion.Value = ModContext.HarnessVersion;
        }
    }

    /// <summary>Opens the welcome window (also from the mod menu).</summary>
    public void Open(string? page = null)
    {
        if (_window == null)
        {
            _window = _ui.CreateWindow("WWDS-Mods");
            _window.Pages.Add(new MenuPage("Willkommen", WelcomeRows));
            _window.Pages.Add(new MenuPage(ModsPage, ModuleRows));
            _window.Pages.Add(new MenuPage("Twitch", TwitchRows));
            _window.Pages.Add(new MenuPage("Los geht's", StartRows));
            _window.Closed += () =>
            {
                _shown.Value = true;
                _lastVersion.Value = ModContext.HarnessVersion;
                _checkedVersion = true;
            };
        }

        var index = page == null ? 0 : _window.Pages.ToList().FindIndex(p => p.Title == page);
        _ui.Show(_window, Math.Max(0, index));
    }

    private IEnumerable<Widget> WelcomeRows()
    {
        yield return new HeaderWidget("Mods für Wer weiß denn sowas? (" + ModContext.HarnessVersion + ")");
        yield return new InfoWidget("Diese Mods ergänzen das Spiel um Komfort (Maus, Schnellstart, ESC-Menü, Einstellungen) und um Twitch: "
            + "dein Chat kann abstimmen, wetten, Namen vergeben oder selbst gegen dich spielen. Die Spieldateien bleiben unverändert.", 3);
        yield return new HeaderWidget("Tasten");
        yield return new InfoWidget("F10  Mod-Menü: alle Einstellungen, Twitch, Schnellstart, Mods an/aus\n"
            + "Esc  ESC-Menü: Fortsetzen, Speichern, Einstellungen, Hauptmenü, Beenden\n"
            + "F9  Abstimmungsfenster ein/aus (verschieben mit der Maus)\n"
            + "F8  Kontrollpanel für Streamer", 4);
        yield return new InfoWidget("In allen Mod-Fenstern: Maus, Pfeiltasten und Enter, oder Gamepad (A/B, LB/RB für Reiter). Zurück: Esc, B oder Rechtsklick.", 2);
        yield return new ButtonWidget("Weiter: Mods auswählen", () => _window!.OpenPage(ModsPage));
    }

    private IEnumerable<Widget> ModuleRows()
    {
        yield return new InfoWidget("Schalte aus, was du nicht brauchst (alles ist anfangs an). Das geht später jederzeit im Mod-Menü (F10, Seite Features).", 2);
        string? group = null;
        foreach (var m in Modules)
        {
            var info = Context.Features.Find(m[1]);
            if (info == null) continue;
            if (m[0] != group)
            {
                group = m[0];
                yield return new HeaderWidget(group);
            }

            var f = info;
            yield return new ToggleWidget(m[2], () => f.Enabled.Value && f.State == FeatureState.Running, on => SetFeature(Context, f, on))
            {
                Hint = m[3] + (f.State is FeatureState.Faulted or FeatureState.MissingCapabilities ? "  (" + f.State + ": " + f.Detail + ")" : string.Empty),
            };
        }

        yield return new ButtonWidget("Weiter: Twitch", () => _window!.OpenPage("Twitch"));
    }

    private IEnumerable<Widget> TwitchRows()
    {
        var tw = Context.Get<TwitchFeature>();
        yield return new InfoWidget("Mit Twitch spielt dein Chat mit: Abstimmungen zu jeder Frage, Wetten auf den Sieger, Namen für Gegner, "
            + "oder der Chat spielt selbst gegen dich. Ohne Twitch funktionieren alle anderen Mods genauso.", 3);
        if (tw == null)
        {
            yield return new InfoWidget("Die Twitch-Verbindung ist ausgeschaltet (Seite Mods).");
            yield break;
        }

        yield return new InfoWidget(() => tw.AccountLogin.Length > 0
            ? "Angemeldet als " + tw.AccountLogin + "."
            : tw.HasClientId ? "Noch nicht angemeldet. Die Anmeldung dauert eine Minute: Code im Browser bestätigen." : "Noch nicht angemeldet. Du brauchst eine Client ID einer eigenen Twitch-Anwendung (Anleitung: docs\\twitch-setup.md).", 2);
        var mods = Context.Get<ModMenuFeature>();
        if (mods != null)
        {
            yield return new ButtonWidget("Twitch einrichten", () => mods.Open("Twitch", _window), () => tw.AccountLogin.Length > 0 ? "verwalten" : "anmelden")
            {
                Hint = "Öffnet die Twitch-Seite des Mod-Menüs; Zurück führt wieder hierher.",
            };
        }

        var op = Context.Get<OpponentFeature>();
        if (op != null)
        {
            yield return new ToggleWidget("Twitch als Gegner in der nächsten Show", () => op.IsActive, v => op.SetActive(v))
            {
                Hint = "Der Chat übernimmt den KI-Gegner. Modus (ganzer Chat, ein Zuschauer, ...) im Mod-Menü, Seite Publikum.",
            };
        }

        yield return new ToggleWidget("Testmodus (nichts im Spiel anwenden)", () => tw.TestMode, v => tw.TestMode = v)
        {
            Hint = "Zum Ausprobieren: Chat-Befehle werden nur protokolliert, nicht angewendet und nicht beantwortet.",
        };
        yield return new ButtonWidget("Weiter", () => _window!.OpenPage("Los geht's"));
    }

    private IEnumerable<Widget> StartRows()
    {
        var quick = Context.Get<QuickplayFeature>();
        yield return new InfoWidget("Fertig. Die Haupttür startet eine Show"
            + (quick != null && quick.Enabled.Value ? " direkt mit Schnellstart (Tastatur gegen KI; Aufstellung im Mod-Menü, Seite Schnellstart)." : " mit der normalen Spielerauswahl.")
            + " Dieses Fenster findest du jederzeit im Mod-Menü (F10, Seite Komfort).", 3);
        yield return new ToggleWidget("Bei jedem Start zeigen", () => _showAgain.Value, v => _showAgain.Value = v);
        yield return new ButtonWidget("Los geht's", () => _window!.Close());
    }

    /// <summary>Switches a module on (with the modules it needs) or off (with the modules that need it).</summary>
    internal static void SetFeature(ModContext ctx, FeatureInfo info, bool on)
    {
        if (!on)
        {
            info.Enabled.Value = false;
            ctx.Features.Stop(info, "switched off in a menu");
            return;
        }

        foreach (var dep in info.Feature.Dependencies)
        {
            var d = ctx.Features.Find(dep);
            if (d != null && d.State != FeatureState.Running) SetFeature(ctx, d, true);
        }

        info.Enabled.Value = true;
        ctx.Features.TryStart(info, ctx, ctx.Capabilities);
    }
}
