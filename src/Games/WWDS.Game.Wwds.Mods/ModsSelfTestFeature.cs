using System;
using System.Collections.Generic;
using System.Linq;
using InControl;
using UnityEngine;
using UnityEngine.Audio;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Input;
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

        if (_mode.Value == "Custom")
        {
            BuildCustom();
            return;
        }

        if (_mode.Value == "Long")
        {
            BuildLong();
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
        // Does Unity scale a source by the listener volume before its filters (the voice leveler must know)?
        // A tone on the muted Voice group is measured with the listener at 1 and at 0.5 (mixer silent).
        AudioProbe? probe = null;
        var full = 0f;
        Do("audio probe: tone on the muted mixer", () =>
        {
            probe = AudioProbe.Start();
            GameSettingsFeature.ListenerOverride = 1f;
            return "ok";
        });
        Wait(0.8);
        Do("audio probe: settle", () => probe!.TakeRms().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Wait(1.5);
        Do("audio probe: level at listener 1", () =>
        {
            full = probe!.TakeRms();
            if (full <= 0f) throw new InvalidOperationException("the probe filter got no audio");
            GameSettingsFeature.ListenerOverride = 0.5f;
            return "rms " + full.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        Wait(0.8);
        Do("audio probe: settle again", () => probe!.TakeRms().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Wait(1.5);
        Do("audio probe: listener volume before source filters", () =>
        {
            var half = probe!.TakeRms();
            GameSettingsFeature.ListenerOverride = 1f;
            var ratio = half / full;
            Log.Info("[SelfTest] listener 0.5 gives the source filter " + ratio.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " of the level at 1");
            if (Math.Abs(ratio - 0.5f) > 0.08f) throw new InvalidOperationException("ratio " + ratio + ": the listener volume is not applied before the filters, the leveler compensates wrongly");
            return "yes (ratio " + ratio.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ")";
        });
        var known = 0;
        Do("voice leveler: play a director line (mixer still silent)", () =>
        {
            known = Context.Get<VoiceLevelFeature>()?.Known ?? throw new InvalidOperationException("VoiceLevel feature not running");
            SSoundManager.Instance.PlayVoiceFile(EVoiceType.Director, string.Empty, "mainmenu_welcome", "intro");
            return known + " lines known before";
        });
        Add("voice leveler learns the line", 30, () => Context.Get<VoiceLevelFeature>()!.Known > known);
        Do("audio probe: end, silent again", () =>
        {
            GameSettingsFeature.ListenerOverride = null;
            AudioListener.volume = 0f;
            probe!.Stop();
            return Context.Get<VoiceLevelFeature>()!.Known + " lines known";
        });
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

    /// <summary>A temporary own question pack (written by the runner): its questions, no video hang, own save file.</summary>
    private void BuildCustom()
    {
        var cq = Context.Get<CustomQuestionsFeature>() ?? throw new InvalidOperationException("CustomQuestions feature not running");
        var id = 0;
        Add("self-test pack loaded", 20, () =>
        {
            id = cq.Packs.FirstOrDefault(p => p.Pack.Id == "zz-selftest")?.ShowIds.FirstOrDefault() ?? 0;
            return id > 0;
        });
        Add("own voice line (wav) loaded", 20, () =>
        {
            var voices = Context.Get<CustomVoiceFeature>();
            return voices != null && !voices.Loading && voices.HasLine("wwds_selftest_line");
        });
        Do("quickplay the pack show", () =>
        {
            var quick = Context.Get<QuickplayFeature>()!;
            quick.ShowId.Value = id;
            return "show " + id + ": " + quick.StartFromMainMenu();
        });
        ReachSelection();
        Add("show loop", 90, () => _ui.State == "StateShowLoop");
        Add("pack show active", 5, () => cq.Active && SDataAccess.ActiveSession != null && SDataAccess.ActiveSession.ActiveShowIndex == id);
        PressUntil("first question starts", 180, () => _questionStarted);
        Do("question comes from the pack", () =>
        {
            var q = SDataAccess.ActiveSession!.GetActiveQuestionData();
            if (!q.Frage.StartsWith("Selbsttest Frage", StringComparison.Ordinal)) throw new InvalidOperationException("question: " + q.Frage);
            return q.Frage + " / " + q.A + ", " + q.B + ", " + q.C;
        });
        Do("capture question", () => Capture("custom_question.png"));
        var saves = Application.persistentDataPath;
        PressUntil("round completes without videos hanging (bot plays, human answers C)", 300, () => System.IO.File.Exists(System.IO.Path.Combine(saves, "Sessions_Custom.bin")));
        Do("pack show progress only in Sessions_Custom", () =>
        {
            var own = System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(System.IO.Path.Combine(saves, "Sessions.bin")));
            if (own.Contains("\"ShowID\":" + id)) throw new InvalidOperationException("show " + id + " found in Sessions.bin");
            var custom = System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(System.IO.Path.Combine(saves, "Sessions_Custom.bin")));
            if (!custom.Contains("\"ShowID\":" + id)) throw new InvalidOperationException("show " + id + " missing in Sessions_Custom.bin");
            return "Sessions_Custom.bin has show " + id;
        });
        Do("back to the main menu", () =>
        {
            Context.Get<EscMenuFeature>()!.EndShow();
            return "ok";
        });
        Add("main menu", 60, () => _ui.State == "StateScreenMainMenu");
    }

    /// <summary>A long show with 2 boards (shortened to 2 questions each): board change, final, resume on board 2.</summary>
    private void BuildLong()
    {
        var lg = Context.Get<LongGameFeature>() ?? throw new InvalidOperationException("LongGame feature not running");
        var start = 0;
        Do("quickplay a long show", () => Context.Get<QuickplayFeature>()!.StartFromMainMenu());
        ReachSelection();
        Add("show loop", 90, () => _ui.State == "StateShowLoop");
        Do("boards of 2 questions", () =>
        {
            SDataStore.Instance.normalShowQuestions = 2;
            start = SDataAccess.ActiveSession!.ActiveShowIndex;
            return "show " + start + ", " + lg.Status;
        });
        Add("long show has 2 boards", 5, () => lg.Status == "Brett 1 von 2");
        var firstBoard = string.Empty;
        Do("board 1 categories", () => firstBoard = SDataAccess.ActiveSession!.CurrentShow.dataArray[0].Kategoriename);
        PressUntil("board 2 starts after 2 questions", 600, () => lg.Status == "Brett 2 von 2");
        Add("board 2 has new categories", 120, () => SDataAccess.ActiveSession!.CurrentShow.dataArray[0].Kategoriename != firstBoard);
        Add("progress cleared for board 2", 5, () => SDataAccess.ActiveSession!.GetQuestionCount() == 0);
        PressUntil("a board 2 question is answered (saved)", 400, () => SDataAccess.ActiveSession!.GetQuestionCount() >= 1);
        Do("capture board 2", () => Capture("long_board2.png"));
        Do("leave the show", () =>
        {
            Context.Get<EscMenuFeature>()!.EndShow();
            return "ok";
        });
        Add("main menu", 60, () => _ui.State == "StateScreenMainMenu");
        Wait(2);
        Do("resume the long show", () =>
        {
            MainMenuController.Instance.StartShow(true, start);
            return "show " + start;
        });
        Add("resumed show loop", 120, () => _ui.State == "StateShowLoop");
        Add("resumed on board 2", 5, () => lg.ResumedOn == 2 && lg.Status == "Brett 2 von 2" && SDataAccess.ActiveSession!.CurrentShow.dataArray[0].Kategoriename != firstBoard);
        Do("boards of 2 questions again", () =>
        {
            SDataStore.Instance.normalShowQuestions = 2;
            return "ok";
        });
        PressUntil("final question after the last board", 600, () => SDataAccess.ActiveSession!.GetActiveQuestionData().EQUESTIONTYPE != EQuestionType.Question);
        Do("back to the main menu", () =>
        {
            Context.Get<EscMenuFeature>()!.EndShow();
            return "ok";
        });
        Add("main menu at the end", 60, () => _ui.State == "StateScreenMainMenu");
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
        var presses = KeyboardLayout.Map(phase, false, new[] { "Alpha" + digit });
        if (presses.Count != 1) throw new InvalidOperationException("phase " + phase + " gives " + presses.Count + " presses");
        var control = KeyboardFeature.ToControl(presses[0].Button);
        _ui.Pad.Press(VirtualPad.Keyboard!, control);
        return phase + " -> " + control;
    }

    private string Press(InputControlType control)
    {
        var mouse = Context.Get<MouseFeature>() ?? throw new InvalidOperationException("Mouse feature not running");
        mouse.TestPress(control);
        return control.ToString();
    }

    protected override void OnStop()
    {
        GameSettingsFeature.ForceMute = false;
        GameSettingsFeature.ListenerOverride = null;
    }

    public override void Tick()
    {
        if (_index >= _steps.Count) return;
        if (GameSettingsFeature.ForceMute && GameSettingsFeature.ListenerOverride == null && AudioListener.volume > 0f) AudioListener.volume = 0f;
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

    /// <summary>A quiet test tone on the Voice group with the mixer fully muted, and a filter that measures it.</summary>
    private sealed class AudioProbe
    {
        private readonly GameObject _go;
        private readonly Dictionary<string, float> _saved = new();
        private readonly AudioMixer? _mixer;
        private readonly ProbeFilter _filter;

        private AudioProbe()
        {
            var group = HarmonyLib.AccessTools.Field(typeof(SSoundManager), "VoiceAudioMixer").GetValue(SSoundManager.Instance) as AudioMixerGroup;
            _mixer = group?.audioMixer;
            foreach (var name in new[] { "volMusic", "volSFX", "volUI", "volAmbient", "volVoice" })
            {
                if (_mixer != null && _mixer.GetFloat(name, out var value))
                {
                    _saved[name] = value;
                    _mixer.SetFloat(name, -80f);
                }
            }

            var samples = new float[48000];
            for (var i = 0; i < samples.Length; i++) samples[i] = 0.1f * Mathf.Sin(2f * Mathf.PI * 440f * i / 48000f);
            var clip = AudioClip.Create("wwds_probe", samples.Length, 1, 48000, false);
            clip.SetData(samples, 0);
            _go = new GameObject("WWDS_AudioProbe");
            var source = _go.AddComponent<AudioSource>();
            source.outputAudioMixerGroup = group;
            source.clip = clip;
            source.loop = true;
            _filter = _go.AddComponent<ProbeFilter>();
            source.Play();
        }

        public static AudioProbe Start() => new();

        public float TakeRms() => _filter.Take();

        public void Stop()
        {
            UnityEngine.Object.Destroy(_go);
            foreach (var p in _saved) _mixer?.SetFloat(p.Key, p.Value);
        }
    }

    private sealed class ProbeFilter : MonoBehaviour
    {
        private readonly object _lock = new();
        private double _sum;
        private long _count;

        public float Take()
        {
            lock (_lock)
            {
                var rms = _count == 0 ? 0f : (float)Math.Sqrt(_sum / _count);
                _sum = 0;
                _count = 0;
                return rms;
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            lock (_lock)
            {
                for (var i = 0; i < data.Length; i++) _sum += data[i] * data[i];
                _count += data.Length;
            }
        }
    }
}
