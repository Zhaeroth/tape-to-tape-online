using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// How many of each kind of personal data <see cref="LogScrub.Scrub"/> replaced (a bug report's manifest and the check tours read them).
/// Not thread-safe: one per caller.
/// </summary>
public sealed class LogScrubCounts
{
    /// <summary>SteamID64s replaced by player tags.</summary>
    public int SteamIds { get; set; }

    /// <summary>Lobby IDs replaced by lobby tags.</summary>
    public int Lobbies { get; set; }

    /// <summary>Registered Steam names replaced by player tags.</summary>
    public int Names { get; set; }

    /// <summary>Folders replaced by labels.</summary>
    public int Folders { get; set; }

    /// <summary>IP addresses replaced.</summary>
    public int Addresses { get; set; }

    /// <summary>Time zone offsets replaced.</summary>
    public int Offsets { get; set; }

    /// <summary>Control, bidirectional and private-use characters replaced.</summary>
    public int Controls { get; set; }

    /// <summary>The log tag key, written out (never by design: a backstop).</summary>
    public int Keys { get; set; }

    /// <summary>Registered player-written names (custom teams', skaters', goalies' and logos') replaced by their label.</summary>
    public int Texts { get; set; }

    /// <summary>Every replacement.</summary>
    public int Total => SteamIds + Lobbies + Names + Folders + Addresses + Offsets + Controls + Keys + Texts;
}

/// <summary>
/// The log's backstop for personal data (D127): every line the mod writes passes through <see cref="Scrub"/> before it
/// reaches a file or MelonLoader's console. It replaces SteamID64s and lobby IDs with their <see cref="LogTags"/>,
/// registered Steam names with their player's tag, known folders with labels (<c>&lt;game folder&gt;</c>,
/// <c>%LOCALAPPDATA%</c>, ...), any other <c>X:\Users\&lt;name&gt;</c> with <c>&lt;user folder&gt;</c>, IPv4 and IPv6
/// addresses with <c>&lt;ip&gt;</c>, time zone offsets (<c>UTC+01:00</c>) with <c>UTC±&lt;offset&gt;</c> and the tags'
/// own key with <see cref="KeyLabel"/>; control and direction characters become '?' (a stack trace's line breaks stay). A
/// line that names a player as <c>"&lt;name&gt;" (&lt;id&gt;)</c> then reads just <c>P#a0c1d2</c>. Lines are structured at
/// their source (tags, labels, counts); this catches what a source still writes.
/// <para>
/// Names are matched as whole tokens, ordinal, in the forms the code writes them: as Steam gave them, cleaned
/// (<see cref="SafeText"/>, <see cref="DisplayName"/>), as the lobby draws them without what its font can't (the forms a
/// font most often makes: without emoji, without symbols, the name's Latin and Cyrillic part, its ASCII part), and cut
/// with an ellipsis at any length.
/// A name made only of the log's own words (<c>Online</c>, <c>the host</c>), ASCII digits and ASCII punctuation, a single
/// ASCII character, or one holding a tag's <c>P#</c> or <c>L#</c> is replaced only where it is quoted. A name two players
/// share stands as <see cref="LogTags.UnknownPlayer"/>.
/// </para>
/// <para>
/// Player-written names (a custom team's, skater's, goalie's or logo's: <see cref="RegisterPlayerText"/>) are found the
/// same way, in the same pass, and written as <see cref="PlayerTextLabel"/> with the text's shape, never as a player's
/// tag. Such a text is more often an ordinary word than a Steam name is, so besides the names' rule a form shorter than
/// <see cref="MinPlayerText"/> characters or made of a single word is replaced only where it is quoted. A form a Steam
/// name also has stays that player's tag.
/// </para>
/// Thread-safe: the names and folders are immutable snapshots replaced on change. Returns the same string instance when
/// there is nothing to replace, without allocating.
/// </summary>
public sealed class LogScrub
{
    /// <summary>The first SteamID64 of an individual account (universe public, type individual, instance desktop).</summary>
    public const ulong FirstSteamId = 76561197960265728;

    /// <summary>The last SteamID64 of an individual account (the 32-bit account number at its largest).</summary>
    public const ulong LastSteamId = 76561202255233023;

    /// <summary>What an IP address becomes.</summary>
    public const string AddressLabel = "<ip>";

    /// <summary>What a time zone offset becomes, after its <c>UTC</c> or <c>GMT</c>.</summary>
    public const string OffsetLabel = "±<offset>";

    /// <summary>What the log tag key becomes, should a line ever hold it.</summary>
    public const string KeyLabel = "<log tag key>";

    /// <summary>What any Windows user's profile folder becomes when it isn't one of the known folders.</summary>
    public const string UserFolderLabel = "<user folder>";

    /// <summary>The most Steam IDs whose names are kept; a flood of reads past it is refused (and counted).</summary>
    public const int MaxNames = 4096;

    /// <summary>The most different names kept for one Steam ID (renames); a flood of new names past it is refused (and counted).</summary>
    public const int MaxNamesPerPlayer = 16;

    /// <summary>The most player-written names kept (<see cref="RegisterPlayerText"/>); more are refused (and counted).</summary>
    public const int MaxPlayerTexts = 512;

    /// <summary>A player-written name's form shorter than this is replaced only where it is quoted.</summary>
    public const int MinPlayerText = 3;

    /// <summary>What a registered player-written name becomes (followed by its shape when it was registered with one).</summary>
    public const string PlayerTextLabel = "a custom name";

    // A cut name keeps at least this many characters before its ellipsis to count as the name.
    private const int MinCut = 4;
    private const char Ellipsis = '\u2026';
    private const ulong Ambiguous = ulong.MaxValue;

    // The owner of a form that is a player-written name's, not a Steam ID's.
    private const ulong PlayerText = ulong.MaxValue - 1;

    // The log's own words: a Steam name equal to one (any case) is replaced only where it's quoted, so the words stay.
    private static readonly HashSet<string> LogWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "again", "all", "an", "and", "any", "are", "as", "at", "away", "back", "before", "brawl",
        "but", "by", "campaign", "can", "cost", "count", "data", "done", "down", "error", "event", "events", "failed",
        "false", "file", "folder", "for", "friend", "friends", "from", "game", "games", "goalie", "guest", "has", "have",
        "here", "host", "hosting", "hud", "in", "info", "input", "is", "it", "its", "join", "joined", "keyboard", "left",
        "library", "lobby", "log", "logs", "lost", "map", "match", "me", "menu", "menus", "mod", "mouse", "my", "new",
        "no", "none", "not", "now", "null", "of", "off", "ok", "on", "one", "online", "or", "out", "pad", "panel",
        "player", "players", "progress", "quick", "ready", "relay", "run", "save", "saved", "screen", "seat", "sent",
        "session", "skater", "so", "start", "started", "steam", "stream", "team", "test", "that", "the", "their", "them",
        "they", "this", "time", "title", "to", "trace", "true", "unknown", "up", "user", "warn", "was", "we", "when",
        "with", "yes", "you", "your",
    };

    // Where a Windows user's profile folder starts, after its drive letter.
    private static readonly string[] UserMarkers = { ":\\Users\\", ":/Users/" };

    /// <summary>
    /// What a label's font may draw, for the name a lobby shows (<see cref="DisplayName.Clean"/>'s <c>canDraw</c> drops the
    /// rest, and a source that slips writes that). Which characters a font lacks depends on the font and the language and
    /// is known only in the game, so the forms the game's fonts most often make are kept: without emoji past the Basic
    /// Multilingual Plane (the sprite asset holds only some), without symbols (only letters, digits and punctuation: the
    /// fonts hold no stars or hearts), the name's Latin and Cyrillic part (the scripts the game's Latin label fonts hold:
    /// a name with accents beside another script is drawn as that), and its ASCII part alone (a script no font of the
    /// language holds).
    /// </summary>
    private static readonly Func<int, bool>[] DrawnGuesses =
    {
        static codePoint => codePoint <= char.MaxValue,
        static codePoint => CharUnicodeInfo.GetUnicodeCategory(codePoint) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation,
        static codePoint => codePoint is < 0x250 or (>= 0x400 and <= 0x4FF),
        static codePoint => codePoint < 0x80,
    };

    private readonly object _gate = new();

    // Each ID's names, read without the lock (a name known already costs a look, wherever it comes in the order: the lobby
    // reads a friend's Steam name ten times a second, and each board brings the host's cleaned copy of it); replaced whole.
    private readonly ConcurrentDictionary<ulong, string[]> _known = new();

    // The player-written names registered, read without the lock (a text known already costs a look).
    private readonly ConcurrentDictionary<string, byte> _knownTexts = new(StringComparer.Ordinal);

    // Under _gate: each form's ID (or Ambiguous, or PlayerText), the forms by their first character, each ID's names, and
    // what each player-written form becomes.
    private readonly Dictionary<string, ulong> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<char, List<string>> _formsByFirst = new();
    private readonly Dictionary<ulong, HashSet<string>> _namesOf = new();
    private readonly Dictionary<string, string> _textLabels = new(StringComparer.Ordinal);

    // Snapshots the scrubbing threads read without the lock: replaced whole, never changed.
    private volatile LogTags _tags;
    private volatile Folder[] _folders = Array.Empty<Folder>();
    private volatile Dictionary<char, NameForm[]> _byFirst = new();
    private int _refusedNames;
    private int _refusedTexts;
    private int _textCount;

    /// <summary>A scrubber naming players and lobbies with <paramref name="tags"/>.</summary>
    public LogScrub(LogTags tags)
    {
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
    }

    /// <summary>The tags players and lobbies get now: the lobby's while in one, a launch's own otherwise.</summary>
    public LogTags Tags
    {
        get => _tags;
        set => _tags = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>How many Steam IDs have a registered name.</summary>
    public int NameCount => _known.Count;

    /// <summary>Names refused because <see cref="MaxNames"/> IDs, or <see cref="MaxNamesPerPlayer"/> names of one ID, were already registered.</summary>
    public int RefusedNames => Volatile.Read(ref _refusedNames);

    /// <summary>How many player-written names are registered (<see cref="RegisterPlayerText"/>).</summary>
    public int PlayerTextCount => Volatile.Read(ref _textCount);

    /// <summary>Player-written names refused because <see cref="MaxPlayerTexts"/> were already registered.</summary>
    public int RefusedPlayerTexts => Volatile.Read(ref _refusedTexts);

    /// <summary>True for a SteamID64 of an individual account (<see cref="FirstSteamId"/> to <see cref="LastSteamId"/>).</summary>
    public static bool IsSteamId(ulong value) => value is >= FirstSteamId and <= LastSteamId;

    /// <summary>True for a lobby's Steam ID: universe public, account type chat (lobbies are chat rooms to Steam).</summary>
    public static bool IsLobbyId(ulong value) => value >> 56 == 1 && ((value >> 52) & 0xF) == 8;

    /// <summary>
    /// Adds folders to replace, each a folder and its label (<c>&lt;game folder&gt;</c>). Ignoring case and the kind of
    /// slash, only as a whole name (<c>C:\Users\bob</c> isn't in <c>C:\Users\bobby</c>), longer folders first; folders of
    /// three characters or fewer (a drive) are left out. A folder added again takes its new label.
    /// </summary>
    public void AddFolders(IEnumerable<KeyValuePair<string, string>> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        lock (_gate)
        {
            var all = _folders.ToDictionary(f => f.Back, f => f.Label, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> folder in folders)
            {
                string path = (folder.Key ?? string.Empty).Trim().TrimEnd('\\', '/').Replace('/', '\\');
                if (path.Length > 3)
                {
                    all[path] = folder.Value ?? string.Empty;
                }
            }

            _folders = all.OrderByDescending(f => f.Key.Length).ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new Folder(f.Key, f.Key.Replace('\\', '/'), f.Value)).ToArray();
        }
    }

    /// <summary>
    /// Registers <paramref name="name"/> as <paramref name="steamId"/>'s Steam name (every persona name read, and the
    /// names the lobby board brings). Cheap when nothing is new: a name known for that ID costs a look, without the lock or
    /// an allocation, whichever of its names came last. False when nothing was added (already known, empty, Steam's
    /// <c>[unknown]</c>, a zero ID, past <see cref="MaxNames"/>, or past <see cref="MaxNamesPerPlayer"/> for that ID: a peer
    /// renaming a player on every message can't grow the registry or its cost without end).
    /// </summary>
    public bool RegisterName(ulong steamId, string? name)
    {
        if (steamId == 0 || string.IsNullOrWhiteSpace(name) || name == DisplayName.SteamUnknown)
        {
            return false;
        }

        if (_known.TryGetValue(steamId, out string[]? known) && Array.IndexOf(known, name) >= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_known.ContainsKey(steamId) && _known.Count >= MaxNames)
            {
                _refusedNames++;
                return false;
            }

            if (!_namesOf.TryGetValue(steamId, out HashSet<string>? names))
            {
                _namesOf[steamId] = names = new HashSet<string>(StringComparer.Ordinal);
            }

            if (names.Contains(name))
            {
                return false; // registered by another thread meanwhile
            }

            if (names.Count >= MaxNamesPerPlayer)
            {
                _refusedNames++;
                return false;
            }

            names.Add(name);
            _known[steamId] = names.ToArray();
            var changed = new HashSet<char>();
            foreach (string form in Forms(name))
            {
                if (!_owners.TryGetValue(form, out ulong owner))
                {
                    _owners[form] = steamId;
                    AddForm(form);
                    changed.Add(form[0]);
                }
                else if (owner == PlayerText)
                {
                    // A player's name is worth more as their tag; the text is hidden either way.
                    _owners[form] = steamId;
                    _textLabels.Remove(form);
                    changed.Add(form[0]);
                }
                else if (owner != steamId && owner != Ambiguous)
                {
                    _owners[form] = Ambiguous;
                    changed.Add(form[0]);
                }
            }

            return Publish(changed);
        }
    }

    /// <summary>
    /// Registers <paramref name="text"/>, a name a player typed (a custom team's, skater's, goalie's or logo's), where the
    /// mod first holds it: from then on every line writes it, in any of a name's forms, as <see cref="PlayerTextLabel"/>
    /// followed by <paramref name="shape"/>'s result in parentheses (asked once, only for a new text). A backstop behind the
    /// lines that write such names by their shape at the source. Cheap when the text is known: a look, without the lock or
    /// an allocation. False when nothing was added (already known, blank, or past <see cref="MaxPlayerTexts"/>: counted).
    /// Never throws for the shape: a shape that fails gives the label alone.
    /// </summary>
    /// <param name="text">The name as the game holds it.</param>
    /// <param name="shape">The text's shape for a log line (<see cref="PlayerTextShape.Describe"/>), or null for the label alone.</param>
    public bool RegisterPlayerText(string? text, Func<string?, string>? shape = null)
    {
        if (string.IsNullOrWhiteSpace(text) || _knownTexts.ContainsKey(text))
        {
            return false;
        }

        if (Volatile.Read(ref _textCount) >= MaxPlayerTexts)
        {
            Interlocked.Increment(ref _refusedTexts);
            return false;
        }

        string label = PlayerTextLabel;
        try
        {
            string? described = shape?.Invoke(text);
            if (!string.IsNullOrEmpty(described))
            {
                label = $"{PlayerTextLabel} ({described})";
            }
        }
        catch (Exception)
        {
            // The label alone still hides the text.
        }

        lock (_gate)
        {
            if (_knownTexts.ContainsKey(text))
            {
                return false; // registered by another thread meanwhile
            }

            if (_textCount >= MaxPlayerTexts)
            {
                Interlocked.Increment(ref _refusedTexts);
                return false;
            }

            _knownTexts[text] = 0;
            Volatile.Write(ref _textCount, _textCount + 1);
            var changed = new HashSet<char>();
            foreach (string form in Forms(text))
            {
                if (_owners.ContainsKey(form))
                {
                    continue; // a Steam name's form keeps its tag, another text's its label: hidden either way
                }

                _owners[form] = PlayerText;
                _textLabels[form] = label;
                AddForm(form);
                changed.Add(form[0]);
            }

            Publish(changed);
            return true;
        }
    }

    // Under _gate.
    private void AddForm(string form)
    {
        if (!_formsByFirst.TryGetValue(form[0], out List<string>? forms))
        {
            _formsByFirst[form[0]] = forms = new List<string>();
        }

        forms.Add(form);
    }

    /// <summary>
    /// Under _gate: a new snapshot for the buckets in <paramref name="changed"/> (the scrubbing threads keep reading the old
    /// one meanwhile). False when nothing changed.
    /// </summary>
    private bool Publish(HashSet<char> changed)
    {
        if (changed.Count == 0)
        {
            return false;
        }

        var byFirst = new Dictionary<char, NameForm[]>(_byFirst);
        foreach (char first in changed)
        {
            byFirst[first] = _formsByFirst[first]
                .OrderByDescending(form => form.Length).ThenBy(form => form, StringComparer.Ordinal)
                .Select(MakeForm).ToArray();
        }

        _byFirst = byFirst;
        return true;
    }

    // Under _gate.
    private NameForm MakeForm(string form)
    {
        ulong owner = _owners[form];
        return owner == PlayerText
            ? new NameForm(form, owner, IsQuotedOnlyText(form), _textLabels[form])
            : new NameForm(form, owner, IsQuotedOnly(form), null);
    }

    /// <summary>
    /// <paramref name="text"/> with the personal data in it replaced (see the class). Null gives "". The same instance
    /// when there is nothing to replace. Never throws.
    /// </summary>
    /// <param name="text">A log line (a stack trace's line breaks stay).</param>
    /// <param name="counts">Adds what was replaced, when given.</param>
    public string Scrub(string? text, LogScrubCounts? counts = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            LogTags tags = _tags;
            text = NeutraliseControls(text, keepLineBreaks: true, counts);
            text = ReplaceKey(text, tags, counts);
            text = ReplaceFolders(text, _folders, counts);
            text = ReplaceUserFolders(text, counts);
            bool players = false;
            text = ReplaceNames(text, _byFirst, tags, counts, ref players);
            text = ReplaceIds(text, tags, counts, ref players);
            text = ReplaceAddresses(text, counts);
            text = ReplaceOffsets(text, counts);
            return players ? Collapse(text) : text;
        }
        catch (Exception)
        {
            // Never into a log call: a line that couldn't be checked is not written as it was.
            return "(a log line couldn't be checked for personal data, so it was left out)";
        }
    }

    /// <summary>
    /// <paramref name="text"/> with control characters, Unicode line and paragraph separators, bidirectional controls,
    /// private-use characters and lone surrogates replaced by '?', so text from someone else can't forge or restyle a log
    /// line. With <paramref name="keepLineBreaks"/>, line feeds, carriage returns and tabs stay (a stack trace's lines). The
    /// same instance when nothing needs replacing.
    /// </summary>
    public static string NeutraliseControls(string text, bool keepLineBreaks, LogScrubCounts? counts = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (Unsafe(text, i, keepLineBreaks, out bool pair))
            {
                first = i;
                break;
            }

            if (pair)
            {
                i++;
            }
        }

        if (first < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            if (Unsafe(text, i, keepLineBreaks, out bool pair))
            {
                builder.Append('?');
                if (counts != null)
                {
                    counts.Controls++;
                }
            }
            else if (pair)
            {
                builder.Append(text[i]).Append(text[++i]);
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    private static bool Unsafe(string text, int i, bool keepLineBreaks, out bool pair)
    {
        char c = text[i];
        pair = false;
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            pair = true;
            return false;
        }

        if (keepLineBreaks && c is '\n' or '\r' or '\t')
        {
            return false;
        }

        return char.IsSurrogate(c) || SafeText.IsUnsafe(c);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsPathNameChar(char c) => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '~' or '$';

    private static string ReplaceFolders(string text, Folder[] folders, LogScrubCounts? counts)
    {
        if (folders.Length == 0 || (text.IndexOf('\\') < 0 && text.IndexOf('/') < 0))
        {
            return text;
        }

        foreach (Folder folder in folders)
        {
            text = ReplaceFolder(text, folder.Back, folder.Label, counts);
            text = ReplaceFolder(text, folder.Forward, folder.Label, counts);
        }

        return text;
    }

    private static string ReplaceFolder(string text, string folder, string label, LogScrubCounts? counts)
    {
        int at = text.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            int end = at + folder.Length;
            if (end == text.Length || !IsPathNameChar(text[end]))
            {
                text = string.Concat(text.AsSpan(0, at), label, text.AsSpan(end));
                end = at + label.Length;
                if (counts != null)
                {
                    counts.Folders++;
                }
            }

            at = end < text.Length ? text.IndexOf(folder, end, StringComparison.OrdinalIgnoreCase) : -1;
        }

        return text;
    }

    /// <summary>Any <c>X:\Users\&lt;name&gt;</c> (either slash) left after the known folders: the name is a Windows user's.</summary>
    private static string ReplaceUserFolders(string text, LogScrubCounts? counts)
    {
        foreach (string marker in UserMarkers)
        {
            int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (at >= 1)
            {
                int start = at - 1; // the drive letter
                int nameStart = at + marker.Length;
                int end = nameStart;
                while (end < text.Length && text[end] is not ('\\' or '/' or '"' or '\'' or '\n' or '\r'))
                {
                    end++;
                }

                if (char.IsLetter(text[start]) && end > nameStart && (start == 0 || !IsWordChar(text[start - 1])))
                {
                    text = string.Concat(text.AsSpan(0, start), UserFolderLabel, text.AsSpan(end));
                    if (counts != null)
                    {
                        counts.Folders++;
                    }

                    at = text.IndexOf(marker, start + UserFolderLabel.Length, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    at = text.IndexOf(marker, nameStart, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return text;
    }

    private static string ReplaceNames(string text, Dictionary<char, NameForm[]> names, LogTags tags, LogScrubCounts? counts, ref bool players)
    {
        if (names.Count == 0)
        {
            return text;
        }

        StringBuilder? builder = null;
        int copied = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (names.TryGetValue(text[i], out NameForm[]? forms) && Match(text, i, forms, out NameForm? form, out int length))
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, i - copied);
                if (form!.Label != null)
                {
                    builder.Append(form.Label);
                    if (counts != null)
                    {
                        counts.Texts++;
                    }
                }
                else
                {
                    builder.Append(form.Id == Ambiguous ? LogTags.UnknownPlayer : tags.Player(form.Id));
                    players = true;
                    if (counts != null)
                    {
                        counts.Names++;
                    }
                }

                i += length;
                copied = i;
                continue;
            }

            i++;
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>The longest of <paramref name="forms"/> (longest first) written at <paramref name="at"/>, in full or cut.</summary>
    private static bool Match(string text, int at, NameForm[] forms, out NameForm? match, out int length)
    {
        bool quoted = at > 0 && text[at - 1] == '"';
        bool startsToken = at == 0 || !IsWordChar(text[at - 1]);
        foreach (NameForm form in forms)
        {
            string name = form.Text;
            if (form.QuotedOnly ? !quoted : (IsWordChar(name[0]) && !startsToken))
            {
                continue;
            }

            int same = 0;
            int most = Math.Min(name.Length, text.Length - at);
            while (same < most && text[at + same] == name[same])
            {
                same++;
            }

            int end;
            if (same == name.Length)
            {
                end = at + same; // in full
                if (end < text.Length && IsWordChar(name[^1]) && IsWordChar(text[end]))
                {
                    continue;
                }
            }
            else if (same >= MinCut && at + same < text.Length && text[at + same] == Ellipsis)
            {
                end = at + same + 1; // cut, with its ellipsis
            }
            else
            {
                continue;
            }

            if (form.QuotedOnly && (end >= text.Length || text[end] != '"'))
            {
                continue;
            }

            match = form;
            length = end - at;
            return true;
        }

        match = null;
        length = 0;
        return false;
    }

    private static string ReplaceIds(string text, LogTags tags, LogScrubCounts? counts, ref bool players)
    {
        StringBuilder? builder = null;
        int copied = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (!Ascii.IsDigit(text[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && Ascii.IsDigit(text[i]))
            {
                i++;
            }

            int digits = i - start;
            if (digits is not (17 or 18) || !ulong.TryParse(text.AsSpan(start, digits), NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
            {
                continue;
            }

            string? tag = null;
            if (digits == 17 && IsSteamId(value))
            {
                tag = tags.Player(value);
                players = true;
                if (counts != null)
                {
                    counts.SteamIds++;
                }
            }
            else if (digits == 18 && IsLobbyId(value))
            {
                tag = tags.Lobby(value);
                if (counts != null)
                {
                    counts.Lobbies++;
                }
            }

            if (tag == null)
            {
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, start - copied).Append(tag);
            copied = i;
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    private static string ReplaceAddresses(string text, LogScrubCounts? counts)
    {
        StringBuilder? builder = null;
        int copied = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            bool starts = i == 0 || (!IsWordChar(text[i - 1]) && text[i - 1] is not ('.' or ':'));
            int length = 0;
            if (starts && Ascii.IsDigit(c))
            {
                length = IPv4Length(text, i);
            }

            if (length == 0 && starts && (Ascii.IsHexDigit(c) || c == ':'))
            {
                length = IPv6Length(text, i);
            }

            if (length == 0)
            {
                i++;
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, i - copied).Append(AddressLabel);
            if (counts != null)
            {
                counts.Addresses++;
            }

            i += length;
            copied = i;
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// The length of an IPv4 address at <paramref name="at"/>, or 0: four numbers of 0 to 255, the first not 0 and the last
    /// not 0 (a network's address, and what four-part version numbers such as 1.0.0.0 end in), not followed by more of
    /// a number or a dotted part (a version such as 1.2.3.4.U5000).
    /// </summary>
    private static int IPv4Length(string text, int at)
    {
        int i = at;
        int last = 0;
        for (int part = 0; part < 4; part++)
        {
            if (part > 0)
            {
                if (i >= text.Length || text[i] != '.')
                {
                    return 0;
                }

                i++;
            }

            int start = i;
            int value = 0;
            while (i < text.Length && Ascii.IsDigit(text[i]) && i - start < 3)
            {
                value = (value * 10) + (text[i] - '0');
                i++;
            }

            if (i == start || value > 255 || (i < text.Length && Ascii.IsDigit(text[i])) || (part == 0 && value == 0))
            {
                return 0;
            }

            last = value;
        }

        bool more = i < text.Length && (IsWordChar(text[i]) || (text[i] == '.' && i + 1 < text.Length && IsWordChar(text[i + 1])));
        return more || last == 0 ? 0 : i - at;
    }

    /// <summary>
    /// The length of an IPv6 address at <paramref name="at"/>, or 0: hex groups and colons (an IPv4 tail allowed) with
    /// "::" or seven colons, at least three hex digits and one decimal digit (so a name such as Face::Add isn't one), standing
    /// alone, and one <see cref="IPAddress"/> reads.
    /// </summary>
    private static int IPv6Length(string text, int at)
    {
        int i = at;
        int colons = 0;
        int hex = 0;
        int digits = 0;
        bool doubled = false;
        while (i < text.Length && (Ascii.IsHexDigit(text[i]) || text[i] is ':' or '.'))
        {
            if (text[i] == ':')
            {
                colons++;
                doubled |= i > at && text[i - 1] == ':';
            }
            else if (text[i] != '.')
            {
                hex++;
                digits += Ascii.IsDigit(text[i]) ? 1 : 0;
            }

            i++;
        }

        while (i > at && text[i - 1] == '.')
        {
            i--; // a sentence's full stop
        }

        if ((!doubled && colons < 7) || hex < 3 || digits == 0 || (i < text.Length && IsWordChar(text[i])))
        {
            return 0;
        }

        return IPAddress.TryParse(text.AsSpan(at, i - at), out IPAddress? address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? i - at : 0;
    }

    /// <summary>The tags' key, as 32 hex digits standing alone: never written by design, replaced should a line hold it.</summary>
    private static string ReplaceKey(string text, LogTags tags, LogScrubCounts? counts)
    {
        StringBuilder? builder = null;
        int copied = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (!Ascii.IsHexDigit(text[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && Ascii.IsHexDigit(text[i]))
            {
                i++;
            }

            if (i - start != LogTags.KeyHexLength || !tags.IsKey(text.AsSpan(start, i - start)))
            {
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, start - copied).Append(KeyLabel);
            copied = i;
            if (counts != null)
            {
                counts.Keys++;
            }
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    private static string ReplaceOffsets(string text, LogScrubCounts? counts)
    {
        int at = IndexOfZone(text, 0);
        if (at < 0)
        {
            return text;
        }

        StringBuilder? builder = null; // made at the first offset: "UTC date ..." alone allocates nothing
        int copied = 0;
        while (at >= 0)
        {
            int i = at + 3;
            int end = 0;
            if (i < text.Length && text[i] is '+' or '-' or '\u2212')
            {
                int digits = i + 1;
                int j = digits;
                while (j < text.Length && Ascii.IsDigit(text[j]) && j - digits < 2)
                {
                    j++;
                }

                if (j > digits)
                {
                    end = j;
                    if (j + 2 < text.Length && text[j] == ':' && Ascii.IsDigit(text[j + 1]) && Ascii.IsDigit(text[j + 2]))
                    {
                        end = j + 3;
                    }
                    else if (j + 1 < text.Length && text[j] == ':' && Ascii.IsDigit(text[j + 1]))
                    {
                        end = 0; // not an offset's minutes
                    }
                }
            }

            if (end > 0 && (end == text.Length || !Ascii.IsDigit(text[end])))
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, i - copied).Append(OffsetLabel);
                copied = end;
                if (counts != null)
                {
                    counts.Offsets++;
                }
            }

            at = IndexOfZone(text, Math.Max(at + 3, copied));
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    private static int IndexOfZone(string text, int from)
    {
        while (from < text.Length)
        {
            int utc = text.IndexOf("UTC", from, StringComparison.Ordinal);
            int gmt = text.IndexOf("GMT", from, StringComparison.Ordinal);
            int at = utc < 0 ? gmt : gmt < 0 ? utc : Math.Min(utc, gmt);
            if (at < 0)
            {
                return -1;
            }

            if (at == 0 || !char.IsLetter(text[at - 1]))
            {
                return at;
            }

            from = at + 3;
        }

        return -1;
    }

    /// <summary>
    /// <c>"P#a0c1d2" (P#a0c1d2)</c> or <c>P#a0c1d2 (P#a0c1d2)</c>, a name and its ID made tags, as just <c>P#a0c1d2</c>.
    /// </summary>
    private static string Collapse(string text)
    {
        int at = text.IndexOf("P#", StringComparison.Ordinal);
        if (at < 0)
        {
            return text;
        }

        StringBuilder? builder = null;
        int copied = 0;
        while (at >= 0)
        {
            int tagEnd = at + 2;
            while (tagEnd < text.Length && (Ascii.IsHexDigit(text[tagEnd]) || text[tagEnd] == '?'))
            {
                tagEnd++;
            }

            int tagLength = tagEnd - at;
            bool quoted = at > 0 && text[at - 1] == '"' && tagEnd < text.Length && text[tagEnd] == '"';
            int open = quoted ? tagEnd + 1 : tagEnd; // where " (" should start
            int next = tagEnd;
            if (tagLength is 2 + LogTags.ShortDigits or 2 + LogTags.LongDigits &&
                string.CompareOrdinal(text, open, " (", 0, 2) == 0 &&
                string.CompareOrdinal(text, open + 2, text, at, tagLength) == 0 &&
                open + 2 + tagLength < text.Length && text[open + 2 + tagLength] == ')')
            {
                int start = quoted ? at - 1 : at;
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, start - copied).Append(text, at, tagLength);
                next = open + 2 + tagLength + 1;
                copied = next;
            }

            at = next < text.Length ? text.IndexOf("P#", next, StringComparison.Ordinal) : -1;
        }

        if (builder == null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    private sealed record Folder(string Back, string Forward, string Label);

    // Label: what a player-written name's form becomes (null for a Steam name's: its owner's tag).
    private sealed record NameForm(string Text, ulong Id, bool QuotedOnly, string? Label);

    /// <summary>
    /// The forms the code writes a name in: as given, trimmed, cleaned for a log line, cleaned for a label, and drawn on a
    /// label (<see cref="DrawnGuesses"/>).
    /// </summary>
    private static HashSet<string> Forms(string name)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal) { name, name.Trim() };
        forms.Add(SafeText.Clean(name, Math.Max(2, name.Length)));
        forms.Add(SafeText.Clean(name.Trim(), Math.Max(2, name.Length)));
        forms.Add(NeutraliseControls(name, keepLineBreaks: true)); // a raw name in a line, after the line's own cleaning
        string none = DisplayName.Clean(null, 1);
        string label = DisplayName.Clean(name, 1, maxChars: Math.Max(2, name.Length));
        if (label != none)
        {
            forms.Add(label);
        }

        foreach (Func<int, bool> drawable in DrawnGuesses)
        {
            string drawn = DisplayName.Clean(name, 1, canDraw: drawable, maxChars: Math.Max(2, name.Length));
            if (drawn != none)
            {
                forms.Add(drawn);
            }
        }

        forms.RemoveWhere(string.IsNullOrWhiteSpace);
        return forms;
    }

    /// <summary>
    /// A name the log's own text could hold: a single ASCII character, one made to look like a tag (<c>P#3fa9c1</c>: it
    /// would take that player's tag over in every line), or nothing but the log's words, ASCII numbers, ASCII punctuation
    /// and spaces (<c>Online</c>, <c>the host</c>, <c>Online:</c>, <c>Player 2</c>, <c>...</c>).
    /// </summary>
    private static bool IsQuotedOnly(string name)
    {
        if ((name.Length == 1 && name[0] < 128) || name.Contains("P#", StringComparison.Ordinal) || name.Contains("L#", StringComparison.Ordinal))
        {
            return true;
        }

        int i = 0;
        while (i < name.Length)
        {
            if (!char.IsLetterOrDigit(name[i]))
            {
                if (name[i] >= 128)
                {
                    return false; // an emoji or another symbol: nothing the log writes itself
                }

                i++;
                continue;
            }

            int start = i;
            while (i < name.Length && char.IsLetterOrDigit(name[i]))
            {
                i++;
            }

            string word = name.Substring(start, i - start);
            if (!LogWords.Contains(word) && !IsAsciiNumber(word))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A player-written name's form the log's own text could hold: one the names' rule keeps quoted-only
    /// (<see cref="IsQuotedOnly"/>), one shorter than <see cref="MinPlayerText"/> characters, or a single word (one run of
    /// letters and digits with only ASCII around it: <c>Rockets</c>, <c>logo</c>, <c>Riley!</c>), which a custom team or a
    /// logo's file is often called and the log may write as an ordinary word. An emoji or another symbol makes a form
    /// distinctive, as it does a Steam name.
    /// </summary>
    private static bool IsQuotedOnlyText(string form)
    {
        if (IsQuotedOnly(form) || form.Trim().Length < MinPlayerText)
        {
            return true;
        }

        int runs = 0;
        bool inRun = false;
        foreach (char c in form)
        {
            bool part = char.IsLetterOrDigit(c);
            if (!part && c >= 128 && !char.IsWhiteSpace(c))
            {
                return false; // nothing the log writes itself
            }

            if (part && !inRun)
            {
                runs++;
            }

            inRun = part;
        }

        return runs <= 1;
    }

    private static bool IsAsciiNumber(string word)
    {
        foreach (char c in word)
        {
            if (!Ascii.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
