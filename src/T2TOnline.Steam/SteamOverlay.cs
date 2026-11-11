using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>Whether a call through Steam's in-game overlay went out.</summary>
public enum SteamOverlayResult
{
    /// <summary>Steam was asked to open it.</summary>
    Opened,

    /// <summary>Steam's overlay isn't up for this game (switched off in Steam's settings, or not hooked into the game yet).</summary>
    OverlayOff,

    /// <summary>Steam isn't available, or this steam_api64 lacks the call (an optional export).</summary>
    Unavailable,
}

/// <summary>
/// Steam's in-game overlay (<c>ISteamUtils</c>, <c>ISteamFriends</c>): whether it's up, its invite window for a lobby, and a
/// player's profile. None of these throw. Each call that opens something checks <see cref="IsEnabled"/> itself, at the
/// press: Steam says the overlay is off for its first seconds while it loads, so a value read at start-up means nothing.
/// </summary>
public static class SteamOverlay
{
    /// <summary>
    /// True when Steam's overlay is up for this game, false when it isn't, null when Steam can't say (not available, or
    /// steam_api64 lacks <c>SteamAPI_ISteamUtils_IsOverlayEnabled</c>, an optional export).
    /// </summary>
    public static bool? IsEnabled
    {
        get
        {
            nint utils = SteamInterfaces.Utils(nameof(SteamNative.IsOverlayEnabled));
            if (utils == 0)
            {
                return null;
            }

            try
            {
                return SteamNative.IsOverlayEnabled(utils);
            }
            catch (Exception e)
            {
                SteamRuntime.ReportFailure(e);
                return null;
            }
        }
    }

    /// <summary>
    /// Whether the overlay is open right now, as Steam's overlay events have said so far (<see cref="SteamOverlayWatch.Shared"/>:
    /// only the game's Facepunch events change it, which the mod's plugin hears). False until one has been heard, so a
    /// reading before the first event means "not known to be open"; and false again once an overlay has been "open" for longer
    /// than <see cref="SteamOverlayWatch.MaxOpen"/> (a close that was never heard).
    /// </summary>
    public static bool IsOpen => SteamOverlayWatch.Shared.IsOpen;

    /// <summary>
    /// Opens Steam's invite window for <paramref name="lobbyId"/>: the player picks friends there, and each gets a Steam
    /// invite to the lobby. Not when the overlay is known to be off (it would open nothing): says which.
    /// </summary>
    public static SteamOverlayResult OpenInviteDialog(ulong lobbyId)
    {
        nint friends = SteamInterfaces.Friends(nameof(SteamNative.ActivateGameOverlayInviteDialog));
        if (friends == 0 || lobbyId == 0)
        {
            return SteamOverlayResult.Unavailable;
        }

        if (IsEnabled == false)
        {
            return SteamOverlayResult.OverlayOff;
        }

        try
        {
            SteamNative.ActivateGameOverlayInviteDialog(friends, lobbyId);
            return SteamOverlayResult.Opened;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return SteamOverlayResult.Unavailable;
        }
    }

    /// <summary>
    /// Opens a player's Steam profile in the overlay (<c>ActivateGameOverlayToUser("steamid", id)</c>). Not when the overlay
    /// is known to be off (it would open nothing): says which. Works for any Steam ID, a friend's or not.
    /// </summary>
    public static SteamOverlayResult OpenProfile(ulong steamId)
    {
        nint friends = SteamInterfaces.Friends(nameof(SteamNative.ActivateGameOverlayToUser));
        byte[]? dialog = NativeStrings.ToUtf8("steamid");
        if (friends == 0 || steamId == 0 || dialog is null)
        {
            return SteamOverlayResult.Unavailable;
        }

        if (IsEnabled == false)
        {
            return SteamOverlayResult.OverlayOff;
        }

        try
        {
            SteamNative.ActivateGameOverlayToUser(friends, dialog, steamId);
            return SteamOverlayResult.Opened;
        }
        catch (Exception e)
        {
            SteamRuntime.ReportFailure(e);
            return SteamOverlayResult.Unavailable;
        }
    }
}
