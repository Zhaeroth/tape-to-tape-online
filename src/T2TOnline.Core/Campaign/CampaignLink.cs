using System;
using System.Collections.Generic;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Campaign;

/// <summary>Reads one host campaign message of <paramref name="type"/> (its header already read). False when it's malformed.</summary>
public delegate bool CampaignMessageReader(MessageType type, ref BitReader reader);

/// <summary>Reads one campaign message of <paramref name="type"/> from friend <paramref name="friend"/> (its header already read). False when it's malformed.</summary>
public delegate bool CampaignFriendMessageReader(ulong friend, MessageType type, ref BitReader reader);

/// <summary>
/// The host's side of the campaign link: sends each friend the newest run checkpoint (channel 2) and host view
/// (channel 1), once each, and again to a friend who rejoins; keeps each friend's follow reports. Game-independent: the
/// glue publishes runs and views and calls <see cref="Update"/> every frame. Not thread-safe (one game thread).
/// </summary>
public sealed class CampaignHost
{
    private readonly ISessionTransport _transport;
    private readonly BitWriter _writer = new(4096);
    private readonly Dictionary<ulong, uint> _viewSentTo = new();
    private readonly List<ulong> _friends = new();
    private readonly ulong[] _sequences = new ulong[256];
    private uint _epoch;

    // The newest checkpoint's payload, compressed once for every friend and every retry (null: none yet, or it doesn't fit).
    private RunCheckpoint? _encoded;
    private byte[]? _encodedPayload;

    /// <summary>Creates the host side over <paramref name="transport"/>.</summary>
    public CampaignHost(ISessionTransport transport) => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>The run checkpoints.</summary>
    public CheckpointPublisher Checkpoints { get; } = new();

    /// <summary>Each friend's newest follow report.</summary>
    public FollowTracker Follows { get; } = new();

    /// <summary>The host's newest view, or null before the first.</summary>
    public HostView? View { get; private set; }

    /// <summary>Friends in the link.</summary>
    public IReadOnlyList<ulong> Friends => _friends;

    /// <summary>Checkpoints too big for one message, counted once each (not sent; the glue logs them).</summary>
    public int OversizeCheckpoints { get; private set; }

    /// <summary>Sends the transport refused (tried again next frame).</summary>
    public int RefusedSends { get; private set; }

    /// <summary>Follow reports that didn't decode.</summary>
    public int MalformedReports { get; private set; }

    /// <summary>
    /// Reads friends' other campaign messages (a progress resend request, a run copy request), header already read; false
    /// when malformed. Unset, or for a type nobody reads, they count as malformed.
    /// </summary>
    public CampaignFriendMessageReader? FriendMessages { get; set; }

    /// <summary>A friend joined (or rejoined): they get the newest checkpoint and view.</summary>
    public void AddFriend(ulong friend)
    {
        if (!_friends.Contains(friend))
        {
            _friends.Add(friend);
        }

        Checkpoints.Forget(friend);
        _viewSentTo.Remove(friend);
        Follows.Forget(friend);
    }

    /// <summary>A friend left.</summary>
    public void RemoveFriend(ulong friend)
    {
        _friends.Remove(friend);
        Checkpoints.Forget(friend);
        _viewSentTo.Remove(friend);
        Follows.Forget(friend);
    }

    /// <summary>
    /// The host's game saved its run: the parts become the next checkpoint unless they're unchanged. Returns the new
    /// checkpoint, or null when unchanged. The view stays: friends on it bring their run up to date
    /// (<see cref="FollowStep.UpdateRun"/>). With friends in, the new checkpoint's message is made now (it's deflated once,
    /// for every friend), so its cost is measured where it's published (<see cref="LastPublishCost"/>).
    /// </summary>
    public RunCheckpoint? PublishRun(IReadOnlyDictionary<RunCheckpointPart, byte[]> parts)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        RunCheckpoint? published = Checkpoints.Publish(parts);
        long hashed = System.Diagnostics.Stopwatch.GetTimestamp();
        int payload = published != null && _friends.Count > 0 ? Encode(published)?.Length ?? -1 : 0;
        long encoded = System.Diagnostics.Stopwatch.GetTimestamp();
        LastPublishCost = new PublishCost(Milliseconds(hashed - start), Milliseconds(encoded - hashed), payload);
        return published;
    }

    /// <summary>
    /// What the last <see cref="PublishRun"/> cost on the host's main thread: hashing (and comparing with the last
    /// checkpoint), and deflating the message for friends (0 with none in, or when unchanged), with the message's size
    /// (-1 when it's too big to send).
    /// </summary>
    public PublishCost LastPublishCost { get; private set; }

    /// <summary>
    /// The host's game is on <paramref name="screen"/> with <paramref name="values"/>. A change starts a new view (the
    /// newest checkpoint is the one friends need first); the same place again changes nothing. Returns the view.
    /// </summary>
    public HostView SetView(string screen, IReadOnlyList<KeyValuePair<string, NetValue>> values, bool needsRun = true)
    {
        uint checkpoint = needsRun ? Checkpoints.Latest?.Sequence ?? 0 : 0;
        var candidate = new HostView(_epoch + 1, screen, checkpoint, values);
        if (View != null && View.ContentHash == candidate.ContentHash && View.Checkpoint == checkpoint)
        {
            return View;
        }

        _epoch++;
        View = candidate;
        return View;
    }

    /// <summary>Sends what each friend still needs: the newest checkpoint first, then the newest view.</summary>
    public void Update()
    {
        foreach (ulong friend in _friends)
        {
            RunCheckpoint? pending = Checkpoints.PendingFor(friend);
            if (pending != null)
            {
                byte[]? payload = Encode(pending);
                if (payload == null)
                {
                    Checkpoints.MarkSent(friend, pending.Sequence); // never fits: don't retry it every frame
                }
                else
                {
                    Begin(MessageType.RunCheckpoint);
                    _writer.WriteBytes(payload);
                    if (Send(friend, Channel.Bulk))
                    {
                        Checkpoints.MarkSent(friend, pending.Sequence);
                    }
                }
            }

            if (View != null && (!_viewSentTo.TryGetValue(friend, out uint sent) || sent != View.Epoch))
            {
                Begin(MessageType.HostView);
                HostViewMessage.Write(_writer, View);
                if (Send(friend, Channel.Events))
                {
                    _viewSentTo[friend] = View.Epoch;
                }
            }
        }
    }

    /// <summary>
    /// Sends a module's message (menus, progress) to every friend in the link: <paramref name="payload"/> writes it after
    /// the header. Returns how many friends it went to (a refused send is counted in <see cref="RefusedSends"/>).
    /// </summary>
    public int Broadcast(MessageType type, Channel channel, Action<BitWriter> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Begin(type);
        payload(_writer);
        int sent = 0;
        foreach (ulong friend in _friends)
        {
            if (Send(friend, channel))
            {
                sent++;
            }
        }

        return sent;
    }

    /// <summary>Sends a module's message to one friend in the link. False when they aren't in it or the send was refused.</summary>
    public bool SendTo(ulong friend, MessageType type, Channel channel, Action<BitWriter> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!_friends.Contains(friend))
        {
            return false;
        }

        Begin(type);
        payload(_writer);
        return Send(friend, channel);
    }

    /// <summary>Handles a campaign message from a friend (header included). False when it's malformed or unexpected.</summary>
    public bool Receive(ulong sender, ReadOnlySpan<byte> message)
    {
        var reader = new BitReader(message);
        if (!MessageHeader.TryRead(ref reader, out MessageHeader header) || !_friends.Contains(sender))
        {
            return false;
        }

        switch (header.Type)
        {
            case MessageType.FollowReport:
                if (!FollowReportMessage.TryRead(ref reader, out FollowReport report))
                {
                    MalformedReports++;
                    return false;
                }

                Follows.Record(sender, report);
                return true;
            case MessageType.ProgressResend or MessageType.ProgressRunRequest:
                return FriendMessages?.Invoke(sender, header.Type, ref reader) == true;
            default:
                return false;
        }
    }

    private void Begin(MessageType type)
    {
        _writer.Reset();
        new MessageHeader(type, _sequences[(byte)type]++, 0, 0).Write(_writer);
    }

    /// <summary>
    /// <paramref name="checkpoint"/>'s message payload (byte-aligned, as the header), compressed the first time it's asked
    /// for; null when it's too big to send (counted once in <see cref="OversizeCheckpoints"/>).
    /// </summary>
    private byte[]? Encode(RunCheckpoint checkpoint)
    {
        if (!ReferenceEquals(checkpoint, _encoded))
        {
            var payload = new BitWriter(4096);
            _encoded = checkpoint;
            _encodedPayload = RunCheckpointMessage.Write(payload, checkpoint) ? payload.ToArray() : null;
            if (_encodedPayload == null)
            {
                OversizeCheckpoints++;
            }
        }

        return _encodedPayload;
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private bool Send(ulong friend, Channel channel)
    {
        if (_transport.Send(friend, channel, _writer.AsSpan()))
        {
            return true;
        }

        RefusedSends++;
        return false;
    }
}

/// <summary>
/// A friend's side of the campaign link: host views and run checkpoints go to the <see cref="Follower"/>; its reports go
/// back to the host. Game-independent. Not thread-safe (one game thread).
/// </summary>
public sealed class CampaignClient
{
    private readonly ISessionTransport _transport;
    private readonly ulong _host;
    private readonly BitWriter _writer = new(256);
    private readonly BitWriter _otherWriter = new(64);
    private readonly ulong[] _otherSequences = new ulong[256];
    private ulong _reportSequence;

    /// <summary>Creates the friend side over <paramref name="transport"/>, talking to <paramref name="host"/>.</summary>
    public CampaignClient(ISessionTransport transport, ulong host)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _host = host;
    }

    /// <summary>Where the host is, and what to do about it.</summary>
    public CampaignFollower Follower { get; } = new();

    /// <summary>
    /// Reads the host's other campaign messages (menus, progress), header already read; false when malformed. Unset, or
    /// for a type nobody reads, they count as malformed.
    /// </summary>
    public CampaignMessageReader? OtherMessages { get; set; }

    /// <summary>Messages that didn't decode.</summary>
    public int Malformed { get; private set; }

    /// <summary>Checkpoints received (including ignored older ones).</summary>
    public int CheckpointsReceived { get; private set; }

    /// <summary>Views received (including ignored older ones).</summary>
    public int ViewsReceived { get; private set; }

    /// <summary>Handles a campaign message from the host (header included). False when it's malformed or unexpected.</summary>
    public bool Receive(ulong sender, ReadOnlySpan<byte> message)
    {
        var reader = new BitReader(message);
        if (sender != _host || !MessageHeader.TryRead(ref reader, out MessageHeader header))
        {
            return false;
        }

        switch (header.Type)
        {
            case MessageType.RunCheckpoint:
                if (!RunCheckpointMessage.TryRead(ref reader, out RunCheckpoint? checkpoint))
                {
                    Malformed++;
                    return false;
                }

                CheckpointsReceived++;
                Follower.OfferCheckpoint(checkpoint!);
                return true;
            case MessageType.HostView:
                if (!HostViewMessage.TryRead(ref reader, out HostView? view))
                {
                    Malformed++;
                    return false;
                }

                ViewsReceived++;
                Follower.OfferView(view!);
                return true;
            case MessageType.MenuOpen or MessageType.MenuNav or MessageType.MenuSelect or MessageType.MenuClick or MessageType.MenuAttachment or
                MessageType.Notification or MessageType.ProgressEvent or MessageType.ProgressStatus or MessageType.ProgressRunCopy:
                if (OtherMessages?.Invoke(header.Type, ref reader) == true)
                {
                    return true;
                }

                Malformed++;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// The link to the host (re)started: a rejoining friend is new to the host's tracker, so the follower's report goes out
    /// again with the next <see cref="Update"/>, changed or not.
    /// </summary>
    public void ReportAgain() => Follower.ReportAgain();

    /// <summary>
    /// Sends a module's message (a progress resend request) to the host: <paramref name="payload"/> writes it after the
    /// header. False when the send was refused.
    /// </summary>
    public bool SendToHost(MessageType type, Channel channel, Action<BitWriter> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _otherWriter.Reset();
        new MessageHeader(type, _otherSequences[(byte)type]++, 0, 0).Write(_otherWriter);
        payload(_otherWriter);
        return _transport.Send(_host, channel, _otherWriter.AsSpan());
    }

    /// <summary>Sends the follower's report to the host when it changed. Call every frame.</summary>
    public void Update()
    {
        if (!Follower.TryTakeReport(out FollowReport report))
        {
            return;
        }

        _writer.Reset();
        new MessageHeader(MessageType.FollowReport, _reportSequence++, 0, 0).Write(_writer);
        FollowReportMessage.Write(_writer, report);
        if (!_transport.Send(_host, Channel.Events, _writer.AsSpan()))
        {
            Follower.ReportAgain();
        }
    }
}

/// <summary>What publishing a run checkpoint cost the host (<see cref="CampaignHost.LastPublishCost"/>).</summary>
/// <param name="HashMs">Hashing the parts and comparing them with the last checkpoint, in milliseconds.</param>
/// <param name="DeflateMs">Making the message (deflating the parts) for friends, in milliseconds.</param>
/// <param name="MessageBytes">The message's size (0 when none was made, -1 when it's too big to send).</param>
public readonly record struct PublishCost(double HashMs, double DeflateMs, int MessageBytes);
