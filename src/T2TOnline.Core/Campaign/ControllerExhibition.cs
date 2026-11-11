using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The rules page of Play Now's controller screen as menu data: each of its six list selectors'
/// choice, in the game's order (<c>BaseGameSettings</c>: periods, period length, skaters, difficulty, power-ups, injuries), as
/// one short text value. A friend's list selectors are made the same way from the same data, so the host's choice is the
/// friend's.
/// </summary>
public static class ControllerRules
{
    /// <summary>How many rules the page has.</summary>
    public const int Count = 6;

    /// <summary>The highest choice read (the game's lists have a handful of items each).</summary>
    public const int MaxChoice = 63;

    /// <summary>The rules' words, for the log, in order.</summary>
    private static readonly string[] Names = { "periods", "minutes", "skaters", "difficulty", "power-ups", "injuries" };

    /// <summary>The value for <paramref name="choices"/>: "1,0,2,1,1,0". Empty when they aren't the page's (its panel isn't loaded yet).</summary>
    public static string Format(IReadOnlyList<int> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count != Count)
        {
            return string.Empty;
        }

        foreach (int choice in choices)
        {
            if (choice is < 0 or > MaxChoice)
            {
                return string.Empty;
            }
        }

        return string.Join(",", choices);
    }

    /// <summary>Reads <see cref="Format"/>'s value. False for anything else. Never throws.</summary>
    public static bool TryParse(string? text, out IReadOnlyList<int> choices)
    {
        choices = Array.Empty<int>();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string[] parts = text.Split(',');
        if (parts.Length != Count)
        {
            return false;
        }

        var read = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out read[i]) || read[i] > MaxChoice)
            {
                return false;
            }
        }

        choices = read;
        return true;
    }

    /// <summary>The choices in words, for the log: "periods 1, minutes 0, skaters 2, difficulty 1, power-ups 1, injuries 0".</summary>
    public static string Describe(IReadOnlyList<int> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count != Count)
        {
            return "not known";
        }

        var parts = new string[Count];
        for (int i = 0; i < Count; i++)
        {
            parts[i] = string.Create(CultureInfo.InvariantCulture, $"{Names[i]} {choices[i]}");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>What a friend's rules page has to do to be as the host's (<see cref="ControllerExhibitionPlan.ForRules"/>).</summary>
public enum ControllerRulesStep
{
    /// <summary>Nothing: as the host's.</summary>
    None,

    /// <summary>The host's is up and this PC's panel isn't loaded yet: looked at again later.</summary>
    Wait,

    /// <summary>The game's own show of the page.</summary>
    Show,

    /// <summary>The game's own hide of the page.</summary>
    Hide,
}

/// <summary>What a side's team panel on a friend's screen has to do to be as the host's (<see cref="ControllerExhibitionPlan.ForTeam"/>).</summary>
[Flags]
public enum ControllerTeamChange
{
    /// <summary>Nothing: as the host's.</summary>
    None = 0,

    /// <summary>The panel shows another team, or the other colours: its own refresh, with the host's team.</summary>
    Team = 1,

    /// <summary>The tab's name changes (its own refresh of the active tab).</summary>
    Tab = 2,
}

/// <summary>One side's team panel on a friend's screen as it shows now.</summary>
/// <param name="Id">The team it shows (its <c>TeamData.id</c>; empty: none).</param>
/// <param name="Tab">The tab it names (<c>TeamSource</c>; -1: none).</param>
/// <param name="ColorHome">It wears the team's home colours.</param>
public readonly record struct ControllerTeamShown(string Id, int Tab, bool ColorHome);

/// <summary>
/// What the exhibition's screen adds to the campaign's on a friend: its two teams and its rules
/// page. Pure decisions; the game glue makes the game's own calls.
/// </summary>
public static class ControllerExhibitionPlan
{
    /// <summary>
    /// What a side's panel showing <paramref name="shown"/> does to show the host's <paramref name="want"/>. A side the host's
    /// data names no team for is left alone (nothing it shows is known to be wrong).
    /// </summary>
    public static ControllerTeamChange ForTeam(ControllerTeamShown shown, ControllerTeam want)
    {
        if (!want.Known)
        {
            return ControllerTeamChange.None;
        }

        ControllerTeamChange change = ControllerTeamChange.None;
        if (!string.Equals(shown.Id, want.Id, StringComparison.Ordinal) || shown.ColorHome != want.ColorHome)
        {
            change |= ControllerTeamChange.Team;
        }

        if (shown.Tab != want.Tab)
        {
            change |= ControllerTeamChange.Tab;
        }

        return change;
    }

    /// <summary>
    /// What this PC's rules page does: <paramref name="hostIn"/>, the host's screen is on it; <paramref name="hereIn"/>, this
    /// PC's is; <paramref name="panelLoaded"/>, its panel (loaded in the background) is here.
    /// </summary>
    public static ControllerRulesStep ForRules(bool hostIn, bool hereIn, bool panelLoaded)
    {
        if (hostIn == hereIn)
        {
            return ControllerRulesStep.None;
        }

        return !hostIn ? ControllerRulesStep.Hide : panelLoaded ? ControllerRulesStep.Show : ControllerRulesStep.Wait;
    }

    /// <summary>
    /// The list selectors to set: for each rule, the host's choice when this PC's list has it and shows another. A list with
    /// fewer choices here (the host's players' count is on its way) is left for the next look.
    /// </summary>
    public static IReadOnlyList<(int Rule, int Choice)> RuleChanges(IReadOnlyList<int> host, IReadOnlyList<int> shown, IReadOnlyList<int> counts)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(counts);
        var changes = new List<(int, int)>();
        for (int i = 0; i < host.Count && i < shown.Count && i < counts.Count; i++)
        {
            if (host[i] != shown[i] && host[i] < counts[i])
            {
                changes.Add((i, host[i]));
            }
        }

        return changes;
    }
}

/// <summary>
/// The custom content the teams on the host's exhibition controller screen need (<see cref="MenuAttachmentKind.ControllerContent"/>):
/// the references a match setup would carry, sent early, so a friend's real screen shows a custom team of the host's as the
/// game shows it (built with the game's own code once its pieces are here, as a match does). The pieces themselves travel the
/// usual way (<see cref="CustomContentSender"/>, each once a session). Checked when it's written and when it's read; immutable.
/// </summary>
public sealed class ControllerContentData
{
    private ControllerContentData(IReadOnlyList<CustomRef> refs) => Refs = refs;

    /// <summary>What the two teams on the screen need, at most <see cref="CustomContentValues.MaxRefs"/>.</summary>
    public IReadOnlyList<CustomRef> Refs { get; }

    /// <summary>Data for <paramref name="refs"/>. Throws for too many.</summary>
    public static ControllerContentData Of(IReadOnlyList<CustomRef> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);
        if (refs.Count > CustomContentValues.MaxRefs)
        {
            throw new ArgumentException($"At most {CustomContentValues.MaxRefs} references.", nameof(refs));
        }

        return new ControllerContentData(new List<CustomRef>(refs));
    }

    /// <summary>For logs: "2 piece(s) of custom content".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Refs.Count} piece(s) of custom content");

    /// <summary>The body: a count, then each reference's <see cref="CustomRef.Format"/>. Throws for a reference too long to carry.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte((byte)Refs.Count);
        foreach (CustomRef found in Refs)
        {
            writer.WriteShortString(found.Format());
        }
    }

    /// <summary>Reads <see cref="Write"/>'s body. False for a count out of range or a reference that doesn't read. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ControllerContentData? data)
    {
        data = null;
        int count = reader.ReadByte();
        if (reader.IsOverrun || count > CustomContentValues.MaxRefs)
        {
            return false;
        }

        var refs = new List<CustomRef>(count);
        for (int i = 0; i < count; i++)
        {
            string text = reader.ReadShortString();
            if (reader.IsOverrun || !CustomRef.TryParse(text, out CustomRef found))
            {
                return false;
            }

            refs.Add(found);
        }

        data = new ControllerContentData(refs);
        return true;
    }
}
