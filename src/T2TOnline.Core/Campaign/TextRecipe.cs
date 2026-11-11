using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>Which of an event choice's objects a text recipe or an image source names. Values are part of the protocol: only add.</summary>
public enum ChoiceObjectSlot : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The choice's power-up or relic (<see cref="EventChoiceData.Object1"/>).</summary>
    Object1 = 1,

    /// <summary>The choice's second power-up or relic (<see cref="EventChoiceData.Object2"/>).</summary>
    Object2 = 2,

    /// <summary>One of the choice's free agent's talents (<see cref="ChoiceObjectRef.Index"/>: its number, from 0).</summary>
    AgentTalent = 3,

    /// <summary>The choice's free agent's ability.</summary>
    AgentAbility = 4,
}

/// <summary>One of an event choice's objects: a slot and, for a free agent's talent, its number.</summary>
/// <param name="Slot">Which object.</param>
/// <param name="Index">A free agent's talent's number (from 0), else 0.</param>
public readonly record struct ChoiceObjectRef(ChoiceObjectSlot Slot, int Index = 0)
{
    /// <summary>No object.</summary>
    public static ChoiceObjectRef None => default;

    /// <summary>The choice's power-up or relic.</summary>
    public static ChoiceObjectRef Object1 => new(ChoiceObjectSlot.Object1);

    /// <summary>The choice's second power-up or relic.</summary>
    public static ChoiceObjectRef Object2 => new(ChoiceObjectSlot.Object2);

    /// <summary>The choice's free agent's ability.</summary>
    public static ChoiceObjectRef AgentAbility => new(ChoiceObjectSlot.AgentAbility);

    /// <summary>True for no object.</summary>
    public bool IsNone => Slot == ChoiceObjectSlot.None;

    /// <summary>The choice's free agent's talent number <paramref name="index"/> (from 0).</summary>
    public static ChoiceObjectRef AgentTalent(int index) => new(ChoiceObjectSlot.AgentTalent, index);

    /// <summary>For logs: "object 1", "the free agent's talent 2".</summary>
    public override string ToString() => Slot switch
    {
        ChoiceObjectSlot.None => "none",
        ChoiceObjectSlot.Object1 => "object 1",
        ChoiceObjectSlot.Object2 => "object 2",
        ChoiceObjectSlot.AgentTalent => string.Create(CultureInfo.InvariantCulture, $"the free agent's talent {Index + 1}"),
        ChoiceObjectSlot.AgentAbility => "the free agent's ability",
        _ => string.Create(CultureInfo.InvariantCulture, $"slot {(int)Slot}"),
    };

    /// <summary>True when <paramref name="choice"/> has this object (none always is).</summary>
    public bool IsOn(EventChoiceData choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return Slot switch
        {
            ChoiceObjectSlot.None => true,
            ChoiceObjectSlot.Object1 => choice.Object1 != null,
            ChoiceObjectSlot.Object2 => choice.Object2 != null,
            ChoiceObjectSlot.AgentTalent => choice.FreeAgent != null && Index < choice.FreeAgent.Talents.Count,
            ChoiceObjectSlot.AgentAbility => choice.FreeAgent?.AbilityId != null,
            _ => false,
        };
    }

    /// <summary>True for a slot this version knows, with an index only for a free agent's talent (below <see cref="FreeAgentData.MaxTalents"/>).</summary>
    internal bool IsValid => Slot switch
    {
        ChoiceObjectSlot.AgentTalent => Index is >= 0 and < FreeAgentData.MaxTalents,
        ChoiceObjectSlot.None or ChoiceObjectSlot.Object1 or ChoiceObjectSlot.Object2 or ChoiceObjectSlot.AgentAbility => Index == 0,
        _ => false,
    };

    internal void Write(BitWriter writer)
    {
        writer.WriteByte((byte)Slot);
        if (Slot == ChoiceObjectSlot.AgentTalent)
        {
            writer.WriteVarUInt((ulong)Index);
        }
    }

    internal static bool TryRead(ref BitReader reader, out ChoiceObjectRef value)
    {
        var slot = (ChoiceObjectSlot)reader.ReadByte();
        bool ok = true;
        int index = slot == ChoiceObjectSlot.AgentTalent ? EventWire.Count(ref reader, FreeAgentData.MaxTalents - 1, ref ok) : 0;
        value = new ChoiceObjectRef(slot, index);
        return ok && !reader.IsOverrun && value.IsValid;
    }
}

/// <summary>What a <see cref="TextRecipe"/> node does. Values are part of the protocol: only add.</summary>
public enum TextOp : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>A text as it is (a name, a number, a colour): <see cref="TextRecipe.Text"/>.</summary>
    Literal = 1,

    /// <summary>A translation term's text in this PC's language (the game's <c>LocalizationManager.GetTranslation</c>).</summary>
    Term = 2,

    /// <summary>The game's <c>SafeStringFormatter.SafeFormat</c> of the first argument with the others.</summary>
    Format = 3,

    /// <summary>The arguments joined (<c>string.Concat</c>).</summary>
    Concat = 4,

    /// <summary>A choice object's name (the game's <c>PowerUp.localizedPowerupName</c>).</summary>
    ObjectName = 5,

    /// <summary>A choice object's description (the game's <c>PowerUp.GetDescription(includeUpgrade, includeStatBonus, colour)</c>).</summary>
    ObjectDescription = 6,

    /// <summary>A forward stat's name (the game's <c>UISkaterUtils.GetForwardStat(stat, description)</c>).</summary>
    StatName = 7,

    /// <summary>A Nurse procedure's name (the game's <c>NurseProcedureScreen.GetProcedureName</c>).</summary>
    NurseProcedureName = 8,

    /// <summary>A Nurse card's description (the game's <c>NurseProcedureScreen.GetCardDescription</c>).</summary>
    NurseCardDescription = 9,

    /// <summary>
    /// A team's display name (the game's <c>CampaignUtil.GetTeamDisplayName</c>: a squad's translated name for the run's team):
    /// <see cref="TextRecipe.Number"/> is the <see cref="ToastTeam"/>. Made by <see cref="IToastTextFunctions"/>.
    /// </summary>
    TeamName = 10,

    /// <summary>
    /// A relic's, talent's or ability's name (the game's <c>localizedRelicName</c> or <c>localizedPowerupName</c>), found by
    /// <see cref="TextRecipe.AssetKind"/>, the id in <see cref="TextRecipe.Text"/> and the level in <see cref="TextRecipe.Number"/>.
    /// Made by <see cref="IToastTextFunctions"/>.
    /// </summary>
    AssetName = 11,

    /// <summary>An injury type's name (the game's <c>UIInjuryUtils.GetInjuryName</c>): <see cref="TextRecipe.Number"/> is the <c>InjuryType</c>. Made by <see cref="IToastTextFunctions"/>.</summary>
    InjuryName = 12,

    /// <summary>
    /// A forward stat's name as the toasts write it (the game's <c>EForwardStatExtensions.GetLocalizedName</c>: its keyword-tagged
    /// name): <see cref="TextRecipe.Number"/> is the <c>EForwardStat</c>. Made by <see cref="IToastTextFunctions"/>.
    /// </summary>
    LocalizedStatName = 13,
}

/// <summary>
/// The game's functions a <see cref="TextRecipe"/> is made with, on one PC, for one event choice: each returns the game's
/// own text in that PC's language, or null when it can't (a term missing, an object the choice doesn't have, a value the
/// game doesn't know). The plugin implements it with the game's own calls; a recipe catches anything one throws.
/// </summary>
public interface ITextFunctions
{
    /// <summary>A translation term's text, or null.</summary>
    string? Term(string key);

    /// <summary>The game's safe format of <paramref name="format"/> with <paramref name="args"/>, or null.</summary>
    string? Format(string format, IReadOnlyList<string> args);

    /// <summary>A choice object's name, or null.</summary>
    string? ObjectName(ChoiceObjectRef target);

    /// <summary>A choice object's description, or null.</summary>
    string? ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex);

    /// <summary>A forward stat's name (the game's <c>EForwardStat</c> number), or null.</summary>
    string? StatName(int stat, bool description);

    /// <summary>A Nurse procedure's name (the game's <c>Procedure</c> number), or null.</summary>
    string? NurseProcedureName(int procedure);

    /// <summary>A Nurse card's description: its procedure (or none), disclosed talent (or none), injury offer (or none) and games; or null.</summary>
    string? NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames);
}

/// <summary>
/// The game's functions the toasts' (and formatted prompts') recipes need besides <see cref="ITextFunctions"/>: names the game
/// writes in the player's language and a recipe can't make from a term alone. A recipe op that needs one on a PC whose functions
/// don't implement this makes nothing (null). Each returns null when it can't (a team this PC doesn't hold, an asset it lacks,
/// a value the game doesn't know).
/// </summary>
public interface IToastTextFunctions : ITextFunctions
{
    /// <summary>A team's display name (the game's <c>CampaignUtil.GetTeamDisplayName</c>), or null.</summary>
    string? TeamName(ToastTeam team);

    /// <summary>A relic's, talent's or ability's name in this PC's language, by kind, id and level, or null.</summary>
    string? AssetName(ToastObjectKind kind, string id, int level);

    /// <summary>An injury's name (the game's <c>UIInjuryUtils.GetInjuryName</c>; the <c>InjuryType</c> number), or null.</summary>
    string? InjuryName(int injury);

    /// <summary>A forward stat's keyword-tagged name (the game's <c>EForwardStatExtensions.GetLocalizedName</c>; the <c>EForwardStat</c> number), or null.</summary>
    string? LocalizedStatName(int stat);
}

/// <summary>
/// How the host's game made one of an event choice's translated texts, or a toast's or prompt's formatted one, as a small tree
/// of the game's own text functions over the choice's own objects and values: a friend's game makes the same text in its own language with the same
/// functions and its copies of the same objects. The host sends a recipe only when it makes the host's text exactly on the
/// host's PC. Immutable; checked when it's written and when it's read (at most
/// <see cref="MaxDepth"/> deep, <see cref="MaxNodes"/> nodes, <see cref="MaxArgs"/> arguments, strings up to
/// <see cref="MaxTextBytes"/> bytes). <see cref="Evaluate"/> never throws.
/// <code>
/// u8 op · literal, term: string · format: u8 n (1..9: the format, then its arguments) · node* · concat: u8 n (1..8) · node* ·
/// object name: ref · description: ref, bool upgrade, bool stat bonus, colour (≤ 16 bytes) · stat: varint (0..63), bool ·
/// Nurse name: varint (0..31) · Nurse card: varint procedure (-1 none), ref (may be none), varint injury (-1 none), varint games ·
/// team name: u8 team (1 run, 2 home, 3 visitor) · asset name: u8 kind (1 relic, 2 talent, 3 ability), string id, varint level (0..99) ·
/// injury name: varint (0..63) · localized stat: varint (0..63)
/// ref: u8 slot (0 none, 1 object 1, 2 object 2, 3 agent talent + varuint index, 4 agent ability)
/// </code>
/// </summary>
public sealed class TextRecipe
{
    /// <summary>Deepest tree (a lone node is 1).</summary>
    public const int MaxDepth = 4;

    /// <summary>Most nodes in a tree.</summary>
    public const int MaxNodes = 16;

    /// <summary>Most arguments of a format (besides the format) or parts of a concat.</summary>
    public const int MaxArgs = 8;

    /// <summary>Longest literal or term, in UTF-8 bytes.</summary>
    public const int MaxTextBytes = 256;

    /// <summary>Longest colour (a description's highlight, "1FFF75"), in UTF-8 bytes.</summary>
    public const int MaxColorBytes = 16;

    /// <summary>Highest stat number (the game's <c>EForwardStat</c> and room for more).</summary>
    public const int MaxStat = 63;

    /// <summary>Highest Nurse procedure number (the game's <c>Procedure</c> and room for more).</summary>
    public const int MaxProcedure = 31;

    /// <summary>Highest injury type number.</summary>
    public const int MaxInjury = 63;

    /// <summary>Most injury games.</summary>
    public const int MaxGames = 9999;

    /// <summary>Highest asset level (the toasts' limit).</summary>
    public const int MaxAssetLevel = NotificationCopy.MaxLevel;

    /// <summary>The longest text a format or concat of a recipe may make, in characters (a card's text is far shorter): one that would make more makes nothing.</summary>
    public const int MaxMadeChars = 16 * 1024;

    private TextRecipe(TextOp op, string? text = null, ChoiceObjectRef target = default, int number = 0, int injury = -1, int games = 0,
        bool flag1 = false, bool flag2 = false, IReadOnlyList<TextRecipe>? args = null, ToastObjectKind assetKind = ToastObjectKind.None)
    {
        Op = op;
        AssetKind = assetKind;
        Text = text;
        Target = target;
        Number = number;
        Injury = injury;
        Games = games;
        Flag1 = flag1;
        Flag2 = flag2;
        Args = args ?? Array.Empty<TextRecipe>();
    }

    /// <summary>What it does.</summary>
    public TextOp Op { get; }

    /// <summary>A literal's text, a term's key, a description's colour, or an asset's id; else null.</summary>
    public string? Text { get; }

    /// <summary>An asset name's kind (a relic, talent or ability), else <see cref="ToastObjectKind.None"/>.</summary>
    public ToastObjectKind AssetKind { get; }

    /// <summary>The object a name, description or Nurse card is about (none otherwise).</summary>
    public ChoiceObjectRef Target { get; }

    /// <summary>A stat's number, a procedure's number (-1: none, on a Nurse card), a team's (<see cref="ToastTeam"/>), an injury's type, an asset's level, else 0.</summary>
    public int Number { get; }

    /// <summary>A Nurse card's injury type (-1: none), else -1.</summary>
    public int Injury { get; }

    /// <summary>A Nurse card's injury games, else 0.</summary>
    public int Games { get; }

    /// <summary>A description's <c>includeUpgrade</c>, or a stat name's <c>description</c>.</summary>
    public bool Flag1 { get; }

    /// <summary>A description's <c>includeStatBonus</c>.</summary>
    public bool Flag2 { get; }

    /// <summary>A format's format and arguments (the format first), or a concat's parts; empty otherwise.</summary>
    public IReadOnlyList<TextRecipe> Args { get; }

    /// <summary>A text as it is.</summary>
    public static TextRecipe Literal(string text) => new(TextOp.Literal, text ?? throw new ArgumentNullException(nameof(text)));

    /// <summary>A translation term's text.</summary>
    public static TextRecipe Term(string key) => new(TextOp.Term, key ?? throw new ArgumentNullException(nameof(key)));

    /// <summary>The game's safe format of <paramref name="format"/> with <paramref name="args"/>.</summary>
    public static TextRecipe Format(TextRecipe format, params TextRecipe[] args)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(args);
        return new(TextOp.Format, args: new[] { format }.Concat(args).ToArray());
    }

    /// <summary>The parts joined.</summary>
    public static TextRecipe Concat(params TextRecipe[] parts) => new(TextOp.Concat, args: (parts ?? throw new ArgumentNullException(nameof(parts))).ToArray());

    /// <summary>A choice object's name.</summary>
    public static TextRecipe ObjectName(ChoiceObjectRef target) => new(TextOp.ObjectName, target: target);

    /// <summary>A choice object's description.</summary>
    public static TextRecipe ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex) =>
        new(TextOp.ObjectDescription, colorHex ?? throw new ArgumentNullException(nameof(colorHex)), target, flag1: includeUpgrade, flag2: includeStatBonus);

    /// <summary>A forward stat's name.</summary>
    public static TextRecipe StatName(int stat, bool description) => new(TextOp.StatName, number: stat, flag1: description);

    /// <summary>A Nurse procedure's name.</summary>
    public static TextRecipe NurseProcedureName(int procedure) => new(TextOp.NurseProcedureName, number: procedure);

    /// <summary>A Nurse card's description.</summary>
    public static TextRecipe NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames) =>
        new(TextOp.NurseCardDescription, target: talent, number: procedure ?? -1, injury: injury ?? -1, games: injuryGames);

    /// <summary>A team's display name (the run's, home or visitor team's on the PC that makes it).</summary>
    public static TextRecipe TeamName(ToastTeam team) => new(TextOp.TeamName, number: (int)team);

    /// <summary>A relic's, talent's or ability's name, found by kind, id and level.</summary>
    public static TextRecipe AssetName(ToastObjectKind kind, string id, int level) =>
        new(TextOp.AssetName, id ?? throw new ArgumentNullException(nameof(id)), number: level, assetKind: kind);

    /// <summary>An injury type's name.</summary>
    public static TextRecipe InjuryName(int injury) => new(TextOp.InjuryName, number: injury);

    /// <summary>A forward stat's keyword-tagged name.</summary>
    public static TextRecipe LocalizedStatName(int stat) => new(TextOp.LocalizedStatName, number: stat);

    /// <summary>
    /// The text this recipe makes with <paramref name="functions"/>, or null when it can't be made (a function gave nothing
    /// or threw, or the recipe is out of its limits). Never throws.
    /// </summary>
    public string? Evaluate(ITextFunctions functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        if (Check() != null)
        {
            return null;
        }

        try
        {
            return Make(functions);
        }
        catch (Exception)
        {
            return null; // the game's function threw: no text from this recipe
        }
    }

    /// <summary>Every object the recipe names (none excluded).</summary>
    public IEnumerable<ChoiceObjectRef> Targets() =>
        (Target.IsNone ? Enumerable.Empty<ChoiceObjectRef>() : new[] { Target }).Concat(Args.SelectMany(a => a?.Targets() ?? Enumerable.Empty<ChoiceObjectRef>()));

    /// <summary>
    /// For logs: "format(term \"v2ui/event_choice/fan_no_1/name_desc_format\", name of object 1, ...)". A recipe may come from a
    /// host and a literal can be a name a player typed (a custom skater's or team's), so a literal with a letter in it is
    /// written as its length (<c>a text (11 characters)</c>), one without (a number, a format's holes) as it is; a term key
    /// and an asset id go by <see cref="Diagnostics.HostText.KeyForLog(string?)"/>.
    /// </summary>
    public override string ToString() => Op switch
    {
        TextOp.Literal => HasLetter(Text) ? $"a text ({Diagnostics.HostText.TextShape(Text)})" : $"\"{Diagnostics.SafeText.Clean(Text, 40)}\"",
        TextOp.Term => $"term \"{Diagnostics.HostText.KeyForLog(Text)}\"",
        TextOp.Format => $"format({string.Join(", ", Args)})",
        TextOp.Concat => $"concat({string.Join(", ", Args)})",
        TextOp.ObjectName => $"name of {Target}",
        TextOp.ObjectDescription => $"description of {Target}",
        TextOp.StatName => string.Create(CultureInfo.InvariantCulture, $"stat {Number}{(Flag1 ? " description" : string.Empty)}"),
        TextOp.NurseProcedureName => string.Create(CultureInfo.InvariantCulture, $"procedure {Number}"),
        TextOp.NurseCardDescription => string.Create(CultureInfo.InvariantCulture, $"Nurse card (procedure {Number}, {Target}, injury {Injury}, {Games} games)"),
        TextOp.TeamName => $"name of the {(Enum.IsDefined((ToastTeam)Number) ? ((ToastTeam)Number).ToString().ToLowerInvariant() : "?")} team",
        TextOp.AssetName => string.Create(CultureInfo.InvariantCulture, $"name of {AssetKind.ToString().ToLowerInvariant()} \"{Diagnostics.HostText.KeyForLog(Text)}\" level {Number}"),
        TextOp.InjuryName => string.Create(CultureInfo.InvariantCulture, $"injury {Number}'s name"),
        TextOp.LocalizedStatName => string.Create(CultureInfo.InvariantCulture, $"stat {Number}'s keyword name"),
        _ => Op.ToString(),
    };

    /// <summary>
    /// True when the text this recipe makes is the game's own words only: terms, objects' and stats' names, numbers. False
    /// when it holds a literal with a letter in it or a team's name (a custom team's, or a skater's in a literal, is typed by
    /// its player), so a log line writes such a text as its shape.
    /// </summary>
    public bool IsGameWords() => GameWordsBelow(0);

    /// <summary>True when <paramref name="text"/> has a letter in it (a name or a word, not a number or a format's holes).</summary>
    internal static bool HasLetter(string? text)
    {
        foreach (char c in text ?? string.Empty)
        {
            if (char.IsLetter(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Why the recipe can't be sent (too deep, too many nodes, a value out of range), or null when it can.</summary>
    public string? Check()
    {
        int nodes = 0;
        return Check(1, ref nodes);
    }

    /// <summary>Writes the recipe. Throws <see cref="ArgumentException"/> (saying what) when it's out of its limits.</summary>
    internal void Write(BitWriter writer)
    {
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException($"a text recipe: {problem}", nameof(writer));
        }

        WriteNode(writer);
    }

    /// <summary>Reads a recipe. False for anything malformed or out of its limits. Never throws.</summary>
    internal static bool TryRead(ref BitReader reader, out TextRecipe? recipe)
    {
        int nodes = 0;
        if (TryRead(ref reader, 1, ref nodes, out recipe) && recipe!.Check() == null)
        {
            return true;
        }

        recipe = null;
        return false;
    }

    private static bool TryRead(ref BitReader reader, int depth, ref int nodes, out TextRecipe? recipe)
    {
        recipe = null;
        if (depth > MaxDepth || ++nodes > MaxNodes)
        {
            return false;
        }

        var op = (TextOp)reader.ReadByte();
        bool ok = true;
        switch (op)
        {
            case TextOp.Literal or TextOp.Term:
                if (!EventWire.TryReadBounded(ref reader, MaxTextBytes, out string? text))
                {
                    return false;
                }

                recipe = new TextRecipe(op, text);
                break;
            case TextOp.Format or TextOp.Concat:
                int count = reader.ReadByte();
                if (reader.IsOverrun || count is 0 or > MaxArgs + 1)
                {
                    return false;
                }

                var args = new TextRecipe[count];
                for (int i = 0; i < count; i++)
                {
                    if (!TryRead(ref reader, depth + 1, ref nodes, out TextRecipe? arg))
                    {
                        return false;
                    }

                    args[i] = arg!;
                }

                recipe = new TextRecipe(op, args: args);
                break;
            case TextOp.ObjectName:
                if (!ChoiceObjectRef.TryRead(ref reader, out ChoiceObjectRef named))
                {
                    return false;
                }

                recipe = new TextRecipe(op, target: named);
                break;
            case TextOp.ObjectDescription:
                if (!ChoiceObjectRef.TryRead(ref reader, out ChoiceObjectRef described))
                {
                    return false;
                }

                bool upgrade = reader.ReadBool(), bonus = reader.ReadBool();
                if (!EventWire.TryReadBounded(ref reader, MaxColorBytes, out string? color))
                {
                    return false;
                }

                recipe = new TextRecipe(op, color, described, flag1: upgrade, flag2: bonus);
                break;
            case TextOp.StatName:
                int stat = EventWire.Whole(ref reader, ref ok);
                recipe = new TextRecipe(op, number: stat, flag1: reader.ReadBool());
                break;
            case TextOp.NurseProcedureName:
                recipe = new TextRecipe(op, number: EventWire.Whole(ref reader, ref ok));
                break;
            case TextOp.NurseCardDescription:
                int procedure = EventWire.Whole(ref reader, ref ok);
                if (!ok || !ChoiceObjectRef.TryRead(ref reader, out ChoiceObjectRef talent))
                {
                    return false;
                }

                int injury = EventWire.Whole(ref reader, ref ok), games = EventWire.Whole(ref reader, ref ok);
                recipe = new TextRecipe(op, target: talent, number: procedure, injury: injury, games: games);
                break;
            case TextOp.TeamName:
                recipe = new TextRecipe(op, number: reader.ReadByte());
                break;
            case TextOp.AssetName:
                var kind = (ToastObjectKind)reader.ReadByte();
                if (reader.IsOverrun || !EventWire.TryReadBounded(ref reader, MaxTextBytes, out string? id))
                {
                    return false;
                }

                recipe = new TextRecipe(op, id, number: EventWire.Whole(ref reader, ref ok), assetKind: kind);
                break;
            case TextOp.InjuryName or TextOp.LocalizedStatName:
                recipe = new TextRecipe(op, number: EventWire.Whole(ref reader, ref ok));
                break;
            default:
                return false;
        }

        if (!ok || reader.IsOverrun)
        {
            recipe = null;
            return false;
        }

        return true;
    }

    private void WriteNode(BitWriter writer)
    {
        writer.WriteByte((byte)Op);
        switch (Op)
        {
            case TextOp.Literal or TextOp.Term:
                EventWire.Bounded(writer, Text!, MaxTextBytes);
                break;
            case TextOp.Format or TextOp.Concat:
                writer.WriteByte((byte)Args.Count);
                foreach (TextRecipe arg in Args)
                {
                    arg.WriteNode(writer);
                }

                break;
            case TextOp.ObjectName:
                Target.Write(writer);
                break;
            case TextOp.ObjectDescription:
                Target.Write(writer);
                writer.WriteBool(Flag1);
                writer.WriteBool(Flag2);
                EventWire.Bounded(writer, Text!, MaxColorBytes);
                break;
            case TextOp.StatName:
                writer.WriteVarInt(Number);
                writer.WriteBool(Flag1);
                break;
            case TextOp.NurseProcedureName:
                writer.WriteVarInt(Number);
                break;
            case TextOp.NurseCardDescription:
                writer.WriteVarInt(Number);
                Target.Write(writer);
                writer.WriteVarInt(Injury);
                writer.WriteVarInt(Games);
                break;
            case TextOp.TeamName:
                writer.WriteByte((byte)Number);
                break;
            case TextOp.AssetName:
                writer.WriteByte((byte)AssetKind);
                EventWire.Bounded(writer, Text!, MaxTextBytes);
                writer.WriteVarInt(Number);
                break;
            case TextOp.InjuryName or TextOp.LocalizedStatName:
                writer.WriteVarInt(Number);
                break;
        }
    }

    private bool GameWordsBelow(int depth) => Op switch
    {
        TextOp.Literal => !HasLetter(Text),
        TextOp.TeamName => false,
        TextOp.Format or TextOp.Concat => depth <= MaxDepth && Args.All(arg => arg == null || arg.GameWordsBelow(depth + 1)),
        _ => true,
    };

    private string? Check(int depth, ref int nodes)
    {
        if (depth > MaxDepth)
        {
            return $"deeper than {MaxDepth}";
        }

        if (++nodes > MaxNodes)
        {
            return $"more than {MaxNodes} nodes";
        }

        if (!Target.IsValid)
        {
            return $"an object it names is out of range ({Target})";
        }

        bool hasArgs = Op is TextOp.Format or TextOp.Concat;
        if (!hasArgs && Args.Count != 0)
        {
            return $"a {Op} with arguments";
        }

        string? problem = Op switch
        {
            TextOp.Literal => EventWire.CheckBounded(Text, "a literal", MaxTextBytes, newlines: true, required: true),
            TextOp.Term => EventWire.CheckBounded(Text, "a term", MaxTextBytes, newlines: false, required: true) ?? (Text!.Length == 0 ? "an empty term" : null),
            TextOp.Format => Args.Count is < 1 or > MaxArgs + 1 ? $"a format with {Args.Count - 1} arguments" : null,
            TextOp.Concat => Args.Count is < 1 or > MaxArgs ? $"a concat of {Args.Count} parts" : null,
            TextOp.ObjectName => Target.IsNone ? "an object's name without an object" : null,
            TextOp.ObjectDescription => Target.IsNone ? "an object's description without an object" : EventWire.CheckBounded(Text, "a colour", MaxColorBytes, newlines: false, required: true),
            TextOp.StatName => Number is < 0 or > MaxStat ? $"stat {Number} is out of range" : null,
            TextOp.NurseProcedureName => Number is < 0 or > MaxProcedure ? $"procedure {Number} is out of range" : null,
            TextOp.NurseCardDescription => Number is < -1 or > MaxProcedure || Injury is < -1 or > MaxInjury || Games is < -1 or > MaxGames ? "a Nurse card's values are out of range" : null,
            TextOp.TeamName => Number is < (int)ToastTeam.Run or > (int)ToastTeam.Visitor ? $"team {Number} is no team" : null,
            TextOp.AssetName => AssetKind is not (ToastObjectKind.Relic or ToastObjectKind.Talent or ToastObjectKind.Ability) ? $"an asset of kind {AssetKind}"
                : Number is < 0 or > MaxAssetLevel ? $"asset level {Number} is out of range"
                : EventWire.CheckName(Text, "an asset's id", required: true),
            TextOp.InjuryName => Number is < 0 or > MaxInjury ? $"injury {Number} is out of range" : null,
            TextOp.LocalizedStatName => Number is < 0 or > MaxStat ? $"stat {Number} is out of range" : null,
            _ => $"an op of kind {Op}",
        };
        if (problem != null)
        {
            return problem;
        }

        // Only the fields its op has (the factories set no others; the wire carries no others).
        bool text = Op is TextOp.Literal or TextOp.Term or TextOp.ObjectDescription or TextOp.AssetName;
        bool target = Op is TextOp.ObjectName or TextOp.ObjectDescription or TextOp.NurseCardDescription;
        bool number = Op is TextOp.StatName or TextOp.NurseProcedureName or TextOp.NurseCardDescription or TextOp.TeamName or TextOp.AssetName or TextOp.InjuryName or TextOp.LocalizedStatName;
        bool card = Op == TextOp.NurseCardDescription;
        if ((!text && Text != null) || (!target && !Target.IsNone) || (!number && Number != 0) || (!card && (Injury != -1 || Games != 0)) ||
            (Op is not (TextOp.ObjectDescription or TextOp.StatName) && Flag1) || (Op != TextOp.ObjectDescription && Flag2) ||
            (Op != TextOp.AssetName && AssetKind != ToastObjectKind.None))
        {
            return $"a {Op} with a field it doesn't have";
        }

        foreach (TextRecipe arg in Args)
        {
            problem = arg == null ? "an argument is missing" : arg.Check(depth + 1, ref nodes);
            if (problem != null)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the format can't make more than <see cref="MaxMadeChars"/>: its own length and each of its placeholders as long
    /// as the longest value or as wide as it asks. Nested formats multiply (a crafted recipe within its limits could ask for
    /// 150 MB per text), and so does a width (the game's format pads a value to it: <c>{0,999999}</c>, 1 MB from 11 bytes).
    /// </summary>
    private static bool FormatFits(string format, string[] values)
    {
        int longest = 0;
        foreach (string value in values)
        {
            longest = Math.Max(longest, value.Length);
        }

        long made = format.Length;
        for (int open = format.IndexOf('{'); open >= 0 && made <= MaxMadeChars; open = format.IndexOf('{', open + 1))
        {
            made += Math.Max(longest, Width(format, open));
        }

        return made <= MaxMadeChars;
    }

    /// <summary>The width the placeholder opening at <paramref name="open"/> asks for (<c>{0,-12}</c>: 12), 0 for none; the composite format's own reading of it, and no more than just past <see cref="MaxMadeChars"/>.</summary>
    private static long Width(string format, int open)
    {
        int at = open + 1;
        while (at < format.Length && format[at] is (>= '0' and <= '9') or ' ')
        {
            at++;
        }

        if (at >= format.Length || format[at] != ',')
        {
            return 0;
        }

        at++;
        while (at < format.Length && format[at] is ' ' or '-')
        {
            at++;
        }

        long width = 0;
        while (at < format.Length && format[at] is >= '0' and <= '9' && width <= MaxMadeChars)
        {
            width = (width * 10) + (format[at] - '0');
            at++;
        }

        return width;
    }

    private string? Make(ITextFunctions functions)
    {
        switch (Op)
        {
            case TextOp.Literal:
                return Text;
            case TextOp.Term:
                return functions.Term(Text!);
            case TextOp.Format:
                string? format = Args[0].Make(functions);
                var values = new string[Args.Count - 1];
                for (int i = 1; i < Args.Count; i++)
                {
                    string? value = Args[i].Make(functions);
                    if (value == null)
                    {
                        return null;
                    }

                    values[i - 1] = value;
                }

                string? formatted = format == null || !FormatFits(format, values) ? null : functions.Format(format, values);
                return formatted?.Length > MaxMadeChars ? null : formatted;
            case TextOp.Concat:
                var parts = new string[Args.Count];
                int total = 0;
                for (int i = 0; i < Args.Count; i++)
                {
                    string? part = Args[i].Make(functions);
                    if (part == null)
                    {
                        return null;
                    }

                    total += part.Length;
                    parts[i] = part;
                }

                return total > MaxMadeChars ? null : string.Concat(parts);
            case TextOp.ObjectName:
                return functions.ObjectName(Target);
            case TextOp.ObjectDescription:
                return functions.ObjectDescription(Target, Flag1, Flag2, Text!);
            case TextOp.StatName:
                return functions.StatName(Number, Flag1);
            case TextOp.NurseProcedureName:
                return functions.NurseProcedureName(Number);
            case TextOp.NurseCardDescription:
                return functions.NurseCardDescription(Number < 0 ? null : Number, Target, Injury < 0 ? null : Injury, Games);
            case TextOp.TeamName:
                return (functions as IToastTextFunctions)?.TeamName((ToastTeam)Number);
            case TextOp.AssetName:
                return (functions as IToastTextFunctions)?.AssetName(AssetKind, Text!, Number);
            case TextOp.InjuryName:
                return (functions as IToastTextFunctions)?.InjuryName(Number);
            case TextOp.LocalizedStatName:
                return (functions as IToastTextFunctions)?.LocalizedStatName(Number);
            default:
                return null;
        }
    }
}
