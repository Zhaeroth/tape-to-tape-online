#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.Input;
using Il2CppControllerConnection;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// A Rewired custom controller the mod gives a free Rewired player, standing in for a friend's pad
/// (<see cref="VirtualPad"/>) or keyboard (<see cref="VirtualKeyboard"/>). To the game it's a plugged-in controller in
/// activation mode; pressing the activate action joins it through the game's own join code (<see cref="GameJoin"/>).
/// </summary>
/// <remarks>
/// Rewired's own joystick auto-assignment picks a player by its joystick count alone (custom controllers aren't counted),
/// so to it a player holding only a virtual controller is empty: a pad plugged in on this PC (or this PC's pad coming back to a player
/// it had before) could be given to it and drive the same game player as the friend. While the controller is on a player, that player is
/// excluded from auto-assignment, which every path of it honours; the setting goes back as it was with the controller (or passes to the
/// controller that replaces it on the same player, <see cref="Attach"/>).
/// </remarks>
internal abstract class VirtualController : IDisposable
{
    /// <summary>The map layout every virtual controller uses.</summary>
    protected const int DefaultLayoutId = 0;

    private bool? _excludedBefore; // the player's auto-assignment exclusion before a virtual controller set it; null until then
    private bool _handedOver;
    private bool _disposed;
    private readonly HashSet<int> _actionsSeen = new();
    private int _actionReadFailures;

    /// <summary>Wraps a created custom controller for <paramref name="player"/>.</summary>
    protected VirtualController(CustomController controller, Player player, EInputDeviceType deviceType)
    {
        Controller = controller;
        Player = player;
        DeviceType = deviceType;
    }

    /// <summary>The Rewired player the controller belongs to.</summary>
    public Player Player { get; }

    /// <summary>The Rewired player id.</summary>
    public int RewiredPlayerId => Player.id;

    /// <summary>The device family the game treats it as (button prompts, gamepad and keyboard checks).</summary>
    public EInputDeviceType DeviceType { get; protected set; }

    /// <summary>True for a virtual keyboard.</summary>
    public bool IsKeyboard => DeviceType == EInputDeviceType.MouseKeyboard;

    /// <summary>What it is, for the log: "virtual pad" or "virtual keyboard".</summary>
    public abstract string Kind { get; }

    /// <summary>The friend's controls its maps are built from, or null while it has the game's (or, for a keyboard, none yet).</summary>
    public InputBindingSet? Controls { get; protected set; }

    /// <summary>True after <see cref="Dispose"/>.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>The Rewired custom controller.</summary>
    protected CustomController Controller { get; }

    /// <summary>The map categories currently enabled for one controller of <paramref name="player"/>.</summary>
    public static IReadOnlyCollection<int> EnabledCategories(Player player, ControllerType type, int controllerId)
    {
        var categories = new SortedSet<int>();
        foreach (ControllerMap map in MapsOf(player, type, controllerId))
        {
            if (map.enabled)
            {
                categories.Add(map.categoryId);
            }
        }

        return categories;
    }

    /// <summary>Enables exactly <paramref name="categories"/> among one controller's maps in <paramref name="player"/>.</summary>
    public static void SetEnabledCategories(Player player, ControllerType type, int controllerId, IReadOnlyCollection<int> categories)
    {
        foreach (ControllerMap map in MapsOf(player, type, controllerId))
        {
            map.enabled = categories.Contains(map.categoryId);
        }
    }

    /// <summary>The map categories currently enabled for this controller.</summary>
    public IReadOnlyCollection<int> CurrentEnabledCategories() => EnabledCategories(Player, ControllerType.Custom, Controller.id);

    /// <summary>The controller's maps as they are now (every map, any layout), for the maps guard.</summary>
    public ControllerMapsPrint ReadMaps() => ControllerMapsReader.Read(Player, ControllerType.Custom, Controller.id);

    /// <summary>Each of the controller's maps: its category and whether it's on (the guard's cheap look, a few times a second).</summary>
    public List<(int Category, bool Enabled)> LookAtMaps() => ControllerMapsReader.Look(Player, ControllerType.Custom, Controller.id);

    /// <summary>
    /// Puts the controller's maps back as the mod built them (<paramref name="expected"/>), after something changed them:
    /// maps the mod never made (another layout, a category it has none in, a second one) are removed; a map missing from a
    /// category is loaded again, on if <paramref name="enabled"/> has its category (the categories on when last seen whole;
    /// the game sets them again at its next switch anyway); then every map's element maps are rebuilt from the controls it
    /// has (<see cref="RebuildMaps"/>). Returns how many maps were removed and loaded, for the log.
    /// </summary>
    public (int Removed, int Loaded) RestoreMaps(ControllerMapsPrint expected, IReadOnlyCollection<int> enabled)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(enabled);
        Player.ControllerHelper.MapHelper maps = Player.controllers.maps;
        var kept = new HashSet<int>();
        var remove = new List<int>();
        foreach (ControllerMap map in Il2CppLists.Items(maps.GetMaps(ControllerType.Custom, Controller.id)))
        {
            if (map == null)
            {
                continue;
            }

            bool wanted = map.layoutId == DefaultLayoutId && expected.In(map.categoryId).Maps > 0 && kept.Add(map.categoryId);
            if (!wanted)
            {
                remove.Add(map.id);
            }
        }

        foreach (int mapId in remove)
        {
            maps.RemoveMap(ControllerType.Custom, Controller.id, mapId);
        }

        int loaded = 0;
        foreach (int category in expected.Categories.Keys)
        {
            if (MapIn(category) == null)
            {
                maps.LoadMap(ControllerType.Custom, Controller.id, category, DefaultLayoutId, enabled.Contains(category));
                loaded++;
            }
        }

        RebuildMaps();
        return (remove.Count, loaded);
    }

    /// <summary>Empties every map and fills it again from the controls the controller has now (the friend's, or the game's template maps).</summary>
    protected abstract void RebuildMaps();

    /// <summary>
    /// Devices on the controller's Rewired player besides it, by name ("a joystick \"Xbox Controller\"", "the keyboard"):
    /// that player is the friend's alone, so anything else there would drive their game player with someone else's input
    /// and maps. Empty when there's none (the usual answer). <paramref name="skipJoystickId"/> is a joystick the mod holds
    /// itself (the one-PC test's pad), which its own guard takes back.
    /// </summary>
    public List<string> ForeignDevices(int skipJoystickId)
    {
        var found = new List<string>();
        Player.ControllerHelper controllers = Player.controllers;
        if (controllers.joystickCount > 0)
        {
            foreach (Joystick joystick in Il2CppLists.Items(controllers.Joysticks))
            {
                if (joystick != null && joystick.id != skipJoystickId)
                {
                    found.Add($"a joystick \"{SafeText.Clean(joystick.name, 48)}\"");
                }
            }
        }

        if (controllers.hasKeyboard)
        {
            found.Add("the keyboard");
        }

        if (controllers.hasMouse)
        {
            found.Add("the mouse");
        }

        if (controllers.customControllerCount > 1)
        {
            found.Add($"{controllers.customControllerCount - 1} other custom controller(s)");
        }

        return found;
    }

    /// <summary>
    /// Moves every joystick on the controller's player (but <paramref name="skipJoystickId"/>) to where the game puts a pad
    /// that connects: the first Rewired player with no controller, waiting to join (the game's own connect handler does
    /// the same). With no such player it's only taken off (plugged in again, the game takes it as a new pad). Returns
    /// where each went, for the log.
    /// </summary>
    public List<string> MoveForeignJoysticks(int skipJoystickId)
    {
        var moved = new List<string>();
        Player.ControllerHelper controllers = Player.controllers;
        var joysticks = new List<Joystick>();
        foreach (Joystick joystick in Il2CppLists.Items(controllers.Joysticks))
        {
            if (joystick != null && joystick.id != skipJoystickId)
            {
                joysticks.Add(joystick);
            }
        }

        foreach (Joystick joystick in joysticks)
        {
            string name = $"\"{SafeText.Clean(joystick.name, 48)}\"";
            int alsoOn = OtherPlayerWith(joystick);
            if (alsoOn >= 0)
            {
                // Someone else plays with it too (the host's own pad, say): it only comes off the friend's player.
                controllers.RemoveController(joystick);
                moved.Add($"{name} off it (it stays on Rewired player {alsoOn})");
                continue;
            }

            Player? free = null;
            for (int id = 1; id < ReInput.players.playerCount && free == null; id++)
            {
                free = IsFree(ReInput.players.GetPlayer(id)) ? ReInput.players.GetPlayer(id) : null;
            }

            if (free != null)
            {
                free.controllers.AddController(joystick, true);
                RewiredPlayerInputAssigner.SetControlsToActivationMode(free.id);
                moved.Add($"{name} to Rewired player {free.id}, waiting to join");
            }
            else
            {
                controllers.RemoveController(joystick);
                moved.Add($"{name} off it (no Rewired player is free)");
            }
        }

        return moved;
    }

    /// <summary>Another Rewired player than this controller's that has <paramref name="joystick"/> too, or -1.</summary>
    private int OtherPlayerWith(Joystick joystick)
    {
        for (int id = 0; id < ReInput.players.playerCount; id++)
        {
            if (id != Player.id && ReInput.controllers.IsJoystickAssignedToPlayer(joystick.id, id))
            {
                return id;
            }
        }

        return -1;
    }

    /// <summary>
    /// Rewired's own map automation on <paramref name="player"/>, for the log: its layout manager and map enabler (each
    /// does nothing without rule sets, and the game defines none, so neither can load or switch a virtual controller's maps).
    /// </summary>
    public static string MapAutomation(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        ControllerMapLayoutManager layouts = player.controllers.maps.layoutManager;
        ControllerMapEnabler enabler = player.controllers.maps.mapEnabler;
        return $"layout manager {(layouts.enabled ? "on" : "off")} with {layouts.ruleSets?.Count ?? 0} rule set(s), " +
            $"map enabler {(enabler.enabled ? "on" : "off")} with {enabler.ruleSets?.Count ?? 0} rule set(s)";
    }

    /// <summary>
    /// Holds the controller neutral (its friend dropped, unplugged their pad, or the AI plays for them). When it's theirs
    /// again, what they're already holding counts only once they've let it go.
    /// </summary>
    public abstract void ApplyNeutral();

    /// <summary>
    /// Notes which of the game's actions its player gave the game this frame (each once, for <see cref="ActionsSeen"/>):
    /// proof that the friend's maps reach the game's own actions (a trigger's skating backwards, a remapped check), not
    /// only that their input arrived. Call once a frame, after Rewired's update. Only actions not seen yet are read, so it
    /// costs least once the usual ones have come. Never throws: after three failures it stops, said once.
    /// </summary>
    public void NoteActions()
    {
        if (_disposed || _actionReadFailures >= 3)
        {
            return;
        }

        try
        {
            IReadOnlyList<int> ids = RewiredBindings.ActionIds();
            if (_actionsSeen.Count >= ids.Count)
            {
                return;
            }

            foreach (int id in ids)
            {
                if (!_actionsSeen.Contains(id) && Math.Abs(Player.GetAxisRaw(id)) > 0.5f)
                {
                    _actionsSeen.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            if (++_actionReadFailures == 3)
            {
                ModLog.Warn($"{Kind}: reading the game's actions on Rewired player {RewiredPlayerId} failed three times ({ModLog.SafeErrorText(ex)}); not read again.");
            }
        }
    }

    /// <summary>The game's actions its player gave the game while it was on it, by name in the game's order, and how many of the game's there are.</summary>
    public (IReadOnlyList<string> Names, int Of) ActionsSeen()
    {
        IReadOnlyList<int> ids = RewiredBindings.ActionIds();
        return (ids.Where(_actionsSeen.Contains).Select(RewiredBindings.ActionName).ToList(), ids.Count);
    }

    /// <summary>Removes the controller from its player and destroys it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            VirtualPadCompat.Unregister(Player.id, Controller.id);
            if (!_handedOver && _excludedBefore is bool before)
            {
                Player.controllers.excludeFromControllerAutoAssignment = before;
            }

            Player.controllers.RemoveController(Controller);
            ReInput.controllers.DestroyCustomController(Controller);
        }
        catch (Exception ex)
        {
            ModLog.Error($"{Kind}: could not remove it cleanly", ex);
        }
    }

    /// <summary>
    /// The Rewired player a new controller goes on: <paramref name="preferredPlayerId"/> if that one is free (or holds only
    /// <paramref name="replacing"/>, the controller the new one takes over from), otherwise the lowest-numbered free one
    /// (player 0 is the keyboard's). Null if none is free.
    /// </summary>
    protected static Player? ChoosePlayer(int preferredPlayerId, VirtualController? replacing)
    {
        // Never player 0, even when asked and free: it's the host's keyboard player, whose maps a friend's mustn't touch.
        Player? preferred = preferredPlayerId > 0 ? ReInput.players.GetPlayer(preferredPlayerId) : null;
        if (preferred != null && (IsFree(preferred) || (replacing != null && replacing.Player.id == preferred.id && HoldsOnly(preferred, replacing))))
        {
            return preferred;
        }

        for (int id = 1; id < ReInput.players.playerCount; id++)
        {
            Player player = ReInput.players.GetPlayer(id);
            if (IsFree(player))
            {
                return player;
            }
        }

        return null;
    }

    /// <summary>
    /// Gives the controller to its player, keeps Rewired's joystick auto-assignment off that player (see the remarks), and
    /// registers it for the game's device checks. When it takes over from <paramref name="replacing"/> on the same player,
    /// the player's own exclusion setting passes to it, so it goes back as it was when this one goes.
    /// </summary>
    protected void Attach(VirtualController? replacing)
    {
        Player.controllers.AddController(Controller, true);
        Player.ControllerHelper controllers = Player.controllers;
        if (replacing != null && replacing.Player.id == Player.id)
        {
            _excludedBefore = replacing._excludedBefore;
            replacing._handedOver = true;
        }
        else
        {
            _excludedBefore = controllers.excludeFromControllerAutoAssignment;
        }

        controllers.excludeFromControllerAutoAssignment = true;
        VirtualPadCompat.Register(Player.id, Controller.id, DeviceType);
    }

    /// <summary>"excluded already" or "not excluded": the player's auto-assignment setting before a virtual controller set it.</summary>
    protected string ExclusionBefore => _excludedBefore == true ? "excluded already" : "not excluded";

    /// <summary>The controller's map in <paramref name="category"/>, or null.</summary>
    protected ControllerMap? MapIn(int category) => Player.controllers.maps.GetMap(ControllerType.Custom, Controller.id, category, DefaultLayoutId);

    /// <summary>Loads the controller's (empty) default map in every one of <paramref name="categories"/>, on if <paramref name="enabled"/> has it.</summary>
    protected void LoadMaps(IEnumerable<int> categories, IReadOnlyCollection<int>? enabled)
    {
        Player.ControllerHelper.MapHelper maps = Player.controllers.maps;
        foreach (int category in categories)
        {
            maps.LoadMap(ControllerType.Custom, Controller.id, category, DefaultLayoutId, enabled != null && enabled.Contains(category));
        }
    }

    private static List<ControllerMap> MapsOf(Player player, ControllerType type, int controllerId)
    {
        var all = new Il2CppSystem.Collections.Generic.List<ControllerMap>();
        player.controllers.maps.GetAllMaps(type, all);
        var maps = new List<ControllerMap>();
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].controllerId == controllerId)
            {
                maps.Add(all[i]);
            }
        }

        return maps;
    }

    private static bool IsFree(Player? player)
    {
        if (player == null)
        {
            return false;
        }

        Player.ControllerHelper controllers = player.controllers;
        return controllers.joystickCount == 0 && controllers.customControllerCount == 0 && !controllers.hasKeyboard && !controllers.hasMouse;
    }

    private static bool HoldsOnly(Player player, VirtualController controller)
    {
        Player.ControllerHelper controllers = player.controllers;
        return controllers.joystickCount == 0 && controllers.customControllerCount == 1 && !controllers.hasKeyboard && !controllers.hasMouse &&
            !controller._disposed;
    }
}
#endif
