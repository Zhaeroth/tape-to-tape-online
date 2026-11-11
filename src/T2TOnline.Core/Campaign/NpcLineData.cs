using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>Where an event NPC's line came from in its dialogue pool (the game's <c>NpcDialoguePool</c>). Values are part of the protocol: only add.</summary>
public enum NpcLineSource : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>The pool's first-visit line (<c>FirstVisitDialogue</c>).</summary>
    FirstVisit = 1,

    /// <summary>One of the pool's per-run visit lines (<c>VisitDialogues[index]</c>).</summary>
    Visit = 2,

    /// <summary>The pool's line for an event with no choices (<c>NoChoicesDialogue</c>).</summary>
    NoChoices = 3,

    /// <summary>One of the pool's random lines (<c>Dialogues[index].Dialogue</c>).</summary>
    PoolEntry = 4,

    /// <summary>A line from elsewhere (a training milestone, the tutorial): found by its sequence's name.</summary>
    Other = 5,
}

/// <summary>
/// A line an event's NPC spoke on the host's screen (the game's <c>SpeechSequence</c> its <c>SpeechBubbleController.Show</c>
/// played), sent as a <see cref="MenuAttachmentKind.NpcLine"/> attachment of the event menu, format
/// <see cref="MenuAttachment.NpcLineFormat"/>: which line of the NPC's dialogue pool it was, by source and index, with the
/// pool's and the sequence's asset names to check it by. A friend's game plays the same asset itself, so the line shows in
/// that player's own language. A newer line (a higher <see cref="Number"/>) replaces the one before. Checked when it's
/// written and when it's read; immutable.
/// <code>
/// u16 number · u8 source · varuint index · optional name pool · name sequence · u8 anchor side + 1 (0: none)
/// </code>
/// </summary>
public sealed class NpcLineData
{
    /// <summary>Highest index in a pool's list of lines.</summary>
    public const int MaxIndex = 1023;

    /// <summary>Highest anchor side (the game's <c>AnchorSide</c> number: left, right, center, and room for more).</summary>
    public const int MaxAnchorSide = 14;

    /// <summary>The line's number in its menu, from 1 (each line the host's NPC speaks there one more).</summary>
    public int Number { get; init; } = 1;

    /// <summary>Where it came from in the pool.</summary>
    public NpcLineSource Source { get; init; }

    /// <summary>Its index in the pool's list (<see cref="NpcLineSource.Visit"/> and <see cref="NpcLineSource.PoolEntry"/>), else 0.</summary>
    public int Index { get; init; }

    /// <summary>The dialogue pool's asset name, or null when the NPC had none.</summary>
    public string? Pool { get; init; }

    /// <summary>The speech sequence's asset name.</summary>
    public string Sequence { get; init; } = string.Empty;

    /// <summary>The side of the anchor the bubble showed on (the game's <c>Nullable&lt;AnchorSide&gt;</c>), or null for the sequence's own.</summary>
    public int? AnchorSide { get; init; }

    /// <summary>For logs: "line 2: "Visit_03" (visit 3 of pool "ChaosDialoguePool")"; the asset names by <see cref="Diagnostics.HostText.KeyForLog(string?)"/>.</summary>
    public override string ToString()
    {
        string where = Source switch
        {
            NpcLineSource.FirstVisit => "first visit",
            NpcLineSource.Visit => $"visit {Index + 1}",
            NpcLineSource.NoChoices => "no choices",
            NpcLineSource.PoolEntry => $"pool entry {Index + 1}",
            _ => "found by name",
        };
        string pool = Pool == null ? string.Empty : $" of pool \"{Diagnostics.HostText.KeyForLog(Pool)}\"";
        return $"line {Number}: \"{Diagnostics.HostText.KeyForLog(Sequence)}\" ({where}{pool})";
    }

    /// <summary>Why the line can't be sent, or null when it can.</summary>
    public string? Problem() => Check();

    /// <summary>Writes the line. Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteUInt16((ushort)Number);
        writer.WriteByte((byte)Source);
        writer.WriteVarUInt((ulong)Index);
        EventWire.OptionalName(writer, Pool);
        EventWire.Name(writer, Sequence);
        writer.WriteByte((byte)(AnchorSide is int side ? side + 1 : 0));
    }

    /// <summary>Reads a line. False for anything malformed or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out NpcLineData? line)
    {
        line = null;
        int number = reader.ReadUInt16();
        var source = (NpcLineSource)reader.ReadByte();
        bool ok = true;
        int index = EventWire.Count(ref reader, MaxIndex, ref ok);
        if (!ok || !EventWire.TryReadOptionalName(ref reader, out string? pool))
        {
            return false;
        }

        string? sequence = EventWire.ReadName(ref reader);
        int anchor = reader.ReadByte();
        if (reader.IsOverrun || sequence == null)
        {
            return false;
        }

        var read = new NpcLineData { Number = number, Source = source, Index = index, Pool = pool, Sequence = sequence, AnchorSide = anchor == 0 ? null : anchor - 1 };
        if (read.Check() != null)
        {
            return false;
        }

        line = read;
        return true;
    }

    private string? Check()
    {
        if (Number is < 1 or > ushort.MaxValue)
        {
            return $"line number {Number} is out of range";
        }

        if (Source is < NpcLineSource.FirstVisit or > NpcLineSource.Other)
        {
            return $"source {Source} is no source";
        }

        if (Index is < 0 or > MaxIndex || (Index != 0 && Source is not (NpcLineSource.Visit or NpcLineSource.PoolEntry)))
        {
            return $"index {Index} is out of range for a {Source} line";
        }

        if (AnchorSide is < 0 or > MaxAnchorSide)
        {
            return $"anchor side {AnchorSide} is out of range";
        }

        return EventWire.CheckName(Pool, "the pool's name", required: false) ?? EventWire.CheckName(Sequence, "the sequence's name", required: true);
    }
}
