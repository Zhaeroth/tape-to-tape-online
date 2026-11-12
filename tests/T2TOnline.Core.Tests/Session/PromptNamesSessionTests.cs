using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Tests.Protocol;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// A friend's button prompt names reach the host (<see cref="MessageType.PromptNames"/>): after every welcome, after their
/// controls, again when they change; taken at most once a second; a bad table counted and dropped.
/// </summary>
public class PromptNamesSessionTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const long Ms = 1000;
    private const long Second = 1_000_000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.8.0-rc", 0x1122334455667788);

    [Fact]
    public void Names_set_before_the_welcome_go_right_after_it_after_the_controls()
    {
        var h = new Harness();
        h.Friend.SetBindings(InputMessagesTests.PadSet());
        h.Friend.SetPromptNames(PromptNamesMessageTests.Sample());
        h.JoinNow();

        List<HostEvent> events = h.TakeHostEvents();
        int controls = events.FindIndex(e => e.Kind == HostEventKind.BindingsChanged);
        int names = events.FindIndex(e => e.Kind == HostEventKind.PromptNamesChanged);
        Assert.True(controls >= 0 && names > controls);
        PromptNameTable table = h.Host.Find(FriendId)!.PromptNames!;
        Assert.Equal((1, 3, 5), (table.Family, table.Generation, table.Entries.Count));
        Assert.Equal(1, h.Friend.PromptNamesSent);
        Assert.Equal(0, h.Host.Find(FriendId)!.Link.Malformed);
    }

    [Fact]
    public void Names_set_while_joined_go_at_once_and_the_newest_wins_once_a_second()
    {
        var h = new Harness();
        h.JoinNow();
        Assert.Null(h.Host.Find(FriendId)!.PromptNames);
        h.TakeHostEvents();

        h.Friend.SetPromptNames(PromptNamesMessageTests.Sample(generation: 1));
        h.Run(50 * Ms);
        h.Friend.SetPromptNames(PromptNamesMessageTests.Sample(generation: 2));
        h.Run(10 * Ms);
        h.Friend.SetPromptNames(PromptNamesMessageTests.Sample(family: 2, generation: 3));
        h.Run(100 * Ms);

        Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.PromptNamesChanged);
        Assert.Equal(1, h.Host.Find(FriendId)!.PromptNames!.Generation);
        h.Run(Second);
        Assert.Single(h.TakeHostEvents(), e => e.Kind == HostEventKind.PromptNamesChanged);
        Assert.Equal((2, 3), (h.Host.Find(FriendId)!.PromptNames!.Family, h.Host.Find(FriendId)!.PromptNames!.Generation));
        Assert.Equal(3, h.Friend.PromptNamesSent);
    }

    [Fact]
    public void A_repeated_hello_starts_them_over_and_the_friend_sends_them_again()
    {
        var h = new Harness();
        h.Friend.SetPromptNames(PromptNamesMessageTests.Sample());
        h.JoinNow();
        Assert.NotNull(h.Host.Find(FriendId)!.PromptNames);

        // Their session restarted (a new FriendSession, the same names made again): the host forgets the old table, and
        // the new one comes after the new welcome.
        h.Friend = h.NewFriend();
        h.Friend.SetPromptNames(PromptNamesMessageTests.Sample(generation: 9));
        h.Friend.Start(h.Net.Now);
        h.Run(15 * Ms);
        Assert.Null(h.Host.Find(FriendId)!.PromptNames); // the hello arrived; the names wait for the welcome's answer
        h.Run(2 * Second);
        Assert.Equal(9, h.Host.Find(FriendId)!.PromptNames!.Generation);
    }

    [Fact]
    public void Bad_or_oversized_names_are_counted_and_the_table_before_stays()
    {
        var h = new Harness();
        h.Friend.SetPromptNames(PromptNamesMessageTests.Sample());
        h.JoinNow();
        RemoteFriend friend = h.Host.Find(FriendId)!;
        h.Run(2 * Second);

        h.Host.Receive(FriendId, Channel.Events, Message(new byte[] { 2, 1, 1, 0, 0 }), h.Net.Now); // format 2
        h.Run(2 * Second);
        h.Host.Receive(FriendId, Channel.Events, Message(new byte[PromptNamesMessage.MaxPayloadBytes + 1]), h.Net.Now);
        h.Host.Receive(FriendId, Channel.Realtime, Message(new byte[] { 1, 1, 1, 0, 0 }), h.Net.Now); // the wrong channel
        h.Run(2 * Second);

        Assert.Equal(3, friend.BadPromptNames);
        Assert.Equal(3, friend.Link.Malformed);
        Assert.Equal(3, friend.PromptNames!.Generation);
    }

    [Fact]
    public void Names_from_someone_not_joined_are_counted_not_taken()
    {
        var h = new Harness();
        h.Friend.Start(h.Net.Now);
        h.Run(20 * Ms); // waiting for the host's answer
        h.Host.Receive(FriendId, Channel.Events, Message(Payload(PromptNamesMessageTests.Sample())), h.Net.Now);
        Assert.Equal(1, h.Host.Find(FriendId)!.Link.Malformed);
        Assert.Null(h.Host.Find(FriendId)!.PromptNames);
    }

    [Fact]
    public void A_flood_is_taken_at_most_once_a_second()
    {
        var h = new Harness();
        h.JoinNow();
        h.TakeHostEvents();
        for (int i = 0; i < 300; i++)
        {
            h.Friend.SetPromptNames(PromptNamesMessageTests.Sample(generation: (ushort)(i + 1)));
            h.Run(10 * Ms);
        }

        h.Run(Second);
        Assert.InRange(h.TakeHostEvents().Count(e => e.Kind == HostEventKind.PromptNamesChanged), 1, 5);
        Assert.Equal(300, h.Host.Find(FriendId)!.PromptNames!.Generation); // the newest, once its second came
    }

    private static byte[] Payload(PromptNameTable table)
    {
        var writer = new BitWriter();
        PromptNamesMessage.Write(writer, table);
        return writer.ToArray();
    }

    private static byte[] Message(byte[] payload)
    {
        var writer = new BitWriter();
        new MessageHeader(MessageType.PromptNames, 0, 0, 0).Write(writer);
        writer.WriteBytes(payload);
        return writer.ToArray();
    }

    /// <summary>A host and one pad friend on a <see cref="FakeNetwork"/>.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            Net = new FakeNetwork { LatencyMicros = 10 * Ms };
            Host = new HostSession(Identity, Net.For(HostId));
            Host.SetMembers(new[] { HostId, FriendId });
            Friend = NewFriend();
        }

        public FakeNetwork Net { get; }

        public HostSession Host { get; }

        public FriendSession Friend { get; set; }

        public FriendSession NewFriend() => new(Identity, HostId, InputKind.Gamepad, 1, Net.For(FriendId));

        public void JoinNow()
        {
            Friend.Start(Net.Now);
            for (int i = 0; i < 1000 && Friend.State == FriendSessionState.Connecting; i++)
            {
                Run(Ms);
                foreach (HostEvent e in TakeHostEvents().Where(e => e.Kind == HostEventKind.JoinRequested))
                {
                    Host.Accept(e.SteamId, 1, Net.Now);
                }
            }

            Assert.Equal(FriendSessionState.Joined, Friend.State);
            Run(50 * Ms);
        }

        public void Run(long duration)
        {
            long end = Net.Now + duration;
            while (Net.Now < end)
            {
                Net.Now += Ms;
                Net.Deliver((from, to, channel, bytes) =>
                {
                    if (to == HostId)
                    {
                        Host.Receive(from, channel, bytes, Net.Now);
                    }
                    else if (to == FriendId)
                    {
                        Friend.Receive(from, channel, bytes, Net.Now);
                    }
                });
                Host.Update(Net.Now);
                Friend.Update(Net.Now);
            }
        }

        public List<HostEvent> TakeHostEvents()
        {
            var events = new List<HostEvent>();
            while (Host.TryTakeEvent(out HostEvent e))
            {
                events.Add(e);
            }

            return events;
        }
    }
}
