using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// A <c>.t2tstream</c>'s match setup without reading the whole file: the recorder writes the setup as its first record
/// (<c>StreamRecorder.Start</c>), so a look at the first few records says what match a recording is (a campaign match's,
/// an exhibition's) for a fraction of a full load. Never throws on a bad file.
/// </summary>
public static class RecordingPeek
{
    /// <summary>How many records are read at most before giving up on finding the setup.</summary>
    public const int MaxRecords = 16;

    /// <summary>
    /// Reads <paramref name="input"/> (disposed when done) up to its match setup, within <paramref name="maxRecords"/>
    /// records, into <paramref name="setup"/> (cleared first). False, with <paramref name="problem"/> saying why, for a file
    /// that isn't a stream, ends first, or has no setup that early (a recording made before v0.4).
    /// </summary>
    public static bool TryReadSetup(System.IO.Stream input, List<KeyValuePair<string, NetValue>> setup, out string problem, int maxRecords = MaxRecords)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(setup);
        setup.Clear();
        if (!MatchStreamReader.TryOpen(input, out MatchStreamReader? reader, out problem))
        {
            input.Dispose();
            return false;
        }

        using (reader)
        {
            for (int read = 0; read < Math.Max(maxRecords, 1); read++)
            {
                if (!reader.TryRead(out MatchStreamRecord record))
                {
                    problem = reader.Truncated ? "the file ends before its match setup" : "no match setup in the file";
                    return false;
                }

                var bits = new BitReader(record.Message);
                if (!MessageHeader.TryRead(ref bits, out MessageHeader header) || header.Type != MessageType.MatchSetup)
                {
                    continue;
                }

                if (MatchSetupMessage.TryRead(ref bits, out _, setup) && setup.Count > 0)
                {
                    problem = string.Empty;
                    return true;
                }

                setup.Clear();
                problem = "its match setup doesn't read";
                return false;
            }
        }

        problem = $"no match setup in its first {Math.Max(maxRecords, 1)} records";
        return false;
    }
}
