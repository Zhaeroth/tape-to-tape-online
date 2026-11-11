using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// The look of the host's campaign run team that its run file doesn't keep: the
/// game makes a run's team from its squad's team, or from the player's custom campaign team, at each run load
/// (<c>CampaignState.InstantiateStartingTeam</c>), and the run file then sets only the name, logo, player colours and home
/// kits. The away kit, the away goalie's kit, the three transition colours and the goalie's own colours are whatever the
/// loading PC's squad or custom team says: on a friend, not the host's. Sent as a <see cref="CustomContentKind.RunLook"/>
/// item; a friend puts it on its copy of the host's run after every load. Each change of look the host sends has a new
/// <see cref="Serial"/>, so a look that comes back to an earlier one is still a new item (custom content goes to a friend
/// once per item: without it, a friend kept wearing the look in between). Checked when it's written and when it's read;
/// immutable.
/// <code>
/// u8 version (1) · varuint serial · optional schemes away · optional schemes away goalie · colour ×3 transitions ·
/// optional schemes goalie
/// schemes: 8 × (bool · colour ×3); colour: 4 × f32
/// </code>
/// </summary>
public sealed class RunLookData
{
    /// <summary>The item's key (one per run team).</summary>
    public const string Key = "run-look";

    /// <summary>The layout's version.</summary>
    public const byte Version = 1;

    /// <summary>Largest encoding (three teams' colours at 8 schemes, the transitions: well under a KB).</summary>
    public const int MaxBytes = 2048;

    /// <summary>
    /// The host's number for this look, new for each change it sends (not part of the look: <see cref="FirstDifference"/>
    /// leaves it out). Two sends of the same colours are two items, so the newest always reaches friends.
    /// </summary>
    public uint Serial { get; init; }

    /// <summary>The team's away kit (the game's <c>TeamData.awayColors</c>; null: none).</summary>
    public IReadOnlyList<ColorSchemeData?>? Away { get; init; }

    /// <summary>The team's away goalie kit (<c>awayGoalieColors</c>; null: none).</summary>
    public IReadOnlyList<ColorSchemeData?>? AwayGoalie { get; init; }

    /// <summary>The first transition colour (<c>primaryColorTransition</c>).</summary>
    public ColorValue PrimaryTransition { get; init; }

    /// <summary>The second transition colour (<c>secondaryColorTransition</c>).</summary>
    public ColorValue SecondaryTransition { get; init; }

    /// <summary>The third transition colour (<c>tertiaryColorTransition</c>).</summary>
    public ColorValue TertiaryTransition { get; init; }

    /// <summary>The team's goalie's own colours (<c>GoaltenderData.colorSchemes</c>; null: none, or no goalie).</summary>
    public IReadOnlyList<ColorSchemeData?>? Goalie { get; init; }

    /// <summary>Why the look can't be sent, or null when it can.</summary>
    public string? Problem()
    {
        if (!ColorWire.IsValid(Away) || !ColorWire.IsValid(AwayGoalie) || !ColorWire.IsValid(Goalie))
        {
            return "a kit isn't eight schemes of valid colours";
        }

        return PrimaryTransition.IsValid && SecondaryTransition.IsValid && TertiaryTransition.IsValid ? null : "a transition colour is out of range";
    }

    /// <summary>The look's bytes (an item's). Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public byte[] ToBytes()
    {
        string? problem = Problem();
        if (problem != null)
        {
            throw new ArgumentException(problem);
        }

        var writer = new BitWriter(512);
        writer.WriteByte(Version);
        writer.WriteVarUInt(Serial);
        ColorWire.OptionalSchemes(writer, Away);
        ColorWire.OptionalSchemes(writer, AwayGoalie);
        ColorWire.Color(writer, PrimaryTransition);
        ColorWire.Color(writer, SecondaryTransition);
        ColorWire.Color(writer, TertiaryTransition);
        ColorWire.OptionalSchemes(writer, Goalie);
        return writer.ToArray();
    }

    /// <summary>Reads a look from an item's bytes. False for another version, anything out of range, cut short or followed by more. Never throws.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out RunLookData? look)
    {
        look = null;
        if (bytes.Length is 0 or > MaxBytes)
        {
            return false;
        }

        var reader = new BitReader(bytes);
        if (reader.ReadByte() != Version)
        {
            return false;
        }

        ulong serial = reader.ReadVarUInt();
        if (reader.IsOverrun || serial > uint.MaxValue || !ColorWire.TryReadOptionalSchemes(ref reader, out ColorSchemeData?[]? away) ||
            !ColorWire.TryReadOptionalSchemes(ref reader, out ColorSchemeData?[]? awayGoalie))
        {
            return false;
        }

        ColorValue first = ColorWire.ReadColor(ref reader), second = ColorWire.ReadColor(ref reader), third = ColorWire.ReadColor(ref reader);
        if (reader.IsOverrun || !ColorWire.TryReadOptionalSchemes(ref reader, out ColorSchemeData?[]? goalie) || reader.RemainingBits >= 8)
        {
            return false;
        }

        var read = new RunLookData
        {
            Serial = (uint)serial,
            Away = away,
            AwayGoalie = awayGoalie,
            PrimaryTransition = first,
            SecondaryTransition = second,
            TertiaryTransition = third,
            Goalie = goalie,
        };
        if (read.Problem() != null)
        {
            return false;
        }

        look = read;
        return true;
    }

    /// <summary>The look as a custom content item for friends.</summary>
    public CustomItem ToItem() => new(CustomContentKind.RunLook, Key, ToBytes());

    /// <summary>The same look numbered <paramref name="serial"/> (what the host sends for each change).</summary>
    public RunLookData WithSerial(uint serial) => new()
    {
        Serial = serial,
        Away = Away,
        AwayGoalie = AwayGoalie,
        PrimaryTransition = PrimaryTransition,
        SecondaryTransition = SecondaryTransition,
        TertiaryTransition = TertiaryTransition,
        Goalie = Goalie,
    };

    /// <summary>
    /// The first part of <paramref name="here"/> that isn't this look, in words ("away kit' jerseyScheme (primary) (...)"), or
    /// null when they're the same (whatever their <see cref="Serial"/>s).
    /// </summary>
    public string? FirstDifference(RunLookData here)
    {
        ArgumentNullException.ThrowIfNull(here);
        return SkaterModelDiff.SchemesDiffer("away kit", Away, here.Away) ?? SkaterModelDiff.SchemesDiffer("away goalie kit", AwayGoalie, here.AwayGoalie) ??
            Transition("first transition colour", PrimaryTransition, here.PrimaryTransition) ??
            Transition("second transition colour", SecondaryTransition, here.SecondaryTransition) ??
            Transition("third transition colour", TertiaryTransition, here.TertiaryTransition) ?? SkaterModelDiff.SchemesDiffer("goalie's colours", Goalie, here.Goalie);
    }

    /// <summary>For logs: "away kit, away goalie kit, 3 transition colours, goalie colours".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Away != null)
        {
            parts.Add("away kit");
        }

        if (AwayGoalie != null)
        {
            parts.Add("away goalie kit");
        }

        parts.Add("3 transition colours");
        if (Goalie != null)
        {
            parts.Add("goalie colours");
        }

        return string.Join(", ", parts);
    }

    private static string? Transition(string what, ColorValue host, ColorValue here) =>
        host.Equals(here) ? null : $"{what} ({Show(here)} here, {Show(host)} on the host's)";

    private static string Show(ColorValue color) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{color.R:0.###}/{color.G:0.###}/{color.B:0.###}/{color.A:0.###}");
}
