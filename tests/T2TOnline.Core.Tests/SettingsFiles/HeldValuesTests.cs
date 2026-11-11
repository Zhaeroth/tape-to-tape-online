using System;
using System.Threading;
using System.Threading.Tasks;
using T2TOnline.Core.SettingsFiles;

namespace T2TOnline.Core.Tests.SettingsFiles;

public class HeldValuesTests
{
    [Fact]
    public void Without_a_tour_a_setting_is_written_as_ever()
    {
        Assert.Equal(TourSettingAction.Write, TourSettingHold.Decide(false, "ShareProgress"));
        Assert.Equal(TourSettingAction.Write, TourSettingHold.Decide(false, "AllowDirectConnections"));
    }

    [Theory]
    [InlineData("ShareAchievements")]
    [InlineData("ShareProgress")]
    [InlineData("FightGraceWindow")]
    [InlineData("AiStandIn")]
    [InlineData("NativeLobby")]
    [InlineData("OnlineInput")]
    [InlineData("CoopSlot")]
    public void During_a_tour_a_setting_is_held_in_memory(string name)
    {
        Assert.Equal(TourSettingAction.HoldInMemory, TourSettingHold.Decide(true, name));
    }

    [Fact]
    public void During_a_tour_direct_connections_never_change()
    {
        Assert.Equal("AllowDirectConnections", TourSettingHold.DirectConnections);
        Assert.Equal(TourSettingAction.Refuse, TourSettingHold.Decide(true, "AllowDirectConnections"));
        Assert.Equal(TourSettingAction.Refuse, TourSettingHold.Decide(true, "allowdirectconnections")); // whatever the case
    }

    [Fact]
    public void Nothing_is_held_at_first()
    {
        var held = new HeldValues();
        Assert.False(held.Any);
        Assert.False(held.TryGet("ShareProgress", out bool _));
        Assert.Empty(held.Drop());
    }

    [Fact]
    public void A_held_value_is_read_by_its_name_and_type()
    {
        var held = new HeldValues();
        held.Hold("ShareProgress", false);
        held.Hold("CoopSlot", 2);
        Assert.True(held.Any);
        Assert.True(held.TryGet("ShareProgress", out bool share));
        Assert.False(share);
        Assert.True(held.TryGet("CoopSlot", out int slot));
        Assert.Equal(2, slot);
        Assert.False(held.TryGet("CoopSlot", out bool _)); // not a switch
        Assert.False(held.TryGet("shareprogress", out bool _)); // names are exact
        Assert.False(held.TryGet("AiStandIn", out bool _));
    }

    [Fact]
    public void A_second_change_replaces_the_first()
    {
        var held = new HeldValues();
        held.Hold("NativeLobby", false);
        held.Hold("NativeLobby", true);
        Assert.True(held.TryGet("NativeLobby", out bool on));
        Assert.True(on);
        Assert.Equal(new[] { "NativeLobby" }, held.Drop());
    }

    [Fact]
    public void Dropping_lets_every_value_go_and_names_them_sorted()
    {
        var held = new HeldValues();
        held.Hold("ShareProgress", false);
        held.Hold("CoopSlot", 3);
        held.Hold("AiStandIn", false);
        Assert.Equal(new[] { "AiStandIn", "CoopSlot", "ShareProgress" }, held.Drop());
        Assert.False(held.Any);
        Assert.False(held.TryGet("CoopSlot", out int _));
        Assert.Empty(held.Drop());
    }

    [Fact]
    public void A_name_or_value_is_needed()
    {
        var held = new HeldValues();
        Assert.Throws<ArgumentNullException>(() => held.Hold(null!, true));
        Assert.Throws<ArgumentNullException>(() => held.Hold("ShareProgress", null!));
    }

    [Fact]
    public async Task Reads_during_changes_see_a_whole_map()
    {
        var held = new HeldValues();
        held.Hold("CoopSlot", 1);
        using var stop = new CancellationTokenSource();
        int bad = 0;
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!held.TryGet("CoopSlot", out int slot) || slot is < 1 or > 3)
                {
                    Interlocked.Increment(ref bad);
                }
            }
        });
        for (int i = 0; i < 20000; i++)
        {
            held.Hold("CoopSlot", (i % 3) + 1);
            held.Hold($"Other{i % 7}", true);
        }

        stop.Cancel();
        await reader;
        Assert.Equal(0, bad);
    }
}
