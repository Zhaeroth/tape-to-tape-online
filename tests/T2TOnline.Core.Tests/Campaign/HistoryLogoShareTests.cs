using System;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A host's old run history page from a custom-logo run shows the real logo on a friend (v1).</summary>
public class HistoryLogoShareTests
{
    private const long Second = 1_000_000;
    private const string LogoA = "custom_wolves.png";
    private const string LogoB = "custom_bears.png";

    [Fact]
    public void Only_a_custom_logo_of_a_page_the_host_rests_on_is_read()
    {
        var share = new HistoryLogoShare();
        share.PageShown(LogoA, 0);
        Assert.False(share.ShouldRead(HistoryLogoShare.SettleMicros - 1, out _)); // still turning pages
        Assert.True(share.ShouldRead(HistoryLogoShare.SettleMicros, out string name));
        Assert.Equal(LogoA, name);
        Assert.False(share.ShouldRead(10 * Second, out _)); // once for the page

        // The game's own logos are on every PC: nothing to send.
        share.PageShown("logo_default_01", 20 * Second);
        Assert.Empty(share.Page);
        Assert.False(share.ShouldRead(30 * Second, out _));
        share.PageShown(null, 30 * Second);
        share.PageShown("custom_x", 31 * Second); // not a PNG's name: a name no friend could be sent
        Assert.Empty(share.Page);
    }

    [Fact]
    public void Flipping_through_pages_sends_only_the_page_the_host_stops_on()
    {
        var share = new HistoryLogoShare();
        var read = new System.Collections.Generic.List<string>();
        long now = 0;
        for (int page = 0; page < 40; page++)
        {
            share.PageShown(page % 2 == 0 ? LogoA : LogoB, now);
            now += Second / 4; // a page every quarter second
            if (share.ShouldRead(now, out string name))
            {
                read.Add(name);
            }
        }

        Assert.Empty(read);
        now += 2 * Second; // rests on the last page
        Assert.True(share.ShouldRead(now, out string rested));
        Assert.Equal(LogoB, rested);
    }

    [Fact]
    public void The_same_page_again_does_not_start_over_and_a_page_seen_again_reads_the_same_logo_uncounted()
    {
        var share = new HistoryLogoShare();
        share.PageShown(LogoA, 0);
        share.PageShown(LogoA, 5 * Second); // the host's menu showed it again: the settle isn't restarted
        Assert.True(share.ShouldRead(5 * Second, out _));
        Assert.True(share.Admit(LogoA, 100_000).Sent);
        Assert.Equal((1, 100_000L), (share.Logos, share.Bytes));

        share.PageShown(LogoB, 6 * Second);
        share.PageShown(LogoA, 7 * Second); // back to the first
        Assert.True(share.ShouldRead(9 * Second, out string again));
        Assert.Equal(LogoA, again);
        Assert.True(share.Admit(LogoA, 100_000).Sent);
        Assert.Equal((1, 100_000L), (share.Logos, share.Bytes)); // not counted twice
        Assert.True(share.WasAdmitted(LogoA));
        Assert.False(share.WasAdmitted(LogoB));
    }

    [Fact]
    public void A_long_browse_cant_send_more_than_the_sessions_budget()
    {
        var share = new HistoryLogoShare();
        for (int i = 0; i < HistoryLogoShare.MaxLogos; i++)
        {
            Assert.True(share.Admit($"custom_logo{i}.png", 10_000).Sent);
        }

        HistoryLogoVerdict count = share.Admit("custom_one_more.png", 10_000);
        Assert.False(count.Sent);
        Assert.Contains("the most is 8", count.Why, StringComparison.Ordinal);

        // A refused logo's page isn't read again each time the host comes back to it.
        share.PageShown("custom_one_more.png", 0);
        Assert.False(share.ShouldRead(10 * Second, out _));

        var big = new HistoryLogoShare();
        Assert.True(big.Admit(LogoA, 4L * 1024 * 1024).Sent);
        Assert.True(big.Admit(LogoB, 4L * 1024 * 1024).Sent);
        Assert.True(big.Admit("custom_c.png", 4L * 1024 * 1024).Sent);
        HistoryLogoVerdict bytes = big.Admit("custom_d.png", 1024);
        Assert.False(bytes.Sent);
        Assert.Contains("12 MB", bytes.Why, StringComparison.Ordinal);
        Assert.False(big.Admit("custom_empty.png", 0).Sent);
        Assert.Equal(3, big.Logos);
    }

    [Fact]
    public void A_long_browse_leaves_a_friends_decoding_budget_half_full_for_its_matches()
    {
        // A friend decodes four logos of the largest size in a session, and its matches' logos come from the same budget.
        long largest = (long)PngHeader.MaxSide * PngHeader.MaxSide;
        var share = new HistoryLogoShare();
        Assert.True(share.Admit(LogoA, 1024, largest).Sent);
        Assert.True(share.Admit(LogoB, 1024, largest).Sent);
        HistoryLogoVerdict third = share.Admit("custom_c.png", 1024, largest);
        Assert.False(third.Sent);
        Assert.Contains("Mpx", third.Why, StringComparison.Ordinal);
        Assert.Equal(HistoryLogoShare.MaxPixels, share.Pixels);
        Assert.True(share.Pixels <= CustomLogoBudget.MaxPixels / 2);

        // Seen again, uncounted; refused logos aren't read again; a small one still fits only if there's room.
        Assert.True(share.Admit(LogoA, 1024, largest).Sent);
        Assert.Equal(HistoryLogoShare.MaxPixels, share.Pixels);
        share.PageShown("custom_c.png", 0);
        Assert.False(share.ShouldRead(10 * Second, out _));

        share.Clear();
        Assert.Equal(0, share.Pixels);
        Assert.True(share.Admit("custom_c.png", 1024, largest).Sent);
    }

    [Fact]
    public void A_logo_that_cant_be_had_is_not_read_again_and_hosting_starts_afresh()
    {
        var share = new HistoryLogoShare();
        share.PageShown(LogoA, 0);
        Assert.True(share.ShouldRead(2 * Second, out string name));
        share.Unreadable(name);
        share.PageShown(LogoB, 3 * Second);
        share.PageShown(LogoA, 4 * Second);
        Assert.False(share.ShouldRead(10 * Second, out _));

        share.PageGone();
        Assert.Empty(share.Page);
        share.Admit(LogoB, 5000);
        share.Clear();
        Assert.Equal((0, 0L), (share.Logos, share.Bytes));
        share.PageShown(LogoA, 0);
        Assert.True(share.ShouldRead(2 * Second, out _)); // hosting again: tried again
    }

    [Fact]
    public void What_counts_as_a_custom_logo_is_what_the_carrier_accepts()
    {
        Assert.True(HistoryLogoShare.IsCustom(LogoA));
        Assert.False(HistoryLogoShare.IsCustom("custom_"));
        Assert.False(HistoryLogoShare.IsCustom("custom_a.jpg"));
        Assert.False(HistoryLogoShare.IsCustom("custom_a/b.png"));
        Assert.False(HistoryLogoShare.IsCustom("custom_CON.png"));
        Assert.False(HistoryLogoShare.IsCustom("t2t_hostlogo_0123456789abcdef.png"));
        Assert.False(HistoryLogoShare.IsCustom(string.Empty));
        Assert.False(HistoryLogoShare.IsCustom(null));
        Assert.False(HistoryLogoShare.IsCustom("custom_" + new string('a', 200) + ".png"));
    }

    [Fact]
    public void A_friend_waits_for_the_pages_logo_and_shows_the_page_again_once_it_is_here()
    {
        var wait = new HistoryLogoWait();
        Assert.Equal(HistoryLogoWaitState.Idle, wait.Poll(0, here: false));

        wait.PageShown(LogoA, here: false, 0);
        Assert.Equal(LogoA, wait.Name);
        Assert.Equal(HistoryLogoWaitState.Waiting, wait.Poll(1, here: false));
        Assert.Equal(HistoryLogoWaitState.Waiting, wait.Poll(60, here: false));
        Assert.Equal(HistoryLogoWaitState.Arrived, wait.Poll(61, here: true));
        Assert.Equal(HistoryLogoWaitState.Idle, wait.Poll(62, here: true)); // once

        // The page shown again with the logo in doesn't wait for it again.
        wait.PageShown(LogoA, here: true, 62);
        Assert.Equal(HistoryLogoWaitState.Idle, wait.Poll(63, here: true));
    }

    [Fact]
    public void A_friends_wait_ends_with_a_page_that_has_no_custom_logo_and_gives_up_after_two_minutes()
    {
        var wait = new HistoryLogoWait();
        wait.PageShown(LogoA, false, 10);
        wait.PageShown(LogoA, false, 50); // the same page again: the wait keeps its start
        Assert.Equal(HistoryLogoWaitState.Waiting, wait.Poll(10 + HistoryLogoWait.GiveUpSeconds - 1, false));
        Assert.Equal(HistoryLogoWaitState.GaveUp, wait.Poll(10 + HistoryLogoWait.GiveUpSeconds, false));
        Assert.Equal(HistoryLogoWaitState.Idle, wait.Poll(300, false)); // said once

        wait.PageShown(LogoA, false, 400);
        wait.PageShown("logo_default_01", false, 401); // the host turned the page
        Assert.Equal(HistoryLogoWaitState.Idle, wait.Poll(402, false));
        Assert.Empty(wait.Name);

        wait.PageShown(LogoA, false, 500);
        wait.PageShown(LogoB, false, 510); // another custom logo: its own wait
        Assert.Equal(LogoB, wait.Name);
        Assert.Equal(HistoryLogoWaitState.Waiting, wait.Poll(10 + 510 - 1, false));
        wait.Clear();
        Assert.Equal(HistoryLogoWaitState.Idle, wait.Poll(520, true));
    }
}
