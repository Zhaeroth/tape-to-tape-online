using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Stream;

/// <summary>
/// The host's side of the looping gameplay sounds (<see cref="SoundLoopMessage"/>): what the recorder's polls of the game's
/// FMOD instances saw, turned into the stream's starts, moves and stops. A poll names the kinds it looked at
/// (<see cref="Polled"/>) and each instance of them it saw (<see cref="Seen"/>, by FMOD's own handle); then
/// <see cref="EndPoll"/> says what changed:
/// <list type="bullet">
/// <item>a new playing instance starts a loop under a new handle, at its position (at most <see cref="MaxLoops"/> at once;
/// more are refused, counted, and stay refused);</item>
/// <item>a playing loop that moved <see cref="MoveMetres"/> or more moves, at most every <see cref="MoveEveryMicros"/>;</item>
/// <item>every <see cref="RefreshEveryMicros"/> while it plays, its start goes again (a friend who started getting the
/// stream since, or whose playback started over, picks it up; for a friend already playing it, a start only moves it);</item>
/// <item>an instance that stops playing (stopping, stopped) or is gone stops its loop. A stopping one is remembered until
/// it's gone, so its fade-out isn't taken for a new loop.</item>
/// <item>an instance first seen not playing never was a loop of ours yet: a fading one stays so until it's gone; one the game
/// has only just started (the poll runs in that very frame, and FMOD's state follows its own update) starts its loop at the
/// first poll that sees it play.</item>
/// </list>
/// Stops come first in a poll's output, then starts, then moves. Game-independent; times in microseconds.
/// </summary>
public sealed class LoopTracker
{
    /// <summary>The most loops playing at once.</summary>
    public const int MaxLoops = 16;

    /// <summary>The least distance, in metres, a playing loop moves before a friend hears it move.</summary>
    public const float MoveMetres = 0.2f;

    /// <summary>The shortest time between two moves of one loop (30 a second at most).</summary>
    public const long MoveEveryMicros = 33_333;

    /// <summary>How often a playing loop's start goes again.</summary>
    public const long RefreshEveryMicros = 2_000_000;

    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly bool[] _polled = new bool[LoopKinds.Count];
    private readonly int[] _known = new int[LoopKinds.Count];
    private readonly int[] _started = new int[LoopKinds.Count];
    private readonly List<ulong> _gone = new();
    private readonly List<SoundLoopMessage> _starts = new();
    private readonly List<SoundLoopMessage> _moves = new();
    private uint _nextHandle = 1;
    private long _now;

    /// <summary>Loops playing now (started and not stopped).</summary>
    public int Playing { get; private set; }

    /// <summary>True while any instance is remembered (playing, refused or fading out): polls should look every time.</summary>
    public bool Any => _entries.Count > 0;

    /// <summary>Loops started since the last <see cref="Reset"/>.</summary>
    public int Started => _started.Sum();

    /// <summary>Loops that stopped (stopping, stopped or gone on the host).</summary>
    public int Stopped { get; private set; }

    /// <summary>Moves sent.</summary>
    public int Moves { get; private set; }

    /// <summary>Starts sent again for loops already playing.</summary>
    public int Refreshes { get; private set; }

    /// <summary>Instances refused for <see cref="MaxLoops"/> playing already.</summary>
    public int Refused { get; private set; }

    /// <summary>Instances of one kind remembered now (playing, refused or fading out).</summary>
    public int Known(LoopKind kind) => (byte)kind < LoopKinds.Count ? _known[(byte)kind] : 0;

    /// <summary>Starts a poll at <paramref name="nowMicros"/>.</summary>
    public void BeginPoll(long nowMicros)
    {
        _now = nowMicros;
        Array.Clear(_polled);
        foreach (Entry entry in _entries.Values)
        {
            entry.Seen = false;
        }
    }

    /// <summary>
    /// This poll looked at every instance of <paramref name="kind"/> (none may be there): any remembered one it didn't see
    /// is gone. A kind not polled is left as it was.
    /// </summary>
    public void Polled(LoopKind kind)
    {
        if ((byte)kind < LoopKinds.Count)
        {
            _polled[(byte)kind] = true;
        }
    }

    /// <summary>
    /// One instance of <paramref name="kind"/> this poll saw (<paramref name="instance"/>: FMOD's handle), whether it plays
    /// (playing, sustaining or starting), and where. Marks the kind polled. A position that isn't finite counts as 0, one
    /// far out is held to <see cref="SoundLoopMessage.MaxCoordinate"/>.
    /// </summary>
    public void Seen(ulong instance, LoopKind kind, bool playing, float x, float y, float z)
    {
        if ((byte)kind >= LoopKinds.Count)
        {
            return;
        }

        Polled(kind);
        if (!_entries.TryGetValue(instance, out Entry? entry))
        {
            entry = new Entry(kind);
            _entries[instance] = entry;
            _known[(byte)kind]++;
        }

        if (playing && entry.NeverPlayed)
        {
            entry.NeverPlayed = false;
            if (Playing >= MaxLoops)
            {
                Refused++;
            }
            else
            {
                entry.Handle = _nextHandle;
                entry.Live = entry.IsNew = true;
                _nextHandle = _nextHandle == uint.MaxValue ? 1 : _nextHandle + 1;
                Playing++;
            }
        }

        entry.Seen = true;
        entry.IsPlaying = playing;
        (entry.X, entry.Y, entry.Z) = (Sane(x), Sane(y), Sane(z));
    }

    /// <summary>What changed since the last poll, added to <paramref name="output"/>: stops, then starts, then moves.</summary>
    public void EndPoll(List<SoundLoopMessage> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _gone.Clear();
        _starts.Clear();
        _moves.Clear();
        foreach (KeyValuePair<ulong, Entry> pair in _entries)
        {
            Entry entry = pair.Value;
            if (!entry.Seen)
            {
                if (_polled[(byte)entry.Kind])
                {
                    // Gone (released and stopped): its loop stops if it still played.
                    StopIfLive(entry, output);
                    _gone.Add(pair.Key);
                }

                continue;
            }

            if (!entry.Live)
            {
                continue; // refused, not seen playing yet, or stopped: nothing to say
            }

            if (!entry.IsPlaying)
            {
                StopIfLive(entry, output);
                continue;
            }

            if (entry.IsNew || _now - entry.StartedAt >= RefreshEveryMicros)
            {
                Refreshes += entry.IsNew ? 0 : 1;
                _started[(byte)entry.Kind] += entry.IsNew ? 1 : 0;
                entry.IsNew = false;
                entry.StartedAt = entry.MovedAt = _now;
                (entry.SentX, entry.SentY, entry.SentZ) = (entry.X, entry.Y, entry.Z);
                _starts.Add(SoundLoopMessage.Start(entry.Handle, entry.Kind, entry.X, entry.Y, entry.Z));
            }
            else if (_now - entry.MovedAt >= MoveEveryMicros && Distance(entry) >= MoveMetres)
            {
                Moves++;
                entry.MovedAt = _now;
                (entry.SentX, entry.SentY, entry.SentZ) = (entry.X, entry.Y, entry.Z);
                _moves.Add(SoundLoopMessage.Move(entry.Handle, entry.X, entry.Y, entry.Z));
            }
        }

        foreach (ulong instance in _gone)
        {
            _known[(byte)_entries[instance].Kind]--;
            _entries.Remove(instance);
        }

        output.AddRange(_starts);
        output.AddRange(_moves);
    }

    /// <summary>Forgets everything (a new recording): handles start at 1 again.</summary>
    public void Reset()
    {
        _entries.Clear();
        Array.Clear(_known);
        Array.Clear(_started);
        _nextHandle = 1;
        Playing = Stopped = Moves = Refreshes = Refused = 0;
    }

    /// <summary>For the log: "magnet 3, retro rocket 1", or "none".</summary>
    public string DescribeStarted()
    {
        IEnumerable<string> kinds = Enumerable.Range(0, LoopKinds.Count).Where(k => _started[k] > 0).Select(k => $"{LoopKinds.Name((LoopKind)k)} {_started[k]}");
        return Started == 0 ? "none" : string.Join(", ", kinds);
    }

    private static float Sane(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, -SoundLoopMessage.MaxCoordinate, SoundLoopMessage.MaxCoordinate) : 0f;

    private static float Distance(Entry entry)
    {
        float dx = entry.X - entry.SentX, dy = entry.Y - entry.SentY, dz = entry.Z - entry.SentZ;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private void StopIfLive(Entry entry, List<SoundLoopMessage> output)
    {
        if (!entry.Live)
        {
            return;
        }

        entry.Live = false;
        Playing--;
        Stopped++;
        // A loop seen playing only this poll (and stopping before its start went) never reached a friend: nothing to stop.
        if (!entry.IsNew)
        {
            output.Add(SoundLoopMessage.Stop(entry.Handle));
        }

        entry.IsNew = false;
    }

    private sealed class Entry
    {
        public Entry(LoopKind kind) => Kind = kind;

        public LoopKind Kind { get; }

        /// <summary>No poll has seen it play yet: the first that does starts its loop (or refuses it).</summary>
        public bool NeverPlayed { get; set; } = true;

        /// <summary>The stream's handle; 0 for an instance never started (refused, or not seen playing).</summary>
        public uint Handle { get; set; }

        /// <summary>Started and not stopped (never for a handle of 0).</summary>
        public bool Live { get; set; }

        public bool IsNew { get; set; }

        public bool Seen { get; set; }

        public bool IsPlaying { get; set; }

        public long StartedAt { get; set; }

        public long MovedAt { get; set; }

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }

        public float SentX { get; set; }

        public float SentY { get; set; }

        public float SentZ { get; set; }
    }
}
