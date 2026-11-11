#if HAS_INTEROP
using System;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The map's relic bar browsed as on the host's screen (v1). The host's relic
/// toggle (<c>WorldMapMenusHandler.OnToggleRelicsNavigation</c>) only moves the cursor: into the relic bar
/// (<c>m_RelicsList</c>'s navigable parent, its default relic selected next frame) or, when the cursor is in it, back to
/// the map (verified in the game's code: <c>NavigableParent.HasChild</c>, then
/// <c>AbstractNavigableUtils.SelectOnNextFrame</c>); a relic icon (<c>ManualRelicIcon</c>, a tooltip definition) shows its
/// details while it's selected. So the bar's state is where the host's cursor is, read where the map view reads the cursor
/// (no patch).
/// <list type="bullet">
/// <item>Host: the map view says which relic the cursor is on in the bar (its asset id and place), or nothing when it isn't
/// in the bar (<see cref="Core.Campaign.MapViewDetails.Relic"/>).</item>
/// <item>Friend: when the host's cursor goes into the bar, this PC's map runs the game's own toggle (refused there for
/// anything else, <see cref="MapGuards"/>), then puts its cursor on the same relic of its own copy of the run, whose details
/// show in this PC's language; when the host's cursor leaves the bar, the same toggle brings this PC's cursor back to the
/// map, where the host's node is held as before. A toggle that doesn't move the cursor here (the map ignoring input just
/// then) is followed by selecting the relic directly.</item>
/// </list>
/// Nothing here throws into the game.
/// </summary>
internal static class RelicBarMirror
{
    private const double ToggleWaitSeconds = 0.4;
    private const int NamedLines = 5;

    private static double _toggledAt = double.NegativeInfinity;
    private static string? _heldRelic;
    private static int? _heldAt;
    private static IntPtr _heldNavigable;
    private static bool _hostLogged;
    private static bool _fallbackLogged;
    private static int _toggles;
    private static int _moves;
    private static int _lines;
    private static int _errors;

    /// <summary>True while the mirror runs the map's own relic toggle (the only toggle a friend's map guard lets through).</summary>
    public static bool Toggling { get; private set; }

    /// <summary>For the end-of-session line.</summary>
    public static string Summary => $"{_toggles} relic bar toggle(s) and {_moves} relic cursor move(s) as on the host's screen";

    /// <summary>Friend: following started or stopped.</summary>
    public static void Reset()
    {
        _toggledAt = double.NegativeInfinity;
        _heldRelic = null;
        _heldAt = null;
        _heldNavigable = IntPtr.Zero;
        _fallbackLogged = false;
        _toggles = 0;
        _moves = 0;
        _lines = 0;
    }

    /// <summary>Host: hosting started.</summary>
    public static void ResetHost() => _hostLogged = false;

    /// <summary>
    /// Host: the relic <paramref name="current"/> is (its asset id and place in the bar) when it's in the map's relic bar;
    /// nulls otherwise. Read only when the cursor moves. Never throws.
    /// </summary>
    public static (string? Relic, int? At) HostRelic(AbstractNavigable current)
    {
        try
        {
            ManualScrollingList? list = WorldMapMenusHandler.s_Instance?.m_RelicsList;
            NavigableParent? bar = list?.m_NavParent;
            if (list == null || bar == null || !bar.HasChild(current))
            {
                return (null, null);
            }

            ManualRelicIcon? icon = current.GetComponentInParent<ManualRelicIcon>();
            string id = SafeText.Clean(icon?.CurrentRelic?.id, Core.Campaign.MapViewDetails.MaxRelicIdLength);
            int at = icon == null ? -1 : PlaceOf(list, icon);
            if (id.Length == 0 || at is < 0 or > Core.Campaign.MapViewDetails.MaxRelicPlace)
            {
                return (null, null);
            }

            if (!_hostLogged)
            {
                _hostLogged = true;
                ModLog.Info($"Scenes: the host browses its relic bar (on {id}, place {at + 1}); friends' maps follow it (logged once).");
            }

            return (id, at);
        }
        catch (Exception ex)
        {
            Failed("reading the host's relic bar", ex);
            return (null, null);
        }
    }

    /// <summary>
    /// Friend, as the host's map cursor is held: this PC's relic bar opened or closed, and its cursor on the host's relic
    /// (<paramref name="relic"/> at <paramref name="at"/>; null: the host's cursor isn't in the bar). True while the host's
    /// cursor is in the bar (the map's node isn't held then). Never throws.
    /// </summary>
    public static bool Follow(string? relic, int? at)
    {
        try
        {
            return FollowCore(relic, at);
        }
        catch (Exception ex)
        {
            Toggling = false;
            Failed("following the host's relic bar", ex);
            return relic != null;
        }
    }

    private static bool FollowCore(string? relic, int? at)
    {
        WorldMapMenusHandler? handler = WorldMapMenusHandler.s_Instance;
        ManualScrollingList? list = handler?.m_RelicsList;
        NavigableParent? bar = list?.m_NavParent;
        NavigationController? navigation = WorldMap.Navigation;
        if (handler == null || list == null || bar == null || navigation == null || !WorldMap.MapLoaded)
        {
            return relic != null;
        }

        AbstractNavigable? current = navigation.CurrentNavigable;
        bool inBar = current != null && bar.HasChild(current);
        double now = FrameClock.Seconds;
        bool waiting = now - _toggledAt < ToggleWaitSeconds;
        if (relic == null)
        {
            if (inBar && !waiting)
            {
                // The host's cursor left the bar: the same toggle brings this PC's back to the map.
                Toggle(handler, "closed");
            }

            return false;
        }

        if (!inBar)
        {
            if (!waiting && _toggledAt < now - (ToggleWaitSeconds * 3))
            {
                Toggle(handler, "opened");
                return true;
            }

            if (waiting)
            {
                return true; // its own toggle selects the bar's relic next frame
            }

            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                ModLog.Info("Scenes: the relic bar's own toggle didn't move this map's cursor (its map was ignoring input just then); the host's relic is selected directly.");
            }
        }

        // Held ten times a second while the host browses: the bar's icons are looked through only when something moved.
        if (current != null && current.Pointer == _heldNavigable && relic == _heldRelic && at == _heldAt)
        {
            return true;
        }

        AbstractNavigable? target = Find(list, relic, at ?? -1);
        if (target == null)
        {
            return true;
        }

        _heldRelic = relic;
        _heldAt = at;
        _heldNavigable = target.Pointer;
        if (current != null && current.Pointer == target.Pointer)
        {
            return true;
        }

        FriendUi.Select(navigation, target);
        _moves++;
        if (_lines++ < NamedLines)
        {
            ModLog.Info($"Scenes: this map's cursor is on the relic {HostText.KeyForLog(relic)} (place {(at ?? 0) + 1}) as on {LogNames.HostTag()}'s screen, " +
                $"its details in this PC's language{(_lines == NamedLines ? " (further moves are only counted)" : string.Empty)}.");
        }

        return true;
    }

    /// <summary>Runs the map's own relic toggle, past the friend's lock and the map guard.</summary>
    private static void Toggle(WorldMapMenusHandler handler, string what)
    {
        _toggledAt = FrameClock.Seconds;
        _toggles++;
        FriendUi.Apply(() =>
        {
            Toggling = true;
            try
            {
                handler.OnToggleRelicsNavigation(null!);
            }
            finally
            {
                Toggling = false;
            }
        });
        if (_lines++ < NamedLines)
        {
            ModLog.Info($"Scenes: this map's relic bar {what} with the game's own toggle, as on {LogNames.HostTag()}'s screen.");
        }
    }

    /// <summary>The navigable of the bar's icon showing relic <paramref name="relic"/> (the one at <paramref name="at"/> when it's there twice), or null.</summary>
    private static AbstractNavigable? Find(ManualScrollingList list, string relic, int at)
    {
        AbstractNavigable? first = null;
        int place = 0;
        foreach (ManualRelicIcon icon in list.GetComponentsInChildren<ManualRelicIcon>(false))
        {
            if (icon != null && icon.CurrentRelic?.id == relic && icon.Navigable is { } navigable)
            {
                if (place == at)
                {
                    return navigable;
                }

                first ??= navigable;
            }

            place++;
        }

        return first;
    }

    /// <summary>The place of <paramref name="icon"/> among the bar's shown icons (0 first), or -1.</summary>
    private static int PlaceOf(ManualScrollingList list, ManualRelicIcon icon)
    {
        int place = 0;
        foreach (ManualRelicIcon shown in list.GetComponentsInChildren<ManualRelicIcon>(false))
        {
            if (shown != null && shown.Pointer == icon.Pointer)
            {
                return place;
            }

            place++;
        }

        return -1;
    }

    private static void Failed(string what, Exception ex)
    {
        if (_errors++ < 3)
        {
            ModLog.Error($"Scenes: {what} failed", ex);
        }
    }
}
#endif
