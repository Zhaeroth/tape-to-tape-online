using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Lobby;

/// <summary>Why a session can't start or go on: every reason has words, a button and a place to land (<see cref="JoinFailures"/>).</summary>
public enum LobbyFailure
{
    /// <summary>No seat: Steam's or the host's answer.</summary>
    Full,

    /// <summary>The lobby's gone: Steam's answer.</summary>
    Closed,

    /// <summary>Steam's error, or no answer in time.</summary>
    Unreachable,

    /// <summary>Steam refused, or this account is banned from the lobby.</summary>
    NotAllowed,

    /// <summary>Someone in the lobby blocked someone.</summary>
    Blocked,

    /// <summary>A limited or banned account.</summary>
    Limited,

    /// <summary>Too many tries.</summary>
    RateLimited,

    /// <summary>Another T2T Online protocol.</summary>
    ModVersion,

    /// <summary>Another game update.</summary>
    GameBuild,

    /// <summary>The host isn't our Steam friend, or we aren't theirs.</summary>
    NotAFriend,

    /// <summary>The host has online off.</summary>
    HostOff,

    /// <summary>The host couldn't make a controller for us.</summary>
    NoController,

    /// <summary>The host doesn't know our kind of controller.</summary>
    UnsupportedInput,

    /// <summary>Not in the Steam lobby, no reason given, or one this version doesn't know.</summary>
    Unknown,

    /// <summary>The host removed us.</summary>
    Removed,

    /// <summary>The host closed the lobby.</summary>
    HostClosed,

    /// <summary>The host is gone, and the rejoin window is over.</summary>
    HostLost,

    /// <summary>The mod doesn't know this game update.</summary>
    OnlineOff,

    /// <summary>Online switched off after an error.</summary>
    OnlineBroken,

    /// <summary>Steam isn't connected.</summary>
    SteamOff,

    /// <summary>Steam hasn't started yet.</summary>
    SteamWaiting,

    /// <summary>A restored backup waits for a restart.</summary>
    RestoredBackup,

    /// <summary>The native screen couldn't open. A toast: the title stays usable, and the line names where a problem report is made (Settings > Online).</summary>
    ScreenFailed,

    /// <summary>Steam made no lobby for the host.</summary>
    CreateFailed,

    /// <summary>Not a lobby's failure: a notice that needs a press (<see cref="NoticeKind.Blocking"/>), shown on the same one-button prompt. The log says which notice.</summary>
    Notice,

    /// <summary>This PC's Steam couldn't check its friends list, so the host's game isn't joined (D134).</summary>
    FriendsUnchecked,

    /// <summary>The host's Steam couldn't check its friends list, so it refused us (D134).</summary>
    HostFriendsUnchecked,

    /// <summary>Steam wouldn't keep connections relayed, so online is off this session (D133).</summary>
    RelayOnlyRefused,

    /// <summary>The link to the host went direct although relay only was set, so we left (D133).</summary>
    DirectLink,
}

/// <summary>The buttons a failure offers. The first is focused; B is the last.</summary>
public enum FailureButtons
{
    /// <summary>One button: <see cref="LobbyTerms.Ok"/>.</summary>
    Ok,

    /// <summary><see cref="LobbyWords.TryAgain"/> focused, then <see cref="LobbyTerms.Back"/>.</summary>
    TryAgainOrBack,

    /// <summary>A toast: no buttons, because nothing was lost.</summary>
    None,
}

/// <summary>Where the player lands after a failure's button. Focus always goes to a real control.</summary>
public enum FailureLanding
{
    /// <summary>Stay where they are (a toast, or the session goes on).</summary>
    Stay,

    /// <summary>The Join list, on the row they tried (<see cref="BrowserList"/> keeps it).</summary>
    JoinListSameRow,

    /// <summary>The Join list, the row gone: the nearest one has focus.</summary>
    JoinListNearestRow,

    /// <summary>The Online group, on Host Lobby.</summary>
    OnlineGroup,

    /// <summary>The title, on the Online row.</summary>
    Title,
}

/// <summary>A failure as the player sees it.</summary>
/// <param name="Reason">Which failure; goes in the log (<c>Lobby: join failed: ...</c>), never on the screen.</param>
/// <param name="Line">What happened and what to do.</param>
/// <param name="Buttons">Its buttons.</param>
/// <param name="Landing">Where OK or Back goes. Try again tries again.</param>
public sealed record FailureMessage(LobbyFailure Reason, LobbyLine Line, FailureButtons Buttons, FailureLanding Landing)
{
    /// <summary>True for a toast: nothing is lost, so nothing asks for a press.</summary>
    public bool IsToast => Buttons == FailureButtons.None;
}

/// <summary>
/// One rule for every failure: what happened, what to do, one focused button, in the player's language, no codes, and nothing
/// that times out (XAG 115, NN/g). The plugin knows why; this knows what to say. The words keep
/// what was good in the session's old sentences: naming the host, and "both need the same one".
/// </summary>
public static class JoinFailures
{
    /// <summary>A failure from Steam's <c>EChatRoomEnterResponse</c>, the outcome of a join. A number it doesn't know is <see cref="LobbyFailure.Unknown"/>.</summary>
    public static LobbyFailure FromSteamEnter(int response) => response switch
    {
        2 => LobbyFailure.Closed, // DoesntExist
        3 or 6 => LobbyFailure.NotAllowed, // NotAllowed, Banned
        4 => LobbyFailure.Full,
        5 => LobbyFailure.Unreachable, // Error
        7 or 9 => LobbyFailure.Limited, // Limited, CommunityBan
        10 or 11 => LobbyFailure.Blocked, // MemberBlockedYou, YouBlockedMember
        15 => LobbyFailure.RateLimited,
        _ => LobbyFailure.Unknown, // ClanDisabled, or a number Steam adds later
    };

    /// <summary>A failure from the host's refusal. A reason it doesn't know is <see cref="LobbyFailure.Unknown"/>.</summary>
    public static LobbyFailure FromReject(RejectReason reason) => reason switch
    {
        RejectReason.ProtocolMismatch => LobbyFailure.ModVersion,
        RejectReason.GameBuildMismatch => LobbyFailure.GameBuild,
        RejectReason.SessionFull => LobbyFailure.Full,
        RejectReason.NoControllerSlot => LobbyFailure.NoController,
        RejectReason.UnsupportedInput => LobbyFailure.UnsupportedInput,
        RejectReason.HostUnavailable => LobbyFailure.HostOff,
        RejectReason.NotAFriend => LobbyFailure.NotAFriend,
        RejectReason.Removed => LobbyFailure.Removed,
        RejectReason.FriendshipUnconfirmed => LobbyFailure.HostFriendsUnchecked,
        _ => LobbyFailure.Unknown, // NotInLobby, None, a later version's
    };

    /// <summary>A failure from how a friend's session ended. <paramref name="everJoined"/>: it had joined at some point, so a silence means the host is lost, not unreachable.</summary>
    public static LobbyFailure FromLeave(LeaveReason reason, bool everJoined) => reason switch
    {
        LeaveReason.HostClosed => LobbyFailure.HostClosed,
        LeaveReason.Removed => LobbyFailure.Removed,
        LeaveReason.TimedOut or LeaveReason.LeftLobby or LeaveReason.None => everJoined ? LobbyFailure.HostLost : LobbyFailure.Unreachable,
        LeaveReason.Quit => LobbyFailure.HostClosed,
        _ => everJoined ? LobbyFailure.HostLost : LobbyFailure.Unknown,
    };

    /// <summary>A failure's words, button and landing.</summary>
    /// <param name="failure">The failure.</param>
    /// <param name="hostName">The host's name, cleaned (<see cref="DisplayName"/>): most sentences name the person.</param>
    /// <param name="ourVersion">Our T2T Online version, when it's known: said on a version mismatch.</param>
    /// <param name="theirVersion">Theirs, when it's known (it's in their lobby data).</param>
    public static FailureMessage Message(LobbyFailure failure, string hostName = "", string ourVersion = "", string theirVersion = "")
    {
        string host = hostName ?? string.Empty;
        return failure switch
        {
            LobbyFailure.Full => Ok(failure, LobbyWords.FailFull, FailureLanding.JoinListSameRow, host),
            LobbyFailure.Closed => Ok(failure, LobbyWords.FailClosed, FailureLanding.JoinListNearestRow, host),
            LobbyFailure.Unreachable => Retry(failure, LobbyWords.FailReach, FailureLanding.JoinListSameRow, host),
            LobbyFailure.NotAllowed => Ok(failure, LobbyWords.FailNotAllowed, FailureLanding.JoinListSameRow, host),
            LobbyFailure.Blocked => Ok(failure, LobbyWords.FailBlocked, FailureLanding.JoinListSameRow),
            LobbyFailure.Limited => Ok(failure, LobbyWords.FailLimited, FailureLanding.JoinListSameRow),
            LobbyFailure.RateLimited => Ok(failure, LobbyWords.FailRateLimit, FailureLanding.JoinListSameRow),
            LobbyFailure.ModVersion when ourVersion?.Length > 0 && theirVersion?.Length > 0 => Ok(failure, LobbyWords.FailModVersion, FailureLanding.JoinListSameRow, host, ourVersion, theirVersion),
            LobbyFailure.ModVersion => Ok(failure, LobbyWords.FailModVersionPlain, FailureLanding.JoinListSameRow, host),
            LobbyFailure.GameBuild => Ok(failure, LobbyWords.FailGameBuild, FailureLanding.JoinListSameRow, host),
            LobbyFailure.NotAFriend => Ok(failure, LobbyWords.FailNotFriend, FailureLanding.JoinListSameRow, host),
            LobbyFailure.HostOff => Ok(failure, LobbyWords.FailHostOff, FailureLanding.JoinListSameRow, host),
            LobbyFailure.NoController => Ok(failure, LobbyWords.FailNoController, FailureLanding.JoinListSameRow, host),
            LobbyFailure.UnsupportedInput => Ok(failure, LobbyWords.FailInput, FailureLanding.JoinListSameRow, host),
            LobbyFailure.Removed => Ok(failure, LobbyWords.FailKicked, FailureLanding.JoinListNearestRow, host),
            LobbyFailure.HostClosed => Ok(failure, LobbyWords.FailHostClosed, FailureLanding.JoinListNearestRow, host),
            LobbyFailure.HostLost => Ok(failure, LobbyWords.FailHostLost, FailureLanding.JoinListNearestRow, host),
            LobbyFailure.OnlineOff => Ok(failure, LobbyWords.FailOnlineOff, FailureLanding.Title),
            LobbyFailure.OnlineBroken => Ok(failure, LobbyWords.FailOnlineBroken, FailureLanding.Title),
            LobbyFailure.SteamOff => Retry(failure, LobbyWords.FailSteam, FailureLanding.Title),
            LobbyFailure.SteamWaiting => Retry(failure, LobbyWords.FailSteamWaiting, FailureLanding.Title),
            LobbyFailure.RestoredBackup => Ok(failure, LobbyWords.FailRestored, FailureLanding.Title),
            LobbyFailure.ScreenFailed => new FailureMessage(failure, new LobbyLine(LobbyWords.FailShell), FailureButtons.None, FailureLanding.Stay),
            LobbyFailure.CreateFailed => Retry(failure, LobbyWords.FailCreate, FailureLanding.OnlineGroup),
            LobbyFailure.Notice => Ok(failure, LobbyWords.FailUnknown, FailureLanding.Stay, host), // a notice brings its own line (Notices.Prompt); this is only for one asked for without it
            // A broken Steam, not a passing hiccup: OK, not Try again (a try would get the same answer until a restart).
            LobbyFailure.FriendsUnchecked => Ok(failure, LobbyWords.FailFriendsUnchecked, FailureLanding.JoinListSameRow, host),
            LobbyFailure.HostFriendsUnchecked => Ok(failure, LobbyWords.FailHostFriendsUnchecked, FailureLanding.JoinListSameRow, host),
            LobbyFailure.RelayOnlyRefused => Ok(failure, LobbyWords.FailRelayOnly, FailureLanding.Title),
            LobbyFailure.DirectLink => Ok(failure, LobbyWords.FailDirectLink, FailureLanding.JoinListNearestRow, host),
            _ => Retry(LobbyFailure.Unknown, LobbyWords.FailUnknown, FailureLanding.JoinListSameRow, host),
        };
    }

    /// <summary>The words for a browser's greyed row, when A explains instead of trying: it's the same failure Steam or the host would give.</summary>
    public static FailureMessage? ForRow(BrowserBlock block, string hostName, string ourVersion = "", string theirVersion = "") => block switch
    {
        BrowserBlock.Full => Message(LobbyFailure.Full, hostName),
        BrowserBlock.DifferentMod => Message(LobbyFailure.ModVersion, hostName, ourVersion, theirVersion),
        BrowserBlock.DifferentBuild => Message(LobbyFailure.GameBuild, hostName),
        _ => null,
    };

    /// <summary>The host's toast for a friend it couldn't seat. The seat clears, and the log has the rest.</summary>
    public static LobbyLine HostToast(RejectReason reason, string friendName)
    {
        string name = friendName ?? string.Empty;
        LobbyWord word = reason switch
        {
            RejectReason.ProtocolMismatch => LobbyWords.RefusedMod,
            RejectReason.GameBuildMismatch => LobbyWords.RefusedBuild,
            RejectReason.SessionFull => LobbyWords.RefusedFull,
            RejectReason.NotInLobby => LobbyWords.RefusedNotInLobby,
            RejectReason.NoControllerSlot => LobbyWords.RefusedController,
            RejectReason.UnsupportedInput => LobbyWords.RefusedInput,
            RejectReason.HostUnavailable => LobbyWords.RefusedHostOff,
            RejectReason.NotAFriend => LobbyWords.RefusedNotFriend,
            RejectReason.Removed => LobbyWords.RefusedRemoved,
            RejectReason.FriendshipUnconfirmed => LobbyWords.RefusedFriendsUnchecked,
            _ => LobbyWords.RefusedOther,
        };
        return new LobbyLine(word, name);
    }

    private static FailureMessage Ok(LobbyFailure failure, LobbyWord word, FailureLanding landing, params string[] args) =>
        new(failure, new LobbyLine(word, args), FailureButtons.Ok, landing);

    private static FailureMessage Retry(LobbyFailure failure, LobbyWord word, FailureLanding landing, params string[] args) =>
        new(failure, new LobbyLine(word, args), FailureButtons.TryAgainOrBack, landing);
}
