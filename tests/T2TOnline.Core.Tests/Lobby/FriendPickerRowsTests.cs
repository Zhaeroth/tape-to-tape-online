using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class FriendPickerRowsTests
{
    private const long Now = 500_000_000;

    private static PickerFriend Friend(ulong id, string name, PickerActivity activity = PickerActivity.Online, bool inLobby = false) => new(id, name, activity, inLobby);

    private static FriendPickerRows Listed(params PickerFriend[] friends)
    {
        var rows = new FriendPickerRows();
        rows.Update(friends, Now);
        return rows;
    }

    [Fact]
    public void Online_friends_show_t2t_players_first_then_by_name_and_offline_ones_are_left_out()
    {
        FriendPickerRows rows = Listed(
            Friend(1, "zed", PickerActivity.Online),
            Friend(2, "Off", PickerActivity.Offline),
            Friend(3, "Amy", PickerActivity.OtherGame),
            Friend(4, "Mo", PickerActivity.PlayingT2T),
            Friend(5, "Bo", PickerActivity.PlayingT2T));

        Assert.Equal(new[] { "Bo", "Mo", "Amy", "zed" }, rows.Rows.Select(r => r.Name));
    }

    [Fact]
    public void Each_row_says_what_they_are_doing()
    {
        FriendPickerRows rows = Listed(Friend(1, "A", PickerActivity.PlayingT2T), Friend(2, "B", PickerActivity.OtherGame), Friend(3, "C", PickerActivity.Online));

        Assert.Equal(new[] { LobbyWords.PlayingT2T.Key, LobbyWords.PlayingOther.Key, LobbyWords.OnlineNow.Key }, rows.Rows.Select(r => r.Line.Line.Term));
    }

    [Fact]
    public void Someone_already_here_is_marked_and_cant_be_invited()
    {
        PickerRow row = Listed(Friend(1, "A", inLobby: true)).Rows[0];

        Assert.Equal(PickerMark.InThisLobby, row.Mark);
        Assert.False(row.CanInvite);
        Assert.Equal(LobbyWords.InThisLobby.Key, row.Line.Line.Term);
    }

    [Fact]
    public void An_invite_marks_them_for_two_minutes_and_then_they_can_be_invited_again()
    {
        var rows = new FriendPickerRows();
        PickerFriend[] friends = { Friend(1, "A") };
        rows.Update(friends, Now);
        Assert.True(rows.Rows[0].CanInvite);

        rows.Invited(1, Now);
        rows.Update(friends, Now);

        Assert.Equal(PickerMark.Invited, rows.Rows[0].Mark);
        Assert.False(rows.Rows[0].CanInvite);
        Assert.Equal(120, rows.Rows[0].ResendSeconds);
        Assert.Equal((LobbyIcon.Invited, LobbyWords.Invited.Key), (rows.Rows[0].Line.Icon, rows.Rows[0].Line.Line.Term));

        rows.Update(friends, Now + FriendPickerRows.ResendMicros - 1);
        Assert.Equal(PickerMark.Invited, rows.Rows[0].Mark);
        Assert.Equal(1, rows.Rows[0].ResendSeconds);

        rows.Update(friends, Now + FriendPickerRows.ResendMicros);
        Assert.Equal(PickerMark.None, rows.Rows[0].Mark);
        Assert.True(rows.Rows[0].CanInvite);
        Assert.Equal(0, rows.Rows[0].ResendSeconds);
    }

    [Fact]
    public void Being_here_wins_over_a_mark_from_before()
    {
        var rows = new FriendPickerRows();
        rows.Invited(1, Now);
        rows.Update(new[] { Friend(1, "A", inLobby: true) }, Now);

        Assert.Equal(PickerMark.InThisLobby, rows.Rows[0].Mark);
        Assert.Equal(0, rows.Rows[0].ResendSeconds);
    }

    [Fact]
    public void Focus_follows_the_friend_not_the_row()
    {
        FriendPickerRows rows = Listed(Friend(1, "B"), Friend(2, "C"));
        Assert.Equal(1UL, rows.FocusedFriend);
        rows.Focus(2);

        // Someone sorts above them.
        rows.Update(new[] { Friend(3, "A"), Friend(1, "B"), Friend(2, "C") }, Now);

        Assert.Equal(2UL, rows.FocusedFriend);
        Assert.Equal(2, rows.FocusIndex);
    }

    [Fact]
    public void When_the_focused_friend_goes_focus_stays_at_that_place()
    {
        FriendPickerRows rows = Listed(Friend(1, "A"), Friend(2, "B"), Friend(3, "C"));
        rows.Focus(2);

        rows.Update(new[] { Friend(1, "A"), Friend(3, "C") }, Now);
        Assert.Equal(3UL, rows.FocusedFriend);

        rows.Update(new[] { Friend(1, "A") }, Now);
        Assert.Equal(1UL, rows.FocusedFriend);

        rows.Update(new[] { Friend(9, "Off", PickerActivity.Offline) }, Now);
        Assert.True(rows.IsEmpty);
        Assert.Equal(0UL, rows.FocusedFriend);
        Assert.Equal(-1, rows.FocusIndex);
    }

    [Fact]
    public void The_list_wraps()
    {
        FriendPickerRows rows = Listed(Friend(1, "A"), Friend(2, "B"));

        rows.Move(-1);
        Assert.Equal(2UL, rows.FocusedFriend);
        rows.Move(1);
        Assert.Equal(1UL, rows.FocusedFriend);

        new FriendPickerRows().Move(1); // nothing to move in
    }

    [Fact]
    public void Focus_on_someone_not_there_says_false()
    {
        Assert.False(Listed(Friend(1, "A")).Focus(99));
    }

    [Fact]
    public void Doubles_and_id_0_are_left_out_and_a_long_list_is_capped()
    {
        Assert.Single(Listed(Friend(1, "A"), Friend(1, "A"), Friend(0, "Nobody")).Rows);

        FriendPickerRows many = Listed(Enumerable.Range(1, 1000).Select(i => Friend((ulong)i, $"F{i:0000}")).ToArray());
        Assert.Equal(FriendPickerRows.MaxRows, many.Rows.Count);
    }

    [Fact]
    public void The_list_opens_on_the_first_friend_who_can_be_invited()
    {
        // Who's here plays the game, so they come first; A on them only grunts.
        FriendPickerRows rows = Listed(Friend(1, "A", PickerActivity.PlayingT2T, inLobby: true), Friend(2, "B", PickerActivity.PlayingT2T), Friend(3, "C"));
        Assert.Equal(2UL, rows.FocusedFriend);
        Assert.True(rows.Rows[rows.FocusIndex].CanInvite);

        // Nobody to invite: still somewhere.
        Assert.Equal(1UL, Listed(Friend(1, "A", PickerActivity.PlayingT2T, inLobby: true)).FocusedFriend);
    }

    [Fact]
    public void A_focus_already_on_someone_stays_when_they_arrive_here()
    {
        var rows = new FriendPickerRows();
        rows.Update(new[] { Friend(1, "A"), Friend(2, "B") }, Now);
        Assert.Equal(1UL, rows.FocusedFriend);
        rows.Update(new[] { Friend(1, "A", inLobby: true), Friend(2, "B") }, Now + 1);
        Assert.Equal(1UL, rows.FocusedFriend);
    }

    [Fact]
    public void Update_says_when_what_a_row_shows_changed()
    {
        var rows = new FriendPickerRows();
        Assert.True(rows.Update(new[] { Friend(1, "A"), Friend(2, "B") }, Now));
        Assert.False(rows.Update(new[] { Friend(1, "A"), Friend(2, "B") }, Now + 1_000_000));
        Assert.True(rows.Update(new[] { Friend(1, "A"), Friend(2, "B", PickerActivity.OtherGame) }, Now + 2_000_000));
        Assert.True(rows.Update(new[] { Friend(1, "A") }, Now + 3_000_000));

        // A mark's coming and going shows; its seconds running down don't.
        rows.Invited(1, Now + 4_000_000);
        Assert.True(rows.Update(new[] { Friend(1, "A") }, Now + 4_000_000));
        Assert.False(rows.Update(new[] { Friend(1, "A") }, Now + 6_000_000));
        Assert.True(rows.Update(new[] { Friend(1, "A") }, Now + 4_000_000 + FriendPickerRows.ResendMicros));
    }
}
