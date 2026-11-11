using System;
using System.Collections.Generic;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Tests.Session;

namespace T2TOnline.Core.Tests.Campaign;

public class CampaignLinkTests
{
    private const ulong HostId = 100;
    private const ulong FriendA = 200;
    private const ulong FriendB = 300;
    private const long Ms = 1000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.6.0-m4", 0x1122334455667788);

    private static Dictionary<RunCheckpointPart, byte[]> Run(string text) => new()
    {
        [RunCheckpointPart.Run] = Encoding.UTF8.GetBytes(text),
        [RunCheckpointPart.RunStats] = Encoding.UTF8.GetBytes("stats:" + text),
    };

    private static List<KeyValuePair<string, NetValue>> Node(string node) => new() { new("node", NetValue.FromString(node)) };

    [Fact]
    public void Friends_get_the_run_then_the_view_and_the_host_sees_them_follow()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 30 * Ms }, FriendA, FriendB);
        h.JoinAll();
        h.Campaign.PublishRun(Run("act 1"));
        HostView view = h.Campaign.SetView("map", Node("0,0"));
        h.Run(100 * Ms);

        foreach (ulong friend in h.FriendIds)
        {
            CampaignFollower follower = h.Clients[friend].Follower;
            Assert.Equal(FollowStep.GoToView, follower.Next(h.Net.Now));
            Assert.Equal("act 1", Encoding.UTF8.GetString(follower.Checkpoints.Latest!.Get(RunCheckpointPart.Run)!));
            follower.Arrived(follower.Target!, follower.Checkpoints.Latest.Sequence);
        }

        h.Run(100 * Ms);
        Assert.True(h.Campaign.Follows.AllFollowing(h.FriendIds, view.Epoch));
        Assert.Equal(0, h.Campaign.RefusedSends);
    }

    [Fact]
    public void A_run_saved_while_friends_are_on_a_view_reaches_them_as_an_update()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 20 * Ms }, FriendA);
        h.JoinAll();
        h.Campaign.PublishRun(Run("before reward"));
        h.Campaign.SetView("menu", Node("reward"));
        h.Run(100 * Ms);
        CampaignFollower follower = h.Clients[FriendA].Follower;
        Assert.Equal(FollowStep.GoToView, follower.Next(h.Net.Now));
        follower.Arrived(follower.Target!, 1);

        Assert.Null(h.Campaign.PublishRun(Run("before reward"))); // the same run saved again: nothing sent
        Assert.NotNull(h.Campaign.PublishRun(Run("after reward")));
        h.Run(100 * Ms);
        Assert.Equal(FollowStep.UpdateRun, follower.Next(h.Net.Now));
        Assert.Equal("after reward", Encoding.UTF8.GetString(follower.Checkpoints.Latest!.Get(RunCheckpointPart.Run)!));
        Assert.Equal(1, h.Clients[FriendA].ViewsReceived);
        Assert.Equal(2, h.Clients[FriendA].CheckpointsReceived);
    }

    [Fact]
    public void A_friend_who_rejoins_gets_the_newest_run_and_view_again()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 20 * Ms }, FriendA);
        h.JoinAll();
        h.Campaign.PublishRun(Run("x"));
        h.Campaign.SetView("map", Node("1,2"));
        h.Run(100 * Ms);
        Assert.Equal(1, h.Clients[FriendA].ViewsReceived);

        h.RestartFriend(FriendA); // the friend's game started over; the host re-adds them on rejoin
        h.JoinAll();
        h.Run(100 * Ms);
        CampaignClient client = h.Clients[FriendA];
        Assert.Equal(1, client.ViewsReceived);
        Assert.Equal(1, client.CheckpointsReceived);
        Assert.Equal(FollowStep.GoToView, client.Follower.Next(h.Net.Now));
        Assert.Equal("1,2", client.Follower.Target!.Text("node"));
    }

    [Fact]
    public void A_friend_whose_link_restarts_tells_the_host_where_it_is_again()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 20 * Ms }, FriendA);
        h.JoinAll();
        h.Campaign.PublishRun(Run("x"));
        HostView view = h.Campaign.SetView("map", Node("1,2"));
        h.Run(100 * Ms);
        CampaignFollower follower = h.Clients[FriendA].Follower;
        Assert.Equal(FollowStep.GoToView, follower.Next(h.Net.Now));
        follower.Arrived(follower.Target!, 1);
        h.Run(100 * Ms);
        Assert.True(h.Campaign.Follows.TryGet(FriendA, out FollowReport before));
        Assert.Equal(FollowState.Following, before.State);

        h.RestartSession(FriendA); // the friend's session timed out and says hello again; its campaign side carries on
        h.JoinAll();
        Assert.False(h.Campaign.Follows.TryGet(FriendA, out _)); // the host re-added them and forgot where they were
        h.Run(100 * Ms);
        Assert.False(h.Campaign.Follows.TryGet(FriendA, out _)); // nothing changed on the friend's side, so nothing was said

        h.Clients[FriendA].ReportAgain();
        h.Run(100 * Ms);
        Assert.True(h.Campaign.Follows.TryGet(FriendA, out FollowReport after));
        Assert.Equal(new FollowReport(view.Epoch, FollowState.Following, 1, string.Empty), after);
        Assert.Equal(FollowStep.None, follower.Next(h.Net.Now)); // the run and view sent again are the ones it has
    }

    [Fact]
    public void A_refused_checkpoint_goes_out_again_unchanged_to_every_friend()
    {
        var transport = new RefusingTransport { Refuse = 2 };
        var host = new CampaignHost(transport);
        host.AddFriend(FriendA);
        host.AddFriend(FriendB);
        RunCheckpoint published = host.PublishRun(Run("a run worth sending"))!;
        host.Update(); // both refused
        Assert.Empty(transport.Sent);
        host.Update(); // both go
        host.Update(); // nothing left to send
        Assert.Equal(2, host.RefusedSends);
        Assert.Equal(new[] { FriendA, FriendB }, transport.Sent.ConvertAll(m => m.To));
        foreach ((ulong _, byte[] bytes) in transport.Sent)
        {
            var client = new CampaignClient(transport, HostId);
            Assert.True(client.Receive(HostId, bytes));
            RunCheckpoint got = client.Follower.Checkpoints.Latest!;
            Assert.Equal(published.Sequence, got.Sequence);
            Assert.Equal(published.Hash, got.Hash);
        }
    }

    [Fact]
    public void A_checkpoint_too_big_to_send_is_counted_once_and_never_retried()
    {
        var transport = new RefusingTransport();
        var host = new CampaignHost(transport);
        host.AddFriend(FriendA);
        host.AddFriend(FriendB);
        byte[] noise = new byte[RunCheckpointMessage.MaxMessageBytes + 1024]; // doesn't compress
        new Random(3).NextBytes(noise);
        Assert.NotNull(host.PublishRun(new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = noise }));
        host.Update();
        host.Update();
        Assert.Equal(1, host.OversizeCheckpoints);
        Assert.Empty(transport.Sent);

        host.PublishRun(Run("smaller"));
        host.Update();
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public void Publishing_says_what_it_cost_and_makes_the_message_once_for_every_friend()
    {
        var transport = new RefusingTransport();
        var host = new CampaignHost(transport);
        Assert.NotNull(host.PublishRun(Run("nobody in yet")));
        Assert.Equal(0, host.LastPublishCost.MessageBytes); // nothing deflated with no friend in

        host.AddFriend(FriendA);
        host.AddFriend(FriendB);
        Assert.NotNull(host.PublishRun(Run("two friends in")));
        PublishCost cost = host.LastPublishCost;
        Assert.True(cost.MessageBytes > 0);
        Assert.True(cost.HashMs >= 0 && cost.DeflateMs >= 0);
        host.Update();
        Assert.Equal(2, transport.Sent.Count);
        Assert.All(transport.Sent, m => Assert.True(m.Bytes.Length >= cost.MessageBytes)); // the one message made at publish

        Assert.Null(host.PublishRun(Run("two friends in")));
        Assert.Equal(0, host.LastPublishCost.MessageBytes); // unchanged: nothing made
    }

    [Fact]
    public void A_checkpoint_too_big_says_so_in_its_cost()
    {
        var host = new CampaignHost(new RefusingTransport());
        host.AddFriend(FriendA);
        byte[] noise = new byte[RunCheckpointMessage.MaxMessageBytes + 1024];
        new Random(5).NextBytes(noise);
        host.PublishRun(new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = noise });
        Assert.Equal(-1, host.LastPublishCost.MessageBytes);
        host.Update();
        Assert.Equal(1, host.OversizeCheckpoints); // counted once, at publish
    }

    [Fact]
    public void The_same_place_again_is_not_a_new_view()
    {
        var h = new Harness(new FakeNetwork(), FriendA);
        HostView first = h.Campaign.SetView("map", Node("1,2"));
        Assert.Same(first, h.Campaign.SetView("map", Node("1,2")));
        HostView moved = h.Campaign.SetView("map", Node("2,2"));
        Assert.Equal(first.Epoch + 1, moved.Epoch);
        h.Campaign.PublishRun(Run("y"));
        HostView withRun = h.Campaign.SetView("map", Node("2,2"));
        Assert.Equal(moved.Epoch + 1, withRun.Epoch); // same place, but friends now need the newer run first
        Assert.Equal(1u, withRun.Checkpoint);
        Assert.Equal(0u, h.Campaign.SetView("title", new List<KeyValuePair<string, NetValue>>(), needsRun: false).Checkpoint);
    }

    [Fact]
    public void Module_messages_reach_the_reader_on_every_friend()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms }, FriendA, FriendB);
        h.JoinAll();
        var got = new List<(ulong Friend, MessageType Type, int Value)>();
        foreach (ulong friend in h.FriendIds)
        {
            ulong id = friend;
            h.Clients[friend].OtherMessages = (MessageType type, ref BitReader reader) =>
            {
                got.Add((id, type, reader.ReadByte()));
                return !reader.IsOverrun;
            };
        }

        Assert.Equal(2, h.Campaign.Broadcast(MessageType.MenuOpen, Channel.Events, writer => writer.WriteByte(7)));
        Assert.Equal(2, h.Campaign.Broadcast(MessageType.MenuAttachment, Channel.Events, writer => writer.WriteByte(8)));
        Assert.True(h.Campaign.SendTo(FriendB, MessageType.ProgressEvent, Channel.Events, writer => writer.WriteByte(9)));
        Assert.True(h.Campaign.SendTo(FriendA, MessageType.MenuAttachment, Channel.Events, writer => writer.WriteByte(10)));
        Assert.False(h.Campaign.SendTo(999, MessageType.ProgressEvent, Channel.Events, writer => writer.WriteByte(9)));
        h.Run(50 * Ms);

        Assert.Contains((FriendA, MessageType.MenuOpen, 7), got);
        Assert.Contains((FriendB, MessageType.MenuOpen, 7), got);
        Assert.Contains((FriendA, MessageType.MenuAttachment, 8), got);
        Assert.Contains((FriendB, MessageType.MenuAttachment, 8), got);
        Assert.Contains((FriendB, MessageType.ProgressEvent, 9), got);
        Assert.Contains((FriendA, MessageType.MenuAttachment, 10), got);
        Assert.Equal(6, got.Count);

        // A menu's attachment comes after its open: one reliable channel keeps them in order.
        Assert.True(got.FindIndex(g => g == (FriendA, MessageType.MenuOpen, 7)) < got.FindIndex(g => g == (FriendA, MessageType.MenuAttachment, 8)));
    }

    [Fact]
    public void The_hosts_toasts_reach_every_friends_reader_in_order()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 10 * Ms }, FriendA, FriendB);
        h.JoinAll();
        var got = new List<(ulong Friend, NotificationOp Op)>();
        foreach (ulong friend in h.FriendIds)
        {
            ulong id = friend;
            h.Clients[friend].OtherMessages = (MessageType type, ref BitReader reader) =>
            {
                if (type != MessageType.Notification || !NotificationMessage.TryRead(ref reader, out NotificationEvent? read))
                {
                    return false;
                }

                got.Add((id, read!.Op));
                return true;
            };
        }

        Assert.Equal(2, h.Campaign.Broadcast(MessageType.Notification, Channel.Events, w => NotificationMessage.Write(w, NotificationEvent.Queued(NotificationCopyTests.Relic()))));
        Assert.Equal(2, h.Campaign.Broadcast(MessageType.Notification, Channel.Events, w => NotificationMessage.Write(w, NotificationEvent.Popped(4, true))));
        h.Run(50 * Ms);

        // The toast before its pop, on each friend's one reliable channel.
        Assert.Equal(new[] { (FriendA, NotificationOp.Queue), (FriendA, NotificationOp.Pop) }, got.FindAll(g => g.Friend == FriendA));
        Assert.Equal(new[] { (FriendB, NotificationOp.Queue), (FriendB, NotificationOp.Pop) }, got.FindAll(g => g.Friend == FriendB));
        Assert.Equal(0, h.Sessions[FriendA].Link.Malformed);
    }

    [Fact]
    public void A_progress_status_reaches_a_friends_module_and_a_resend_request_the_hosts()
    {
        var h = new Harness(new FakeNetwork { LatencyMicros = 20 * Ms }, FriendA, FriendB);
        h.JoinAll();
        var statuses = new List<(ulong Friend, ProgressStatus Status)>();
        foreach (ulong friend in h.FriendIds)
        {
            ulong id = friend;
            h.Clients[friend].OtherMessages = (MessageType type, ref BitReader reader) =>
            {
                if (type != MessageType.ProgressStatus || !ProgressStatusMessage.TryRead(ref reader, out ProgressStatus status))
                {
                    return false;
                }

                statuses.Add((id, status));
                return true;
            };
        }

        var requests = new List<(ulong Friend, ProgressResend Request)>();
        h.Campaign.FriendMessages = (ulong friend, MessageType type, ref BitReader reader) =>
        {
            if (type != MessageType.ProgressResend || !ProgressResendMessage.TryRead(ref reader, out ProgressResend request))
            {
                return false;
            }

            requests.Add((friend, request));
            return true;
        };

        var status = new ProgressStatus(77, false, 1, 4);
        Assert.True(h.Campaign.SendTo(FriendB, MessageType.ProgressStatus, Channel.Events, w => ProgressStatusMessage.Write(w, status)));
        var request = new ProgressResend(77, 2, 3);
        Assert.True(h.Clients[FriendA].SendToHost(MessageType.ProgressResend, Channel.Events, w => ProgressResendMessage.Write(w, request)));
        h.Run(100 * Ms);

        Assert.Equal(new[] { (FriendB, status) }, statuses);
        Assert.Equal(new[] { (FriendA, request) }, requests);
        Assert.Equal(0, h.Sessions[FriendA].Link.Malformed);
        Assert.Equal(0, h.Sessions[FriendB].Link.Malformed);

        // Without a reader on the host (an older host's link), the request counts as malformed and nothing else happens.
        h.Campaign.FriendMessages = null;
        var writer = new BitWriter();
        new MessageHeader(MessageType.ProgressResend, 0, 0, 0).Write(writer);
        ProgressResendMessage.Write(writer, request);
        Assert.False(h.Campaign.Receive(FriendA, writer.AsSpan()));
        Assert.False(h.Campaign.Receive(999, writer.AsSpan()));
    }

    [Fact]
    public void A_run_copy_request_reaches_the_hosts_module_and_its_answer_the_friends()
    {
        // Friend to host on channel 1, the answer (a checkpoint) on channel 2.
        var h = new Harness(new FakeNetwork { LatencyMicros = 20 * Ms }, FriendA);
        h.JoinAll();
        var requests = new List<(ulong Friend, ProgressRunRequest Request)>();
        h.Campaign.FriendMessages = (ulong friend, MessageType type, ref BitReader reader) =>
        {
            if (type != MessageType.ProgressRunRequest || !ProgressRunRequestMessage.TryRead(ref reader, out ProgressRunRequest request))
            {
                return false;
            }

            requests.Add((friend, request));
            return true;
        };

        var copies = new List<ProgressRunCopy>();
        h.Clients[FriendA].OtherMessages = (MessageType type, ref BitReader reader) =>
        {
            if (type != MessageType.ProgressRunCopy || !ProgressRunCopyMessage.TryRead(ref reader, out ProgressRunCopy? copy))
            {
                return false;
            }

            copies.Add(copy!);
            return true;
        };

        var asked = new ProgressRunRequest(77, "run-a");
        Assert.True(h.Clients[FriendA].SendToHost(MessageType.ProgressRunRequest, Channel.Events, w => ProgressRunRequestMessage.Write(w, asked)));
        var checkpoint = new RunCheckpoint(4, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = new byte[] { 1, 2, 3 } });
        Assert.True(h.Campaign.SendTo(FriendA, MessageType.ProgressRunCopy, Channel.Bulk,
            w => Assert.True(ProgressRunCopyMessage.Write(w, new ProgressRunCopy(77, "run-a", RunCopyStatus.Copy, checkpoint)))));
        h.Run(100 * Ms);

        Assert.Equal(new[] { (FriendA, asked) }, requests);
        ProgressRunCopy got = Assert.Single(copies);
        Assert.Equal((77UL, "run-a", RunCopyStatus.Copy, checkpoint.Hash), (got.Session, got.Run, got.Status, got.Checkpoint!.Hash));
        Assert.Equal(0, h.Sessions[FriendA].Link.Malformed);
    }

    [Fact]
    public void Module_messages_nobody_reads_count_as_malformed()
    {
        var h = new Harness(new FakeNetwork(), FriendA);
        h.JoinAll();
        var writer = new BitWriter();
        new MessageHeader(MessageType.MenuClick, 0, 0, 0).Write(writer);
        writer.WriteByte(1);
        Assert.False(h.Clients[FriendA].Receive(HostId, writer.AsSpan()));
        Assert.Equal(1, h.Clients[FriendA].Malformed);
        h.Clients[FriendA].OtherMessages = (MessageType _, ref BitReader reader) => reader.ReadByte() == 1;
        Assert.True(h.Clients[FriendA].Receive(HostId, writer.AsSpan()));
    }

    [Fact]
    public void Messages_from_strangers_and_junk_are_refused()
    {
        var h = new Harness(new FakeNetwork(), FriendA);
        h.JoinAll();
        var writer = new BitWriter();
        new MessageHeader(MessageType.FollowReport, 0, 0, 0).Write(writer);
        FollowReportMessage.Write(writer, new FollowReport(1, FollowState.Following, 0, ""));
        Assert.False(h.Campaign.Receive(999, writer.AsSpan()));
        Assert.True(h.Campaign.Receive(FriendA, writer.AsSpan()));
        Assert.False(h.Clients[FriendA].Receive(999, writer.AsSpan()));
        Assert.False(h.Clients[FriendA].Receive(HostId, writer.AsSpan())); // a friend-to-host type from the host

        var random = new Random(8);
        for (int i = 0; i < 2000; i++)
        {
            byte[] junk = new byte[random.Next(0, 40)];
            random.NextBytes(junk);
            if (junk.Length > 0)
            {
                junk[0] = (byte)(i % 2 == 0 ? MessageType.HostView : MessageType.RunCheckpoint);
            }

            _ = h.Clients[FriendA].Receive(HostId, junk);
            _ = h.Campaign.Receive(FriendA, junk);
        }
    }

    /// <summary>A transport that refuses its first <see cref="Refuse"/> sends and keeps the rest.</summary>
    private sealed class RefusingTransport : ISessionTransport
    {
        public int Refuse { get; set; }

        public List<(ulong To, byte[] Bytes)> Sent { get; } = new();

        public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
        {
            if (Refuse > 0)
            {
                Refuse--;
                return false;
            }

            Sent.Add((peer, message.ToArray()));
            return true;
        }
    }

    private sealed class Harness
    {
        public Harness(FakeNetwork net, params ulong[] friends)
        {
            Net = net;
            FriendIds = friends;
            Host = new HostSession(Identity, net.For(HostId), maxFriends: 3);
            var members = new List<ulong> { HostId };
            members.AddRange(friends);
            Host.SetMembers(members);
            Campaign = new CampaignHost(net.For(HostId));
            Host.CampaignMessages = (sender, _, message, _) => Campaign.Receive(sender, message);
            foreach (ulong friend in friends)
            {
                NewFriend(friend);
            }
        }

        public FakeNetwork Net { get; }

        public ulong[] FriendIds { get; }

        public HostSession Host { get; }

        public CampaignHost Campaign { get; }

        public Dictionary<ulong, FriendSession> Sessions { get; } = new();

        public Dictionary<ulong, CampaignClient> Clients { get; } = new();

        public void RestartFriend(ulong friend) => NewFriend(friend);

        /// <summary>A new session to the host (a timed-out friend rejoining) with the same campaign client.</summary>
        public void RestartSession(ulong friend)
        {
            CampaignClient client = Clients[friend];
            Sessions[friend] = new FriendSession(Identity, HostId, InputKind.Gamepad, 7, Net.For(friend))
            {
                CampaignMessages = (sender, _, message, _) => client.Receive(sender, message),
            };
        }

        public void JoinAll()
        {
            foreach (ulong friend in FriendIds)
            {
                if (Sessions[friend].State == FriendSessionState.Idle)
                {
                    Sessions[friend].Start(Net.Now);
                }
            }

            for (int i = 0; i < 1000; i++)
            {
                Step(Ms);
                while (Host.TryTakeEvent(out HostEvent e))
                {
                    if (e.Kind == HostEventKind.JoinRequested)
                    {
                        Host.Accept(e.SteamId, 1, Net.Now);
                    }

                    if (e.Kind is HostEventKind.Joined or HostEventKind.Rejoined)
                    {
                        Campaign.AddFriend(e.SteamId);
                    }
                }

                if (Array.TrueForAll(FriendIds, f => Sessions[f].State == FriendSessionState.Joined))
                {
                    return;
                }
            }

            Assert.Fail("Friends didn't join.");
        }

        public void Run(long duration)
        {
            long end = Net.Now + duration;
            while (Net.Now < end)
            {
                Step(Ms);
            }
        }

        private void Step(long duration)
        {
            Net.Now += duration;
            Net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    Host.Receive(from, channel, bytes, Net.Now);
                }
                else if (Sessions.TryGetValue(to, out FriendSession? session))
                {
                    session.Receive(from, channel, bytes, Net.Now);
                }
            });
            Host.Update(Net.Now);
            Campaign.Update();
            foreach (ulong friend in FriendIds)
            {
                Sessions[friend].Update(Net.Now);
                Clients[friend].Update();
            }
        }

        private void NewFriend(ulong friend)
        {
            var client = new CampaignClient(Net.For(friend), HostId);
            Clients[friend] = client;
            Sessions[friend] = new FriendSession(Identity, HostId, InputKind.Gamepad, 7, Net.For(friend))
            {
                CampaignMessages = (sender, _, message, _) => client.Receive(sender, message),
            };
        }
    }
}
