using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

/// <summary>
/// Steam's networking debug output: without Steam nothing registers and nothing throws; what Steam hands the function (a
/// UTF-8 C string, on any thread) reaches the handler, and nothing a handler does reaches Steam.
/// </summary>
[Collection(SharedSteamRuntime.Name)]
public class SteamDebugOutputTests
{
    [Fact]
    public void Without_steam_nothing_is_registered_and_nothing_throws()
    {
        Assert.False(SteamDebugOutput.Start(SteamNetworkingDebugOutputType.Verbose, (_, _) => { }));
        Assert.False(SteamDebugOutput.Stop());
        Assert.False(SteamDebugOutput.Start(SteamNetworkingDebugOutputType.None, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => SteamDebugOutput.Start(SteamNetworkingDebugOutputType.Verbose, null!));
    }

    [Fact]
    public void A_line_from_steam_reaches_the_handler_as_text_and_none_after_stop()
    {
        var lines = new List<(SteamNetworkingDebugOutputType, string)>();
        nint message = Marshal.StringToCoTaskMemUTF8("[#1 P2P] ICE failed 3998 ICE not enabled by local user options\n");
        try
        {
            SteamDebugOutput.SetHandlerForTest((type, text) => lines.Add((type, text)));
            SteamDebugOutput.Deliver(SteamNetworkingDebugOutputType.Msg, message);
            SteamDebugOutput.Deliver(SteamNetworkingDebugOutputType.Msg, 0);

            Assert.Equal(new[] { (SteamNetworkingDebugOutputType.Msg, "[#1 P2P] ICE failed 3998 ICE not enabled by local user options\n") }, lines);

            SteamDebugOutput.Stop(); // drops the handler even though Steam can't be told
            SteamDebugOutput.Deliver(SteamNetworkingDebugOutputType.Msg, message);
            Assert.Single(lines);
        }
        finally
        {
            SteamDebugOutput.SetHandlerForTest(null);
            Marshal.FreeCoTaskMem(message);
        }
    }

    [Fact]
    public void A_handler_that_throws_never_reaches_steam()
    {
        nint message = Marshal.StringToCoTaskMemUTF8("a line");
        try
        {
            long before = SteamDebugOutput.Faults;
            SteamDebugOutput.SetHandlerForTest((_, _) => throw new InvalidOperationException("a broken handler"));

            SteamDebugOutput.Deliver(SteamNetworkingDebugOutputType.Verbose, message);

            Assert.Equal(before + 1, SteamDebugOutput.Faults);
        }
        finally
        {
            SteamDebugOutput.SetHandlerForTest(null);
            Marshal.FreeCoTaskMem(message);
        }
    }
}
