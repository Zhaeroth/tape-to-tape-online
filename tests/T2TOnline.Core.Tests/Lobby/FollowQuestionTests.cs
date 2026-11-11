using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The question a friend's failed match load asks, and the join it can lead to.</summary>
public class FollowQuestionTests
{
    private const long Second = 1_000_000;
    private const ulong Lobby = 109775240000000001;

    [Fact]
    public void A_failure_is_asked_once_as_soon_as_it_can_be_and_its_answers_map_to_the_buttons()
    {
        var question = new FollowQuestion();
        Assert.True(question.Failed(0));
        Assert.True(question.Waiting);

        // A loading transition, a scene load or another prompt: it waits.
        Assert.Equal(FollowQuestionStep.Nothing, question.Step(canAsk: false, Second));
        Assert.Equal(FollowQuestionStep.Ask, question.Step(canAsk: true, 2 * Second));
        Assert.True(question.Asking);
        Assert.False(question.Waiting);

        // The same failure said again while it's up asks nothing more.
        Assert.False(question.Failed(3 * Second));
        Assert.Equal(FollowQuestionStep.Nothing, question.Step(canAsk: true, 3 * Second));

        Assert.Equal(FollowAnswer.TryAgain, question.Answer(first: true, second: false));
        Assert.False(question.Asking);
        Assert.Equal(FollowAnswer.None, question.Answer(first: true, second: false)); // answered once

        question.Failed(10 * Second);
        question.Step(canAsk: true, 10 * Second);
        Assert.Equal(FollowAnswer.Leave, question.Answer(first: false, second: true));

        question.Failed(20 * Second);
        question.Step(canAsk: true, 20 * Second);
        Assert.Equal(FollowAnswer.None, question.Answer(first: false, second: false)); // B, or the game hid it
        Assert.Equal("3 asked (1 try again, 1 leave, 1 no answer), 0 said by a toast instead", question.Summary());
    }

    [Fact]
    public void A_failure_that_finds_no_moment_to_ask_is_told_by_the_toast_and_one_whose_reason_went_is_forgotten()
    {
        var question = new FollowQuestion();
        question.Failed(0);
        Assert.Equal(FollowQuestionStep.Nothing, question.Step(canAsk: false, FollowQuestion.WaitMicros - 1));
        Assert.Equal(FollowQuestionStep.Tell, question.Step(canAsk: false, FollowQuestion.WaitMicros));
        Assert.False(question.Waiting);
        Assert.Equal(FollowQuestionStep.Nothing, question.Step(canAsk: true, FollowQuestion.WaitMicros + 1));

        // The host's next match loads here before a moment came: nothing is asked.
        question.Failed(100 * Second);
        question.Cleared();
        Assert.Equal(FollowQuestionStep.Nothing, question.Step(canAsk: true, 101 * Second));

        // The prompt couldn't be shown after all: told instead, and a later failure asks again.
        question.Failed(200 * Second);
        Assert.Equal(FollowQuestionStep.Ask, question.Step(canAsk: true, 200 * Second));
        question.NotAsked();
        Assert.False(question.Asking);
        Assert.True(question.Failed(300 * Second));
        Assert.Equal("0 asked (0 try again, 0 leave, 0 no answer), 2 said by a toast instead", question.Summary());

        // A clock that goes back never tells early, and any times are taken without an overflow.
        question.Reset();
        question.Failed(long.MaxValue);
        Assert.Equal(FollowQuestionStep.Nothing, question.Step(canAsk: false, long.MinValue));
        question.Reset();
        question.Failed(long.MinValue);
        Assert.Equal(FollowQuestionStep.Tell, question.Step(canAsk: false, long.MaxValue));
    }

    [Fact]
    public void A_question_up_whose_reason_goes_is_over_at_once_with_no_answer_and_says_so_once()
    {
        var question = new FollowQuestion();
        Assert.Equal(FollowQuestionStep.Nothing, question.WhileAsking(reasonGone: true, promptUp: false)); // none up

        question.Failed(0);
        Assert.Equal(FollowQuestionStep.Nothing, question.WhileAsking(reasonGone: true, promptUp: false)); // waiting, not up
        Assert.Equal(FollowQuestionStep.Ask, question.Step(canAsk: true, Second));
        Assert.Equal(FollowQuestionStep.Nothing, question.WhileAsking(reasonGone: false, promptUp: true)); // up, its answer to come

        // The host's next match starts loading here with the prompt still up: closed, with no answer, and over, so the device
        // lent for it goes back before that match wants it (a close for its reason sends no answer of its own).
        Assert.Equal(FollowQuestionStep.Close, question.WhileAsking(reasonGone: true, promptUp: true));
        Assert.False(question.Asking);
        Assert.Equal(FollowQuestionStep.Nothing, question.WhileAsking(reasonGone: true, promptUp: true)); // once, not every frame
        Assert.Equal(FollowQuestionStep.Nothing, question.WhileAsking(reasonGone: true, promptUp: false));
        Assert.Equal(FollowAnswer.None, question.Answer(first: true, second: false)); // nothing answers it afterwards

        // The game hid it (a scene load) with no answer reaching here: over too.
        question.Failed(10 * Second);
        question.Step(canAsk: true, 10 * Second);
        Assert.Equal(FollowQuestionStep.Gone, question.WhileAsking(reasonGone: false, promptUp: false));
        Assert.False(question.Asking);
        Assert.Equal(FollowQuestionStep.Nothing, question.WhileAsking(reasonGone: false, promptUp: false));

        Assert.Equal("2 asked (0 try again, 0 leave, 2 no answer), 0 said by a toast instead", question.Summary());
        Assert.True(question.Failed(20 * Second)); // a later failure asks again
    }

    [Fact]
    public void Joining_again_presses_its_own_lobbys_row_also_when_it_reads_full_and_no_other()
    {
        var again = new JoinAgain();
        Assert.False(again.Fits(new BrowserRow { LobbyId = Lobby })); // nothing wanted

        again.Want(Lobby, "Sam", 0);
        Assert.False(again.Fits(null));
        Assert.True(again.Fits(new BrowserRow { LobbyId = Lobby, Block = BrowserBlock.None }));

        // Four seats taken counting the one the host keeps for this player while they're away: its own seat, not a stranger's.
        Assert.True(again.Fits(new BrowserRow { LobbyId = Lobby, Block = BrowserBlock.Full, Players = 4, MaxPlayers = 4 }));

        Assert.False(again.Fits(new BrowserRow { LobbyId = Lobby + 1, Block = BrowserBlock.None })); // another lobby
        Assert.False(again.Fits(new BrowserRow { LobbyId = Lobby, Block = BrowserBlock.Checking })); // its details still coming
        Assert.False(again.Fits(new BrowserRow { LobbyId = Lobby, Block = BrowserBlock.DifferentMod }));
        Assert.False(again.Fits(new BrowserRow { LobbyId = Lobby, Block = BrowserBlock.DifferentBuild }));
        Assert.False(again.Fits(new BrowserRow { LobbyId = Lobby, Block = BrowserBlock.Full, Fading = true })); // its host closed it

        again.Drop();
        Assert.False(again.Fits(new BrowserRow { LobbyId = Lobby, Block = BrowserBlock.None }));
    }

    [Fact]
    public void Joining_again_waits_for_the_lobbys_row_on_an_idle_join_list_and_happens_once()
    {
        var again = new JoinAgain();
        Assert.Equal(JoinAgainStep.Wait, again.Step(0, inSession: false, listUp: true, rowJoinable: true)); // nothing wanted

        again.Want(Lobby, "Sam", 0);
        Assert.Equal(Lobby, again.Lobby);

        // The game is still on its way home; then the list is up but the row's details haven't come.
        Assert.Equal(JoinAgainStep.Wait, again.Step(Second, inSession: false, listUp: false, rowJoinable: false));
        Assert.Equal(JoinAgainStep.Wait, again.Step(2 * Second, inSession: false, listUp: true, rowJoinable: false));
        Assert.Equal(JoinAgainStep.Join, again.Step(3 * Second, inSession: false, listUp: true, rowJoinable: true));
        Assert.Equal(0UL, again.Lobby);
        Assert.Equal("Sam", again.HostName);
        Assert.Equal(JoinAgainStep.Wait, again.Step(4 * Second, inSession: false, listUp: true, rowJoinable: true)); // once
    }

    [Fact]
    public void Joining_again_is_dropped_for_another_session_or_a_left_list_and_given_up_in_time()
    {
        var again = new JoinAgain();
        again.Want(Lobby, "Sam", 0);
        Assert.Equal(JoinAgainStep.Dropped, again.Step(Second, inSession: true, listUp: true, rowJoinable: true)); // an invite came first
        Assert.Equal(0UL, again.Lobby);

        again.Want(Lobby, "Sam", 0);
        again.Drop(); // Back on the Join list
        Assert.Equal(JoinAgainStep.Wait, again.Step(Second, inSession: false, listUp: true, rowJoinable: true));

        again.Want(Lobby, "Sam", 0);
        Assert.Equal(JoinAgainStep.Wait, again.Step(JoinAgain.WaitMicros - 1, inSession: false, listUp: true, rowJoinable: false));
        Assert.Equal(JoinAgainStep.GaveUp, again.Step(JoinAgain.WaitMicros, inSession: false, listUp: true, rowJoinable: false));
        Assert.Equal(0UL, again.Lobby);

        // Any times are taken without an overflow; a clock that goes back gives nothing up.
        again.Want(Lobby, "Sam", long.MaxValue);
        Assert.Equal(JoinAgainStep.Wait, again.Step(long.MinValue, inSession: false, listUp: false, rowJoinable: false));
        again.Want(Lobby, "Sam", long.MinValue);
        Assert.Equal(JoinAgainStep.GaveUp, again.Step(long.MaxValue, inSession: false, listUp: false, rowJoinable: false));
    }
}
