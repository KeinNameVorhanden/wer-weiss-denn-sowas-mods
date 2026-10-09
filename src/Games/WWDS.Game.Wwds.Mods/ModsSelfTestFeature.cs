using System;
using System.Collections.Generic;
using System.Linq;
using InControl;
using UnityEngine;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Model;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Unattended runtime check of the player-facing mods (off by default; tools/Run-ModsSelfTest.ps1 turns it
/// on in a test config): every mod menu page builds, the ESC menu opens, volumes round-trip, quickplay
/// starts a show from the door screen, a category and an answer are chosen through the mouse's virtual
/// presses (answer via its button glyph), Start opens the ESC menu and the show ends back in the main
/// menu. Results are logged as "[SelfTest] PASS/FAIL ..." and the game quits at the end.
/// </summary>
public sealed class ModsSelfTestFeature : FeatureBase
{
    public const string FeatureId = "ModsSelfTest";

    private readonly List<Step> _steps = new();
    private readonly List<string> _failures = new();
    private int _index;
    private double _stepStart = -1;
    private double _waitUntil;
    private bool _categoryTurn;
    private bool _questionStarted;
    private bool _answerTurn;
    private AnswerLocked? _locked;
    private ModUi _ui = null!;
    private double _nextNotice;
    private bool _watching;

    /// <summary>
    /// A person takes over with a key or a click inside the focused game window (virtual presses never show
    /// up in UnityEngine.Input). Watched from the main menu on; hovering, e.g. the taskbar preview, never counts.
    /// </summary>
    private bool UserTookOver()
    {
        if (!_watching)
        {
            _watching = _ui.State == "StateScreenMainMenu";
            return false;
        }

        return Application.isFocused && Input.anyKeyDown;
    }

    public override string Id => FeatureId;

    public override string DisplayName => "Mods self-test";

    public override string Description => "Unattended runtime check of the mod menus, quickplay and mouse presses (testing only).";

    public override bool EnabledByDefault => false;

    protected override string LogCategoryName => "Mods";

    private ModCore.Config.IConfigValue<string> _mode = null!;
    private ModCore.Config.IConfigValue<string> _shots = null!;

    protected override void OnStart()
    {
        _mode = Context.Config.Bind("ModsSelfTest", "Mode", "Full", "Full | Screens (only captures the menus).");
        _shots = Context.Config.Bind("ModsSelfTest", "ScreenshotDir", "", "Folder for screenshots of the mod UI (empty = none).");
        _ui = ModUi.For(Context);
        // Unattended runs are always silent; the player's volume settings are not touched.
        GameSettingsFeature.ForceMute = true;
        AudioListener.volume = 0f;
        Track(Context.Bus.Subscribe<RoundStarted>(e =>
        {
            if (e.Team == TeamSlot.A && e.Contestant?.Kind == ContestantKind.Human && e.Kind == QuestionKind.Normal) _categoryTurn = true;
        }));
        Track(Context.Bus.Subscribe<QuestionStarted>(_ => _questionStarted = true));
        Track(Context.Bus.Subscribe<AnswersPresented>(e =>
        {
            if (e.Answering?.Team == TeamSlot.A && e.Answering.Kind == ContestantKind.Human) _answerTurn = true;
        }));
        Track(Context.Bus.Subscribe<AnswerLocked>(e =>
        {
            if (e.Team == TeamSlot.A) _locked = e;
        }));
        Build();
        Log.Info("Self-test armed: " + _steps.Count + " steps");
    }

    private void Build()
    {
        Add("main menu reached", 120, () => _ui.State == "StateScreenMainMenu");
        Wait(3);
        if (_mode.Value == "Screens")
        {
            BuildScreens();
            return;
        }

        if (_mode.Value == "Catalogue")
        {
            BuildCatalogue();
            return;
        }

        Do("mod menu pages build", () =>
        {
            var menu = Context.Get<ModMenuFeature>() ?? throw new InvalidOperationException("ModMenu feature not running");
            menu.Open(null);
            var window = menu.Window!;
            var report = new List<string>();
            foreach (var page in window.Pages.ToList())
            {
                window.OpenPage(page.Title);
                report.Add(page.Title + "=" + window.RowCount);
                if (window.LastError != null) throw new InvalidOperationException(window.LastError);
                if (window.RowCount == 0) throw new InvalidOperationException("page " + page.Title + " is empty");
            }

            window.Close();
            return string.Join(", ", report.ToArray());
        });
        Wait(1);
        Do("esc menu opens on the door screen", () =>
        {
            var esc = Context.Get<EscMenuFeature>()!;
            esc.Open();
            if (!esc.IsOpen) throw new InvalidOperationException("not open");
            return "ok";
        });
        Wait(0.5);
        Do("settings opened from the ESC menu", () =>
        {
            var esc = Context.Get<EscMenuFeature>()!;
            Context.Get<ModMenuFeature>()!.Open(ModMenuFeature.GamePage, esc.WindowForTest);
            if (esc.IsOpen) throw new InvalidOperationException("ESC menu still open");
            return "ok";
        });
        Wait(0.5);
        Do("back from settings returns to the ESC menu", () =>
        {
            Context.Get<ModMenuFeature>()!.Window!.Close();
            var esc = Context.Get<EscMenuFeature>()!;
            if (!esc.IsOpen) throw new InvalidOperationException("ESC menu did not come back");
            esc.Close();
            return "ok";
        });
        Wait(1);
        Do("menu settings are written to the config file", () =>
        {
            var gs = Context.Get<GameSettingsFeature>()!;
            var cfg = System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, string.Empty), "wwds.harness.cfg");
            var before = gs.MuteInBackground;
            gs.MuteInBackground = !before;
            var text = System.IO.File.ReadAllText(cfg);
            gs.MuteInBackground = before;
            var expected = "MuteInBackground = " + (!before ? "true" : "false");
            if (!text.Contains(expected)) throw new InvalidOperationException("'" + expected + "' not in " + cfg);
            return expected + " saved";
        });
        Do("volume round trip in game steps", () =>
        {
            var gs = Context.Get<GameSettingsFeature>()!;
            var before = gs.Volume(GameSettingsFeature.Channel.Music);
            gs.SetVolume(GameSettingsFeature.Channel.Music, 0.73f);
            var stored = SDataStore.Instance.SettingsData.MusicVolume;
            gs.SetMuted(GameSettingsFeature.Channel.Music, true);
            gs.SetMuted(GameSettingsFeature.Channel.Music, false);
            gs.SetVolume(GameSettingsFeature.Channel.Music, before);
            if (Math.Abs(stored - 0.7f) > 0.001f) throw new InvalidOperationException("stored " + stored + ", expected 0.7");
            // The run itself stays silent (forced mute), so the volume rule is checked without making noise.
            var plain = GameSettingsFeature.TargetVolume(0.5f, muteInBackground: true, focused: true, forceMute: false);
            var background = GameSettingsFeature.TargetVolume(0.5f, muteInBackground: true, focused: false, forceMute: false);
            if (Math.Abs(plain - 0.5f) > 0.001f || background != 0f) throw new InvalidOperationException("volume rule: " + plain + " / " + background);
            if (AudioListener.volume != 0f) throw new InvalidOperationException("self-test is not silent: " + AudioListener.volume);
            return "0.73 stored as " + stored + ", master 0.5 -> " + plain + ", background -> 0, run silent";
        });
        Do("quickplay starts from the door screen", () => Context.Get<QuickplayFeature>()!.StartFromMainMenu());
        ReachSelection();
        Add("show starts with the line-up", 40, () => _ui.State is "StateShowIntro" or "StateShowLoop");
        Add("show loop (intro skipped)", 60, () => _ui.State == "StateShowLoop");
        Add("human category turn", 120, () => _categoryTurn);
        Wait(2.5);
        Do("arm question flag", () =>
        {
            _questionStarted = false;
            return "armed";
        });
        PressUntil("category by virtual presses (propose, log in)", 20, () => _questionStarted);
        Add("answers open for the human", 90, () => _answerTurn);
        Wait(1);
        Do("answer A by its button glyph", () =>
        {
            var button = GameObject.Find("AnswerButtonA") ?? throw new InvalidOperationException("AnswerButtonA not found");
            var face = MouseFeature.GlyphFace(button) ?? throw new InvalidOperationException("no glyph under AnswerButtonA");
            if (face != InputControlType.Action4) throw new InvalidOperationException("glyph maps to " + face + ", expected Action4 (Y)");
            return Press(face);
        });
        Wait(1);
        Do("second press confirms the answer", () => Press(InputControlType.Action4));
        Add("vote window shows the audience vote", 5, () => Context.Get<VoteHudFeature>()!.Visible);
        Do("capture live vote window", () => Capture("hud_live.png"));
        Add("answer A locked", 10, () => _locked != null && _locked.OptionIndex == 0);
        Do("wait for the next human turn", () =>
        {
            _categoryTurn = false;
            _answerTurn = false;
            _locked = null;
            return "armed";
        });
        Add("next human category turn", 240, () => _categoryTurn);
        Wait(2.5);
        Do("arm second question", () =>
        {
            _questionStarted = false;
            return "armed";
        });
        PressUntil("second category by Enter presses", 20, () => _questionStarted);
        Add("next human answer turn", 90, () => _answerTurn);
        Wait(1);
        Do("keyboard shortcut 2 selects answer B", () => Shortcut(2));
        Wait(1);
        Do("keyboard shortcut 2 confirms", () => Shortcut(2));
        Add("answer B locked by keyboard", 10, () => _locked != null && _locked.OptionIndex == 1);
        Wait(4);
        Do("start button opens the ESC menu", () => Press(InputControlType.Start));
        Add("ESC menu open (pause replaced)", 5, () => Context.Get<EscMenuFeature>()!.IsOpen);
        Wait(1);
        Do("back to the main menu", () =>
        {
            Context.Get<EscMenuFeature>()!.EndShow();
            return "ok";
        });
        Add("main menu after the show", 60, () => _ui.State == "StateScreenMainMenu");
    }

    /// <summary>Show 31 = the other game's show 1: bundles and videos from its install, progress in the separate file.</summary>
    private void BuildCatalogue()
    {
        var cat = Context.Get<CatalogueFeature>() ?? throw new InvalidOperationException("Catalogue feature not running");
        Add("other game's catalogue available", 20, () => cat.Active);
        Do("catalogue source", () => cat.Other!.Name + " at " + cat.Other.StreamingAssets);
        Do("quickplay show 31", () => Context.Get<QuickplayFeature>()!.StartFromMainMenu());
        ReachSelection();
        Add("show loop", 90, () => _ui.State == "StateShowLoop");
        Add("show 31 loads the other game's bundles", 5, () => cat.ForeignShowActive && SDataAccess.ActiveSession != null && SDataAccess.ActiveSession.ActiveShowIndex == 31);
        Add("first question starts", 120, () => _questionStarted);
        Do("capture question", () => Capture("catalogue_question.png"));
        var saves = Application.persistentDataPath;
        PressUntil("round completes (bot plays, human answers C)", 300, () => System.IO.File.Exists(System.IO.Path.Combine(saves, cat.CatalogueSaveFile + ".bin")));
        Add("question video came from the other game", 60, () => cat.ForeignVideos > 0);
        Do("show 31 progress only in the separate file", () =>
        {
            var own = System.IO.File.ReadAllBytes(System.IO.Path.Combine(saves, "Sessions.bin"));
            var text = System.Text.Encoding.UTF8.GetString(own);
            if (text.Contains("\"ShowID\":31")) throw new InvalidOperationException("show 31 found in Sessions.bin");
            var other = System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(System.IO.Path.Combine(saves, cat.CatalogueSaveFile + ".bin")));
            if (!other.Contains("\"ShowID\":31")) throw new InvalidOperationException("show 31 missing in " + cat.CatalogueSaveFile);
            return cat.CatalogueSaveFile + ".bin has show 31";
        });
        Do("back to the main menu", () =>
        {
            Context.Get<EscMenuFeature>()!.EndShow();
            return "ok";
        });
        Add("main menu", 60, () => _ui.State == "StateScreenMainMenu");
    }

    private void BuildScreens()
    {
        Add("welcome window opens by itself", 15, () => Context.Get<FirstRunFeature>()?.Window?.IsOpen == true);
        Wait(1);
        foreach (var title in new[] { "Willkommen", FirstRunFeature.ModsPage, "Twitch", "Los geht's" })
        {
            var t = title;
            Do("welcome page " + t, () =>
            {
                Context.Get<FirstRunFeature>()!.Window!.OpenPage(t);
                return "ok";
            });
            Wait(1);
            Do("capture welcome " + t, () => Capture("welcome_" + t.Replace("'", string.Empty).Replace(' ', '_') + ".png"));
            Wait(0.5);
        }

        Do("close welcome", () =>
        {
            Context.Get<FirstRunFeature>()!.Window!.Close();
            return "ok";
        });
        Wait(1);
        var titles = new[] { "Spiel", "Twitch", "Publikum", "Schnellstart", "Komfort", "Features", "Alle Einstellungen" };
        for (var i = 0; i < titles.Length; i++)
        {
            var title = titles[i];
            var n = i;
            Do("open " + title, () =>
            {
                Context.Get<ModMenuFeature>()!.Open(title);
                return "ok";
            });
            Wait(1);
            Do("capture " + title, () => Capture("menu_" + n + ".png"));
            Wait(1);
        }

        Do("close menu", () =>
        {
            Context.Get<ModMenuFeature>()!.Window!.Close();
            Context.Get<EscMenuFeature>()!.Open();
            return "ok";
        });
        Wait(1);
        Do("capture esc", () => Capture("esc.png"));
        Wait(1);
        Do("quit door confirmation", () =>
        {
            Context.Get<EscMenuFeature>()!.Close();
            Context.Get<EscMenuFeature>()!.ShowQuitConfirmForTest();
            return "ok";
        });
        Wait(1);
        Do("capture quit", () => Capture("quit.png"));
        Do("vote window preview and toast", () =>
        {
            Context.Get<EscMenuFeature>()!.CloseQuitConfirm();
            Context.Get<VoteHudFeature>()!.Preview(10);
            _ui.Toast("Show gespeichert", 6, "selftest");
            return "ok";
        });
        Wait(1);
        Add("vote window visible", 3, () => Context.Get<VoteHudFeature>()!.Visible);
        Do("capture vote window", () => Capture("hud.png"));
        Wait(1);
    }

    /// <summary>In-game capture of the game window (only when a screenshot folder is configured).</summary>
    private string Capture(string name)
    {
        if (_shots.Value.Length == 0) return "no folder";
        var path = System.IO.Path.Combine(_shots.Value, name);
        ScreenCapture.CaptureScreenshot(path);
        return path;
    }

    private string Shortcut(int digit)
    {
        var phase = Context.Adapter?.GetSnapshot().Phase ?? GamePhase.Unknown;
        var controls = KeyboardFeature.Controls(phase, digit, false).ToList();
        if (controls.Count != 1) throw new InvalidOperationException("phase " + phase + " gives " + controls.Count + " controls");
        _ui.Pad.Press(VirtualPad.Keyboard!, controls[0]);
        return phase + " -> " + controls[0];
    }

    private string Press(InputControlType control)
    {
        var mouse = Context.Get<MouseFeature>() ?? throw new InvalidOperationException("Mouse feature not running");
        mouse.TestPress(control);
        return control.ToString();
    }

    protected override void OnStop() => GameSettingsFeature.ForceMute = false;

    public override void Tick()
    {
        if (_index >= _steps.Count) return;
        if (GameSettingsFeature.ForceMute && AudioListener.volume > 0f) AudioListener.volume = 0f;
        var now = Context.Clock.Now;
        if (UserTookOver())
        {
            // A person is playing: step aside, keep the game open (the runner waits for it to close).
            _index = _steps.Count;
            SwitchOff();
            Log.Info("[SelfTest] ABORTED by user input: the game stays open");
            _ui.Toast("Selbsttest abgebrochen: das Spiel gehört dir (Ton bleibt bis zum Neustart aus)", 8, "selftest");
            return;
        }

        if (now >= _nextNotice)
        {
            _nextNotice = now + 5;
            _ui.Toast("Automatischer Selbsttest läuft: eine Taste oder ein Klick im Spiel bricht ihn ab", 5.5f, "selftest");
        }

        if (now < _waitUntil) return;
        var step = _steps[_index];
        if (_stepStart < 0) _stepStart = now;
        string? result;
        try
        {
            result = step.Run();
        }
        catch (Exception ex)
        {
            Fail(step.Name + ": " + ex.Message);
            return;
        }

        if (result == null)
        {
            if (now - _stepStart > step.Timeout) Fail(step.Name + ": timed out after " + step.Timeout + " s (state " + _ui.State + ")");
            return;
        }

        if (step.Name.Length > 0) Log.Info("[SelfTest] PASS " + step.Name + (result.Length > 0 ? " (" + result + ")" : string.Empty));
        Next(now + step.After);
    }

    private void Fail(string message)
    {
        _failures.Add(message);
        Log.Error("[SelfTest] FAIL " + message);
        // A failed step makes the rest meaningless: report and quit.
        _index = _steps.Count;
        Finish();
    }

    private void Next(double resumeAt)
    {
        _index++;
        _stepStart = -1;
        _waitUntil = resumeAt;
        if (_index >= _steps.Count) Finish();
    }

    /// <summary>The test never runs again by itself, even if the runner that switched it on was stopped.</summary>
    private void SwitchOff()
    {
        var info = Context.Features.Find(FeatureId);
        if (info != null) info.Enabled.Value = false;
    }

    private void Finish()
    {
        SwitchOff();
        Log.Info("[SelfTest] DONE " + (_failures.Count == 0 ? "all passed" : _failures.Count + " failed"));
        Context.Scheduler.After(2, () =>
        {
            if (Context.Host != null) Context.Host.QuitGame();
        });
    }

    /// <summary>Presses A every 1.5 s (like a player clicking again) until <paramref name="done"/>.</summary>
    private void PressUntil(string name, double timeout, Func<bool> done)
    {
        var next = 0.0;
        var presses = 0;
        _steps.Add(new Step(name, timeout, () =>
        {
            if (done()) return presses + " presses";
            if (Context.Clock.Now < next) return null;
            next = Context.Clock.Now + 1.5;
            presses++;
            Press(InputControlType.Action1);
            return null;
        }));
    }

    /// <summary>
    /// Waits for the player selection. A saved unfinished show makes the Main door ask "continue?" first:
    /// the test answers "new show" (B, then A on the hint) like a player; the saved show stays untouched.
    /// </summary>
    private void ReachSelection()
    {
        var next = 0.0;
        var answered = 0;
        _steps.Add(new Step("selection screen", 30, () =>
        {
            if (_ui.State == "StateScreenSelectPlayers") return answered > 0 ? "answered the resume question with 'new show'" : string.Empty;
            var menu = MainMenuController.Instance;
            if (menu == null || Context.Clock.Now < next) return null;
            var t = typeof(MainMenuController);
            var dialog = (bool)HarmonyLib.AccessTools.Field(t, "loadSavegame").GetValue(menu);
            var hint = (bool)HarmonyLib.AccessTools.Field(t, "loadSavegameHintActive").GetValue(menu);
            if (!dialog && !hint) return null;
            next = Context.Clock.Now + 1.5;
            answered++;
            Press(hint ? InputControlType.Action1 : InputControlType.Action2);
            return null;
        }));
    }

    private void Add(string name, double timeout, Func<bool> condition) => _steps.Add(new Step(name, timeout, () => condition() ? string.Empty : null));

    private void Do(string name, Func<string> action) => _steps.Add(new Step(name, 0, () => action()));

    private void Wait(double seconds) => _steps.Add(new Step(string.Empty, 0, () => string.Empty) { After = seconds });

    private sealed class Step
    {
        public Step(string name, double timeout, Func<string?> run)
        {
            Name = name;
            Timeout = timeout;
            Run = run;
        }

        public string Name { get; }

        public double Timeout { get; }

        public Func<string?> Run { get; }

        public double After { get; set; }
    }
}
