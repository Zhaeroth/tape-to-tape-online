using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class TitleViewTests
{
    private const int Main = (int)TitleGroup.Main;
    private const int Campaign = (int)TitleGroup.Campaign;
    private const int Play = (int)TitleGroup.Play;
    private const int PressToStart = (int)TitleGroup.PressToStart;

    /// <summary>The host's title view with its scene detail and these title details.</summary>
    public static HostView View(TitleViewDetails details, uint epoch = 4) =>
        new(epoch, "title", 0, new[] { new KeyValuePair<string, NetValue>("scene", NetValue.FromString("start_screen")) }.Concat(details.ToValues()).ToList());

    private static TitleState Still(int group, string? button = null) => new(group, Moving: false, MovingTo: group, InputReady: true, button, Busy: false);

    [Fact]
    public void The_details_ride_in_the_title_view_and_read_back()
    {
        var details = new TitleViewDetails(Campaign, "continueRun");
        HostView view = View(details);
        Assert.Equal(details, TitleViewDetails.Read(view));

        // Through the wire, as every view goes.
        var writer = new BitWriter();
        HostViewMessage.Write(writer, view);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(HostViewMessage.TryRead(ref reader, out HostView? read));
        Assert.Equal(details, TitleViewDetails.Read(read!));

        // Absent ones read as unknown; the title view of an older host has none.
        Assert.Equal(new TitleViewDetails(null, null), TitleViewDetails.Read(View(new TitleViewDetails(null, null))));
        Assert.Empty(new TitleViewDetails(null, null).ToValues());
        Assert.Equal(new TitleViewDetails(Main, null), TitleViewDetails.Read(View(new TitleViewDetails(Main, null))));
    }

    [Fact]
    public void Malformed_details_read_as_unknown()
    {
        HostView Raw(NetValue group, NetValue button) => new(1, "title", 0, new[]
        {
            new KeyValuePair<string, NetValue>(TitleViewDetails.GroupKey, group),
            new KeyValuePair<string, NetValue>(TitleViewDetails.ButtonKey, button),
        });

        Assert.Equal(new TitleViewDetails(null, null), TitleViewDetails.Read(Raw(NetValue.FromInt(TitleViewDetails.MaxGroup + 1), NetValue.FromString("a b"))));
        Assert.Equal(new TitleViewDetails(null, null), TitleViewDetails.Read(Raw(NetValue.FromInt(-1), NetValue.FromString(new string('a', TitleViewDetails.MaxButtonChars + 1)))));
        Assert.Equal(new TitleViewDetails(null, null), TitleViewDetails.Read(Raw(NetValue.FromString("2"), NetValue.FromInt(3))));
        Assert.Equal(new TitleViewDetails(TitleViewDetails.MaxGroup, "x"), TitleViewDetails.Read(Raw(NetValue.FromInt(TitleViewDetails.MaxGroup), NetValue.FromString("x"))));

        // Details out of range aren't even written.
        Assert.Empty(new TitleViewDetails(-1, "new run").ToValues());
        Assert.Empty(new TitleViewDetails(16, string.Empty).ToValues());
        Assert.False(TitleViewDetails.IsName("continue\nRun"));
        Assert.True(TitleViewDetails.IsName("continueRun"));
    }

    [Fact]
    public void Only_the_button_is_a_cursor_move()
    {
        Assert.Contains(TitleViewDetails.ButtonKey, SceneViewKeys.Transient);
        Assert.DoesNotContain(TitleViewDetails.GroupKey, SceneViewKeys.Transient);

        HostView a = View(new TitleViewDetails(Campaign, "newRun"));
        HostView b = View(new TitleViewDetails(Campaign, "continueRun"));
        HostView c = View(new TitleViewDetails(Play, "playNow"));
        Assert.True(b.SameExcept(a, SceneViewKeys.Transient));
        Assert.False(c.SameExcept(a, SceneViewKeys.Transient));
        Assert.True(c.SameExcept(a, TitleViewDetails.Keys)); // the same title screen, wherever its cursor is
    }

    [Fact]
    public void A_title_moves_to_the_hosts_group_then_its_button()
    {
        var host = new TitleViewDetails(Campaign, "continueRun");

        // Still at "press start": the game's own press-start first (it goes to the main buttons).
        Assert.Equal(TitleStep.PressStart, TitleFollow.Next(host, Still(PressToStart)));

        // On another group: the host's group.
        Assert.Equal(TitleStep.Group, TitleFollow.Next(host, Still(Main, "campaign")));

        // Moving there: wait until the move is over (the game selects the group's default button as it ends).
        Assert.Equal(TitleStep.None, TitleFollow.Next(host, new TitleState(Main, true, Campaign, false, null, false)));
        Assert.Equal(TitleStep.None, TitleFollow.Next(host, new TitleState(Main, true, Campaign, true, null, false)));

        // There, on another button: the host's button; on it: nothing.
        Assert.Equal(TitleStep.Button, TitleFollow.Next(host, Still(Campaign, "newRun")));
        Assert.Equal(TitleStep.Button, TitleFollow.Next(host, Still(Campaign)));
        Assert.Equal(TitleStep.None, TitleFollow.Next(host, Still(Campaign, "continueRun")));

        // Moving somewhere else: the host's group again once the move takes input; until then nothing (the game's own
        // change does nothing then).
        Assert.Equal(TitleStep.None, TitleFollow.Next(host, new TitleState(Campaign, true, Play, false, null, false)));
        Assert.Equal(TitleStep.Group, TitleFollow.Next(host, new TitleState(Campaign, true, Play, true, null, false)));
    }

    [Fact]
    public void Nothing_moves_without_a_known_group_or_on_a_busy_title()
    {
        Assert.Equal(TitleStep.None, TitleFollow.Next(null, Still(Main)));
        Assert.Equal(TitleStep.None, TitleFollow.Next(new TitleViewDetails(null, "campaign"), Still(Main)));
        Assert.Equal(TitleStep.None, TitleFollow.Next(new TitleViewDetails(Campaign, "newRun"), Still(Main) with { Busy = true }));

        // "Press start" on the host's screen is never followed back: a friend's title stays on its buttons.
        Assert.Equal(TitleStep.None, TitleFollow.Next(new TitleViewDetails(PressToStart, null), Still(Main, "campaign")));

        // A group the game has none of (readable from the network, up to MaxGroup) is never handed to its group change
        // (the last one it has still is).
        Assert.Equal(TitleStep.None, TitleFollow.Next(new TitleViewDetails((int)TitleGroup.Customize + 1, "x"), Still(Main, "campaign")));
        Assert.Equal(TitleStep.None, TitleFollow.Next(new TitleViewDetails(TitleViewDetails.MaxGroup, null), Still(PressToStart)));
        Assert.Equal(TitleStep.Group, TitleFollow.Next(new TitleViewDetails((int)TitleGroup.Customize, null), Still(Main, "campaign")));

        // A group with no button known (a menu over the host's title): the group only.
        Assert.Equal(TitleStep.None, TitleFollow.Next(new TitleViewDetails(Main, null), Still(Main, "play")));
        Assert.Equal(TitleStep.Group, TitleFollow.Next(new TitleViewDetails(Main, null), Still(Campaign, "newRun")));
    }

    [Fact]
    public void Every_state_settles_on_the_hosts_screen()
    {
        // Played out as the game does it: press-start goes to Main through a move; a group change moves; a move ends where it
        // was going and selects the group's first button; a button selection lands.
        string[] first = { "campaign", "campaign", "newRun", "playNow", "tutorial", "customizeTeams" };
        foreach (int target in Enumerable.Range(Main, 5))
        {
            foreach (int start in Enumerable.Range(PressToStart, 6))
            {
                var host = new TitleViewDetails(target, target == Campaign ? "unlockables" : first[target]);
                TitleState here = Still(start, start == PressToStart ? null : first[start]);
                int steps = 0;
                for (TitleStep step = TitleFollow.Next(host, here); step != TitleStep.None; step = TitleFollow.Next(host, here))
                {
                    here = step switch
                    {
                        TitleStep.PressStart => Still(Main, first[Main]),
                        TitleStep.Group => Still(host.Group!.Value, first[host.Group.Value]),
                        _ => here with { Button = host.Button },
                    };
                    Assert.True(++steps <= 3, $"from {start} to {target}");
                }

                Assert.Equal((target, host.Button), (here.Group, here.Button));
            }
        }
    }
}
