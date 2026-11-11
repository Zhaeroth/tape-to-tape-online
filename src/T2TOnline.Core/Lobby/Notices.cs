using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Lobby;

/// <summary>What the mod does with a notice for the player. Every site that used to put a line in the status box has one.</summary>
public enum NoticeKind
{
    /// <summary>Nothing on screen: the log has it, and the developer's status box. A routine line, or one the lobby's own screen already says.</summary>
    LogOnly,

    /// <summary>The game's own toast (<c>NotificationQueue</c>): nothing is lost if it's missed, and it never takes the focus.</summary>
    Toast,

    /// <summary>The game's own yes/no with one focused button: something the player must read, and may have to act on. A toast instead while a game is in play.</summary>
    Blocking,
}

/// <summary>
/// When a toast may show while a game is in play (the game's <c>ENotificationTiming</c>): a toast over a faceoff or a goal would cover play, so the game's own stops
/// are used. Outside a game it's always <see cref="Now"/>.
/// </summary>
public enum ToastTiming
{
    /// <summary>At once (the title, the lobby; in play, what no stop of the game can bring, or that's about this player's own hands): the screens queue it and show it.</summary>
    Now,

    /// <summary>At the next faceoff (<c>NextFaceoff</c>): who joined, dropped or came back is worth knowing about soon, and a faceoff is where it can be read.</summary>
    NextFaceoff,

    /// <summary>At the next stop of any kind the game makes (<c>All</c>): a faceoff, a goal's end, the map.</summary>
    NextStop,
}

/// <summary>Where a notice goes, once the router has weighed the notice against what's on screen (<see cref="Notices.Deliver"/>).</summary>
public enum NoticeDelivery
{
    /// <summary>Nowhere on screen: it's in the log (and the developer's box).</summary>
    None,

    /// <summary>A native toast.</summary>
    Toast,

    /// <summary>A native prompt with one button.</summary>
    Prompt,

    /// <summary>The status box: the native screens can't carry it, and a notice is never lost.</summary>
    StatusBox,
}

/// <summary>What the router knows when a notice comes.</summary>
/// <param name="ScreensReady">The native screens can show it: they're wired in, the <c>NativeLobby</c> setting is on and the shell is available for this game.</param>
/// <param name="InPlay">A prompt would sit over the game: a friend is following the host (their own UI is locked), or the host has a match on. Not the host on the map or at the title.</param>
/// <param name="LobbyUp">The lobby's own screen is up, and says who's here and what they're doing.</param>
/// <param name="AtTitle">The title is what's on screen: a game may be under way (a host's Start, a friend following its menus), but no match's stop comes here, and its queue goes with it.</param>
public readonly record struct NoticeContext(bool ScreensReady, bool InPlay, bool LobbyUp, bool AtTitle = false);

/// <summary>Every place the mod tells the player something (<see cref="Notices.Of"/>): what it's about, not where it's said.</summary>
public enum NoticeId
{
    // Players coming and going (the lobby's cards say it while the lobby is up).

    /// <summary>Host: a friend joined.</summary>
    FriendJoined,

    /// <summary>Host: a friend left for good.</summary>
    FriendLeft,

    /// <summary>Host: a friend dropped out; their seat is kept.</summary>
    FriendDropped,

    /// <summary>Host: a dropped friend is back.</summary>
    FriendBack,

    /// <summary>Friend: the host is back after the link dropped.</summary>
    HostBack,

    /// <summary>Friend: the link to the host dropped in play; this PC is trying to get back.</summary>
    HostReconnecting,

    /// <summary>Host: a friend the host removed.</summary>
    FriendRemoved,

    /// <summary>Host: a friend couldn't join; the line is <see cref="JoinFailures.HostToast"/>'s.</summary>
    FriendRefused,

    // The device.

    /// <summary>This PC's pad disconnected.</summary>
    PadLost,

    /// <summary>After a session the pad didn't go back to the player's game.</summary>
    PadNotBack,

    /// <summary>Joining needs a gamepad and none is connected.</summary>
    NoGamepad,

    /// <summary>The keyboard can't be used to join.</summary>
    NoKeyboard,

    /// <summary>A trigger of the pad stops short of the game's press point.</summary>
    TriggerShort,

    /// <summary>Reading the pad failed and the session ended.</summary>
    PadsFailed,

    // Steam, the lobby and invites.

    /// <summary>Host: Steam lost the lobby and a new one was made.</summary>
    LobbyReplaced,

    /// <summary>Host: out of the Steam lobby for now; new friends can't join.</summary>
    OutOfLobby,

    /// <summary>Host: a friend in the lobby is hosting too.</summary>
    HostingToo,

    /// <summary>Host: an invite went out.</summary>
    InviteSent,

    /// <summary>Host: an invite couldn't be sent.</summary>
    InviteFailed,

    /// <summary>A Steam invite waits for the player's run to be left.</summary>
    InviteHeldRun,

    /// <summary>A Steam invite waits for an idle title.</summary>
    InviteWaits,

    /// <summary>A Steam join was refused: this PC is hosting friends.</summary>
    CantJoinHosting,

    /// <summary>Joining a host's lobby, whose name is known.</summary>
    JoiningLobby,

    /// <summary>Joining a lobby, whose host isn't known yet.</summary>
    JoiningPlain,

    /// <summary>A Steam join waited too long for the game.</summary>
    SteamJoinLate,

    /// <summary>F7 found no hosting friend and hosts.</summary>
    HostingOwn,

    /// <summary>A joiner came into a game in play: they press their join button.</summary>
    PressToJoin,

    /// <summary>The same, for a friend on the keyboard: the keys.</summary>
    PressToJoinKeyboard,

    /// <summary>The other player's T2T Online is another release on the same protocol.</summary>
    ReleaseDiffers,

    /// <summary>A join was refused because the player's own run is in play.</summary>
    OwnRunInPlay,

    // Why Online can't be used (the Online button says the same, through JoinFailures).

    /// <summary>The mod doesn't know this game update; online is off.</summary>
    OnlineOff,

    /// <summary>Online is off after an error.</summary>
    OnlineBroken,

    /// <summary>A later start-up check switched online off and the session ended.</summary>
    OnlineSwitchedOff,

    /// <summary>Steam isn't connected.</summary>
    SteamOff,

    /// <summary>Steam hasn't started yet.</summary>
    SteamWaiting,

    /// <summary>A restored backup waits for a restart, so no session starts.</summary>
    RestoreBlocksSession,

    // The campaign and the saves: what must stay visible.

    /// <summary>A co-op run starts at the title screen.</summary>
    CoopAtTitle,

    /// <summary>The host's co-op run couldn't be saved.</summary>
    CoopSaveFailed,

    /// <summary>The host's co-op run saves again.</summary>
    CoopSaveBack,

    /// <summary>A run is held and isn't being saved.</summary>
    RunNotSaved,

    /// <summary>A friend's saves couldn't be guarded, so the host's run isn't loaded.</summary>
    GuardFailed,

    /// <summary>A friend joined during their own run: what it did since its last save isn't kept.</summary>
    OwnRunReplaced,

    /// <summary>Saves were restored: the game must restart.</summary>
    RestoredRestart,

    /// <summary>The backup before a session failed.</summary>
    BackupFailed,

    /// <summary>The check after a session found a save changed.</summary>
    SaveCheck,

    /// <summary>Steam Cloud's saves are newer than a restore's.</summary>
    CloudNewer,

    /// <summary>A restore stopped midway and can't be put right: the saves may be mixed.</summary>
    RestoreMixed,

    /// <summary>A restore stopped midway and is finished at the next start.</summary>
    RestoreWaits,

    /// <summary>A restore that stopped midway was finished at start-up.</summary>
    RestoreDone,

    /// <summary>A restore that stopped midway was undone at start-up.</summary>
    RestoreUndone,

    /// <summary>Friend: the host has shared progress off.</summary>
    SharingOff,

    /// <summary>Friend: the host turned shared progress back on.</summary>
    SharingOn,

    /// <summary>The menus shared with friends stopped working.</summary>
    MenusFailed,

    /// <summary>A part of the shared campaign failed and is off.</summary>
    CampaignPartOff,

    /// <summary>Friend: this game couldn't keep up with the host's (a screen, or the match).</summary>
    FollowFailed,

    /// <summary>Host: a friend's game couldn't keep up with this one.</summary>
    FriendFollowFailed,

    /// <summary>Friend: the player's own settings or remapper couldn't open while following.</summary>
    OwnMenuFailed,

    // Said by the log only: the lobby, the failure screen or the player's own press already says it.

    /// <summary>Hosting or leaving by F7 or the panel; the screens show the result.</summary>
    SessionLeft,

    /// <summary>A press that does nothing because a session is on.</summary>
    AlreadyInSession,

    /// <summary>The panel's list changed under a press.</summary>
    ListChanged,

    /// <summary>A join failed; the failure screen says why.</summary>
    JoinRefused,

    /// <summary>A friend's session ended; the failure screen says why.</summary>
    SessionOver,

    /// <summary>Joined, or back after a drop; the lobby's screen says so.</summary>
    LobbyJoined,

    /// <summary>A note on a join made by F7 while other games exist.</summary>
    JoiningNote,

    /// <summary>The one-PC test, a developer's tool.</summary>
    OnePcTest,

    /// <summary>The host's co-op slot became the game's run file.</summary>
    CoopSlotSwitched,

    /// <summary>A restore of co-op slots only; the panel says so.</summary>
    RestoredSlots,

    /// <summary>A host's menu is shown as text; the text panel says so.</summary>
    MenuAsText,

    /// <summary>The loader isn't the tested one; the start-up lines say so.</summary>
    LoaderNotice,

    /// <summary>A shared achievement unlocked; Steam shows its own.</summary>
    AchievementShared,

    /// <summary>The player's own menu, which they just asked for from the panel.</summary>
    OwnMenu,

    /// <summary>Host: a friend is in their own controls or settings.</summary>
    FriendInOwnMenu,

    /// <summary>A Steam invite to a lobby arrived (accepting it in Steam, or Join Lobby, joins). Said once per invite.</summary>
    InviteReceived,

    /// <summary>A friend came into a campaign run in play: whose, and where it is. Said once that game is on their screen, before what to press.</summary>
    JoinedRun,

    /// <summary>Old files of T2T Online are in the game folder, so online is off this session (the install check, D125): the files to delete.</summary>
    InstallLeftovers,

    /// <summary>Steam wouldn't keep connections relayed, so online is off this session (D133): the setting that allows direct connections.</summary>
    RelayOnlyRefused,

    /// <summary>
    /// A start without the guard on the game's loads from Steam Cloud (a game build the mod can't patch) found restored saves
    /// still as restored: the game's loads may put Steam Cloud's older copies back over them.
    /// </summary>
    CloudNotHeld,
}

/// <summary>How the router treats one notice: what it is, which word it says, and when it speaks.</summary>
/// <param name="Id">Which notice.</param>
/// <param name="Kind">What is done with it.</param>
/// <param name="Word">The word it says (for the player), or null for a log-only notice and for a dynamic one.</param>
/// <param name="Args">How many arguments the site passes: a word's <c>{0}</c>, <c>{1}</c>...; for a log-only notice the one text; for a dynamic one the one <see cref="LobbyLine"/>.</param>
/// <param name="InPlay">The timing of its toast while a game is in play.</param>
/// <param name="QuietInLobby">The lobby's own screen says it while it's up, so no toast on top.</param>
/// <param name="Dynamic">The site picks the line (a refusal's reason picks its word); the notice has no word of its own.</param>
public sealed record NoticeSpec(NoticeId Id, NoticeKind Kind, LobbyWord? Word, int Args, ToastTiming InPlay, bool QuietInLobby, bool Dynamic = false)
{
    /// <summary>True when a player may see it.</summary>
    public bool IsShown => Kind != NoticeKind.LogOnly;
}

/// <summary>
/// The one router for what the mod tells the player: every site names a <see cref="NoticeId"/>, and this says its kind and word, and where it goes now. A player
/// in normal play sees nothing about networking unless it's wrong, and then in the game's own toast or prompt; diagnosis loses nothing, as every notice is
/// logged, and a notice the native screens can't carry falls back to the status box.
/// </summary>
public static class Notices
{
    private static readonly NoticeSpec[] Table = BuildTable();

    /// <summary>Every notice, in <see cref="NoticeId"/>'s order.</summary>
    public static IReadOnlyList<NoticeSpec> All => Table;

    /// <summary>The notice's row. An id this version doesn't know gets a log-only row, never an exception.</summary>
    public static NoticeSpec Of(NoticeId id) => (uint)id < (uint)Table.Length ? Table[(int)id] : new NoticeSpec(id, NoticeKind.LogOnly, null, 1, ToastTiming.Now, false);

    /// <summary>
    /// Where a notice goes. Nowhere for a log-only one. Without the native screens, the status box: it's never lost. With them, nothing when the lobby's own screen says it
    /// (<see cref="NoticeSpec.QuietInLobby"/>), a prompt for a blocking one, and a toast for the rest, and for a blocking one while a game is in play.
    /// </summary>
    public static NoticeDelivery Deliver(NoticeSpec spec, NoticeContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Kind == NoticeKind.LogOnly)
        {
            return NoticeDelivery.None;
        }

        if (!context.ScreensReady)
        {
            return NoticeDelivery.StatusBox;
        }

        if (spec.QuietInLobby && context.LobbyUp)
        {
            return NoticeDelivery.None;
        }

        return spec.Kind == NoticeKind.Blocking && !context.InPlay ? NoticeDelivery.Prompt : NoticeDelivery.Toast;
    }

    /// <summary>When its toast shows: at once outside a game or at the title, the notice's own timing in play.</summary>
    public static ToastTiming TimingFor(NoticeSpec spec, NoticeContext context)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return context.InPlay && !context.AtTitle ? spec.InPlay : ToastTiming.Now;
    }

    /// <summary>
    /// The notice's line: its word with the site's arguments. A log-only notice has no word: the line is the site's text as a term of nothing, for the log. Never throws,
    /// and never fewer arguments than the word wants (a missing one is left as its mark by <see cref="LobbyWords.Fill"/>).
    /// </summary>
    public static LobbyLine LineOf(NoticeSpec spec, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Word == null ? new LobbyLine(string.Empty, args ?? Array.Empty<string>()) : new LobbyLine(spec.Word, args ?? Array.Empty<string>());
    }

    /// <summary>
    /// The notice in English, for the log and the status box (which draws in one font, whatever the game's language is). A log-only notice is the site's text; a dynamic one
    /// is the line the site made.
    /// </summary>
    public static string English(NoticeSpec spec, IReadOnlyList<string> args, LobbyLine? dynamicLine = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Dynamic)
        {
            return dynamicLine?.Text(0) ?? string.Empty;
        }

        if (spec.Word == null)
        {
            return args != null && args.Count > 0 ? args[0] ?? string.Empty : string.Empty;
        }

        return LobbyWords.Fill(spec.Word.Texts[0], args);
    }

    /// <summary>A blocking notice as the screens' one-button prompt: the line, one focused OK, and nowhere to go (the player stays where they were).</summary>
    public static FailureMessage Prompt(LobbyLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new FailureMessage(LobbyFailure.Notice, line, FailureButtons.Ok, FailureLanding.Stay);
    }

    private static NoticeSpec[] BuildTable()
    {
        var rows = new List<NoticeSpec>(80)
        {
            // Players coming and going. In a game they wait for the faceoff; in the lobby the cards say it.
            Row(NoticeId.FriendJoined, NoticeKind.Toast, LobbyWords.Joined, 1, ToastTiming.NextFaceoff, quiet: true),
            Row(NoticeId.FriendLeft, NoticeKind.Toast, LobbyWords.Left, 1, ToastTiming.NextFaceoff, quiet: true),
            Row(NoticeId.FriendDropped, NoticeKind.Toast, LobbyWords.ToastDropped, 1, ToastTiming.NextFaceoff, quiet: true),
            Row(NoticeId.FriendBack, NoticeKind.Toast, LobbyWords.ToastBack, 1, ToastTiming.NextFaceoff, quiet: true),
            // A friend's stops come from the host (its pops, over the link): with the link gone none comes, and the picture has stopped anyway, so these say it at once.
            Row(NoticeId.HostBack, NoticeKind.Toast, LobbyWords.ToastBack, 1, ToastTiming.Now, quiet: true),
            Row(NoticeId.HostReconnecting, NoticeKind.Toast, LobbyWords.ReconnectingTo, 1, ToastTiming.Now, quiet: true),
            Log(NoticeId.FriendRemoved),
            new NoticeSpec(NoticeId.FriendRefused, NoticeKind.Toast, null, 1, ToastTiming.NextStop, false, Dynamic: true),

            // The device.
            Row(NoticeId.PadLost, NoticeKind.Toast, LobbyWords.ToastController, 0, ToastTiming.Now), // their player stands still from now: it can't wait
            Row(NoticeId.PadNotBack, NoticeKind.Toast, LobbyWords.NoticePadNotBack, 0, ToastTiming.NextStop),
            Row(NoticeId.NoGamepad, NoticeKind.Toast, LobbyWords.NoticeNoGamepad, 0, ToastTiming.NextStop),
            Row(NoticeId.NoKeyboard, NoticeKind.Toast, LobbyWords.NoticeNoKeyboard, 0, ToastTiming.NextStop),
            Row(NoticeId.TriggerShort, NoticeKind.Toast, LobbyWords.NoticeTriggerShort, 0, ToastTiming.NextStop),
            Row(NoticeId.PadsFailed, NoticeKind.Blocking, LobbyWords.NoticePadsFailed, 0, ToastTiming.NextStop),

            // Steam, the lobby and invites.
            Row(NoticeId.LobbyReplaced, NoticeKind.Toast, LobbyWords.ToastLobbyReplaced, 0, ToastTiming.NextStop),
            Row(NoticeId.OutOfLobby, NoticeKind.Toast, LobbyWords.ToastOutOfLobby, 0, ToastTiming.NextStop),
            Row(NoticeId.HostingToo, NoticeKind.Toast, LobbyWords.ToastHostingToo, 1, ToastTiming.NextStop),
            Row(NoticeId.InviteSent, NoticeKind.Toast, LobbyWords.ToastInviteSent, 1, ToastTiming.NextStop),
            Row(NoticeId.InviteFailed, NoticeKind.Toast, LobbyWords.ToastInviteFailed, 1, ToastTiming.NextStop),
            Row(NoticeId.InviteHeldRun, NoticeKind.Toast, LobbyWords.ToastInviteHeldRun, 1, ToastTiming.NextStop),
            Row(NoticeId.InviteWaits, NoticeKind.Toast, LobbyWords.ToastInviteWaits, 1, ToastTiming.NextStop),
            Row(NoticeId.CantJoinHosting, NoticeKind.Toast, LobbyWords.ToastCantJoinHosting, 1, ToastTiming.NextStop),
            Row(NoticeId.JoiningLobby, NoticeKind.Toast, LobbyWords.JoiningLobby, 1, ToastTiming.NextStop),
            Row(NoticeId.JoiningPlain, NoticeKind.Toast, LobbyWords.Joining, 0, ToastTiming.NextStop),
            Row(NoticeId.SteamJoinLate, NoticeKind.Toast, LobbyWords.NoticeSteamJoinLate, 0, ToastTiming.NextStop),
            Row(NoticeId.HostingOwn, NoticeKind.Toast, LobbyWords.NoticeHostingOwn, 0, ToastTiming.NextStop),
            Row(NoticeId.PressToJoin, NoticeKind.Toast, LobbyWords.ToastPressToJoin, 0, ToastTiming.Now), // said once the game they came into is on their screen
            Row(NoticeId.PressToJoinKeyboard, NoticeKind.Toast, LobbyWords.ToastPressToJoinKeyboard, 0, ToastTiming.Now),
            Row(NoticeId.ReleaseDiffers, NoticeKind.Toast, LobbyWords.NoticeReleaseDiffers, 1, ToastTiming.NextStop),
            Row(NoticeId.OwnRunInPlay, NoticeKind.Blocking, LobbyWords.NoticeOwnRunInPlay, 0, ToastTiming.NextStop),

            // Why Online can't be used: the same words as the Online button's prompts.
            Row(NoticeId.OnlineOff, NoticeKind.Blocking, LobbyWords.FailOnlineOff, 0, ToastTiming.NextStop),
            Row(NoticeId.OnlineBroken, NoticeKind.Blocking, LobbyWords.FailOnlineBroken, 0, ToastTiming.NextStop),
            Row(NoticeId.OnlineSwitchedOff, NoticeKind.Blocking, LobbyWords.NoticeOnlineSwitchedOff, 0, ToastTiming.NextStop),
            Row(NoticeId.SteamOff, NoticeKind.Blocking, LobbyWords.FailSteam, 0, ToastTiming.NextStop),
            Row(NoticeId.SteamWaiting, NoticeKind.Blocking, LobbyWords.FailSteamWaiting, 0, ToastTiming.NextStop),
            Row(NoticeId.RestoreBlocksSession, NoticeKind.Blocking, LobbyWords.FailRestored, 0, ToastTiming.NextStop),

            // The campaign and the saves: what must stay visible.
            Row(NoticeId.CoopAtTitle, NoticeKind.Toast, LobbyWords.NoticeCoopAtTitle, 0, ToastTiming.NextStop),
            Row(NoticeId.CoopSaveFailed, NoticeKind.Blocking, LobbyWords.NoticeCoopSaveFailed, 0, ToastTiming.NextStop),
            Row(NoticeId.CoopSaveBack, NoticeKind.Toast, LobbyWords.NoticeCoopSaveBack, 0, ToastTiming.NextStop),
            Row(NoticeId.RunNotSaved, NoticeKind.Blocking, LobbyWords.NoticeRunNotSaved, 0, ToastTiming.NextStop),
            Row(NoticeId.GuardFailed, NoticeKind.Toast, LobbyWords.NoticeGuardFailed, 0, ToastTiming.NextStop),
            Row(NoticeId.OwnRunReplaced, NoticeKind.Toast, LobbyWords.NoticeOwnRunReplaced, 0, ToastTiming.NextStop),
            Row(NoticeId.RestoredRestart, NoticeKind.Blocking, LobbyWords.NoticeRestoredRestart, 0, ToastTiming.NextStop),
            Row(NoticeId.BackupFailed, NoticeKind.Blocking, LobbyWords.NoticeBackupFailed, 0, ToastTiming.NextStop),
            Row(NoticeId.SaveCheck, NoticeKind.Blocking, LobbyWords.NoticeSaveCheck, 0, ToastTiming.NextStop),
            Row(NoticeId.CloudNewer, NoticeKind.Blocking, LobbyWords.NoticeCloudNewer, 0, ToastTiming.NextStop),
            Row(NoticeId.RestoreMixed, NoticeKind.Blocking, LobbyWords.NoticeRestoreMixed, 0, ToastTiming.NextStop),
            Row(NoticeId.RestoreWaits, NoticeKind.Blocking, LobbyWords.NoticeRestoreWaits, 0, ToastTiming.NextStop),
            Row(NoticeId.RestoreDone, NoticeKind.Toast, LobbyWords.NoticeRestoreDone, 0, ToastTiming.NextStop),
            Row(NoticeId.RestoreUndone, NoticeKind.Toast, LobbyWords.NoticeRestoreUndone, 0, ToastTiming.NextStop),
            Row(NoticeId.SharingOff, NoticeKind.Toast, LobbyWords.NoticeSharingOff, 1, ToastTiming.NextStop),
            Row(NoticeId.SharingOn, NoticeKind.Toast, LobbyWords.NoticeSharingOn, 1, ToastTiming.NextStop),
            Row(NoticeId.MenusFailed, NoticeKind.Toast, LobbyWords.NoticeMenusFailed, 0, ToastTiming.NextStop),
            Row(NoticeId.CampaignPartOff, NoticeKind.Toast, LobbyWords.NoticeCampaignPartOff, 0, ToastTiming.NextStop),
            Row(NoticeId.FollowFailed, NoticeKind.Toast, LobbyWords.NoticeFollowFailed, 1, ToastTiming.NextStop),
            Row(NoticeId.FriendFollowFailed, NoticeKind.Toast, LobbyWords.NoticeFriendFollowFailed, 1, ToastTiming.NextStop),
            Row(NoticeId.OwnMenuFailed, NoticeKind.Toast, LobbyWords.NoticeOwnMenuFailed, 0, ToastTiming.NextStop),

            // The log only.
            Log(NoticeId.SessionLeft),
            Log(NoticeId.AlreadyInSession),
            Log(NoticeId.ListChanged),
            Log(NoticeId.JoinRefused),
            Log(NoticeId.SessionOver),
            Log(NoticeId.LobbyJoined),
            Log(NoticeId.JoiningNote),
            Log(NoticeId.OnePcTest),
            Log(NoticeId.CoopSlotSwitched),
            Log(NoticeId.RestoredSlots),
            Log(NoticeId.MenuAsText),
            Log(NoticeId.LoaderNotice),
            Log(NoticeId.AchievementShared),
            Log(NoticeId.OwnMenu),
            Log(NoticeId.FriendInOwnMenu),

            // Steam, again: the table goes in the ids' order, and new ids go at their end.
            Row(NoticeId.InviteReceived, NoticeKind.Toast, LobbyWords.ToastInvitedYou, 1, ToastTiming.NextStop),
            Row(NoticeId.JoinedRun, NoticeKind.Toast, LobbyWords.BannerJoinedRun, 3, ToastTiming.Now),
            Row(NoticeId.InstallLeftovers, NoticeKind.Blocking, LobbyWords.NoticeInstallLeftovers, 1, ToastTiming.NextStop),
            Row(NoticeId.RelayOnlyRefused, NoticeKind.Blocking, LobbyWords.FailRelayOnly, 0, ToastTiming.NextStop),
            Row(NoticeId.CloudNotHeld, NoticeKind.Blocking, LobbyWords.NoticeCloudNotHeld, 0, ToastTiming.NextStop),
        };

        return rows.ToArray();
    }

    private static NoticeSpec Row(NoticeId id, NoticeKind kind, LobbyWord word, int args, ToastTiming inPlay, bool quiet = false) => new(id, kind, word, args, inPlay, quiet);

    private static NoticeSpec Log(NoticeId id) => new(id, NoticeKind.LogOnly, null, 1, ToastTiming.Now, false);
}
