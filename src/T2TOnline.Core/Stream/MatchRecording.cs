using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Stream;

/// <summary>One snapshot record of a stream file, still encoded (decoded again when played).</summary>
/// <param name="HostTimeMicros">Its time on the stream clock.</param>
/// <param name="Message">The whole message, header included.</param>
public readonly record struct RecordedSnapshot(long HostTimeMicros, byte[] Message);

/// <summary>
/// A whole <c>.t2tstream</c>, read for playback: the snapshots (checked by decoding them once, then kept encoded, so an
/// hour-long recording costs tens of megabytes, not gigabytes), the object and event tables, the match setup when the
/// recording has one, and every other message by time. Malformed records are counted and skipped, never thrown.
/// </summary>
public sealed class MatchRecording
{
    private MatchRecording(MatchStreamHeader header) => Header = header;

    /// <summary>The file's header.</summary>
    public MatchStreamHeader Header { get; }

    /// <summary>Snapshots that decoded, in file order (which is time order: the recorder writes them as it takes them).</summary>
    public List<RecordedSnapshot> Snapshots { get; } = new();

    /// <summary>The object table by network id.</summary>
    public Dictionary<ushort, ObjectEntry> Objects { get; } = new();

    /// <summary>The event table by id.</summary>
    public Dictionary<ushort, EventEntry> Events { get; } = new();

    /// <summary>The match setup the recording started with (empty for recordings made before v0.4).</summary>
    public List<KeyValuePair<string, NetValue>> Setup { get; } = new();

    /// <summary>Everything else, in time order.</summary>
    public List<TimedMessage> Messages { get; } = new();

    /// <summary>Records that didn't decode.</summary>
    public int Malformed { get; private set; }

    /// <summary>Snapshots whose delta base was missing.</summary>
    public int MissingBase { get; private set; }

    /// <summary>
    /// True when the file was cut off (the recording didn't end cleanly), or couldn't be read to the end: what came before
    /// is kept.
    /// </summary>
    public bool Truncated { get; private set; }

    /// <summary>Recording length in seconds, from the first to the last snapshot.</summary>
    public double Seconds => Snapshots.Count < 2 ? 0 : (Snapshots[^1].HostTimeMicros - Snapshots[0].HostTimeMicros) / 1_000_000.0;

    /// <summary>True when every object has its component ids, so the recording can play onto a freshly loaded match.</summary>
    public bool HasComponentIds => Objects.Count > 0 && Objects.Values.All(o => o.Components is { Count: > 0 });

    /// <summary>Reads and checks a stream file. False, with the reason (and no recording), for anything unusable. Never throws on a bad file.</summary>
    public static bool TryLoad(string path, [NotNullWhen(true)] out MatchRecording? stream, out string problem)
    {
        stream = null;
        System.IO.Stream file;
        try
        {
            file = File.OpenRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The message names the file by its full path: only the folder's label and the file name go on to a log.
            problem = SafeError.Text(ex);
            return false;
        }

        return TryRead(file, out stream, out problem);
    }

    /// <summary>Reads and checks a stream (disposed when done). False, with the reason (and no recording), for anything unusable.</summary>
    public static bool TryRead(System.IO.Stream input, [NotNullWhen(true)] out MatchRecording? stream, out string problem)
    {
        ArgumentNullException.ThrowIfNull(input);
        stream = null;
        if (!MatchStreamReader.TryOpen(input, out MatchStreamReader? reader, out problem))
        {
            input.Dispose();
            return false;
        }

        using (reader)
        {
            var loaded = new MatchRecording(reader!.Header);
            var history = new SnapshotHistory(128);
            var objects = new List<ObjectEntry>();
            var events = new List<EventEntry>();
            long lastSnapshot = long.MinValue;
            bool outOfOrder = false;
            while (reader.TryRead(out MatchStreamRecord record))
            {
                var bits = new BitReader(record.Message);
                if (!MessageHeader.TryRead(ref bits, out MessageHeader header))
                {
                    loaded.Malformed++;
                    continue;
                }

                switch (header.Type)
                {
                    case MessageType.Snapshot:
                        if (SnapshotCodec.TryRead(ref bits, history, out SnapshotFrame frame, out bool missingBase))
                        {
                            history.Add(frame);
                            loaded.Snapshots.Add(new RecordedSnapshot(frame.HostTimeMicros, record.Message));
                            outOfOrder |= frame.HostTimeMicros < lastSnapshot;
                            lastSnapshot = Math.Max(lastSnapshot, frame.HostTimeMicros);
                        }
                        else if (missingBase)
                        {
                            loaded.MissingBase++;
                        }
                        else
                        {
                            loaded.Malformed++;
                        }

                        break;
                    case MessageType.ObjectTable:
                        objects.Clear();
                        if (ObjectTableMessage.TryRead(ref bits, objects))
                        {
                            foreach (ObjectEntry entry in objects)
                            {
                                // An object announced again (its component list changed during play) keeps its first entry:
                                // a recording's objects are mapped once, as they joined.
                                loaded.Objects.TryAdd(entry.NetId, entry);
                            }
                        }
                        else
                        {
                            loaded.Malformed++;
                        }

                        break;
                    case MessageType.EventTable:
                        events.Clear();
                        if (EventTableMessage.TryRead(ref bits, events))
                        {
                            foreach (EventEntry entry in events)
                            {
                                loaded.Events[entry.EventId] = entry;
                            }
                        }
                        else
                        {
                            loaded.Malformed++;
                        }

                        break;
                    case MessageType.MatchSetup:
                        loaded.Setup.Clear();
                        if (!MatchSetupMessage.TryRead(ref bits, out _, loaded.Setup))
                        {
                            loaded.Setup.Clear();
                            loaded.Malformed++;
                        }

                        break;
                    default:
                        int offset = (bits.BitPosition + 7) / 8;
                        loaded.Messages.Add(new TimedMessage(record.TimeMicros, header.Type, record.Message.AsSpan(offset).ToArray()));
                        break;
                }
            }

            loaded.Truncated = reader.Truncated;
            // Stable sort: messages written at the same instant keep their order (a HUD value before the event it explains).
            List<TimedMessage> sorted = loaded.Messages.OrderBy(m => m.TimeMicros).ToList();
            loaded.Messages.Clear();
            loaded.Messages.AddRange(sorted);
            if (outOfOrder)
            {
                return Fail(out problem, "its snapshots aren't in time order");
            }

            if (loaded.Snapshots.Count < 2)
            {
                return Fail(out problem, "fewer than two snapshots");
            }

            stream = loaded;
            problem = string.Empty;
            return true;
        }
    }

    private static bool Fail(out string problem, string why)
    {
        problem = why;
        return false;
    }
}
