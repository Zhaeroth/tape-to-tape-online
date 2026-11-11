using System;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>
/// A puck collision rebuilt on a friend's PC without all of the host's objects (the game's CollisionVFX.Instantiate
/// throws on a collision whose hit collider isn't found).
/// </summary>
public class CollisionCheckTests
{
    private static readonly bool[] AllSent = { true, true, true, true };

    [Fact]
    public void Every_member_found_is_complete()
    {
        Assert.Equal(CollisionRebuild.Complete, CollisionCheck.Of(AllSent, new[] { true, true, true, true }));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_missing_collider_is_missing_whatever_else_is_found(int collider)
    {
        bool[] found = { true, true, true, true };
        found[collider] = false;
        Assert.Equal(CollisionRebuild.MissingCollider, CollisionCheck.Of(AllSent, found));
        found[0] = false;
        Assert.Equal(CollisionRebuild.MissingCollider, CollisionCheck.Of(AllSent, found));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_missing_body_alone_leaves_the_colliders(int body)
    {
        bool[] found = { true, true, true, true };
        found[body] = false;
        Assert.Equal(CollisionRebuild.MissingBody, CollisionCheck.Of(AllSent, found));
    }

    [Fact]
    public void A_member_the_host_had_as_nothing_isn_t_missing_here()
    {
        // The boards have no rigidbody: the host sent null for it, and null here is the same collision.
        Assert.Equal(CollisionRebuild.Complete, CollisionCheck.Of(new[] { true, false, true, false }, new[] { true, false, true, false }));
    }

    [Fact]
    public void A_collision_has_four_members() =>
        Assert.Throws<ArgumentException>(() => CollisionCheck.Of(new[] { true, true }, new[] { true, true }));

    [Fact]
    public void Only_the_collider_listener_is_skipped_and_only_for_a_missing_collider()
    {
        Assert.True(CollisionCheck.Skips(CollisionCheck.ColliderListener, new[] { CollisionRebuild.Complete, CollisionRebuild.MissingCollider }));
        Assert.False(CollisionCheck.Skips(CollisionCheck.ColliderListener, new[] { CollisionRebuild.MissingBody }));
        Assert.False(CollisionCheck.Skips(CollisionCheck.ColliderListener, Array.Empty<CollisionRebuild>()));
        Assert.False(CollisionCheck.Skips("SoundManager", new[] { CollisionRebuild.MissingCollider }));
    }
}
