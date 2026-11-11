using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class ToastHoldTests
{
    private static NotificationEvent Toast(int timing, string id = "relic") =>
        NotificationEvent.Queued(new NotificationCopy { Type = 100, Timing = timing, Team = ToastTeam.Home, ObjectId = id });

    /// <summary>What the callbacks saw, in order ("show a", "pop 2", "expired a").</summary>
    private sealed class Log
    {
        public List<string> Seen { get; } = new();

        public HashSet<string> Ready { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);

        public ToastAttempt Show(NotificationEvent e, double waited)
        {
            string id = e.Copy!.ObjectId!;
            Seen.Add($"show {id}");
            return Ready.Contains(id) ? ToastAttempt.Shown : Broken.Contains(id) ? ToastAttempt.Failed : ToastAttempt.Later;
        }

        public void Pop(NotificationEvent e) => Seen.Add($"pop {e.Timing}");

        public void Expired(NotificationEvent e, double waited) => Seen.Add($"expired {e.Copy!.ObjectId} after {waited:0}");

        public void Drain(ToastHold hold, double now) => hold.Drain(now, Show, Pop, Expired);
    }

    [Fact]
    public void A_toast_made_during_the_hosts_match_load_waits_and_shows_once_this_pc_can_make_it()
    {
        var hold = new ToastHold();
        var log = new Log();
        Assert.False(hold.Due(0));
        Assert.True(hold.Hold(Toast(32, "hotcake"), now: 10));
        Assert.True(hold.Due(10));

        // This PC's match hasn't loaded: still not makeable, kept, and not tried again before the retry time.
        log.Drain(hold, 10);
        Assert.Equal(new[] { "show hotcake" }, log.Seen);
        Assert.Equal(1, hold.Count);
        Assert.False(hold.Due(10 + (ToastHold.RetrySeconds / 2)));

        // The match loaded: made and queued, nothing held.
        log.Ready.Add("hotcake");
        Assert.True(hold.Due(12));
        log.Drain(hold, 12);
        Assert.Equal(new[] { "show hotcake", "show hotcake" }, log.Seen);
        Assert.Equal(0, hold.Count);
        Assert.False(hold.Due(13));
    }

    [Fact]
    public void A_pop_that_would_show_a_held_toast_waits_behind_it_and_others_go_at_once()
    {
        var hold = new ToastHold();
        var log = new Log();
        Assert.True(hold.Hold(Toast(32 | 4, "hotcake"), 0));

        // The host popped timing 4 (the held toast's): it waits. Timing 2 shares nothing with it: it goes now.
        NotificationEvent popShared = NotificationEvent.Popped(4, keepGoing: false);
        NotificationEvent popOther = NotificationEvent.Popped(2, keepGoing: false);
        Assert.True(hold.Blocks(popShared));
        Assert.False(hold.Blocks(popOther));
        Assert.True(hold.Hold(popShared, 1));

        // A toast of the same timing behind the pop waits too (the host queued it after the pop showed the first); a clear never waits.
        Assert.True(hold.Blocks(Toast(4, "second")));
        Assert.False(hold.Blocks(Toast(1, "third")));
        Assert.False(hold.Blocks(NotificationEvent.Cleared()));
        Assert.True(hold.Hold(Toast(4, "second"), 1));

        // Nothing makeable yet: the first is tried, the pop and the toast behind it aren't.
        log.Drain(hold, 1);
        Assert.Equal(new[] { "show hotcake" }, log.Seen);
        Assert.Equal(3, hold.Count);

        // Both makeable now: in the host's order, the first toast, its pop, then the second.
        log.Ready.UnionWith(new[] { "hotcake", "second" });
        log.Drain(hold, 2);
        Assert.Equal(new[] { "show hotcake", "show hotcake", "pop 4", "show second" }, log.Seen);
        Assert.Equal(0, hold.Count);
    }

    [Fact]
    public void A_toast_that_never_becomes_makeable_is_dropped_after_the_hold_and_what_waited_behind_it_goes()
    {
        var hold = new ToastHold();
        var log = new Log();
        Assert.True(hold.Hold(Toast(8, "gone"), 0));
        Assert.True(hold.Hold(NotificationEvent.Popped(8, keepGoing: true), 0));
        log.Drain(hold, ToastHold.HoldSeconds - 1);
        Assert.Equal(2, hold.Count);

        log.Drain(hold, ToastHold.HoldSeconds + 1);
        Assert.Equal(new[] { "show gone", "show gone", "expired gone after 31", "pop 8" }, log.Seen);
        Assert.Equal(0, hold.Count);
    }

    [Fact]
    public void A_toast_that_fails_for_good_is_let_go_at_once()
    {
        var hold = new ToastHold();
        var log = new Log();
        log.Broken.Add("missing");
        Assert.True(hold.Hold(Toast(1, "missing"), 0));
        Assert.True(hold.Hold(NotificationEvent.Popped(1, false), 0));
        log.Drain(hold, 0);
        Assert.Equal(new[] { "show missing", "pop 1" }, log.Seen);
        Assert.Equal(0, hold.Count);
    }

    [Fact]
    public void A_clear_drops_everything_held_and_the_hold_is_bounded()
    {
        var hold = new ToastHold();
        Assert.False(hold.Hold(NotificationEvent.Cleared(), 0));
        for (int i = 0; i < ToastHold.MaxHeld - 1; i++)
        {
            Assert.True(hold.Hold(Toast(1, $"t{i}"), 0));
        }

        Assert.True(hold.Hold(NotificationEvent.Popped(1, false), 0));
        Assert.False(hold.Hold(Toast(1, "one too many"), 0));
        Assert.False(hold.Hold(NotificationEvent.Popped(1, false), 0));
        Assert.Equal(ToastHold.MaxHeld, hold.Count);

        Assert.Equal(ToastHold.MaxHeld - 1, hold.Clear());
        Assert.Equal(0, hold.Count);
        Assert.False(hold.Due(100));
    }

    [Fact]
    public void A_callback_that_throws_takes_only_its_own_event_with_it()
    {
        var hold = new ToastHold();
        Assert.True(hold.Hold(Toast(1, "bad"), 0));
        Assert.True(hold.Hold(Toast(2, "next"), 0));
        Assert.Throws<InvalidOperationException>(() => hold.Drain(0, (_, _) => throw new InvalidOperationException("broken"), _ => { }, (_, _) => { }));
        Assert.Equal(1, hold.Count);

        var log = new Log();
        log.Ready.Add("next");
        log.Drain(hold, 1);
        Assert.Equal(new[] { "show next" }, log.Seen);
        Assert.Equal(0, hold.Count);
    }

    [Fact]
    public void Timings_come_from_the_toast_or_the_pop_and_a_toast_with_none_never_holds_anything_back()
    {
        Assert.Equal(37, ToastHold.TimingOf(Toast(37)));
        Assert.Equal(6, ToastHold.TimingOf(NotificationEvent.Popped(6, false)));
        Assert.Equal(0, ToastHold.TimingOf(NotificationEvent.Cleared()));

        var hold = new ToastHold();
        Assert.True(hold.Hold(Toast(0, "untimed"), 0));
        Assert.False(hold.Blocks(NotificationEvent.Popped(-1, false)));
        Assert.False(hold.Blocks(Toast(5)));
    }
}
