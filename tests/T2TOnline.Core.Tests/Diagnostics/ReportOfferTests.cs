using System;
using System.Text.RegularExpressions;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The problem report's offer at the title: each reason, once a launch, never in a session or a tour, and the marker's life.</summary>
public class ReportOfferTests
{
    private static readonly DateTime At = new(2026, 11, 6, 21, 4, 5, DateTimeKind.Utc);

    private static ReportOfferReason DueAtIdleTitle(ReportOffer offer) => offer.Due(idleTitle: true, inSession: false, tourHolds: false, promptOpen: false);

    [Fact]
    public void A_fresh_launch_offers_nothing()
    {
        var offer = new ReportOffer();
        Assert.Equal(ReportOfferReason.None, DueAtIdleTitle(offer));
        Assert.False(offer.Waiting);
        Assert.Equal(ReportOfferMarker.None, offer.Marker);
        Assert.Equal(ReportOfferMarker.None, offer.MarkerAtQuit);
    }

    [Fact]
    public void A_clean_session_offers_nothing_and_clears_the_marker()
    {
        var offer = new ReportOffer();
        offer.SessionStarted(errorsSoFar: 3);
        Assert.Equal(ReportOfferMarker.InSession, offer.Marker);
        Assert.Equal(ReportOfferReason.None, offer.SessionEnded(broken: false, errorsSoFar: 3));
        Assert.Equal(ReportOfferReason.None, DueAtIdleTitle(offer));
        Assert.Equal(ReportOfferMarker.None, offer.Marker);
    }

    [Fact]
    public void A_session_that_ended_broken_is_offered()
    {
        var offer = new ReportOffer();
        offer.SessionStarted(0);
        Assert.Equal(ReportOfferReason.SessionBroken, offer.SessionEnded(broken: true, errorsSoFar: 1));
        Assert.Equal(ReportOfferReason.SessionBroken, DueAtIdleTitle(offer));
        Assert.Equal(ReportOfferMarker.Pending, offer.Marker);
    }

    [Fact]
    public void A_session_that_logged_an_error_is_offered_but_errors_before_it_are_not_counted()
    {
        var offer = new ReportOffer();
        offer.SessionStarted(errorsSoFar: 5);
        Assert.Equal(ReportOfferReason.SessionError, offer.SessionEnded(broken: false, errorsSoFar: 6));
        Assert.Equal(ReportOfferReason.SessionError, DueAtIdleTitle(offer));
    }

    [Fact]
    public void A_runtime_stop_is_offered_and_ends_the_session()
    {
        var offer = new ReportOffer();
        offer.SessionStarted(0);
        offer.RuntimeStopped();
        Assert.False(offer.InSession);
        Assert.Equal(ReportOfferReason.RuntimeStopped, DueAtIdleTitle(offer));
        Assert.Equal(ReportOfferReason.None, offer.SessionEnded(broken: true, errorsSoFar: 9)); // already over
    }

    [Fact]
    public void A_session_end_with_no_session_on_is_nothing()
    {
        var offer = new ReportOffer();
        Assert.Equal(ReportOfferReason.None, offer.SessionEnded(broken: true, errorsSoFar: 4));
        Assert.Equal(ReportOfferReason.None, DueAtIdleTitle(offer));
    }

    [Fact]
    public void The_last_launch_closing_in_a_session_is_offered()
    {
        var offer = new ReportOffer();
        string marker = ReportOffer.MarkerText(ReportOfferMarker.InSession, ReportOfferReason.None, At);
        Assert.Equal(ReportOfferReason.LastLaunchInSession, offer.ReadMarker(marker));
        Assert.True(offer.PendingFromLastLaunch);
        Assert.Equal(ReportOfferReason.LastLaunchInSession, DueAtIdleTitle(offer));
        Assert.Equal(ReportOfferMarker.Pending, offer.Marker);
    }

    [Theory]
    [InlineData(ReportOfferReason.SessionBroken)]
    [InlineData(ReportOfferReason.SessionError)]
    [InlineData(ReportOfferReason.RuntimeStopped)]
    [InlineData(ReportOfferReason.LastLaunchInSession)]
    public void A_pending_marker_carries_its_reason_to_the_next_launch(ReportOfferReason reason)
    {
        var offer = new ReportOffer();
        Assert.Equal(reason, offer.ReadMarker(ReportOffer.MarkerText(ReportOfferMarker.Pending, reason, At)));
        Assert.Equal(reason, DueAtIdleTitle(offer));
        Assert.True(offer.PendingFromLastLaunch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("T2T Online report offer:")]
    [InlineData("T2T Online report offer: running 2026-11-06T21:04:05Z")]
    [InlineData("Steam refused relay-only connections at 2026-11-06T21:04:05Z (UTC).")]
    public void A_marker_the_mod_did_not_write_is_nothing(string? text)
    {
        var offer = new ReportOffer();
        Assert.Equal(ReportOfferReason.None, offer.ReadMarker(text));
        Assert.Equal(ReportOfferReason.None, DueAtIdleTitle(offer));
    }

    [Fact]
    public void An_oversized_marker_is_refused()
    {
        string text = ReportOffer.MarkerText(ReportOfferMarker.InSession, ReportOfferReason.None, At).TrimEnd() + new string(' ', ReportOffer.MaxMarkerLength);
        Assert.False(ReportOffer.TryParseMarker(text, out _, out _));
    }

    [Fact]
    public void A_pending_marker_with_an_unknown_reason_still_offers()
    {
        var offer = new ReportOffer();
        Assert.Equal(ReportOfferReason.LastLaunchInSession, offer.ReadMarker("T2T Online report offer: pending something-new 2026-11-06T21:04:05Z\n"));
    }

    [Fact]
    public void Offered_once_a_launch()
    {
        var offer = new ReportOffer();
        offer.RuntimeStopped();
        Assert.Equal(ReportOfferReason.RuntimeStopped, DueAtIdleTitle(offer));
        offer.Offered();
        Assert.Equal(ReportOfferReason.None, DueAtIdleTitle(offer));
        Assert.False(offer.Waiting);
        Assert.Equal(ReportOfferMarker.None, offer.Marker);

        // A later error this launch isn't offered again now, but it's kept for the next launch.
        offer.SessionStarted(0);
        offer.SessionEnded(broken: true, errorsSoFar: 1);
        Assert.Equal(ReportOfferReason.None, DueAtIdleTitle(offer));
        Assert.Equal(ReportOfferMarker.Pending, offer.MarkerAtQuit);
        Assert.Equal(ReportOfferReason.SessionBroken, offer.Pending);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public void Never_away_from_an_idle_title_in_a_session_in_a_tour_or_over_a_prompt(bool idleTitle, bool inSession, bool tourHolds, bool promptOpen)
    {
        var offer = new ReportOffer();
        offer.RuntimeStopped();
        Assert.Equal(ReportOfferReason.None, offer.Due(idleTitle, inSession, tourHolds, promptOpen));
        Assert.True(offer.Waiting); // it waits for the moment
    }

    [Fact]
    public void Never_while_its_own_session_is_on()
    {
        var offer = new ReportOffer();
        offer.ReadMarker(ReportOffer.MarkerText(ReportOfferMarker.InSession, ReportOfferReason.None, At));
        offer.SessionStarted(0);
        Assert.Equal(ReportOfferReason.None, DueAtIdleTitle(offer));
        Assert.Equal(ReportOfferMarker.InSession, offer.Marker);
        offer.SessionEnded(broken: false, errorsSoFar: 0);
        Assert.Equal(ReportOfferReason.LastLaunchInSession, DueAtIdleTitle(offer)); // the last launch's is still due
        Assert.Equal(ReportOfferMarker.Pending, offer.Marker);
    }

    [Fact]
    public void A_reason_that_tells_more_replaces_one_that_tells_less_and_not_the_other_way()
    {
        var offer = new ReportOffer();
        offer.ReadMarker(ReportOffer.MarkerText(ReportOfferMarker.InSession, ReportOfferReason.None, At));
        offer.SessionStarted(0);
        offer.SessionEnded(broken: false, errorsSoFar: 1);
        Assert.Equal(ReportOfferReason.SessionError, offer.Pending);
        Assert.False(offer.PendingFromLastLaunch);
        offer.RuntimeStopped();
        Assert.Equal(ReportOfferReason.RuntimeStopped, offer.Pending);
        offer.SessionStarted(1);
        offer.SessionEnded(broken: true, errorsSoFar: 2);
        Assert.Equal(ReportOfferReason.RuntimeStopped, offer.Pending);
    }

    [Fact]
    public void The_marker_s_life_through_a_launch()
    {
        var offer = new ReportOffer();
        Assert.Equal(ReportOfferMarker.None, offer.Marker);
        offer.SessionStarted(0);
        Assert.Equal(ReportOfferMarker.InSession, offer.Marker);
        Assert.Equal(ReportOfferMarker.None, offer.MarkerAtQuit); // a clean quit mid-session clears it
        offer.SessionEnded(broken: false, errorsSoFar: 0);
        Assert.Equal(ReportOfferMarker.None, offer.Marker);
        offer.SessionStarted(0);
        offer.SessionEnded(broken: true, errorsSoFar: 1);
        Assert.Equal(ReportOfferMarker.Pending, offer.Marker);
        Assert.Equal(ReportOfferMarker.Pending, offer.MarkerAtQuit); // not offered yet: the next launch offers it
        offer.Offered();
        Assert.Equal(ReportOfferMarker.None, offer.Marker);
        Assert.Equal(ReportOfferMarker.None, offer.MarkerAtQuit);
    }

    [Fact]
    public void A_clean_close_during_a_session_that_logged_an_error_offers_it_at_the_next_launch()
    {
        var offer = new ReportOffer();
        offer.SessionStarted(errorsSoFar: 2);
        Assert.Equal(ReportOfferMarker.Pending, offer.Closing(broken: false, errorsSoFar: 3));
        Assert.Equal(ReportOfferReason.SessionError, offer.Pending);
        Assert.False(offer.InSession);

        var next = new ReportOffer();
        Assert.Equal(ReportOfferReason.SessionError, next.ReadMarker(ReportOffer.MarkerText(ReportOfferMarker.Pending, offer.Pending, At)));
    }

    [Fact]
    public void A_clean_close_during_a_session_that_broke_offers_it_at_the_next_launch()
    {
        var offer = new ReportOffer();
        offer.SessionStarted(0);
        Assert.Equal(ReportOfferMarker.Pending, offer.Closing(broken: true, errorsSoFar: 0));
        Assert.Equal(ReportOfferReason.SessionBroken, offer.Pending);
    }

    [Fact]
    public void A_clean_close_during_a_clean_session_clears_the_marker()
    {
        var offer = new ReportOffer();
        offer.SessionStarted(errorsSoFar: 4);
        Assert.Equal(ReportOfferMarker.None, offer.Closing(broken: false, errorsSoFar: 4));
        Assert.Equal(ReportOfferReason.None, offer.Pending);
    }

    [Fact]
    public void A_clean_close_with_no_session_keeps_what_is_due()
    {
        var offer = new ReportOffer();
        Assert.Equal(ReportOfferMarker.None, offer.Closing(broken: false, errorsSoFar: 7)); // errors outside a session offer nothing
        offer.RuntimeStopped();
        Assert.Equal(ReportOfferMarker.Pending, offer.Closing(broken: false, errorsSoFar: 9));
        Assert.Equal(ReportOfferReason.RuntimeStopped, offer.Pending);
    }

    [Fact]
    public void The_marker_text_holds_the_kind_the_reason_and_the_UTC_time_only()
    {
        Assert.Equal("T2T Online report offer: in-session 2026-11-06T21:04:05Z\n", ReportOffer.MarkerText(ReportOfferMarker.InSession, ReportOfferReason.SessionBroken, At));
        Assert.Equal("T2T Online report offer: pending runtime-stopped 2026-11-06T21:04:05Z\n", ReportOffer.MarkerText(ReportOfferMarker.Pending, ReportOfferReason.RuntimeStopped, At));
        Assert.Equal(string.Empty, ReportOffer.MarkerText(ReportOfferMarker.None, ReportOfferReason.RuntimeStopped, At));
        Assert.Equal(string.Empty, ReportOffer.MarkerText(ReportOfferMarker.Pending, ReportOfferReason.None, At));
    }

    [Fact]
    public void A_local_time_is_written_as_UTC()
    {
        DateTime local = At.ToLocalTime();
        Assert.Equal(ReportOffer.MarkerText(ReportOfferMarker.InSession, ReportOfferReason.None, At), ReportOffer.MarkerText(ReportOfferMarker.InSession, ReportOfferReason.None, local));
    }

    [Theory]
    [InlineData(ReportOfferMarker.InSession, ReportOfferReason.None)]
    [InlineData(ReportOfferMarker.Pending, ReportOfferReason.SessionError)]
    [InlineData(ReportOfferMarker.Pending, ReportOfferReason.SessionBroken)]
    [InlineData(ReportOfferMarker.Pending, ReportOfferReason.RuntimeStopped)]
    [InlineData(ReportOfferMarker.Pending, ReportOfferReason.LastLaunchInSession)]
    public void The_marker_text_reads_back(ReportOfferMarker kind, ReportOfferReason reason)
    {
        Assert.True(ReportOffer.TryParseMarker(ReportOffer.MarkerText(kind, reason, At), out ReportOfferMarker read, out ReportOfferReason readReason));
        Assert.Equal(kind, read);
        Assert.Equal(reason, readReason);
    }

    [Fact]
    public void Every_reason_is_described_in_words()
    {
        foreach (ReportOfferReason reason in Enum.GetValues<ReportOfferReason>())
        {
            string words = ReportOffer.Describe(reason);
            Assert.False(string.IsNullOrWhiteSpace(words));
            Assert.NotEqual(reason.ToString(), words);
        }
    }

    [Fact]
    public void The_sources_are_plain_and_name_no_key_or_address()
    {
        Assert.Equal(4, ReportSources.All.Length);
        Assert.Contains("Settings > Online", ReportSources.All);
        Assert.Contains("the Online menu", ReportSources.All);
        foreach (string source in ReportSources.All)
        {
            Assert.DoesNotContain("F1", source, StringComparison.Ordinal);
            Assert.DoesNotContain("panel", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("github", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(ReportOfferReason.LastLaunchInSession, "t2t/report_offer_closed")]
    [InlineData(ReportOfferReason.SessionError, "t2t/report_offer")]
    [InlineData(ReportOfferReason.SessionBroken, "t2t/report_offer")]
    [InlineData(ReportOfferReason.RuntimeStopped, "t2t/report_offer")]
    public void Each_reason_shows_the_word_that_is_true_for_it(ReportOfferReason reason, string key)
    {
        Assert.Equal(key, ReportOffer.WordFor(reason)?.Key);
    }

    [Fact]
    public void Every_reason_but_none_has_a_word_so_a_new_one_cannot_fall_through()
    {
        foreach (ReportOfferReason reason in Enum.GetValues<ReportOfferReason>())
        {
            if (reason == ReportOfferReason.None)
            {
                Assert.Null(ReportOffer.WordFor(reason));
            }
            else
            {
                Assert.NotNull(ReportOffer.WordFor(reason));
            }
        }

        Assert.Null(ReportOffer.WordFor((ReportOfferReason)99));
    }

    [Fact]
    public void Only_the_error_reasons_say_an_error_ended_the_game()
    {
        // The closed-during-a-game word says nothing about an error (the game may only have been closed); the error word names one.
        LobbyWord closed = LobbyWords.ReportOfferClosed;
        Assert.NotEqual(LobbyWords.ReportOffer.Texts[0], closed.Texts[0]);
        Assert.Contains("closed", closed.Texts[0]);
        Assert.DoesNotContain("error", closed.Texts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error", LobbyWords.ReportOffer.Texts[0]);
        Assert.Equal(WordFit.Sentence, closed.Fit);
        Assert.Same(closed, LobbyWords.Find("t2t/report_offer_closed"));
    }

    [Fact]
    public void The_closed_word_asks_the_same_question_in_every_language()
    {
        // Its second sentence is the error word's, word for word, in each language.
        for (int language = 0; language < LobbyWords.LanguageCount; language++)
        {
            string error = LobbyWords.ReportOffer.Texts[language];
            string closed = LobbyWords.ReportOfferClosed.Texts[language];
            string question = Regex.Match(error, @"^.*?[.。]\s*(.+)$").Groups[1].Value;
            Assert.NotEmpty(question);
            Assert.EndsWith(question, closed, StringComparison.Ordinal);
            Assert.NotEqual(error, closed);
        }
    }
}
