using System.Collections.Generic;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Tests.Snapshots;

/// <summary>Snapshot helpers for tests.</summary>
internal static class TestFrames
{
    /// <summary>A copy of <paramref name="frame"/> that shares its (immutable) state arrays, to change into the next one.</summary>
    public static SnapshotFrame Copy(SnapshotFrame frame)
    {
        var copy = new SnapshotFrame { Sequence = frame.Sequence, HostTimeMicros = frame.HostTimeMicros, TimeScale = frame.TimeScale, Camera = frame.Camera };
        copy.GroupScales.AddRange(frame.GroupScales);
        foreach (KeyValuePair<ushort, byte[]> pair in frame.Objects)
        {
            copy.Objects.Add(pair.Key, pair.Value);
        }

        return copy;
    }
}
