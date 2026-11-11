#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppRewired;
using Il2CppRewired.Data;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using KeyCode = UnityEngine.KeyCode;
using Player = Il2CppRewired.Player;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// This PC's keyboard and mouse as a friend's controller (as <see cref="LocalPad"/> is for a pad): the friend's own
/// controls read from their maps (their game's loaded keyboard and mouse maps, saved remaps included, every category; the
/// game's defaults for a category none of their players has loaded), the key table those make, and, from the host's Start
/// to the game's end (a play period), the keyboard and mouse taken away from this game's own players so only the mod reads
/// them, until they're given back.
/// They're read at the controller level (Rewired's keyboard and mouse), which doesn't depend on any player or map: keys
/// as the friend's maps bind them, with their modifiers; mouse buttons only as their maps bind them (no pointer, no
/// movement: the game binds none, and the host decides menus, D45).
/// </summary>
internal sealed class LocalKeyboard
{
    /// <summary>The owners file's name in the mod's data folder (<see cref="KeyboardOwners"/>).</summary>
    private const string OwnersFileName = "keyboard-taken.txt";

    /// <summary>How the owners file is named in the log.</summary>
    private const string OwnersFileLabel = CampaignFiles.DataFolderLabel + @"\" + OwnersFileName;

    // The keyboard a session holds now (from Detach to its give-back), for the guard on Rewired's assignment save, and the
    // saves held back meanwhile.
    private static LocalKeyboard? _taken;
    private static int _savesHeldBack;
    private static bool _guardInstalled;

    private readonly List<(int PlayerId, bool Keyboard, bool Mouse)> _owners;
    private readonly List<MapSnapshot> _snapshots;
    private readonly KeyCode[] _keys;
    private readonly KeyModifiers[] _modifiers;
    private readonly int[] _mouseButtons; // Rewired mouse element id per element, or -1 for a key
    private readonly bool _anyChord;
    private readonly bool _anyMouse;
    private readonly ulong _altFree; // the keys whose binding needs neither Alt nor the Windows key: left out when pressed with them
    private readonly bool _playsWithAlt; // a binding outside the menus is on Alt: other keys pressed while it's held aren't Alt+Tab
    private readonly bool _playsWithWindows;
    private readonly KeyboardChordFilter _chords = new();
    private bool _wasEnabled = true;
    private bool _staleModifierSaid;
    private ulong _used; // the key table's elements held or pressed while online
    private int _offTimes;
    private double _offSince;
    private double _offSeconds;

    private LocalKeyboard(InputBindingSet controls, List<(int, bool, bool)> owners, List<MapSnapshot> snapshots, int sourcePlayer, int uiCategory)
    {
        Controls = controls;
        _owners = owners;
        _snapshots = snapshots;
        SourcePlayer = sourcePlayer;
        _chords.Reset(); // an Alt+Tab in the lobby: this PC's Alt counts once it's been seen up, as after any time the keyboard was off
        _playsWithAlt = KeyboardChordFilter.PlaysWith(controls.Bindings, uiCategory, (int)KeyCode.LeftAlt, (int)KeyCode.RightAlt, (int)KeyCode.AltGr);
        _playsWithWindows = KeyboardChordFilter.PlaysWith(controls.Bindings, uiCategory, (int)KeyCode.LeftWindows, (int)KeyCode.RightWindows, (int)KeyCode.LeftMeta, (int)KeyCode.RightMeta);
        int count = controls.Elements.Count;
        _keys = new KeyCode[count];
        _modifiers = new KeyModifiers[count];
        _mouseButtons = new int[count];
        for (int i = 0; i < count; i++)
        {
            BindingElement element = controls.Elements[i];
            bool mouse = element.Kind == BindingElementKind.MouseButton;
            _keys[i] = mouse ? KeyCode.None : (KeyCode)element.Code;
            _modifiers[i] = element.Modifiers;
            _mouseButtons[i] = mouse ? element.Code : -1;
            _anyChord |= element.Modifiers != KeyModifiers.None;
            _anyMouse |= mouse;
            bool altFree = !mouse && (element.Modifiers & (KeyModifiers.Alt | KeyModifiers.Command)) == 0 && !IsAltOrWindows(_keys[i]);
            _altFree |= altFree ? 1UL << i : 0;
        }
    }

    /// <summary>Our controls: the key table and every binding on it.</summary>
    public InputBindingSet Controls { get; }

    /// <summary>The Rewired player whose maps the controls were read from.</summary>
    public int SourcePlayer { get; }

    /// <summary>True between <see cref="Detach"/> and <see cref="GiveBack"/>.</summary>
    public bool IsDetached { get; private set; }

    /// <summary>The players that had the keyboard or mouse when found, for the log ("0").</summary>
    public string Owners => _owners.Count == 0 ? "none" : string.Join(", ", _owners.Select(o => o.PlayerId));

    /// <summary>
    /// Reads this PC's keyboard controls (generation <paramref name="generation"/>), with which players hold the keyboard
    /// and mouse and their maps as they are. Null, with why, when Rewired has no keyboard.
    /// </summary>
    public static LocalKeyboard? Find(byte generation, out string problem)
    {
        problem = string.Empty;
        if (!ReInput.isReady || ReInput.controllers.Keyboard == null)
        {
            problem = "Rewired has no keyboard";
            return null;
        }

        int keyboardId = ReInput.controllers.Keyboard.id;
        int mouseId = ReInput.controllers.Mouse?.id ?? 0;
        var owners = new List<(int, bool, bool)>();
        var snapshots = new List<MapSnapshot>();
        Player? keyboardPlayer = null;
        Player? mousePlayer = null;
        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            Player player = ReInput.players.GetPlayer(id);
            bool keyboard = player.controllers.hasKeyboard;
            bool mouse = player.controllers.hasMouse;
            if (!keyboard && !mouse)
            {
                continue;
            }

            owners.Add((id, keyboard, mouse));
            if (keyboard)
            {
                keyboardPlayer ??= player;
                snapshots.Add(MapSnapshot.Take(player, ControllerType.Keyboard, keyboardId));
            }

            if (mouse)
            {
                mousePlayer ??= player;
                snapshots.Add(MapSnapshot.Take(player, ControllerType.Mouse, mouseId));
            }
        }

        // The keyboard player's maps (player 0 on every PC: the game assigns the keyboard there at start-up); the game's
        // defaults (or the player's saved maps) for a category it hasn't loaded.
        int source = keyboardPlayer?.id ?? 0;
        int[] categories = RewiredBindings.CategoryIds();
        var own = new List<InputBinding>();
        int noKey = 0;
        int mouseAxes = 0;
        foreach (int category in categories)
        {
            ControllerMap? keys = keyboardPlayer?.controllers.maps.GetMap(ControllerType.Keyboard, keyboardId, category, 0) ??
                ReInput.mapping.GetKeyboardMapInstanceSavedOrDefault(source, category, 0);
            noKey += RewiredBindings.AddKeyboard(own, category, keys);
            ControllerMap? buttons = (mousePlayer ?? keyboardPlayer)?.controllers.maps.GetMap(ControllerType.Mouse, mouseId, category, 0) ??
                ReInput.mapping.GetMouseMapInstanceSavedOrDefault(mousePlayer?.id ?? source, category, 0);
            mouseAxes += RewiredBindings.AddMouse(own, category, buttons);
        }

        List<InputBinding> defaults = RewiredBindings.KeyboardDefaults(categories, out _);
        int differences = InputBindingSet.CountDifferences(own, defaults);
        var builder = new KeyboardBindingTableBuilder(KeyboardBindingTableBuilder.BrawlKeys);
        int leftOut = 0;
        foreach (InputBinding binding in own)
        {
            // "UI Mouse Click" does nothing on the host's virtual keyboard (RewiredBindings.SkipsAction): no key for it.
            if (RewiredBindings.SkipsAction(binding.ActionId))
            {
                leftOut++;
                continue;
            }

            builder.Add(binding);
        }

        InputBindingSet controls = builder.Build(generation, differences > 0 ? BindingSource.OwnMaps : BindingSource.GameDefaults, (byte)Math.Min(differences, byte.MaxValue));
        var found = new LocalKeyboard(controls, owners, snapshots, source, ReInput.mapping.GetMapCategoryId("UI"));
        ModLog.Info($"Your keyboard: {controls.Elements.Count(e => e.Kind == BindingElementKind.Key)} keys and {controls.Elements.Count(e => e.Kind == BindingElementKind.MouseButton)} mouse buttons " +
            $"go to the host ({(differences > 0 ? "your own maps" : "the game's defaults")}, Rewired player {source}: {controls.Bindings.Count} bindings, {RewiredBindings.PerCategory(controls.Bindings)}; " +
            $"{differences} differ from the game's defaults); the keyboard and mouse are on this game's players {found.Owners}." +
            (leftOut > 0 ? $" {leftOut} binding(s) of \"{RewiredBindings.MouseClickActionName}\" left out (the host's virtual keyboard has no mouse for it)." : string.Empty) +
            (builder.SkippedTableFull > 0 ? $" {builder.SkippedTableFull} binding(s) left out: more than {KeyboardMouseState.MaxElements} keys." : string.Empty) +
            (mouseAxes > 0 ? $" {mouseAxes} mouse movement or wheel binding(s) left out: online, the mouse carries buttons only." : string.Empty) +
            (noKey + builder.SkippedInvalid > 0 ? $" {noKey + builder.SkippedInvalid} binding(s) the online format can't carry left out." : string.Empty));
        if (differences > 0)
        {
            List<InputBinding> differ = InputBindingSet.NotIn(own, defaults, 10);
            ModLog.Info($"Your keyboard bindings the game's defaults don't have: {string.Join("; ", differ.Select(RewiredBindings.Describe))}.");
        }

        return found;
    }

    /// <summary>The owners file: which players had the keyboard and mouse, kept from <see cref="Detach"/> to <see cref="GiveBack"/>.</summary>
    private static string OwnersPath => Path.Combine(CampaignFiles.DataFolder, OwnersFileName);

    /// <summary>
    /// Takes the keyboard and mouse away from this game's players (Rewired's System player keeps them), so the game no longer
    /// reacts to them. Which players had them is noted in a file first (<see cref="KeyboardOwners"/>): should the game close
    /// or crash before <see cref="GiveBack"/>, the next start gives them back from it (<see cref="GiveBackAfterUnfinishedSession"/>).
    /// </summary>
    public void Detach()
    {
        NoteOwners();
        IsDetached = true; // first: should taking them fail part-way, the session's end still gives back what was taken
        _taken = this; // Rewired's assignment save is held back from now on (InstallAssignmentGuard)
        foreach ((int playerId, bool keyboard, bool mouse) in _owners)
        {
            Player.ControllerHelper controllers = ReInput.players.GetPlayer(playerId).controllers;
            if (keyboard)
            {
                controllers.hasKeyboard = false;
            }

            if (mouse)
            {
                controllers.hasMouse = false;
            }
        }

        ModLog.Info($"Your keyboard and mouse are taken off this game's players {Owners} until you leave: only the online session reads them.");
    }

    /// <summary>
    /// While detached, takes the keyboard or mouse back from any of this game's players that has been given it since (the
    /// game's connect handler gives a connecting device to the first empty player). Returns the player it was taken from,
    /// or -1. Call every input update.
    /// </summary>
    public int KeepDetached()
    {
        if (!IsDetached)
        {
            return -1;
        }

        int taken = -1;
        foreach ((int playerId, _, _) in _owners)
        {
            Player.ControllerHelper controllers = ReInput.players.GetPlayer(playerId).controllers;
            if (controllers.hasKeyboard)
            {
                controllers.hasKeyboard = false;
                taken = playerId;
            }

            if (controllers.hasMouse)
            {
                controllers.hasMouse = false;
                taken = playerId;
            }
        }

        return taken;
    }

    /// <summary>
    /// Reads the key table now: element <c>i</c> at bit <c>i</c>, held, and gone down since Rewired's last update. A key
    /// with modifiers counts with all of them held. Nothing while Rewired's keyboard is off (the window lost focus, or the
    /// mod's panel is open): the friend's player stands still then. A key pressed while Alt or the Windows key is held
    /// doesn't count until it's let go, unless its binding asks for that modifier (<see cref="KeyboardChordFilter"/>):
    /// Alt+Tab never sends Tab ("Change Line") to the host as the window loses focus.
    /// </summary>
    public (ulong Held, ulong Pressed) Read()
    {
        Keyboard keyboard = ReInput.controllers.Keyboard;
        bool enabled = keyboard.enabled;
        if (enabled != _wasEnabled)
        {
            _wasEnabled = enabled;
            double now = Streaming.FrameClock.Seconds;
            if (enabled)
            {
                double off = Math.Max(0, now - _offSince);
                _offSeconds += off;
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Your keyboard is on again after {off:0.0} s: your keys go to the host."));
            }
            else
            {
                _offTimes++;
                _offSince = now;
                string why = !UnityEngine.Application.isFocused ? "the game window lost focus" : Online.Panel.PanelInput.Held ? "the mod's panel is open" : "Rewired switched it off";
                ModLog.Info($"Your keyboard is off ({why}): your player stands still until it's back.");
            }
        }

        if (!enabled)
        {
            _chords.Reset(); // every key counts as let go
            return (0, 0);
        }

        // Down-presses only matter on an update where some key went down: one question first, not one per key. Alt and the
        // Windows key are asked then too (a press made with them held is left out), and after the keyboard was off until
        // they've been seen up (the filter's ModifiersUnproven).
        bool anyDown = keyboard.GetAnyButtonDown();
        bool control = false, alt = false, shift = false, command = false;
        if (_anyChord)
        {
            control = keyboard.GetModifierKey(ModifierKey.Control);
            shift = keyboard.GetModifierKey(ModifierKey.Shift);
        }

        if (_anyChord || anyDown || _chords.ModifiersUnproven)
        {
            alt = keyboard.GetModifierKey(ModifierKey.Alt);
            command = keyboard.GetModifierKey(ModifierKey.Command);
            if ((alt || command) && _chords.ModifiersUnproven && !_staleModifierSaid)
            {
                _staleModifierSaid = true;
                ModLog.Info("Your keyboard: Alt or the Windows key reads as held as the keyboard comes back (its release may have gone to another window); your presses count until it's been seen up.");
            }
        }

        Mouse? mouse = _anyMouse ? ReInput.controllers.Mouse : null;
        bool mouseOn = mouse is { enabled: true };
        ulong held = 0;
        ulong pressed = 0;
        for (int i = 0; i < _keys.Length; i++)
        {
            bool isHeld;
            bool isDown;
            int button = _mouseButtons[i];
            if (button >= 0)
            {
                isHeld = mouseOn && mouse!.GetButtonById(button);
                isDown = mouseOn && mouse!.GetButtonDownById(button);
            }
            else
            {
                KeyModifiers needed = _modifiers[i];
                bool chord = needed == KeyModifiers.None ||
                    (((needed & KeyModifiers.Control) == 0 || control) && ((needed & KeyModifiers.Alt) == 0 || alt) &&
                     ((needed & KeyModifiers.Shift) == 0 || shift) && ((needed & KeyModifiers.Command) == 0 || command));
                isHeld = chord && keyboard.GetKey(_keys[i]);
                isDown = chord && anyDown && keyboard.GetKeyDown(_keys[i]);
            }

            held |= isHeld ? 1UL << i : 0;
            pressed |= isDown ? 1UL << i : 0;
        }

        (ulong keptHeld, ulong keptPressed) = _chords.Apply(held, pressed, (alt && !_playsWithAlt) || (command && !_playsWithWindows), _altFree);
        _used |= keptHeld | keptPressed;
        return (keptHeld, keptPressed);
    }

    /// <summary>True for Alt and the Windows key themselves: a binding on one of them is never left out for it being held.</summary>
    private static bool IsAltOrWindows(KeyCode key) =>
        key is KeyCode.LeftAlt or KeyCode.RightAlt or KeyCode.AltGr or KeyCode.LeftWindows or KeyCode.RightWindows or KeyCode.LeftMeta or KeyCode.RightMeta;

    /// <summary>
    /// Gives the keyboard and mouse back to the players that had them, then checks their maps against the snapshot taken
    /// when they were found (and rebuilds any Rewired reset), and saves Rewired's controller assignments again: Rewired
    /// saves them, at once, whenever a joystick connects or any controller disconnects, which may have happened while the
    /// keyboard was taken, and the game would otherwise start next time with no keyboard on player 0. The assignments are
    /// saved whatever the maps' check did; the owners file goes once they are.
    /// </summary>
    public void GiveBack() => Give(lending: false);

    /// <summary>
    /// Gives the keyboard and mouse back for a moment only, as <see cref="GiveBack()"/> does (the friend's own remapper or
    /// settings while joined): <see cref="Find"/> reads them again after and its <see cref="Detach"/> takes them again.
    /// </summary>
    public void Lend() => Give(lending: true);

    private void Give(bool lending)
    {
        IsDetached = false;
        int heldBack = _savesHeldBack;
        _taken = null; // Rewired's assignment saves go through again (this one's below included)
        _savesHeldBack = 0;
        foreach ((int playerId, bool keyboard, bool mouse) in _owners)
        {
            Player.ControllerHelper controllers = ReInput.players.GetPlayer(playerId).controllers;
            if (keyboard)
            {
                controllers.hasKeyboard = true;
            }

            if (mouse)
            {
                controllers.hasMouse = true;
            }
        }

        int rebuilt = 0;
        int bindings = 0;
        int notChecked = 0;
        foreach (MapSnapshot snapshot in _snapshots)
        {
            try
            {
                rebuilt += snapshot.Restore(ReInput.players.GetPlayer(snapshot.PlayerId), snapshot.ControllerId);
                bindings += snapshot.Bindings;
            }
            catch (Exception ex)
            {
                notChecked++;
                ModLog.Error($"Your {snapshot.Type} maps on Rewired player {snapshot.PlayerId} couldn't be checked against the snapshot; they stay as Rewired gave them back", ex);
            }
        }

        bool saved = SaveAssignments();
        ForgetOwners();
        ModLog.Info($"Your keyboard and mouse are {(lending ? "lent back to" : "back on")} this game's players {Owners}{(lending ? " for your game's own menu" : string.Empty)}, " +
            (rebuilt == 0 ? $"their maps as before ({bindings} bindings)" : $"Rewired had reset {rebuilt} of their maps; restored from the snapshot ({bindings} bindings)") +
            (notChecked > 0 ? $", {notChecked} not checked (see the error above)" : string.Empty) +
            $"; Rewired's controller assignments {(saved ? "saved again as they are" : "not saved (no player-prefs data store)")}" +
            $"; it was off {_offTimes} time(s), {_offSeconds + (_wasEnabled ? 0 : Math.Max(0, Streaming.FrameClock.Seconds - _offSince)):0} s in all" +
            (heldBack > 0 ? $"; {heldBack} assignment save(s) of Rewired's held back while they were taken" : string.Empty) +
            (_chords.Filtered > 0 ? $"; {_chords.Filtered} press(es) made with Alt or the Windows key held left out (Alt+Tab and the like)" : string.Empty) + ".");
        LogKeysUsed();
    }

    /// <summary>The give-back's second line: which of the keys sent were used online, and how many never were.</summary>
    private void LogKeysUsed()
    {
        try
        {
            IReadOnlyList<BindingElement> elements = Controls.Elements;
            var used = new List<string>();
            int never = 0;
            for (int i = 0; i < elements.Count && i < 64; i++)
            {
                if ((_used & (1UL << i)) != 0)
                {
                    used.Add(RewiredBindings.ElementName(elements[i]));
                }
                else
                {
                    never++;
                }
            }

            ModLog.Info(used.Count == 0
                ? $"Your keyboard online: no key of the {elements.Count} sent was used."
                : $"Your keyboard online: used {string.Join(", ", used)}; {never} of the {elements.Count} keys sent never pressed.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Your keyboard online: the keys used couldn't be listed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// Patches Rewired's save of the controller assignments (<c>UserDataStore_PlayerPrefs.SaveControllerAssignments</c>, the
    /// only writer of the saved assignment: a joystick connecting, any controller going, start-up, a deferred joystick load,
    /// and every <c>SaveAll</c>) so it's skipped while this PC's keyboard is taken:
    /// the saved assignment then keeps the keyboard on player 0 for the whole session, even through a crash and the mod's
    /// uninstall before the next start. The give-back's own save runs once the keyboard is back, so it goes through.
    /// <c>keyboard-taken.txt</c> stays as the second net. A failure is logged and changes nothing else. Never throws.
    /// </summary>
    public static void InstallAssignmentGuard(HarmonyLib.Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        if (_guardInstalled)
        {
            return;
        }

        try
        {
            MethodInfo? target = AccessTools.Method(typeof(UserDataStore_PlayerPrefs), nameof(UserDataStore_PlayerPrefs.SaveControllerAssignments), Type.EmptyTypes);
            if (target == null)
            {
                ModLog.Warn("Your keyboard: Rewired's assignment save wasn't found, so it can't be held back while your keyboard is taken; the start-up repair (keyboard-taken.txt) still covers a crash.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(LocalKeyboard).GetMethod(nameof(SaveAssignmentsPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
            _guardInstalled = true;
            ModLog.Info("Your keyboard: Rewired's save of the controller assignments is held back while a session holds your keyboard, so a crash then can't leave this game without it.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Your keyboard: Rewired's assignment save couldn't be guarded ({ModLog.SafeErrorText(ex)}); the start-up repair (keyboard-taken.txt) still covers a crash.");
        }
    }

    /// <summary>Rewired's assignment save: skipped, reporting "not saved", while this PC's keyboard is taken (the first one logged).</summary>
    private static bool SaveAssignmentsPrefix(ref bool __result)
    {
        if (_taken is not { IsDetached: true })
        {
            return true;
        }

        if (_savesHeldBack++ == 0)
        {
            ModLog.Info("Your keyboard: Rewired wanted to save the controller assignments while your keyboard is taken (a controller connected or went); " +
                "not saved, so they keep your keyboard on player 0 if the game closes now. Saved when you leave.");
        }

        __result = false;
        return false;
    }

    /// <summary>
    /// At start-up, once Rewired is ready and before any session can take the keyboard: if one took this PC's keyboard and
    /// mouse and the game closed or crashed before giving them back (the owners file is still there), gives them back to the
    /// players that had them and saves Rewired's controller assignments. Rewired may have saved them as on nobody while they
    /// were taken (it saves, at once, whenever a joystick connects or any controller disconnects), and its data store then
    /// takes the keyboard off player 0 at every start, which the game never undoes: no keyboard in this game at all. Nothing
    /// to do without the file.
    /// </summary>
    public static void GiveBackAfterUnfinishedSession()
    {
        string path = OwnersPath;
        if (!File.Exists(path))
        {
            return;
        }

        string? text = null;
        try
        {
            text = new FileInfo(path).Length <= KeyboardOwners.MaxLength ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($"Your keyboard: {OwnersFileLabel} (a session that didn't end) couldn't be read ({CampaignFiles.ErrorText(ex)}); left for the next start.");
            return;
        }

        if (!KeyboardOwners.TryParse(text, out List<KeyboardOwner> owners))
        {
            ModLog.Warn($"Your keyboard: {OwnersFileLabel} (a session that didn't end) isn't one the mod wrote; deleted, nothing given back.");
            ForgetOwners();
            return;
        }

        var given = new List<string>();
        var givenMaps = new List<(int PlayerId, ControllerType Type, int ControllerId)>();
        int players = ReInput.players.playerCount;
        foreach (KeyboardOwner owner in owners)
        {
            if (owner.PlayerId >= players)
            {
                continue;
            }

            Player.ControllerHelper controllers = ReInput.players.GetPlayer(owner.PlayerId).controllers;
            if (owner.Keyboard && !controllers.hasKeyboard)
            {
                controllers.hasKeyboard = true;
                given.Add($"the keyboard to Rewired player {owner.PlayerId}");
                givenMaps.Add((owner.PlayerId, ControllerType.Keyboard, ReInput.controllers.Keyboard?.id ?? 0));
            }

            if (owner.Mouse && !controllers.hasMouse)
            {
                controllers.hasMouse = true;
                given.Add($"the mouse to Rewired player {owner.PlayerId}");
                givenMaps.Add((owner.PlayerId, ControllerType.Mouse, ReInput.controllers.Mouse?.id ?? 0));
            }
        }

        if (given.Count == 0)
        {
            ForgetOwners();
            if (owners.Count == 0)
            {
                return;
            }

            ModLog.Info($"Your keyboard: the last online session here ended without giving the keyboard and mouse back (the game closed or crashed), " +
                $"but they're on their players ({string.Join(", ", owners.Select(o => o.PlayerId))}) already: nothing to do.");
            return;
        }

        bool saved = SaveAssignments();
        ForgetOwners();
        string maps = LoadSavedMaps(givenMaps);
        ModLog.Warn($"Your keyboard: the last online session here ended without giving the keyboard and mouse back (the game closed or crashed), and Rewired " +
            $"had saved them as on nobody. Given back now: {string.Join(", ", given)}; Rewired's controller assignments {(saved ? "saved again" : "not saved (no player-prefs data store)")}; " +
            $"{maps}.");
    }

    /// <summary>
    /// After <see cref="GiveBackAfterUnfinishedSession"/>: Rewired loaded this start's saved maps while the keyboard and
    /// mouse were on nobody, so giving them back can leave a player with Rewired's default maps (their remaps came back only
    /// at the next start before). Loads each one's saved maps again through Rewired's data store (as at start-up; nothing
    /// changes for a player who never remapped) and says whether that changed them. Never throws: a failure is in the line.
    /// </summary>
    private static string LoadSavedMaps(List<(int PlayerId, ControllerType Type, int ControllerId)> given)
    {
        try
        {
            if (ReInput.userDataStore?.TryCast<UserDataStore_PlayerPrefs>() is not { } store)
            {
                return "their saved maps not loaded again (no player-prefs data store)";
            }

            int changed = 0;
            int bindings = 0;
            foreach ((int playerId, ControllerType type, int controllerId) in given)
            {
                Player player = ReInput.players.GetPlayer(playerId);
                ControllerMapsPrint before = ControllerMapsReader.Read(player, type, controllerId);
                store.LoadControllerData(playerId, type, controllerId);
                ControllerMapsPrint after = ControllerMapsReader.Read(player, type, controllerId);
                changed += after.SameMapsAs(before) ? 0 : 1;
                bindings += after.Bindings;
            }

            return changed == 0
                ? $"their maps were already as saved ({bindings} bindings)"
                : $"their saved maps (their own remaps) loaded again for {changed} of {given.Count} ({bindings} bindings)";
        }
        catch (Exception ex)
        {
            ModLog.Error("Your keyboard: loading the saved maps of the keyboard and mouse given back failed; they load at the next start", ex);
            return "their saved maps not loaded again (see the error above; they load at the next start)";
        }
    }

    /// <summary>Saves Rewired's controller assignments as they are now. False when there's no player-prefs data store (nothing to save).</summary>
    private static bool SaveAssignments() =>
        ReInput.userDataStore?.TryCast<UserDataStore_PlayerPrefs>() is { } store && store.SaveControllerAssignments();

    /// <summary>Writes the owners file (atomically). A failure is logged: the session goes on without the safety net.</summary>
    private void NoteOwners()
    {
        if (_owners.Count == 0)
        {
            return; // nobody had them: nothing to give back
        }

        try
        {
            string text = KeyboardOwners.Format(_owners.Select(o => new KeyboardOwner(o.PlayerId, o.Keyboard, o.Mouse)));
            Directory.CreateDirectory(CampaignFiles.DataFolder);
            string temporary = OwnersPath + CampaignFiles.PartSuffix;
            File.WriteAllText(temporary, text);
            File.Move(temporary, OwnersPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            ModLog.Warn($"Your keyboard: couldn't note which players have it in {OwnersFileLabel} ({CampaignFiles.ErrorText(ex)}); should the game close " +
                "or crash before the session ends, it may start without the keyboard.");
        }
    }

    /// <summary>Deletes the owners file, if any. A failure is logged: the next start finds the keyboard on its players and only deletes it.</summary>
    private static void ForgetOwners()
    {
        try
        {
            if (File.Exists(OwnersPath))
            {
                File.Delete(OwnersPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($"Your keyboard: {OwnersFileLabel} couldn't be deleted ({CampaignFiles.ErrorText(ex)}); the next start checks it again.");
        }
    }
}
#endif
