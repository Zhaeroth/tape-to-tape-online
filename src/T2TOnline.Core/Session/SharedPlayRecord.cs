using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace T2TOnline.Core.Session;

/// <summary>
/// A friend's record of the host's play it shared, per hosting session (<see cref="Protocol.WelcomeMessage.HostingSession"/>,
/// D34, D135): the numbers of the host's matches its game played (<see cref="MaxMatches"/> newest), and whether it held
/// the host's co-op run; the newest <see cref="MaxKept"/> sessions kept. Saved as a small text file by the game glue, so
/// it outlives this PC's game: after a restart and a rejoin of the same hosting session, the moments the host's ledger
/// says this PC is owed unlock for the play it really shared. It names no host: the glue keeps each hosting session under
/// this PC's own key for it and its host (<see cref="Campaign.HostKeys.SessionTag"/>), so another host naming the same
/// session finds nothing here. Lines of the format before it (with the host's ID) still read, without the ID
/// (<see cref="HadHostIds"/>: write it again). Parsing is tolerant (a bad line is skipped). Game-independent. Not
/// thread-safe (one game thread).
/// Each match is kept with what this PC saw of it (<see cref="MatchView"/>), so a moment owed for it is judged after a
/// restart as it would have been live.
/// <code>
/// session &lt;hex16&gt; at &lt;utc, round-trip format&gt; run &lt;0|1&gt; matches &lt;n:view,n:view,...|-&gt;
/// </code>
/// </summary>
public sealed class SharedPlayRecord
{
    /// <summary>Hosting sessions remembered (newest kept): a friend is in one at a time, and a restart rejoins the latest.</summary>
    public const int MaxKept = 8;

    /// <summary>Matches remembered per hosting session (the highest numbers kept): a run is about fifteen.</summary>
    public const int MaxMatches = 64;

    private static readonly char[] Blanks = { ' ', '\t', '\r' };
    private readonly List<Played> _sessions = new();

    /// <summary>Hosting sessions remembered.</summary>
    public int Count => _sessions.Count;

    /// <summary>True when the text read held lines of the old format, with the host's ID: the record should be written again.</summary>
    public bool HadHostIds { get; private set; }

    /// <summary>True when this PC shared play in hosting session <paramref name="session"/>.</summary>
    public bool Has(ulong session) => Find(session) != null;

    /// <summary>True when this PC played the host's match <paramref name="match"/> in hosting session <paramref name="session"/>.</summary>
    public bool PlayedMatch(ulong session, int match) => Find(session)?.IndexOf(match) >= 0;

    /// <summary>
    /// What this PC saw of the host's match <paramref name="match"/> in hosting session <paramref name="session"/>
    /// (<see cref="MatchView.Unknown"/> when it didn't play it, or played it before the kind was kept).
    /// </summary>
    public MatchView ViewOf(ulong session, int match)
    {
        Played? played = Find(session);
        int at = played?.IndexOf(match) ?? -1;
        return at >= 0 ? played!.Matches[at].View : MatchView.Unknown;
    }

    /// <summary>The highest match number this PC played in hosting session <paramref name="session"/> (0: none).</summary>
    public int NewestMatch(ulong session) => Find(session) is { Matches.Count: > 0 } played ? played.Matches[^1].Match : 0;

    /// <summary>The matches this PC played in hosting session <paramref name="session"/>.</summary>
    public int MatchCount(ulong session) => Find(session)?.Matches.Count ?? 0;

    /// <summary>True when this PC held the host's co-op run in hosting session <paramref name="session"/>.</summary>
    public bool HeldRun(ulong session) => Find(session)?.Run == true;

    /// <summary>
    /// This PC played the host's match <paramref name="match"/> in hosting session <paramref name="session"/> (seen at
    /// <paramref name="utc"/>). False when it was known already, or isn't a session or a match (0).
    /// </summary>
    public bool NoteMatch(ulong session, int match, DateTime utc) => NoteView(session, match, MatchView.Unknown, utc);

    /// <summary>
    /// This PC saw <paramref name="view"/> of the host's match <paramref name="match"/> in hosting session
    /// <paramref name="session"/> (at <paramref name="utc"/>), merged into what it saw of it before (<see cref="MatchView.With"/>).
    /// True when the record changed (a new match, or what it keeps of one); false otherwise, or for no session or match (0).
    /// </summary>
    public bool NoteView(ulong session, int match, MatchView view, DateTime utc)
    {
        if (session == 0 || match <= 0)
        {
            return false;
        }

        Played played = Entry(session, utc);
        int at = played.IndexOf(match);
        if (at >= 0)
        {
            MatchView before = played.Matches[at].View;
            MatchView after = before.With(view);
            played.Matches[at] = (match, after);
            return after.Format() != before.Format();
        }

        played.Matches.Insert(~at, (match, MatchView.Unknown.With(view)));
        if (played.Matches.Count > MaxMatches)
        {
            played.Matches.RemoveAt(0);
        }

        return true;
    }

    /// <summary>
    /// This PC held the host's co-op run in hosting session <paramref name="session"/> (seen at <paramref name="utc"/>).
    /// False when it was known already, or isn't a session (0).
    /// </summary>
    public bool NoteRun(ulong session, DateTime utc)
    {
        if (session == 0)
        {
            return false;
        }

        Played played = Entry(session, utc);
        if (played.Run)
        {
            return false;
        }

        played.Run = true;
        return true;
    }

    /// <summary>The record as text (the current format: no host IDs).</summary>
    public string Format()
    {
        var text = new StringBuilder();
        text.Append("# T2T Online: the host's play this PC shared, per hosting session: the host's matches it played and whether it held the " +
            "host's co-op run (after a restart, a rejoin unlocks the moments owed for that play).\n");
        foreach (Played played in _sessions)
        {
            string matches = played.Matches.Count == 0 ? "-"
                : string.Join(",", played.Matches.Select(m => $"{m.Match.ToString(CultureInfo.InvariantCulture)}:{m.View.Format()}"));
            text.Append(CultureInfo.InvariantCulture,
                $"session {played.Session:x16} at {played.At.ToString("o", CultureInfo.InvariantCulture)} run {(played.Run ? 1 : 0)} matches {matches}\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// A record from <see cref="Format"/>'s text, or the old format's (<c>host &lt;id&gt; session &lt;hex16&gt; at &lt;utc&gt;</c>:
    /// the session is kept with no play known, the ID isn't); lines that don't read are skipped. Never throws.
    /// </summary>
    public static SharedPlayRecord Parse(string? text)
    {
        var record = new SharedPlayRecord();
        foreach (string raw in (text ?? string.Empty).Split('\n'))
        {
            string[] parts = raw.Split('#')[0].Split(Blanks, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 8 && parts[0] == "session" && parts[2] == "at" && parts[4] == "run" && parts[6] == "matches" &&
                TryReadSession(parts[1], out ulong session) && TryReadTime(parts[3], out DateTime at) && parts[5] is "0" or "1" &&
                TryReadMatches(parts[7], out List<(int Match, MatchView View)> matches))
            {
                Played played = record.Entry(session, at);
                played.Run |= parts[5] == "1";
                foreach ((int match, MatchView view) in matches)
                {
                    record.NoteView(session, match, view, at);
                }
            }
            else if (parts.Length == 6 && parts[0] == "host" && parts[2] == "session" && parts[4] == "at" &&
                ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
                TryReadSession(parts[3], out ulong oldSession) && TryReadTime(parts[5], out DateTime oldAt))
            {
                record.Entry(oldSession, oldAt);
                record.HadHostIds = true;
            }
        }

        return record;
    }

    private static bool TryReadSession(string text, out ulong session) =>
        ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out session) && session != 0;

    private static bool TryReadTime(string text, out DateTime utc) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);

    /// <summary>The matches of a line: "-", or "n:view" each (a bare "n", as the first 0.11.0 builds wrote, knows nothing more).</summary>
    private static bool TryReadMatches(string text, out List<(int Match, MatchView View)> matches)
    {
        matches = new List<(int, MatchView)>();
        if (text == "-")
        {
            return true;
        }

        foreach (string part in text.Split(','))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            string number = colon < 0 ? part : part[..colon];
            MatchView view = MatchView.Unknown;
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int match) || match <= 0 ||
                (colon >= 0 && !MatchView.TryParse(part[(colon + 1)..], out view)))
            {
                return false;
            }

            matches.Add((match, view));
        }

        return true;
    }

    private Played? Find(ulong session)
    {
        // A loop, not a lambda: asked every frame while a moment waits.
        foreach (Played played in _sessions)
        {
            if (session != 0 && played.Session == session)
            {
                return played;
            }
        }

        return null;
    }

    /// <summary>Session <paramref name="session"/>'s entry, made (newest, the oldest dropped beyond <see cref="MaxKept"/>) when it's new.</summary>
    private Played Entry(ulong session, DateTime utc)
    {
        Played? played = Find(session);
        if (played != null)
        {
            return played;
        }

        played = new Played(session, DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        _sessions.Add(played);
        if (_sessions.Count > MaxKept)
        {
            _sessions.RemoveAt(0);
        }

        return played;
    }

    /// <summary>One hosting session's play shared here.</summary>
    private sealed class Played
    {
        public Played(ulong session, DateTime at)
        {
            Session = session;
            At = at;
        }

        public ulong Session { get; }

        /// <summary>When this PC first shared it (UTC).</summary>
        public DateTime At { get; }

        public bool Run { get; set; }

        /// <summary>The host's matches played here, by number ascending, with what this PC saw of each.</summary>
        public List<(int Match, MatchView View)> Matches { get; } = new();

        /// <summary>Where match <paramref name="match"/> is in <see cref="Matches"/>, or the complement of where it would go (a loop: asked every frame while a moment waits).</summary>
        public int IndexOf(int match)
        {
            int low = 0, high = Matches.Count - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                int at = Matches[middle].Match;
                if (at == match)
                {
                    return middle;
                }

                if (at < match)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return ~low;
        }
    }
}

/// <summary>
/// What a friend's game saw of the host's play in the hosting session it's in: the host's matches it played, and whether it
/// holds or held the host's co-op run (D135). A shared moment unlocks only for play it saw (<see cref="MomentCheck"/>).
/// </summary>
public interface IMomentWitness
{
    /// <summary>The highest of the host's match numbers this PC played in this hosting session (0: none).</summary>
    int NewestPlayed { get; }

    /// <summary>True while this PC holds the host's co-op run, or once it held it in this hosting session.</summary>
    bool HeldRun { get; }

    /// <summary>True when this PC played the host's match <paramref name="match"/> in this hosting session.</summary>
    bool Played(int match);

    /// <summary>What this PC saw of the host's match <paramref name="match"/> (<see cref="MatchView.Unknown"/> when it didn't play it).</summary>
    MatchView ViewOf(int match);
}

/// <summary>
/// A friend's witness of the host's play (<see cref="IMomentWitness"/>, D34, D135): what it sees live in this F7 session (each
/// host match its game plays, the host's co-op run held), and what <see cref="SharedPlayRecord"/> kept of the hosting session
/// it was welcomed into from before its game restarted. What's seen live in a hosting session is noted in the record (the
/// glue saves it then). A welcome into another hosting session starts the live part over. Game-independent.
/// </summary>
public sealed class SharedPlayGate : IMomentWitness
{
    private readonly SharedPlayRecord _record;
    private readonly Dictionary<int, MatchView> _matches = new();
    private ulong _session;
    private int _newest;
    private bool _run;

    /// <summary>A gate on <paramref name="record"/>.</summary>
    public SharedPlayGate(SharedPlayRecord record) => _record = record ?? throw new ArgumentNullException(nameof(record));

    /// <summary>True when the hosting session this PC was welcomed into is one it shared play in before (a rejoin, or its game restarted since).</summary>
    public bool SharedBefore { get; private set; }

    /// <summary>The hosting session this PC was welcomed into (0: none, or the one-PC test).</summary>
    public ulong Session => _session;

    /// <inheritdoc />
    public int NewestPlayed => Math.Max(_newest, _session == 0 ? 0 : _record.NewestMatch(_session));

    /// <inheritdoc />
    public bool HeldRun => _run || (_session != 0 && _record.HeldRun(_session));

    /// <summary>True once this PC held the host's co-op run in this F7 session's hosting session (seen live, not from the record).</summary>
    public bool HeldRunLive => _run;

    /// <inheritdoc />
    public bool Played(int match) => match > 0 && (_matches.ContainsKey(match) || (_session != 0 && _record.PlayedMatch(_session, match)));

    /// <inheritdoc />
    public MatchView ViewOf(int match)
    {
        MatchView kept = _session != 0 && match > 0 ? _record.ViewOf(_session, match) : MatchView.Unknown;
        return _matches.TryGetValue(match, out MatchView live) ? kept.With(live) : kept;
    }

    /// <summary>
    /// Welcomed into hosting session <paramref name="session"/> (a new F7 session or a rejoin). True when it's one this PC
    /// shared play in before (its moments' play is known at once). Another hosting session than the last starts the live
    /// part over.
    /// </summary>
    public bool Welcomed(ulong session)
    {
        if (session != _session)
        {
            ForgetLive();
        }

        _session = session;
        SharedBefore = _record.Has(session);
        return SharedBefore;
    }

    /// <summary>The F7 session ended or a new one starts: nothing is known until the next welcome.</summary>
    public void Clear()
    {
        _session = 0;
        SharedBefore = false;
        ForgetLive();
    }

    /// <summary>
    /// This PC played the host's match <paramref name="match"/> (its number on the host). True the first time in this
    /// hosting session; <paramref name="noted"/>: the record changed (save it).
    /// </summary>
    public bool NoteMatch(int match, DateTime utc, out bool noted) => NoteMatch(match, MatchView.Unknown, utc, out noted);

    /// <summary>
    /// This PC played the host's match <paramref name="match"/> (its number on the host), of the kind
    /// <paramref name="view"/> says (from the setup it loaded). True the first time in this hosting session;
    /// <paramref name="noted"/>: the record changed (save it).
    /// </summary>
    public bool NoteMatch(int match, MatchView view, DateTime utc, out bool noted)
    {
        noted = false;
        if (match <= 0 || _matches.ContainsKey(match))
        {
            return false;
        }

        _matches[match] = view;
        _newest = Math.Max(_newest, match);
        noted = _record.NoteView(_session, match, view, utc);
        return true;
    }

    /// <summary>
    /// What this PC saw of the host's match <paramref name="match"/> since (its end, its score, a sign its stream carried),
    /// merged into what it saw before (<see cref="MatchView.With"/>). False for a match not played here in this hosting
    /// session; <paramref name="noted"/>: the record changed (save it).
    /// </summary>
    public bool NoteView(int match, MatchView view, DateTime utc, out bool noted)
    {
        noted = false;
        if (match <= 0 || !_matches.TryGetValue(match, out MatchView before))
        {
            return false;
        }

        _matches[match] = before.With(view);
        noted = _record.NoteView(_session, match, view, utc);
        return true;
    }

    /// <summary>
    /// This PC holds the host's co-op run now. True the first time in this hosting session; <paramref name="noted"/>: the
    /// record changed (save it). Cheap after the first time (asked every frame).
    /// </summary>
    public bool NoteRun(DateTime utc, out bool noted)
    {
        noted = false;
        if (_run)
        {
            return false;
        }

        _run = true;
        noted = _record.NoteRun(_session, utc);
        return true;
    }

    private void ForgetLive()
    {
        _matches.Clear();
        _newest = 0;
        _run = false;
    }
}
