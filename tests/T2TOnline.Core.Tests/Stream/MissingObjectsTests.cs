using System.Linq;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class MissingObjectsTests
{
    [Fact]
    public void An_event_whose_argument_is_an_object_not_found_here_goes_to_no_listener()
    {
        // SoundManager.PickupPuck(Forward) for a forward this PC couldn't map: every listener would get null.
        Assert.True(MissingObjects.SkipsEvent(new[] { (true, false) }));

        // Staggered(Skater, float): the skater missing, the number not an object.
        Assert.True(MissingObjects.SkipsEvent(new[] { (true, false), (false, false) }));
    }

    [Fact]
    public void An_argument_the_host_sent_as_nothing_is_the_games_own_null_and_is_delivered()
    {
        // PuckPossessionChange(null): a loose puck on the host too.
        Assert.False(MissingObjects.SkipsEvent(new[] { (false, false) }));
        Assert.False(MissingObjects.SkipsEvent(new[] { (true, true), (false, true) }));
        Assert.False(MissingObjects.SkipsEvent(System.Array.Empty<(bool, bool)>()));
    }

    [Fact]
    public void A_listener_that_threw_with_objects_missing_is_skipped_for_that_event_while_they_are()
    {
        var missing = new MissingObjects();
        Assert.False(missing.Skips("GameplayEvents.SkaterCheck", "T2T.Crowd.CoachEventListener", objectsMissing: true));

        Assert.True(missing.NoteFailure("GameplayEvents.SkaterCheck", "T2T.Crowd.CoachEventListener", objectsMissing: true));
        Assert.False(missing.NoteFailure("GameplayEvents.SkaterCheck", "T2T.Crowd.CoachEventListener", objectsMissing: true));

        Assert.True(missing.Skips("GameplayEvents.SkaterCheck", "T2T.Crowd.CoachEventListener", objectsMissing: true));
        Assert.False(missing.Skips("GameplayEvents.SkaterCheck", "T2T.Crowd.CoachEventListener", objectsMissing: false));
        Assert.False(missing.Skips("GameplayEvents.SkaterCheck", "SoundManager", objectsMissing: true));
        Assert.False(missing.Skips("GameplayEvents.GoalScored", "T2T.Crowd.CoachEventListener", objectsMissing: true));
        Assert.Equal(new[] { "GameplayEvents.SkaterCheck → T2T.Crowd.CoachEventListener" }, missing.Learned.ToArray());
    }

    [Fact]
    public void A_failure_with_every_object_here_isnt_learned()
    {
        // A listener that throws on its own (local data missing) keeps getting the event: the failure is the game's.
        var missing = new MissingObjects();
        Assert.False(missing.NoteFailure("GameplayEvents.GoalScored", "SoundManager", objectsMissing: false));
        Assert.False(missing.Skips("GameplayEvents.GoalScored", "SoundManager", objectsMissing: true));

        missing.NoteFailure("GameplayEvents.SkaterCheck", "SoundManager", objectsMissing: true);
        missing.Clear();
        Assert.False(missing.Skips("GameplayEvents.SkaterCheck", "SoundManager", objectsMissing: true));
    }
}
