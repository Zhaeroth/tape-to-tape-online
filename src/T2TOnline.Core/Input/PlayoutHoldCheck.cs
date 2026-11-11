using System;
using System.Collections.Generic;
using System.Numerics;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Input;

/// <summary>
/// Checks that holds keep their length through playout. Each hold is seen twice: in the friend's own frames, in order
/// (every frame playout moves past, skipped ones included), and in the host's played ticks. A played hold is matched to
/// the friend's hold that contains the frame it started on, and the difference in length is what playout did to it:
/// frames added while waiting for a late one, or removed when skipping to cut delay. Frames are
/// <see cref="PadInputSender.FrameRate"/> Hz on both sides, so a difference of at most one frame means the host felt
/// the same hold. A pad's buttons and a keyboard's key-table elements are checked alike, as bit masks.
/// </summary>
public sealed class PlayoutHoldCheck
{
    private const int MaxRecent = 64;
    private const int MaxWaiting = 16;

    private readonly ulong _mask;
    private readonly uint[] _friendPressFrame;
    private readonly List<(uint Press, uint Release)>[] _friendHolds;
    private readonly uint?[] _cutAt;
    private readonly long[] _playedPressTick;
    private readonly uint[] _playedPressFrame;
    private ulong _friendHeld;
    private ulong _playedHeld;

    /// <summary>Creates an empty check for a pad's buttons.</summary>
    public PlayoutHoldCheck()
        : this(PadState.ButtonCount)
    {
    }

    /// <summary>Creates an empty check for <paramref name="elementCount"/> elements (1 to 64: a keyboard's key table).</summary>
    public PlayoutHoldCheck(int elementCount)
    {
        if (elementCount is < 1 or > KeyboardMouseState.MaxElements)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount), "1 to 64 elements.");
        }

        _mask = KeyboardMouseState.MaskOf(elementCount);
        _friendPressFrame = new uint[elementCount];
        _friendHolds = new List<(uint, uint)>[elementCount];
        _cutAt = new uint?[elementCount];
        _playedPressTick = new long[elementCount];
        _playedPressFrame = new uint[elementCount];
        for (int i = 0; i < elementCount; i++)
        {
            _friendHolds[i] = new List<(uint, uint)>();
        }
    }

    /// <summary>Holds compared.</summary>
    public long Count { get; private set; }

    /// <summary>Holds whose length changed by more than one frame.</summary>
    public long OverOneFrame { get; private set; }

    /// <summary>Friend holds the host never showed on their own: skipped over entirely, or merged into the next one.</summary>
    public long Lost { get; private set; }

    /// <summary>Holds the host let go of before the friend did, because their stream went silent.</summary>
    public long CutShort { get; private set; }

    /// <summary>|played − friend| per hold, in milliseconds, since the caller last cleared it.</summary>
    public SampleWindow ErrorMs { get; } = new();

    /// <summary>The latest comparisons, oldest first, for logs; the caller clears it.</summary>
    public List<HoldComparison> Recent { get; } = new();

    /// <summary>Records one friend frame, in frame order: its number and the buttons the friend held in it.</summary>
    public void ObserveFriendFrame(uint frame, PadButtons held) => ObserveFriendFrame(frame, (ulong)held);

    /// <summary>Records one friend frame, in frame order: its number and the elements the friend held in it (element <c>i</c> at bit <c>i</c>).</summary>
    public void ObserveFriendFrame(uint frame, ulong held)
    {
        held &= _mask;
        ulong changed = held ^ _friendHeld;
        while (changed != 0)
        {
            int i = BitOperations.TrailingZeroCount(changed);
            changed &= changed - 1;
            if ((held & (1UL << i)) != 0)
            {
                _friendPressFrame[i] = frame;
                continue;
            }

            uint press = _friendPressFrame[i];
            if (_cutAt[i] is uint cut && SequenceMath.Distance(press, cut) >= 0)
            {
                _cutAt[i] = null; // already counted as cut short
                continue;
            }

            List<(uint Press, uint Release)> waiting = _friendHolds[i];
            waiting.Add((press, frame));
            if (waiting.Count > MaxWaiting)
            {
                waiting.RemoveAt(0);
                Lost++;
            }
        }

        _friendHeld = held;
    }

    /// <summary>Records one played tick: its index, the friend frame it showed, and the buttons it held.</summary>
    public void ObservePlayedTick(long tick, uint shownFrame, PadButtons held) => ObservePlayedTick(tick, shownFrame, (ulong)held);

    /// <summary>Records one played tick: its index, the friend frame it showed, and the elements it held.</summary>
    public void ObservePlayedTick(long tick, uint shownFrame, ulong held)
    {
        held &= _mask;
        ulong changed = held ^ _playedHeld;
        while (changed != 0)
        {
            int i = BitOperations.TrailingZeroCount(changed);
            changed &= changed - 1;
            if ((held & (1UL << i)) != 0)
            {
                _playedPressTick[i] = tick;
                _playedPressFrame[i] = shownFrame;
            }
            else
            {
                Complete(i, tick);
            }
        }

        _playedHeld = held;
    }

    private void Complete(int button, long releaseTick)
    {
        uint shown = _playedPressFrame[button];
        List<(uint Press, uint Release)> waiting = _friendHolds[button];
        int match = waiting.FindIndex(h => SequenceMath.Distance(h.Press, shown) >= 0 && SequenceMath.Distance(shown, h.Release) > 0);
        if (match < 0)
        {
            // The friend is still holding: the host let go first, because their stream went quiet.
            if ((_friendHeld & (1UL << button)) != 0)
            {
                CutShort++;
                _cutAt[button] = shown;
            }

            return;
        }

        (uint press, uint release) = waiting[match];
        Lost += match;
        waiting.RemoveRange(0, match + 1);
        var comparison = new HoldComparison(button, SequenceMath.Distance(press, release), (int)Math.Min(releaseTick - _playedPressTick[button], int.MaxValue));
        Count++;
        if (Math.Abs(comparison.ErrorFrames) > 1)
        {
            OverOneFrame++;
        }

        ErrorMs.Add(Math.Abs(comparison.ErrorMs));
        Recent.Add(comparison);
        if (Recent.Count > MaxRecent)
        {
            Recent.RemoveAt(0);
        }
    }
}

/// <summary>One hold, as the friend held it and as the host played it.</summary>
/// <param name="Element">The button's bit index in <see cref="PadButtons"/>, or the key-table element's index.</param>
/// <param name="FriendFrames">Length in the friend's frames.</param>
/// <param name="PlayedTicks">Length in the host's played ticks.</param>
public readonly record struct HoldComparison(int Element, int FriendFrames, int PlayedTicks)
{
    /// <summary>The pad button, for a pad's hold (<see cref="PadButtons.None"/> for a key-table element past the pad's buttons).</summary>
    public PadButtons Button => (uint)Element < PadState.ButtonCount ? (PadButtons)(1 << Element) : PadButtons.None;

    /// <summary>Played minus friend, in frames (in 64 bits: crafted frame numbers can be far apart).</summary>
    public long ErrorFrames => (long)PlayedTicks - FriendFrames;

    /// <summary>Played minus friend, in milliseconds.</summary>
    public double ErrorMs => ErrorFrames * 1000.0 / PadInputSender.FrameRate;

    /// <summary>The friend's hold, in milliseconds.</summary>
    public double FriendMs => FriendFrames * 1000.0 / PadInputSender.FrameRate;
}
