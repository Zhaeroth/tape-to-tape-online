using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class ControllerFeedbackTests
{
    [Fact]
    public void Every_feedback_the_game_has_round_trips_within_a_menu_value()
    {
        // EControllerAssignmentFeedback: 23 types; EOptions: None 0, SkipAudio 1, SkipAnimation 2.
        for (int type = 0; type <= 22; type++)
        {
            foreach (bool negative in new[] { false, true })
            {
                for (int options = 0; options <= 3; options++)
                {
                    for (int count = 0; count < 6; count++)
                    {
                        Assert.True(ControllerFeedback.TryEncode(type, negative, options, count, out int value));
                        Assert.InRange(value, 0, MenuTarget.MaxIndex);
                        Assert.True(ControllerFeedback.TryDecode(count % 18, value, out ControllerFeedbackStep step));
                        Assert.Equal(new ControllerFeedbackStep(count % 18, type, negative, options), step);
                    }
                }
            }
        }
    }

    [Fact]
    public void The_exhibition_two_teams_are_targets_of_their_own()
    {
        Assert.True(ControllerFeedback.TryEncode(13, false, 0, 0, out int value));
        Assert.True(ControllerFeedback.TryDecode(ControllerFeedback.HomeTeamTarget, value, out ControllerFeedbackStep home));
        Assert.True(ControllerFeedback.TryDecode(ControllerFeedback.VisitorTeamTarget, value, out ControllerFeedbackStep visitor));
        Assert.Equal((18, 19), (home.Target, visitor.Target));
        Assert.False(ControllerFeedback.TryDecode(ControllerFeedback.VisitorTeamTarget + 1, value, out _));
    }

    [Fact]
    public void Two_equal_feedbacks_in_a_row_are_two_values()
    {
        // The menu board drops a value equal to the last one for the same target.
        Assert.True(ControllerFeedback.TryEncode(3, true, 0, 0, out int first));
        Assert.True(ControllerFeedback.TryEncode(3, true, 0, 1, out int second));
        Assert.NotEqual(first, second);
        Assert.True(ControllerFeedback.TryEncode(3, true, 0, 4, out int fifth));
        Assert.Equal(first, fifth);
    }

    [Fact]
    public void The_games_all_options_read_as_both_skips()
    {
        Assert.True(ControllerFeedback.TryEncode(5, false, -1, 0, out int value));
        Assert.True(ControllerFeedback.TryDecode(ControllerFeedback.GlobalTarget, value, out ControllerFeedbackStep step));
        Assert.Equal(ControllerFeedback.OptionBits, step.Options);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public void A_type_this_version_cant_carry_isnt_sent(int type) => Assert.False(ControllerFeedback.TryEncode(type, false, 0, 0, out _));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(20, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 1024)]
    public void A_step_out_of_range_is_refused(int target, int value)
    {
        Assert.False(ControllerFeedback.TryDecode(target, value, out ControllerFeedbackStep step));
        Assert.Equal(default, step);
    }
}
