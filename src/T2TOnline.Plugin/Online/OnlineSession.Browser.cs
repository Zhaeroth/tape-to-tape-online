#if HAS_INTEROP
using System;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Online;

// The friends' games a browser lists to join (the native lobby's Join Lobby screen, and the developer's F1 panel's Online tab), the
// lobby data a host publishes for them, and the Steam calls on other players (invite, profile). The data is read by
// polling (T2TOnline.Steam's SteamLobbyBrowser: no callbacks, D20) only while a browser is open, and made into
// JoinableGame rows here, where this PC's version is known. What the host publishes comes from Advert, which the session
// sets as the lobby changes.
internal sealed partial class OnlineSession
{
    private static readonly TimeSpan InvitePruneInterval = TimeSpan.FromSeconds(1);

    private readonly SteamLobbyBrowser _browser = new(ModLog.SteamInfo, ModLog.SteamWarn);
    private readonly SteamLobbyAdvertiser _advertiser = new(ModLog.SteamInfo, ModLog.SteamWarn);
    private LobbyAdvert _advert = LobbyAdvert.Default;
    private bool _browserOpen;
    private int _browserVersion = -1;
    private int _invitesVersion = -1;
    private TimeSpan _nextInvitePrune;

    /// <summary>What a lobby must match to be joined from here, for the native browser's rows.</summary>
    public T2TOnline.Core.Lobby.BrowserIdentity BrowserIdentity() => new(ProtocolInfo.Version, _buildHex);

    /// <summary>
    /// What a host tells friends about its lobby before they join (state, mode, save), published as lobby data. The session
    /// sets it as the lobby changes; it starts as in the lobby, Campaign, no save. Only a host's lobby is changed.
    /// </summary>
    public LobbyAdvert Advert
    {
        get => _advert;
        set
        {
            _advert = value ?? LobbyAdvert.Default;
            PublishListing(_clock.Elapsed);
        }
    }

    /// <summary>
    /// A browser opens: friends' games are looked for at once and kept up to date (the friends list every 2 s, each game's
    /// details every 5 s) until <see cref="CloseBrowser"/>, and <see cref="JoinableGames"/> follows them. Not while in a session.
    /// </summary>
    public void OpenBrowser()
    {
        if (!_browserOpen)
        {
            _browserOpen = true;
            ModLog.Info("Online (browser): opened; friends' games are looked for now and kept up to date while it is open.");
        }
    }

    /// <summary>The browser closes: the work stops, and the last list stays for a join to look a game up in.</summary>
    public void CloseBrowser()
    {
        if (_browserOpen)
        {
            _browserOpen = false;
            ModLog.Info($"Online (browser): closed with {_games.Count} friend game(s) listed.");
        }
    }

    /// <summary>
    /// Every frame: each Steam invite that arrived is said once, oldest first ("Alex invited you."), wherever the player is:
    /// Steam's own popup needs its overlay. The invited game's row says so too, for as long as the invite lives
    /// (<see cref="JoinableGame.Invited"/>). The invite's sender is Steam's word, and only their name is shown, cleaned.
    /// </summary>
    private void SayNewInvites(TimeSpan elapsed)
    {
        while (SteamInviteBook.Shared.TryTakeUnannounced(elapsed, out SteamInvite invite))
        {
            NotifyAbout(invite.FromSteamId, NoticeId.InviteReceived, DisplayNameOf(invite.FromSteamId));
        }
    }

    /// <summary>
    /// Host: sends a Steam invite for this lobby to <paramref name="friend"/> (the native invite list). True means the invite
    /// went out, nothing more: Steam doesn't say whether it arrived, so a row should say "Invited", never "Delivered".
    /// False when it didn't (not hosting, or Steam refused: the log says which).
    /// </summary>
    public bool InviteFriend(ulong friend)
    {
        if (TourRefusesInvite("an invite to a friend was asked for"))
        {
            return false; // a check tour never invites anyone (OnlineSession.TourHolds.cs)
        }

        ulong lobby = _link.Id;
        if (_host == null || lobby == 0)
        {
            ModLog.Warn($"Online (invite): no invite sent to {Tag(friend)}: not hosting a lobby.");
            return false;
        }

        bool sent = SteamLobbies.InviteUser(lobby, friend);
        if (sent)
        {
            ModLog.Info($"Online (invite): invite to lobby {Tags.Lobby(lobby)} sent to {Tag(friend)} (Steam doesn't say whether it arrived).");
        }
        else
        {
            ModLog.Warn($"Online (invite): Steam did not send the invite to {Tag(friend)} (we may not be in the lobby, Steam may be offline, or the user isn't valid): {SteamRuntime.Status}");
        }

        return sent;
    }

    /// <summary>
    /// Opens a player's Steam profile in the overlay. The overlay is checked now, at the press: Steam says it is off while it
    /// loads. <see cref="SteamOverlayResult.OverlayOff"/> means the profile didn't open (hide the prompt, or say so).
    /// </summary>
    public static SteamOverlayResult OpenProfile(ulong steamId)
    {
        SteamOverlayResult result = SteamOverlay.OpenProfile(steamId);
        if (result == SteamOverlayResult.Opened)
        {
            ModLog.Info($"Online (profile): Steam's overlay is opening {Tag(steamId)}'s profile.");
        }
        else
        {
            ModLog.Warn($"Online (profile): {Tag(steamId)}'s profile did not open: {(result == SteamOverlayResult.OverlayOff ? "Steam's overlay is off, or still loading" : $"Steam can't open it ({SteamRuntime.Status})")}.");
        }

        return result;
    }

    /// <summary>
    /// Looks now, and keeps looking for <see cref="SteamLobbyBrowser.Linger"/> (the list fills in as Steam answers): the native Join list's
    /// refresh (its own polling is <see cref="OpenBrowser"/>'s) and the developer's F1 panel's Find games. The log line says which.
    /// </summary>
    private void FindGamesCore()
    {
        if (_self == 0)
        {
            return;
        }

        _browser.Refresh(_clock.Elapsed);
        ModLog.Info($"Online ({(_browserOpen ? "browser" : "panel")}): looking for friends' games now (and for {SteamLobbyBrowser.Linger.TotalSeconds:0} s more).");
    }

    /// <summary>Every frame: the browser's polling, the list made from it when it changed, and a host's lobby data.</summary>
    private void PollGames(TimeSpan elapsed)
    {
        bool open = _browserOpen && !InLobby && T2TOnlinePlugin.OnlineAvailable;
        if (InLobby)
        {
            _browser.Stop(); // a look (F1's Find games) ends once a session starts
        }

        _browser.Poll(elapsed, open);
        if (_browser.Active && elapsed >= _nextInvitePrune)
        {
            _nextInvitePrune = elapsed + InvitePruneInterval;
            SteamInviteBook.Shared.Prune(elapsed);
        }

        if (_browser.Version != _browserVersion || SteamInviteBook.Shared.Version != _invitesVersion)
        {
            _browserVersion = _browser.Version;
            _invitesVersion = SteamInviteBook.Shared.Version;
            _games = _browser.Listings.Select(listing => Describe(listing, elapsed)).ToList();
        }

        PublishListing(elapsed);
    }

    /// <summary>Host: the lobby data friends read, when it changed (cheap otherwise).</summary>
    private void PublishListing(TimeSpan elapsed)
    {
        if (_host != null && _link.Id != 0)
        {
            _advertiser.Publish(_link.Id, _advert, elapsed);
        }
    }

    /// <summary>A friend's game as a row: what the browser knows, with this PC's version judged against the host's.</summary>
    private JoinableGame Describe(SteamLobbyListing listing, TimeSpan now)
    {
        string friends = string.Join(", ", listing.FriendNames.Select(name => SafeText.Clean(name)));
        bool invited = SteamInviteBook.Shared.IsInvited(listing.LobbyId, now);
        switch (listing.Answer)
        {
            case ListingAnswer.Checking:
                return new JoinableGame(listing.LobbyId, friends, string.Empty, listing.FriendsSeen, "checking...", Joinable: true, Checked: false, Invited: invited);
            case ListingAnswer.NoAnswer:
                return new JoinableGame(listing.LobbyId, friends, string.Empty, listing.FriendsSeen, "Steam didn't send its details (it may have closed)", Joinable: true, Checked: true, Invited: invited);
        }

        ListedCompat compat = ListedLobby.CheckCompat(listing.ModVersion, listing.Protocol, listing.Build, ProtocolInfo.Version, _buildHex);
        string theirs = ReleaseVersion.ForScreen(SentText.Release(listing.ModVersion)); // shown, and logged at a join: its shape or its length
        string yours = ReleaseVersion.ForScreen(_identity.ModVersion);
        string note = compat switch
        {
            ListedCompat.Compatible => string.Empty,
            ListedCompat.NotT2TOnline => "not a T2T Online game",
            ListedCompat.TheirsIsOlder => $"an older T2T Online (theirs {theirs}, yours {yours})",
            ListedCompat.YoursIsOlder => $"a newer T2T Online (theirs {theirs}, yours {yours})",
            ListedCompat.OtherGameBuild => "another game update (both need the same one)",
            _ => $"another T2T Online version (theirs {theirs}, yours {yours})",
        };
        string host = listing.HostId != 0 ? SafeText.Clean(listing.HostName) : string.Empty;
        return new JoinableGame(
            listing.LobbyId,
            host.Length > 0 ? host : friends,
            theirs,
            listing.Players is > 0 and <= MaxLobbyMembers ? listing.Players : listing.FriendsSeen,
            note,
            Joinable: compat == ListedCompat.Compatible,
            Checked: true,
            HostId: listing.HostId,
            Mode: listing.Mode,
            State: listing.State,
            Save: listing.Save,
            PingMs: listing.PingMs,
            Compat: compat,
            Invited: invited,
            Answered: true);
    }
}
#endif
