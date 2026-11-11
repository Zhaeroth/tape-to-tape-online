namespace T2TOnline.Core.Protocol;

/// <summary>
/// Every message on the wire starts with one of these. Values are part of the protocol:
/// never renumber, only add, and bump <see cref="ProtocolInfo.Version"/> when a layout changes.
/// </summary>
public enum MessageType : byte
{
    /// <summary>Not a valid message.</summary>
    None = 0,

    // Session (1-15)

    /// <summary>Friend to host: versions, game hash, Steam ID, input device.</summary>
    Hello = 1,

    /// <summary>Host to friend: accepted, with slot, phase and host time.</summary>
    Welcome = 2,

    /// <summary>Host to friend: refused, with a reason.</summary>
    Reject = 3,

    /// <summary>Either way: clock and round-trip probe.</summary>
    Ping = 4,

    /// <summary>Either way: reply to <see cref="Ping"/>.</summary>
    Pong = 5,

    /// <summary>Either way: leaving, with a reason.</summary>
    Leave = 6,

    /// <summary>Host to friend: hashes of score, clock, possession and run, for drift checks.</summary>
    StateHash = 7,

    /// <summary>
    /// Either way (channel 1): a friend's pad family, the game's own device type (<see cref="PadDeviceMessage"/>). Friend to
    /// host: the friend's pad is now of this family (another pad was picked up mid-session), so the host's button prompts
    /// for them follow. Host to friend: the family the host reads the friend's virtual pad as, after a join, a rejoin and
    /// each change.
    /// </summary>
    PadDevice = 8,

    /// <summary>
    /// Host to friend (channel 1, protocol 7): the lobby's board, its whole shared state (<see cref="LobbyBoardMessage"/>),
    /// on change and at most four times a second per friend, and at once to a friend who just joined.
    /// </summary>
    LobbyBoard = 9,

    /// <summary>
    /// Friend to host (channel 1, protocol 7): the friend's ready mark, the Start epoch they follow, and their stage
    /// (<see cref="LobbyMemberMessage"/>). Rate-limited on the host; the sender is the transport's, so nobody can speak for another seat.
    /// </summary>
    LobbyMember = 10,

    // Input (16-31)

    /// <summary>Friend to host: newest pad frame plus the ones before it.</summary>
    PadInput = 16,

    /// <summary>
    /// Friend to host (channel 1): the friend's pad (or keyboard) is away: unplugged, or lent back to their own game's
    /// remapper or settings for a moment, with an optional reason byte (<see cref="PadDisconnectedMessage"/>).
    /// </summary>
    PadDisconnected = 17,

    /// <summary>Friend to host: the physical pad is back.</summary>
    PadReconnected = 18,

    /// <summary>
    /// Friend to host (channel 0): the generation of the friend's key table, then their newest keyboard-and-mouse frame plus
    /// the ones before it (<see cref="Input.KeyboardMouseCodec"/>).
    /// </summary>
    KeyboardMouseInput = 19,

    /// <summary>
    /// Friend to host (channel 1): the friend's own controls, a pad's or a keyboard's (<see cref="InputRemapsMessage"/>),
    /// sent after every welcome.
    /// </summary>
    InputRemaps = 20,

    /// <summary>Friend to host (v2+): own skater position, velocity and movement state.</summary>
    OwnerState = 21,

    /// <summary>
    /// Friend to host (channel 1): the friend's button prompt names on their own device family, from their own maps
    /// (<see cref="PromptNamesMessage"/>), sent after every welcome (after <see cref="InputRemaps"/>) and whenever their device
    /// or controls change: the host's prompts for their game player are named from it, as on their couch.
    /// </summary>
    PromptNames = 22,

    // Match stream (32-63)

    /// <summary>Host to friend: changed replay objects, camera pose and time scales.</summary>
    Snapshot = 32,

    /// <summary>
    /// Reserved, never sent: a keyframe (every replay object) goes as a <see cref="Snapshot"/> with the keyframe flag, on
    /// the realtime channel.
    /// </summary>
    Keyframe = 33,

    /// <summary>Host to friend: clock, scores, stamina, penalties, ability charge, control map.</summary>
    HudState = 34,

    /// <summary>Host to friend: a forwarded game event.</summary>
    GameEvent = 35,

    /// <summary>Host to friend: a one-shot sound played directly by gameplay code.</summary>
    SoundCall = 36,

    /// <summary>
    /// Host to friend: one of the host's looping gameplay sounds started (or still plays), moved or stopped
    /// (<see cref="SoundLoopMessage"/>): starts and stops on channel 1, moves on channel 0.
    /// </summary>
    SoundLoop = 37,

    /// <summary>Host to friend: an object was spawned.</summary>
    Spawn = 38,

    /// <summary>Host to friend: an object was removed.</summary>
    Despawn = 39,

    /// <summary>Host to friend (v2+): the host takes a friend's skater back.</summary>
    AuthorityToHost = 40,

    /// <summary>Host to friend (v2+): the friend's skater is theirs again, with the exact state to resume from.</summary>
    AuthorityToOwner = 41,

    /// <summary>Friend to host (v3): a shot, pass, check or punch the friend started.</summary>
    ActionClaim = 42,

    /// <summary>Friend to host (v3): the friend's skater picked up a loose puck.</summary>
    PickupClaim = 43,

    /// <summary>Host to friend (v3): a claim was accepted or rejected.</summary>
    ClaimResult = 44,

    /// <summary>Host to friend: network ids for replay objects, with the stable key that finds each one locally.</summary>
    ObjectTable = 45,

    /// <summary>Host to friend: the game's events by id, with their argument field names, read from the running build.</summary>
    EventTable = 46,

    /// <summary>Friend to host: the newest snapshot decoded (the base for the next delta), or a request for a keyframe.</summary>
    SnapshotAck = 47,

    /// <summary>Friend to host: how the friend's playout of the stream went over the last second (net overlay, grace window).</summary>
    StreamReport = 48,

    /// <summary>
    /// Host to friend (channel 0): the host's game vibrated a friend's virtual controller (<see cref="RumbleMessage"/>); that
    /// friend's own pad plays the game's nudge.
    /// </summary>
    Rumble = 49,

    // Match setup (64-79)

    /// <summary>Host to friend: mode, rules, teams, lineups, relics and slots for the next match.</summary>
    MatchSetup = 64,

    /// <summary>Friend to host: loaded and in client mode, with the setup hash.</summary>
    MatchReady = 65,

    /// <summary>Host to friend: the match with this setup hash is over (or the host left it).</summary>
    MatchEnd = 66,

    /// <summary>
    /// Host to friend: one piece of custom content that exists only on the host's PC (a custom team, skater, goalie or
    /// logo), for a match setup that names it or the host's campaign run team. An older friend counts it as malformed and
    /// drops it.
    /// </summary>
    CustomContent = 67,

    // Menus and campaign (80-111)

    /// <summary>Host to friend: a menu opened, with its data.</summary>
    MenuOpen = 80,

    /// <summary>Host to friend: a player navigated a menu.</summary>
    MenuNav = 81,

    /// <summary>Host to friend: a menu element was selected.</summary>
    MenuSelect = 82,

    /// <summary>Host to friend: a menu element was clicked, with its result.</summary>
    MenuClick = 83,

    /// <summary>Host to friend: where the host's game is (a screen and its details), for friends' games to follow.</summary>
    HostView = 84,

    /// <summary>Host to friend: the whole run, after every change.</summary>
    RunCheckpoint = 85,

    /// <summary>Friend to host: where the friend's game is in following the host's view.</summary>
    FollowReport = 86,

    /// <summary>
    /// Host to friend: data that goes with an open menu and doesn't fit its menu data (the run-end menu's run summary).
    /// An older friend counts it as malformed and drops it (its menu shows as text, as before).
    /// </summary>
    MenuAttachment = 87,

    /// <summary>
    /// Host to friend: the host's run toasts (a toast queued, the toasts of a timing shown, the queue cleared;
    /// <c>Campaign.NotificationMessage</c>), for friends' games to show the same ones with their own objects.
    /// </summary>
    Notification = 88,

    // Progress (112-127)

    /// <summary>Host to friend: an achievement was earned.</summary>
    AchievementUnlocked = 112,

    /// <summary>Host to friend: profile progress (unlock, stat, bench-quest step).</summary>
    ProgressEvent = 113,

    /// <summary>
    /// Friend to host: send these progress batches again (a gap on the friend's side, <c>Campaign.ProgressResendMessage</c>).
    /// An older host counts it as malformed and drops it (the friend gets them on a rejoin, as before).
    /// </summary>
    ProgressResend = 114,

    /// <summary>
    /// Host to friend: whether the host shares progress, and its newest batch (<c>Campaign.ProgressStatusMessage</c>). An
    /// older friend counts it as malformed and drops it.
    /// </summary>
    ProgressStatus = 115,

    /// <summary>
    /// Friend to host (channel 1, protocol 6): send me your copy of this shared run (<c>Campaign.ProgressRunRequestMessage</c>):
    /// the friend held it before its game restarted, and has a run's end to apply on it.
    /// </summary>
    ProgressRunRequest = 116,

    /// <summary>Host to friend (channel 2, protocol 6): the run's copy, or that it isn't kept (<c>Campaign.ProgressRunCopyMessage</c>).</summary>
    ProgressRunCopy = 117,
}
