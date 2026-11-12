using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;

namespace T2TOnline.Core.Tests.Session;

/// <summary>
/// A link keeps every round trip for the session (<see cref="PeerLink.RoundTrips"/>), so the host can say a departing
/// friend's percentiles, while the report's window (<see cref="PeerLink.RoundTripMs"/>) is cleared
/// every five seconds.
/// </summary>
public class LinkRoundTripTests
{
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const long Ms = 1000;

    [Fact]
    public void The_sessions_round_trips_outlive_the_reports_window()
    {
        var identity = new SessionIdentity(ProtocolInfo.Version, "0.8.0-rc", 0x1122334455667788);
        var net = new FakeNetwork { LatencyMicros = 20 * Ms };
        var host = new HostSession(identity, net.For(HostId));
        host.SetMembers(new[] { HostId, FriendId });
        var friend = new FriendSession(identity, HostId, InputKind.Gamepad, 1, net.For(FriendId));
        friend.Start(net.Now);
        for (int i = 0; i < 3000; i++)
        {
            net.Now += Ms;
            net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    host.Receive(from, channel, bytes, net.Now);
                }
                else
                {
                    friend.Receive(from, channel, bytes, net.Now);
                }
            });
            host.Update(net.Now);
            friend.Update(net.Now);
            while (host.TryTakeEvent(out HostEvent e))
            {
                if (e.Kind == HostEventKind.JoinRequested)
                {
                    host.Accept(e.SteamId, 1, net.Now);
                }
            }

            if (i == 1500)
            {
                host.Find(FriendId)!.Link.RoundTripMs.Clear(); // a report
            }
        }

        PeerLink link = host.Find(FriendId)!.Link;
        Assert.True(link.RoundTrips.Count > link.RoundTripMs.Count);
        Assert.InRange(link.RoundTrips.Percentile(0.5), 40 * Ms, 41 * Ms); // to the 250 µs bucket's edge
        Assert.Equal(link.RoundTrips.Percentile(0.5), link.RoundTrips.Percentile(1));
        Assert.True(friend.Link.RoundTrips.Count > 0);
        Assert.Equal(0, link.Malformed);
    }
}
