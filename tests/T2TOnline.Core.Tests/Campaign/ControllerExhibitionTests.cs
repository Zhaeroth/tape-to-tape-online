using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Campaign;

public class ControllerExhibitionTests
{
    private static KeyValuePair<string, NetValue> V(string key, NetValue value) => new(key, value);

    /// <summary>Play Now's settings and teams as the host writes them (values only).</summary>
    internal static List<KeyValuePair<string, NetValue>> PlayNow(bool inRules = false) => new()
    {
        V(ControllerScreenState.ModeKey, NetValue.FromInt(ControllerScreenState.TeamSelectionMode)),
        V(ControllerScreenState.LockedKey, NetValue.FromBool(false)),
        V(ControllerScreenState.OnboardingKey, NetValue.FromBool(false)),
        V(ControllerScreenState.ReadyKey, NetValue.FromBool(false)),
        V(ControllerScreenState.GameModeKey, NetValue.FromInt(0)),
        V(ControllerScreenState.GameTypeKey, NetValue.FromInt(0)),
        V(ControllerScreenState.RulesPageKey, NetValue.FromBool(true)),
        V(ControllerScreenState.InRulesKey, NetValue.FromBool(inRules)),
        V(ControllerScreenState.HomePlayersKey, NetValue.FromInt(2)),
        V(ControllerScreenState.VisitorPlayersKey, NetValue.FromInt(1)),
        V(ControllerScreenState.RulesKey, NetValue.FromString("1,0,2,1,1,0")),
        V(ControllerScreenState.HomeTeamKey, NetValue.FromString("team-lumberjacks")),
        V(ControllerScreenState.HomeNameKey, NetValue.FromString("Lumberjacks")),
        V(ControllerScreenState.HomeTabKey, NetValue.FromInt(3)),
        V(ControllerScreenState.HomeColorKey, NetValue.FromBool(true)),
        V(ControllerScreenState.VisitorTeamKey, NetValue.FromString("team-ice-bears")),
        V(ControllerScreenState.VisitorNameKey, NetValue.FromString("Ice Bears")),
        V(ControllerScreenState.VisitorTabKey, NetValue.FromInt(2)),
        V(ControllerScreenState.VisitorColorKey, NetValue.FromBool(true)),
    };

    private static List<KeyValuePair<string, NetValue>> With(string key, NetValue value)
    {
        List<KeyValuePair<string, NetValue>> values = PlayNow();
        values.RemoveAll(v => v.Key == key);
        values.Add(V(key, value));
        return values;
    }

    /// <summary>What a host's custom team needs: the team and its logo.</summary>
    internal static ControllerContentData Content() => ControllerContentData.Of(new[]
    {
        new CustomRef(CustomContentKind.Team, 0x0123456789ABCDEF, "team-ice-bears"),
        new CustomRef(CustomContentKind.Logo, 0xFEDCBA9876543210, "custom_bears.png"),
    });

    [Theory]
    [InlineData(ControllerScreenState.CampaignMode, 0, 0, false, ControllerScreenKind.Campaign)]
    [InlineData(ControllerScreenState.TeamSelectionMode, 0, 0, false, ControllerScreenKind.PlayNow)]
    [InlineData(ControllerScreenState.SideSelectionMode, 0, 0, false, ControllerScreenKind.InMatch)]
    [InlineData(ControllerScreenState.CampaignMode, 0, 0, true, ControllerScreenKind.Tutorial)] // TutorialGameModeController: mode 2, onboarding
    [InlineData(ControllerScreenState.TeamSelectionMode, 2, 0, false, ControllerScreenKind.NotStreamed)] // the title's Shootout
    [InlineData(ControllerScreenState.TeamSelectionMode, 0, 3, false, ControllerScreenKind.NotStreamed)] // another game type
    public void Each_of_the_games_screens_is_told_apart(int mode, int gameMode, int gameType, bool onboarding, ControllerScreenKind kind)
    {
        ControllerScreenState state = ControllerScreenStateTests.Read(new[]
        {
            V(ControllerScreenState.ModeKey, NetValue.FromInt(mode)),
            V(ControllerScreenState.OnboardingKey, NetValue.FromBool(onboarding)),
            V(ControllerScreenState.GameModeKey, NetValue.FromInt(gameMode)),
            V(ControllerScreenState.GameTypeKey, NetValue.FromInt(gameType)),
        });

        Assert.Equal(kind, state.Kind);
    }

    [Fact]
    public void An_exhibition_screen_reads_back_its_teams_and_rules()
    {
        ControllerScreenState state = ControllerScreenStateTests.Read(PlayNow(inRules: true));

        Assert.Equal(ControllerScreenKind.PlayNow, state.Kind);
        Assert.True(state.RulesPage);
        Assert.True(state.InRules);
        Assert.Equal((2, 1), (state.HomePlayers, state.VisitorPlayers));
        Assert.Equal(new[] { 1, 0, 2, 1, 1, 0 }, state.Rules);
        Assert.Equal(new ControllerTeam("team-lumberjacks", "Lumberjacks", 3, true), state.HomeTeam);
        Assert.Equal(new ControllerTeam("team-ice-bears", "Ice Bears", 2, true), state.VisitorTeam);
    }

    [Fact]
    public void A_campaign_screen_says_nothing_of_teams_or_rules()
    {
        ControllerScreenState state = ControllerScreenStateTests.Read(ControllerScreenStateTests.Settings());

        Assert.Equal(ControllerScreenKind.Campaign, state.Kind);
        Assert.False(state.RulesPage);
        Assert.Empty(state.Rules);
        Assert.Equal((-1, -1), (state.HomePlayers, state.VisitorPlayers));
        Assert.False(state.HomeTeam.Known);
        Assert.False(state.VisitorTeam.Known);
    }

    [Fact]
    public void A_host_screen_of_twelve_icons_fits_a_menus_limits_with_its_rows()
    {
        // The exhibition's prefab has six players a side, and a text panel for its fallback (its teams, a status line, its lines).
        List<KeyValuePair<string, NetValue>> values = PlayNow();
        Assert.InRange(values.Count, 0, MenuData.MaxValues);
        var rows = new List<MenuItem>();
        rows.AddRange(Enumerable.Range(0, 12).Select(slot => ControllerScreenStateTests.Icon(slot, side: slot % 3)));
        rows.Add(new MenuItem("teams", new[] { V("text", NetValue.FromString("Ice Bears on the left side, Lumberjacks on the right side.")) }));

        ControllerScreenState state = ControllerScreenStateTests.Read(values, rows.ToArray());

        Assert.Equal(12, state.Icons.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void A_team_on_a_tab_out_of_range_is_no_team(int tab)
    {
        ControllerScreenState state = ControllerScreenStateTests.Read(With(ControllerScreenState.HomeTabKey, NetValue.FromInt(tab)));

        Assert.False(state.HomeTeam.Known);
        Assert.True(state.VisitorTeam.Known);
    }

    [Fact]
    public void A_team_id_too_long_is_no_team_and_a_name_too_long_is_none()
    {
        string tooLong = new('x', ControllerScreenState.MaxTeamChars + 1);

        Assert.False(ControllerScreenStateTests.Read(With(ControllerScreenState.HomeTeamKey, NetValue.FromString(tooLong))).HomeTeam.Known);
        ControllerTeam named = ControllerScreenStateTests.Read(With(ControllerScreenState.HomeNameKey, NetValue.FromString(tooLong))).HomeTeam;
        Assert.True(named.Known);
        Assert.Equal(string.Empty, named.Name);
    }

    [Theory]
    [InlineData(ControllerScreenState.GameModeKey, -1)]
    [InlineData(ControllerScreenState.GameModeKey, ControllerScreenState.MaxGameNumber + 1)]
    [InlineData(ControllerScreenState.GameTypeKey, -1)]
    [InlineData(ControllerScreenState.GameTypeKey, ControllerScreenState.MaxGameNumber + 1)]
    public void A_game_mode_out_of_the_games_range_is_refused(string key, int value)
    {
        Assert.False(ControllerScreenState.TryRead(new MenuData(With(key, NetValue.FromInt(value)), Array.Empty<MenuItem>()), out _, out string problem));
        Assert.NotEmpty(problem);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ControllerScreenState.MaxSidePlayers + 1)]
    public void A_players_count_out_of_range_says_nothing(int count)
    {
        Assert.Equal(-1, ControllerScreenStateTests.Read(With(ControllerScreenState.HomePlayersKey, NetValue.FromInt(count))).HomePlayers);
    }

    [Fact]
    public void Rules_round_trip()
    {
        int[] choices = { 1, 0, 2, 63, 1, 0 };

        Assert.Equal("1,0,2,63,1,0", ControllerRules.Format(choices));
        Assert.True(ControllerRules.TryParse(ControllerRules.Format(choices), out IReadOnlyList<int> read));
        Assert.Equal(choices, read);
        Assert.Equal("periods 1, minutes 0, skaters 2, difficulty 63, power-ups 1, injuries 0", ControllerRules.Describe(choices));
        Assert.Equal("not known", ControllerRules.Describe(Array.Empty<int>()));
    }

    [Theory]
    [InlineData(1, 2, 3, 4, 5)] // too few
    [InlineData(1, 2, 3, 4, 5, 6, 7)] // too many
    [InlineData(-1, 0, 0, 0, 0, 0)] // a list with nothing chosen yet
    [InlineData(64, 0, 0, 0, 0, 0)] // over the highest choice
    public void Rules_that_are_not_the_pages_format_to_nothing(params int[] choices)
    {
        Assert.Equal(string.Empty, ControllerRules.Format(choices));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3,4,5")]
    [InlineData("1,2,3,4,5,6,7")]
    [InlineData("1,2,3,4,5,x")]
    [InlineData("1,2,3,4,5,-1")]
    [InlineData("1,2,3,4,5,+1")]
    [InlineData("1,2,3,4,5, 1")]
    [InlineData("1,2,3,4,5,64")]
    [InlineData("1,2,3,4,5,99999999999999999999")]
    public void Rules_that_do_not_read_are_refused(string? text)
    {
        Assert.False(ControllerRules.TryParse(text, out IReadOnlyList<int> read));
        Assert.Empty(read);
    }

    [Fact]
    public void Rules_that_do_not_read_leave_the_screen_with_none()
    {
        Assert.Empty(ControllerScreenStateTests.Read(With(ControllerScreenState.RulesKey, NetValue.FromString("nonsense"))).Rules);
    }

    [Fact]
    public void A_team_panel_changes_only_what_differs()
    {
        var want = new ControllerTeam("a", "A", 3, true);

        Assert.Equal(ControllerTeamChange.None, ControllerExhibitionPlan.ForTeam(new ControllerTeamShown("a", 3, true), want));
        Assert.Equal(ControllerTeamChange.Team, ControllerExhibitionPlan.ForTeam(new ControllerTeamShown("b", 3, true), want));
        Assert.Equal(ControllerTeamChange.Team, ControllerExhibitionPlan.ForTeam(new ControllerTeamShown("a", 3, false), want));
        Assert.Equal(ControllerTeamChange.Tab, ControllerExhibitionPlan.ForTeam(new ControllerTeamShown("a", 0, true), want));
        Assert.Equal(ControllerTeamChange.Team | ControllerTeamChange.Tab, ControllerExhibitionPlan.ForTeam(new ControllerTeamShown(string.Empty, -1, false), want));
    }

    [Fact]
    public void A_side_the_host_names_no_team_for_is_left_alone()
    {
        Assert.Equal(ControllerTeamChange.None, ControllerExhibitionPlan.ForTeam(new ControllerTeamShown("b", 1, false), default));
    }

    [Theory]
    [InlineData(false, false, false, ControllerRulesStep.None)]
    [InlineData(true, true, true, ControllerRulesStep.None)]
    [InlineData(true, false, true, ControllerRulesStep.Show)]
    [InlineData(true, false, false, ControllerRulesStep.Wait)] // its panel loads in the background here
    [InlineData(false, true, true, ControllerRulesStep.Hide)]
    [InlineData(false, true, false, ControllerRulesStep.Hide)]
    public void The_rules_page_follows_the_hosts(bool hostIn, bool hereIn, bool loaded, ControllerRulesStep step)
    {
        Assert.Equal(step, ControllerExhibitionPlan.ForRules(hostIn, hereIn, loaded));
    }

    [Fact]
    public void Only_a_rule_this_pc_has_and_shows_otherwise_is_set()
    {
        int[] host = { 1, 0, 3, 1, 1, 0 };
        int[] shown = { 1, 2, 0, 1, 0, 0 };
        int[] counts = { 3, 3, 2, 3, 2, 2 }; // skaters: fewer choices here until the host's counts are in

        Assert.Equal(new[] { (1, 0), (4, 1) }, ControllerExhibitionPlan.RuleChanges(host, shown, counts));
        Assert.Empty(ControllerExhibitionPlan.RuleChanges(host, host, counts));
        Assert.Empty(ControllerExhibitionPlan.RuleChanges(Array.Empty<int>(), shown, counts));
    }

    [Fact]
    public void The_custom_content_list_round_trips_as_an_attachment()
    {
        MenuAttachment attachment = MenuAttachment.ControllerContent(7, Content());

        Assert.Equal(MenuAttachmentKind.ControllerContent, attachment.Kind);
        Assert.Equal(MenuAttachment.ControllerContentFormat, attachment.Format);
        Assert.True(MenuAttachment.IsKnown(MenuAttachmentKind.ControllerContent));
        Assert.True(attachment.TryGetControllerContent(out ControllerContentData? read));
        Assert.Equal(Content().Refs, read!.Refs);
        Assert.False(attachment.TryGetNurseSteps(out _));
        Assert.Contains("controller screen's custom content for #7", attachment.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_custom_content_list_is_capped()
    {
        CustomRef[] many = Enumerable.Range(0, CustomContentValues.MaxRefs + 1).Select(i => new CustomRef(CustomContentKind.Skater, (ulong)i, $"s{i}")).ToArray();

        Assert.Throws<ArgumentException>(() => ControllerContentData.Of(many));
        Assert.Equal(CustomContentValues.MaxRefs, ControllerContentData.Of(many.Take(CustomContentValues.MaxRefs).ToArray()).Refs.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.ControllerContent(0, Content()));
    }

    [Fact]
    public void A_list_saying_it_holds_more_than_the_cap_or_a_bad_reference_is_refused()
    {
        var writer = new BitWriter();
        writer.WriteByte(CustomContentValues.MaxRefs + 1);
        var over = new BitReader(writer.AsSpan());
        Assert.False(ControllerContentData.TryRead(ref over, out _));

        writer.Reset();
        writer.WriteByte(1);
        writer.WriteShortString("not a reference");
        var bad = new BitReader(writer.AsSpan());
        Assert.False(ControllerContentData.TryRead(ref bad, out _));

        writer.Reset();
        writer.WriteByte(2);
        writer.WriteShortString(Content().Refs[0].Format());
        var cut = new BitReader(writer.AsSpan());
        Assert.False(ControllerContentData.TryRead(ref cut, out _));
    }

    [Fact]
    public void Random_bytes_never_throw_the_list_reader()
    {
        var random = new Random(0xC6);
        for (int i = 0; i < 2000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);
            var reader = new BitReader(bytes);
            _ = ControllerContentData.TryRead(ref reader, out _);
        }
    }

    [Fact]
    public void Random_text_never_throws_the_rules_reader()
    {
        var random = new Random(0xC7);
        const string alphabet = "0123456789,- +x";
        for (int i = 0; i < 2000; i++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            if (ControllerRules.TryParse(text, out IReadOnlyList<int> read))
            {
                Assert.Equal(ControllerRules.Count, read.Count);
                Assert.All(read, choice => Assert.InRange(choice, 0, ControllerRules.MaxChoice));
            }
        }
    }
}
