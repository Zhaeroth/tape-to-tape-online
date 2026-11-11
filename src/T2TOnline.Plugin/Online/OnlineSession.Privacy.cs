#if HAS_INTEROP
using System;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

// What the session shares with other players' PCs and whom it plays with. D133: every connection goes through Steam's
// relays (no ICE, so no player learns another's IP address) unless the player allows direct connections; set once at
// start-up, before any session can open, read back and logged. If Steam won't keep relay only, online is off for the
// session (Steam's fallback, the player's own setting, connects friends directly). A friend's link to its host that stays
// direct anyway (DirectLinkWatch) is left through the session's own end path; a host only warns (see IcePolicy.OnRoute). D134: only
// confirmed Steam friends play together; when Steam can't say, the answer is no, on the host and on the friend.
internal sealed partial class OnlineSession
{
    /// <summary>Relay only is in effect (Steam read it back): a direct link afterwards gets one warning (<see cref="NoteRoute"/>).</summary>
    private bool _relayOnly;

    /// <summary>The direct-link warning was logged (once a launch).</summary>
    private bool _directWarned;

    /// <summary>Steam wouldn't keep relay only, so online is off this session: the notice and failure say so instead of "Steam is off".</summary>
    private bool _relayRefused;

    /// <summary>A friend's link to its host was found direct: the session is left on the next frame (<see cref="LeaveDirectLink"/>), not inside the report that found it.</summary>
    private bool _directLinkLeave;

    /// <summary>How long each link has read direct (D133's guard acts only on a link that stays direct).</summary>
    private readonly DirectLinkWatch _directLinks = new();

    /// <summary>What <see cref="Ready"/> says when <c>_self</c> is 0 after start-up.</summary>
    private NoticeId SteamOffNotice => _relayRefused ? NoticeId.RelayOnlyRefused : NoticeId.SteamOff;

    /// <summary>What the Online screens say when <c>_self</c> is 0 after start-up.</summary>
    private LobbyFailure SteamOffFailure => _relayRefused ? LobbyFailure.RelayOnlyRefused : LobbyFailure.SteamOff;

    /// <summary>
    /// D133, at start-up after relay access is requested: ICE off (relay only) or, with <see cref="ModSettings.AllowDirectConnections"/>,
    /// every candidate type; then what Steam reads back, in one line. A global value, so every session opened afterwards takes it.
    /// False when relay only was wanted and Steam won't keep it: online is then off for the session, by the same path as a
    /// Steam that isn't usable (<c>_self</c> 0, no Steam joins), with its own words.
    /// </summary>
    private bool ApplyIce()
    {
        bool allowDirect = ModSettings.AllowDirectConnections;
        bool set = SteamNetworking.SetIce(IcePolicy.Wanted(allowDirect));
        bool read = SteamNetworking.TryGetIce(out int value);
        IceOutcome outcome = IcePolicy.Check(allowDirect, set, read, value);
        _relayOnly = outcome.RelayOnly;
        if (outcome.InEffect)
        {
            ModLog.Info(outcome.Line);
        }
        else
        {
            ModLog.Warn(outcome.Line);
        }

        if (!outcome.TurnsOnlineOff)
        {
            if (outcome.RelayOnly)
            {
                NativeUi.RelayRefusal.Held(); // Settings > Online hides Direct Connections again unless it's on
            }

            return true;
        }

        NativeUi.RelayRefusal.Seen(); // Settings > Online shows Direct Connections: the way to play anyway
        _self = 0;
        _sessions.Loopback = 0;
        _relayRefused = true;
        _steamOff = "Steam wouldn't keep connections relay only, so IP addresses could be shared (see the log)";
        return false;
    }

    /// <summary>D134, at start-up: said once when this Steam can't be asked who is a friend at all (every join and seat would be refused).</summary>
    private static void ReportFriendCheck()
    {
        if (!SteamFriends.CanCheckFriends)
        {
            ModLog.Warn($"Steam: Steam can't say who is a Steam friend ({SteamFriends.FriendCheckProblem(SteamRuntime.LocalSteamId)}): every join and every seat is refused, " +
                "as only confirmed friends play together. If the game's files were changed, Steam's \"Verify integrity of game files\" restores them.");
        }
    }

    /// <summary>
    /// D134: Steam's friends list on <paramref name="steamId"/>, with why it couldn't answer in <paramref name="problem"/>
    /// (empty when it did). The callers refuse anything but <see cref="FriendCheckResult.Friend"/>.
    /// </summary>
    private static FriendCheckResult CheckFriend(ulong steamId, out string problem)
    {
        bool answered = SteamFriends.TryIsFriend(steamId, out bool isFriend);
        problem = answered ? string.Empty : SteamFriends.FriendCheckProblem(steamId);
        return FriendCheck.From(answered, isFriend);
    }

    /// <summary>
    /// A link's route as the 5 s report reads it, while relay only is in effect: once it has read direct on every report for
    /// <see cref="DirectLinkWatch.ConfirmAfter"/> (a single reading may be a route Steam lost for a moment), a friend's link
    /// to its host is left on the next frame and a host's link to a friend is warned about once a launch.
    /// </summary>
    private void NoteRoute(ulong peer, bool connected, bool relayed)
    {
        bool toHost = _host == null && _friend != null && !_solo && peer == _friend.Host;
        switch (_directLinks.Observe(peer, _relayOnly, connected, relayed, toHost, _clock.Elapsed, out TimeSpan directFor))
        {
            case DirectLinkAction.Leave when !_directLinkLeave:
                _directLinkLeave = true;
                ModLog.Warn($"ICE: the link to {Tag(peer)} has been direct for {directFor.TotalSeconds:0} s although relay only is set; Steam didn't keep the setting, " +
                    "so your IP addresses are shared with each other. Leaving their game.");
                break;
            case DirectLinkAction.Warn when !_directWarned:
                _directWarned = true;
                ModLog.Warn($"ICE: the link to {Tag(peer)} has been direct for {directFor.TotalSeconds:0} s although relay only is set; Steam didn't keep the setting, " +
                    "so your IP addresses are shared with each other. Their game leaves when it sees the same (with relay only on); this one keeps them, as it has no true reason to give them.");
                break;
        }
    }

    /// <summary>
    /// A friend whose link to its host went direct leaves the host's game: the session's own end path (the goodbye, so the
    /// host keeps their seat as for any leave; the pad and the campaign given back), with the failure prompt. True when it left.
    /// </summary>
    private bool LeaveDirectLink()
    {
        if (!_directLinkLeave)
        {
            return false;
        }

        _directLinkLeave = false;
        if (_friend == null || _solo)
        {
            return false; // the session ended meanwhile
        }

        _failure = LobbyFailure.DirectLink;
        ModLog.Info(LobbyMeter.JoinFailed(_failure.Value, RowAgeSeconds()));
        LeaveAll("the link to the host went direct");
        return true;
    }
}
#endif
