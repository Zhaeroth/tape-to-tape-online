using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// One page of the host's run history as its title screen shows it (the game's <c>RunEndMenu</c> in history mode): which
/// page of how many, the host's UTC offset at the run's time, and the run (<see cref="RunHistoryData"/>), sent as a
/// <see cref="MenuAttachmentKind.HistoryPage"/> attachment of the menu (format <see cref="MenuAttachment.HistoryPageFormat"/>),
/// again for each page the host moves to. A friend's game shows the same page of the same run, with the date and time the
/// host's screen shows (<see cref="LocalTicks"/>). Checked when it's written and when it's read; immutable.
/// <code>
/// u16 index · u16 count · i16 host UTC offset, minutes · the run history entry's fields
/// </code>
/// </summary>
public sealed record HistoryPageData
{
    /// <summary>Most runs in the host's history list.</summary>
    public const int MaxCount = 1000;

    /// <summary>Largest UTC offset either way, in minutes (UTC+14:00 and UTC-14:00 are the extremes in use).</summary>
    public const int MaxOffsetMinutes = 14 * 60;

    /// <summary>The page shown: its place in the host's list (0 to <see cref="Count"/> - 1).</summary>
    public int Index { get; init; }

    /// <summary>How many runs the host's list has (1 to <see cref="MaxCount"/>): the page arrows show from it.</summary>
    public int Count { get; init; }

    /// <summary>
    /// The host's UTC offset at the run's time, in minutes (its game shows the run's UTC time in its own local time), from
    /// -<see cref="MaxOffsetMinutes"/> to <see cref="MaxOffsetMinutes"/>.
    /// </summary>
    public int HostUtcOffsetMinutes { get; init; }

    /// <summary>The run on the page.</summary>
    public RunHistoryData? Entry { get; init; }

    /// <summary>
    /// For logs: "page 3 of 12 (run 9f0c...)". The host's UTC offset is left out (D127): it travels only for the friend's
    /// screen, which shows the run's time as the host's does.
    /// </summary>
    public override string ToString() => $"page {Index + 1} of {Count} (run {Diagnostics.HostText.KeyForLog(Entry?.RunId)})";

    /// <summary>Why the page can't be sent, or null when it can.</summary>
    public string? Problem()
    {
        if (Count is < 1 or > MaxCount)
        {
            return $"{Count} runs in the list (1 to {MaxCount})";
        }

        if (Index < 0 || Index >= Count)
        {
            return $"page {Index} isn't in a list of {Count}";
        }

        if (HostUtcOffsetMinutes is < -MaxOffsetMinutes or > MaxOffsetMinutes)
        {
            return $"a UTC offset past {MaxOffsetMinutes / 60} hours either way";
        }

        if (Entry == null)
        {
            return "the run is missing";
        }

        if (Entry.TimestampUtc < 0 || Entry.TimestampUtc > DateTime.MaxValue.Ticks)
        {
            return "the run's time isn't a date";
        }

        return null;
    }

    /// <summary>Writes the page. Throws <see cref="ArgumentException"/> (saying what) for anything out of range, the run's own limits included.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Problem();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteUInt16((ushort)Index);
        writer.WriteUInt16((ushort)Count);
        writer.WriteInt16((short)HostUtcOffsetMinutes);
        Entry!.Write(writer);
    }

    /// <summary>Reads a page. False for anything malformed or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out HistoryPageData? page)
    {
        page = null;
        int index = reader.ReadUInt16();
        int count = reader.ReadUInt16();
        int offset = reader.ReadInt16();
        if (reader.IsOverrun || !RunHistoryData.TryRead(ref reader, out RunHistoryData? entry))
        {
            return false;
        }

        var read = new HistoryPageData { Index = index, Count = count, HostUtcOffsetMinutes = offset, Entry = entry };
        if (reader.IsOverrun || read.Problem() != null)
        {
            return false;
        }

        page = read;
        return true;
    }

    /// <summary>
    /// The UTC time (in <see cref="DateTime"/> ticks) to give this PC's game so that its own local time shows the host's
    /// wall-clock time of <paramref name="utcTicks"/>: the game shows a run's time as <c>ToLocalTime</c> of its UTC ticks, so
    /// the ticks move by the host's offset less this PC's at the moved instant (<paramref name="localOffset"/>, this PC's
    /// UTC offset at a UTC time; asked twice, so a daylight-saving change between the two instants lands right). Kept
    /// within <see cref="DateTime"/>'s range. Never throws for ticks in that range.
    /// </summary>
    public static long LocalTicks(long utcTicks, int hostOffsetMinutes, Func<DateTime, TimeSpan> localOffset)
    {
        ArgumentNullException.ThrowIfNull(localOffset);
        long ticks = Math.Clamp(utcTicks, 0, DateTime.MaxValue.Ticks);
        long wall = Clamp(ticks + (hostOffsetMinutes * TimeSpan.TicksPerMinute));
        long moved = Clamp(wall - localOffset(new DateTime(ticks, DateTimeKind.Utc)).Ticks);
        return Clamp(wall - localOffset(new DateTime(moved, DateTimeKind.Utc)).Ticks);
    }

    private static long Clamp(long ticks) => Math.Clamp(ticks, 0, DateTime.MaxValue.Ticks);
}
