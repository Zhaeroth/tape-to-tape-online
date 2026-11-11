namespace T2TOnline.Core.Protocol;

/// <summary>Constants shared by both ends of a session.</summary>
public static class ProtocolInfo
{
    /// <summary>Bumped on any change to a message layout. Peers with different versions can't join each other.</summary>
    public const byte Version = 9;

    /// <summary>
    /// Largest unreliable message, in bytes. Staying under the path MTU means a message never fragments,
    /// so losing one packet never loses more than one message. Larger ones still go (the host counts them:
    /// <c>Stream.StreamFriend.OversizeSends</c>).
    /// </summary>
    public const int MaxUnreliableMessageBytes = 1100;
}

/// <summary>
/// The three Steam networking channels a session uses. Which message goes on which is part of the design: messages that
/// refer to each other share a reliable channel, so they arrive in order (a match's setup, tables, events and end, D29).
/// </summary>
public enum Channel
{
    /// <summary>
    /// Unreliable, no delay: pad input, snapshots (keyframes included: a friend refuses a snapshot on any other channel),
    /// snapshot acknowledgements, stream reports, pings and pongs; the match stream's rumbles (a late one is worse than none)
    /// and looping sounds' moves (a lost one is superseded by the next).
    /// </summary>
    Realtime = 0,

    /// <summary>
    /// Reliable and ordered: session control (hello, welcome, reject, leave, pad unplugged and back); the match stream's
    /// setup, object and event tables, game events, sounds (looping ones' starts and stops too), HUD state and match end, and
    /// the friend's ready; host views, menus, follow reports, achievements and progress batches.
    /// </summary>
    Events = 1,

    /// <summary>
    /// Reliable, ordered and large, host to friend only: run checkpoints, the pieces of a host's custom content (a friend
    /// refuses one on any other channel) and a restarted friend's run copy. A host reads nothing on it: what reaches it
    /// there is dropped unread.
    /// </summary>
    Bulk = 2,
}
