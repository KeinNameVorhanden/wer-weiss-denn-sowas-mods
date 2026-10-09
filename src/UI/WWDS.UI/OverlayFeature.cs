using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WWDS.Audience;
using WWDS.Audience.Engine;
using WWDS.Audience.Guessing;
using WWDS.Audience.Naming;
using WWDS.Audience.Opponents;
using WWDS.Audience.Statistics;
using WWDS.Audience.Voting;
using WWDS.ModCore.Adapters;
using WWDS.ModCore.Capabilities;
using WWDS.ModCore.Config;
using WWDS.ModCore.Events;
using WWDS.ModCore.Features;
using WWDS.ModCore.Logging;
using WWDS.ModCore.Model;
using WWDS.ModCore.Threading;

namespace WWDS.UI;

/// <summary>
/// IMGUI control panel (default F8; the vote window lives in the mods' VoteHud feature). Shows game, loader, adapter, Twitch,
/// question, scores and the running audience interaction; offers feature toggles, moderator controls,
/// mock audience buttons, patch status and the event log. Deep object inspection is left to
/// RuntimeUnityEditor (F12).
/// </summary>
public sealed class OverlayFeature : FeatureBase, IGuiLayer
{
    public const string FeatureId = "Overlay";

    private static readonly string[] Tabs = { "Status", "Audience", "Features", "Events", "Debug" };

    private IConfigValue<string> _panelKey = null!;
    private IConfigValue<float> _scale = null!;
    private IConfigValue<bool> _showCorrect = null!;
    private IConfigValue<bool> _startOpen = null!;
    private IConfigValue<float> _cycleTabs = null!;
    private double _nextTabSwitch;
    private Rect _window = new(40, 40, 560, 620);
    private Vector2 _scroll;
    private int _tab;
    private bool _panelOpen;
    private bool _cursorVisible;
    private CursorLockMode _cursorLock;
    private AudienceConnectionChanged? _link;
    private InteractionStatusChanged _interaction = InteractionStatusChanged.Idle;
    private AudienceSimulator? _simulator;
    private string _lastAction = string.Empty;
    private GUIStyle? _small;

    public override string Id => FeatureId;

    public override string DisplayName => "Overlay";

    public override string Description => "Control panel (F8) for status, audience moderation, features and events.";

    protected override string LogCategoryName => LogCategory.UI;

    public int Order => 100;

    protected override void OnStart()
    {
        var c = Context.Config;
        _panelKey = c.Bind("UI", "PanelHotkey", "F8", "Key that toggles the control panel (UnityEngine.KeyCode name).");
        _scale = c.Bind("UI", "PanelScale", 1f, "Size of the F8 control panel (0.75 to 2).");
        _showCorrect = c.Bind("UI", "ShowCorrectAnswerInPanel", false, "Mark the correct answer in the panel (spoiler on stream!).");
        _startOpen = c.Bind("UI", "StartOpen", false, "Open the control panel at startup.");
        _cycleTabs = c.Bind("UI", "DebugCycleTabsSeconds", 0f, "Test aid: switch panel tabs every N seconds (0 = off).");
        if (_startOpen.Value) SetPanel(true);
        Context.GuiLayers.Add(this);
        _simulator = new AudienceSimulator(Context);
        Track(Context.Bus.Subscribe<AudienceConnectionChanged>(e => _link = e));
        Track(Context.Bus.Subscribe<InteractionStatusChanged>(e => _interaction = e));
    }

    protected override void OnStop()
    {
        Context.GuiLayers.Remove(this);
        if (_panelOpen) SetPanel(false);
    }

    public override void Tick()
    {
        if (Pressed(_panelKey.Value)) SetPanel(!_panelOpen);
        if (_cycleTabs.Value > 0 && Context.Clock.Now >= _nextTabSwitch)
        {
            _nextTabSwitch = Context.Clock.Now + _cycleTabs.Value;
            _tab = (_tab + 1) % Tabs.Length;
        }

        if (_panelOpen)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    private static bool Pressed(string key)
    {
        try
        {
            return Input.GetKeyDown((KeyCode)Enum.Parse(typeof(KeyCode), key, true));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void SetPanel(bool open)
    {
        if (open)
        {
            _cursorVisible = Cursor.visible;
            _cursorLock = Cursor.lockState;
        }
        else
        {
            Cursor.visible = _cursorVisible;
            Cursor.lockState = _cursorLock;
        }

        _panelOpen = open;
    }

    public void Draw()
    {
        _small ??= new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        if (!_panelOpen) return;
        var scale = Mathf.Clamp(_scale.Value, 0.75f, 2f);
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
        _window = GUILayout.Window(0x57574453, _window, DrawWindow, "WWDS Harness " + WWDS.ModCore.ModContext.HarnessVersion + "  (" + _panelKey.Value + ")");
        GUI.matrix = matrix;
    }

    /// <summary>Draws "A  52% (13)  label" as a bar with text.</summary>
    private void DrawBarLine(string line, float width)
    {
        var rect = GUILayoutUtility.GetRect(width, 20);
        var pct = ParsePercent(line);
        if (pct >= 0)
        {
            GUI.color = new Color(0.25f, 0.55f, 1f, 0.8f);
            GUI.DrawTexture(new Rect(rect.x, rect.y + 2, rect.width * pct / 100f, rect.height - 4), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        GUI.Label(rect, line, _small);
    }

    private static float ParsePercent(string line)
    {
        var parts = line.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return -1;
        var p = parts[1].IndexOf('%');
        return p > 0 && float.TryParse(parts[1].Substring(0, p), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    // ---- panel ---------------------------------------------------------------------------------

    private void DrawWindow(int id)
    {
        _tab = GUILayout.Toolbar(_tab, Tabs);
        _scroll = GUILayout.BeginScrollView(_scroll);
        try
        {
            switch (_tab)
            {
                case 0: DrawStatus(); break;
                case 1: DrawAudience(); break;
                case 2: DrawFeatures(); break;
                case 3: DrawEvents(); break;
                default: DrawDebug(); break;
            }
        }
        catch (Exception ex)
        {
            GUILayout.Label("UI error: " + ex.Message);
        }

        GUILayout.EndScrollView();
        if (_lastAction.Length > 0) GUILayout.Label(_lastAction, _small);
        GUI.DragWindow();
    }

    private void Row(string label, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130));
        GUILayout.Label(value, _small);
        GUILayout.EndHorizontal();
    }

    private void DrawStatus()
    {
        var d = Context.Detection;
        Row("Game", d.Profile?.DisplayName ?? "unsupported");
        Row("Build", d.Compatibility + (d.Build != null ? " (" + d.Build.Label + ")" : string.Empty));
        Row("Loader", Context.LoaderName + ", Unity " + Application.unityVersion);
        Row("Game adapter", Context.Adapter == null ? "not attached" : "connected (" + Context.Adapter.AdapterVersion + ")");
        var caps = Context.Capabilities;
        var all = CapabilitySet.Split((GameCapability)long.MaxValue).ToList();
        Row("Capabilities", all.Count(caps.Has) + "/" + all.Count + " (missing: " + string.Join(", ", all.Where(c => !caps.Has(c)).Select(c => c.ToString()).ToArray()) + ")");
        Row("Twitch", _link == null ? "off" : _link.Transport + ": " + _link.State + (_link.DryRun ? " (TEST MODE)" : string.Empty));
        if (_link != null && _link.Channel.Length > 0) Row("Channel", _link.Channel);
        GUILayout.Space(6);

        var s = Context.Adapter?.GetSnapshot() ?? GameSnapshot.Empty;
        Row("State", s.Phase + " (" + (s.RawState ?? "-") + ")");
        if (s.Question != null)
        {
            Row("Question", s.Question.Kind + ", " + s.Question.Category + ": " + s.Question.Text);
            foreach (var a in s.Question.Answers) Row("  " + a.Key, a.Text + (_showCorrect.Value && a.IsCorrect ? "   [correct]" : string.Empty));
        }

        foreach (var team in new[] { s.TeamA, s.TeamB })
        {
            if (team == null) continue;
            Row(team.DisplayName, team.Score + "   " + string.Join(", ", team.Members.Select(m => m.Name + (m.Kind == ContestantKind.Bot ? " (bot" + (m.ExternallyControlled ? ", audience" : string.Empty) + ")" : string.Empty)).ToArray()));
        }

        if (s.ActiveContestant != null) Row("Current contestant", s.ActiveContestant.Name + " (team " + s.ActiveContestant.Team + ")");
        if (_interaction.Title.Length > 0)
        {
            GUILayout.Space(6);
            Row("Audience", _interaction.Title + " - " + _interaction.State);
            foreach (var l in _interaction.Lines) DrawBarLine(l, 500);
        }
    }

    private void DrawAudience()
    {
        var audience = Context.Get<AudienceService>();
        if (audience == null)
        {
            GUILayout.Label("Audience feature not running.");
            return;
        }

        var current = audience.Engine.Active.LastOrDefault(i => i.IsActive);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(audience.Paused ? "Resume audience" : "Pause audience")) audience.Paused = !audience.Paused;
        if (GUILayout.Button("Vote on question")) RunAction("vote.open");
        if (GUILayout.Button("Name opponent")) RunAction("naming.open", "target", "opponent");
        if (GUILayout.Button("Guess answer")) RunAction("guess.open", "kind", "answer");
        GUILayout.EndHorizontal();

        if (current == null)
        {
            GUILayout.Label("No interaction running.");
        }
        else
        {
            var left = current.State == InteractionState.Collecting ? Math.Max(0, current.LockAt - Context.Clock.Now) : 0;
            GUILayout.Label(current.Title + "  [" + current.Kind + ", " + current.State + ", " + current.Participants + " participants" + (left > 0 ? ", " + Math.Ceiling(left) + " s" : string.Empty) + "]");
            foreach (var l in current.TallyLines()) DrawBarLine(l, 500);
            GUILayout.BeginHorizontal();
            var by = new Viewer("local", "local", "Streamer", WWDS.ModCore.Commands.Authority.LocalOperator, "local");
            if (GUILayout.Button("Lock")) audience.Engine.Lock(current, "panel");
            if (GUILayout.Button("Cancel")) audience.Engine.Cancel(current, "panel");
            if (GUILayout.Button("+15 s")) audience.Engine.Extend(current, 15);
            if (GUILayout.Button("Reopen")) audience.Engine.Reopen(current, current.Options.Duration);
            if (GUILayout.Button("Clear")) audience.Engine.Clear(current);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Override:", GUILayout.Width(70));
            switch (current)
            {
                case VoteInteraction vote:
                    foreach (var c in vote.Choices)
                    {
                        if (GUILayout.Button(c.Key)) audience.Engine.Force(current, c.Key, by);
                    }

                    break;
                case NamingInteraction naming:
                    foreach (var e in naming.Entries.Where(e => !e.Banned).Take(6))
                    {
                        if (GUILayout.Button("#" + e.Number + " " + e.Text)) audience.Engine.Force(current, e.Number.ToString(), by);
                    }

                    break;
                case GuessInteraction guess when guess.GuessKind == GuessKind.Option:
                    foreach (var k in guess.OptionKeys)
                    {
                        if (GUILayout.Button(k)) audience.Engine.Force(current, k, by);
                    }

                    break;
            }

            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8);
        var opponent = Context.Get<OpponentFeature>();
        if (opponent != null)
        {
            GUILayout.Label("Twitch opponent: " + (opponent.Team.HasValue ? "controls team " + opponent.Team : "inactive") + ", " + opponent.Opponent.Mode + "/" + opponent.Opponent.Behavior + ", " + opponent.Opponent.Statistics);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Opponent ON")) _lastAction = opponent.SetActive(true);
            if (GUILayout.Button("Opponent OFF")) _lastAction = opponent.SetActive(false);
            foreach (OpponentMode mode in Enum.GetValues(typeof(OpponentMode)))
            {
                if (GUILayout.Button(mode.ToString())) RunAction("opponent.mode", "mode", mode.ToString());
            }

            GUILayout.EndHorizontal();
        }

        var guessing = Context.Get<GuessingFeature>();
        if (guessing != null) GUILayout.Label("Guess leaderboard: " + string.Join(", ", guessing.Leaderboard.Top(5).Select(kv => kv.Key + " " + kv.Value).ToArray()), _small);
        var stats = Context.Get<StatisticsFeature>();
        if (stats != null)
        {
            var d = stats.Data;
            var total = d.AudienceMajorityCorrect + d.AudienceMajorityWrong;
            GUILayout.Label("Audience majority correct: " + d.AudienceMajorityCorrect + "/" + total + "   Most active: " + string.Join(", ", stats.MostActive(5).Select(kv => kv.Key + " (" + kv.Value + ")").ToArray()), _small);
        }
    }

    private void DrawFeatures()
    {
        foreach (var info in Context.Features.Features)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(info.Feature.DisplayName, GUILayout.Width(170));
            GUILayout.Label(info.State + (info.Detail != null ? " (" + info.Detail + ")" : string.Empty), _small, GUILayout.Width(250));
            if (info.Feature.Id != Id)
            {
                if (info.State == FeatureState.Running)
                {
                    if (GUILayout.Button("Stop")) Context.Features.Stop(info, "stopped in panel");
                }
                else if (GUILayout.Button("Start"))
                {
                    info.Enabled.Value = true;
                    Context.Features.TryStart(info, Context, Context.Capabilities);
                }
            }

            GUILayout.EndHorizontal();
        }
    }

    private void DrawEvents()
    {
        foreach (var e in Context.Journal.Snapshot().Reverse().Take(60))
        {
            GUILayout.Label(e.At.ToLocalTime().ToString("HH:mm:ss") + "  " + e.Text, _small);
        }
    }

    private void DrawDebug()
    {
        var adapter = Context.Adapter;
        GUILayout.BeginHorizontal();
        if (adapter != null && GUILayout.Button("Start bot show")) _lastAction = adapter.StartShow(new ShowSetup()).ToString();
        if (adapter != null && GUILayout.Button("Control A: " + adapter.IsExternallyControlled(TeamSlot.A))) _lastAction = adapter.SetExternalControl(TeamSlot.A, !adapter.IsExternallyControlled(TeamSlot.A)).ToString();
        if (adapter != null && GUILayout.Button("Control B: " + adapter.IsExternallyControlled(TeamSlot.B))) _lastAction = adapter.SetExternalControl(TeamSlot.B, !adapter.IsExternallyControlled(TeamSlot.B)).ToString();
        GUILayout.EndHorizontal();

        GUILayout.Label("Mock audience (no Twitch needed):");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("20 random votes")) _simulator!.Crowd(20, "vote", new[] { "A", "B", "C" });
        if (GUILayout.Button("Votes A12 B5 C3")) _simulator!.Votes("vote", new KeyValuePair<string, int>("A", 12), new KeyValuePair<string, int>("B", 5), new KeyValuePair<string, int>("C", 3));
        if (GUILayout.Button("10 names")) _simulator!.Crowd(10, "name", new[] { "Quiz Master", "Elton Fan", "Chat Crowd", "Team Pixel", "Bernds Bande" });
        if (GUILayout.Button("10 guesses")) _simulator!.Crowd(10, "guess", new[] { "A", "B", "C", "5000", "12000" });
        GUILayout.EndHorizontal();

        GUILayout.Label("Pending decisions are resolved by the audience features; use the Audience tab overrides to decide manually.", _small);
        GUILayout.Space(6);
        if (adapter is IPatchStatusProvider patches)
        {
            GUILayout.Label("Patches:");
            foreach (var p in patches.GetPatchStatus())
            {
                GUILayout.Label((p.Status == "Applied" ? "[ok] " : "[!!] ") + p.Id + " -> " + p.Target + (p.Error != null ? "  " + p.Error : string.Empty), _small);
            }
        }
    }

    private void RunAction(string name, params string[] kv)
    {
        var actions = Context.Get<WWDS.Audience.Bindings.BindingActions>();
        if (actions == null)
        {
            _lastAction = "audience not running";
            return;
        }

        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < kv.Length; i += 2) args[kv[i]] = kv[i + 1];
        _lastAction = name + ": " + actions.Run(name, args, new object());
    }
}
