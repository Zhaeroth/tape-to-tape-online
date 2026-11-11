using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

public class SteamLobbyLinkTests
{
    private const ulong Self = 76561199000000001UL;
    private const ulong HostId = 76561199000000002UL;
    private const ulong Lobby = 109775240917663232UL;
    private const ulong OtherLobby = 109775240917663233UL;
    private const ulong NewLobby = 109775240917663234UL;

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    /// <summary>A scripted Steam: calls stay pending until a test answers them; members per lobby; every lobby left.</summary>
    private sealed class FakeLobbies : ISteamLobbyApi, ISteamCallResultSource
    {
        private readonly Dictionary<ulong, byte[]> _answers = new();
        private ulong _nextHandle = 1000;

        public List<ulong> Creates { get; } = new();

        public List<SteamLobbyType> CreateTypes { get; } = new();

        public List<(ulong Handle, ulong Lobby)> Joins { get; } = new();

        public List<ulong> Left { get; } = new();

        public Dictionary<ulong, ulong[]> Members { get; } = new();

        public SteamCallResultPath Path => SteamCallResultPath.SteamUtils;

        public SteamCallResult<LobbyCreated> CreateLobby(SteamLobbyType type, int maxMembers, SteamCallResultPath path)
        {
            ulong handle = _nextHandle++;
            Creates.Add(handle);
            CreateTypes.Add(type);
            return new SteamCallResult<LobbyCreated>(handle, LobbyCreated.CallbackId, this, 0);
        }

        public SteamCallResult<LobbyEnter> JoinLobby(ulong lobby, SteamCallResultPath path)
        {
            ulong handle = _nextHandle++;
            Joins.Add((handle, lobby));
            return new SteamCallResult<LobbyEnter>(handle, LobbyEnter.CallbackId, this, 0);
        }

        public void LeaveLobby(ulong lobby) => Left.Add(lobby);

        public ulong[] GetMembers(ulong lobby) => Members.TryGetValue(lobby, out ulong[]? members) ? members : Array.Empty<ulong>();

        /// <summary>Answers the newest join of <paramref name="lobby"/>.</summary>
        public void AnswerJoin(ulong lobby, SteamChatRoomEnterResponse response) => AnswerJoin(Joins.Last(j => j.Lobby == lobby).Handle, lobby, response);

        public void AnswerJoin(ulong handle, ulong lobby, SteamChatRoomEnterResponse response)
        {
            var bytes = new byte[LobbyEnter.SizeInBytes];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), lobby);
            BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), (uint)response);
            _answers[handle] = bytes;
        }

        public void AnswerCreate(ulong handle, SteamResult result, ulong lobby)
        {
            var bytes = new byte[LobbyCreated.SizeInBytes];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (int)result);
            BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), lobby);
            _answers[handle] = bytes;
        }

        public bool TryIsCompleted(ulong call, out bool completed, out bool failed)
        {
            completed = _answers.ContainsKey(call);
            failed = false;
            return true;
        }

        public SteamCallFetch Fetch<T>(ulong call, int callbackId, out T result)
            where T : unmanaged
        {
            result = default;
            if (!_answers.Remove(call, out byte[]? bytes))
            {
                return SteamCallFetch.Refused;
            }

            result = MemoryMarshal.Read<T>(bytes);
            return SteamCallFetch.Fetched;
        }

        public SteamApiCallFailure GetFailureReason(ulong call) => SteamApiCallFailure.None;
    }

    private sealed class Rig
    {
        public Rig()
        {
            Link = new SteamLobbyLink(Steam, Lines.Add, Lines.Add);
        }

        public FakeLobbies Steam { get; } = new();

        public List<string> Lines { get; } = new();

        public SteamLobbyLink Link { get; }

        /// <summary>Hosting lobby <see cref="Lobby"/> with only us in it.</summary>
        public Rig Hosting()
        {
            Link.Create(4, TimeSpan.Zero);
            Steam.AnswerCreate(Steam.Creates[^1], SteamResult.OK, Lobby);
            Assert.Equal(SteamLobbyCall.Created, Link.Poll(TimeSpan.Zero));
            Steam.Members[Lobby] = new[] { Self };
            Assert.True(Link.RefreshMembers(Self, TimeSpan.Zero, hosting: true, "you"));
            return this;
        }

        /// <summary>In the host's lobby <see cref="Lobby"/> with them.</summary>
        public Rig InHostsLobby()
        {
            Link.Join(Lobby, "host", TimeSpan.Zero);
            Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.Success);
            Assert.Equal(SteamLobbyCall.Entered, Link.Poll(TimeSpan.Zero));
            Steam.Members[Lobby] = new[] { HostId, Self };
            Assert.True(Link.RefreshMembers(Self, TimeSpan.Zero, hosting: false, "host"));
            return this;
        }
    }

    [Fact]
    public void A_lobby_made_is_ours_to_host()
    {
        var rig = new Rig();
        rig.Link.Create(4, TimeSpan.Zero);
        Assert.True(rig.Link.Creating);
        Assert.True(rig.Link.Active);
        Assert.Equal(SteamLobbyCall.None, rig.Link.Poll(Second));

        rig.Steam.AnswerCreate(rig.Steam.Creates[0], SteamResult.OK, Lobby);

        Assert.Equal(SteamLobbyCall.Created, rig.Link.Poll(2 * Second));
        Assert.Equal(Lobby, rig.Link.Id);
        Assert.False(rig.Link.Creating);
    }

    [Fact]
    public void A_lobby_is_friends_only_unless_asked_private()
    {
        var rig = new Rig().Hosting();
        Assert.Equal(new[] { SteamLobbyType.FriendsOnly }, rig.Steam.CreateTypes);
        Assert.False(rig.Link.IsPrivate);

        var tour = new Rig();
        tour.Link.Create(4, TimeSpan.Zero, privateLobby: true);
        Assert.Equal(new[] { SteamLobbyType.Private }, tour.Steam.CreateTypes);
        Assert.True(tour.Link.IsPrivate);
    }

    [Fact]
    public void A_private_lobby_that_is_gone_is_replaced_by_a_private_one()
    {
        var rig = new Rig();
        rig.Link.Create(4, TimeSpan.Zero, privateLobby: true);
        rig.Steam.AnswerCreate(rig.Steam.Creates[^1], SteamResult.OK, Lobby);
        Assert.Equal(SteamLobbyCall.Created, rig.Link.Poll(TimeSpan.Zero));
        rig.Steam.Members[Lobby] = new[] { Self };
        Assert.True(rig.Link.RefreshMembers(Self, TimeSpan.Zero, hosting: true, "you"));

        rig.Steam.Members[Lobby] = Array.Empty<ulong>();
        rig.Link.RefreshMembers(Self, Second, hosting: true, "you");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        rig.Link.PollRejoin(2 * Second, hosting: true);
        rig.Link.RefreshMembers(Self, 2 * Second, hosting: true, "you");

        Assert.True(rig.Link.Replacing);
        Assert.Equal(new[] { SteamLobbyType.Private, SteamLobbyType.Private }, rig.Steam.CreateTypes);
    }

    [Fact]
    public void A_creation_Steam_never_answers_is_given_up_and_its_late_lobby_left()
    {
        var rig = new Rig();
        rig.Link.Create(4, TimeSpan.Zero);

        Assert.Equal(SteamLobbyCall.None, rig.Link.Poll(SteamLobbyLink.CallTimeout - Second));
        Assert.Equal(SteamLobbyCall.Failed, rig.Link.Poll(SteamLobbyLink.CallTimeout));
        Assert.False(rig.Link.Active);
        Assert.Contains("Steam didn't answer", rig.Link.Problem, StringComparison.Ordinal);

        rig.Steam.AnswerCreate(rig.Steam.Creates[0], SteamResult.OK, Lobby);
        rig.Link.PollAbandoned();
        Assert.Equal(new[] { Lobby }, rig.Steam.Left);
    }

    [Fact]
    public void A_refused_creation_says_why_in_words()
    {
        var rig = new Rig();
        rig.Link.Create(4, TimeSpan.Zero);
        rig.Steam.AnswerCreate(rig.Steam.Creates[0], SteamResult.LimitExceeded, 0);

        Assert.Equal(SteamLobbyCall.Failed, rig.Link.Poll(Second));
        Assert.Equal("Couldn't create a lobby (this account has too many lobbies open).", rig.Link.Problem);
    }

    [Fact]
    public void A_join_Steam_refuses_says_why_in_words()
    {
        var rig = new Rig();
        rig.Link.Join(Lobby, "Alex", TimeSpan.Zero);
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);

        Assert.Equal(SteamLobbyCall.Failed, rig.Link.Poll(Second));
        Assert.Equal("Couldn't join Alex's game (their lobby is gone).", rig.Link.Problem);
        Assert.Equal(0UL, rig.Link.Id);
    }

    [Fact]
    public void A_failed_joins_log_copy_names_nobody()
    {
        var rig = new Rig();
        rig.Link.Join(Lobby, "Alex", TimeSpan.Zero);
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        Assert.Equal(SteamLobbyCall.Failed, rig.Link.Poll(Second));
        Assert.Equal("Couldn't join the host's game (their lobby is gone).", rig.Link.ProblemForLog);

        rig.Link.Join(Lobby, "Alex", TimeSpan.Zero);
        Assert.Equal(SteamLobbyCall.Failed, rig.Link.Poll(SteamLobbyLink.CallTimeout));
        Assert.Contains("Alex", rig.Link.Problem, StringComparison.Ordinal); // the screen's sentence keeps the name
        Assert.Equal("Steam didn't answer, so you didn't join the host's game.", rig.Link.ProblemForLog);

        rig.Link.Create(4, TimeSpan.Zero);
        rig.Steam.AnswerCreate(rig.Steam.Creates[0], SteamResult.LimitExceeded, 0);
        Assert.Equal(SteamLobbyCall.Failed, rig.Link.Poll(SteamLobbyLink.CallTimeout + Second));
        Assert.Equal(rig.Link.Problem, rig.Link.ProblemForLog);
    }

    [Fact]
    public void The_lobby_being_joined_is_known_until_the_join_is_over()
    {
        var rig = new Rig();
        Assert.Equal(0UL, rig.Link.JoiningLobby);

        rig.Link.Join(Lobby, "Alex", TimeSpan.Zero);
        Assert.Equal(Lobby, rig.Link.JoiningLobby);

        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.Success);
        Assert.Equal(SteamLobbyCall.Entered, rig.Link.Poll(Second));
        Assert.Equal(0UL, rig.Link.JoiningLobby);
        Assert.Equal(Lobby, rig.Link.Id);

        rig.Link.Leave("F7 pressed");
        rig.Link.Join(OtherLobby, "Riley", TimeSpan.Zero);
        rig.Link.Leave("F7 pressed");
        Assert.Equal(0UL, rig.Link.JoiningLobby);
    }

    [Fact]
    public void A_cancelled_join_that_finishes_late_is_left()
    {
        var rig = new Rig();
        rig.Link.Join(Lobby, "host", TimeSpan.Zero);
        rig.Link.Leave("F7 pressed");

        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.Success);
        rig.Link.PollAbandoned();

        Assert.Equal(new[] { Lobby }, rig.Steam.Left);
    }

    [Fact]
    public void A_cancelled_join_of_the_lobby_joined_again_does_not_take_us_out_of_it()
    {
        var rig = new Rig();
        rig.Link.Join(Lobby, "host", TimeSpan.Zero);
        ulong first = rig.Steam.Joins[0].Handle;
        rig.Link.Leave("F7 pressed");
        rig.Link.Join(Lobby, "host", Second);

        // The cancelled join finishes first, while the second is on its way into the same lobby: kept.
        rig.Steam.AnswerJoin(first, Lobby, SteamChatRoomEnterResponse.Success);
        rig.Link.PollAbandoned();
        Assert.Empty(rig.Steam.Left);

        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.Success);
        Assert.Equal(SteamLobbyCall.Entered, rig.Link.Poll(2 * Second));
        Assert.Equal(Lobby, rig.Link.Id);
        Assert.Empty(rig.Steam.Left);
    }

    [Fact]
    public void A_cancelled_join_finishing_once_we_are_in_that_lobby_keeps_us_there()
    {
        var rig = new Rig();
        rig.Link.Join(Lobby, "host", TimeSpan.Zero);
        ulong first = rig.Steam.Joins[0].Handle;
        rig.Link.Leave("F7 pressed");
        rig.InHostsLobby();

        rig.Steam.AnswerJoin(first, Lobby, SteamChatRoomEnterResponse.Success);
        rig.Link.PollAbandoned();

        Assert.Empty(rig.Steam.Left);
        Assert.Equal(Lobby, rig.Link.Id);
    }

    [Fact]
    public void Out_of_the_lobby_the_members_stay_and_we_ask_back_in_one_request_at_a_time()
    {
        var rig = new Rig().InHostsLobby();
        rig.Steam.Members[Lobby] = new[] { HostId };

        Assert.False(rig.Link.RefreshMembers(Self, Second, hosting: false, "host"));
        Assert.Equal(new[] { HostId, Self }, rig.Link.Members);
        Assert.True(rig.Link.OutOfLobby);
        Assert.Equal(Second, rig.Link.OutSince);
        Assert.Equal(2, rig.Steam.Joins.Count);

        // Pending: no second request, however often the members are read.
        Assert.False(rig.Link.RefreshMembers(Self, 10 * Second, hosting: false, "host"));
        Assert.Equal(2, rig.Steam.Joins.Count);

        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.Success);
        rig.Steam.Members[Lobby] = new[] { HostId, Self };
        Assert.Equal(SteamLobbyRejoin.Back, rig.Link.PollRejoin(11 * Second, hosting: false));
        Assert.False(rig.Link.OutOfLobby);
        Assert.Equal(Lobby, rig.Link.Id);
        Assert.Empty(rig.Steam.Left);
    }

    [Fact]
    public void A_failed_request_to_get_back_in_is_asked_again_after_the_interval()
    {
        var rig = new Rig().InHostsLobby();
        rig.Steam.Members[Lobby] = new[] { HostId };
        rig.Link.RefreshMembers(Self, Second, hosting: false, "host");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.Error);
        Assert.Equal(SteamLobbyRejoin.None, rig.Link.PollRejoin(2 * Second, hosting: false));

        rig.Link.RefreshMembers(Self, 3 * Second, hosting: false, "host");
        Assert.Equal(2, rig.Steam.Joins.Count); // within the interval

        rig.Link.RefreshMembers(Self, Second + SteamLobbyLink.RejoinInterval, hosting: false, "host");
        Assert.Equal(3, rig.Steam.Joins.Count);
    }

    [Fact]
    public void A_request_to_get_back_in_that_Steam_never_answers_is_given_up_and_asked_again()
    {
        var rig = new Rig().InHostsLobby();
        rig.Steam.Members[Lobby] = new[] { HostId };
        rig.Link.RefreshMembers(Self, Second, hosting: false, "host");
        ulong stuck = rig.Steam.Joins[^1].Handle;

        Assert.Equal(SteamLobbyRejoin.None, rig.Link.PollRejoin(Second + SteamLobbyLink.CallTimeout - Second, hosting: false));
        Assert.Equal(SteamLobbyRejoin.None, rig.Link.PollRejoin(Second + SteamLobbyLink.CallTimeout, hosting: false));
        Assert.Contains(rig.Lines, line => line.Contains("hasn't answered our request to get back into", StringComparison.Ordinal));

        rig.Link.RefreshMembers(Self, Second + SteamLobbyLink.CallTimeout, hosting: false, "host");
        Assert.Equal(3, rig.Steam.Joins.Count);

        // The old request lets us in after all: that's our lobby, so we stay.
        rig.Steam.AnswerJoin(stuck, Lobby, SteamChatRoomEnterResponse.Success);
        rig.Link.PollAbandoned();
        Assert.Empty(rig.Steam.Left);
    }

    [Fact]
    public void A_host_whose_lobby_is_gone_hosts_a_new_one()
    {
        var rig = new Rig().Hosting();
        rig.Steam.Members[Lobby] = Array.Empty<ulong>();
        rig.Link.RefreshMembers(Self, Second, hosting: true, "you");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        Assert.Equal(SteamLobbyRejoin.None, rig.Link.PollRejoin(2 * Second, hosting: true));

        // The next refresh makes a new lobby instead of asking for the old one again.
        rig.Link.RefreshMembers(Self, 2 * Second, hosting: true, "you");
        Assert.True(rig.Link.Replacing);
        Assert.Equal(2, rig.Steam.Creates.Count);
        Assert.Single(rig.Steam.Joins); // the one request to get back in, no more

        rig.Steam.AnswerCreate(rig.Steam.Creates[^1], SteamResult.OK, NewLobby);
        rig.Steam.Members[NewLobby] = new[] { Self };
        Assert.Equal(SteamLobbyRejoin.Replaced, rig.Link.PollRejoin(3 * Second, hosting: true));
        Assert.Equal(NewLobby, rig.Link.Id);
        Assert.Equal(new[] { Self }, rig.Link.Members);
        Assert.False(rig.Link.OutOfLobby);
        Assert.False(rig.Link.Replacing);
    }

    [Fact]
    public void Its_lines_name_no_lobby_and_no_host_by_their_ids()
    {
        var rig = new Rig().InHostsLobby();
        rig.Steam.Members[Lobby] = new[] { HostId };
        rig.Link.RefreshMembers(Self, Second, hosting: false, "P#3fa9c1");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        Assert.Equal(SteamLobbyRejoin.Gone, rig.Link.PollRejoin(2 * Second, hosting: false));
        rig.Link.Retarget(NewLobby);
        rig.Link.RefreshMembers(Self, 3 * Second, hosting: false, "P#3fa9c1");
        rig.Steam.AnswerJoin(NewLobby, SteamChatRoomEnterResponse.Success);
        rig.Steam.Members[NewLobby] = new[] { HostId, Self };
        Assert.Equal(SteamLobbyRejoin.Back, rig.Link.PollRejoin(4 * Second, hosting: false));
        rig.Link.Leave("done");

        Assert.Contains(rig.Lines, line => line.Contains("not in P#3fa9c1's lobby", StringComparison.Ordinal));
        Assert.Contains(rig.Lines, line => line.Contains("the host's new one", StringComparison.Ordinal));
        foreach (ulong id in new[] { Lobby, NewLobby, HostId, Self })
        {
            string text = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.DoesNotContain(rig.Lines, line => line.Contains(text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_new_lobby_Steam_refuses_is_tried_again_after_the_interval()
    {
        var rig = new Rig().Hosting();
        rig.Steam.Members[Lobby] = Array.Empty<ulong>();
        rig.Link.RefreshMembers(Self, Second, hosting: true, "you");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        rig.Link.PollRejoin(Second, hosting: true);
        rig.Link.RefreshMembers(Self, Second, hosting: true, "you");
        rig.Steam.AnswerCreate(rig.Steam.Creates[^1], SteamResult.Fail, 0);

        Assert.Equal(SteamLobbyRejoin.None, rig.Link.PollRejoin(2 * Second, hosting: true));
        rig.Link.RefreshMembers(Self, 3 * Second, hosting: true, "you");
        Assert.Equal(2, rig.Steam.Creates.Count);
        rig.Link.RefreshMembers(Self, 2 * Second + SteamLobbyLink.RejoinInterval, hosting: true, "you");
        Assert.Equal(3, rig.Steam.Creates.Count);
        Assert.True(rig.Link.OutOfLobby);
    }

    [Fact]
    public void A_friend_whose_lobby_is_gone_asks_for_the_hosts_new_one_once_pointed_at_it()
    {
        var rig = new Rig().InHostsLobby();
        rig.Steam.Members[Lobby] = Array.Empty<ulong>();
        rig.Link.RefreshMembers(Self, Second, hosting: false, "host");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        Assert.Equal(SteamLobbyRejoin.Gone, rig.Link.PollRejoin(2 * Second, hosting: false));

        // Until pointed at a new lobby, a friend asks for the old one again (it never makes one of its own).
        rig.Link.RefreshMembers(Self, Second + SteamLobbyLink.RejoinInterval, hosting: false, "host");
        Assert.Equal(Lobby, rig.Steam.Joins[^1].Lobby);
        Assert.Empty(rig.Steam.Creates);
    }

    [Fact]
    public void A_friend_pointed_at_the_hosts_new_lobby_moves_into_it()
    {
        var rig = new Rig().InHostsLobby();
        rig.Steam.Members[Lobby] = Array.Empty<ulong>();
        rig.Link.RefreshMembers(Self, Second, hosting: false, "host");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        rig.Link.PollRejoin(2 * Second, hosting: false);

        rig.Link.Retarget(NewLobby);
        rig.Link.RefreshMembers(Self, 2 * Second, hosting: false, "host"); // at once, not after the interval
        Assert.Equal(NewLobby, rig.Steam.Joins[^1].Lobby);

        rig.Steam.AnswerJoin(NewLobby, SteamChatRoomEnterResponse.Success);
        Assert.Equal(SteamLobbyRejoin.Back, rig.Link.PollRejoin(3 * Second, hosting: false));
        Assert.Equal(NewLobby, rig.Link.Id);
        Assert.Empty(rig.Steam.Left);
        Assert.Empty(rig.Steam.Creates);
    }

    [Fact]
    public void Getting_into_another_lobby_than_asked_leaves_it()
    {
        var rig = new Rig().InHostsLobby();
        rig.Steam.Members[Lobby] = new[] { HostId };
        rig.Link.RefreshMembers(Self, Second, hosting: false, "host");

        rig.Steam.AnswerJoin(rig.Steam.Joins[^1].Handle, OtherLobby, SteamChatRoomEnterResponse.Success);

        Assert.Equal(SteamLobbyRejoin.None, rig.Link.PollRejoin(2 * Second, hosting: false));
        Assert.Equal(new[] { OtherLobby }, rig.Steam.Left);
        Assert.Equal(Lobby, rig.Link.Id);
    }

    [Fact]
    public void Leaving_while_a_new_lobby_is_being_made_leaves_that_one_when_it_comes()
    {
        var rig = new Rig().Hosting();
        rig.Steam.Members[Lobby] = Array.Empty<ulong>();
        rig.Link.RefreshMembers(Self, Second, hosting: true, "you");
        rig.Steam.AnswerJoin(Lobby, SteamChatRoomEnterResponse.DoesntExist);
        rig.Link.PollRejoin(Second, hosting: true);
        rig.Link.RefreshMembers(Self, Second, hosting: true, "you");

        rig.Link.Leave("F7 pressed");
        rig.Steam.AnswerCreate(rig.Steam.Creates[^1], SteamResult.OK, NewLobby);
        rig.Link.PollAbandoned();

        Assert.Equal(new[] { Lobby, NewLobby }, rig.Steam.Left);
        Assert.False(rig.Link.Active);
        Assert.False(rig.Link.OutOfLobby);
    }
}
