using System;
using System.Collections.Generic;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>What a lobby create or join came to this frame (<see cref="SteamLobbyLink.Poll"/>).</summary>
public enum SteamLobbyCall
{
    /// <summary>Nothing finished.</summary>
    None,

    /// <summary>Our lobby was made: we host it.</summary>
    Created,

    /// <summary>We're in a friend's lobby.</summary>
    Entered,

    /// <summary>The create or join failed, or Steam didn't answer in time: <see cref="SteamLobbyLink.Problem"/> says so.</summary>
    Failed,
}

/// <summary>What getting back into the lobby came to this frame (<see cref="SteamLobbyLink.PollRejoin"/>).</summary>
public enum SteamLobbyRejoin
{
    /// <summary>Nothing finished.</summary>
    None,

    /// <summary>
    /// Back in: read who's there (the owner may have changed meanwhile). <see cref="SteamLobbyLink.Id"/> is a new lobby
    /// when <see cref="SteamLobbyLink.Retarget"/> pointed us at the host's new one.
    /// </summary>
    Back,

    /// <summary>Hosting: the old lobby was gone, so a new one was made. <see cref="SteamLobbyLink.Id"/> is the new lobby: set its data.</summary>
    Replaced,

    /// <summary>Not hosting: Steam says the lobby is gone. If the host made a new one, <see cref="SteamLobbyLink.Retarget"/> points us at it.</summary>
    Gone,
}

/// <summary>
/// The session's Steam lobby (D20: polled, no callbacks): creating one to host or joining a friend's, giving up on a call
/// Steam doesn't answer, who's in it, getting back in after falling out of it, and leaving. A call given up on, or still
/// running when the lobby is left, keeps being polled, and the lobby it makes or enters late is left at once (unless it's
/// the lobby we're in, or on the way into, by then), so no stray lobby of ours shows up in friends' search.
/// <para>
/// Out of the lobby (our own connection to Steam dropped), it asks to be let back in every <see cref="RejoinInterval"/>,
/// one request at a time, each given up after <see cref="CallTimeout"/>. A lobby Steam says is gone is made again when
/// hosting (friends in the session carry on over their own Steam sessions; new friends find the new lobby), and a friend
/// can be pointed at the host's new lobby (<see cref="Retarget"/>).
/// </para>
/// Game-independent; logs through the callbacks it's given. Main thread only. Never throws.
/// </summary>
public sealed class SteamLobbyLink
{
    /// <summary>How long a create, join or rejoin may go unanswered before it's given up on (and still polled).</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Time between requests to be let back in (and between tries at making a new lobby to host).</summary>
    public static readonly TimeSpan RejoinInterval = TimeSpan.FromSeconds(5);

    private readonly ISteamLobbyApi _api;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly List<SteamCallResult<LobbyCreated>> _abandonedCreates = new();
    private readonly List<SteamCallResult<LobbyEnter>> _abandonedJoins = new();
    private SteamCallResult<LobbyCreated>? _creating;
    private SteamCallResult<LobbyEnter>? _joining;
    private ulong _joiningLobby;
    private SteamCallResult<LobbyEnter>? _rejoin;
    private ulong _rejoinLobby;
    private SteamCallResult<LobbyCreated>? _replacing;
    private TimeSpan _callSince;
    private TimeSpan _rejoinSince;
    private TimeSpan _replaceSince;
    private TimeSpan _nextRejoin = TimeSpan.MinValue;
    private int _rejoinTries;
    private int _maxMembers = 1;
    private SteamLobbyType _type = SteamLobbyType.FriendsOnly;
    private ulong _target;
    private bool _gone;

    /// <summary>Creates the link; <paramref name="info"/> and <paramref name="warn"/> get its log lines.</summary>
    public SteamLobbyLink(Action<string> info, Action<string> warn)
        : this(NativeLobbyApi.Instance, info, warn)
    {
    }

    internal SteamLobbyLink(ISteamLobbyApi api, Action<string> info, Action<string> warn)
    {
        _api = api;
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>The lobby we're in, or 0.</summary>
    public ulong Id { get; private set; }

    /// <summary>Who's in it, as last read (kept while we're out of it ourselves).</summary>
    public ulong[] Members { get; private set; } = Array.Empty<ulong>();

    /// <summary>True while a lobby is being made.</summary>
    public bool Creating => _creating != null;

    /// <summary>True while a friend's lobby is being joined.</summary>
    public bool Joining => _joining != null;

    /// <summary>True in a lobby, or on the way into one.</summary>
    public bool Active => Id != 0 || _creating != null || _joining != null;

    /// <summary>True while we've fallen out of our lobby and are getting back in (or, hosting, making a new one).</summary>
    public bool OutOfLobby => OutSince != TimeSpan.MinValue;

    /// <summary>When we noticed we're out of our lobby, or <see cref="TimeSpan.MinValue"/> while in it.</summary>
    public TimeSpan OutSince { get; private set; } = TimeSpan.MinValue;

    /// <summary>True while a new lobby is being made because the one we hosted is gone.</summary>
    public bool Replacing => _replacing != null;

    /// <summary>Whose lobby is being joined (cleaned Steam name), or empty.</summary>
    public string JoiningName { get; private set; } = string.Empty;

    /// <summary>The lobby being joined, or 0 while no join is on its way.</summary>
    public ulong JoiningLobby => _joining != null ? _joiningLobby : 0;

    /// <summary>Why the last create or join failed, one sentence for the status box.</summary>
    public string Problem { get; private set; } = string.Empty;

    /// <summary><see cref="Problem"/> for the log: the same sentence, saying "the host's game" where it names whose (D127).</summary>
    public string ProblemForLog { get; private set; } = string.Empty;

    /// <summary>Steam's answer to the last join (<c>EChatRoomEnterResponse</c>) when it gave one and it wasn't success, else null: <see cref="Problem"/> as a number for the lobby's words.</summary>
    public int? LastEnterResponse { get; private set; }

    /// <summary>How many polls the last create or join took.</summary>
    public int Polls { get; private set; }

    /// <summary>How call results are fetched (switched to manual dispatch after a failed create).</summary>
    public SteamCallResultPath Path { get; private set; } = SteamCallResultPath.SteamUtils;

    /// <summary>
    /// True when the lobby hosted is private (<see cref="SteamLobbyType.Private"/>: joined by invite only, shown to no
    /// friend), as asked at <see cref="Create"/>; a lobby made to replace a gone one is the same kind.
    /// </summary>
    public bool IsPrivate => _type == SteamLobbyType.Private;

    /// <summary>
    /// Starts making a lobby for up to <paramref name="maxMembers"/> to host: friends-only, or private when
    /// <paramref name="privateLobby"/> (the developer's check tour: nobody is to find or join it).
    /// </summary>
    public void Create(int maxMembers, TimeSpan now, bool privateLobby = false)
    {
        _maxMembers = Math.Max(1, maxMembers);
        _type = privateLobby ? SteamLobbyType.Private : SteamLobbyType.FriendsOnly;
        _creating = _api.CreateLobby(_type, _maxMembers, Path);
        _callSince = now;
    }

    /// <summary>Starts joining <paramref name="hostName"/>'s lobby.</summary>
    public void Join(ulong lobby, string hostName, TimeSpan now)
    {
        JoiningName = hostName ?? string.Empty;
        _joiningLobby = lobby;
        _joining = _api.JoinLobby(lobby, Path);
        _callSince = now;
    }

    /// <summary>Call every frame: a create or join that finished, failed, or was given up on now.</summary>
    public SteamLobbyCall Poll(TimeSpan now)
    {
        if (_creating != null)
        {
            return PollCreate(now);
        }

        return _joining != null ? PollJoin(now) : SteamLobbyCall.None;
    }

    /// <summary>
    /// Reads who's in the lobby. False when we aren't in it ourselves (our own connection to Steam dropped?): that says
    /// nothing about anyone else, so <see cref="Members"/> stays as it was (on the host, an empty list would drop every
    /// friend), and we ask to be let back in, one request at a time, every <see cref="RejoinInterval"/> (hosting a lobby
    /// Steam says is gone: a new one is made instead). <paramref name="hostTag"/> is how the log names the host (its tag,
    /// never its name: D127).
    /// </summary>
    public bool RefreshMembers(ulong self, TimeSpan now, bool hosting, string hostTag)
    {
        ulong[] members = _api.GetMembers(Id);
        if (Array.IndexOf(members, self) >= 0)
        {
            Members = members;
            OutSince = TimeSpan.MinValue;
            return true;
        }

        if (OutSince == TimeSpan.MinValue)
        {
            OutSince = now;
        }

        if (_rejoin != null || _replacing != null || now < _nextRejoin)
        {
            return false;
        }

        _nextRejoin = now + RejoinInterval;
        if (_gone && hosting)
        {
            _replacing = _api.CreateLobby(_type, _maxMembers, Path);
            _replaceSince = now;
            _info($"Online: lobby {SteamLogTags.Lobby(Id)} is gone, so we're making a new one to host; friends in the session carry on meanwhile.");
            return false;
        }

        if (_rejoinTries++ == 0)
        {
            _warn($"Online: we're not in {(hosting ? "our own" : $"{hostTag}'s")} lobby ({SteamLogTags.Lobby(Id)}) any more (our connection to Steam dropped?); asking to be let back in " +
                $"every {RejoinInterval.TotalSeconds:0} s{(hosting ? ". Friends in the session carry on meanwhile; new friends can't join until we're back" : string.Empty)}.");
        }

        // Kept apart from abandoned calls (which leave whatever they enter), so getting back in keeps the lobby.
        _rejoinLobby = _target != 0 ? _target : Id;
        _rejoin = _api.JoinLobby(_rejoinLobby, Path);
        _rejoinSince = now;
        return false;
    }

    /// <summary>
    /// Call every frame: whether a request to be let back in (or, hosting, to make a new lobby) finished just now. A
    /// request Steam doesn't answer within <see cref="CallTimeout"/> is given up on (still polled: a late answer that lets
    /// us back in is kept) and asked again.
    /// </summary>
    public SteamLobbyRejoin PollRejoin(TimeSpan now, bool hosting)
    {
        if (_replacing != null)
        {
            return PollReplacing(now);
        }

        if (_rejoin == null)
        {
            return SteamLobbyRejoin.None;
        }

        SteamCallResult<LobbyEnter> call = _rejoin;
        if (call.Poll() == SteamCallResultState.Pending)
        {
            if (now - _rejoinSince < CallTimeout)
            {
                return SteamLobbyRejoin.None;
            }

            _warn($"Online: Steam hasn't answered our request to get back into lobby {SteamLogTags.Lobby(_rejoinLobby)} after {CallTimeout.TotalSeconds:0} s; asking again " +
                "(if the old request lets us in later, we stay).");
            _abandonedJoins.Add(call);
            _rejoin = null;
            _nextRejoin = now;
            return SteamLobbyRejoin.None;
        }

        _rejoin = null;
        bool entered = call.State == SteamCallResultState.Completed && call.Result.EnterResponse == SteamChatRoomEnterResponse.Success;
        if (entered && call.Result.LobbyId == _rejoinLobby)
        {
            if (_rejoinLobby != Id)
            {
                _info($"Online: now in lobby {SteamLogTags.Lobby(_rejoinLobby)}, the host's new one (lobby {SteamLogTags.Lobby(Id)} was gone).");
            }

            Id = _rejoinLobby;
            _target = 0;
            _gone = false;
            OutSince = TimeSpan.MinValue;
            _info($"Online: back in lobby {SteamLogTags.Lobby(Id)} ({_rejoinTries} request(s)).");
            _rejoinTries = 0;
            return SteamLobbyRejoin.Back;
        }

        if (entered)
        {
            _api.LeaveLobby(call.Result.LobbyId); // not the lobby we asked for
        }

        if (_rejoinTries <= 3 || _rejoinTries % 12 == 0)
        {
            _warn($"Online: couldn't get back into lobby {SteamLogTags.Lobby(_rejoinLobby)} yet ({call.State}, failure {call.FailureReason}, response {call.Result.EnterResponse}; " +
                $"{_rejoinTries} request(s) so far); asking again every {RejoinInterval.TotalSeconds:0} s.");
        }

        if (call.State != SteamCallResultState.Completed || call.Result.EnterResponse != SteamChatRoomEnterResponse.DoesntExist)
        {
            SwitchPathIfItHelps(call.ShouldTryOtherPath, call.Path);
            return SteamLobbyRejoin.None;
        }

        // Gone. Hosting, the next refresh makes a new lobby; a friend asks again (for the host's new lobby, once retargeted).
        _gone = true;
        _target = 0;
        if (hosting)
        {
            _nextRejoin = now;
            return SteamLobbyRejoin.None;
        }

        return SteamLobbyRejoin.Gone;
    }

    /// <summary>
    /// Friend, out of the lobby: the host is in <paramref name="lobby"/> now (they made a new one), so the next request to
    /// be let back in asks for it, at once. Nothing for 0 or the lobby we're already asking for.
    /// </summary>
    public void Retarget(ulong lobby)
    {
        if (lobby == 0 || lobby == Id || lobby == _target)
        {
            return;
        }

        _target = lobby;
        _gone = false;
        _nextRejoin = TimeSpan.MinValue;
        _info($"Online: the host is in lobby {SteamLogTags.Lobby(lobby)} now; asking to be let into it instead of lobby {SteamLogTags.Lobby(Id)}.");
    }

    /// <summary>
    /// Leaves the lobby. A create, join or rejoin still in flight finishes anyway: that lobby is left when it does, or a
    /// stray empty lobby of ours would show up first in friends' search.
    /// </summary>
    public void Leave(string reason)
    {
        if (Id != 0)
        {
            _api.LeaveLobby(Id);
            _info($"Online: left lobby {SteamLogTags.Lobby(Id)} ({reason}).");
        }

        if (_creating != null)
        {
            _abandonedCreates.Add(_creating);
        }

        if (_replacing != null)
        {
            _abandonedCreates.Add(_replacing);
        }

        if (_joining != null)
        {
            _abandonedJoins.Add(_joining);
        }

        if (_rejoin != null)
        {
            _abandonedJoins.Add(_rejoin);
        }

        Id = 0;
        Members = Array.Empty<ulong>();
        _creating = null;
        _replacing = null;
        _joining = null;
        _joiningLobby = 0;
        _rejoin = null;
        _rejoinLobby = 0;
        _rejoinTries = 0;
        _nextRejoin = TimeSpan.MinValue;
        _target = 0;
        _gone = false;
        OutSince = TimeSpan.MinValue;
    }

    /// <summary>
    /// Call every frame, even after an error turned online off: abandoned calls that finish leave their lobby, unless it's
    /// the one we're in or on the way into now (F7 to cancel a join, then F7 to join the same lobby again: the first join's
    /// late success mustn't take us out of it).
    /// </summary>
    public void PollAbandoned()
    {
        for (int i = _abandonedCreates.Count - 1; i >= 0; i--)
        {
            SteamCallResult<LobbyCreated> call = _abandonedCreates[i];
            if (call.Poll() != SteamCallResultState.Pending)
            {
                _abandonedCreates.RemoveAt(i);
                if (call.State == SteamCallResultState.Completed && call.Result.Result == SteamResult.OK && !Wanted(call.Result.LobbyId))
                {
                    _api.LeaveLobby(call.Result.LobbyId);
                    _info($"Online: left lobby {SteamLogTags.Lobby(call.Result.LobbyId)}, whose creation was cancelled.");
                }
            }
        }

        for (int i = _abandonedJoins.Count - 1; i >= 0; i--)
        {
            SteamCallResult<LobbyEnter> call = _abandonedJoins[i];
            if (call.Poll() == SteamCallResultState.Pending)
            {
                continue;
            }

            _abandonedJoins.RemoveAt(i);
            if (call.State != SteamCallResultState.Completed || call.Result.EnterResponse != SteamChatRoomEnterResponse.Success)
            {
                continue;
            }

            if (Wanted(call.Result.LobbyId))
            {
                _info($"Online: a cancelled request to join lobby {SteamLogTags.Lobby(call.Result.LobbyId)} finished; staying, as we're in it (or on the way in) now.");
                continue;
            }

            _api.LeaveLobby(call.Result.LobbyId);
            _info($"Online: left lobby {SteamLogTags.Lobby(call.Result.LobbyId)}, whose join was cancelled.");
        }
    }

    /// <summary>Why a lobby call that never got Steam's answer failed, for the status box.</summary>
    private static string CallProblem(SteamCallResultState state) => state == SteamCallResultState.TimedOut ? "Steam didn't answer" : "the request to Steam failed";

    /// <summary>Why a lobby join didn't work, for the status box.</summary>
    private static string JoinProblem(SteamCallResult<LobbyEnter> call) =>
        call.State != SteamCallResultState.Completed ? CallProblem(call.State)
        : call.Result.EnterResponse switch
        {
            SteamChatRoomEnterResponse.DoesntExist => "their lobby is gone",
            SteamChatRoomEnterResponse.Full => "their lobby is full",
            SteamChatRoomEnterResponse.NotAllowed => "Steam didn't let you in",
            SteamChatRoomEnterResponse.Banned or SteamChatRoomEnterResponse.CommunityBan => "Steam doesn't let this account into their lobby",
            SteamChatRoomEnterResponse.Limited => "limited Steam accounts can't join lobbies",
            SteamChatRoomEnterResponse.MemberBlockedYou => "a player in their lobby has blocked you on Steam",
            SteamChatRoomEnterResponse.YouBlockedMember => "you've blocked a player in their lobby on Steam",
            SteamChatRoomEnterResponse.RateLimitExceeded => "too many tries; wait a minute",
            SteamChatRoomEnterResponse response => $"Steam's answer was {(int)response}",
        };

    /// <summary>Why Steam didn't make a lobby (<c>LobbyCreated_t::m_eResult</c>), for the status box.</summary>
    private static string CreateProblem(SteamResult result) => result switch
    {
        SteamResult.Timeout or SteamResult.NoConnection => "Steam's servers didn't answer",
        SteamResult.LimitExceeded => "this account has too many lobbies open",
        SteamResult.AccessDenied => "Steam didn't allow it",
        _ => $"Steam's answer was {(int)result}",
    };

    /// <summary>True for a lobby we're in, or asking to get into, right now.</summary>
    private bool Wanted(ulong lobby) =>
        lobby != 0 && (lobby == Id || (_joining != null && lobby == _joiningLobby) || (_rejoin != null && lobby == _rejoinLobby));

    private SteamLobbyCall PollCreate(TimeSpan now)
    {
        SteamCallResult<LobbyCreated> call = _creating!;
        if (call.Poll() == SteamCallResultState.Pending)
        {
            if (now - _callSince < CallTimeout)
            {
                return SteamLobbyCall.None;
            }

            // Steam never answered: give up, but keep polling the call (PollAbandoned), so a lobby made late is left.
            _warn($"Online: Steam hasn't answered the lobby creation after {CallTimeout.TotalSeconds:0} s ({call.PollCount} polls via {Path}); " +
                "giving up. A lobby made later is left at once.");
            _abandonedCreates.Add(call);
            _creating = null;
            Problem = ProblemForLog = "Steam didn't answer, so no lobby was made.";
            return SteamLobbyCall.Failed;
        }

        _creating = null;
        Polls = call.PollCount;
        if (call.State != SteamCallResultState.Completed || call.Result.Result != SteamResult.OK)
        {
            _warn($"Online: lobby creation {call.State} ({call.Failure}, Steam's reason {call.FailureReason}, result {call.Result.Result}) via {call.Path}.");
            if (call.State == SteamCallResultState.TimedOut)
            {
                // Our poll limit ran out, not Steam's answer: the lobby may still be made. Watched again, so it's left.
                call.Resume();
                _abandonedCreates.Add(call);
            }

            SwitchPathIfItHelps(call.ShouldTryOtherPath, call.Path);
            Problem = ProblemForLog = $"Couldn't create a lobby ({(call.State == SteamCallResultState.Completed ? CreateProblem(call.Result.Result) : CallProblem(call.State))}).";
            return SteamLobbyCall.Failed;
        }

        Id = call.Result.LobbyId;
        return SteamLobbyCall.Created;
    }

    /// <summary>
    /// Steam finished the call but its result couldn't be fetched this way: the next call uses the other way. A call that
    /// simply failed or wasn't answered says nothing about the way, so it never switches.
    /// </summary>
    private void SwitchPathIfItHelps(bool shouldTryOtherPath, SteamCallResultPath used)
    {
        if (!shouldTryOtherPath)
        {
            return;
        }

        Path = used == SteamCallResultPath.SteamUtils ? SteamCallResultPath.ManualDispatch : SteamCallResultPath.SteamUtils;
        _info($"Online: Steam answered but its result couldn't be read via {used}; the next call uses {Path}.");
    }

    private SteamLobbyCall PollJoin(TimeSpan now)
    {
        SteamCallResult<LobbyEnter> call = _joining!;
        if (call.Poll() == SteamCallResultState.Pending)
        {
            if (now - _callSince < CallTimeout)
            {
                return SteamLobbyCall.None;
            }

            // As for a lobby creation: given up, but still polled, so a join that works late is left at once.
            _warn($"Online: Steam hasn't answered joining lobby {SteamLogTags.Lobby(_joiningLobby)} after {CallTimeout.TotalSeconds:0} s ({call.PollCount} polls via {Path}); " +
                "giving up. If it lets us in later, we leave at once.");
            _abandonedJoins.Add(call);
            _joining = null;
            Problem = $"Steam didn't answer, so you didn't join {JoiningName}'s game.";
            ProblemForLog = "Steam didn't answer, so you didn't join the host's game.";
            return SteamLobbyCall.Failed;
        }

        _joining = null;
        Polls = call.PollCount;
        LastEnterResponse = null;
        if (call.State != SteamCallResultState.Completed || call.Result.EnterResponse != SteamChatRoomEnterResponse.Success)
        {
            LastEnterResponse = call.State == SteamCallResultState.Completed ? (int)call.Result.EnterResponse : null;
            _warn($"Online: joining the lobby {call.State} ({call.Failure}, Steam's reason {call.FailureReason}, response {call.Result.EnterResponse}) via {call.Path}.");
            if (call.State == SteamCallResultState.TimedOut)
            {
                call.Resume();
                _abandonedJoins.Add(call);
            }

            SwitchPathIfItHelps(call.ShouldTryOtherPath, call.Path);
            Problem = $"Couldn't join {JoiningName}'s game ({JoinProblem(call)}).";
            ProblemForLog = $"Couldn't join the host's game ({JoinProblem(call)}).";
            return SteamLobbyCall.Failed;
        }

        Id = call.Result.LobbyId;
        return SteamLobbyCall.Entered;
    }

    /// <summary>Hosting a new lobby after the old one was gone: done, failed (tried again later) or still waiting.</summary>
    private SteamLobbyRejoin PollReplacing(TimeSpan now)
    {
        SteamCallResult<LobbyCreated> call = _replacing!;
        if (call.Poll() == SteamCallResultState.Pending)
        {
            if (now - _replaceSince < CallTimeout)
            {
                return SteamLobbyRejoin.None;
            }

            _warn($"Online: Steam hasn't answered making a new lobby after {CallTimeout.TotalSeconds:0} s; trying again in {RejoinInterval.TotalSeconds:0} s " +
                "(a lobby made later is left at once).");
            _abandonedCreates.Add(call);
            _replacing = null;
            _nextRejoin = now + RejoinInterval;
            return SteamLobbyRejoin.None;
        }

        _replacing = null;
        if (call.State != SteamCallResultState.Completed || call.Result.Result != SteamResult.OK)
        {
            _warn($"Online: making a new lobby {call.State} ({call.Failure}, Steam's reason {call.FailureReason}, result {call.Result.Result}); " +
                $"trying again in {RejoinInterval.TotalSeconds:0} s.");
            if (call.State == SteamCallResultState.TimedOut)
            {
                call.Resume();
                _abandonedCreates.Add(call);
            }

            SwitchPathIfItHelps(call.ShouldTryOtherPath, call.Path);
            _nextRejoin = now + RejoinInterval;
            return SteamLobbyRejoin.None;
        }

        ulong old = Id;
        Id = call.Result.LobbyId;
        Members = _api.GetMembers(Id);
        OutSince = TimeSpan.MinValue;
        _gone = false;
        _rejoinTries = 0;
        _info($"Online: hosting a new lobby, {SteamLogTags.Lobby(Id)} (lobby {SteamLogTags.Lobby(old)} was gone).");
        return SteamLobbyRejoin.Replaced;
    }

    private sealed class NativeLobbyApi : ISteamLobbyApi
    {
        public static readonly NativeLobbyApi Instance = new();

        public SteamCallResult<LobbyCreated> CreateLobby(SteamLobbyType type, int maxMembers, SteamCallResultPath path) => SteamLobbies.CreateLobby(type, maxMembers, path);

        public SteamCallResult<LobbyEnter> JoinLobby(ulong lobby, SteamCallResultPath path) => SteamLobbies.JoinLobby(lobby, path);

        public void LeaveLobby(ulong lobby) => SteamLobbies.LeaveLobby(lobby);

        public ulong[] GetMembers(ulong lobby) => SteamLobbies.GetMembers(lobby);
    }
}

/// <summary>The lobby calls <see cref="SteamLobbyLink"/> needs; Steam in the game, a fake in tests. Never throws.</summary>
internal interface ISteamLobbyApi
{
    /// <summary>As <see cref="SteamLobbies.CreateLobby"/>.</summary>
    SteamCallResult<LobbyCreated> CreateLobby(SteamLobbyType type, int maxMembers, SteamCallResultPath path);

    /// <summary>As <see cref="SteamLobbies.JoinLobby"/>.</summary>
    SteamCallResult<LobbyEnter> JoinLobby(ulong lobby, SteamCallResultPath path);

    /// <summary>As <see cref="SteamLobbies.LeaveLobby"/>.</summary>
    void LeaveLobby(ulong lobby);

    /// <summary>As <see cref="SteamLobbies.GetMembers"/>.</summary>
    ulong[] GetMembers(ulong lobby);
}
