using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>Which of the game's teams a toast is about. Values are part of the protocol: only add.</summary>
public enum ToastTeam : byte
{
    /// <summary>No team, or one a friend doesn't hold (the toast shows without it).</summary>
    None = 0,

    /// <summary>The run's team.</summary>
    Run = 1,

    /// <summary>The match's home team.</summary>
    Home = 2,

    /// <summary>The match's visitor team.</summary>
    Visitor = 3,
}

/// <summary>What kind of the game's assets a toast's object is. Values are part of the protocol: only add.</summary>
public enum ToastObjectKind : byte
{
    /// <summary>No object.</summary>
    None = 0,

    /// <summary>A relic (the game's <c>Relic</c>).</summary>
    Relic = 1,

    /// <summary>A talent (the game's <c>Talent</c>).</summary>
    Talent = 2,

    /// <summary>An ability (the game's <c>Ability</c>).</summary>
    Ability = 3,
}

/// <summary>Where a toast's text comes from on a friend's PC. Values are part of the protocol: only add.</summary>
public enum ToastTextKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>The host's shown text, as it is.</summary>
    Literal = 1,

    /// <summary>A translation term (the host's text is that term's in its language): translated on the friend's PC.</summary>
    Term = 2,

    /// <summary>The toast's object's name in the friend's language.</summary>
    ObjectName = 3,

    /// <summary>The toast's object's description in the friend's language.</summary>
    ObjectDescription = 4,

    /// <summary>
    /// A text the game formats from a term and arguments (an injury's, a line change's): a <see cref="TextRecipe"/> the friend's game
    /// makes in its own language (<see cref="ToastText.Recipe"/>; the host's text is the fallback). Only sent when the host's own
    /// functions make the host's text from it exactly.
    /// </summary>
    Recipe = 5,
}

/// <summary>Where a toast's image comes from on a friend's PC. Values are part of the protocol: only add.</summary>
public enum ToastImageKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>The toast's object's own icon (the name is the sprite's, for a fallback).</summary>
    ObjectIcon = 1,

    /// <summary>A sprite the friend's game has loaded under this name.</summary>
    Named = 2,
}

/// <summary>
/// A toast's text: where it comes from on a friend's PC, and the host's text (shown when the friend's game can't make it).
/// </summary>
/// <param name="Kind">Where it comes from.</param>
/// <param name="Value">The host's shown text.</param>
/// <param name="Term">The translation term, for <see cref="ToastTextKind.Term"/> only (else null).</param>
/// <param name="Recipe">The recipe, for <see cref="ToastTextKind.Recipe"/> only (else null).</param>
public sealed record ToastText(ToastTextKind Kind, string Value, string? Term = null, TextRecipe? Recipe = null)
{
    /// <summary>For logs: the kind in words.</summary>
    public string How => Kind switch
    {
        ToastTextKind.Term => "a term",
        ToastTextKind.ObjectName => "its object's name",
        ToastTextKind.ObjectDescription => "its object's description",
        ToastTextKind.Recipe => "a recipe",
        _ => "the host's text",
    };

    /// <summary>For logs, as <see cref="ShownTextLog.Toast(ToastText?, Func{string?, string})"/> writes it (each text by its length): never the text itself.</summary>
    public override string ToString() => ShownTextLog.Toast(this, Diagnostics.HostText.TextShape);
}

/// <summary>A toast's image: where it comes from and the sprite's name.</summary>
/// <param name="Kind">Where it comes from.</param>
/// <param name="Name">The sprite's name.</param>
public sealed record ToastImage(ToastImageKind Kind, string Name);

/// <summary>
/// One of the host's run toasts (the game's <c>NotificationData</c>, queued with <c>NotificationQueue.Queue</c>) as a friend's
/// game rebuilds it with its own objects: its type and timing as the game's
/// numbers, the team by which of the game's teams it is, the skater by run skater id, the injury by its type and games,
/// the relic, talent or ability by asset id and level (<see cref="ObjectFromData"/>: the toast's own object, else only the
/// one its texts and icon came from), the texts as terms or object texts where they were (so a friend's game shows them in
/// its own language), else the host's, and the images by sprite name. Checked when it's written and when it's read
/// (<see cref="Problem"/>); immutable.
/// <code>
/// varint type · u32 timing · u8 team · bool home · optional name skater · bool injury [varint type · varint games] ·
/// u8 object kind [name id · varint level · bool from data] · optional text title · optional image icon ·
/// optional text description · varuint count, texts · varuint count, images
/// text: u8 kind (0: none, nothing follows) · optional text value (always there) · term: name term | recipe: text recipe ·
/// image: u8 kind (0: none, nothing follows) · name
/// (a custom text or image is never none)
/// </code>
/// </summary>
public sealed class NotificationCopy
{
    /// <summary>Longest text taken, in characters.</summary>
    public const int MaxTextChars = 256;

    /// <summary>Most custom texts taken (the game's toast shows a few).</summary>
    public const int MaxTexts = 8;

    /// <summary>Most custom images taken.</summary>
    public const int MaxImages = 4;

    /// <summary>Highest toast type number taken (the game's are 0-4 and 100).</summary>
    public const int MaxType = 255;

    /// <summary>Highest injury type number taken.</summary>
    public const int MaxInjuryType = 255;

    /// <summary>Most games an injury lasts, as the game's event data allows (-1: until healed).</summary>
    public const int MaxInjuryGames = 9999;

    /// <summary>Highest object level taken.</summary>
    public const int MaxLevel = 99;

    /// <summary>The toast's type (the game's <c>ENotificationType</c> number).</summary>
    public int Type { get; init; }

    /// <summary>When it shows (the game's <c>ENotificationTiming</c> flags).</summary>
    public int Timing { get; init; }

    /// <summary>Which team it's about.</summary>
    public ToastTeam Team { get; init; }

    /// <summary>The game's <c>IsTeamHome</c>.</summary>
    public bool IsTeamHome { get; init; }

    /// <summary>The skater it's about (run skater id), or null.</summary>
    public string? SkaterId { get; init; }

    /// <summary>The new injury's type (the game's <c>InjuryType</c> number), or null for no injury.</summary>
    public int? InjuryType { get; init; }

    /// <summary>The new injury's games (its healing time).</summary>
    public int InjuryGames { get; init; }

    /// <summary>The object's kind (<see cref="ToastObjectKind.None"/>: none).</summary>
    public ToastObjectKind ObjectKind { get; init; }

    /// <summary>The object's asset id ("" for none).</summary>
    public string ObjectId { get; init; } = string.Empty;

    /// <summary>The object's level.</summary>
    public int ObjectLevel { get; init; }

    /// <summary>The object is the toast's own (<c>PowerUpOrRelic</c>); else only its texts and icon came from it.</summary>
    public bool ObjectFromData { get; init; }

    /// <summary>The title, or null.</summary>
    public ToastText? Title { get; init; }

    /// <summary>The title's icon, or null.</summary>
    public ToastImage? TitleIcon { get; init; }

    /// <summary>The description, or null.</summary>
    public ToastText? Description { get; init; }

    /// <summary>The custom texts, in order (none: empty).</summary>
    public IReadOnlyList<ToastText> CustomTexts { get; init; } = Array.Empty<ToastText>();

    /// <summary>The custom images, in order (none: empty).</summary>
    public IReadOnlyList<ToastImage> CustomImages { get; init; } = Array.Empty<ToastImage>();

    /// <summary>
    /// For logs, as <see cref="ShownTextLog.Toast(NotificationCopy, Func{string?, string})"/> writes it with each text's length
    /// for its shape: "type 100, timing 32, relic 727e0295-..., title (a term) v2ui/mercy/notification/title, description (a
    /// recipe) format(term "v2ui/mercy/notification/desc", name of the run team)". A copy may come from a host, and a toast's
    /// text can hold a name a player typed: no text of it is written as it is.
    /// </summary>
    public override string ToString() => ShownTextLog.Toast(this, Diagnostics.HostText.TextShape);

    /// <summary>Why the copy can't be sent, or null when it can.</summary>
    public string? Problem()
    {
        if (Type is < 0 or > MaxType)
        {
            return $"type {Type} is out of range";
        }

        if (!Enum.IsDefined(Team))
        {
            return $"team {Team} is unknown";
        }

        if (InjuryType is < 0 or > MaxInjuryType || (InjuryType != null && InjuryGames is < -1 or > MaxInjuryGames))
        {
            return "the injury is out of range";
        }

        if (!Enum.IsDefined(ObjectKind))
        {
            return $"object kind {ObjectKind} is unknown";
        }

        if (ObjectKind == ToastObjectKind.None ? ObjectId.Length > 0 || ObjectFromData || ObjectLevel != 0 : ObjectLevel is < 0 or > MaxLevel)
        {
            return "the object doesn't fit its kind";
        }

        if (CustomTexts.Count > MaxTexts || CustomImages.Count > MaxImages)
        {
            return $"{CustomTexts.Count} texts and {CustomImages.Count} images are too many";
        }

        return EventWire.CheckName(SkaterId, "the skater id", required: false) ??
            (ObjectKind == ToastObjectKind.None ? null : EventWire.CheckName(ObjectId, "the object id", required: true)) ??
            CheckText(Title, "the title") ?? CheckText(Description, "the description") ?? CheckImage(TitleIcon, "the title icon") ??
            CustomTexts.Select((t, i) => CheckText(t, $"custom text {i}")).FirstOrDefault(p => p != null) ??
            CustomImages.Select((m, i) => CheckImage(m, $"custom image {i}")).FirstOrDefault(p => p != null);
    }

    /// <summary>Writes the copy. Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Problem();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteVarInt(Type);
        writer.WriteUInt32(unchecked((uint)Timing));
        writer.WriteByte((byte)Team);
        writer.WriteBool(IsTeamHome);
        EventWire.OptionalName(writer, SkaterId);
        writer.WriteBool(InjuryType != null);
        if (InjuryType is int injury)
        {
            writer.WriteVarInt(injury);
            writer.WriteVarInt(InjuryGames);
        }

        writer.WriteByte((byte)ObjectKind);
        if (ObjectKind != ToastObjectKind.None)
        {
            EventWire.Name(writer, ObjectId);
            writer.WriteVarInt(ObjectLevel);
            writer.WriteBool(ObjectFromData);
        }

        WriteText(writer, Title);
        WriteImage(writer, TitleIcon);
        WriteText(writer, Description);
        writer.WriteVarUInt((ulong)CustomTexts.Count);
        foreach (ToastText text in CustomTexts)
        {
            WriteText(writer, text);
        }

        writer.WriteVarUInt((ulong)CustomImages.Count);
        foreach (ToastImage image in CustomImages)
        {
            WriteImage(writer, image);
        }
    }

    /// <summary>Reads a copy. False for anything malformed or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out NotificationCopy? copy)
    {
        copy = null;
        bool ok = true;
        long type = reader.ReadVarInt();
        int timing = unchecked((int)reader.ReadUInt32());
        var team = (ToastTeam)reader.ReadByte();
        bool home = reader.ReadBool();
        if (reader.IsOverrun || type is < 0 or > MaxType || !EventWire.TryReadOptionalName(ref reader, out string? skater))
        {
            return false;
        }

        int? injury = null;
        int games = 0;
        if (reader.ReadBool())
        {
            injury = EventWire.Whole(ref reader, ref ok);
            games = EventWire.Whole(ref reader, ref ok);
        }

        var kind = (ToastObjectKind)reader.ReadByte();
        string id = string.Empty;
        int level = 0;
        bool fromData = false;
        if (ok && !reader.IsOverrun && kind != ToastObjectKind.None)
        {
            id = EventWire.ReadName(ref reader) ?? string.Empty;
            level = EventWire.Whole(ref reader, ref ok);
            fromData = reader.ReadBool();
        }

        if (!ok || reader.IsOverrun || !TryReadText(ref reader, out ToastText? title) || !TryReadImage(ref reader, out ToastImage? icon) ||
            !TryReadText(ref reader, out ToastText? description))
        {
            return false;
        }

        int textCount = EventWire.Count(ref reader, MaxTexts, ref ok);
        var texts = new List<ToastText>(ok ? textCount : 0);
        for (int i = 0; ok && i < textCount; i++)
        {
            if (!TryReadText(ref reader, out ToastText? text) || text == null)
            {
                return false;
            }

            texts.Add(text);
        }

        int imageCount = ok ? EventWire.Count(ref reader, MaxImages, ref ok) : 0;
        var images = new List<ToastImage>(ok ? imageCount : 0);
        for (int i = 0; ok && i < imageCount; i++)
        {
            if (!TryReadImage(ref reader, out ToastImage? image) || image == null)
            {
                return false;
            }

            images.Add(image);
        }

        if (!ok || reader.IsOverrun)
        {
            return false;
        }

        var read = new NotificationCopy
        {
            Type = (int)type,
            Timing = timing,
            Team = team,
            IsTeamHome = home,
            SkaterId = skater,
            InjuryType = injury,
            InjuryGames = games,
            ObjectKind = kind,
            ObjectId = id,
            ObjectLevel = level,
            ObjectFromData = fromData,
            Title = title,
            TitleIcon = icon,
            Description = description,
            CustomTexts = texts,
            CustomImages = images,
        };
        if (read.Problem() != null)
        {
            return false;
        }

        copy = read;
        return true;
    }

    private static string? CheckText(ToastText? text, string what)
    {
        if (text == null)
        {
            return null;
        }

        if (text.Kind is ToastTextKind.None || !Enum.IsDefined(text.Kind))
        {
            return $"{what} has no kind";
        }

        if (text.Value == null || text.Value.Length > MaxTextChars)
        {
            return $"{what} is missing or over {MaxTextChars} characters";
        }

        if ((text.Kind == ToastTextKind.Term) != (text.Term != null))
        {
            return $"{what} has a term only a term's text has";
        }

        if ((text.Kind == ToastTextKind.Recipe) != (text.Recipe != null))
        {
            return $"{what} has a recipe only a recipe's text has";
        }

        string? recipe = text.Recipe?.Check();
        return EventWire.CheckText(text.Value, what) ?? (text.Term == null ? null : EventWire.CheckName(text.Term, $"{what}'s term", required: true)) ??
            (recipe == null ? null : $"{what}'s recipe: {recipe}");
    }

    private static string? CheckImage(ToastImage? image, string what)
    {
        if (image == null)
        {
            return null;
        }

        return image.Kind is ToastImageKind.None || !Enum.IsDefined(image.Kind) ? $"{what} has no kind" : EventWire.CheckName(image.Name, what, required: true);
    }

    private static void WriteText(BitWriter writer, ToastText? text)
    {
        writer.WriteByte((byte)(text?.Kind ?? ToastTextKind.None));
        if (text != null)
        {
            EventWire.OptionalText(writer, text.Value);
            if (text.Kind == ToastTextKind.Term)
            {
                EventWire.Name(writer, text.Term!);
            }
            else if (text.Kind == ToastTextKind.Recipe)
            {
                text.Recipe!.Write(writer);
            }
        }
    }

    private static void WriteImage(BitWriter writer, ToastImage? image)
    {
        writer.WriteByte((byte)(image?.Kind ?? ToastImageKind.None));
        if (image != null)
        {
            EventWire.Name(writer, image.Name);
        }
    }

    private static bool TryReadText(ref BitReader reader, out ToastText? text)
    {
        text = null;
        var kind = (ToastTextKind)reader.ReadByte();
        if (reader.IsOverrun)
        {
            return false;
        }

        if (kind == ToastTextKind.None)
        {
            return true;
        }

        if (!EventWire.TryReadOptionalText(ref reader, out string? value) || value == null)
        {
            return false;
        }

        string? term = null;
        if (kind == ToastTextKind.Term && (term = EventWire.ReadName(ref reader)) == null)
        {
            return false;
        }

        TextRecipe? recipe = null;
        if (kind == ToastTextKind.Recipe && !TextRecipe.TryRead(ref reader, out recipe))
        {
            return false;
        }

        text = new ToastText(kind, value, term, recipe);
        return true;
    }

    private static bool TryReadImage(ref BitReader reader, out ToastImage? image)
    {
        image = null;
        var kind = (ToastImageKind)reader.ReadByte();
        if (reader.IsOverrun)
        {
            return false;
        }

        if (kind == ToastImageKind.None)
        {
            return true;
        }

        string? name = EventWire.ReadName(ref reader);
        if (name == null)
        {
            return false;
        }

        image = new ToastImage(kind, name);
        return true;
    }
}

/// <summary>What a toast message does on a friend's PC. Values are part of the protocol: only add.</summary>
public enum NotificationOp : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>Queue a toast (<c>NotificationQueue.Queue</c>).</summary>
    Queue = 1,

    /// <summary>Show the toasts queued for a timing (<c>NotificationQueue.PopQueue</c>).</summary>
    Pop = 2,

    /// <summary>Drop every queued toast (<c>NotificationQueue.Clear</c>).</summary>
    Clear = 3,
}

/// <summary>One thing the host's toast queue did, for friends' queues to do the same.</summary>
/// <param name="Op">What it did.</param>
/// <param name="Copy">The toast queued (<see cref="NotificationOp.Queue"/> only).</param>
/// <param name="Timing">The timings popped (<see cref="NotificationOp.Pop"/> only; the game's flags).</param>
/// <param name="KeepGoing">The pop's "keep going" (<see cref="NotificationOp.Pop"/> only).</param>
public sealed record NotificationEvent(NotificationOp Op, NotificationCopy? Copy, int Timing, bool KeepGoing)
{
    /// <summary>A toast queued.</summary>
    public static NotificationEvent Queued(NotificationCopy copy) => new(NotificationOp.Queue, copy ?? throw new ArgumentNullException(nameof(copy)), 0, false);

    /// <summary>The toasts of <paramref name="timing"/> popped.</summary>
    public static NotificationEvent Popped(int timing, bool keepGoing) => new(NotificationOp.Pop, null, timing, keepGoing);

    /// <summary>The queue cleared.</summary>
    public static NotificationEvent Cleared() => new(NotificationOp.Clear, null, 0, false);
}

/// <summary>
/// Host to friend (channel 1, <c>MessageType.Notification</c>): one <see cref="NotificationEvent"/>.
/// <code>u8 op · queue: <see cref="NotificationCopy"/> | pop: u32 timing · bool keep going | clear: nothing</code>
/// A bad message reads as false, never throws.
/// </summary>
public static class NotificationMessage
{
    /// <summary>The largest message body (a copy at its limits fits well under it).</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>Writes the payload (after the header). False, with nothing useful written, when a copy is out of range.</summary>
    public static bool Write(BitWriter writer, NotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(notification);
        switch (notification.Op)
        {
            case NotificationOp.Queue when notification.Copy != null && notification.Copy.Problem() == null:
                // A copy whose recipes (each up to a few KB at their limits) don't fit a message is refused here, not by the friend.
                int start = writer.BitLength;
                writer.WriteByte((byte)NotificationOp.Queue);
                notification.Copy.Write(writer);
                return writer.BitLength - start <= MaxBytes * 8;
            case NotificationOp.Pop:
                writer.WriteByte((byte)NotificationOp.Pop);
                writer.WriteUInt32(unchecked((uint)notification.Timing));
                writer.WriteBool(notification.KeepGoing);
                return true;
            case NotificationOp.Clear:
                writer.WriteByte((byte)NotificationOp.Clear);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the payload. False for anything malformed, an unknown op or a body over <see cref="MaxBytes"/>. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out NotificationEvent? notification)
    {
        notification = null;
        if (reader.RemainingBits > MaxBytes * 8)
        {
            return false;
        }

        var op = (NotificationOp)reader.ReadByte();
        switch (op)
        {
            case NotificationOp.Queue:
                if (!NotificationCopy.TryRead(ref reader, out NotificationCopy? copy) || copy == null)
                {
                    return false;
                }

                notification = NotificationEvent.Queued(copy);
                return true;
            case NotificationOp.Pop:
                int timing = unchecked((int)reader.ReadUInt32());
                bool keepGoing = reader.ReadBool();
                if (reader.IsOverrun)
                {
                    return false;
                }

                notification = NotificationEvent.Popped(timing, keepGoing);
                return true;
            case NotificationOp.Clear when !reader.IsOverrun:
                notification = NotificationEvent.Cleared();
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Which of the game's toasts are each player's own, so they're never sent to friends and a friend's own are never held
/// back: unlocks (type Unlock) and bench quests (timing BenchQuest) are each profile's, and so are the only ones queued for
/// the title screen, the benchwarmers' at a run's end ("kept powerups", hot and cold: <c>BenchSnapshotService.PushNotifications</c>,
/// Oct 29 build: every other maker queues for another timing): each friend's game runs that call itself, on its own profile
/// with its own roll (<see cref="RunEndingEntry"/>), so the host's toasts would name what the host's roll kept, not theirs.
/// Game-independent.
/// </summary>
public static class ToastOwner
{
    /// <summary>The game's <c>ENotificationType.Unlock</c>.</summary>
    public const int UnlockType = 4;

    /// <summary>The game's <c>ENotificationTiming.TitleScreen</c>.</summary>
    public const int TitleScreenTiming = 0x10;

    /// <summary>The game's <c>ENotificationTiming.BenchQuest</c>.</summary>
    public const int BenchQuestTiming = 0x80;

    /// <summary>True for a toast of <paramref name="type"/> and <paramref name="timing"/> that's each player's own (see the class).</summary>
    public static bool IsEachPlayersOwn(int type, int timing) => type == UnlockType || timing is BenchQuestTiming or TitleScreenTiming;
}

/// <summary>
/// The host's cap on toasts sent: at most <see cref="PerSecond"/> a second, the rest dropped and counted (a relic that
/// fires every frame mustn't fill the link). A token bucket: a burst of <see cref="PerSecond"/> at once, then one every
/// 1/<see cref="PerSecond"/> s. Not thread-safe.
/// </summary>
public sealed class ToastThrottle
{
    /// <summary>Toasts sent a second at most.</summary>
    public const int PerSecond = 8;

    private readonly long _microsPerToken;
    private readonly long _full;
    private long _tokens;
    private long _last = long.MinValue;

    /// <summary>The host's cap: <see cref="PerSecond"/> a second, as many at once.</summary>
    public ToastThrottle()
        : this(PerSecond, PerSecond)
    {
    }

    /// <summary><paramref name="perSecond"/> a second, <paramref name="burst"/> at once (a friend's cap on what a host sends: wider than the host's own, for a link catching up).</summary>
    public ToastThrottle(int perSecond, int burst)
    {
        _microsPerToken = 1_000_000 / Math.Max(1, perSecond);
        _full = Math.Max(1, burst) * _microsPerToken;
        _tokens = _full;
    }

    /// <summary>Toasts refused since the last <see cref="Reset"/>.</summary>
    public int Dropped { get; private set; }

    /// <summary>True when a toast may go at <paramref name="nowMicros"/> (it's counted); false when it's over the cap (counted in <see cref="Dropped"/>).</summary>
    public bool TryTake(long nowMicros)
    {
        if (_last != long.MinValue && nowMicros > _last)
        {
            _tokens = Math.Min(_full, _tokens + (nowMicros - _last));
        }

        _last = Math.Max(_last, nowMicros);
        if (_tokens >= _microsPerToken)
        {
            _tokens -= _microsPerToken;
            return true;
        }

        Dropped++;
        return false;
    }

    /// <summary>A full bucket again and nothing dropped (hosting started).</summary>
    public void Reset()
    {
        _tokens = _full;
        _last = long.MinValue;
        Dropped = 0;
    }
}

/// <summary>
/// Counts of toasts by type, for the log ("Generic 5, StatBonus 2"): what the host's game queued in a match (the probe), and
/// what each side sent, showed, skipped or dropped. Names come from the caller (the game's enum). Not thread-safe.
/// </summary>
public sealed class ToastTally
{
    private readonly SortedDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Every toast counted.</summary>
    public int Total { get; private set; }

    /// <summary>Counts one toast of <paramref name="type"/>. Returns true the first time this type is counted (since the last <see cref="Clear"/>).</summary>
    public bool Add(string type)
    {
        Total++;
        string key = string.IsNullOrEmpty(type) ? "?" : type;
        bool first = !_counts.TryGetValue(key, out int count);
        _counts[key] = count + 1;
        return first;
    }

    /// <summary>The counts by type ("Generic 5, StatBonus 2"), or "none".</summary>
    public override string ToString() => _counts.Count == 0 ? "none" : string.Join(", ", _counts.Select(c => $"{c.Key} {c.Value}"));

    /// <summary>Forgets every count.</summary>
    public void Clear()
    {
        _counts.Clear();
        Total = 0;
    }
}
