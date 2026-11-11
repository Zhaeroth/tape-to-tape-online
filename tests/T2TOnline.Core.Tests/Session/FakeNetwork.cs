using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// An in-memory network between session endpoints, with latency, jitter and loss. Realtime messages can be lost and
/// reordered; reliable channels arrive once and in order, like Steam's. Time is in microseconds. A peer can have a link
/// of its own (<see cref="Links"/>), which applies to everything it sends and receives.
/// </summary>
/// <remarks>
/// While a link is cut, realtime messages are lost and reliable ones wait, as Steam keeps resending them: they arrive in
/// order once it's back. A link down longer than <see cref="ReliableTimeoutMicros"/> has timed out, as Steam's connection
/// would: what waited on it, and anything sent on it until it's back, is lost.
/// </remarks>
internal sealed class FakeNetwork
{
    private readonly List<InFlight> _inFlight = new();
    private readonly Dictionary<(ulong, ulong, Channel), long> _lastReliableDue = new();
    private readonly Random _random;
    private long _order;
    private long _cutSince = long.MinValue;

    public FakeNetwork(int seed = 1) => _random = new Random(seed);

    public long Now { get; set; }

    public long LatencyMicros { get; set; }

    public long JitterMicros { get; set; }

    public double RealtimeLossFraction { get; set; }

    /// <summary>When true, nothing is delivered (a dead link).</summary>
    public bool Cut { get; set; }

    /// <summary>How long a cut link keeps reliable messages before its connection times out (Steam's default: 10 s).</summary>
    public long ReliableTimeoutMicros { get; set; } = 10_000_000;

    /// <summary>Per-peer links, overriding the network-wide settings for messages to or from that peer.</summary>
    public Dictionary<ulong, LinkProfile> Links { get; } = new();

    /// <summary>
    /// When set, a send it answers true for (from, to, channel, message) is refused: the sender gets false and nothing
    /// goes, as Steam refuses a send when the peer's send buffer is full.
    /// </summary>
    public Func<ulong, ulong, Channel, byte[], bool>? Refuse { get; set; }

    /// <summary>Sends refused by <see cref="Refuse"/>.</summary>
    public int Refused { get; private set; }

    public int Sent { get; private set; }

    public ISessionTransport For(ulong self) => new Endpoint(this, self);

    /// <summary>Hands every message due by <see cref="Now"/> to <paramref name="deliver"/>, earliest first.</summary>
    public void Deliver(Action<ulong, ulong, Channel, byte[]> deliver)
    {
        _cutSince = Cut ? (_cutSince == long.MinValue ? Now : _cutSince) : long.MinValue;
        foreach (LinkProfile link in Links.Values)
        {
            link.CutSince = link.Cut ? (link.CutSince == long.MinValue ? Now : link.CutSince) : long.MinValue;
        }

        List<InFlight> due = _inFlight.Where(m => m.Due <= Now).OrderBy(m => m.Due).ThenBy(m => m.Order).ToList();
        foreach (InFlight message in due)
        {
            long linkCutSince = LinkOf(message.From, message.To)?.CutSince ?? long.MinValue;
            long cutSince = _cutSince == long.MinValue ? linkCutSince : linkCutSince == long.MinValue ? _cutSince : Math.Min(_cutSince, linkCutSince);
            if (cutSince == long.MinValue)
            {
                _inFlight.Remove(message);
                deliver(message.From, message.To, message.Channel, message.Bytes);
            }
            else if (message.Channel == Channel.Realtime || Now - cutSince > ReliableTimeoutMicros)
            {
                _inFlight.Remove(message); // lost
            }

            // Otherwise a reliable message waits for the link: still in flight, still in order.
        }
    }

    private LinkProfile? LinkOf(ulong from, ulong to) =>
        Links.TryGetValue(to, out LinkProfile? link) || Links.TryGetValue(from, out link) ? link : null;

    private bool Send(ulong from, ulong to, Channel channel, ReadOnlySpan<byte> bytes)
    {
        if (Refuse?.Invoke(from, to, channel, bytes.ToArray()) == true)
        {
            Refused++;
            return false;
        }

        Sent++;
        LinkProfile? link = LinkOf(from, to);
        if (channel == Channel.Realtime && _random.NextDouble() < (link?.Loss ?? RealtimeLossFraction))
        {
            return true; // lost on the way: the sender can't tell
        }

        long jitterRange = link?.Jitter ?? JitterMicros;
        long jitter = jitterRange == 0 ? 0 : (long)(_random.NextDouble() * jitterRange);
        long due = Now + (link?.Latency ?? LatencyMicros) + jitter;
        if (channel != Channel.Realtime)
        {
            var key = (from, to, channel);
            due = Math.Max(due, _lastReliableDue.GetValueOrDefault(key) + 1);
            _lastReliableDue[key] = due;
        }

        _inFlight.Add(new InFlight(due, _order++, from, to, channel, bytes.ToArray()));
        return true;
    }

    /// <summary>One peer's link: one-way latency and jitter (microseconds), realtime loss, and whether it's down.</summary>
    internal sealed class LinkProfile
    {
        public long Latency { get; set; }

        public long Jitter { get; set; }

        public double Loss { get; set; }

        public bool Cut { get; set; }

        /// <summary>When the network first saw this link cut (kept by <see cref="FakeNetwork.Deliver"/>), or long.MinValue.</summary>
        public long CutSince { get; set; } = long.MinValue;
    }

    private sealed record InFlight(long Due, long Order, ulong From, ulong To, Channel Channel, byte[] Bytes);

    private sealed class Endpoint : ISessionTransport
    {
        private readonly FakeNetwork _network;
        private readonly ulong _self;

        public Endpoint(FakeNetwork network, ulong self)
        {
            _network = network;
            _self = self;
        }

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message) => _network.Send(_self, peer, channel, message);
    }
}
