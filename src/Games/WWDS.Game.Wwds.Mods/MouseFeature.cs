using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DoozyUI;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;
using WWDS.Runtime.Patching;

namespace WWDS.Game.Wwds.Mods;

/// <summary>Whose controller the mouse acts as.</summary>
public enum MouseOwner
{
    /// <summary>The keyboard player (the mouse is part of the keyboard).</summary>
    KeyboardPlayer,

    /// <summary>Whoever's turn it is in a show (couch play with one mouse).</summary>
    ActivePlayer,
}

/// <summary>
/// Mouse support for every screen (hybrid router). The game hides and
/// locks the cursor and drives every screen from controller polling, so clicks become one-tick virtual
/// button presses on the owner's InControl device: the game's own loops then apply turn checks, two-press
/// confirms and cooldowns exactly as for a pad. Clicking any element that shows a button glyph presses
/// that button (answers, bets, dialogs, prompts). Hover moves the game's own focus where it has one
/// (doors, category grid, menu lists); the wheel and digit keys set the bonus estimate; right click is
/// "back" outside a show. DoozyUI's native pointer handling is switched off so it cannot bypass the
/// game's checks.
/// </summary>
public sealed class MouseFeature : FeatureBase
{
    public const string FeatureId = "Mouse";

    private static readonly Dictionary<string, InputControlType> Faces = new()
    {
        ["A"] = InputControlType.Action1,
        ["B"] = InputControlType.Action2,
        ["X"] = InputControlType.Action3,
        ["Y"] = InputControlType.Action4,
        ["LB"] = InputControlType.LeftBumper,
        ["RB"] = InputControlType.RightBumper,
        ["LT"] = InputControlType.LeftBumper,
        ["RT"] = InputControlType.RightBumper,
        ["Menu"] = InputControlType.Start,
        ["Start"] = InputControlType.Start,
        ["ESC"] = InputControlType.Start,
    };

    private static MouseFeature? _current;
    private readonly List<RaycastResult> _hits = new();
    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<MouseOwner> _owner = null!;
    private IConfigValue<float> _hideAfter = null!;
    private ModUi _ui = null!;
    private Vector3 _lastMouse;
    private float _lastMove;
    private UnityEngine.Object? _hover;
    private ViewBonusQuestionUI? _bonusView;
    private Team? _bonusTeam;

    public override string Id => FeatureId;

    public override string DisplayName => "Mouse support";

    public override string Description => "Visible cursor; clicks, hover and wheel work on every screen through the game's own input.";

    protected override string LogCategoryName => "Mods";

    private bool Active => _enabled.Value && _current == this;

    protected override void OnStart()
    {
        var c = Context.Config;
        _enabled = c.Bind("Mouse", "Enabled", true, "Mouse support (cursor, clicks, hover, wheel).");
        _owner = c.Bind("Mouse", "Owner", MouseOwner.KeyboardPlayer, "KeyboardPlayer: the mouse plays for the keyboard player | ActivePlayer: for whoever's turn it is.");
        _hideAfter = c.Bind("Mouse", "HideAfterSeconds", 4f, "Hide the cursor after this many seconds without movement (0 = always visible).");
        _ui = ModUi.For(Context);
        _current = this;
        Context.Provide(this);
        var none = ModCore.Capabilities.GameCapability.None;
        _ui.Patch(new PatchSpec("mods.mouse.cursor", "The game always hides and locks the cursor; keep it free while mouse support is on.",
            () => AccessTools.Method(typeof(SUtility), "SetMouseCursorVisible", new[] { typeof(bool) }), none)
        {
            Prefix = AccessTools.Method(typeof(MouseFeature), nameof(CursorPrefix)),
        });
        foreach (var type in new[] { typeof(RepresentationMenuDoor), typeof(NavigationSubElement) })
        {
            foreach (var name in new[] { "OnMouseEnter", "OnMouseUpAsButton", "OnMouseExit" })
            {
                var t = type;
                var n = name;
                _ui.Patch(new PatchSpec("mods.mouse.native." + t.Name + "." + n, "Ungated native mouse handler; the router acts instead.",
                    () => AccessTools.Method(t, n), none)
                {
                    Prefix = AccessTools.Method(typeof(MouseFeature), nameof(NativeMousePrefix)),
                });
            }
        }

        _ui.Patch(new PatchSpec("mods.mouse.bonus-turn", "Remember whose bonus estimate is being entered (wheel and digits edit it).",
            () => AccessTools.Method(typeof(ViewBonusQuestionUI), "OnWaitForAnswerUpdated", new[] { typeof(Team) }), none)
        {
            Prefix = AccessTools.Method(typeof(MouseFeature), nameof(BonusTurnPrefix)),
        });
        Track(_ui.OnState(state =>
        {
            if (state != "StateShowLoop" && state != "StateShowTutorial") _bonusView = null;
        }));
    }

    protected override void OnStop()
    {
        _current = null;
        _ui.Pad.Clear();
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }

    public override void Tick()
    {
        if (!_enabled.Value) return;
        UpdateCursor();
        if (_ui.AnyOpen || _ui.Blocker.Active) return;
        DisableNativePointer();
        if (Context.Get<VoteHudFeature>() is { CapturesPointer: true }) return;
        try
        {
            Route();
        }
        catch (Exception ex)
        {
            Log.Warn("Mouse router: " + ex.Message);
        }
    }

    private void UpdateCursor()
    {
        var mouse = Input.mousePosition;
        if (mouse != _lastMouse || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Math.Abs(Input.mouseScrollDelta.y) > 0)
        {
            _lastMouse = mouse;
            _lastMove = Time.unscaledTime;
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = _hideAfter.Value <= 0 || Time.unscaledTime - _lastMove < _hideAfter.Value || _ui.AnyOpen;
    }

    /// <summary>DoozyUI buttons react to pointer events without the game's checks; only the router may click.</summary>
    private static void DisableNativePointer()
    {
        var es = EventSystem.current;
        if (es != null && es.currentInputModule is InControlInputModule m && m.allowMouseInput) m.allowMouseInput = false;
    }

    // ---- routing ------------------------------------------------------------------------------------

    private void Route()
    {
        var moved = Time.unscaledTime == _lastMove && !Input.GetMouseButton(0);
        var left = Input.GetMouseButtonDown(0);
        var right = Input.GetMouseButtonDown(1);
        var wheel = Input.mouseScrollDelta.y;
        var state = _ui.State;
        if (state == "StateScreenMainMenu" && Doors(moved, left)) return;
        if (state == "StateScreenSelectPlayers" && Selection(left, right, wheel)) return;
        var hit = RaycastUi();
        if (Category(hit, moved, left)) return;
        if (Navigation(hit, moved, left)) return;
        if (Bonus(wheel)) return;
        if (left)
        {
            var face = hit == null ? null : GlyphFace(hit);
            if (face != null) PressOwner(face.Value);
            else if (state is "StateShowIntro" or "StateShowOutro") PressOwner(InputControlType.Action1);
        }

        if (right && !_ui.InShow) PressOwner(InputControlType.Action2);
    }

    private GameObject? RaycastUi()
    {
        var es = EventSystem.current;
        if (es == null) return null;
        _hits.Clear();
        es.RaycastAll(new PointerEventData(es) { position = Input.mousePosition }, _hits);
        var root = _ui.Root.Object.transform;
        foreach (var h in _hits)
        {
            if (h.gameObject != null && !h.gameObject.transform.IsChildOf(root)) return h.gameObject;
        }

        return null;
    }

    /// <summary>
    /// The button glyph shown on or next to the clicked element ("ControlsA", "ControlsRB", ...): the
    /// nearest ancestor that contains exactly one glyph decides. Several glyphs = ambiguous, no press.
    /// </summary>
    internal static InputControlType? GlyphFace(GameObject hit)
    {
        var t = hit.transform;
        for (var level = 0; level < 5 && t != null; level++, t = t.parent)
        {
            var faces = new HashSet<InputControlType>();
            foreach (var child in t.GetComponentsInChildren<Transform>(false))
            {
                var name = child.name;
                if (!name.StartsWith("Controls", StringComparison.Ordinal)) continue;
                if (Faces.TryGetValue(name.Substring("Controls".Length), out var control)) faces.Add(control);
            }

            if (faces.Count == 1) return faces.First();
            if (faces.Count > 1) return null;
        }

        return null;
    }

    // ---- main menu doors (3D) -----------------------------------------------------------------------

    private bool Doors(bool moved, bool left)
    {
        var menu = MainMenuController.Instance;
        if (menu == null || menu.IsInputLocked()) return false;
        var t = typeof(MainMenuController);
        if ((bool)AccessTools.Field(t, "isInside").GetValue(menu) || (bool)AccessTools.Field(t, "loadSavegame").GetValue(menu)) return false;
        var cam = Camera.main;
        if (cam == null || !Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out var hit, 1000f)) return false;
        var door = hit.collider.GetComponentInParent<RepresentationMenuDoor>();
        if (door == null) return false;
        var doors = (RepresentationMenuDoor[])AccessTools.Field(t, "MenuDoors").GetValue(menu);
        var index = Array.IndexOf(doors, door);
        if (index < 0) return false;
        if (moved && _hover != door)
        {
            _hover = door;
            AccessTools.Method(t, "SelectState", new[] { typeof(int) }).Invoke(menu, new object[] { index });
        }

        if (!left) return true;
        AccessTools.Method(t, "SelectState", new[] { typeof(int) }).Invoke(menu, new object[] { index });
        // Same as a confirmed door press: inside first, so a key press during the animation cannot submit twice.
        AccessTools.Field(t, "isInside").SetValue(menu, true);
        door.Submit();
        return true;
    }

    // ---- player selection ---------------------------------------------------------------------------

    private bool Selection(bool left, bool right, float wheel)
    {
        var owner = Owner();
        var view = UnityEngine.Object.FindObjectOfType<ViewSelectPlayers>();
        if (owner == null || view == null) return false;
        var registered = (Dictionary<InputDevice, PlayerAgentLocal>)AccessTools.Field(typeof(ViewSelectPlayers), "registeredDevices").GetValue(view);
        var hit = RaycastUi();
        if (!registered.TryGetValue(owner, out var agent))
        {
            if (left) Press(owner, Input.mousePosition.x < Screen.width / 2f ? InputControlType.LeftBumper : InputControlType.RightBumper);
            else if (right) Press(owner, InputControlType.Action2);
            return true;
        }

        var slot = UnityEngine.Object.FindObjectsOfType<ViewPlayerSlot>().FirstOrDefault(s => s.Occupant == agent);
        if (left)
        {
            var face = hit == null ? null : GlyphFace(hit);
            Press(owner, face ?? InputControlType.Action1);
        }
        else if (right)
        {
            Press(owner, InputControlType.Action2);
        }
        else if (Math.Abs(wheel) > 0.01f && slot != null && slot.CurrentState == PlayerLoginStates.ProfileSelection)
        {
            AccessTools.Method(typeof(ViewPlayerSlot), "RequestProfileChange").Invoke(slot, new object[] { agent, wheel > 0 ? InputDirection.Left : InputDirection.Right });
        }

        return true;
    }

    // ---- category grid ------------------------------------------------------------------------------

    private bool Category(GameObject? hit, bool moved, bool left)
    {
        if (hit == null) return false;
        var button = hit.GetComponentInParent<HardwareButtonAddition>();
        if (button == null) return false;
        var view = hit.GetComponentInParent<ViewCategoryUI>() ?? UnityEngine.Object.FindObjectOfType<ViewCategoryUI>();
        if (view == null) return false;
        var t = typeof(ViewCategoryUI);
        var buttons = (HardwareButtonAddition[])AccessTools.Field(t, "categoryButtons").GetValue(view);
        if (Array.IndexOf(buttons, button) < 0) return false;
        var player = AccessTools.Field(t, "activePlayer").GetValue(view) as APlayerAgent;
        if (player is not PlayerAgentLocal || (bool)AccessTools.Field(t, "hasNewCategory").GetValue(view)) return true;
        var device = (player.Input as InputPlayer)?.Device;
        if (device == null || device != Owner()) return true;
        var nav = (HardwareButtonNavigationController)AccessTools.Field(t, "navigationController").GetValue(view);
        if ((moved || left) && _hover != button)
        {
            _hover = button;
            nav.Select(button);
        }

        // The game's own two-step confirm: the first press proposes, the second on the same category logs in.
        if (left) Press(device, InputControlType.Action1);
        return true;
    }

    // ---- menu lists (Tech room, archive, make-up, pause) --------------------------------------------

    private bool Navigation(GameObject? hit, bool moved, bool left)
    {
        if (hit == null) return false;
        var element = hit.GetComponentInParent<NavigationElement>();
        if (element == null) return false;
        var controller = element.GetComponentInParent<NavigationController>();
        if (controller == null || !controller.isActiveAndEnabled) return false;
        if ((bool)AccessTools.Field(typeof(NavigationController), "InputLocked").GetValue(null)) return true;
        if ((moved || left) && _hover != element)
        {
            _hover = element;
            Focus(controller, element);
        }

        if (left)
        {
            var face = GlyphFace(hit);
            PressOwner(face ?? InputControlType.Action1);
        }

        return true;
    }

    private static void Focus(NavigationController controller, NavigationElement element)
    {
        var currentField = AccessTools.Field(typeof(NavigationController), "currentNavigationElement");
        var group = element.transform.parent == null ? null : element.transform.parent.GetComponentInParent<NavigationGroup>();
        NavigationElement target = group != null && group != element ? group : element;
        var current = currentField.GetValue(controller) as NavigationElement;
        if (current != target)
        {
            if (current != null) NavigationElement.DynamicRemoveHighlight(current);
            currentField.SetValue(controller, target);
            NavigationElement.DynamicHighlight(target);
        }

        if (group == null || group == element) return;
        var highlighted = AccessTools.Field(typeof(NavigationGroup), "highlightedSubElement");
        var inner = highlighted.GetValue(group) as NavigationElement;
        if (inner == element) return;
        if (inner != null) NavigationElement.DynamicRemoveHighlight(inner);
        highlighted.SetValue(group, element);
        NavigationElement.DynamicHighlight(element);
    }

    // ---- bonus estimate -----------------------------------------------------------------------------

    private bool Bonus(float wheel)
    {
        var view = _bonusView;
        var team = _bonusTeam;
        if (view == null || team == null || !view.isActiveAndEnabled || team.Count == 0) return false;
        if (UIManager.Instance.gamePaused) return false;
        var player = team[team.ActivePlayerIndex];
        if ((player.Input as InputPlayer)?.Device is not { } device || device != Owner()) return false;
        var isTeamA = SDataAccess.ActiveSession != null && SDataAccess.ActiveSession.TeamA == team;
        var t = typeof(ViewBonusQuestionUI);
        var bet = (int)AccessTools.Field(t, isTeamA ? "teamABet" : "teamBBet").GetValue(view);
        var next = bet;
        if (Math.Abs(wheel) > 0.01f)
        {
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            next += Math.Sign(wheel) * (shift ? 1 : EstimateStep(bet));
        }

        foreach (var c in Input.inputString)
        {
            if (c >= '0' && c <= '9' && next < 100000000) next = (next * 10) + (c - '0');
            else if (c == '\b') next /= 10;
        }

        if (next == bet) return false;
        next = Math.Max(0, next);
        var other = (int)AccessTools.Field(t, isTeamA ? "teamBBet" : "teamABet").GetValue(view);
        var cant = next == other;
        // Exactly the three steps of the game's UpdateBet.
        AccessTools.Method(t, "SetTeamBetText").Invoke(view, new object[] { next, isTeamA });
        AccessTools.Field(t, "cantLoginAnswer").SetValue(view, cant);
        ((ViewControlIndicatorButton)AccessTools.Field(t, "confirmButton").GetValue(view)).ChangeVisibilty(!cant);
        var changed = (bool)AccessTools.Method(t, "HasChangedBet").Invoke(view, new object[] { next, isTeamA });
        var voted = (bool)AccessTools.Field(t, "teamAVoted").GetValue(view) && (bool)AccessTools.Field(t, "teamBVoted").GetValue(view);
        ((ViewControlIndicatorButton)AccessTools.Field(t, "finishButton").GetValue(view)).ChangeVisibilty(!changed && voted && !cant);
        return true;
    }

    /// <summary>Wheel step for an estimate: about a tenth of its magnitude, at least 1 (25 -&gt; 1, 1500 -&gt; 100).</summary>
    internal static int EstimateStep(int value)
    {
        var step = 1;
        while (Math.Abs(value) >= step * 100) step *= 10;
        return step;
    }

    // ---- presses ------------------------------------------------------------------------------------

    private InputDevice? Owner()
    {
        var keyboard = VirtualPad.Keyboard;
        if (_owner.Value == MouseOwner.KeyboardPlayer || !_ui.InShow) return keyboard;
        var s = SDataAccess.ActiveSession;
        var team = s == null ? null : s.TeamAActive ? s.TeamA : s.TeamB;
        if (team == null || team.Count == 0) return keyboard;
        var agent = team[Mathf.Clamp(team.ActivePlayerIndex, 0, team.Count - 1)];
        return agent is PlayerAgentLocal && agent.Input is InputPlayer ip && ip.Device != null ? ip.Device : keyboard;
    }

    /// <summary>Self-test hook: a click's press on the owner device.</summary>
    internal void TestPress(InputControlType control) => PressOwner(control);

    private void PressOwner(InputControlType control)
    {
        var owner = Owner();
        if (owner != null) Press(owner, control);
    }

    /// <summary>Holds <paramref name="control"/> for two input ticks: one press, one release, like a tap.</summary>
    private void Press(InputDevice device, InputControlType control) => _ui.Pad.Press(device, control);

    private static bool CursorPrefix()
    {
        var self = _current;
        if (self == null || !self._enabled.Value) return true;
        Cursor.lockState = CursorLockMode.None;
        return false;
    }

    private static bool NativeMousePrefix() => _current == null || !_current._enabled.Value;

    private static void BonusTurnPrefix(ViewBonusQuestionUI __instance, Team team)
    {
        var self = _current;
        if (self == null) return;
        self._bonusView = __instance;
        self._bonusTeam = team;
    }
}
