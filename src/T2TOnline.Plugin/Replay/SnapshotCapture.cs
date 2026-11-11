using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace T2TOnline.Plugin.Replay;

/// <summary>
/// Writes replay snapshots to a file for offline work on client mode, and keeps them in memory so they can be played
/// back onto the same match (<c>FrozenReplay</c>). The file holds game-derived data, so it stays on the
/// developer's PC (UserData), never in the repository.
/// </summary>
/// <remarks>
/// Format, little-endian: "T2TSNAP2" (version 1 wrote whole 512-byte buffers); then per snapshot: float time,
/// int sequence, then per object: int id (>= 0), int length, the state bytes; the snapshot ends with int -1 and int
/// object count. A sibling ".names.txt" maps ids to object names, for reading a capture offline.
/// </remarks>
internal sealed class SnapshotCapture : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly Dictionary<int, string> _names = new();
    private readonly List<CapturedSnapshot> _snapshots = new();
    private readonly List<CapturedObject> _current = new();
    private float _currentTime;
    private int _currentSequence;

    /// <summary>Opens <paramref name="path"/> for a capture that ends at <paramref name="endsAt"/> (probe clock seconds).</summary>
    public SnapshotCapture(string path, double endsAt)
    {
        Path = path;
        EndsAt = endsAt;
        _writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write), Encoding.UTF8, leaveOpen: false);
        _writer.Write(Encoding.ASCII.GetBytes("T2TSNAP2"));
    }

    /// <summary>The capture file.</summary>
    public string Path { get; }

    /// <summary>When the capture should stop, in probe clock seconds.</summary>
    public double EndsAt { get; }

    /// <summary>Snapshots written.</summary>
    public int Snapshots { get; private set; }

    /// <summary>State bytes written.</summary>
    public long Bytes { get; private set; }

    /// <summary>The captured snapshots, oldest first.</summary>
    public IReadOnlyList<CapturedSnapshot> Captured => _snapshots;

    /// <summary>Starts a snapshot.</summary>
    public void BeginSnapshot(float time, int sequence)
    {
        _writer.Write(time);
        _writer.Write(sequence);
        _currentTime = time;
        _currentSequence = sequence;
        _current.Clear();
    }

    /// <summary>
    /// Writes one object's state: the first <paramref name="size"/> bytes of <paramref name="buffer"/>.
    /// <c>ReplayState.ToArray()</c> returns the whole pooled buffer (512 bytes); only <c>Size</c> of it is the state.
    /// </summary>
    public void WriteObject(int id, byte[] buffer, int size, string name)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        size = Math.Clamp(size, 0, buffer.Length);
        _writer.Write(id);
        _writer.Write(size);
        _writer.Write(buffer, 0, size);
        _names.TryAdd(id, name);
        _current.Add(new CapturedObject(id, buffer.AsSpan(0, size).ToArray()));
        Bytes += size;
    }

    /// <summary>Ends a snapshot.</summary>
    public void EndSnapshot(int objectCount)
    {
        _writer.Write(-1);
        _writer.Write(objectCount);
        _snapshots.Add(new CapturedSnapshot(_currentTime, _currentSequence, _current.ToArray()));
        Snapshots++;
    }

    /// <summary>Closes the file and writes the names file next to it.</summary>
    public void Dispose()
    {
        _writer.Dispose();
        var names = new StringBuilder();
        foreach ((int id, string name) in _names)
        {
            names.Append(id).Append('\t').Append(name).Append('\n');
        }

        File.WriteAllText(System.IO.Path.ChangeExtension(Path, ".names.txt"), names.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>One captured snapshot.</summary>
/// <param name="Time">The recorder's timestamp, in seconds.</param>
/// <param name="Sequence">The recorder's sequence number.</param>
/// <param name="Objects">Each recorded object's state.</param>
internal sealed record CapturedSnapshot(float Time, int Sequence, CapturedObject[] Objects);

/// <summary>One object's state in a captured snapshot.</summary>
/// <param name="Id">The object's <c>ReplayIdentity</c> id.</param>
/// <param name="State">Its replay state bytes.</param>
internal sealed record CapturedObject(int Id, byte[] State);
