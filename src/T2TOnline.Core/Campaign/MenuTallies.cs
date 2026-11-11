using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What a friend's game showed of the host's menus in one session, for one line when it ends: a menu
/// counts once, by how it ended up: real, as text with its reason, as a note, not shown on purpose with its reason (the host's
/// match menus while this PC couldn't load that match), or nothing at all (closed while it was still opening or waiting, as
/// the host's warm-ups are). A wait shows the reason it waits for, so only the last state counts; a real
/// menu that later went to text counts as real, and as fallen back. Bounded (<see cref="NamedCounts"/>, <see cref="MaxOpen"/>):
/// kinds and reasons can come from the host. Not thread-safe.
/// </summary>
public sealed class FriendMenuTally
{
    /// <summary>Most menus followed at once (more than a host's board holds); others are only counted when they end.</summary>
    public const int MaxOpen = 64;

    private readonly Dictionary<uint, Shown> _open = new();
    private readonly NamedCounts _real = new();
    private readonly NamedCounts _text = new();
    private readonly NamedCounts _notes = new();
    private readonly NamedCounts _unseen = new();
    private readonly NamedCounts _hidden = new();
    private readonly NamedCounts _fellBack = new();
    private int _late;

    /// <summary>A host menu opened (<paramref name="serial"/>, of <paramref name="kind"/>: its class). One still open under that serial ends first.</summary>
    public void Opened(uint serial, string kind)
    {
        Closed(serial);
        if (_open.Count < MaxOpen)
        {
            _open[serial] = new Shown(kind);
        }
        else
        {
            _unseen.Add(kind);
        }
    }

    /// <summary>The real menu came up.</summary>
    public void Real(uint serial)
    {
        if (_open.TryGetValue(serial, out Shown? shown))
        {
            shown.Real = true;
            shown.Note = false;
            shown.Why = null;
            shown.Hidden = null;
            shown.FellBack = null;
        }
    }

    /// <summary>It shows as text (or waits as text) because of <paramref name="why"/>.</summary>
    public void Text(uint serial, string why)
    {
        if (_open.TryGetValue(serial, out Shown? shown))
        {
            shown.Why = why;
            shown.Note = false;
            shown.Hidden = null;
            if (shown.Real)
            {
                shown.FellBack = why;
            }
        }
    }

    /// <summary>It shows as a note.</summary>
    public void Note(uint serial)
    {
        if (_open.TryGetValue(serial, out Shown? shown))
        {
            shown.Note = true;
            shown.Why = null;
            shown.Hidden = null;
            if (shown.Real)
            {
                shown.FellBack = "a note";
            }
        }
    }

    /// <summary>It shows nothing here on purpose, because of <paramref name="why"/> (no text panel either).</summary>
    public void Hidden(uint serial, string why)
    {
        if (_open.TryGetValue(serial, out Shown? shown) && !shown.Real)
        {
            shown.Hidden = why;
            shown.Why = null;
            shown.Note = false;
        }
    }

    /// <summary>
    /// It closed (nothing happens for one not followed). <paramref name="nothingShown"/>: its wait hadn't shown anything yet, so its reason
    /// never reached the screen.
    /// </summary>
    public void Closed(uint serial, bool nothingShown = false)
    {
        if (!_open.Remove(serial, out Shown? shown))
        {
            return;
        }

        if (shown.Real)
        {
            _real.Add(shown.Kind);
            if (shown.FellBack != null)
            {
                _fellBack.Add($"{shown.Kind}: {shown.FellBack}");
            }
        }
        else if (shown.Note)
        {
            _notes.Add(shown.Kind);
        }
        else if (shown.Hidden != null)
        {
            _hidden.Add($"{shown.Kind}: {shown.Hidden}");
        }
        else if (shown.Why != null && !nothingShown)
        {
            _text.Add($"{shown.Kind}: {shown.Why}");
        }
        else
        {
            _unseen.Add(shown.Kind);
        }
    }

    /// <summary>Every menu still followed ends (the session is ending); <paramref name="nothingShown"/> says which of them showed nothing yet.</summary>
    public void CloseAll(System.Func<uint, bool>? nothingShown = null)
    {
        foreach (uint serial in new List<uint>(_open.Keys))
        {
            Closed(serial, nothingShown?.Invoke(serial) == true);
        }
    }

    /// <summary>A screen inside a menu came up for real (the Nurse's procedure screen, <paramref name="kind"/>).</summary>
    public void ScreenReal(string kind) => _real.Add(kind);

    /// <summary>
    /// A screen inside a menu shows as text because of <paramref name="why"/>. <paramref name="wasReal"/>: it had come up for real
    /// (<see cref="ScreenReal"/>: it counts as fallen back).
    /// </summary>
    public void ScreenText(string kind, string why, bool wasReal)
    {
        (wasReal ? _fellBack : _text).Add($"{kind}: {why}");
    }

    /// <summary>A menu opened here after the host's had closed (it was closed at once).</summary>
    public void Late() => _late++;

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _open.Clear();
        _real.Clear();
        _text.Clear();
        _notes.Clear();
        _unseen.Clear();
        _hidden.Clear();
        _fellBack.Clear();
        _late = 0;
    }

    /// <summary>True when any menu was followed this session.</summary>
    public bool Any => _open.Count + _real.Total + _text.Total + _notes.Total + _unseen.Total + _hidden.Total + _late > 0;

    /// <summary>The session's line (<see cref="CloseAll"/> first); <paramref name="linesLeftOut"/>: menu lines the log's budget only counted.</summary>
    public string Line(string host, int linesLeftOut)
    {
        int total = _real.Total + _text.Total + _notes.Total + _unseen.Total + _hidden.Total;
        return $"Menus: this session showed {total} of {(host.Length > 0 ? host : "the host")}'s menus: {_real.Total} real{List(_real)}, {_text.Total} as text{List(_text)}, " +
            $"{_notes.Total} as a note{List(_notes)}, {_unseen.Total} closed before anything showed here{List(_unseen)}" +
            $"{(_hidden.Total > 0 ? $", {_hidden.Total} not shown on purpose{List(_hidden)}" : string.Empty)}; " +
            $"{_fellBack.Total} of the real ones fell back to text after opening{List(_fellBack)}; {_late} opened after the host's had closed; {linesLeftOut} log line(s) only counted.";
    }

    private static string List(NamedCounts counts) => counts.Total > 0 ? $" ({counts})" : string.Empty;

    private sealed class Shown
    {
        public Shown(string kind) => Kind = kind;

        public string Kind { get; }

        public bool Real { get; set; }

        public bool Note { get; set; }

        public string? Why { get; set; }

        public string? Hidden { get; set; }

        public string? FellBack { get; set; }
    }
}

/// <summary>
/// What a host's game sent friends of its menus in one session, for one line when it ends, to compare
/// with each friend's <see cref="FriendMenuTally"/>. Not thread-safe.
/// </summary>
public sealed class HostMenuTally
{
    private readonly NamedCounts _menus = new();
    private readonly NamedCounts _attachments = new();
    private int _noFriend;
    private int _boardFull;

    /// <summary>A menu of <paramref name="kind"/> (its class) went on the board for friends.</summary>
    public void Opened(string kind) => _menus.Add(kind);

    /// <summary>An attachment of <paramref name="kind"/> went to at least one friend.</summary>
    public void Attachment(string kind) => _attachments.Add(kind);

    /// <summary>An attachment went to no friend.</summary>
    public void NoFriend() => _noFriend++;

    /// <summary>A menu opened with the board full: friends didn't get it.</summary>
    public void BoardFull() => _boardFull++;

    /// <summary>True when anything went to friends, or should have.</summary>
    public bool Any => _menus.Total + _attachments.Total + _noFriend + _boardFull > 0;

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _menus.Clear();
        _attachments.Clear();
        _noFriend = 0;
        _boardFull = 0;
    }

    /// <summary>The session's line. <paramref name="tooBig"/>: messages too big to send; <paramref name="linesLeftOut"/>: menu lines the log's budget only counted.</summary>
    public string Line(int tooBig, int linesLeftOut) =>
        $"Menus: this session the host opened {_menus.Total} menu(s) for friends{(_menus.Total > 0 ? $" ({_menus})" : string.Empty)}; " +
        $"attachments: {_attachments.Total}{(_attachments.Total > 0 ? $" ({_attachments})" : string.Empty)}; {_noFriend} went to no friend, {tooBig} too big" +
        $"{(_boardFull > 0 ? $", {_boardFull} menu(s) not sent (the board was full)" : string.Empty)}; {linesLeftOut} log line(s) only counted.";
}
