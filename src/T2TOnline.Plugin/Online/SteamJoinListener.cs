#if HAS_INTEROP
using System;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSteamworks;
using Il2CppSteamworks.Data;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// Hears Steam ask this game to join a friend's lobby while it runs: an invite accepted in Steam, or "Join Game" on a
/// friend in Steam's friends list. Steam says so with callbacks (<c>GameLobbyJoinRequested_t</c>,
/// <c>GameRichPresenceJoinRequested_t</c>), and the game's Facepunch.Steamworks owns Steam's callback dispatch (D20: the
/// mod never registers callbacks of its own), so the mod adds itself to the two Facepunch events those callbacks raise,
/// <c>SteamFriends.OnGameLobbyJoinRequested</c> and <c>OnGameRichPresenceJoinRequested</c>. The game never subscribes to
/// them itself; nothing is patched. Facepunch runs them on the game's main thread, inside the game's own update
/// (<c>GameplayProjectInstaller.Update</c> → <c>SteamService.Update</c> → <c>Dispatch.Frame</c>; the game starts Steam
/// without async callbacks), so a handler only checks the request and leaves it in the session's
/// <see cref="SteamJoinInbox"/>, and never throws.
/// <para>
/// Two more Facepunch events are heard the same way, for the native lobby: <c>SteamMatchmaking.OnLobbyInvite</c> (Steam
/// delivered an invite to a lobby: the friends browser marks that game "Invited", through <see cref="SteamInviteBook"/>) and
/// <c>SteamFriends.OnGameOverlayActivated</c> (the overlay opened or closed: <see cref="SteamOverlayWatch"/>, so the lobby
/// can leave the pad alone while a profile or the invite window is up). They are added after the join events and fail apart
/// from them: if the game's Facepunch won't take them, joins still work and the browser just never says "Invited".
/// </para>
/// </summary>
/// <remarks>
/// A game Steam starts for an invite gets the lobby on its command line instead (<see cref="FromCommandLine"/>). Both end
/// in <see cref="OnlineSession"/>'s join path, as F7 and the panel's Join do. The handlers stay in Facepunch's events for
/// the session (as patches do, D38): a handler with nowhere to put a request drops it.
/// </remarks>
internal static class SteamJoinListener
{
    private const int RefusalsLogged = 3;
    private const int InvitesLogged = 6;
    private const int OverlayLogged = 6;

    // Kept here so the converted delegates live as long as Facepunch holds them.
    private static Il2CppSystem.Action<Lobby, SteamId>? _lobbyJoin;
    private static Il2CppSystem.Action<Friend, string>? _presenceJoin;
    private static Il2CppSystem.Action<Friend, Lobby>? _lobbyInvite;
    private static Il2CppSystem.Action<bool>? _overlayActivated;
    private static SteamJoinInbox? _inbox;
    private static Func<TimeSpan>? _now;
    private static int _refusals;
    private static int _failures;
    private static int _invitesHeard;
    private static int _overlayHeard;

    /// <summary>True once the mod is in Facepunch's join events.</summary>
    public static bool Installed => _lobbyJoin != null && _presenceJoin != null;

    /// <summary>True once the mod is in Facepunch's lobby invite and overlay events.</summary>
    public static bool LobbyEventsInstalled => _lobbyInvite != null && _overlayActivated != null;

    /// <summary>
    /// Adds the mod to Facepunch's join events (once), leaving what they hear in <paramref name="inbox"/>, stamped with
    /// <paramref name="now"/>. False, with why, when the game's Facepunch won't take them; then only the panel, F7 and a
    /// launch from an invite can join.
    /// </summary>
    public static bool Install(SteamJoinInbox inbox, Func<TimeSpan> now, out string problem)
    {
        _inbox = inbox;
        _now = now;
        problem = string.Empty;
        if (Installed)
        {
            return true;
        }

        try
        {
            _lobbyJoin ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Lobby, SteamId>>(new Action<Lobby, SteamId>(OnLobbyJoinRequested));
            _presenceJoin ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Friend, string>>(new Action<Friend, string>(OnRichPresenceJoinRequested));
            if (_lobbyJoin == null || _presenceJoin == null)
            {
                problem = "the join handlers couldn't be made for the game's Facepunch";
                _lobbyJoin = null;
                _presenceJoin = null;
                return false;
            }

            Il2CppSteamworks.SteamFriends.add_OnGameLobbyJoinRequested(_lobbyJoin);
            Il2CppSteamworks.SteamFriends.add_OnGameRichPresenceJoinRequested(_presenceJoin);
            InstallLobbyEvents();
            return true;
        }
        catch (Exception ex)
        {
            problem = ModLog.SafeErrorText(ex);
            _lobbyJoin = null;
            _presenceJoin = null;
            return false;
        }
    }

    /// <summary>
    /// Adds the mod to Facepunch's lobby invite and overlay events (once). Apart from the join events: a failure is logged and
    /// leaves the joins as they are.
    /// </summary>
    private static void InstallLobbyEvents()
    {
        if (LobbyEventsInstalled)
        {
            return;
        }

        try
        {
            _lobbyInvite ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Friend, Lobby>>(new Action<Friend, Lobby>(OnLobbyInvite));
            _overlayActivated ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(new Action<bool>(OnOverlayActivated));
            if (_lobbyInvite == null || _overlayActivated == null)
            {
                _lobbyInvite = null;
                _overlayActivated = null;
                ModLog.Warn("Steam: the lobby invite and overlay handlers couldn't be made for the game's Facepunch; the browser won't mark invited games, and the lobby won't know when the overlay is up.");
                return;
            }

            Il2CppSteamworks.SteamMatchmaking.add_OnLobbyInvite(_lobbyInvite);
            Il2CppSteamworks.SteamFriends.add_OnGameOverlayActivated(_overlayActivated);
            ModLog.Info("Steam: lobby invites (the browser marks that game Invited) and the overlay opening and closing (the lobby leaves the pad alone meanwhile) are heard.");
        }
        catch (Exception ex)
        {
            _lobbyInvite = null;
            _overlayActivated = null;
            ModLog.Warn($"Steam: the lobby invite and overlay events can't be heard ({ModLog.SafeErrorText(ex)}); joins from Steam still work, but the browser won't mark invited games and the lobby won't know when the overlay is up.");
        }
    }

    /// <summary>
    /// The lobby on the command line Steam started the game with for an invite or "Join Game" (<c>+connect_lobby &lt;id&gt;</c>),
    /// or 0: the mod's runtime's view of the process's command line, and the game's own runtime's as a second opinion
    /// (<c>System.Environment.GetCommandLineArgs</c> in the game). A switch without a lobby is logged; never throws.
    /// </summary>
    public static ulong FromCommandLine()
    {
        string[] arguments = Arguments("the mod's", Environment.GetCommandLineArgs);
        if (SteamJoinRequests.TryFindInCommandLine(arguments, out ulong lobby))
        {
            return lobby;
        }

        string[] game = Arguments("the game's", GameArguments);
        if (SteamJoinRequests.TryFindInCommandLine(game, out lobby))
        {
            ModLog.Info($"Steam: the game's command line names lobby {ModLog.Scrub.Tags.Lobby(lobby)} (the mod's runtime didn't see it).");
            return lobby;
        }

        foreach (string argument in arguments.Concat(game))
        {
            if (argument != null && argument.StartsWith(SteamJoinRequests.ConnectLobby, StringComparison.Ordinal))
            {
                ModLog.Warn($"Steam: the command line has {SteamJoinRequests.ConnectLobby}, but not with a lobby's ID after it; not joining anything.");
                break;
            }
        }

        return 0;
    }

    /// <summary>One view of the command line, or none if it couldn't be read (logged).</summary>
    private static string[] Arguments(string whose, Func<string[]> read)
    {
        try
        {
            return read() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Steam: {whose} command line couldn't be read ({ModLog.SafeErrorText(ex)}).");
            return Array.Empty<string>();
        }
    }

    /// <summary>The command line as the game's own runtime has it.</summary>
    private static string[] GameArguments()
    {
        Il2CppStringArray? arguments = Il2CppSystem.Environment.GetCommandLineArgs();
        if (arguments == null)
        {
            return Array.Empty<string>();
        }

        int count = Math.Min(arguments.Length, SteamJoinRequests.MaxArguments);
        var copy = new string[count];
        for (int i = 0; i < count; i++)
        {
            copy[i] = arguments[i] ?? string.Empty;
        }

        return copy;
    }

    /// <summary>Facepunch's <c>OnGameLobbyJoinRequested</c>: an invite accepted, or "Join Game" on a friend in a lobby.</summary>
    private static void OnLobbyJoinRequested(Lobby lobby, SteamId friend)
    {
        try
        {
            Offer(lobby.Id.Value, friend.Value, SteamJoinSource.Invite, string.Empty);
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    /// <summary>Facepunch's <c>OnGameRichPresenceJoinRequested</c>: "Join Game" on a friend whose rich presence has a connect string.</summary>
    private static void OnRichPresenceJoinRequested(Friend friend, string connect)
    {
        try
        {
            if (!SteamJoinRequests.TryParseConnect(connect, out ulong lobby))
            {
                // Another game's or another version's connect string: nothing this mod can join.
                if (_refusals++ < RefusalsLogged)
                {
                    ModLog.Warn($"Steam: \"Join Game\" on {ModLog.Scrub.Tags.Player(friend.Id.Value)} handed over a connect string of {connect?.Length ?? 0} characters, which isn't a T2T Online lobby; nothing joined.");
                }

                return;
            }

            Offer(lobby, friend.Id.Value, SteamJoinSource.JoinGame, connect);
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    /// <summary>Facepunch's <c>OnLobbyInvite</c>: Steam delivered an invite to a lobby (not yet accepted).</summary>
    private static void OnLobbyInvite(Friend friend, Lobby lobby)
    {
        try
        {
            ulong from = friend.Id.Value;
            ulong id = lobby.Id.Value;
            if (!SteamInviteBook.Shared.Add(id, from, _now?.Invoke() ?? TimeSpan.Zero))
            {
                if (_refusals++ < RefusalsLogged)
                {
                    ModLog.Warn($"Steam: an invite from {ModLog.Scrub.Tags.Player(from)} names {ModLog.Scrub.Tags.Lobby(id)}, which isn't a Steam lobby; ignored.");
                }

                return;
            }

            if (_invitesHeard++ < InvitesLogged)
            {
                ModLog.Info($"Steam: {ModLog.Scrub.Tags.Player(from)} invited you to lobby {ModLog.Scrub.Tags.Lobby(id)}; the friends browser marks that game Invited (accepting in Steam joins it, as before).");
            }
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    /// <summary>Facepunch's <c>OnGameOverlayActivated</c>: Steam's overlay opened (true) or closed (false) over the game.</summary>
    private static void OnOverlayActivated(bool active)
    {
        try
        {
            SteamOverlayWatch.Shared.Set(active);
            if (_overlayHeard++ < OverlayLogged)
            {
                ModLog.Info($"Steam: the overlay {(active ? "opened" : "closed")} over the game.");
            }
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private static void Offer(ulong lobby, ulong friend, SteamJoinSource source, string connect)
    {
        if (_inbox == null || _now == null)
        {
            return;
        }

        if (!_inbox.Offer(new SteamJoinRequest(lobby, friend, source, _now()), out SteamJoinRequest replaced))
        {
            if (_refusals++ < RefusalsLogged)
            {
                ModLog.Warn($"Steam: a request to join {ModLog.Scrub.Tags.Lobby(lobby)} ({source}{(connect.Length > 0 ? $", a connect string of {connect.Length} characters" : string.Empty)}) isn't for a Steam lobby; nothing joined.");
            }

            return;
        }

        if (replaced.LobbyId != 0 && replaced.LobbyId != lobby)
        {
            ModLog.Info($"Steam: a newer request to join (lobby {ModLog.Scrub.Tags.Lobby(lobby)}) replaces the one for lobby {ModLog.Scrub.Tags.Lobby(replaced.LobbyId)}, which hadn't been acted on yet.");
        }
    }

    /// <summary>A handler failed: logged (three times, then counted), never passed back into the game's Steam update.</summary>
    private static void Failed(Exception ex)
    {
        if (_failures++ < RefusalsLogged)
        {
            ModLog.Error("Steam: handling a request to join from Steam failed; nothing joined (Join Lobby still works)", ex);
        }
    }
}
#endif
