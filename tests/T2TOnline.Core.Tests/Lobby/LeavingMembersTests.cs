using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

public class LeavingMembersTests
{
    private const ulong Sam = 76561199000000001;
    private const ulong Riley = 76561199000000002;
    private const long Second = 1_000_000;

    [Fact]
    public void A_friend_who_quit_is_not_arriving_while_steam_still_lists_them()
    {
        // Sam quits in the lobby: the goodbye comes before Steam's member list drops them (a poll a second, after Steam's own
        // update). Meanwhile they'd be "arriving": on the board as Joining, their seat held.
        var leaving = new LeavingMembers();
        leaving.Left(Sam, 10 * Second);
        Assert.True(leaving.IsLeaving(Sam, 10 * Second));
        Assert.False(leaving.IsLeaving(Riley, 10 * Second));

        leaving.Prune(new[] { Sam, Riley }, 11 * Second);
        Assert.True(leaving.IsLeaving(Sam, 11 * Second));

        // Steam's list no longer names them: forgotten.
        leaving.Prune(new[] { Riley }, 12 * Second);
        Assert.False(leaving.IsLeaving(Sam, 12 * Second));
        Assert.Equal(0, leaving.Count);
    }

    [Fact]
    public void A_goodbye_lasts_only_its_linger_then_a_listed_friend_is_arriving_again()
    {
        var leaving = new LeavingMembers();
        leaving.Left(Sam, 0);
        Assert.True(leaving.IsLeaving(Sam, LeavingMembers.LingerMicros - 1));
        Assert.False(leaving.IsLeaving(Sam, LeavingMembers.LingerMicros));

        leaving.Prune(new[] { Sam }, LeavingMembers.LingerMicros);
        Assert.Equal(0, leaving.Count);
    }

    [Fact]
    public void A_hello_again_ends_the_goodbye_and_a_new_goodbye_starts_over()
    {
        var leaving = new LeavingMembers();
        leaving.Left(Sam, 0);
        leaving.Back(Sam);
        Assert.False(leaving.IsLeaving(Sam, Second));

        leaving.Left(Sam, 5 * Second);
        Assert.True(leaving.IsLeaving(Sam, 5 * Second + LeavingMembers.LingerMicros - 1));
        leaving.Clear();
        Assert.False(leaving.IsLeaving(Sam, 6 * Second));
    }

    [Fact]
    public void Held_goodbyes_are_capped_the_oldest_going_first_and_no_id_or_time_throws()
    {
        var leaving = new LeavingMembers();
        for (int i = 0; i < LeavingMembers.MaxMembers + 5; i++)
        {
            leaving.Left(76561199000000100UL + (ulong)i, i);
        }

        Assert.Equal(LeavingMembers.MaxMembers, leaving.Count);
        Assert.False(leaving.IsLeaving(76561199000000100UL, LeavingMembers.MaxMembers + 5));
        Assert.True(leaving.IsLeaving(76561199000000100UL + LeavingMembers.MaxMembers + 4, LeavingMembers.MaxMembers + 5));

        // Nobody is id 0; extreme times are taken without an overflow.
        leaving.Clear();
        leaving.Left(0, 0);
        Assert.Equal(0, leaving.Count);
        leaving.Left(Sam, long.MinValue);
        Assert.False(leaving.IsLeaving(Sam, long.MaxValue));
        leaving.Left(Riley, long.MaxValue);
        Assert.True(leaving.IsLeaving(Riley, long.MaxValue));
        leaving.Prune(System.Array.Empty<ulong>(), 0);
        Assert.Equal(0, leaving.Count);
    }
}
