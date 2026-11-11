using System;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>What a menu attachment carries. Values are part of the protocol: only add.</summary>
public enum MenuAttachmentKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>
    /// The run-end menu's run: the run history entry the host's game builds for the run that just ended
    /// (<see cref="RunHistoryData"/>'s fields), so a friend's game shows the host's run's end from it.
    /// </summary>
    RunSummary = 1,

    /// <summary>
    /// An event menu's choices: the <see cref="EventChoiceSet"/> the host's game rolled for it (at its open, and again for
    /// each page or reroll), so a friend's game shows the host's event screen with the host's choices.
    /// </summary>
    EventChoices = 2,

    /// <summary>
    /// An event menu's NPC line: the <see cref="NpcLineData"/> of the line the host's NPC just spoke (again for each line), so
    /// a friend's game plays the same line, in its own language.
    /// </summary>
    NpcLine = 3,

    /// <summary>
    /// A prompt's data: the <see cref="PromptData"/> the host's prompt was made from, so a friend's game makes the same prompt
    /// (each text from the term the host's game made it from, in the friend's own language; else the host's text).
    /// </summary>
    Prompt = 4,

    /// <summary>
    /// A run-start menu's run and offers: the <see cref="RunPreviewSet"/> the host's game holds when its old squad menu opens
    /// (its in-memory run, written by a copy of the game's run repository, and the menu's own rolls), so a friend's game
    /// loads the host's new run as a preview (its first real checkpoint replaces it) and shows the host's offers; at the lineup
    /// with benchwarmers, the run and the lineup's benchwarmers (format 4).
    /// </summary>
    RunPreview = 5,

    /// <summary>
    /// The unlockables screen's profile part: the <see cref="UnlockStatesData"/> the host's menu showed (each row's unlocked,
    /// progress and total, the quest rows, the tabs), so a friend's game builds its own menu and shows the host's states on
    /// it, in its own language.
    /// </summary>
    UnlockStates = 6,

    /// <summary>
    /// A run history page at the title: the <see cref="HistoryPageData"/> the host's run-end menu shows in its history mode
    /// (again for each page), so a friend's game shows the same run on the same page, at the host's local time.
    /// </summary>
    HistoryPage = 7,

    /// <summary>
    /// The General Manager's rerolled map node rewards: the <see cref="NodeRewardsData"/> the host's map holds after its
    /// <c>RerollSkaterNodeRewards</c> (again at each reroll), so a friend's map shows the same rewards at once.
    /// </summary>
    NodeRewards = 8,

    /// <summary>
    /// A Nurse event menu's roster: the <see cref="NurseRosterData"/> the host's Nurse drew (at the menu's open; again with
    /// its procedure's outcome once committed), so a friend's game rebuilds the same roster with the game's own factory and
    /// opens the host's procedure screen for real.
    /// </summary>
    NurseRoster = 9,

    /// <summary>
    /// A Nurse event menu's procedure screen steps: the <see cref="NurseStepsData"/> of the host's newest presses on it (again
    /// with each one), so a friend's game replays them through its own screen's input handlers.
    /// </summary>
    NurseSteps = 10,

    /// <summary>
    /// An exhibition controller screen's custom content: the <see cref="ControllerContentData"/> its two teams need (again when
    /// they change), so a friend's game shows a custom team of the host's on its real screen.
    /// </summary>
    ControllerContent = 11,
}

/// <summary>
/// Data that goes with one menu open on the host's screen and doesn't fit its <see cref="MenuData"/> (whose rows hold
/// <see cref="MenuData.MaxItemValues"/> short values each): a kind, the kind's layout version, and a body, for the menu with
/// the host's serial. The host sends it after the menu's open (and again to a friend who joins while the menu is open; an
/// event menu's choices again with each new set, its NPC line with each line); a friend keeps the newest of each kind for
/// each open menu (<see cref="MenuAttachmentStore"/>). A body is checked against its kind when it's made and again when it's read, so any
/// attachment a friend holds is one the glue can use. Immutable.
/// <para>
/// Each kind has a factory (<see cref="RunSummary"/>, <see cref="EventChoices"/>, <see cref="NpcLine"/>,
/// <see cref="Prompt"/>, <see cref="RunPreview"/>, <see cref="UnlockStates"/>, <see cref="HistoryPage"/>,
/// <see cref="NodeRewards"/>, <see cref="NurseRoster"/>, <see cref="NurseSteps"/>, <see cref="ControllerContent"/>), which throws for data out of its
/// limits, and a reader (<see cref="TryGetRunSummary"/> and the
/// rest), which never throws. A new kind adds its value to <see cref="MenuAttachmentKind"/>, a format constant, a factory, a
/// reader and a case in <see cref="IsKnown"/> and in the body check.
/// </para>
/// </summary>
public sealed class MenuAttachment
{
    /// <summary>Largest body, uncompressed: the run history entry's own cap.</summary>
    public const int MaxBodyBytes = ProgressLimits.MaxRunHistoryBytes;

    /// <summary>The run summary body's layout: <see cref="RunHistoryData"/>'s fields, and nothing after them.</summary>
    public const byte RunSummaryFormat = 1;

    /// <summary>
    /// The event choices body's layout: <see cref="EventChoiceSet"/>'s fields, and nothing after them. Format 3 (format 2's
    /// free agents, previews, takeovers and longer texts, with each text's recipe, each image's source and each free agent's
    /// look); a format 1 or 2 body is refused.
    /// </summary>
    public const byte EventChoicesFormat = 3;

    /// <summary>The NPC line body's layout: <see cref="NpcLineData"/>'s fields, and nothing after them.</summary>
    public const byte NpcLineFormat = 1;

    /// <summary>
    /// The prompt body's layout: <see cref="PromptData"/>'s fields, and nothing after them. Format 2: each text
    /// with how it's made (<see cref="PromptText"/>); a format 1 body (one "from keys" flag for every text) is refused.
    /// </summary>
    public const byte PromptFormat = 2;

    /// <summary>
    /// The run preview body's layout for an old squad menu: <see cref="RunPreviewSet"/>'s fields, and nothing after them. Format 3:
    /// each skater's number format follows its look; a format 1 body is refused.
    /// </summary>
    public const byte RunPreviewFormat = 3;

    /// <summary>The unlock states body's layout: <see cref="UnlockStatesData"/>'s fields, and nothing after them.</summary>
    public const byte UnlockStatesFormat = 1;

    /// <summary>The history page body's layout: <see cref="HistoryPageData"/>'s fields, and nothing after them.</summary>
    public const byte HistoryPageFormat = 1;

    /// <summary>
    /// The run preview body's layout for the lineup with benchwarmers: format 3's fields, then the lineup's pool
    /// (<see cref="RunPreviewSet.Pool"/>), and nothing after. Format 4 (as format 3: each skater's number
    /// format); a format 2 body is refused.
    /// </summary>
    public const byte RunPreviewLineupFormat = 4;

    /// <summary>The node rewards body's layout: <see cref="NodeRewardsData"/>'s fields, and nothing after them.</summary>
    public const byte NodeRewardsFormat = 1;

    /// <summary>The Nurse roster body's layout: <see cref="NurseRosterData"/>'s fields with no outcome, and nothing after them.</summary>
    public const byte NurseRosterFormat = 1;

    /// <summary>The Nurse roster body's layout once the host's procedure is committed: the same fields, then its outcome.</summary>
    public const byte NurseOutcomeFormat = 2;

    /// <summary>The Nurse steps body's layout: <see cref="NurseStepsData"/>'s fields, and nothing after them.</summary>
    public const byte NurseStepsFormat = 1;

    /// <summary>The controller screen's custom content body's layout: <see cref="ControllerContentData"/>'s fields, and nothing after them.</summary>
    public const byte ControllerContentFormat = 1;

    private readonly byte[] _body;
    private byte[]? _deflated;

    private MenuAttachment(uint serial, MenuAttachmentKind kind, byte format, byte[] body)
    {
        Serial = serial;
        Kind = kind;
        Format = format;
        _body = body;
        ContentHash = Fnv.Add(Fnv.Add(Fnv.Add(Fnv.OffsetBasis, (byte)kind), format), body);
    }

    /// <summary>The host's number for the menu it goes with (never 0).</summary>
    public uint Serial { get; }

    /// <summary>What it carries.</summary>
    public MenuAttachmentKind Kind { get; }

    /// <summary>The body's layout version for its kind.</summary>
    public byte Format { get; }

    /// <summary>The body, uncompressed.</summary>
    public ReadOnlySpan<byte> Body => _body;

    /// <summary>The body's length, uncompressed.</summary>
    public int BodyLength => _body.Length;

    /// <summary>The body's length as it's sent (deflated, once).</summary>
    public int DeflatedLength => Deflated().Length;

    /// <summary>A hash of the kind, format and body: equal hashes, same attachment (for logs).</summary>
    public ulong ContentHash { get; }

    /// <summary>
    /// The run summary for menu <paramref name="serial"/>: <paramref name="entry"/>'s fields. Throws for serial 0, or for an
    /// entry over <see cref="RunHistoryData"/>'s limits (a list or text too long, or more than <see cref="MaxBodyBytes"/>).
    /// </summary>
    public static MenuAttachment RunSummary(uint serial, RunHistoryData entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (serial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serial), "Serial 0 is no menu.");
        }

        var writer = new BitWriter(1024);
        entry.Write(writer);
        byte[] body = writer.ToArray();
        if (!IsValidBody(MenuAttachmentKind.RunSummary, RunSummaryFormat, body))
        {
            throw new ArgumentException($"The entry is over the run history entry's limits ({body.Length} bytes; at most {MaxBodyBytes}).", nameof(entry));
        }

        return new MenuAttachment(serial, MenuAttachmentKind.RunSummary, RunSummaryFormat, body);
    }

    /// <summary>
    /// The event choices for menu <paramref name="serial"/>: <paramref name="set"/>'s fields. Throws for serial 0, or for a
    /// set out of <see cref="EventChoiceSet"/>'s limits (saying what).
    /// </summary>
    public static MenuAttachment EventChoices(uint serial, EventChoiceSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Make(serial, MenuAttachmentKind.EventChoices, EventChoicesFormat, set.Write, "event choices", nameof(set));
    }

    /// <summary>
    /// The NPC line for (event) menu <paramref name="serial"/>: <paramref name="line"/>'s fields. Throws for serial 0, or for a
    /// line out of <see cref="NpcLineData"/>'s limits (saying what).
    /// </summary>
    public static MenuAttachment NpcLine(uint serial, NpcLineData line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Make(serial, MenuAttachmentKind.NpcLine, NpcLineFormat, line.Write, "NPC line", nameof(line));
    }

    /// <summary>
    /// The prompt data for (prompt) menu <paramref name="serial"/>: <paramref name="prompt"/>'s fields. Throws for serial 0,
    /// or for a prompt out of <see cref="PromptData"/>'s limits (saying what).
    /// </summary>
    public static MenuAttachment Prompt(uint serial, PromptData prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return Make(serial, MenuAttachmentKind.Prompt, PromptFormat, prompt.Write, "prompt", nameof(prompt));
    }

    /// <summary>
    /// The run preview for (run-start) menu <paramref name="serial"/>: <paramref name="set"/>'s fields. Throws for serial 0, or
    /// for a set out of <see cref="RunPreviewSet"/>'s limits (saying what), or over <see cref="MaxBodyBytes"/>.
    /// </summary>
    public static MenuAttachment RunPreview(uint serial, RunPreviewSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Make(serial, MenuAttachmentKind.RunPreview, set.Format, set.Write, "run preview", nameof(set));
    }

    /// <summary>
    /// The unlock states for (unlockables) menu <paramref name="serial"/>: <paramref name="states"/>' fields. Throws for serial
    /// 0, or for states out of <see cref="UnlockStatesData"/>'s limits (saying what), or over <see cref="MaxBodyBytes"/>.
    /// </summary>
    public static MenuAttachment UnlockStates(uint serial, UnlockStatesData states)
    {
        ArgumentNullException.ThrowIfNull(states);
        return Make(serial, MenuAttachmentKind.UnlockStates, UnlockStatesFormat, states.Write, "unlock states", nameof(states));
    }

    /// <summary>
    /// The history page for (run-end) menu <paramref name="serial"/>: <paramref name="page"/>'s fields. Throws for serial 0,
    /// or for a page out of <see cref="HistoryPageData"/>'s or its run's limits (saying what), or over <see cref="MaxBodyBytes"/>.
    /// </summary>
    public static MenuAttachment HistoryPage(uint serial, HistoryPageData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Make(serial, MenuAttachmentKind.HistoryPage, HistoryPageFormat, page.Write, "history page", nameof(page));
    }

    /// <summary>
    /// The node rewards for (event) menu <paramref name="serial"/>: <paramref name="rewards"/>' fields. Throws for serial 0,
    /// or for rewards out of <see cref="NodeRewardsData"/>'s limits (saying what).
    /// </summary>
    public static MenuAttachment NodeRewards(uint serial, NodeRewardsData rewards)
    {
        ArgumentNullException.ThrowIfNull(rewards);
        return Make(serial, MenuAttachmentKind.NodeRewards, NodeRewardsFormat, rewards.Write, "node rewards", nameof(rewards));
    }

    /// <summary>
    /// The Nurse roster for (event) menu <paramref name="serial"/>: <paramref name="roster"/>'s fields (format 2 when it carries
    /// an outcome). Throws for serial 0, or for a roster out of <see cref="NurseRosterData"/>'s limits (saying what).
    /// </summary>
    public static MenuAttachment NurseRoster(uint serial, NurseRosterData roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return Make(serial, MenuAttachmentKind.NurseRoster, roster.Format, roster.Write, "Nurse roster", nameof(roster));
    }

    /// <summary>
    /// The Nurse procedure screen's steps for (event) menu <paramref name="serial"/>: <paramref name="steps"/>' fields. Throws
    /// for serial 0, or for steps out of <see cref="NurseStepsData"/>'s limits (saying what).
    /// </summary>
    public static MenuAttachment NurseSteps(uint serial, NurseStepsData steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return Make(serial, MenuAttachmentKind.NurseSteps, NurseStepsFormat, steps.Write, "Nurse steps", nameof(steps));
    }

    /// <summary>
    /// The custom content the teams on (controller screen) menu <paramref name="serial"/> need: <paramref name="content"/>'s
    /// fields. Throws for serial 0, or for a reference too long to carry.
    /// </summary>
    public static MenuAttachment ControllerContent(uint serial, ControllerContentData content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Make(serial, MenuAttachmentKind.ControllerContent, ControllerContentFormat, content.Write, "controller screen's custom content", nameof(content));
    }

    /// <summary>True for a kind this version reads.</summary>
    public static bool IsKnown(MenuAttachmentKind kind) =>
        kind is MenuAttachmentKind.RunSummary or MenuAttachmentKind.EventChoices or MenuAttachmentKind.NpcLine or MenuAttachmentKind.Prompt or MenuAttachmentKind.RunPreview or
            MenuAttachmentKind.UnlockStates or MenuAttachmentKind.HistoryPage or MenuAttachmentKind.NodeRewards or
            MenuAttachmentKind.NurseRoster or MenuAttachmentKind.NurseSteps or MenuAttachmentKind.ControllerContent;

    /// <summary>The run summary it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetRunSummary(out RunHistoryData? entry)
    {
        entry = null;
        return Kind == MenuAttachmentKind.RunSummary && TryReadRunSummary(_body, out entry);
    }

    /// <summary>The event choices it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetEventChoices(out EventChoiceSet? set)
    {
        set = null;
        return Kind == MenuAttachmentKind.EventChoices && TryReadEventChoices(_body, out set);
    }

    /// <summary>The NPC line it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetNpcLine(out NpcLineData? line)
    {
        line = null;
        return Kind == MenuAttachmentKind.NpcLine && TryReadWhole(_body, NpcLineData.TryRead, out line);
    }

    /// <summary>The prompt data it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetPrompt(out PromptData? prompt)
    {
        prompt = null;
        return Kind == MenuAttachmentKind.Prompt && TryReadWhole(_body, PromptData.TryRead, out prompt);
    }

    /// <summary>The run preview it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetRunPreview(out RunPreviewSet? set)
    {
        set = null;
        return Kind == MenuAttachmentKind.RunPreview && TryReadWhole(_body, RunPreviewSet.TryRead, out set);
    }

    /// <summary>The unlock states it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetUnlockStates(out UnlockStatesData? states)
    {
        states = null;
        return Kind == MenuAttachmentKind.UnlockStates && TryReadWhole(_body, UnlockStatesData.TryRead, out states);
    }

    /// <summary>The history page it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetHistoryPage(out HistoryPageData? page)
    {
        page = null;
        return Kind == MenuAttachmentKind.HistoryPage && TryReadWhole(_body, HistoryPageData.TryRead, out page);
    }

    /// <summary>The node rewards it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetNodeRewards(out NodeRewardsData? rewards)
    {
        rewards = null;
        return Kind == MenuAttachmentKind.NodeRewards && TryReadWhole(_body, NodeRewardsData.TryRead, out rewards);
    }

    /// <summary>The Nurse roster it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetNurseRoster(out NurseRosterData? roster)
    {
        roster = null;
        return Kind == MenuAttachmentKind.NurseRoster && TryReadWhole(_body, NurseRosterData.TryRead, out roster);
    }

    /// <summary>The Nurse procedure screen's steps it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetNurseSteps(out NurseStepsData? steps)
    {
        steps = null;
        return Kind == MenuAttachmentKind.NurseSteps && TryReadWhole(_body, NurseStepsData.TryRead, out steps);
    }

    /// <summary>The controller screen's custom content it carries. False when it carries something else. Never throws.</summary>
    public bool TryGetControllerContent(out ControllerContentData? content)
    {
        content = null;
        return Kind == MenuAttachmentKind.ControllerContent && TryReadWhole(_body, ControllerContentData.TryRead, out content);
    }

    /// <summary>For logs: "run summary for #28, 3120 bytes", "event choices for #13, 840 bytes", "NPC line for #13, 31 bytes".</summary>
    public override string ToString()
    {
        string what = Kind switch
        {
            MenuAttachmentKind.RunSummary => "run summary",
            MenuAttachmentKind.EventChoices => "event choices",
            MenuAttachmentKind.NpcLine => "NPC line",
            MenuAttachmentKind.Prompt => "prompt",
            MenuAttachmentKind.RunPreview => "run preview",
            MenuAttachmentKind.UnlockStates => "unlock states",
            MenuAttachmentKind.HistoryPage => "history page",
            MenuAttachmentKind.NodeRewards => "node rewards",
            MenuAttachmentKind.NurseRoster => Format == NurseOutcomeFormat ? "Nurse roster and outcome" : "Nurse roster",
            MenuAttachmentKind.NurseSteps => "Nurse steps",
            MenuAttachmentKind.ControllerContent => "controller screen's custom content",
            _ => Kind.ToString(),
        };
        return $"{what} for #{Serial}, {BodyLength} bytes";
    }

    /// <summary>
    /// An attachment read from the wire, its body already inflated: null unless the serial is a menu's, the kind and its
    /// format are known, and the body is exactly one of that kind (nothing more).
    /// </summary>
    internal static MenuAttachment? FromWire(uint serial, MenuAttachmentKind kind, byte format, byte[] body) =>
        serial != 0 && IsValidBody(kind, format, body) ? new MenuAttachment(serial, kind, format, body) : null;

    /// <summary>The body deflated, made once (it goes to every friend, and again to one who joins).</summary>
    internal byte[] Deflated() => _deflated ??= Compression.Deflate(_body);

    /// <summary>True when <paramref name="body"/> is one whole body of <paramref name="kind"/> in <paramref name="format"/>, within the size cap.</summary>
    private static bool IsValidBody(MenuAttachmentKind kind, byte format, byte[] body)
    {
        if (body.Length is 0 or > MaxBodyBytes)
        {
            return false;
        }

        return kind switch
        {
            MenuAttachmentKind.RunSummary => format == RunSummaryFormat && TryReadRunSummary(body, out _),
            MenuAttachmentKind.EventChoices => format == EventChoicesFormat && TryReadEventChoices(body, out _),
            MenuAttachmentKind.NpcLine => format == NpcLineFormat && TryReadWhole<NpcLineData>(body, NpcLineData.TryRead, out _),
            MenuAttachmentKind.Prompt => format == PromptFormat && TryReadWhole<PromptData>(body, PromptData.TryRead, out _),
            MenuAttachmentKind.RunPreview => format is RunPreviewFormat or RunPreviewLineupFormat &&
                TryReadWhole<RunPreviewSet>(body, RunPreviewSet.TryRead, out RunPreviewSet? set) && set!.Format == format,
            MenuAttachmentKind.UnlockStates => format == UnlockStatesFormat && TryReadWhole<UnlockStatesData>(body, UnlockStatesData.TryRead, out _),
            MenuAttachmentKind.HistoryPage => format == HistoryPageFormat && TryReadWhole<HistoryPageData>(body, HistoryPageData.TryRead, out _),
            MenuAttachmentKind.NodeRewards => format == NodeRewardsFormat && TryReadWhole<NodeRewardsData>(body, NodeRewardsData.TryRead, out _),
            MenuAttachmentKind.NurseRoster => format is NurseRosterFormat or NurseOutcomeFormat &&
                TryReadWhole<NurseRosterData>(body, NurseRosterData.TryRead, out NurseRosterData? roster) && roster!.Format == format,
            MenuAttachmentKind.NurseSteps => format == NurseStepsFormat && TryReadWhole<NurseStepsData>(body, NurseStepsData.TryRead, out _),
            MenuAttachmentKind.ControllerContent => format == ControllerContentFormat && TryReadWhole<ControllerContentData>(body, ControllerContentData.TryRead, out _),
            _ => false,
        };
    }

    /// <summary>
    /// An attachment of <paramref name="kind"/> for menu <paramref name="serial"/>, its body written by <paramref name="write"/>.
    /// Throws for serial 0, for a body its writer refuses (out of its limits), or for one that doesn't read back (over the cap).
    /// </summary>
    private static MenuAttachment Make(uint serial, MenuAttachmentKind kind, byte format, Action<BitWriter> write, string what, string parameter)
    {
        if (serial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serial), "Serial 0 is no menu.");
        }

        var writer = new BitWriter(1024);
        try
        {
            write(writer);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"The {what} are out of their limits ({ex.Message}).", parameter, ex);
        }

        byte[] body = writer.ToArray();
        if (!IsValidBody(kind, format, body))
        {
            throw new ArgumentException($"The {what} don't read back as written ({body.Length} bytes; at most {MaxBodyBytes}).", parameter);
        }

        return new MenuAttachment(serial, kind, format, body);
    }

    /// <summary>A kind's body reader (its type's <c>TryRead</c>).</summary>
    private delegate bool BodyReader<T>(ref BitReader reader, out T? value)
        where T : class;

    /// <summary>A body that is one <typeparamref name="T"/> and nothing more (as for a run summary). Never throws.</summary>
    private static bool TryReadWhole<T>(byte[] body, BodyReader<T> read, out T? value)
        where T : class
    {
        var reader = new BitReader(body);
        if (read(ref reader, out value) && reader.RemainingBits < 8)
        {
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryReadRunSummary(byte[] body, out RunHistoryData? entry)
    {
        // The entry must be all there is: bytes it doesn't read (zeros padding a small entry out) are refused, as in a
        // progress batch's run history entry.
        var reader = new BitReader(body);
        if (RunHistoryData.TryRead(ref reader, out entry) && reader.RemainingBits < 8)
        {
            return true;
        }

        entry = null;
        return false;
    }

    private static bool TryReadEventChoices(byte[] body, out EventChoiceSet? set)
    {
        // The set must be all there is, as for a run summary.
        var reader = new BitReader(body);
        if (EventChoiceSet.TryRead(ref reader, out set) && reader.RemainingBits < 8)
        {
            return true;
        }

        set = null;
        return false;
    }
}

/// <summary>
/// Host to friend (channel 1, <see cref="MessageType.MenuAttachment"/>, after the header): one <see cref="MenuAttachment"/>.
/// <code>
/// u32 serial · u8 kind · u8 format · varuint body length · varuint deflated length · the body, deflated
/// </code>
/// Reading checks every field before it allocates or inflates anything: the serial, the kind and its format, both lengths
/// against their caps and against what's left of the message, the inflated length against the one stated (inflating stops
/// at it), the body against its kind, and that nothing follows. A bad message reads as false; it never throws.
/// </summary>
public static class MenuAttachmentMessage
{
    /// <summary>
    /// Largest deflated body: a body at <see cref="MenuAttachment.MaxBodyBytes"/> that doesn't compress at all (deflate's
    /// stored blocks add a few bytes per 64 KB), with room to spare.
    /// </summary>
    public const int MaxDeflatedBytes = MenuAttachment.MaxBodyBytes + 1024;

    /// <summary>Largest message after the header, in bytes: within Steam's 512 KB reliable messages.</summary>
    public const int MaxMessageBytes = MaxDeflatedBytes + 32;

    /// <summary>Writes <paramref name="attachment"/>'s payload (after the header). False when it would be over <see cref="MaxMessageBytes"/>.</summary>
    public static bool Write(BitWriter writer, MenuAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(attachment);
        byte[] deflated = attachment.Deflated();
        if (deflated.Length > MaxDeflatedBytes)
        {
            return false;
        }

        writer.WriteUInt32(attachment.Serial);
        writer.WriteByte((byte)attachment.Kind);
        writer.WriteByte(attachment.Format);
        writer.WriteVarUInt((ulong)attachment.BodyLength);
        writer.WriteVarUInt((ulong)deflated.Length);
        writer.WriteBytes(deflated);
        return true;
    }

    /// <summary>
    /// Reads an attachment (its header already read). False for serial 0, an unknown kind or format, a length out of
    /// range or not matching what follows, a body that inflates to more (or less) than it says or isn't one of its kind,
    /// or anything left over. Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out MenuAttachment? attachment)
    {
        attachment = null;
        if (reader.RemainingBits > (long)MaxMessageBytes * 8)
        {
            return false;
        }

        uint serial = reader.ReadUInt32();
        var kind = (MenuAttachmentKind)reader.ReadByte();
        byte format = reader.ReadByte();
        ulong bodyLength = reader.ReadVarUInt();
        ulong deflatedLength = reader.ReadVarUInt();
        if (reader.IsOverrun || serial == 0 || !MenuAttachment.IsKnown(kind) || bodyLength is 0 or > MenuAttachment.MaxBodyBytes ||
            deflatedLength is 0 or > MaxDeflatedBytes || (long)deflatedLength * 8 > reader.RemainingBits)
        {
            return false;
        }

        byte[] deflated = new byte[(int)deflatedLength];
        reader.ReadBytes(deflated);
        if (reader.IsOverrun || reader.RemainingBits >= 8)
        {
            return false; // something follows: not one of ours
        }

        // Inflating stops at the stated length: a deflate bomb can't make this PC inflate more than the cap.
        if (!Compression.TryInflate(deflated, (int)bodyLength, out byte[] body) || body.Length != (int)bodyLength)
        {
            return false;
        }

        attachment = MenuAttachment.FromWire(serial, kind, format, body);
        return attachment != null;
    }
}
