namespace T2TOnline.Steam.Tests;

/// <summary>The shared watch is process-wide state, so these run with the other tests that use process-wide state.</summary>
[Collection(SharedSteamRuntime.Name)]
public class SteamOverlayWatchTests
{
    [Fact]
    public void The_overlay_is_not_known_to_be_open_until_an_event_says_so()
    {
        var watch = new SteamOverlayWatch();

        Assert.False(watch.Active);
        Assert.False(watch.TryTakeClosed());

        watch.Set(true);
        Assert.True(watch.Active);
        Assert.Equal(1, watch.Opened);
        Assert.False(watch.TryTakeClosed());

        watch.Set(true); // a repeat changes nothing
        Assert.Equal(1, watch.Opened);

        watch.Set(false);
        Assert.False(watch.Active);
        Assert.True(watch.TryTakeClosed());
        Assert.False(watch.TryTakeClosed()); // told once

        watch.Set(false);
        Assert.False(watch.TryTakeClosed());
        watch.Set(true);
        Assert.Equal(2, watch.Opened);
    }

    [Fact]
    public void An_overlay_whose_close_was_never_heard_stops_being_believed_after_ten_minutes()
    {
        long now = 1_000;
        var watch = new SteamOverlayWatch(() => now);

        watch.Set(true);
        Assert.True(watch.IsOpen);
        now += (long)SteamOverlayWatch.MaxOpen.TotalMilliseconds;
        Assert.True(watch.IsOpen);
        now += 1;
        Assert.False(watch.IsOpen);
        Assert.True(watch.Active); // the raw last event still says open

        watch.Set(false);
        Assert.False(watch.IsOpen);
        watch.Set(true); // the next opening is believed again from its own start
        Assert.True(watch.IsOpen);
    }

    [Fact]
    public void The_shared_watch_is_the_one_steam_overlay_reads()
    {
        bool before = SteamOverlayWatch.Shared.Active;
        try
        {
            SteamOverlayWatch.Shared.Set(true);
            Assert.True(SteamOverlay.IsOpen);
        }
        finally
        {
            SteamOverlayWatch.Shared.Set(before);
            SteamOverlayWatch.Shared.TryTakeClosed();
        }
    }
}
