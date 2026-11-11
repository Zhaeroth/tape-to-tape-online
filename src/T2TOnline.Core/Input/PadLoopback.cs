using System;
using System.Collections.Generic;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Input;

/// <summary>
/// The whole remote-pad path in one process: the friend's side (<see cref="PadInputSender"/>, encoded to bytes), a
/// link with an optional fixed delay, and the host's side (<see cref="PadInputReceiver"/>). It drives a virtual pad
/// from a real one on a single PC, so the input path can be tested in the game without a network.
/// </summary>
public sealed class PadLoopback
{
    /// <summary>Pad frames per second, on both sides.</summary>
    public const int FrameRate = PadInputSender.FrameRate;

    private readonly long _delayTicks;
    private readonly PadInputSender _sender;
    private readonly PadInputReceiver _receiver;
    private readonly BitWriter _writer = new(64);
    private readonly Queue<(long DueAt, byte[] Packet)> _link = new();

    /// <summary>Creates a loopback whose timestamps run at <paramref name="ticksPerSecond"/>.</summary>
    /// <param name="ticksPerSecond">Units per second of every timestamp passed in.</param>
    /// <param name="delayTicks">One-way link delay to simulate, in the same units.</param>
    public PadLoopback(long ticksPerSecond, long delayTicks = 0)
    {
        if (ticksPerSecond <= 0 || delayTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), "The clock rate must be positive and the delay not negative.");
        }

        _delayTicks = delayTicks;
        _sender = new PadInputSender(ticksPerSecond);
        _receiver = new PadInputReceiver(ticksPerSecond);
    }

    /// <summary>Packets sent over the link so far.</summary>
    public long PacketsSent { get; private set; }

    /// <summary>Bytes sent over the link so far.</summary>
    public long BytesSent { get; private set; }

    /// <summary>The host side's playout statistics.</summary>
    public PadPlayoutStats PlayoutStats => _receiver.Playout.Stats;

    /// <summary>
    /// Friend side: samples the physical pad at <paramref name="now"/>. Emits a frame for every 120 Hz tick reached
    /// since the last call and, if any, sends a packet with the newest frames.
    /// </summary>
    public void Capture(long now, PadButtons held, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger)
    {
        if (!_sender.Capture(now, held, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger))
        {
            return;
        }

        _writer.Reset();
        _sender.WritePacket(_writer);
        byte[] packet = _writer.ToArray();
        _link.Enqueue((now + _delayTicks, packet));
        PacketsSent++;
        BytesSent += packet.Length;
    }

    /// <summary>
    /// Host side: delivers packets that have arrived by <paramref name="now"/> and plays out every 120 Hz tick
    /// reached. Returns the pad state to apply and any presses too short to show in it (apply those for one update).
    /// </summary>
    public (PadState State, PadButtons Taps) Update(long now)
    {
        while (_link.Count > 0 && _link.Peek().DueAt <= now)
        {
            var reader = new BitReader(_link.Dequeue().Packet);
            _receiver.Receive(ref reader);
        }

        return _receiver.Update(now);
    }
}
