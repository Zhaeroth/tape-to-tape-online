using System;
using System.Globalization;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Diagnostics;

/// <summary>Why the game offers a problem report at the title (D140), least telling first: a later reason replaces an earlier one only when it tells more.</summary>
public enum ReportOfferReason
{
    /// <summary>Nothing went wrong: no offer.</summary>
    None,

    /// <summary>The last launch closed (a crash, or the game was ended) while an online game was on: its marker was left behind.</summary>
    LastLaunchInSession,

    /// <summary>An online session logged one or more ERROR lines while it was on, and ended.</summary>
    SessionError,

    /// <summary>An online session ended broken: online went off after an error in it.</summary>
    SessionBroken,

    /// <summary>The mod's runtime stopped after an error (online off for the rest of the launch).</summary>
    RuntimeStopped,
}

/// <summary>What the offer's marker file holds.</summary>
public enum ReportOfferMarker
{
    /// <summary>No file.</summary>
    None,

    /// <summary>An online game is on: a launch that ends without removing it closed during one.</summary>
    InSession,

    /// <summary>An offer is still due (its reason in the file): the next launch makes it.</summary>
    Pending,
}

/// <summary>
/// The offer of a problem report at the title after something went wrong (D140): when it is due, and what the marker file that carries it
/// across launches holds. Due at an idle title, once a launch, never in an online session, never while a check tour holds the title's prompts,
/// never over another prompt; after a session that ended broken or logged an ERROR, after the runtime stopped, or after a launch that closed
/// during an online game (its marker left behind). A reason that comes after this launch's offer is kept for the next launch. The marker holds a
/// kind, a reason and a UTC time, nothing else. Not thread-safe (the game's thread only).
/// </summary>
public sealed class ReportOffer
{
    /// <summary>The marker's file name, in the mod's data folder (<c>UserData/T2TOnline</c>).</summary>
    public const string MarkerFileName = "report-offer.txt";

    /// <summary>The longest marker text read; anything longer isn't one the mod wrote.</summary>
    public const int MaxMarkerLength = 256;

    private const string MarkerStart = "T2T Online report offer:";
    private const string InSessionWord = "in-session";
    private const string PendingWord = "pending";

    private long _errorsAtStart;

    /// <summary>The reason an offer is due for, or <see cref="ReportOfferReason.None"/>.</summary>
    public ReportOfferReason Pending { get; private set; }

    /// <summary>True when <see cref="Pending"/> came from the last launch's marker (no reason of this launch's has replaced it).</summary>
    public bool PendingFromLastLaunch { get; private set; }

    /// <summary>True once the offer was made this launch (answered either way): no other this launch.</summary>
    public bool OfferedThisLaunch { get; private set; }

    /// <summary>True while an online session is on (between <see cref="SessionStarted"/> and <see cref="SessionEnded"/>).</summary>
    public bool InSession { get; private set; }

    /// <summary>What the marker file should hold now: an online game on, else an offer still due, else nothing.</summary>
    public ReportOfferMarker Marker => InSession ? ReportOfferMarker.InSession : Pending != ReportOfferReason.None ? ReportOfferMarker.Pending : ReportOfferMarker.None;

    /// <summary>What the marker file should hold at a clean quit (the mod's own closing line): an offer still due, else nothing. A session on then ended with the game.</summary>
    public ReportOfferMarker MarkerAtQuit => Pending != ReportOfferReason.None ? ReportOfferMarker.Pending : ReportOfferMarker.None;

    /// <summary>
    /// The game is closing cleanly: an online session still on ends with it, as <see cref="SessionEnded"/> (<paramref name="broken"/>,
    /// <paramref name="errorsSoFar"/>): a session that broke or logged an ERROR is still offered, at the next launch. Returns what the marker file
    /// holds at the close (<see cref="MarkerAtQuit"/>).
    /// </summary>
    public ReportOfferMarker Closing(bool broken, long errorsSoFar)
    {
        _ = SessionEnded(broken, errorsSoFar);
        return MarkerAtQuit;
    }

    /// <summary>
    /// At start-up: the last launch's marker (<paramref name="text"/>, null when there was none). An online game's marker means the launch closed
    /// during it; a pending one carries its reason. Returns what is now due (<see cref="ReportOfferReason.None"/> for no marker or one the mod
    /// didn't write).
    /// </summary>
    public ReportOfferReason ReadMarker(string? text)
    {
        if (!TryParseMarker(text, out ReportOfferMarker kind, out ReportOfferReason reason))
        {
            return ReportOfferReason.None;
        }

        ReportOfferReason due = kind == ReportOfferMarker.InSession ? ReportOfferReason.LastLaunchInSession : reason;
        if (due != ReportOfferReason.None && Note(due))
        {
            PendingFromLastLaunch = true;
        }

        return due;
    }

    /// <summary>An online session started; <paramref name="errorsSoFar"/> is the log's ERROR count now.</summary>
    public void SessionStarted(long errorsSoFar)
    {
        InSession = true;
        _errorsAtStart = errorsSoFar;
    }

    /// <summary>
    /// The online session ended: <paramref name="broken"/> when online went off after an error in it, <paramref name="errorsSoFar"/> the log's
    /// ERROR count now. Returns the reason it gives (<see cref="ReportOfferReason.None"/> for a clean end). Nothing when no session was on.
    /// </summary>
    public ReportOfferReason SessionEnded(bool broken, long errorsSoFar)
    {
        if (!InSession)
        {
            return ReportOfferReason.None;
        }

        InSession = false;
        ReportOfferReason reason = broken ? ReportOfferReason.SessionBroken
            : errorsSoFar > _errorsAtStart ? ReportOfferReason.SessionError
            : ReportOfferReason.None;
        if (reason != ReportOfferReason.None)
        {
            Note(reason);
        }

        return reason;
    }

    /// <summary>The mod's runtime stopped after an error: a session on then is over, and the offer is due.</summary>
    public void RuntimeStopped()
    {
        InSession = false;
        Note(ReportOfferReason.RuntimeStopped);
    }

    /// <summary>
    /// What to offer now, or <see cref="ReportOfferReason.None"/>: something is due, the offer wasn't made this launch, and the moment is one:
    /// an idle title (<paramref name="idleTitle"/>), no online session (<paramref name="inSession"/>), no check tour holding the title's prompts
    /// (<paramref name="tourHolds"/>) and no prompt open (<paramref name="promptOpen"/>).
    /// </summary>
    public ReportOfferReason Due(bool idleTitle, bool inSession, bool tourHolds, bool promptOpen) =>
        Pending == ReportOfferReason.None || OfferedThisLaunch || !idleTitle || inSession || InSession || tourHolds || promptOpen
            ? ReportOfferReason.None
            : Pending;

    /// <summary>True when an offer is due this launch at the right moment (a cheap check before the moment is looked at).</summary>
    public bool Waiting => Pending != ReportOfferReason.None && !OfferedThisLaunch;

    /// <summary>The offer was made (answered either way, or closed): none other this launch, and nothing is due any more.</summary>
    public void Offered()
    {
        OfferedThisLaunch = true;
        Pending = ReportOfferReason.None;
        PendingFromLastLaunch = false;
    }

    /// <summary>
    /// The word the offer shows for <paramref name="reason"/>: <see cref="LobbyWords.ReportOfferClosed"/> for a launch that closed during an online game
    /// (nothing reported an error: the game may only have been closed), <see cref="LobbyWords.ReportOffer"/> ("ended with an error") for the reasons that
    /// are one. Null for <see cref="ReportOfferReason.None"/> and a value this table doesn't name, so a new reason is caught by the test that walks every
    /// reason, not shown with a wrong sentence; the caller falls back to the error word.
    /// </summary>
    public static LobbyWord? WordFor(ReportOfferReason reason) => reason switch
    {
        ReportOfferReason.LastLaunchInSession => LobbyWords.ReportOfferClosed,
        ReportOfferReason.SessionError or ReportOfferReason.SessionBroken or ReportOfferReason.RuntimeStopped => LobbyWords.ReportOffer,
        _ => null,
    };

    /// <summary>A reason in a few words for the log ("a session that ended broken").</summary>
    public static string Describe(ReportOfferReason reason) => reason switch
    {
        ReportOfferReason.None => "nothing",
        ReportOfferReason.LastLaunchInSession => "the last launch closed during an online game",
        ReportOfferReason.SessionError => "a session that logged an ERROR",
        ReportOfferReason.SessionBroken => "a session that ended broken",
        ReportOfferReason.RuntimeStopped => "the runtime stopped after an error",
        _ => reason.ToString(),
    };

    /// <summary>
    /// The marker's text for <paramref name="marker"/> (empty for <see cref="ReportOfferMarker.None"/>: no file): one line, the kind, for a pending
    /// one its reason, and the time in UTC; no names, no paths.
    /// </summary>
    public static string MarkerText(ReportOfferMarker marker, ReportOfferReason reason, DateTime utc)
    {
        DateTime at = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        return marker switch
        {
            ReportOfferMarker.InSession => string.Create(CultureInfo.InvariantCulture, $"{MarkerStart} {InSessionWord} {at:yyyy-MM-ddTHH:mm:ssZ}\n"),
            ReportOfferMarker.Pending when reason != ReportOfferReason.None =>
                string.Create(CultureInfo.InvariantCulture, $"{MarkerStart} {PendingWord} {ReasonWord(reason)} {at:yyyy-MM-ddTHH:mm:ssZ}\n"),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Reads a marker's text: its kind and, for a pending one, its reason. False for null, empty, oversized text, or text the mod didn't write
    /// (a pending marker with a reason this build doesn't know is read as the last launch closing during a game: something was due).
    /// </summary>
    public static bool TryParseMarker(string? text, out ReportOfferMarker kind, out ReportOfferReason reason)
    {
        kind = ReportOfferMarker.None;
        reason = ReportOfferReason.None;
        if (string.IsNullOrEmpty(text) || text.Length > MaxMarkerLength || !text.StartsWith(MarkerStart, StringComparison.Ordinal))
        {
            return false;
        }

        string[] words = text.Substring(MarkerStart.Length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        if (words[0] == InSessionWord)
        {
            kind = ReportOfferMarker.InSession;
            return true;
        }

        if (words[0] != PendingWord)
        {
            return false;
        }

        kind = ReportOfferMarker.Pending;
        reason = words.Length > 1 ? ReasonOf(words[1]) : ReportOfferReason.None;
        if (reason == ReportOfferReason.None)
        {
            reason = ReportOfferReason.LastLaunchInSession;
        }

        return true;
    }

    /// <summary>Keeps <paramref name="reason"/> when it tells more than the one due (or none is); true when it was kept.</summary>
    private bool Note(ReportOfferReason reason)
    {
        if (reason <= Pending && Pending != ReportOfferReason.None)
        {
            return false;
        }

        Pending = reason;
        PendingFromLastLaunch = false;
        return true;
    }

    private static string ReasonWord(ReportOfferReason reason) => reason switch
    {
        ReportOfferReason.LastLaunchInSession => "closed-in-session",
        ReportOfferReason.SessionError => "session-error",
        ReportOfferReason.SessionBroken => "session-broken",
        ReportOfferReason.RuntimeStopped => "runtime-stopped",
        _ => "none",
    };

    private static ReportOfferReason ReasonOf(string word) => word switch
    {
        "closed-in-session" => ReportOfferReason.LastLaunchInSession,
        "session-error" => ReportOfferReason.SessionError,
        "session-broken" => ReportOfferReason.SessionBroken,
        "runtime-stopped" => ReportOfferReason.RuntimeStopped,
        _ => ReportOfferReason.None,
    };
}
