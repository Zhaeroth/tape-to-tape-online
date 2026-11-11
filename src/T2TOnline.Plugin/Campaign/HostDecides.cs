#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppControllerConnection;
using Il2CppRewired;
using T2TOnline.Core.Hosting;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// The host is always the deciding player (D45). Wherever the game takes UI input from
/// one controlling player only (every single-player menu, the map's nodes, the preview's shortcuts, the controller
/// screen's back and start), that player is set by <c>UIInputSource.SetSingleControllingPlayer_Internal</c>, and every path
/// that can hand it to someone else goes through it: a friend's virtual pad pressing first on the title screen, the host's
/// pad disconnecting (the first remaining gamepad player takes over), nobody controlling (the first gamepad player
/// checked). While this PC hosts, a friend's player given control there (a dropped friend's parked pad included) is
/// replaced by the host's own: the host's pad when one is a game player, else the host's keyboard (game player 0). A
/// friend's player is one whose Rewired player has a custom controller (the friends' virtual pads are the only ones while
/// hosting: the game defines none, and the F8 loopback, which makes one for the host's own pad, refuses to start then).
/// Each replacement is logged with who it would have been. Couch play, and hosting alone, are untouched. So is a menu
/// whose prefab lets any player drive it (<c>T2TMenu._canBeControlledByAnyPlayer</c>: the pause menu, the map's own menu,
/// the lineup, Settings, the glossary, the unlocks): while one is up the game takes every player's press without asking
/// who controls, a friend's too, as on a couch, and hands the menus back to the player it saved as the menu closes.
/// <para>
/// A host on the keyboard needs one thing more. The game asks at every menu press of a player that isn't on a keyboard
/// whether it may take the menus because nobody has them (<c>UIInputSource.SetControllerIfNone</c>, called by both
/// <c>IsControlledBy</c>): with its controlling list empty it calls the setter above and answers yes, whoever the setter
/// chose. A host's pad player goes on that list, so the next ask is refused by the game itself. A keyboard player never
/// does (the setter adds only a player without keyboard or mouse: keyboard players always control), so with a keyboard
/// host the list stays empty and every press of a friend would be taken as the controlling player's: their stick would
/// move the host's cursor and their buttons confirm, back out and open the host's menus. So while this PC hosts and its
/// own player is a keyboard one, that ask is answered no for a friend's player and the game's own isn't run
/// (<see cref="FriendControlAsk"/>); the game then looks the friend up on its list, where they aren't. The first few
/// refusals are logged, the rest counted. With a pad of the host's own in the game nothing changes.
/// </para>
/// </summary>
internal sealed class HostDecides : ICampaignModule
{
    /// <summary>Replacements named in the log; later ones are counted.</summary>
    private const int NamedSubstitutions = 20;

    /// <summary>A keyboard host's refusals named in the log; later ones are counted (a friend's every menu press is one).</summary>
    private const int NamedRefusals = 3;

    /// <summary>The check tour's pad stands in for the host's own: its Rewired player isn't a friend's. -1 with no tour.</summary>
    internal static int TourPadPlayer { get; set; } = -1;

    /// <summary>
    /// The check tour's pad set aside for a moment (the campaign tour's keyboard host step): still not a friend's, but not
    /// the host's own player either, so the host's own player is the keyboard alone, as on a keyboard host's PC. False
    /// outside that step: then the choice is exactly a real session's.
    /// </summary>
    internal static bool TourPadSetAside { get; set; }

    /// <summary>The check tour's read: a friend's asks for the menus refused this session (a keyboard host's).</summary>
    internal static int Refusals => _refusals;

    private static int _substitutions;
    private static int _refusals;
    private static int _errors;
    private static bool _noHostLogged;

    // The host's own player as the last ask of this frame found it: a friend's held stick asks many times a frame.
    private static int _hostChoiceFrame = -1;
    private static IntPtr _hostChoiceSource;
    private static HostPlayerChoice _hostChoice;

    /// <inheritdoc />
    public string Name => "Host decides";

    /// <inheritdoc />
    public void StartHosting(CampaignRelay relay)
    {
        _substitutions = 0;
        _refusals = 0;
        _noHostLogged = false;
        _hostChoiceFrame = -1;
        ModLog.Info("Host decides: while hosting, menus that take one player's input take the host's (a friend's controller handed control is replaced, and logged; " +
            "with the host on the keyboard a friend's controller asking for them is refused, and counted).");
    }

    /// <inheritdoc />
    public void StopHosting(string reason)
    {
        if (_substitutions > 0)
        {
            ModLog.Info($"Host decides: the host's player was put in a friend's place {_substitutions} time(s) this session.");
        }

        if (_refusals > 0)
        {
            ModLog.Info($"Host decides: a friend's controller asking for the menus was refused {_refusals} time(s) this session (the host was on the keyboard, with no pad of its own in the game).");
        }
    }

    /// <summary>
    /// Patches the controlling-player setter and the game's ask for a controlling player when there's none. Adds a line to
    /// <paramref name="failed"/> for each that couldn't go in.
    /// </summary>
    internal static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ScenePatches.Prefix(harmony, failed, typeof(UIInputSource), nameof(UIInputSource.SetSingleControllingPlayer_Internal), typeof(HostDecides), nameof(SetControllerPrefix));
        ScenePatches.Prefix(harmony, failed, typeof(UIInputSource), nameof(UIInputSource.SetControllerIfNone), typeof(HostDecides), nameof(ControllerIfNonePrefix));
    }

    /// <summary>
    /// The game asks whether <paramref name="player"/> may take the menus because nobody has them. While hosting on the
    /// keyboard a friend's player is answered no and the game's own ask is skipped (<see cref="FriendControlAsk"/>): run, it
    /// would answer yes at every press, since the host's keyboard player it hands them to never goes on the controlling
    /// list. Every other ask runs as the game's.
    /// </summary>
    private static bool ControllerIfNonePrefix(UIInputSource __instance, IUIInputPlayer player, ref bool __result)
    {
        try
        {
            CampaignRelay? relay = CampaignRelay.Current;
            if (relay == null || !relay.Hosting || player == null || !IsFriends(player, out int friendRewired) ||
                !FriendControlAsk.Refused(hosting: true, friendsPlayer: true, HostChoice(__instance)))
            {
                return true;
            }

            int count = ++_refusals;
            if (count <= NamedRefusals)
            {
                ModLog.Info($"Host decides: a friend's controller (Rewired player {friendRewired}, game player {GamePlayer(friendRewired)}) asked for the menus, which nobody " +
                    $"but the host's keyboard has; refused, they stay the host's{(count == NamedRefusals ? " (further refusals are only counted)" : string.Empty)}.");
            }

            __result = false;
            return false;
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Host decides: checking who asks for the menus failed (the game's own answer stands)", ex);
            }

            return true;
        }
    }

    /// <summary>The game is making <paramref name="player"/> the one controlling player: a friend's becomes the host's.</summary>
    private static void SetControllerPrefix(UIInputSource __instance, ref IUIInputPlayer player)
    {
        try
        {
            // While hosting, not only while friends are in the link: a dropped friend's virtual pad stays a game player for
            // minutes (parked), and the game hands it control when the host's pad goes, or when nobody controls.
            CampaignRelay? relay = CampaignRelay.Current;
            if (relay == null || !relay.Hosting || player == null || !IsFriends(player, out int friendRewired))
            {
                return;
            }

            IUIInputPlayer? host = HostPlayer(__instance, out string hostWhat);
            if (host == null)
            {
                if (!_noHostLogged)
                {
                    _noHostLogged = true;
                    ModLog.Warn($"Host decides: the game handed control to a friend's controller (Rewired player {friendRewired}), and the host has no keyboard or pad player to take it; left as the game set it.");
                }

                return;
            }

            int count = ++_substitutions;
            if (count <= NamedSubstitutions)
            {
                ModLog.Info($"Host decides: the game was handing the menus to a friend's controller (Rewired player {friendRewired}, game player {GamePlayer(friendRewired)}); " +
                    $"the host's {hostWhat} has them instead{(count == NamedSubstitutions ? " (further replacements are only counted)" : string.Empty)}.");
            }

            player = host;
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Host decides: checking the game's controlling player failed (left as the game set it)", ex);
            }
        }
    }

    /// <summary>True when <paramref name="player"/>'s Rewired player is driven by a virtual pad (a custom controller).</summary>
    private static bool IsFriends(IUIInputPlayer player, out int rewiredId)
    {
        rewiredId = player.NativeId;
        Player? rewired = rewiredId >= 0 && rewiredId != TourPadPlayer ? ReInput.players.GetPlayer(rewiredId) : null;
        return rewired != null && rewired.controllers.customControllerCount > 0;
    }

    /// <summary>
    /// The host's own UI player among those the game tracks: its first pad player, else its keyboard player
    /// (<see cref="HostPlayerChoice"/>).
    /// </summary>
    private static IUIInputPlayer? HostPlayer(UIInputSource source, out string what)
    {
        Il2CppSystem.Collections.Generic.List<IUIInputPlayer>? tracked = source.m_TrackedPlayers;
        HostPlayerChoice choice = Choose(tracked);
        IUIInputPlayer? chosen = choice.Chosen >= 0 ? tracked![choice.Chosen] : null;
        int chosenId = chosen?.NativeId ?? -1;
        what = chosen == null ? string.Empty : $"{(choice.HasPad ? "pad" : "keyboard")} (Rewired player {chosenId}, game player {GamePlayer(chosenId)})";
        return chosen;
    }

    /// <summary>Which of <paramref name="tracked"/> is the host's own player (<see cref="HostPlayerChoice"/>).</summary>
    private static HostPlayerChoice Choose(Il2CppSystem.Collections.Generic.List<IUIInputPlayer>? tracked)
    {
        var choice = default(HostPlayerChoice);
        for (int i = 0; tracked != null && i < tracked.Count && !choice.HasPad; i++)
        {
            int id = tracked[i]?.NativeId ?? -1;
            Player? rewired = id >= 0 ? ReInput.players.GetPlayer(id) : null;
            bool tourPad = id == TourPadPlayer;
            if (rewired != null && !(tourPad && TourPadSetAside))
            {
                Player.ControllerHelper controls = rewired.controllers;
                choice.Consider(i, controls.customControllerCount > 0 && !tourPad, controls.joystickCount > 0 || tourPad, controls.hasKeyboard || controls.hasMouse);
            }
        }

        return choice;
    }

    /// <summary>
    /// The check tour's read of the rule's input now: true when a friend's ask would be refused (the host's own player a
    /// keyboard one, no pad of its own); <paramref name="who"/> names that player (<c>keyboard (Rewired player 0, game player 0)</c>, or <c>none</c>).
    /// </summary>
    internal static bool HostOnKeyboardNow(UIInputSource source, out string who)
    {
        HostPlayerChoice choice = Choose(source.m_TrackedPlayers);
        who = HostPlayer(source, out string what) == null ? "none" : what;
        return FriendControlAsk.Refused(hosting: true, friendsPlayer: true, choice);
    }

    /// <summary>
    /// The host's own player among those <paramref name="source"/> tracks, read once a frame: the ask it answers comes at a
    /// friend's every menu press, and a held stick presses many times a frame.
    /// </summary>
    private static HostPlayerChoice HostChoice(UIInputSource source)
    {
        int frame = Streaming.FrameClock.Frame;
        IntPtr pointer = source.Pointer;
        if (frame != _hostChoiceFrame || pointer != _hostChoiceSource)
        {
            _hostChoice = Choose(source.m_TrackedPlayers);
            _hostChoiceFrame = frame;
            _hostChoiceSource = pointer;
        }

        return _hostChoice;
    }

    private static int GamePlayer(int rewiredId) => RewiredPlayerInputAssigner.Instance != null ? RewiredPlayerInputAssigner.GetPlayerId(rewiredId) : -1;
}

/// <summary>Registers the host-decides module and its patch.</summary>
internal static partial class CampaignModules
{
    static partial void AddHostDecides(List<ICampaignModule> modules, CampaignContext context) => modules.Add(new HostDecides());

    static partial void InstallHostDecides(HarmonyLib.Harmony harmony, List<string> failed) => HostDecides.Install(harmony, failed);
}
#endif
