#if HAS_INTEROP
using System;
using Il2CppRewired;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Input;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

// A following friend's way to their own Settings mid-run, through the own-menu flow (OnlineSession.Remap.cs), in the two places a couch
// player reaches theirs: the host's pause menu as their PC draws it, where a copy of its own bottom shortcut reads "[RT] My Settings"
// (Campaign/Menus/MySettingsView.cs), and the world map, where the map bar's own "[Start] Menu" entry opens it
// (Campaign/Menus/MySettingsMapEntry.cs). The press is read where this PC already reads the friend's device: their pad in Rewired's input
// update (the element their own maps bind the place's action to: RT, or Start), their keyboard's key (Left Alt, acting once let go alone so
// Alt+Tab never opens it; or Esc), or a click on the place. The same press is kept from the host, from the moment it goes down on the place
// until it's let go: the host's game never sees it (its pause menu ignores RT and its map ignores a friend's Start, but a step ahead it may
// be on a screen where either acts). Apart from what the session does (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    /// <summary>Modifier presses that went with another key named in the log per session; the rest are counted.</summary>
    private const int NamedChordDrops = 5;

    private readonly MySettingsSpot _pauseSpot = new(MySettingsPlace.PauseMenu, "UI_UIRightTrigger", uiOnly: true, MySettingsBinding.Defaults, "the paused game", MySettingsView.Over);
    private readonly MySettingsSpot _mapSpot = new(MySettingsPlace.MapBar, "Default_Pause", uiOnly: false, MySettingsBinding.StartDefaults, "the map", MySettingsMapEntry.Over);
    private bool _mySettingsOffSaid;

    /// <summary>
    /// Friend, every pad reading in Rewired's input update, before it goes to the host: a press of My Settings noted (opened at the next
    /// frame's update), and the element left out of what's sent while it's kept.
    /// </summary>
    private void MySettingsPad(ref PadButtons held, ref float leftX, ref float leftY, ref float rightX, ref float rightY, ref float leftTrigger, ref float rightTrigger)
    {
        MySettingsPad(_pauseSpot, ref held, ref leftX, ref leftY, ref rightX, ref rightY, ref leftTrigger, ref rightTrigger);
        MySettingsPad(_mapSpot, ref held, ref leftX, ref leftY, ref rightX, ref rightY, ref leftTrigger, ref rightTrigger);
    }

    private static void MySettingsPad(MySettingsSpot spot, ref PadButtons held, ref float leftX, ref float leftY, ref float rightX, ref float rightY, ref float leftTrigger, ref float rightTrigger)
    {
        bool down = spot.Binding.PadDown(held, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger);
        if (spot.Press.Pad(down, spot.Live))
        {
            spot.Asked ??= spot.Binding.PadName;
        }

        if (spot.PadKeep.Step(down, spot.Live))
        {
            spot.Binding.LeaveOut(ref held, ref leftX, ref leftY, ref rightX, ref rightY, ref leftTrigger, ref rightTrigger);
        }
    }

    /// <summary>
    /// A keyboard friend, every key-table reading before it goes to the host: the key that presses My Settings, and a mouse button that went
    /// down on its place, left out while they're kept. The press itself is read in the frame's update (<see cref="UpdateMySettings"/>).
    /// </summary>
    private void MySettingsKeys(ref ulong held, ref ulong pressed)
    {
        MySettingsKeys(_pauseSpot, ref held, ref pressed);
        MySettingsKeys(_mapSpot, ref held, ref pressed);
    }

    private static void MySettingsKeys(MySettingsSpot spot, ref ulong held, ref ulong pressed)
    {
        if (spot.KeyBits != 0 && spot.KeyKeep.Step((held & spot.KeyBits) != 0, spot.Live))
        {
            held &= ~spot.KeyBits;
            pressed &= ~spot.KeyBits;
        }

        if (spot.MouseBits == 0)
        {
            return;
        }

        // Where the pointer is matters only as a button goes down with the place live: one question then.
        bool began = (pressed & spot.MouseBits) != 0;
        bool onPlace = began && spot.Live && ReInput.controllers.Mouse is { } mouse && spot.Over(mouse.screenPosition);
        if (spot.MouseKeep.Step((held & spot.MouseBits) != 0, onPlace))
        {
            held &= ~spot.MouseBits;
            pressed &= ~spot.MouseBits;
        }
    }

    /// <summary>
    /// Friend, every frame: whether My Settings is live on the host's pause menu (the copy shown or hidden with it) or on the map's Menu
    /// prompt, the keyboard's and the mouse's presses, and a press noted since: the friend's own Settings opened. Never throws: a failure
    /// turns only it off.
    /// </summary>
    private void UpdateMySettings()
    {
        if (_friend == null)
        {
            return;
        }

        try
        {
            UpdateMySettingsCore();
        }
        catch (Exception ex)
        {
            _pauseSpot.Live = false;
            _mapSpot.Live = false;
            if (!_mySettingsOffSaid)
            {
                _mySettingsOffSaid = true;
                ModLog.Error("Own menu: My Settings failed; it's off for this session (your own Settings still open from the title once you leave)", ex);
            }

            MySettingsView.Update(null, false); // hidden (never throws)
        }
    }

    private void UpdateMySettingsCore()
    {
        if (_mySettingsOffSaid)
        {
            return;
        }

        bool following = _friend is { State: FriendSessionState.Joined } && DeviceWithHost && (_localPad != null || _localKeyboard != null) && !_broken;

        // The host's pause menu (in a match). What PauseShown gives is alive or nothing, so the checks below compare references: Unity's own
        // equality is a call into the game with a boxed answer, and this runs every frame of a friend's session.
        GameMenu? menu = GameMenuViews.PauseShown();
        MySettingsHidden pauseHidden = menu is null ? MySettingsHidden.NoPauseMenu : Availability(following, MySettingsView.OffReason != null);
        bool wasPauseLive = _pauseSpot.Live;
        bool pauseLive = MySettingsView.Update(menu, pauseHidden == MySettingsHidden.None);
        if (MySettingsView.OffReason != null)
        {
            // The copy on the pause menu failed (the view said why): only it is off. The map's Menu prompt needs no copy and goes on opening
            // the friend's Settings, their one way to Leave Online Game during a run.
            pauseLive = false;
        }

        if (menu is not null)
        {
            BindMySettings(_pauseSpot); // the friend's controls may have changed since (a remap, another pad): one comparison while nothing has
        }

        NotePauseShown(menu, pauseHidden, pauseLive);
        _pauseSpot.Live = pauseLive;

        // The world map's Menu prompt (on the map, with none of the host's screens up here). Only on a PC that follows a host for real: in the
        // one-PC test the map is the host side's own, and its Start is the host's.
        UIInputShortcutBase? entry = following && !_solo && Campaign.Scenes.FriendUi.Locked ? MySettingsMapEntry.Shown() : null;
        bool bare = entry is not null && MenuViews.Instance is not { Friend.AnyHostMenu: true };
        MySettingsHidden mapHidden = !bare ? MySettingsHidden.NoPauseMenu : Availability(following, off: false);
        if (entry is not null)
        {
            BindMySettings(_mapSpot);
        }

        bool wasMapLive = _mapSpot.Live;
        bool mapLive = mapHidden == MySettingsHidden.None;
        NoteMapShown(entry, mapHidden, mapLive);
        _mapSpot.Live = mapLive;

        // Keys and clicks, then a press noted since (the pad's in the input update).
        ReadMySettingsKeyboard(_pauseSpot, pauseLive, wasPauseLive);
        ReadMySettingsKeyboard(_mapSpot, mapLive, wasMapLive);
        if ((pauseLive || mapLive) && ReInput.isReady && ReInput.controllers.Mouse is { enabled: true } mouse && mouse.GetButtonDown(0))
        {
            Vector2 point = mouse.screenPosition;
            MySettingsSpot spot = pauseLive ? _pauseSpot : _mapSpot;
            if (spot.Over(point))
            {
                spot.Asked ??= "click";
            }
        }

        TakeAsked(_pauseSpot);
        TakeAsked(_mapSpot);
    }

    /// <summary>Why My Settings can't open now, with its place on screen (<see cref="MySettingsShortcut.Availability"/>).</summary>
    private MySettingsHidden Availability(bool following, bool off) => MySettingsShortcut.Availability(new MySettingsFacts(
        PauseMenuShown: true,
        Following: following,
        OwnMenuOpen: _ownMenu.HoldsDevice,
        OwnMenusOff: _ownMenuOff,
        UnlocksOpen: _unlocksLent || OwnUnlocks.Holding,
        QuestionUp: _questionLent,
        PromptOpen: LobbyPrompts.IsOpen,
        PadUnplugged: _localPad is { IsConnected: false },
        Off: off));

    private void TakeAsked(MySettingsSpot spot)
    {
        if (spot.Asked is not { } source)
        {
            return;
        }

        spot.Asked = null;
        if (spot.Live)
        {
            OpenFrom(spot, source);
        }
    }

    /// <summary>What presses My Settings at <paramref name="spot"/>, read again when the friend's controls changed (a remap, another pad), as the glyph on screen is drawn from them.</summary>
    private void BindMySettings(MySettingsSpot spot)
    {
        InputBindingSet? controls = _localKeyboard?.Controls ?? _controls;
        if (spot.Bound && ReferenceEquals(controls, spot.BoundTo))
        {
            return;
        }

        if (spot.ActionId < 0 && ReInput.isReady)
        {
            spot.ActionId = ReInput.mapping.GetAction(spot.Action)?.id ?? -1;
        }

        if (_uiCategory < 0 && ReInput.isReady)
        {
            _uiCategory = ReInput.mapping.GetMapCategoryId("UI");
        }

        spot.Bound = true;
        spot.BoundTo = controls;
        spot.Binding = spot.UiOnly && _uiCategory < 0 ? spot.Defaults : MySettingsBinding.From(controls, spot.UiOnly ? _uiCategory : -1, spot.ActionId, spot.Defaults);
        spot.Modifier = MySettingsBinding.IsModifierKey(spot.Binding.Keys[0]);
        spot.KeyBits = MySettingsBits.Mask(controls, spot.Binding.Keys);
        spot.MouseBits = MySettingsBits.MouseMask(controls);
    }

    /// <summary>
    /// The key that presses My Settings at <paramref name="spot"/>, read from Rewired's keyboard (a keyboard friend's is off the game's
    /// players, a pad friend's UI maps are off: neither reaches the game). Read only while it's live, or while a modifier's press waits to
    /// see what it was for; as it goes live, a key already down is noted as down, so its release presses nothing.
    /// </summary>
    private static void ReadMySettingsKeyboard(MySettingsSpot spot, bool live, bool wasLive)
    {
        if ((!live && !spot.Press.KeyWaits) || !ReInput.isReady)
        {
            return;
        }

        Keyboard keyboard = ReInput.controllers.Keyboard;
        bool down = false;
        foreach (BindingElement key in spot.Binding.Keys)
        {
            down |= KeyHeld(keyboard, key);
        }

        bool waits = spot.Press.KeyWaits;
        MySettingsKeyStep step = spot.Press.Key(down, live && wasLive, spot.Modifier, waits && keyboard.GetAnyButtonDown(), !waits || Application.isFocused);
        if (step == MySettingsKeyStep.Fire)
        {
            spot.Asked ??= spot.Binding.KeyName;
        }
        else if (step == MySettingsKeyStep.Dropped && ++spot.Drops <= NamedChordDrops)
        {
            ModLog.Info($"Own menu: {spot.Binding.KeyName} went with another key, the window lost focus or {(spot.Place == MySettingsPlace.MapBar ? "the map's prompt" : "the pause menu")} went " +
                $"(Alt+Tab or the like); not opened{(spot.Drops == NamedChordDrops ? " (further ones are only counted)" : string.Empty)}.");
        }
    }

    /// <summary>A key with the modifiers its binding needs.</summary>
    private static bool KeyHeld(Keyboard keyboard, BindingElement key)
    {
        if (!keyboard.GetKey((KeyCode)key.Code))
        {
            return false;
        }

        KeyModifiers needed = key.Modifiers;
        return needed == KeyModifiers.None ||
            (((needed & KeyModifiers.Control) == 0 || keyboard.GetModifierKey(ModifierKey.Control)) &&
             ((needed & KeyModifiers.Alt) == 0 || keyboard.GetModifierKey(ModifierKey.Alt)) &&
             ((needed & KeyModifiers.Shift) == 0 || keyboard.GetModifierKey(ModifierKey.Shift)) &&
             ((needed & KeyModifiers.Command) == 0 || keyboard.GetModifierKey(ModifierKey.Command)));
    }

    /// <summary>The log's lines as My Settings comes and goes on a pause menu: once per menu shown, and when it goes while the menu stays.</summary>
    private void NotePauseShown(GameMenu? menu, MySettingsHidden hidden, bool live)
    {
        MySettingsSpot spot = _pauseSpot;
        if (menu is null)
        {
            spot.Said = IntPtr.Zero; // gone, or covered by a host overlay: said again when it's back
        }
        else if (live && menu.Pointer != spot.Said)
        {
            spot.Said = menu.Pointer;
            ModLog.Info($"Own menu: My Settings is on the host's pause menu here ({spot.Binding.PadName}, {spot.Binding.KeyName} or a click opens your own Settings).");
        }
        else if (hidden != MySettingsHidden.None && spot.Hidden == MySettingsHidden.None)
        {
            ModLog.Info($"Own menu: My Settings is off the host's pause menu here ({MySettingsShortcut.Describe(hidden)}).");
        }

        spot.Hidden = hidden;
    }

    /// <summary>
    /// The log's lines for the map's Menu prompt: once per map shown (not each time one of the host's screens comes and goes over it), and
    /// when it stops opening the friend's Settings for another reason while it's on screen.
    /// </summary>
    private void NoteMapShown(UIInputShortcutBase? entry, MySettingsHidden hidden, bool live)
    {
        MySettingsSpot spot = _mapSpot;
        if (live && entry is not null && entry.Pointer != spot.Said)
        {
            spot.Said = entry.Pointer;
            ModLog.Info($"Own menu: the map's Menu prompt opens your own Settings here ({spot.Binding.PadName}, {spot.Binding.KeyName} or a click).");
        }
        else if (hidden is not (MySettingsHidden.None or MySettingsHidden.NoPauseMenu) && spot.Hidden == MySettingsHidden.None)
        {
            ModLog.Info($"Own menu: the map's Menu prompt doesn't open your own Settings now ({MySettingsShortcut.Describe(hidden, MySettingsPlace.MapBar)}).");
        }

        spot.Hidden = hidden;
    }

    /// <summary>A press of My Settings: its place's own press, then the friend's own Settings through the own-menu flow (the device lent, the host told).</summary>
    private void OpenFrom(MySettingsSpot spot, string source)
    {
        ModLog.Info($"Own menu: asked from {spot.Where} ({source}).");
        if (spot.Place == MySettingsPlace.MapBar)
        {
            MySettingsMapEntry.PlayPress();
        }
        else
        {
            MySettingsView.PlayPress();
        }

        string said = OpenOwnMenu(remapper: false, onlineTab: true); // on its Online tab: Leave Online Game is there, the game's own tabs a bumper away
        if (_ownMenu.HoldsDevice)
        {
            spot.Opened++;
            return;
        }

        ModLog.Info($"Own menu: not opened from {spot.Where} ({said.TrimEnd('.')}).");
        LobbySounds.Play(LobbySounds.Grunt);
    }

    /// <summary>The friend's side is ending: what My Settings did this session, and everything forgotten (the copy hidden).</summary>
    private void EndMySettings()
    {
        foreach (MySettingsSpot spot in new[] { _pauseSpot, _mapSpot })
        {
            int kept = spot.PadKeep.Count + spot.KeyKeep.Count + spot.MouseKeep.Count;
            if (spot.Opened > 0 || kept > 0 || spot.Drops > 0)
            {
                ModLog.Info($"Own menu: My Settings opened {spot.Opened} time(s) from {spot.Where}; {kept} press(es) kept from the host" +
                    (spot.Drops > 0 ? $"; {spot.Drops} key press(es) that went with another key not taken." : "."));
            }

            spot.Reset();
        }

        Step("forgetting My Settings", MySettingsView.Forget);
        Step("forgetting the map's Menu prompt", MySettingsMapEntry.Forget);
        _mySettingsOffSaid = false;
    }

    /// <summary>One place a following friend opens their own Settings from: what presses it there, its presses, what's kept from the host.</summary>
    private sealed class MySettingsSpot
    {
        // Mutable structs, as fields: stepped in place.
        public MySettingsKeep PadKeep;
        public MySettingsKeep KeyKeep;
        public MySettingsKeep MouseKeep;

        public MySettingsSpot(MySettingsPlace place, string action, bool uiOnly, MySettingsBinding defaults, string where, Func<Vector2, bool> over)
        {
            Place = place;
            Action = action;
            UiOnly = uiOnly;
            Defaults = defaults;
            Where = where;
            Over = over;
            Binding = defaults;
        }

        public MySettingsPlace Place { get; }

        /// <summary>The game's action the place's glyph shows.</summary>
        public string Action { get; }

        /// <summary>True when only the UI maps' bindings count (the pause menu's RT); else any map category (the map's Start).</summary>
        public bool UiOnly { get; }

        public MySettingsBinding Defaults { get; }

        /// <summary>Where it is, for the log ("the paused game", "the map").</summary>
        public string Where { get; }

        /// <summary>True when a screen point is on the place.</summary>
        public Func<Vector2, bool> Over { get; }

        public MySettingsPress Press { get; } = new();

        public MySettingsBinding Binding { get; set; }

        public InputBindingSet? BoundTo { get; set; }

        public bool Bound { get; set; }

        public bool Modifier { get; set; }

        public ulong KeyBits { get; set; }

        public ulong MouseBits { get; set; }

        public int ActionId { get; set; } = -1;

        public bool Live { get; set; }

        public string? Asked { get; set; }

        public MySettingsHidden Hidden { get; set; } = MySettingsHidden.NoPauseMenu;

        public IntPtr Said { get; set; }

        public int Opened { get; set; }

        public int Drops { get; set; }

        public void Reset()
        {
            Press.Reset();
            PadKeep.Clear();
            KeyKeep.Clear();
            MouseKeep.Clear();
            Binding = Defaults;
            BoundTo = null;
            Bound = false;
            KeyBits = 0;
            MouseBits = 0;
            Live = false;
            Asked = null;
            Hidden = MySettingsHidden.NoPauseMenu;
            Said = IntPtr.Zero;
            Opened = 0;
            Drops = 0;
        }
    }
}
#endif
