using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Stream;

/// <summary>How complete a collision rebuilt on a friend's PC is (<see cref="CollisionCheck"/>).</summary>
public enum CollisionRebuild
{
    /// <summary>Every member the host's collision had was found here.</summary>
    Complete,

    /// <summary>A rigidbody the host's collision had isn't here (its colliders are).</summary>
    MissingBody,

    /// <summary>A collider the host's collision had isn't here: a contact's other collider reads as nothing.</summary>
    MissingCollider,
}

/// <summary>
/// The rule for a puck collision (or a hit on the post) whose objects aren't all on a friend's PC. The host sends the
/// collision's four objects (body, other body, collider, other collider); a friend
/// rebuilds it around its own. <c>Events.GameplayEventsListener.OnPuckCollision</c> hands the collision to a UnityEvent
/// whose <c>CollisionVFX.Instantiate</c> reads the first contact's other collider and throws when there is none (its
/// filters need the real collider's name and layer, so no stand-in can do), and a UnityEvent stops at the first listener
/// that throws. The host's collision always has its colliders, so with one missing here that listener is skipped for
/// this collision (counted, its effect isn't the host's to show here); every other listener still gets it, as the game's
/// own safe invocation would give it to them.
/// </summary>
public static class CollisionCheck
{
    /// <summary>The listener class that hands a collision to the effects needing its colliders.</summary>
    public const string ColliderListener = "Events.GameplayEventsListener";

    /// <summary>
    /// How complete a rebuilt collision is, from its four members in the host's order (body, other body, collider, other
    /// collider): whether each was sent as an object (not a null) and whether it was found here.
    /// </summary>
    public static CollisionRebuild Of(ReadOnlySpan<bool> sent, ReadOnlySpan<bool> found)
    {
        if (sent.Length != 4 || found.Length != 4)
        {
            throw new ArgumentException("A collision has four members.");
        }

        if ((sent[2] && !found[2]) || (sent[3] && !found[3]))
        {
            return CollisionRebuild.MissingCollider;
        }

        return (sent[0] && !found[0]) || (sent[1] && !found[1]) ? CollisionRebuild.MissingBody : CollisionRebuild.Complete;
    }

    /// <summary>Whether <paramref name="owner"/>'s listener is skipped for a delivery whose collisions are as <paramref name="rebuilds"/> says.</summary>
    public static bool Skips(string owner, IReadOnlyCollection<CollisionRebuild> rebuilds)
    {
        ArgumentNullException.ThrowIfNull(rebuilds);
        if (!string.Equals(owner, ColliderListener, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (CollisionRebuild rebuild in rebuilds)
        {
            if (rebuild == CollisionRebuild.MissingCollider)
            {
                return true;
            }
        }

        return false;
    }
}
