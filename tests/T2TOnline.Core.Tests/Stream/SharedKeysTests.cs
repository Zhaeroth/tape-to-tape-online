using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class SharedKeysTests
{
    private static readonly ushort[] OldBall = { 41_207 };
    private static readonly ushort[] NewBall = { 9_310 };

    [Fact]
    public void A_ball_made_again_at_a_faceoff_gets_a_copy_of_its_own()
    {
        // The host's new ball has the old one's key and new component ids; the old copy answers to the old ids.
        Assert.Equal(SharedKeyStep.NewCopy, SharedKeys.Decide(sameMatch: false, isCopy: true, NewBall, OldBall));
    }

    [Fact]
    public void A_scene_object_with_other_ids_is_shared_but_never_driven()
    {
        Assert.Equal(SharedKeyStep.ShareUndriven, SharedKeys.Decide(sameMatch: false, isCopy: false, NewBall, OldBall));
    }

    [Fact]
    public void The_same_ids_share_one_object_as_before()
    {
        // The host announced the same object twice, or two of its objects line up with one here.
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(false, true, new ushort[] { 5, 6 }, new ushort[] { 5, 6 }));
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(false, false, new ushort[] { 5, 6 }, new ushort[] { 5, 6 }));
    }

    [Fact]
    public void The_recordings_own_match_and_recordings_without_ids_are_never_changed()
    {
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(sameMatch: true, isCopy: true, NewBall, OldBall));
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(sameMatch: true, isCopy: false, NewBall, OldBall));
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(false, true, null, OldBall));
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(false, true, System.Array.Empty<ushort>(), OldBall));
    }

    [Fact]
    public void An_object_whose_ids_here_are_unknown_is_shared()
    {
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(false, true, NewBall, null));
        Assert.Equal(SharedKeyStep.Share, SharedKeys.Decide(false, false, NewBall, null));
    }

    [Fact]
    public void Ids_compare_in_order_and_by_length()
    {
        Assert.True(SharedKeys.SameIds(new ushort[] { 1, 2, 3 }, new ushort[] { 1, 2, 3 }));
        Assert.False(SharedKeys.SameIds(new ushort[] { 1, 2, 3 }, new ushort[] { 3, 2, 1 }));
        Assert.False(SharedKeys.SameIds(new ushort[] { 1, 2 }, new ushort[] { 1, 2, 3 }));
        Assert.True(SharedKeys.SameIds(null, null));
        Assert.False(SharedKeys.SameIds(new ushort[] { 1 }, null));

        // A component more or less (another build's prefab) is a different object too.
        Assert.Equal(SharedKeyStep.NewCopy, SharedKeys.Decide(false, true, new ushort[] { 1, 2 }, new ushort[] { 1 }));
    }
}
