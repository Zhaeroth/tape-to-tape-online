using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>The run-start menu a <see cref="RunPreviewSet"/> goes with. Values are part of the protocol: only add.</summary>
public enum RunStartMenu : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>The game's <c>OldSquadMenu</c>: the superstar pick, or the old school squad's whole draft.</summary>
    OldSquad = 1,

    /// <summary>
    /// The game's <c>BenchwarmerMenu</c> (the lineup with benchwarmers): the run and the menu's benchwarmers
    /// (<see cref="RunPreviewSet.Pool"/>), no offers; sent in <see cref="MenuAttachment.RunPreviewLineupFormat"/>.
    /// </summary>
    Benchwarmer = 2,
}

/// <summary>What a run-start reward is (the old squad menu's power-up and relic offers). Values are part of the protocol: only add.</summary>
public enum RunStartRewardKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>A talent (the game's <c>Talent</c>).</summary>
    Talent = 1,

    /// <summary>An ability (the game's <c>Ability</c>).</summary>
    Ability = 2,

    /// <summary>A relic (the game's <c>Relic</c>).</summary>
    Relic = 3,
}

/// <summary>One of a reward's stickers (the game's <c>Sticker</c>): its stat (the game's <c>Sticker.StatType</c> number) and value.</summary>
/// <param name="Stat">The stat, as the game's number.</param>
/// <param name="Value">The bonus.</param>
public sealed record RunStartSticker(int Stat, int Value);

/// <summary>
/// One reward the host's old squad menu offered (a <c>PowerUpReward</c> or a <c>RelicReward</c> its roll made): the asset by
/// id and level, and the reward's stickers. A friend makes its own copy from its game's lists, as the reward menus do.
/// </summary>
public sealed class RunStartReward
{
    /// <summary>Most stickers on one reward.</summary>
    public const int MaxStickers = 8;

    /// <summary>What it is.</summary>
    public RunStartRewardKind Kind { get; init; }

    /// <summary>The asset's id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Its level.</summary>
    public int Level { get; init; }

    /// <summary>The reward's stickers, in order.</summary>
    public IReadOnlyList<RunStartSticker> Stickers { get; init; } = Array.Empty<RunStartSticker>();

    /// <summary>For logs: "talent \"speedster\" level 2 (1 sticker)".</summary>
    public override string ToString() =>
        $"{Kind.ToString().ToLowerInvariant()} \"{Diagnostics.HostText.KeyForLog(Id)}\" level {Level}{(Stickers.Count > 0 ? $" ({Stickers.Count} sticker(s))" : string.Empty)}";

    internal void Write(BitWriter writer)
    {
        writer.WriteByte((byte)Kind);
        EventWire.Name(writer, Id);
        writer.WriteVarInt(Level);
        writer.WriteVarUInt((ulong)Stickers.Count);
        foreach (RunStartSticker sticker in Stickers)
        {
            writer.WriteVarInt(sticker.Stat);
            writer.WriteVarInt(sticker.Value);
        }
    }

    internal static bool TryRead(ref BitReader reader, out RunStartReward? reward)
    {
        reward = null;
        var kind = (RunStartRewardKind)reader.ReadByte();
        string? id = EventWire.ReadName(ref reader);
        bool ok = id != null;
        int level = EventWire.Whole(ref reader, ref ok);
        int count = EventWire.Count(ref reader, MaxStickers, ref ok);
        var stickers = new List<RunStartSticker>(ok ? count : 0);
        for (int i = 0; ok && i < count; i++)
        {
            stickers.Add(new RunStartSticker(EventWire.Whole(ref reader, ref ok), EventWire.Whole(ref reader, ref ok)));
        }

        if (!ok || reader.IsOverrun)
        {
            return false;
        }

        var read = new RunStartReward { Kind = kind, Id = id!, Level = level, Stickers = stickers };
        if (read.Check("a reward") != null)
        {
            return false;
        }

        reward = read;
        return true;
    }

    internal string? Check(string what)
    {
        if (Kind is < RunStartRewardKind.Talent or > RunStartRewardKind.Relic)
        {
            return $"{what} is of kind {Kind}";
        }

        string? problem = EventWire.CheckName(Id, $"{what}'s id", required: true);
        if (problem != null)
        {
            return problem;
        }

        if (Level is < 0 or > 99)
        {
            return $"{what}'s level {Level} is out of range";
        }

        if (Stickers == null || Stickers.Count > MaxStickers)
        {
            return $"{what} has {Stickers?.Count ?? 0} stickers (at most {MaxStickers})";
        }

        foreach (RunStartSticker? sticker in Stickers)
        {
            if (sticker == null || sticker.Stat is < 0 or > 255 || sticker.Value is < -999 or > 999)
            {
                return $"{what}'s sticker is out of range";
            }
        }

        return null;
    }
}

/// <summary>
/// What a skater card shows that the run file's model of a forward (<see cref="FreeAgentData"/>) leaves out: the four hidden
/// stat offsets (the card shows each stat less its offset: <c>ForwardData.DisplayShotPower</c> and the rest), and the look
/// the model drops (the skater's build, glasses and away helmet, stick and skate colours, golden helmet, number colour,
/// height and weight). A friend's game gives them to the skater it builds from the model, so its card is the host's.
/// </summary>
public sealed class SkaterLook
{
    /// <summary>A look with nothing set (what a freshly made skater has).</summary>
    public static SkaterLook Default { get; } = new();

    /// <summary>The game's <c>_hiddenShotPower</c>.</summary>
    public int HiddenShotPower { get; init; }

    /// <summary>The game's <c>_hiddenSpeed</c>.</summary>
    public int HiddenSpeed { get; init; }

    /// <summary>The game's <c>_hiddenChecking</c>.</summary>
    public int HiddenChecking { get; init; }

    /// <summary>The game's <c>_hiddenShotAccuracy</c>.</summary>
    public int HiddenShotAccuracy { get; init; }

    /// <summary>The game's <c>isFemale</c>.</summary>
    public bool IsFemale { get; init; }

    /// <summary>The game's <c>HasGoldenHelmet</c>.</summary>
    public bool HasGoldenHelmet { get; init; }

    /// <summary>The game's <c>glassesSkin</c>, or null.</summary>
    public string? GlassesSkin { get; init; }

    /// <summary>The game's <c>helmetAwaySkin</c>, or null.</summary>
    public string? HelmetAwaySkin { get; init; }

    /// <summary>The game's <c>sizeOffsetPercentage</c>.</summary>
    public float SizeOffset { get; init; }

    /// <summary>The game's <c>numberColorOverride</c>.</summary>
    public ColorValue NumberColor { get; init; }

    /// <summary>The game's <c>StickColor</c> (null: none).</summary>
    public ColorSchemeData? StickColor { get; init; }

    /// <summary>The game's <c>SkatesColor</c> (null: none).</summary>
    public ColorSchemeData? SkatesColor { get; init; }

    /// <summary>The game's <c>heightInches</c>.</summary>
    public int HeightInches { get; init; }

    /// <summary>The game's <c>weightKg</c>.</summary>
    public int WeightKg { get; init; }

    internal void Write(BitWriter writer)
    {
        writer.WriteVarInt(HiddenShotPower);
        writer.WriteVarInt(HiddenSpeed);
        writer.WriteVarInt(HiddenChecking);
        writer.WriteVarInt(HiddenShotAccuracy);
        writer.WriteBool(IsFemale);
        writer.WriteBool(HasGoldenHelmet);
        EventWire.OptionalName(writer, GlassesSkin);
        EventWire.OptionalName(writer, HelmetAwaySkin);
        writer.WriteSingle(SizeOffset);
        ColorWire.Color(writer, NumberColor);
        ColorWire.OptionalScheme(writer, StickColor);
        ColorWire.OptionalScheme(writer, SkatesColor);
        writer.WriteVarInt(HeightInches);
        writer.WriteVarInt(WeightKg);
    }

    internal static bool TryRead(ref BitReader reader, out SkaterLook? look)
    {
        look = null;
        bool ok = true;
        int power = EventWire.Whole(ref reader, ref ok), speed = EventWire.Whole(ref reader, ref ok);
        int checking = EventWire.Whole(ref reader, ref ok), accuracy = EventWire.Whole(ref reader, ref ok);
        bool female = reader.ReadBool(), golden = reader.ReadBool();
        if (!ok || !EventWire.TryReadOptionalName(ref reader, out string? glasses) || !EventWire.TryReadOptionalName(ref reader, out string? helmetAway))
        {
            return false;
        }

        float size = reader.ReadSingle();
        ColorValue number = ColorWire.ReadColor(ref reader);
        ColorSchemeData? stick = ColorWire.ReadOptionalScheme(ref reader);
        ColorSchemeData? skates = ColorWire.ReadOptionalScheme(ref reader);
        int height = EventWire.Whole(ref reader, ref ok), weight = EventWire.Whole(ref reader, ref ok);
        if (!ok || reader.IsOverrun)
        {
            return false;
        }

        var read = new SkaterLook
        {
            HiddenShotPower = power,
            HiddenSpeed = speed,
            HiddenChecking = checking,
            HiddenShotAccuracy = accuracy,
            IsFemale = female,
            HasGoldenHelmet = golden,
            GlassesSkin = glasses,
            HelmetAwaySkin = helmetAway,
            SizeOffset = size,
            NumberColor = number,
            StickColor = stick,
            SkatesColor = skates,
            HeightInches = height,
            WeightKg = weight,
        };
        if (read.Check() != null)
        {
            return false;
        }

        look = read;
        return true;
    }

    internal string? Check()
    {
        if (Hidden(HiddenShotPower) || Hidden(HiddenSpeed) || Hidden(HiddenChecking) || Hidden(HiddenShotAccuracy))
        {
            return "a skater's hidden stat offset is out of range";
        }

        string? problem = EventWire.CheckName(GlassesSkin, "a skater's glasses", required: false, allowEmpty: true) ??
            EventWire.CheckName(HelmetAwaySkin, "a skater's away helmet", required: false, allowEmpty: true);
        if (problem != null)
        {
            return problem;
        }

        if (!float.IsFinite(SizeOffset) || SizeOffset is < -1000 or > 1000 || !NumberColor.IsValid || !ColorWire.IsValid(StickColor) || !ColorWire.IsValid(SkatesColor))
        {
            return "a skater's size or colours are out of range";
        }

        return HeightInches is < 0 or > 999 || WeightKg is < 0 or > 9999 ? "a skater's height or weight is out of range" : null;
    }

    private static bool Hidden(int value) => value is < -999 or > 999;
}

/// <summary>
/// One skater a run-start menu offered (a clone its roll made, not in the run): the run file's model of it, its look and its
/// number format. The run file's model has no field for the number format (the game's <c>DeserializerV2</c> builds a regular
/// number whatever the host's skater wore), so a Roman-numeral or Prison skater's back
/// number is carried here and given to the skater a friend builds.
/// </summary>
/// <param name="Model">The run file's model of the skater, with what the card shows besides (<see cref="FreeAgentData"/>).</param>
/// <param name="Look">What else its card shows (<see cref="SkaterLook"/>).</param>
/// <param name="NumberFormat">How its back number is written (the game's <c>NumberFormat</c>: 0 regular, 1 Roman numerals, 2 Prison).</param>
public sealed record RunStartSkater(FreeAgentData Model, SkaterLook Look, int NumberFormat = 0)
{
    /// <summary>For logs: the model's own ("skater fa-3b1c... #17 (from GM_Template_Ole), 3 talent(s)"), with a number format that isn't regular.</summary>
    public override string ToString() => (Model?.ToString() ?? "no skater") + (NumberFormat == 0 ? string.Empty : $", number format {NumberFormat}");

    /// <summary>Why the skater can't be sent ("it's missing", a field out of range), or null.</summary>
    internal string? Check() =>
        Model == null || Look == null ? "it's missing"
        : Model.Check() ?? Look.Check() ?? (NumberFormat is < 0 or > FreeAgentData.MaxNumberFormat ? $"its number format {NumberFormat} is out of range" : null);

    internal void Write(BitWriter writer)
    {
        Model.Write(writer);
        Look.Write(writer);
        writer.WriteVarInt(NumberFormat);
    }

    internal static bool TryRead(ref BitReader reader, out RunStartSkater? skater)
    {
        skater = null;
        bool ok = true;
        if (!FreeAgentData.TryRead(ref reader, out FreeAgentData? model) || !SkaterLook.TryRead(ref reader, out SkaterLook? look))
        {
            return false;
        }

        int format = EventWire.Whole(ref reader, ref ok);
        if (!ok || reader.IsOverrun || format is < 0 or > FreeAgentData.MaxNumberFormat)
        {
            return false;
        }

        skater = new RunStartSkater(model!, look!, format);
        return true;
    }
}

/// <summary>
/// One benchwarmer card of the host's lineup menu (its pool, in the menu's order): the skater the host's game built for it
/// (its profile's snapshot and quest talents included), and what the card shows from the host's profile and the skater's
/// asset besides: whether the host has unlocked it (a locked card shows locked), whether it's hot or cold (the card's
/// status flame or frost), and its bench power (the run file's model of a skater doesn't keep one).
/// </summary>
/// <param name="Skater">The skater as the run file's model and its look.</param>
/// <param name="Unlocked">The host's card shows it unlocked.</param>
/// <param name="Hot">The host's game counts it hot (its last run went well).</param>
/// <param name="Cold">The host's game counts it cold.</param>
/// <param name="BenchPowerId">Its bench power's id (the game's <c>BenchPower.ID</c>), or null for none.</param>
public sealed record RunStartBenchwarmer(RunStartSkater Skater, bool Unlocked, bool Hot, bool Cold, string? BenchPowerId = null)
{
    private const byte UnlockedBit = 1;
    private const byte HotBit = 2;
    private const byte ColdBit = 4;

    /// <summary>For logs: the skater's own, and "locked", "hot" or "cold" when it is.</summary>
    public override string ToString() =>
        $"{Skater}{(Unlocked ? string.Empty : ", locked")}{(Hot ? ", hot" : string.Empty)}{(Cold ? ", cold" : string.Empty)}" +
        (BenchPowerId == null ? string.Empty : $", bench power \"{Diagnostics.HostText.KeyForLog(BenchPowerId)}\"");

    internal void Write(BitWriter writer)
    {
        Skater.Write(writer);
        writer.WriteByte((byte)((Unlocked ? UnlockedBit : 0) | (Hot ? HotBit : 0) | (Cold ? ColdBit : 0)));
        EventWire.OptionalName(writer, BenchPowerId);
    }

    internal static bool TryRead(ref BitReader reader, out RunStartBenchwarmer? benchwarmer)
    {
        benchwarmer = null;
        if (!RunStartSkater.TryRead(ref reader, out RunStartSkater? skater))
        {
            return false;
        }

        byte flags = reader.ReadByte();
        if (reader.IsOverrun || (flags & ~(UnlockedBit | HotBit | ColdBit)) != 0 || !EventWire.TryReadOptionalName(ref reader, out string? benchPower))
        {
            return false;
        }

        var read = new RunStartBenchwarmer(skater!, (flags & UnlockedBit) != 0, (flags & HotBit) != 0, (flags & ColdBit) != 0, benchPower);
        if (read.Check() != null)
        {
            return false;
        }

        benchwarmer = read;
        return true;
    }

    /// <summary>Why its own fields can't be sent (its skater is checked with the rest), or null.</summary>
    internal string? Check() => EventWire.CheckName(BenchPowerId, "a benchwarmer's bench power", required: false);
}

/// <summary>
/// The host's run at a run-start menu, and what that menu offered: sent as a
/// <see cref="MenuAttachmentKind.RunPreview"/> attachment of the menu (format <see cref="MenuAttachment.RunPreviewFormat"/>).
/// <list type="bullet">
/// <item>The run: the bytes the game's own run repository writes for the host's in-memory run (a copy of the repository
/// pointed at the mod's file; the host's own run file and co-op slot are untouched), which a friend's game loads with its
/// own loader as an unnumbered preview of the run (its first real checkpoint replaces it).</item>
/// <item>The offers (the old squad menu's own rolls): its superstars and, for the old school squad's draft, its random
/// skaters (each the run file's model plus its look), power-ups and relics. A friend's menu shows these in place of its own
/// rolls.</item>
/// <item>The lineup with benchwarmers (<see cref="RunStartMenu.Benchwarmer"/>): no offers, but the menu's pool of
/// benchwarmers as the host's game built them (<see cref="Pool"/>; the host's profile's snapshots, quest talents, unlocks
/// and hot or cold status), which a friend's menu shows in place of its own profile's.</item>
/// </list>
/// Checked field by field when it's written and when it's read; immutable once made. An old squad menu's set is
/// <see cref="MenuAttachment.RunPreviewFormat"/> (format 3); a lineup's adds its pool after the relics
/// (<see cref="MenuAttachment.RunPreviewLineupFormat"/>, format 4). Formats 1 and 2 (before each skater carried its number
/// format) are refused.
/// <code>
/// u8 menu · u8 mode · bool home team · varuint run length · run bytes · u64 run hash ·
/// varuint superstars · skater* · varuint random skaters · skater* · varuint power-ups · reward* · varuint relics · reward*
/// [lineup only: varuint benchwarmers · (skater · u8 flags: 1 unlocked, 2 hot, 4 cold · optional bench power id)*]
/// skater: free agent (FreeAgentData) · look (SkaterLook) · varint number format (0..15)
/// </code>
/// </summary>
public sealed class RunPreviewSet
{
    /// <summary>Largest run file (a run at its start has no map yet: tens of KB; a whole checkpoint was 45 KB).</summary>
    public const int MaxRunBytes = 192 * 1024;

    /// <summary>Most skaters in one list (the game offers three).</summary>
    public const int MaxSkaters = 8;

    /// <summary>Most rewards in one list (the game offers three).</summary>
    public const int MaxRewards = 8;

    /// <summary>Most benchwarmers in a lineup's pool (the game has nine; a squad's own swap pool is its own list).</summary>
    public const int MaxPool = 16;

    /// <summary>The old squad menu's mode: the old school squad's whole draft (power-up, superstar, relic, skater; the game's <c>Mode.FullSequence</c>).</summary>
    public const int FullSequence = 0;

    /// <summary>The old squad menu's mode: the superstar pick alone (the game's <c>Mode.SuperStarOnly</c>).</summary>
    public const int SuperStarOnly = 1;

    private byte[] _run = Array.Empty<byte>();

    /// <summary>The run-start menu it goes with.</summary>
    public RunStartMenu Menu { get; init; } = RunStartMenu.OldSquad;

    /// <summary>The old squad menu's mode (<see cref="FullSequence"/> or <see cref="SuperStarOnly"/>; 0 for the bench).</summary>
    public int Mode { get; init; }

    /// <summary>The menu's team is the home team (its <c>IsTeamHome</c>).</summary>
    public bool IsTeamHome { get; init; } = true;

    /// <summary>The host's run as its game's run repository writes it (a run file's bytes).</summary>
    public byte[] RunFile
    {
        get => _run;
        init
        {
            _run = value ?? Array.Empty<byte>();
            RunHash = HashOf(_run);
        }
    }

    /// <summary>The run file's hash, as a checkpoint of it would have (<see cref="RunCheckpoint.Hash"/>): equal hashes, equal runs.</summary>
    public ulong RunHash { get; private init; } = HashOf(Array.Empty<byte>());

    /// <summary>The superstars it offered (in the menu's order).</summary>
    public IReadOnlyList<RunStartSkater> SuperStars { get; init; } = Array.Empty<RunStartSkater>();

    /// <summary>The random skaters it offered (the old school draft's last step), in order.</summary>
    public IReadOnlyList<RunStartSkater> RandomSkaters { get; init; } = Array.Empty<RunStartSkater>();

    /// <summary>The power-ups it offered (the old school draft's first step), in order.</summary>
    public IReadOnlyList<RunStartReward> PowerUps { get; init; } = Array.Empty<RunStartReward>();

    /// <summary>The relics it offered (the old school draft's third step), in order.</summary>
    public IReadOnlyList<RunStartReward> Relics { get; init; } = Array.Empty<RunStartReward>();

    /// <summary>The lineup menu's benchwarmers, in its order (its pool's cards; empty for an old squad menu).</summary>
    public IReadOnlyList<RunStartBenchwarmer> Pool { get; init; } = Array.Empty<RunStartBenchwarmer>();

    /// <summary>
    /// The attachment format it's sent in: a lineup's carries its pool (<see cref="MenuAttachment.RunPreviewLineupFormat"/>, format 4),
    /// an old squad menu's doesn't (<see cref="MenuAttachment.RunPreviewFormat"/>, format 3).
    /// </summary>
    public byte Format => Menu == RunStartMenu.Benchwarmer ? MenuAttachment.RunPreviewLineupFormat : MenuAttachment.RunPreviewFormat;

    /// <summary>The hash a checkpoint of <paramref name="runFile"/> alone would have.</summary>
    public static ulong HashOf(byte[] runFile) =>
        RunCheckpoint.ComputeHash(new[] { new KeyValuePair<RunCheckpointPart, byte[]>(RunCheckpointPart.Run, runFile ?? Array.Empty<byte>()) });

    /// <summary>For logs: "the superstar pick, run 31.2 KB (hash 0123...), 3 superstar(s)".</summary>
    public override string ToString()
    {
        string what = Menu switch
        {
            RunStartMenu.OldSquad when Mode == SuperStarOnly => "the superstar pick",
            RunStartMenu.OldSquad => "the old school draft",
            RunStartMenu.Benchwarmer => "the lineup",
            _ => Menu.ToString(),
        };
        var offers = new List<string>();
        Add(SuperStars.Count, "superstar(s)");
        Add(PowerUps.Count, "power-up(s)");
        Add(Relics.Count, "relic(s)");
        Add(RandomSkaters.Count, "random skater(s)");
        int special = SuperStars.Concat(RandomSkaters).Count(s => s?.NumberFormat is > 0) + Pool.Count(b => b?.Skater?.NumberFormat is > 0);
        if (special > 0)
        {
            offers.Add($"{special} with a Roman-numeral or Prison back number");
        }

        if (Menu == RunStartMenu.Benchwarmer)
        {
            int unlocked = 0, hot = 0, cold = 0;
            foreach (RunStartBenchwarmer benchwarmer in Pool)
            {
                unlocked += benchwarmer.Unlocked ? 1 : 0;
                hot += benchwarmer.Hot ? 1 : 0;
                cold += benchwarmer.Cold ? 1 : 0;
            }

            offers.Add($"{Pool.Count} benchwarmer(s) ({unlocked} unlocked, {hot} hot, {cold} cold)");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{what}, run {_run.Length / 1024.0:0.0} KB (hash {RunHash:x16}){(offers.Count > 0 ? ", " + string.Join(", ", offers) : string.Empty)}");

        void Add(int count, string name)
        {
            if (count > 0)
            {
                offers.Add($"{count} {name}");
            }
        }
    }

    /// <summary>Why the set can't be sent ("its superstar 2: a free agent's id is missing"), or null when it can.</summary>
    public string? Problem() => Check();

    /// <summary>Writes the set. Throws <see cref="ArgumentException"/> (saying what) for anything out of its limits.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteByte((byte)Menu);
        writer.WriteByte((byte)Mode);
        writer.WriteBool(IsTeamHome);
        writer.WriteVarUInt((ulong)_run.Length);
        writer.WriteBytes(_run);
        writer.WriteUInt64(RunHash);
        WriteSkaters(writer, SuperStars);
        WriteSkaters(writer, RandomSkaters);
        WriteRewards(writer, PowerUps);
        WriteRewards(writer, Relics);
        if (Menu == RunStartMenu.Benchwarmer)
        {
            // Format 4: the lineup's benchwarmers follow (an old squad menu's set stays format 3, with nothing after its relics).
            writer.WriteVarUInt((ulong)Pool.Count);
            foreach (RunStartBenchwarmer benchwarmer in Pool)
            {
                benchwarmer.Write(writer);
            }
        }
    }

    /// <summary>
    /// Reads a set (a lineup's with its pool, as its menu byte says). False for anything malformed or out of its limits, or a
    /// run whose hash isn't its own. Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out RunPreviewSet? set)
    {
        set = null;
        var menu = (RunStartMenu)reader.ReadByte();
        int mode = reader.ReadByte();
        bool home = reader.ReadBool();
        ulong length = reader.ReadVarUInt();
        if (reader.IsOverrun || length is 0 or > MaxRunBytes || (long)length * 8 > reader.RemainingBits)
        {
            return false;
        }

        byte[] run = new byte[(int)length];
        reader.ReadBytes(run);
        ulong hash = reader.ReadUInt64();
        if (reader.IsOverrun || hash != HashOf(run) ||
            !TryReadSkaters(ref reader, out List<RunStartSkater>? superStars) || !TryReadSkaters(ref reader, out List<RunStartSkater>? random) ||
            !TryReadRewards(ref reader, out List<RunStartReward>? powerUps) || !TryReadRewards(ref reader, out List<RunStartReward>? relics))
        {
            return false;
        }

        List<RunStartBenchwarmer>? pool = null;
        if (menu == RunStartMenu.Benchwarmer && !TryReadPool(ref reader, out pool))
        {
            return false;
        }

        var read = new RunPreviewSet
        {
            Menu = menu,
            Mode = mode,
            IsTeamHome = home,
            RunFile = run,
            SuperStars = superStars!,
            RandomSkaters = random!,
            PowerUps = powerUps!,
            Relics = relics!,
            Pool = (IReadOnlyList<RunStartBenchwarmer>?)pool ?? Array.Empty<RunStartBenchwarmer>(),
        };
        if (read.Check() != null)
        {
            return false;
        }

        set = read;
        return true;
    }

    private string? Check()
    {
        if (_run.Length is 0 or > MaxRunBytes)
        {
            return $"the run is {_run.Length} bytes (1 to {MaxRunBytes})";
        }

        switch (Menu)
        {
            case RunStartMenu.OldSquad when Mode is not (FullSequence or SuperStarOnly):
                return $"the old squad menu's mode {Mode} is no mode";
            case RunStartMenu.OldSquad when Mode == SuperStarOnly && (PowerUps.Count > 0 || Relics.Count > 0 || RandomSkaters.Count > 0):
                return "the superstar pick offers only superstars";
            case RunStartMenu.OldSquad when Pool == null || Pool.Count > 0:
                return "the old squad menu carries no benchwarmers";
            case RunStartMenu.Benchwarmer when Mode != 0 || SuperStars.Count + RandomSkaters.Count + PowerUps.Count + Relics.Count > 0:
                return "the lineup carries the run and its benchwarmers only";
            case RunStartMenu.OldSquad or RunStartMenu.Benchwarmer:
                break;
            default:
                return $"the run-start menu {Menu} is no menu";
        }

        return CheckSkaters(SuperStars, "superstar") ?? CheckSkaters(RandomSkaters, "random skater") ??
            CheckRewards(PowerUps, "power-up", RunStartRewardKind.Talent, RunStartRewardKind.Ability) ??
            CheckRewards(Relics, "relic", RunStartRewardKind.Relic, RunStartRewardKind.Relic) ?? CheckPool(Pool);
    }

    private static string? CheckPool(IReadOnlyList<RunStartBenchwarmer>? pool)
    {
        if (pool == null || pool.Count > MaxPool)
        {
            return $"{pool?.Count ?? 0} benchwarmers (at most {MaxPool})";
        }

        for (int i = 0; i < pool.Count; i++)
        {
            RunStartSkater? skater = pool[i]?.Skater;
            string? problem = skater == null ? "it's missing" : skater.Check() ?? pool[i].Check();
            if (problem != null)
            {
                return $"its benchwarmer {i + 1}: {problem}";
            }
        }

        return null;
    }

    private static bool TryReadPool(ref BitReader reader, out List<RunStartBenchwarmer>? pool)
    {
        pool = null;
        bool ok = true;
        int count = EventWire.Count(ref reader, MaxPool, ref ok);
        if (!ok)
        {
            return false;
        }

        var list = new List<RunStartBenchwarmer>(count);
        for (int i = 0; i < count; i++)
        {
            if (!RunStartBenchwarmer.TryRead(ref reader, out RunStartBenchwarmer? benchwarmer))
            {
                return false;
            }

            list.Add(benchwarmer!);
        }

        pool = list;
        return true;
    }

    private static string? CheckSkaters(IReadOnlyList<RunStartSkater>? skaters, string what)
    {
        if (skaters == null || skaters.Count > MaxSkaters)
        {
            return $"{skaters?.Count ?? 0} {what}s (at most {MaxSkaters})";
        }

        for (int i = 0; i < skaters.Count; i++)
        {
            string? problem = skaters[i] == null ? "it's missing" : skaters[i].Check();
            if (problem != null)
            {
                return $"its {what} {i + 1}: {problem}";
            }
        }

        return null;
    }

    private static string? CheckRewards(IReadOnlyList<RunStartReward>? rewards, string what, RunStartRewardKind first, RunStartRewardKind last)
    {
        if (rewards == null || rewards.Count > MaxRewards)
        {
            return $"{rewards?.Count ?? 0} {what}s (at most {MaxRewards})";
        }

        for (int i = 0; i < rewards.Count; i++)
        {
            RunStartReward? reward = rewards[i];
            string? problem = reward == null ? $"its {what} {i + 1} is missing"
                : reward.Kind < first || reward.Kind > last ? $"its {what} {i + 1} is a {reward.Kind}"
                : reward.Check($"its {what} {i + 1}");
            if (problem != null)
            {
                return problem;
            }
        }

        return null;
    }

    private static void WriteSkaters(BitWriter writer, IReadOnlyList<RunStartSkater> skaters)
    {
        writer.WriteVarUInt((ulong)skaters.Count);
        foreach (RunStartSkater skater in skaters)
        {
            skater.Write(writer);
        }
    }

    private static void WriteRewards(BitWriter writer, IReadOnlyList<RunStartReward> rewards)
    {
        writer.WriteVarUInt((ulong)rewards.Count);
        foreach (RunStartReward reward in rewards)
        {
            reward.Write(writer);
        }
    }

    private static bool TryReadSkaters(ref BitReader reader, out List<RunStartSkater>? skaters)
    {
        skaters = null;
        bool ok = true;
        int count = EventWire.Count(ref reader, MaxSkaters, ref ok);
        if (!ok)
        {
            return false;
        }

        var list = new List<RunStartSkater>(count);
        for (int i = 0; i < count; i++)
        {
            if (!RunStartSkater.TryRead(ref reader, out RunStartSkater? skater))
            {
                return false;
            }

            list.Add(skater!);
        }

        skaters = list;
        return true;
    }

    private static bool TryReadRewards(ref BitReader reader, out List<RunStartReward>? rewards)
    {
        rewards = null;
        bool ok = true;
        int count = EventWire.Count(ref reader, MaxRewards, ref ok);
        if (!ok)
        {
            return false;
        }

        var list = new List<RunStartReward>(count);
        for (int i = 0; i < count; i++)
        {
            if (!RunStartReward.TryRead(ref reader, out RunStartReward? reward))
            {
                return false;
            }

            list.Add(reward!);
        }

        rewards = list;
        return true;
    }
}

/// <summary>Colours on the wire (a colour is four floats; a scheme three colours; a team's colours eight optional schemes).</summary>
internal static class ColorWire
{
    /// <summary>Writes a colour.</summary>
    public static void Color(BitWriter writer, ColorValue color)
    {
        writer.WriteSingle(color.R);
        writer.WriteSingle(color.G);
        writer.WriteSingle(color.B);
        writer.WriteSingle(color.A);
    }

    /// <summary>Reads a colour (check it with <see cref="ColorValue.IsValid"/>).</summary>
    public static ColorValue ReadColor(ref BitReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    /// <summary>Writes a scheme, or that there's none.</summary>
    public static void OptionalScheme(BitWriter writer, ColorSchemeData? scheme)
    {
        writer.WriteBool(scheme != null);
        if (scheme != null)
        {
            Color(writer, scheme.Primary);
            Color(writer, scheme.Secondary);
            Color(writer, scheme.Tertiary);
        }
    }

    /// <summary>Reads a scheme, or null for none.</summary>
    public static ColorSchemeData? ReadOptionalScheme(ref BitReader reader) =>
        reader.ReadBool() ? new ColorSchemeData(ReadColor(ref reader), ReadColor(ref reader), ReadColor(ref reader)) : null;

    /// <summary>Writes a team's colours (null: none; else the eight schemes, each optional).</summary>
    public static void OptionalSchemes(BitWriter writer, IReadOnlyList<ColorSchemeData?>? schemes)
    {
        writer.WriteBool(schemes != null);
        if (schemes != null)
        {
            foreach (ColorSchemeData? scheme in schemes)
            {
                OptionalScheme(writer, scheme);
            }
        }
    }

    /// <summary>Reads a team's colours (null: none). False when it's cut short.</summary>
    public static bool TryReadOptionalSchemes(ref BitReader reader, out ColorSchemeData?[]? schemes)
    {
        schemes = null;
        if (!reader.ReadBool())
        {
            return !reader.IsOverrun;
        }

        var read = new ColorSchemeData?[FreeAgentData.SchemeCount];
        for (int i = 0; i < read.Length && !reader.IsOverrun; i++)
        {
            read[i] = ReadOptionalScheme(ref reader);
        }

        schemes = read;
        return !reader.IsOverrun;
    }

    /// <summary>True for none, or a scheme whose three colours are valid.</summary>
    public static bool IsValid(ColorSchemeData? scheme) => scheme == null || (scheme.Primary.IsValid && scheme.Secondary.IsValid && scheme.Tertiary.IsValid);

    /// <summary>True for none, or <see cref="FreeAgentData.SchemeCount"/> valid (or missing) schemes.</summary>
    public static bool IsValid(IReadOnlyList<ColorSchemeData?>? schemes)
    {
        if (schemes == null)
        {
            return true;
        }

        if (schemes.Count != FreeAgentData.SchemeCount)
        {
            return false;
        }

        foreach (ColorSchemeData? scheme in schemes)
        {
            if (!IsValid(scheme))
            {
                return false;
            }
        }

        return true;
    }
}
