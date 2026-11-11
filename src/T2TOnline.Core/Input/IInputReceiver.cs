namespace T2TOnline.Core.Input;

/// <summary>What the host's reports read from a friend's input stream, a pad's or a keyboard's.</summary>
public interface IInputReceiver
{
    /// <summary>Packets decoded.</summary>
    long Packets { get; }

    /// <summary>Packets that didn't decode.</summary>
    long MalformedPackets { get; }

    /// <summary>One-update presses injected for taps shorter than a frame.</summary>
    long Taps { get; }

    /// <summary>Ticks dropped after stalls longer than the catch-up limit.</summary>
    long DroppedTicks { get; }

    /// <summary>The playout delay now, in frames.</summary>
    int BufferedFrames { get; }

    /// <summary>The playout's running counts.</summary>
    PadPlayoutStats PlayoutStats { get; }

    /// <summary>Hold lengths, friend frames against played ticks.</summary>
    PlayoutHoldCheck Holds { get; }
}
