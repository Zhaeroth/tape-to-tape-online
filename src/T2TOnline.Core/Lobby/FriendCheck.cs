using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Lobby;

/// <summary>What Steam's friends list said about a player (<see cref="FriendCheck"/>).</summary>
public enum FriendCheckResult
{
    /// <summary>A regular Steam friend.</summary>
    Friend,

    /// <summary>Steam answered: not a friend.</summary>
    NotAFriend,

    /// <summary>Steam couldn't answer: its friends interface or the <c>HasFriend</c> call isn't there (a broken Steam, not a passing hiccup).</summary>
    Unconfirmed,
}

/// <summary>
/// Only confirmed Steam friends play together (D134): a host seats only its friends, and a friend joins only a friend's
/// game. When Steam can't say, the answer is no, on both sides: the host refuses with
/// <see cref="RejectReason.FriendshipUnconfirmed"/>, and a friend leaves the lobby with
/// <see cref="LobbyFailure.FriendsUnchecked"/>. The one-PC test's own account is the caller's exception.
/// </summary>
public static class FriendCheck
{
    /// <summary>Steam's answer: <paramref name="answered"/> is the ask's result, <paramref name="isFriend"/> means something only when it answered.</summary>
    public static FriendCheckResult From(bool answered, bool isFriend) =>
        !answered ? FriendCheckResult.Unconfirmed
        : isFriend ? FriendCheckResult.Friend
        : FriendCheckResult.NotAFriend;

    /// <summary>The host's refusal for a player asking for a seat, or <see cref="RejectReason.None"/> to seat them.</summary>
    public static RejectReason HostRefusal(FriendCheckResult result) => result switch
    {
        FriendCheckResult.Friend => RejectReason.None,
        FriendCheckResult.NotAFriend => RejectReason.NotAFriend,
        _ => RejectReason.FriendshipUnconfirmed,
    };

    /// <summary>A friend's failure for the host's lobby it entered, or null to say hello.</summary>
    public static LobbyFailure? FriendFailure(FriendCheckResult result) => result switch
    {
        FriendCheckResult.Friend => null,
        FriendCheckResult.NotAFriend => LobbyFailure.NotAFriend,
        _ => LobbyFailure.FriendsUnchecked,
    };
}
