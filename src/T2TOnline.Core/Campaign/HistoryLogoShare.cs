using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Campaign;

/// <summary>What <see cref="HistoryLogoShare.Admit"/> decided about a logo the host's history page wears.</summary>
/// <param name="Sent">The logo may go to friends.</param>
/// <param name="Why">When it may not, why (a line for the log); empty when it may.</param>
public readonly record struct HistoryLogoVerdict(bool Sent, string Why);

/// <summary>
/// Host: which of the custom logos the host's run history page wears goes to friends, and when. At the
/// title the host browses its run history (the run-end menu's history mode); a page from a custom-logo run names
/// <c>custom_&lt;file&gt;.png</c>, a sprite only the host's PC has, so a friend's page of it showed the game's default logo.
/// The logo of the page the host settles on goes through the custom content carrier (<see cref="Stream.CustomContentSender"/>:
/// in pieces, paced by the link, each item to each friend once a session), bounded so browsing can't flood a link:
/// <list type="bullet">
/// <item>one logo a page, and only the page the host has rested on for <see cref="SettleSeconds"/> (flipping through
/// pages sends nothing for the pages passed);</item>
/// <item>each logo once a session (a page seen again, or a friend who joins, asks nothing more of the disk or the link than
/// the carrier's own once-per-friend rule);</item>
/// <item>at most <see cref="MaxLogos"/> different logos, <see cref="MaxBytes"/> bytes and <see cref="MaxPixels"/> pixels a session, so
/// a long browse can't use up the friends' own budget for the logos of matches (<see cref="Stream.CustomLogoBudget"/>, which counts
/// logos and pixels: four large ones would spend all of it).</item>
/// </list>
/// A logo that can't go falls back to the friend's default logo, and the log says why, once. Game-independent. Not
/// thread-safe (one game thread).
/// </summary>
public sealed class HistoryLogoShare
{
    /// <summary>How long a page must be up before its logo is read and queued (microseconds): a flip through pages sends nothing.</summary>
    public const long SettleMicros = 1_000_000;

    /// <summary>The same, in seconds (for the log).</summary>
    public const int SettleSeconds = 1;

    /// <summary>Most different logos sent for pages in one hosting session.</summary>
    public const int MaxLogos = 8;

    /// <summary>Most bytes of logos sent for pages in one hosting session.</summary>
    public const long MaxBytes = 12L * 1024 * 1024;

    /// <summary>Most pixels of logos sent for pages in one hosting session: half of what a friend decodes in a session (<see cref="Stream.CustomLogoBudget.MaxPixels"/>).</summary>
    public const long MaxPixels = Stream.CustomLogoBudget.MaxPixels / 2;

    private readonly Dictionary<string, long> _admitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private string _page = string.Empty;
    private long _since;
    private bool _read;

    /// <summary>Logos admitted this hosting session.</summary>
    public int Logos => _admitted.Count;

    /// <summary>Bytes of the logos admitted this hosting session.</summary>
    public long Bytes { get; private set; }

    /// <summary>Pixels of the logos admitted this hosting session.</summary>
    public long Pixels { get; private set; }

    /// <summary>The custom logo the page up now wears (empty: a page that wears none, or none up).</summary>
    public string Page => _page;

    /// <summary>
    /// The history page now up wears logo <paramref name="logoName"/> (null or empty: it wears none). A name that isn't a custom
    /// logo's (<c>custom_&lt;file&gt;.png</c>, the game's own logos are on every PC) counts as none. The same page again
    /// changes nothing.
    /// </summary>
    public void PageShown(string? logoName, long nowMicros)
    {
        string name = IsCustom(logoName) ? logoName! : string.Empty;
        if (string.Equals(name, _page, StringComparison.Ordinal))
        {
            return;
        }

        _page = name;
        _since = nowMicros;
        _read = false;
    }

    /// <summary>The page is gone (the history menu closed): nothing is waiting to go.</summary>
    public void PageGone()
    {
        _page = string.Empty;
        _read = false;
    }

    /// <summary>
    /// True once, when the logo of the page now up has been up for <see cref="SettleSeconds"/> and hasn't been read for it
    /// yet: <paramref name="name"/> is to be read from the host's custom logo folder and <see cref="Admit"/>ted. False for a
    /// page that wears none, one passed through, a logo already read for this page, and one refused before.
    /// </summary>
    public bool ShouldRead(long nowMicros, out string name)
    {
        name = _page;
        if (_page.Length == 0 || _read || nowMicros - _since < SettleMicros || _refused.Contains(_page))
        {
            return false;
        }

        _read = true;
        return true;
    }

    /// <summary>
    /// The logo <paramref name="name"/> of <paramref name="bytes"/> bytes and <paramref name="pixels"/> pixels was read: it may go to
    /// friends when it fits the session's budget (<see cref="MaxLogos"/>, <see cref="MaxBytes"/>, <see cref="MaxPixels"/>); one
    /// admitted before is admitted again, uncounted. A refusal is remembered (the page isn't read again) and says why.
    /// </summary>
    public HistoryLogoVerdict Admit(string name, long bytes, long pixels = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_admitted.ContainsKey(name))
        {
            return new HistoryLogoVerdict(true, string.Empty);
        }

        if (_admitted.Count >= MaxLogos)
        {
            _refused.Add(name);
            return new HistoryLogoVerdict(false, string.Create(CultureInfo.InvariantCulture, $"{_admitted.Count} logos went to friends for history pages this session (the most is {MaxLogos})"));
        }

        if (bytes < 1 || Bytes + bytes > MaxBytes)
        {
            _refused.Add(name);
            return new HistoryLogoVerdict(false, string.Create(CultureInfo.InvariantCulture,
                $"{bytes / 1024} KB more would pass the {MaxBytes / (1024 * 1024)} MB of history page logos a session sends ({Bytes / 1024} KB went already)"));
        }

        if (Pixels + Math.Max(pixels, 0) > MaxPixels)
        {
            _refused.Add(name);
            return new HistoryLogoVerdict(false, string.Create(CultureInfo.InvariantCulture,
                $"{pixels / 1_000_000.0:0.#} Mpx more would pass the {MaxPixels / 1_000_000.0:0.#} Mpx of history page logos a session sends, half of what a friend's game decodes ({Pixels / 1_000_000.0:0.#} Mpx went already)"));
        }

        _admitted[name] = bytes;
        Bytes += bytes;
        Pixels += Math.Max(pixels, 0);
        return new HistoryLogoVerdict(true, string.Empty);
    }

    /// <summary>The logo <paramref name="name"/> can't be had (its file is missing, too big, not a PNG): not read again this session.</summary>
    public void Unreadable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _refused.Add(name);
    }

    /// <summary>True when <paramref name="name"/> was admitted: the page's logo that is kept in front of friends who join.</summary>
    public bool WasAdmitted(string name) => _admitted.ContainsKey(name);

    /// <summary>Hosting stopped: nothing is remembered.</summary>
    public void Clear()
    {
        _admitted.Clear();
        _refused.Clear();
        _page = string.Empty;
        _since = 0;
        _read = false;
        Bytes = 0;
        Pixels = 0;
    }

    /// <summary>True for the game's name for a custom logo's sprite (<c>custom_&lt;file&gt;.png</c>), the only logos only the host's PC has.</summary>
    public static bool IsCustom(string? logoName) =>
        !string.IsNullOrEmpty(logoName) && logoName.StartsWith(Stream.CustomItem.LogoPrefix, StringComparison.Ordinal) &&
        Stream.CustomItem.Check(Stream.CustomContentKind.Logo, logoName, 1) == null;
}

/// <summary>What <see cref="HistoryLogoWait.Poll"/> found.</summary>
public enum HistoryLogoWaitState
{
    /// <summary>Nothing is waited for (the page wears no custom logo, or its wait ended).</summary>
    Idle,

    /// <summary>The page's logo hasn't arrived yet.</summary>
    Waiting,

    /// <summary>The page's logo is here now (said once): the page is shown again with it.</summary>
    Arrived,

    /// <summary>The page's logo didn't come within <see cref="HistoryLogoWait.GiveUpSeconds"/> (said once): the default logo stays.</summary>
    GaveUp,
}

/// <summary>
/// Friend: a host's history page names a custom logo this PC may not have. The page is shown at once with
/// whatever the game finds (its default logo for a miss); the logo the host sends for the page it rests on is put in the
/// game's logo list when it arrives (<c>Match.CustomContent</c>), and the page is shown again with it, once.
/// Game-independent.
/// </summary>
public sealed class HistoryLogoWait
{
    /// <summary>How long a page waits for its logo before it stays as it is (seconds): the host's carrier is paced by the link, and a big logo takes a while.</summary>
    public const double GiveUpSeconds = 120;

    private string _name = string.Empty;
    private double _since;
    private bool _waiting;

    /// <summary>The logo waited for (empty: none).</summary>
    public string Name => _name;

    /// <summary>
    /// A page is shown, wearing <paramref name="logoName"/>. A custom logo (<see cref="HistoryLogoShare.IsCustom"/>) that is
    /// not <paramref name="here"/> yet is waited for; the page shown again for the same logo (the wait's own re-show)
    /// doesn't start a second wait. Anything else waits for nothing.
    /// </summary>
    public void PageShown(string? logoName, bool here, double nowSeconds)
    {
        string name = HistoryLogoShare.IsCustom(logoName) ? logoName! : string.Empty;
        if (name.Length == 0 || here)
        {
            _name = string.Empty;
            _waiting = false;
            return;
        }

        if (!_waiting || !string.Equals(name, _name, StringComparison.Ordinal))
        {
            _name = name;
            _since = nowSeconds;
            _waiting = true;
        }
    }

    /// <summary>Each frame (or so) while the page is up: whether its logo is <paramref name="here"/> now. <see cref="HistoryLogoWaitState.Arrived"/> and <see cref="HistoryLogoWaitState.GaveUp"/> are each returned once.</summary>
    public HistoryLogoWaitState Poll(double nowSeconds, bool here)
    {
        if (!_waiting)
        {
            return HistoryLogoWaitState.Idle;
        }

        if (here)
        {
            _waiting = false;
            return HistoryLogoWaitState.Arrived;
        }

        if (nowSeconds - _since >= GiveUpSeconds)
        {
            _waiting = false;
            return HistoryLogoWaitState.GaveUp;
        }

        return HistoryLogoWaitState.Waiting;
    }

    /// <summary>The history menu closed, or the session ended: nothing is waited for.</summary>
    public void Clear()
    {
        _name = string.Empty;
        _waiting = false;
    }
}
