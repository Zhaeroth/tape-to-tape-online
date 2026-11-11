using System;
using System.Collections.Generic;

namespace T2TOnline.Steam.Tests;

[Collection(SharedSteamRuntime.Name)]
public class SteamFriendsNameReadTests
{
    private const ulong Sam = 76561199000000002;

    [Fact]
    public void Every_name_read_is_reported_and_returned_as_it_is()
    {
        var seen = new List<(ulong, string)>();
        Action<ulong, string>? before = SteamFriends.NameRead;
        SteamFriends.NameRead = (id, name) => seen.Add((id, name));
        try
        {
            Assert.Equal("Sam", SteamFriends.Read(Sam, "Sam"));
            Assert.Equal(string.Empty, SteamFriends.Read(Sam, string.Empty)); // nothing to report
            Assert.Equal("Riley", SteamFriends.Read(0, "Riley")); // no ID: nothing to report
            Assert.Equal(new[] { (Sam, "Sam") }, seen);
        }
        finally
        {
            SteamFriends.NameRead = before;
        }
    }

    [Fact]
    public void A_failing_callback_never_costs_the_name()
    {
        Action<ulong, string>? before = SteamFriends.NameRead;
        SteamFriends.NameRead = (_, _) => throw new InvalidOperationException("backstop down");
        try
        {
            Assert.Equal("Casey", SteamFriends.Read(Sam, "Casey"));
            Assert.Equal(string.Empty, SteamFriends.GetFriendPersonaName(Sam)); // without Steam: empty, and no callback
        }
        finally
        {
            SteamFriends.NameRead = before;
        }
    }
}
