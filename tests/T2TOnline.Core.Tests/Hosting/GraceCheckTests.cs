using T2TOnline.Core.Hosting;

namespace T2TOnline.Core.Tests.Hosting;

/// <summary>The fight grace window's verdict for a held punch (D35), in its order.</summary>
public class GraceCheckTests
{
    private const long Now = 1_000_000;
    private const long Later = Now + 50_000;

    [Fact]
    public void A_victim_who_can_still_block_keeps_the_punch_waiting() =>
        Assert.Equal(GraceOutcome.Waiting, Check((false, false, false, true)).Outcome(true, Now, Later, true, true));

    [Fact]
    public void Every_victim_blocking_the_right_way_is_blocked() =>
        Assert.Equal(GraceOutcome.Blocked, Check((false, true, true, false), (false, true, true, false)).Outcome(true, Now, Later, true, true));

    [Fact]
    public void A_victim_blocking_the_other_way_is_partly_blocked() =>
        Assert.Equal(GraceOutcome.PartlyBlocked, Check((false, true, true, false), (false, true, false, false)).Outcome(true, Now, Later, true, true));

    [Fact]
    public void A_victim_that_s_gone_isn_t_blocking_and_can_t_block()
    {
        // Every victim gone: nothing to wait for, and nobody blocked.
        Assert.Equal(GraceOutcome.Unblockable, Check((true, false, false, false)).Outcome(true, Now, Later, true, true));

        // One gone, the other blocking: still not "blocked" by everyone.
        Assert.Equal(GraceOutcome.Unblockable, Check((true, false, false, false), (false, true, true, false)).Outcome(true, Now, Later, true, true));
    }

    [Fact]
    public void The_window_s_end_lets_it_go() =>
        Assert.Equal(GraceOutcome.TimedOut, Check((false, false, false, true)).Outcome(true, Later, Later, true, true));

    [Fact]
    public void Blocking_wins_over_the_window_s_end() =>
        Assert.Equal(GraceOutcome.Blocked, Check((false, true, true, false)).Outcome(true, Later + 1, Later, true, true));

    [Fact]
    public void A_victim_who_can_t_block_any_more_lets_it_go_early() =>
        Assert.Equal(GraceOutcome.Unblockable, Check((false, false, false, false), (false, true, true, false)).Outcome(true, Now, Later, true, true));

    [Fact]
    public void Switching_the_window_off_is_told_apart_from_the_session_ending()
    {
        Assert.Equal(GraceOutcome.SwitchedOff, Check((false, false, false, true)).Outcome(true, Now, Later, switchedOn: false, active: false));
        Assert.Equal(GraceOutcome.SessionEnded, Check((false, false, false, true)).Outcome(true, Now, Later, switchedOn: true, active: false));
    }

    [Fact]
    public void Switching_the_window_off_lets_a_waiting_punch_go_while_hosting_goes_on()
    {
        // Hosting with friends in stays active with the setting off (the brawl line still counts apexes): the setting
        // alone must end a wait.
        Assert.Equal(GraceOutcome.SwitchedOff, Check((false, false, false, true)).Outcome(true, Now, Later, switchedOn: false, active: true));
    }

    [Fact]
    public void A_punch_no_longer_live_is_interrupted_whatever_else() =>
        Assert.Equal(GraceOutcome.Interrupted, Check((false, true, true, false)).Outcome(false, Later + 1, Later, false, false));

    private static GraceCheck Check(params (bool Gone, bool Blocking, bool RightWay, bool CanBlock)[] victims)
    {
        var check = default(GraceCheck);
        foreach ((bool gone, bool blocking, bool rightWay, bool canBlock) in victims)
        {
            check.Victim(gone, blocking, rightWay, canBlock);
        }

        return check;
    }
}
