using System;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The lines at a session's end say how each host menu ended up on each friend, and what the host
/// sent, each menu counted once by its last state, whatever it passed through.
/// </summary>
public class MenuTalliesTests
{
    private const string Relic = "RelicRewardMenu";
    private const string Event = "EventChoiceMenu";

    [Fact]
    public void A_friend_counts_each_menu_once_by_how_it_ended_up()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, Relic);
        tally.Real(1);
        tally.Closed(1);
        tally.Opened(2, Event);
        tally.Text(2, "your game is on another screen");
        tally.Closed(2);
        tally.Opened(3, "GameSettingsMenuV2");
        tally.Note(3);
        tally.Closed(3);
        tally.Late();

        Assert.True(tally.Any);
        Assert.Equal(
            "Menus: this session showed 3 of Alex's menus: 1 real (RelicRewardMenu 1), 1 as text (EventChoiceMenu: your game is on another screen 1), " +
            "1 as a note (GameSettingsMenuV2 1), 0 closed before anything showed here; 0 of the real ones fell back to text after opening; 1 opened after the host's had closed; 7 log line(s) only counted.",
            tally.Line("Alex", 7));
    }

    [Fact]
    public void The_host_match_menus_a_failed_load_keeps_off_the_screen_count_as_not_shown_with_their_reason()
    {
        const string why = "your game couldn't load the host's match";
        var tally = new FriendMenuTally();
        tally.Opened(1, "GameMenu");
        tally.Text(1, "your game is on another screen"); // first as text, then kept off the screen
        tally.Hidden(1, why);
        tally.Closed(1);
        tally.Opened(2, "GameMenu");
        tally.Hidden(2, why);
        tally.Closed(2);

        // A view kept off that later shows as text counts as text; a real one stays real.
        tally.Opened(3, "ControllerAssignmentMenu");
        tally.Hidden(3, why);
        tally.Text(3, "your game is on another screen");
        tally.Closed(3);
        tally.Opened(4, Relic);
        tally.Real(4);
        tally.Hidden(4, why);
        tally.Closed(4);

        Assert.Equal(
            "Menus: this session showed 4 of Alex's menus: 1 real (RelicRewardMenu 1), 1 as text (ControllerAssignmentMenu: your game is on another screen 1), " +
            "0 as a note, 0 closed before anything showed here, 2 not shown on purpose (GameMenu: your game couldn't load the host's match 2); " +
            "0 of the real ones fell back to text after opening; 0 opened after the host's had closed; 0 log line(s) only counted.",
            tally.Line("Alex", 0));
    }

    [Fact]
    public void A_wait_that_ends_real_is_real()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, Event);
        tally.Text(1, "the host's event choices are still on their way");
        tally.Real(1);
        tally.Closed(1);

        string line = tally.Line("Alex", 0);
        Assert.Contains("1 real (EventChoiceMenu 1)", line, StringComparison.Ordinal);
        Assert.Contains("0 as text", line, StringComparison.Ordinal);
        Assert.Contains("0 of the real ones fell back", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_menu_that_goes_to_text_counts_as_real_and_as_fallen_back()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, Event);
        tally.Real(1);
        tally.Text(1, "your game closed it");
        tally.Closed(1);

        string line = tally.Line("Alex", 0);
        Assert.Contains("1 real", line, StringComparison.Ordinal);
        Assert.Contains("1 of the real ones fell back to text after opening (EventChoiceMenu: your game closed it 1)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_menu_that_comes_back_after_a_wait_hasnt_fallen_back()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, Relic);
        tally.Real(1);
        tally.Text(1, "your game is still loading the host's newest run");
        tally.Real(1);
        tally.Closed(1);

        Assert.Contains("0 of the real ones fell back", tally.Line(string.Empty, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_menu_that_becomes_a_note_has_fallen_back()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, "UnlockableMenu");
        tally.Real(1);
        tally.Note(1);
        tally.Closed(1);

        Assert.Contains("1 of the real ones fell back to text after opening (UnlockableMenu: a note 1)", tally.Line(string.Empty, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void What_closes_before_anything_showed_is_said_apart()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, "GlossaryMenu"); // a warm-up: closed while it waits to open
        tally.Closed(1);
        tally.Opened(2, "ControllerAssignmentMenu"); // a wait's quiet second: its reason never showed
        tally.Text(2, "an exhibition screen");
        tally.Closed(2, nothingShown: true);

        string line = tally.Line("Alex", 0);
        Assert.Contains("0 as text", line, StringComparison.Ordinal);
        Assert.Contains("2 closed before anything showed here (ControllerAssignmentMenu 1, GlossaryMenu 1)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_ending_counts_what_is_still_open()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, Relic);
        tally.Real(1);
        tally.Opened(2, Event);
        tally.Text(2, "waiting");
        tally.CloseAll(serial => serial == 2);

        string line = tally.Line("the host", 0);
        Assert.Contains("showed 2 of the host's menus", line, StringComparison.Ordinal);
        Assert.Contains("1 real", line, StringComparison.Ordinal);
        Assert.Contains("1 closed before anything showed here (EventChoiceMenu 1)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_menu_opened_again_under_its_serial_ends_the_first()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, Relic);
        tally.Real(1);
        tally.Opened(1, Relic);
        tally.Real(1);
        tally.Closed(1);

        Assert.Contains("2 real (RelicRewardMenu 2)", tally.Line("Alex", 0), StringComparison.Ordinal);
    }

    [Fact]
    public void The_nurses_screen_counts_on_its_own()
    {
        var tally = new FriendMenuTally();
        tally.ScreenReal("NurseProcedureScreen");
        tally.ScreenText("NurseProcedureScreen", "the steps didn't come", wasReal: true);
        tally.ScreenText("NurseProcedureScreen", "no roster", wasReal: false);

        string line = tally.Line("Alex", 0);
        Assert.Contains("1 real (NurseProcedureScreen 1)", line, StringComparison.Ordinal);
        Assert.Contains("1 as text (NurseProcedureScreen: no roster 1)", line, StringComparison.Ordinal);
        Assert.Contains("1 of the real ones fell back to text after opening (NurseProcedureScreen: the steps didn't come 1)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Calls_for_a_menu_not_followed_do_nothing()
    {
        var tally = new FriendMenuTally();
        tally.Real(9);
        tally.Text(9, "why");
        tally.Note(9);
        tally.Closed(9);

        Assert.False(tally.Any);
    }

    [Fact]
    public void A_crafted_host_cant_grow_it()
    {
        var tally = new FriendMenuTally();
        for (uint serial = 0; serial < 10_000; serial++)
        {
            tally.Opened(serial, serial + new string('k', 500));
            tally.Text(serial, "why " + serial);
            if (serial % 2 == 0)
            {
                tally.Closed(serial);
            }
        }

        tally.CloseAll();
        string line = tally.Line("Alex", 0);
        Assert.Contains("others", line, StringComparison.Ordinal);
        Assert.True(line.Length < 16_000, $"the line is {line.Length} long");
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var tally = new FriendMenuTally();
        tally.Opened(1, Relic);
        tally.Late();
        tally.Clear();

        Assert.False(tally.Any);
    }

    [Fact]
    public void The_host_says_what_it_opened_and_sent()
    {
        var tally = new HostMenuTally();
        Assert.False(tally.Any);
        tally.Opened(Relic);
        tally.Opened(Relic);
        tally.Attachment("EventChoices");
        tally.Attachment("RunSummary");
        tally.NoFriend();

        Assert.True(tally.Any);
        Assert.Equal(
            "Menus: this session the host opened 2 menu(s) for friends (RelicRewardMenu 2); attachments: 2 (EventChoices 1, RunSummary 1); 1 went to no friend, 3 too big; 4 log line(s) only counted.",
            tally.Line(3, 4));
    }

    [Fact]
    public void The_host_says_a_full_board_only_when_there_was_one()
    {
        var tally = new HostMenuTally();
        tally.Opened(Relic);
        Assert.DoesNotContain("board", tally.Line(0, 0), StringComparison.Ordinal);
        tally.BoardFull();
        Assert.Contains("1 menu(s) not sent (the board was full)", tally.Line(0, 0), StringComparison.Ordinal);
        tally.Clear();
        Assert.False(tally.Any);
    }

    [Fact]
    public void Named_counts_keep_only_so_many_apart()
    {
        var counts = new NamedCounts();
        foreach (int i in Enumerable.Range(0, NamedCounts.MaxNames + 3))
        {
            counts.Add($"name{i:00}");
        }

        counts.Add("name00");
        Assert.Equal(NamedCounts.MaxNames + 4, counts.Total);
        Assert.StartsWith("name00 2, name01 1", counts.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("3 others", counts.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Named_counts_clean_a_name()
    {
        var counts = new NamedCounts();
        counts.Add("bad\nname");
        counts.Add(null);
        Assert.DoesNotContain("\n", counts.ToString(), StringComparison.Ordinal);
        counts.Clear();
        Assert.Equal(0, counts.Total);
        Assert.Equal(string.Empty, counts.ToString());
    }

    [Fact]
    public void A_burst_is_said_once_after_it_has_gone_quiet()
    {
        var burst = new QuietBurst(2_000_000);
        Assert.False(burst.TryReport(0, 0, out _));
        Assert.False(burst.TryReport(3, 100, out _));
        Assert.False(burst.TryReport(5, 200, out _));
        Assert.False(burst.TryReport(5, 2_000_100, out _));
        Assert.True(burst.TryReport(5, 2_000_200, out long count));
        Assert.Equal(5, count);
        Assert.False(burst.TryReport(5, 9_000_000, out _));

        // The next burst says only its own.
        Assert.False(burst.TryReport(7, 9_000_100, out _));
        Assert.True(burst.TryReport(7, 12_000_000, out count));
        Assert.Equal(2, count);
    }

    [Fact]
    public void A_counter_that_starts_again_isnt_a_burst()
    {
        var burst = new QuietBurst(1000);
        Assert.False(burst.TryReport(5, 0, out _));
        Assert.True(burst.TryReport(5, 5000, out _));
        Assert.False(burst.TryReport(0, 6000, out _));
        Assert.False(burst.TryReport(0, 90000, out _));
        Assert.False(burst.TryReport(2, 90001, out _));
        Assert.True(burst.TryReport(2, 99000, out long count));
        Assert.Equal(2, count);
    }
}
