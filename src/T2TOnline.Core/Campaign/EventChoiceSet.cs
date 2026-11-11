using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>What made an <see cref="EventChoiceSet"/>. Values are part of the protocol: only add.</summary>
public enum EventSetStep : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>The choices the event menu opened with.</summary>
    Open = 1,

    /// <summary>The next set of choices after a pick or a skip (the Coach's double coaching, say).</summary>
    Page = 2,

    /// <summary>The choices a reroll put in place of the old ones.</summary>
    Reroll = 3,
}

/// <summary>What an event choice's power-up or relic is. Values are part of the protocol: only add.</summary>
public enum EventObjectKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>A relic (the game's <c>Relic</c>, or a subclass of it).</summary>
    Relic = 1,

    /// <summary>A talent (the game's <c>Talent</c>, or a subclass of it).</summary>
    Talent = 2,

    /// <summary>An ability (the game's <c>Ability</c>, or a subclass of it).</summary>
    Ability = 3,

    /// <summary>
    /// A preview talent the event made at run time (Fan Number 1's <c>FanNumber1PreviewTalent</c>: a size change): its
    /// name, description, icon and training flag go with it (<see cref="EventObject.PreviewName"/> and the rest), and a
    /// friend's game makes its own the way the event does.
    /// </summary>
    Preview = 4,
}

/// <summary>Whose an event choice's power-up or relic is. Values are part of the protocol: only add.</summary>
public enum EventObjectOwner : byte
{
    /// <summary>Nobody's: a fresh copy the event's roll made (a friend makes its own copy from its game's lists).</summary>
    None = 0,

    /// <summary>One of the run's team's relics (a friend uses its copy of the run's).</summary>
    Team = 1,

    /// <summary>A talent or ability one of the run's skaters has (a friend uses its copy of the run's).</summary>
    Skater = 2,

    /// <summary>
    /// A talent or ability of the choice's own free agent (<see cref="EventChoiceData.FreeAgent"/>; which one:
    /// <see cref="EventObject.OwnerIndex"/>): a friend uses the one on the agent it built.
    /// </summary>
    FreeAgent = 3,
}

/// <summary>
/// The choices one of the host's event menus shows (the game's <c>EventNode.eventChoices</c>, after the event's own roll),
/// as plain data: which event it is, the node's description parameters and the fields its screen reads, and every field of
/// every choice (the game's <c>EventChoice</c> struct), with skaters by run skater id and power-ups and relics by asset id
/// and level. A friend's game builds the game's own choices from it against its copy of the host's run and its own lists,
/// so the event's own roll never runs there (every event rolls with unseeded randomness, so it couldn't give the host's
/// choices anyway). Sent as a <see cref="MenuAttachmentKind.EventChoices"/> attachment (format
/// <see cref="MenuAttachment.EventChoicesFormat"/>); checked field by field when it's written and when it's read, so a set a
/// friend holds is one the glue can build from. Immutable once made.
/// <para>
/// Format 2 carries what the General Manager, Fan Number 1 and the Nurse need: a skater the event made that isn't in the
/// run (a free agent, <see cref="FreeAgentData"/>: the run file's own model of it) with its leaving conditions, a preview
/// talent made at run time (<see cref="EventObjectKind.Preview"/>), an object owned by the choice's free agent, a takeover
/// (only in a Nurse's set, <see cref="TakeoverClass"/>), and texts up to <see cref="MaxTextBytes"/>.
/// </para>
/// <para>
/// Format 3 carries what a friend in another language and another game state needs to
/// show exactly the host's screen: after the translated button name and after each custom text, the
/// <see cref="TextRecipe"/> that made it on the host's game (when one did), so a friend's game makes it in its own language;
/// after each image and a preview's icon, its <see cref="ImageSource"/>; and each free agent with its look
/// (<see cref="FreeAgentData.Version2"/>). Every recipe and image source names only objects its choice has.
/// </para>
/// <para>
/// Delegates aren't data: a choice says only whether the host's had them (a friend's choices never run).
/// </para>
/// </summary>
public sealed class EventChoiceSet
{
    /// <summary>Most choices in one set (the game shows at most a handful).</summary>
    public const int MaxChoices = 8;

    /// <summary>Most term parameters in one list.</summary>
    public const int MaxTerms = 16;

    /// <summary>Most custom images or texts on one choice.</summary>
    public const int MaxCustom = 8;

    /// <summary>Most stat bonuses on one choice.</summary>
    public const int MaxStatBonuses = 16;

    /// <summary>Most leaving conditions on one choice's free agent.</summary>
    public const int MaxConditions = 8;

    /// <summary>
    /// Longest text (a button name, a term's value, a custom text, a preview's name or description), in UTF-8 bytes: the
    /// Nurse's card texts and Fan Number 1's size descriptions carry rich-text tags.
    /// </summary>
    public const int MaxTextBytes = 2048;

    /// <summary>Largest set, written: an attachment body's cap (a set at every field's limit at once is bigger).</summary>
    public const int MaxBytes = MenuAttachment.MaxBodyBytes;

    /// <summary>The one event class whose choices may open a takeover screen (the Nurse's procedure screen).</summary>
    public const string TakeoverClass = "NurseEvent";

    /// <summary>Longest name (an id, a class or asset name, a sprite's name, a term's name), in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 128;

    /// <summary>Highest set version (the open is 0, each page or reroll one more).</summary>
    public const int MaxVersion = ushort.MaxValue;

    /// <summary>Highest reroll count or count of choices made.</summary>
    public const int MaxCount = 99;

    /// <summary>Largest map coordinate.</summary>
    public const int MaxCoordinate = 1000;

    /// <summary>The set's version for its menu: 0 for the open, one more for each page or reroll after it.</summary>
    public int Version { get; init; }

    /// <summary>What made it.</summary>
    public EventSetStep Step { get; init; } = EventSetStep.Open;

    /// <summary>The event's class, without its namespace ("CoachEvent").</summary>
    public string EventClass { get; init; } = string.Empty;

    /// <summary>The event asset's name.</summary>
    public string EventName { get; init; } = string.Empty;

    /// <summary>The event asset's id (the game's <c>EventNode.id</c>), or null.</summary>
    public string? EventId { get; init; }

    /// <summary>The menu's node type (the game's <c>NodeType</c> number, -1 for none).</summary>
    public int NodeType { get; init; }

    /// <summary>The map node the event is on (x, y), or null (opened without one).</summary>
    public (int X, int Y)? Point { get; init; }

    /// <summary>The rerolls left in the menu.</summary>
    public int RerollCount { get; init; }

    /// <summary>The choices made in the menu so far.</summary>
    public int ChoicesMade { get; init; }

    /// <summary>A training event's own fields (the game's <c>TrainingEvent</c>), or null for any other event.</summary>
    public TrainingFields? Training { get; init; }

    /// <summary>The node's description parameters (the game's <c>EventNode.descTermParams</c>), or null when it has none.</summary>
    public IReadOnlyList<EventTerm>? DescTerms { get; init; }

    /// <summary>The choices, in the order the menu shows them (at least one).</summary>
    public IReadOnlyList<EventChoiceData> Choices { get; init; } = Array.Empty<EventChoiceData>();

    /// <summary>For logs: "CoachEvent "coachEvent" v0 (open), 3 choice(s)"; the class and the name by <see cref="Diagnostics.HostText.KeyForLog(string?)"/>.</summary>
    public override string ToString() =>
        $"{Diagnostics.HostText.KeyForLog(EventClass)} \"{Diagnostics.HostText.KeyForLog(EventName)}\" v{Version} ({Step.ToString().ToLowerInvariant()}), {Choices.Count} choice(s)";

    /// <summary>
    /// True when the menu's count of rerolls left is no longer the one this set was read with (<paramref name="rerollsNow"/>, as the game
    /// has it): a reroll ran since, so this set isn't its choices.
    /// </summary>
    public bool RerolledSince(int rerollsNow) => RerollCount != Math.Clamp(rerollsNow, 0, MaxCount);

    /// <summary>
    /// For logs, each choice in a few words (<see cref="EventChoiceData.Words"/>): <c>1: "Coach Potato"; 2: (a text: 9
    /// characters, #4be1) [free agent 0f2b7633-... #50, max games 3 (0 played)]</c>. Empty for none. As long as the set's
    /// capped count of choices.
    /// </summary>
    /// <param name="own">True for this PC's own game's set (the host's), false for one a host sent.</param>
    /// <param name="shape">A text's shape for the log (the per-launch one for player-written text); never throws.</param>
    public string ChoiceWords(bool own, Func<string?, string> shape) =>
        string.Join("; ", Choices.Select((choice, index) => $"{index + 1}: {choice.Words(own, shape)}"));

    /// <summary>For logs, the choice at <paramref name="index"/> as a pick: <c>choice 1 of 3 ("Coach Potato")</c>, its words as
    /// <see cref="ChoiceWords"/> writes them; a choice this set doesn't have says so.</summary>
    /// <param name="index">The choice, from 0.</param>
    /// <param name="own">True for this PC's own game's set (the host's), false for one a host sent.</param>
    /// <param name="shape">A text's shape for the log; never throws.</param>
    public string PickWords(int index, bool own, Func<string?, string> shape) => index >= 0 && index < Choices.Count
        ? $"choice {index + 1} of {Choices.Count} ({Choices[index].Words(own, shape)})"
        : $"a choice this set doesn't have ({index + 1} of {Choices.Count})";

    /// <summary>True when the set is within every limit and reads back (so it can be sent).</summary>
    public bool IsWithinLimits() => Problem() == null;

    /// <summary>
    /// This set without its text recipes (every text as the host's): what goes when the recipes would take a set over the
    /// attachment's cap. Every other field the same.
    /// </summary>
    public EventChoiceSet WithoutRecipes() => new()
    {
        Version = Version,
        Step = Step,
        EventClass = EventClass,
        EventName = EventName,
        EventId = EventId,
        NodeType = NodeType,
        Point = Point,
        RerollCount = RerollCount,
        ChoicesMade = ChoicesMade,
        Training = Training,
        DescTerms = DescTerms,
        Choices = Choices.Select(c => c?.WithRecipes(null, null)!).ToList(),
    };

    /// <summary>Why the set can't be sent ("choice 2: a term's value is over 2048 bytes"), or null when it can.</summary>
    public string? Problem()
    {
        var writer = new BitWriter(1024);
        try
        {
            Write(writer);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        if (writer.ByteLength > MaxBytes)
        {
            return $"the set is {writer.ByteLength} bytes (at most {MaxBytes})";
        }

        var reader = new BitReader(writer.AsSpan());
        return TryRead(ref reader, out _) && reader.RemainingBits < 8 ? null : "it doesn't read back as written";
    }

    /// <summary>Writes the set. Throws <see cref="ArgumentException"/> (saying what) for anything over the limits or out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteUInt16((ushort)Version);
        writer.WriteByte((byte)Step);
        EventWire.Name(writer, EventClass);
        EventWire.Name(writer, EventName);
        EventWire.OptionalName(writer, EventId);
        writer.WriteVarInt(NodeType);
        writer.WriteBool(Point.HasValue);
        if (Point is (int x, int y))
        {
            writer.WriteVarInt(x);
            writer.WriteVarInt(y);
        }

        writer.WriteVarUInt((ulong)RerollCount);
        writer.WriteVarUInt((ulong)ChoicesMade);
        writer.WriteBool(Training != null);
        Training?.Write(writer);
        EventWire.Terms(writer, DescTerms);
        writer.WriteVarUInt((ulong)Choices.Count);
        foreach (EventChoiceData choice in Choices)
        {
            choice.Write(writer);
        }
    }

    /// <summary>Reads a set. False for anything malformed, over the limits or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out EventChoiceSet? set)
    {
        set = null;
        int version = reader.ReadUInt16();
        var step = (EventSetStep)reader.ReadByte();
        string? eventClass = EventWire.ReadName(ref reader);
        string? eventName = EventWire.ReadName(ref reader);
        if (!EventWire.TryReadOptionalName(ref reader, out string? eventId))
        {
            return false;
        }

        bool ok = true;
        int nodeType = EventWire.Whole(ref reader, ref ok);
        (int X, int Y)? point = null;
        if (reader.ReadBool())
        {
            int x = EventWire.Whole(ref reader, ref ok), y = EventWire.Whole(ref reader, ref ok);
            point = (x, y);
        }

        int rerolls = EventWire.Count(ref reader, MaxCount, ref ok);
        int made = EventWire.Count(ref reader, MaxCount, ref ok);
        TrainingFields? training = null;
        if (reader.ReadBool() && !TrainingFields.TryRead(ref reader, out training))
        {
            return false;
        }

        if (!ok || reader.IsOverrun || eventClass == null || eventName == null || !EventWire.TryReadTerms(ref reader, out List<EventTerm>? descTerms))
        {
            return false;
        }

        int count = EventWire.Count(ref reader, MaxChoices, ref ok);
        if (!ok || reader.IsOverrun)
        {
            return false;
        }

        var choices = new List<EventChoiceData>(count);
        for (int i = 0; i < count; i++)
        {
            if (!EventChoiceData.TryRead(ref reader, out EventChoiceData? choice) || reader.IsOverrun)
            {
                return false;
            }

            choices.Add(choice!);
        }

        var read = new EventChoiceSet
        {
            Version = version,
            Step = step,
            EventClass = eventClass,
            EventName = eventName,
            EventId = eventId,
            NodeType = nodeType,
            Point = point,
            RerollCount = rerolls,
            ChoicesMade = made,
            Training = training,
            DescTerms = descTerms,
            Choices = choices,
        };
        if (reader.IsOverrun || read.Check() != null)
        {
            return false;
        }

        set = read;
        return true;
    }

    /// <summary>Why the set is out of range (the writer's and the reader's one check), or null when it's fine.</summary>
    private string? Check()
    {
        if (Version is < 0 or > MaxVersion)
        {
            return $"version {Version} is out of range";
        }

        if (Step is not (EventSetStep.Open or EventSetStep.Page or EventSetStep.Reroll))
        {
            return $"step {Step} is no step";
        }

        if (!EventWire.IsClassName(EventClass))
        {
            return "the event's class isn't a class name";
        }

        string? problem = EventWire.CheckName(EventName, "the event's name", required: true) ?? EventWire.CheckName(EventId, "the event's id", required: false);
        if (problem != null)
        {
            return problem;
        }

        if (!EventWire.IsNodeType(NodeType))
        {
            return $"node type {NodeType} is out of range";
        }

        if (Point is (int x, int y) && (x is < -1 or > MaxCoordinate || y is < -1 or > MaxCoordinate))
        {
            return $"map point {x},{y} is out of range";
        }

        if (RerollCount is < 0 or > MaxCount || ChoicesMade is < 0 or > MaxCount)
        {
            return "a count is out of range";
        }

        problem = Training?.Check() ?? EventWire.CheckTerms(DescTerms, "the description's parameters");
        if (problem != null)
        {
            return problem;
        }

        if (Choices == null || Choices.Count is 0 or > MaxChoices)
        {
            return $"{Choices?.Count ?? 0} choices (1 to {MaxChoices})";
        }

        for (int i = 0; i < Choices.Count; i++)
        {
            problem = Choices[i] == null ? "it's missing"
                : Choices[i].HasOnTakeover && EventClass != TakeoverClass ? $"a takeover screen (only a {TakeoverClass}'s choices have one)"
                : Choices[i].Check();
            if (problem != null)
            {
                return $"choice {i + 1}: {problem}";
            }
        }

        return null;
    }
}

/// <summary>A training event's fields its screen reads (the game sets them when the node launches the event).</summary>
public sealed class TrainingFields
{
    /// <summary>The stat it improves (the game's <c>BasicStats</c> number).</summary>
    public int Stat { get; init; }

    /// <summary>Its rarity (the game's <c>Rarity</c> number).</summary>
    public int Rarity { get; init; }

    /// <summary>Its reward type (the game's <c>TrainingRewardType</c> number).</summary>
    public int RewardType { get; init; }

    /// <summary>The run skater it trains (a skater training), or null.</summary>
    public string? ForwardId { get; init; }

    internal void Write(BitWriter writer)
    {
        writer.WriteVarInt(Stat);
        writer.WriteVarInt(Rarity);
        writer.WriteVarInt(RewardType);
        EventWire.OptionalName(writer, ForwardId);
    }

    internal static bool TryRead(ref BitReader reader, out TrainingFields? fields)
    {
        fields = null;
        bool ok = true;
        int stat = EventWire.Whole(ref reader, ref ok), rarity = EventWire.Whole(ref reader, ref ok), reward = EventWire.Whole(ref reader, ref ok);
        if (!ok || !EventWire.TryReadOptionalName(ref reader, out string? forward))
        {
            return false;
        }

        fields = new TrainingFields { Stat = stat, Rarity = rarity, RewardType = reward, ForwardId = forward };
        return true;
    }

    internal string? Check() =>
        Stat is < 0 or > 63 || Rarity is < 0 or > 1023 || RewardType is < 0 or > 63 ? "the training's fields are out of range"
        : EventWire.CheckName(ForwardId, "the training's skater", required: false);
}

/// <summary>A term parameter (the game's <c>EventTermParam</c>): a name and its value, either of which may be null.</summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Value">Its value (already localized on the host's PC, or a number).</param>
public sealed record EventTerm(string? Name, string? Value);

/// <summary>A stat bonus (the game's <c>StatBonus</c>): every field.</summary>
/// <param name="SkaterType">The game's <c>ESkateType</c> number.</param>
/// <param name="StatForward">The game's <c>EForwardStat</c> number.</param>
/// <param name="StatGoalie">The game's <c>EGoalieStat</c> number.</param>
/// <param name="Bonus">The bonus.</param>
/// <param name="IsConditional">Whether it's conditional.</param>
/// <param name="IsBlockableStatGain">Whether the gain can be blocked.</param>
public sealed record StatBonusData(int SkaterType, int StatForward, int StatGoalie, int Bonus, bool IsConditional, bool IsBlockableStatGain);

/// <summary>A choice's stat bonuses (the game's <c>CustomStatBonusesData</c>).</summary>
/// <param name="AreAlreadyCounted">Whether the skater's stats already count them.</param>
/// <param name="Bonuses">The bonuses, in order.</param>
public sealed record StatBonusSet(bool AreAlreadyCounted, IReadOnlyList<StatBonusData> Bonuses);

/// <summary>A choice's injury (the game's <c>Injury</c>): every field.</summary>
/// <param name="Type">The game's <c>InjuryType</c> number.</param>
/// <param name="HealingTime">Matches left to heal.</param>
/// <param name="OriginalTime">Matches it took to heal at first.</param>
/// <param name="LastDisplayTime">The time last shown.</param>
public sealed record InjuryData(int Type, int HealingTime, int OriginalTime, int LastDisplayTime);

/// <summary>An event choice's power-up or relic: which asset at which level, and whose (a fresh copy or one the run has).</summary>
public sealed class EventObject
{
    /// <summary>What it is.</summary>
    public EventObjectKind Kind { get; init; }

    /// <summary>Its asset id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Its class, without the namespace (checked against what a friend's game makes from the id).</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>Its level.</summary>
    public int Level { get; init; }

    /// <summary>Whose it is.</summary>
    public EventObjectOwner Owner { get; init; }

    /// <summary>The run skater who has it (<see cref="EventObjectOwner.Skater"/>), else null.</summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Which of the choice's free agent's power-ups it is (<see cref="EventObjectOwner.FreeAgent"/>): its talent number (from 0,
    /// in <see cref="FreeAgentData.Talents"/>), or -1 for its ability. 0 for any other owner.
    /// </summary>
    public int OwnerIndex { get; init; }

    /// <summary>A relic's own number (the game's <c>Relic.intValue</c>), else 0.</summary>
    public int IntValue { get; init; }

    /// <summary>True for a chaos relic: its games below are shown on its card.</summary>
    public bool Chaos { get; init; }

    /// <summary>A chaos relic's games in all.</summary>
    public int TotalGames { get; init; }

    /// <summary>A chaos relic's games left.</summary>
    public int RemainingGames { get; init; }

    /// <summary>A preview's name (its <c>m_LocalizedName</c>, in the host's language), or null; only on a <see cref="EventObjectKind.Preview"/>.</summary>
    public string? PreviewName { get; init; }

    /// <summary>A preview's description (its <c>m_LocalizedDescription</c>, in the host's language), or null; only on a preview.</summary>
    public string? PreviewDescription { get; init; }

    /// <summary>A preview's icon by sprite name, or null for none; only on a preview.</summary>
    public string? PreviewIcon { get; init; }

    /// <summary>A preview's training flag (its <c>IsTraining</c>: the card's colour); only on a preview.</summary>
    public bool PreviewTraining { get; init; }

    /// <summary>Where a preview's icon came from (the event's own size icon, or a sprite by name), or null; only with a <see cref="PreviewIcon"/>.</summary>
    public ImageSource? PreviewIconSource { get; init; }

    /// <summary>For logs: "relic indecisive (Indecisive) level 1, the team's"; ids and the class by <see cref="Diagnostics.HostText.KeyForLog(string?)"/>.</summary>
    public override string ToString() =>
        $"{Kind.ToString().ToLowerInvariant()} {Diagnostics.HostText.KeyForLog(Id)} ({Diagnostics.HostText.KeyForLog(ClassName)}) level {Level}" +
        Owner switch
        {
            EventObjectOwner.Team => ", the team's",
            EventObjectOwner.Skater => $", {Diagnostics.HostText.KeyForLog(OwnerId)}'s",
            EventObjectOwner.FreeAgent => OwnerIndex < 0 ? ", the free agent's ability" : $", the free agent's talent {OwnerIndex + 1}",
            _ => string.Empty,
        };

    internal void Write(BitWriter writer)
    {
        writer.WriteByte((byte)Kind);
        EventWire.Name(writer, Id);
        EventWire.Name(writer, ClassName);
        writer.WriteVarInt(Level);
        writer.WriteByte((byte)Owner);
        if (Owner == EventObjectOwner.Skater)
        {
            EventWire.Name(writer, OwnerId!);
        }
        else if (Owner == EventObjectOwner.FreeAgent)
        {
            writer.WriteVarInt(OwnerIndex);
        }

        writer.WriteVarInt(IntValue);
        writer.WriteBool(Chaos);
        if (Chaos)
        {
            writer.WriteVarInt(TotalGames);
            writer.WriteVarInt(RemainingGames);
        }

        if (Kind == EventObjectKind.Preview)
        {
            EventWire.OptionalText(writer, PreviewName);
            EventWire.OptionalText(writer, PreviewDescription);
            EventWire.OptionalName(writer, PreviewIcon);
            if (PreviewIcon != null)
            {
                EventWire.OptionalSource(writer, PreviewIconSource);
            }

            writer.WriteBool(PreviewTraining);
        }
    }

    internal static bool TryRead(ref BitReader reader, out EventObject? value)
    {
        value = null;
        var kind = (EventObjectKind)reader.ReadByte();
        string? id = EventWire.ReadName(ref reader);
        string? className = EventWire.ReadName(ref reader);
        bool ok = true;
        int level = EventWire.Whole(ref reader, ref ok);
        var owner = (EventObjectOwner)reader.ReadByte();
        string? ownerId = owner == EventObjectOwner.Skater ? EventWire.ReadName(ref reader) : null;
        int ownerIndex = owner == EventObjectOwner.FreeAgent ? EventWire.Whole(ref reader, ref ok) : 0;
        int intValue = EventWire.Whole(ref reader, ref ok);
        bool chaos = reader.ReadBool();
        int total = chaos ? EventWire.Whole(ref reader, ref ok) : 0;
        int remaining = chaos ? EventWire.Whole(ref reader, ref ok) : 0;
        if (!ok || reader.IsOverrun || id == null || className == null || (owner == EventObjectOwner.Skater && ownerId == null))
        {
            return false;
        }

        string? previewName = null, previewDescription = null, previewIcon = null;
        ImageSource? iconSource = null;
        bool training = false;
        if (kind == EventObjectKind.Preview)
        {
            if (!EventWire.TryReadOptionalText(ref reader, out previewName) || !EventWire.TryReadOptionalText(ref reader, out previewDescription) ||
                !EventWire.TryReadOptionalName(ref reader, out previewIcon) || (previewIcon != null && !EventWire.TryReadOptionalSource(ref reader, out iconSource)))
            {
                return false;
            }

            training = reader.ReadBool();
        }

        value = new EventObject
        {
            Kind = kind,
            Id = id,
            ClassName = className,
            Level = level,
            Owner = owner,
            OwnerId = ownerId,
            OwnerIndex = ownerIndex,
            IntValue = intValue,
            Chaos = chaos,
            TotalGames = total,
            RemainingGames = remaining,
            PreviewName = previewName,
            PreviewDescription = previewDescription,
            PreviewIcon = previewIcon,
            PreviewTraining = training,
            PreviewIconSource = iconSource,
        };
        return !reader.IsOverrun && value.Check() == null;
    }

    internal string? Check()
    {
        if (Kind is not (EventObjectKind.Relic or EventObjectKind.Talent or EventObjectKind.Ability or EventObjectKind.Preview))
        {
            return $"an object of kind {Kind}";
        }

        string? problem = EventWire.CheckName(Id, "an object's id", required: true);
        if (problem != null)
        {
            return problem;
        }

        if (!EventWire.IsClassName(ClassName))
        {
            return "an object's class isn't a class name";
        }

        if (Level is < 0 or > 99)
        {
            return $"an object's level {Level} is out of range";
        }

        switch (Owner)
        {
            case EventObjectOwner.None or EventObjectOwner.Team when OwnerId == null && OwnerIndex == 0:
                break;
            case EventObjectOwner.Skater when OwnerIndex == 0:
                problem = EventWire.CheckName(OwnerId, "an object's skater", required: true);
                if (problem != null)
                {
                    return problem;
                }

                break;
            case EventObjectOwner.FreeAgent when OwnerId == null:
                if (!(Kind == EventObjectKind.Talent && OwnerIndex is >= 0 and < FreeAgentData.MaxTalents) && !(Kind == EventObjectKind.Ability && OwnerIndex == -1))
                {
                    return "a free agent's object that isn't one of its talents or its ability";
                }

                break;
            default:
                return $"an object owned by {Owner}{(OwnerId != null ? " with a skater" : string.Empty)}{(OwnerIndex != 0 ? $" at {OwnerIndex}" : string.Empty)}";
        }

        if (Owner == EventObjectOwner.Team && Kind != EventObjectKind.Relic)
        {
            return "a team's object that isn't a relic";
        }

        if (Owner == EventObjectOwner.Skater && Kind == EventObjectKind.Relic)
        {
            return "a skater's relic";
        }

        if (Kind == EventObjectKind.Preview)
        {
            if (Owner != EventObjectOwner.None || Chaos || IntValue != 0)
            {
                return "a preview that's someone's, a chaos relic's or numbered";
            }

            problem = EventWire.CheckText(PreviewName, "a preview's name") ?? EventWire.CheckText(PreviewDescription, "a preview's description") ??
                EventWire.CheckName(PreviewIcon, "a preview's icon", required: false) ??
                (PreviewIconSource == null ? null
                : PreviewIcon == null ? "a preview's icon source without its icon"
                : PreviewIconSource.Kind == ImageSourceKind.ObjectIcon ? "a preview's icon that's an object's icon"
                : PreviewIconSource.Check());
            if (problem != null)
            {
                return problem;
            }
        }
        else if (PreviewName != null || PreviewDescription != null || PreviewIcon != null || PreviewTraining || PreviewIconSource != null)
        {
            return "a preview's fields on an object that isn't a preview";
        }

        if (Chaos && (Kind != EventObjectKind.Relic || TotalGames is < 0 or > 999 || RemainingGames is < 0 or > 999))
        {
            return "a chaos relic's games are out of range";
        }

        return !Chaos && (TotalGames != 0 || RemainingGames != 0) ? "games on an object that isn't a chaos relic" : null;
    }
}

/// <summary>One event choice (the game's <c>EventChoice</c> struct): every field, its references as ids.</summary>
public sealed class EventChoiceData
{
    /// <summary>The button's name (a term), or null.</summary>
    public string? ButtonName { get; init; }

    /// <summary>The button's translated name (the host's language), or null.</summary>
    public string? TranslatedButtonName { get; init; }

    /// <summary>The host's choice had an action for its pick (a friend's gets one that does nothing).</summary>
    public bool HasOnSelection { get; init; }

    /// <summary>
    /// The host's choice had a takeover screen (the Nurse's procedure screen; a friend's gets one that does nothing). Only in a
    /// set of <see cref="EventChoiceSet.TakeoverClass"/>.
    /// </summary>
    public bool HasOnTakeover { get; init; }

    /// <summary>Whether it can be picked.</summary>
    public bool Interactable { get; init; }

    /// <summary>Its node type (the game's <c>NodeType</c> number).</summary>
    public int NodeType { get; init; }

    /// <summary>Its term parameters, or null when it has none.</summary>
    public IReadOnlyList<EventTerm>? Terms { get; init; }

    /// <summary>Its type (the game's <c>EEventChoiceType</c> number).</summary>
    public int ChoiceType { get; init; }

    /// <summary>The run skater it's about, or null (none, or a free agent: <see cref="FreeAgent"/>).</summary>
    public string? SkaterId { get; init; }

    /// <summary>The skater it's about when the event made one that isn't in the run (a General Manager's free agent), else null.</summary>
    public FreeAgentData? FreeAgent { get; init; }

    /// <summary>Its free agent's leaving conditions (the game's <c>FreeAgentLeavingConditions</c>, in order), or null when it has none.</summary>
    public IReadOnlyList<FreeAgentCondition>? Conditions { get; init; }

    /// <summary>Its power-up or relic (the game's <c>PowerUpOrRelic</c>), or null.</summary>
    public EventObject? Object1 { get; init; }

    /// <summary>Its second power-up or relic (the game's <c>PowerUpOrRelic2</c>: an upgrade's current one), or null.</summary>
    public EventObject? Object2 { get; init; }

    /// <summary>Its custom images by sprite name (a null entry is no image), or null when it has none.</summary>
    public IReadOnlyList<string?>? Images { get; init; }

    /// <summary>Its custom texts (the host's language; a null entry is no text), or null when it has none.</summary>
    public IReadOnlyList<string?>? Texts { get; init; }

    /// <summary>Its stat bonuses, or null.</summary>
    public StatBonusSet? StatBonuses { get; init; }

    /// <summary>Its injury, or null.</summary>
    public InjuryData? Injury { get; init; }

    /// <summary>Whether its reward isn't tracked.</summary>
    public bool SuppressRewardTracking { get; init; }

    /// <summary>How the host's game made <see cref="TranslatedButtonName"/> (a friend makes it in its own language), or null.</summary>
    public TextRecipe? ButtonRecipe { get; init; }

    /// <summary>How the host's game made each of <see cref="Texts"/> (null entries: none), or null for none; as many as the texts.</summary>
    public IReadOnlyList<TextRecipe?>? TextRecipes { get; init; }

    /// <summary>Where each of <see cref="Images"/> came from (null entries: not said), or null for none; as many as the images.</summary>
    public IReadOnlyList<ImageSource?>? ImageSources { get; init; }

    /// <summary>
    /// For logs: its button's words, then what the choice carries:
    /// <c>"Coach Potato" [free agent 0f2b7633-... #50, max games 3 (0 played)] [preview "Grow"] [takeover]</c>. The button's
    /// translated words are written in clear only on this PC's own set (<paramref name="own"/>) and when made of the game's
    /// words only (<see cref="TextRecipe.IsGameWords"/>: a button can carry a skater's name, and a custom skater's is typed);
    /// a host's by the recipe it sent (<c>(a recipe) procedure 3</c>), else as their shape (<c>(a text: 9 characters,
    /// #4be1)</c>); a button with only a term by the term's key. A free agent goes by its id (a host chooses its name), a
    /// preview's name in clear only on this PC's own set.
    /// </summary>
    /// <param name="own">True for this PC's own game's choice (the host's), false for one a host sent.</param>
    /// <param name="shape">A text's shape for the log (the per-launch one for player-written text); never throws.</param>
    public string Words(bool own, Func<string?, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        string button = string.IsNullOrEmpty(TranslatedButtonName)
            ? (string.IsNullOrEmpty(ButtonName) ? "\"\"" : $"\"{Diagnostics.HostText.KeyForLog(ButtonName)}\"")
            : own && ButtonRecipe != null && ButtonRecipe.IsGameWords() ? $"\"{Diagnostics.SafeText.Clean(TranslatedButtonName, 48)}\""
            : !own && ButtonRecipe != null ? $"(a recipe) {ButtonRecipe}"
            : $"(a text: {shape(TranslatedButtonName)})";
        var words = new StringBuilder(button);
        if (FreeAgent is { } agent)
        {
            words.Append(CultureInfo.InvariantCulture, $" [free agent {Diagnostics.HostText.KeyForLog(agent.Id)} #{agent.Number}");
            foreach (FreeAgentCondition condition in (Conditions ?? Array.Empty<FreeAgentCondition>()).Take(2))
            {
                words.Append(", ").Append(condition);
            }

            words.Append(']');
        }

        foreach (EventObject? preview in new[] { Object1, Object2 })
        {
            if (preview?.PreviewName is { } named)
            {
                words.Append(own ? $" [preview \"{Diagnostics.SafeText.Clean(named, 48)}\"]" : $" [preview (a text: {shape(named)})]");
            }
        }

        if (HasOnTakeover)
        {
            words.Append(" [takeover]");
        }

        return words.ToString();
    }

    /// <summary>
    /// This choice with <paramref name="translatedButtonName"/> and <paramref name="texts"/> in place of its own (the texts a
    /// friend's game made in its own language), every other field the same.
    /// </summary>
    public EventChoiceData WithTexts(string? translatedButtonName, IReadOnlyList<string?>? texts) =>
        Copy(translatedButtonName, texts, ButtonRecipe, TextRecipes, ImageSources);

    /// <summary>This choice with <paramref name="buttonRecipe"/> and <paramref name="textRecipes"/> in place of its own, every other field the same.</summary>
    public EventChoiceData WithRecipes(TextRecipe? buttonRecipe, IReadOnlyList<TextRecipe?>? textRecipes) =>
        Copy(TranslatedButtonName, Texts, buttonRecipe, textRecipes, ImageSources);

    /// <summary>This choice with <paramref name="imageSources"/> in place of its own, every other field the same.</summary>
    public EventChoiceData WithImageSources(IReadOnlyList<ImageSource?>? imageSources) =>
        Copy(TranslatedButtonName, Texts, ButtonRecipe, TextRecipes, imageSources);

    private EventChoiceData Copy(string? translatedButtonName, IReadOnlyList<string?>? texts, TextRecipe? buttonRecipe, IReadOnlyList<TextRecipe?>? textRecipes,
        IReadOnlyList<ImageSource?>? imageSources) => new()
    {
        ButtonName = ButtonName,
        TranslatedButtonName = translatedButtonName,
        HasOnSelection = HasOnSelection,
        HasOnTakeover = HasOnTakeover,
        Interactable = Interactable,
        NodeType = NodeType,
        Terms = Terms,
        ChoiceType = ChoiceType,
        SkaterId = SkaterId,
        FreeAgent = FreeAgent,
        Conditions = Conditions,
        Object1 = Object1,
        Object2 = Object2,
        Images = Images,
        Texts = texts,
        StatBonuses = StatBonuses,
        Injury = Injury,
        SuppressRewardTracking = SuppressRewardTracking,
        ButtonRecipe = buttonRecipe,
        TextRecipes = textRecipes,
        ImageSources = imageSources,
    };

    internal void Write(BitWriter writer)
    {
        EventWire.OptionalText(writer, ButtonName);
        EventWire.OptionalText(writer, TranslatedButtonName);
        if (TranslatedButtonName != null)
        {
            EventWire.OptionalRecipe(writer, ButtonRecipe);
        }

        writer.WriteBool(HasOnSelection);
        writer.WriteBool(HasOnTakeover);
        writer.WriteBool(Interactable);
        writer.WriteVarInt(NodeType);
        EventWire.Terms(writer, Terms);
        writer.WriteVarInt(ChoiceType);
        EventWire.OptionalName(writer, SkaterId);
        writer.WriteBool(Object1 != null);
        Object1?.Write(writer);
        writer.WriteBool(Object2 != null);
        Object2?.Write(writer);
        writer.WriteBool(Images != null);
        if (Images != null)
        {
            writer.WriteVarUInt((ulong)Images.Count);
            for (int i = 0; i < Images.Count; i++)
            {
                EventWire.OptionalName(writer, Images[i]);
                if (Images[i] != null)
                {
                    EventWire.OptionalSource(writer, ImageSources?[i]);
                }
            }
        }

        writer.WriteBool(Texts != null);
        if (Texts != null)
        {
            writer.WriteVarUInt((ulong)Texts.Count);
            for (int i = 0; i < Texts.Count; i++)
            {
                EventWire.OptionalText(writer, Texts[i]);
                if (Texts[i] != null)
                {
                    EventWire.OptionalRecipe(writer, TextRecipes?[i]);
                }
            }
        }

        writer.WriteBool(StatBonuses != null);
        if (StatBonuses != null)
        {
            writer.WriteBool(StatBonuses.AreAlreadyCounted);
            writer.WriteVarUInt((ulong)StatBonuses.Bonuses.Count);
            foreach (StatBonusData bonus in StatBonuses.Bonuses)
            {
                writer.WriteVarInt(bonus.SkaterType);
                writer.WriteVarInt(bonus.StatForward);
                writer.WriteVarInt(bonus.StatGoalie);
                writer.WriteVarInt(bonus.Bonus);
                writer.WriteBool(bonus.IsConditional);
                writer.WriteBool(bonus.IsBlockableStatGain);
            }
        }

        writer.WriteBool(Injury != null);
        if (Injury != null)
        {
            writer.WriteVarInt(Injury.Type);
            writer.WriteVarInt(Injury.HealingTime);
            writer.WriteVarInt(Injury.OriginalTime);
            writer.WriteVarInt(Injury.LastDisplayTime);
        }

        writer.WriteBool(SuppressRewardTracking);
        writer.WriteBool(FreeAgent != null);
        FreeAgent?.Write(writer, FreeAgentData.Version2);
        writer.WriteBool(Conditions != null);
        if (Conditions != null)
        {
            writer.WriteVarUInt((ulong)Conditions.Count);
            foreach (FreeAgentCondition condition in Conditions)
            {
                condition.Write(writer);
            }
        }
    }

    internal static bool TryRead(ref BitReader reader, out EventChoiceData? choice)
    {
        choice = null;
        TextRecipe? buttonRecipe = null;
        if (!EventWire.TryReadOptionalText(ref reader, out string? buttonName) || !EventWire.TryReadOptionalText(ref reader, out string? translated) ||
            (translated != null && !EventWire.TryReadOptionalRecipe(ref reader, out buttonRecipe)))
        {
            return false;
        }

        bool onSelection = reader.ReadBool(), onTakeover = reader.ReadBool(), interactable = reader.ReadBool();
        bool ok = true;
        int nodeType = EventWire.Whole(ref reader, ref ok);
        if (!ok || !EventWire.TryReadTerms(ref reader, out List<EventTerm>? terms))
        {
            return false;
        }

        int choiceType = EventWire.Whole(ref reader, ref ok);
        if (!ok || !EventWire.TryReadOptionalName(ref reader, out string? skaterId))
        {
            return false;
        }

        EventObject? object1 = null, object2 = null;
        if ((reader.ReadBool() && !EventObject.TryRead(ref reader, out object1)) || (reader.ReadBool() && !EventObject.TryRead(ref reader, out object2)))
        {
            return false;
        }

        List<string?>? images = null;
        List<ImageSource?>? sources = null;
        if (reader.ReadBool())
        {
            int count = EventWire.Count(ref reader, EventChoiceSet.MaxCustom, ref ok);
            images = new List<string?>(ok ? count : 0);
            var read = new ImageSource?[ok ? count : 0];
            for (int i = 0; ok && i < count; i++)
            {
                ok = EventWire.TryReadOptionalName(ref reader, out string? image) && (image == null || EventWire.TryReadOptionalSource(ref reader, out read[i]));
                images.Add(image);
            }

            sources = read.Any(s => s != null) ? read.ToList() : null;
        }

        List<string?>? texts = null;
        List<TextRecipe?>? recipes = null;
        if (ok && reader.ReadBool())
        {
            int count = EventWire.Count(ref reader, EventChoiceSet.MaxCustom, ref ok);
            texts = new List<string?>(ok ? count : 0);
            var read = new TextRecipe?[ok ? count : 0];
            for (int i = 0; ok && i < count; i++)
            {
                ok = EventWire.TryReadOptionalText(ref reader, out string? text) && (text == null || EventWire.TryReadOptionalRecipe(ref reader, out read[i]));
                texts.Add(text);
            }

            recipes = read.Any(r => r != null) ? read.ToList() : null;
        }

        StatBonusSet? bonuses = null;
        if (ok && reader.ReadBool())
        {
            bool counted = reader.ReadBool();
            int count = EventWire.Count(ref reader, EventChoiceSet.MaxStatBonuses, ref ok);
            var list = new List<StatBonusData>(ok ? count : 0);
            for (int i = 0; ok && i < count && !reader.IsOverrun; i++)
            {
                int type = EventWire.Whole(ref reader, ref ok), forward = EventWire.Whole(ref reader, ref ok), goalie = EventWire.Whole(ref reader, ref ok), bonus = EventWire.Whole(ref reader, ref ok);
                list.Add(new StatBonusData(type, forward, goalie, bonus, reader.ReadBool(), reader.ReadBool()));
            }

            bonuses = new StatBonusSet(counted, list);
        }

        InjuryData? injury = null;
        if (ok && reader.ReadBool())
        {
            injury = new InjuryData(EventWire.Whole(ref reader, ref ok), EventWire.Whole(ref reader, ref ok), EventWire.Whole(ref reader, ref ok), EventWire.Whole(ref reader, ref ok));
        }

        bool suppress = reader.ReadBool();
        FreeAgentData? agent = null;
        if (!ok || reader.IsOverrun || (reader.ReadBool() && !FreeAgentData.TryRead(ref reader, FreeAgentData.Version2, out agent)))
        {
            return false;
        }

        List<FreeAgentCondition>? conditions = null;
        if (reader.ReadBool())
        {
            int count = EventWire.Count(ref reader, EventChoiceSet.MaxConditions, ref ok);
            conditions = new List<FreeAgentCondition>(ok ? count : 0);
            for (int i = 0; ok && i < count; i++)
            {
                ok = FreeAgentCondition.TryRead(ref reader, out FreeAgentCondition? condition);
                conditions.Add(condition!);
            }
        }

        if (!ok || reader.IsOverrun)
        {
            return false;
        }

        choice = new EventChoiceData
        {
            ButtonName = buttonName,
            TranslatedButtonName = translated,
            HasOnSelection = onSelection,
            HasOnTakeover = onTakeover,
            Interactable = interactable,
            NodeType = nodeType,
            Terms = terms,
            ChoiceType = choiceType,
            SkaterId = skaterId,
            FreeAgent = agent,
            Conditions = conditions,
            Object1 = object1,
            Object2 = object2,
            Images = images,
            Texts = texts,
            StatBonuses = bonuses,
            Injury = injury,
            SuppressRewardTracking = suppress,
            ButtonRecipe = buttonRecipe,
            TextRecipes = recipes,
            ImageSources = sources,
        };
        return choice.Check() == null;
    }

    /// <summary>Why the choice is out of range, or null when it's fine (a takeover is checked by its set: only a Nurse's may have one).</summary>
    internal string? Check()
    {
        string? problem = EventWire.CheckText(ButtonName, "the button's name") ?? EventWire.CheckText(TranslatedButtonName, "the button's translated name");
        if (problem != null)
        {
            return problem;
        }

        if (!EventWire.IsNodeType(NodeType) || ChoiceType is < 0 or > 255)
        {
            return $"node type {NodeType} or choice type {ChoiceType} is out of range";
        }

        problem = EventWire.CheckTerms(Terms, "its parameters") ?? EventWire.CheckName(SkaterId, "its skater", required: false) ??
            (Object1 == null ? null : Object1.Check()) ?? (Object2 == null ? null : Object2.Check()) ?? (FreeAgent == null ? null : FreeAgent.Check());
        if (problem != null)
        {
            return problem;
        }

        if (SkaterId != null && FreeAgent != null)
        {
            return "both a run skater and a free agent";
        }

        problem = AgentObject(Object1) ?? AgentObject(Object2);
        if (problem != null)
        {
            return problem;
        }

        if (Conditions != null)
        {
            if (Conditions.Count > EventChoiceSet.MaxConditions)
            {
                return $"{Conditions.Count} leaving conditions";
            }

            foreach (FreeAgentCondition? condition in Conditions)
            {
                problem ??= condition == null ? "a leaving condition is missing" : condition.Check();
            }
        }

        if (Images != null)
        {
            if (Images.Count > EventChoiceSet.MaxCustom)
            {
                return $"{Images.Count} images";
            }

            foreach (string? image in Images)
            {
                problem ??= EventWire.CheckName(image, "an image's name", required: false);
            }
        }

        if (Texts != null)
        {
            if (Texts.Count > EventChoiceSet.MaxCustom)
            {
                return $"{Texts.Count} texts";
            }

            foreach (string? text in Texts)
            {
                problem ??= EventWire.CheckText(text, "a text");
            }
        }

        if (problem != null)
        {
            return problem;
        }

        if (StatBonuses != null)
        {
            if (StatBonuses.Bonuses == null || StatBonuses.Bonuses.Count > EventChoiceSet.MaxStatBonuses)
            {
                return $"{StatBonuses.Bonuses?.Count ?? 0} stat bonuses";
            }

            foreach (StatBonusData bonus in StatBonuses.Bonuses)
            {
                if (bonus == null || bonus.SkaterType is < 0 or > 15 || bonus.StatForward is < 0 or > 63 || bonus.StatGoalie is < 0 or > 63 || bonus.Bonus is < -999 or > 999)
                {
                    return "a stat bonus is out of range";
                }
            }
        }

        if (Injury != null && (Injury.Type is < 0 or > 63 || EventWire.OutOfRange(Injury.HealingTime) || EventWire.OutOfRange(Injury.OriginalTime) || EventWire.OutOfRange(Injury.LastDisplayTime)))
        {
            return "the injury is out of range";
        }

        return CheckRecipesAndSources();
    }

    /// <summary>Why a recipe or image source doesn't fit (no text or image for it, out of its limits, an object the choice hasn't), or null.</summary>
    private string? CheckRecipesAndSources()
    {
        if (ButtonRecipe != null && TranslatedButtonName == null)
        {
            return "a button recipe without a translated button name";
        }

        string? problem = Recipe(ButtonRecipe, "the button's recipe");
        if (problem != null)
        {
            return problem;
        }

        if (TextRecipes != null)
        {
            if (Texts == null || TextRecipes.Count != Texts.Count)
            {
                return $"{TextRecipes.Count} text recipes for {Texts?.Count ?? 0} texts";
            }

            for (int i = 0; i < TextRecipes.Count && problem == null; i++)
            {
                problem = TextRecipes[i] != null && Texts[i] == null ? $"a recipe for text {i + 1}, which is none" : Recipe(TextRecipes[i], $"text {i + 1}'s recipe");
            }
        }

        if (problem != null || ImageSources == null)
        {
            return problem;
        }

        if (Images == null || ImageSources.Count != Images.Count)
        {
            return $"{ImageSources.Count} image sources for {Images?.Count ?? 0} images";
        }

        for (int i = 0; i < ImageSources.Count && problem == null; i++)
        {
            ImageSource? source = ImageSources[i];
            problem = source == null ? null
                : Images[i] == null ? $"a source for image {i + 1}, which is none"
                : source.Check() ?? (source.Target.IsOn(this) ? null : $"image {i + 1}'s source is the icon of {source.Target}, which the choice hasn't");
        }

        return problem;
    }

    private string? Recipe(TextRecipe? recipe, string what)
    {
        if (recipe == null)
        {
            return null;
        }

        string? problem = recipe.Check();
        if (problem != null)
        {
            return $"{what}: {problem}";
        }

        foreach (ChoiceObjectRef target in recipe.Targets())
        {
            if (!target.IsOn(this))
            {
                return $"{what} names {target}, which the choice hasn't";
            }
        }

        return null;
    }

    /// <summary>Why an object owned by the choice's free agent isn't one of its power-ups (no agent, no such talent, another id), or null.</summary>
    private string? AgentObject(EventObject? value)
    {
        if (value is not { Owner: EventObjectOwner.FreeAgent })
        {
            return null;
        }

        if (FreeAgent == null)
        {
            return "a free agent's object without a free agent";
        }

        string? id = value.OwnerIndex < 0 ? FreeAgent.AbilityId : value.OwnerIndex < FreeAgent.Talents.Count ? FreeAgent.Talents[value.OwnerIndex].Id : null;
        return id != value.Id ? $"its {value} isn't the free agent's" : null;
    }
}

/// <summary>The event choice set's pieces on the wire.</summary>
internal static class EventWire
{
    // Strict UTF-8: a text that isn't is refused, never patched up.
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>True for a number that can be the game's <c>NodeType</c> (its values and room for more).</summary>
    public static bool IsNodeType(int value) => value is >= -1 and <= 63;

    /// <summary>True for a time out of the injury's range.</summary>
    public static bool OutOfRange(int value) => value is < -1 or > 9999;

    /// <summary>True for a class name: a C# identifier of at most 64 characters.</summary>
    public static bool IsClassName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || char.IsDigit(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Why <paramref name="name"/> can't be a name (missing when required, empty unless <paramref name="allowEmpty"/>, too
    /// long, control characters), or null. Empty is allowed only where the game's own data has empty strings (a skater's
    /// unset skin or flavour key).
    /// </summary>
    public static string? CheckName(string? name, string what, bool required, bool allowEmpty = false)
    {
        if (name == null)
        {
            return required ? $"{what} is missing" : null;
        }

        if (name.Length == 0)
        {
            return allowEmpty ? null : $"{what} is empty";
        }

        return CheckChars(name, what, EventChoiceSet.MaxNameBytes, newlines: false);
    }

    /// <summary>Why <paramref name="text"/> can't be a text (too long, control characters besides line breaks and tabs), or null.</summary>
    public static string? CheckText(string? text, string what) => text == null ? null : CheckChars(text, what, EventChoiceSet.MaxTextBytes, newlines: true);

    /// <summary>Why a term list can't go, or null.</summary>
    public static string? CheckTerms(IReadOnlyList<EventTerm>? terms, string what)
    {
        if (terms == null)
        {
            return null;
        }

        if (terms.Count > EventChoiceSet.MaxTerms)
        {
            return $"{what}: {terms.Count} terms";
        }

        foreach (EventTerm term in terms)
        {
            string? problem = term == null ? $"{what}: a term is missing" : CheckName(term.Name, $"{what}: a term's name", required: false) ?? CheckText(term.Value, $"{what}: a term's value");
            if (problem != null)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="text"/> can't be a string of at most <paramref name="maxBytes"/> (missing when required, control
    /// characters besides line breaks and tabs when <paramref name="newlines"/>, too long), or null.
    /// </summary>
    public static string? CheckBounded(string? text, string what, int maxBytes, bool newlines, bool required) =>
        text == null ? (required ? $"{what} is missing" : null) : CheckChars(text, what, maxBytes, newlines);

    /// <summary>A required string of at most <paramref name="maxBytes"/> (a text recipe's literal, term or colour).</summary>
    public static void Bounded(BitWriter writer, string text, int maxBytes) => Bytes(writer, text, maxBytes);

    /// <summary>A required string of at most <paramref name="maxBytes"/>; false when it isn't one.</summary>
    public static bool TryReadBounded(ref BitReader reader, int maxBytes, out string? text) => TryReadBytes(ref reader, maxBytes, out text);

    /// <summary>A flag, then the recipe when there's one.</summary>
    public static void OptionalRecipe(BitWriter writer, TextRecipe? recipe)
    {
        writer.WriteBool(recipe != null);
        recipe?.Write(writer);
    }

    /// <summary>A flag, then a recipe when it says so; false when that isn't one.</summary>
    public static bool TryReadOptionalRecipe(ref BitReader reader, out TextRecipe? recipe)
    {
        recipe = null;
        return !reader.ReadBool() ? !reader.IsOverrun : TextRecipe.TryRead(ref reader, out recipe);
    }

    /// <summary>A flag, then the image source when there's one.</summary>
    public static void OptionalSource(BitWriter writer, ImageSource? source)
    {
        writer.WriteBool(source != null);
        source?.Write(writer);
    }

    /// <summary>A flag, then an image source when it says so; false when that isn't one.</summary>
    public static bool TryReadOptionalSource(ref BitReader reader, out ImageSource? source)
    {
        source = null;
        return !reader.ReadBool() ? !reader.IsOverrun : ImageSource.TryRead(ref reader, out source);
    }

    public static void Name(BitWriter writer, string name) => Bytes(writer, name, EventChoiceSet.MaxNameBytes);

    public static void OptionalName(BitWriter writer, string? name)
    {
        writer.WriteBool(name != null);
        if (name != null)
        {
            Bytes(writer, name, EventChoiceSet.MaxNameBytes);
        }
    }

    public static void OptionalText(BitWriter writer, string? text)
    {
        writer.WriteBool(text != null);
        if (text != null)
        {
            Bytes(writer, text, EventChoiceSet.MaxTextBytes);
        }
    }

    public static void Terms(BitWriter writer, IReadOnlyList<EventTerm>? terms)
    {
        writer.WriteBool(terms != null);
        if (terms == null)
        {
            return;
        }

        writer.WriteVarUInt((ulong)terms.Count);
        foreach (EventTerm term in terms)
        {
            OptionalName(writer, term.Name);
            OptionalText(writer, term.Value);
        }
    }

    /// <summary>A required name; null (with the reader overrun) when it isn't one.</summary>
    public static string? ReadName(ref BitReader reader) => TryReadBytes(ref reader, EventChoiceSet.MaxNameBytes, out string? name) ? name : null;

    public static bool TryReadOptionalName(ref BitReader reader, out string? name)
    {
        name = null;
        return !reader.ReadBool() ? !reader.IsOverrun : TryReadBytes(ref reader, EventChoiceSet.MaxNameBytes, out name);
    }

    public static bool TryReadOptionalText(ref BitReader reader, out string? text)
    {
        text = null;
        return !reader.ReadBool() ? !reader.IsOverrun : TryReadBytes(ref reader, EventChoiceSet.MaxTextBytes, out text);
    }

    public static bool TryReadTerms(ref BitReader reader, out List<EventTerm>? terms)
    {
        terms = null;
        if (!reader.ReadBool())
        {
            return !reader.IsOverrun;
        }

        bool ok = true;
        int count = Count(ref reader, EventChoiceSet.MaxTerms, ref ok);
        if (!ok)
        {
            return false;
        }

        var list = new List<EventTerm>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadOptionalName(ref reader, out string? name) || !TryReadOptionalText(ref reader, out string? value))
            {
                return false;
            }

            list.Add(new EventTerm(name, value));
        }

        terms = list;
        return true;
    }

    /// <summary>A whole number that must fit an <see cref="int"/>; one that doesn't clears <paramref name="ok"/>.</summary>
    public static int Whole(ref BitReader reader, ref bool ok)
    {
        long value = reader.ReadVarInt();
        if (reader.IsOverrun || value is < int.MinValue or > int.MaxValue)
        {
            ok = false;
            return 0;
        }

        return (int)value;
    }

    /// <summary>A count up to <paramref name="max"/>; a larger one (or an overrun) clears <paramref name="ok"/>.</summary>
    public static int Count(ref BitReader reader, int max, ref bool ok)
    {
        ulong value = reader.ReadVarUInt();
        if (reader.IsOverrun || value > (ulong)max)
        {
            ok = false;
            return 0;
        }

        return (int)value;
    }

    private static string? CheckChars(string text, string what, int maxBytes, bool newlines)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c) && !(newlines && c is '\n' or '\r' or '\t'))
            {
                return $"{what} has a control character";
            }
        }

        int bytes;
        try
        {
            bytes = Strict.GetByteCount(text);
        }
        catch (ArgumentException)
        {
            return $"{what} isn't valid text";
        }

        return bytes > maxBytes ? $"{what} is over {maxBytes} bytes" : null;
    }

    private static void Bytes(BitWriter writer, string text, int maxBytes)
    {
        byte[] bytes = Strict.GetBytes(text);
        if (bytes.Length > maxBytes)
        {
            throw new ArgumentException($"A text is over {maxBytes} bytes.", nameof(text));
        }

        writer.WriteVarUInt((ulong)bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static bool TryReadBytes(ref BitReader reader, int maxBytes, out string? text)
    {
        text = null;
        ulong length = reader.ReadVarUInt();
        if (reader.IsOverrun || length > (ulong)maxBytes || (long)length * 8 > reader.RemainingBits)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[(int)length];
        reader.ReadBytes(bytes);
        if (reader.IsOverrun)
        {
            return false;
        }

        try
        {
            text = Strict.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }
}
