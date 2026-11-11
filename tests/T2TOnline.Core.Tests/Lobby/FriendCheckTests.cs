using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>Only confirmed Steam friends play together: when Steam can't say, both sides say no (D134).</summary>
public class FriendCheckTests
{
    [Theory]
    [InlineData(true, true, FriendCheckResult.Friend)]
    [InlineData(true, false, FriendCheckResult.NotAFriend)]
    [InlineData(false, false, FriendCheckResult.Unconfirmed)]
    [InlineData(false, true, FriendCheckResult.Unconfirmed)] // an answer that wasn't given means nothing
    public void Steams_answer_is_friend_not_a_friend_or_unconfirmed(bool answered, bool isFriend, FriendCheckResult result)
    {
        Assert.Equal(result, FriendCheck.From(answered, isFriend));
    }

    [Theory]
    [InlineData(FriendCheckResult.Friend, RejectReason.None)]
    [InlineData(FriendCheckResult.NotAFriend, RejectReason.NotAFriend)]
    [InlineData(FriendCheckResult.Unconfirmed, RejectReason.FriendshipUnconfirmed)]
    [InlineData((FriendCheckResult)99, RejectReason.FriendshipUnconfirmed)] // anything unknown is a no
    public void The_host_seats_only_a_confirmed_friend(FriendCheckResult result, RejectReason refusal)
    {
        Assert.Equal(refusal, FriendCheck.HostRefusal(result));
    }

    [Theory]
    [InlineData(FriendCheckResult.Friend, null)]
    [InlineData(FriendCheckResult.NotAFriend, LobbyFailure.NotAFriend)]
    [InlineData(FriendCheckResult.Unconfirmed, LobbyFailure.FriendsUnchecked)]
    [InlineData((FriendCheckResult)99, LobbyFailure.FriendsUnchecked)]
    public void A_friend_joins_only_a_confirmed_friends_game(FriendCheckResult result, LobbyFailure? failure)
    {
        Assert.Equal(failure, FriendCheck.FriendFailure(result));
    }

    [Fact]
    public void Unconfirmed_is_refused_on_the_wire_with_reason_10()
    {
        Assert.Equal(10, (int)FriendCheck.HostRefusal(FriendCheck.From(answered: false, isFriend: false)));
    }

    [Fact]
    public void The_hosts_refusal_reaches_the_friend_as_the_hosts_steam_and_its_own_as_its_own()
    {
        // Host refused: the friend's prompt names the host's Steam; its own Steam failing names its own.
        Assert.Equal(LobbyFailure.HostFriendsUnchecked, JoinFailures.FromReject(FriendCheck.HostRefusal(FriendCheckResult.Unconfirmed)));
        Assert.Equal(LobbyFailure.NotAFriend, JoinFailures.FromReject(FriendCheck.HostRefusal(FriendCheckResult.NotAFriend)));
        Assert.NotEqual(JoinFailures.Message(LobbyFailure.HostFriendsUnchecked, "Alex").Line, JoinFailures.Message(LobbyFailure.FriendsUnchecked, "Alex").Line);
    }
}
