using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>
/// The identifier shape is taken from what honest hosts send: every id kind the mod's logs have held (menu ids, classes and
/// scenes; element names as Unity makes them; view screens, keys and values; NPC lines and pools; events; squads; term keys;
/// asset and run ids) reads as it is, so an honest host's lines read as before. Anything else, a name or a sentence a modified
/// host sends, reads as its length.
/// </summary>
public class HostTextIdsTests
{
    // Every kind of identifier but the asset and run ids, as the mod's log lines hold them.
    private static readonly string[] Seen =
    {
        "+0", "+1", "+10", "+11", "+12", "+18", "+2", "+20", "+21", "+24", "+3", "+4", "+48", "+6", "+7", "+8", "+9", "AnimatedButton_MainMenu(Clone)",
        "AssignControllersButton", "BenchwarmerMenu", "BlademasterDialoguePool", "BlademasterEvent", "BrawlFightMenu", "BrokenSticks", "ChaosDialoguePool",
        "ChaosEvent3Choice", "ChooseMetaMenu", "CoachEvent", "ControllerAssignment", "ControllerAssignmentCampaign", "ControllerAssignmentMenu",
        "CustomizableYesNoPrompt", "CycleListNoTitle_Difficulty", "CycleListNoTitle_GameMode", "Effects", "EventChoice", "EventChoiceItemCoach(Clone)",
        "EventChoiceItem_ChaosTalentRight(Clone)", "EventChoiceMenu", "FanNumber1Event", "Game", "GameMenu", "Glossary", "GlossaryMenu", "Lineup",
        "LineupMenu", "LineupPlayerButton(Clone)", "MetaTeamInfo(Clone)", "NurseDialoguePool", "NurseEvent", "OldSquadMenu", "PermanentUnlockMenu",
        "PowerUpDetails(Clone)", "RelicDetails(Clone)", "RelicRewardMenu", "RunEnd", "RunEndMenu", "Settings", "SettingsMenu",
        "SkaterDetails_OldSchool(Clone)", "SkaterPowerupReward", "SkaterPowerupRewardMenu", "SkipHiddenInput (1)", "Start", "StickPool", "Sticks",
        "TeamTraining", "Temp_EquipmentAdvice", "Tutorial", "UIInputShortcut_Next", "UIInputShortcut_V2", "Visit_01", "Visit_04", "Visit_06", "WorldMap",
        "WorldMapMenu", "act", "camX", "chaos3Choice", "coachEvent", "defense", "enter", "enterNo", "fanEvent", "intro", "introGo", "map", "match", "node",
        "nurseEvent", "oldschool", "online", "opp.0", "opp.1", "opp.2", "opp.3", "opp.4", "oppGoalie", "path", "preview", "reward.ability",
        "reward.relic", "reward.talent", "run", "scene", "sel", "standard_game", "start_screen", "tip", "title", "titleButton", "titleGroup", "type",
        "v2ui/world_map_menu/concede/title", "vanilla", "violence", "world_map", "zoom",

        // Map points and paths (a view's node, cursor and path), and the asset and run ids' shapes (a skater's GUID, a
        // goalie's 32 hex digits, a relic's GUID), with made-up digits.
        "-1,-1", "0,0", "0,7", "1,6", "0,0;0,1", "0,0;1,1;1,2;0,3;1,4;0,5;1,6",
        "00000000-0000-4000-8000-000000000001", "0123456789abcdef0123456789abcdef", "relic_bean_diet",

        // Shared progress as the logs hold it: squads, NPC lines marked seen, an unlock's and a run's GUID shape.
        "tchel", "chaosNode/visit_4", "nurseNode", "00000000-0000-4000-8000-0000000000aa",

        // The longest of the game's term keys (84 characters).
        "settings/control_mapper/joystick_element_assignment_polling_window_message_full_axis",
    };

    public static IEnumerable<object[]> SeenIds() => Seen.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(SeenIds))]
    public void Every_id_an_honest_host_sends_reads_as_it_is(string id)
    {
        Assert.True(HostText.IsKey(id), id);
        Assert.Equal(id, HostText.KeyForLog(id));
    }

    [Theory]
    [InlineData("Riley Casey")]
    [InlineData("Alex (Sam)")]
    [InlineData("Casey's team")]
    [InlineData("Riley (Clone) Casey")]
    [InlineData("Name (1) more")]
    [InlineData("(Clone)")]
    [InlineData(" (1)")]
    [InlineData("x (1a)")]
    [InlineData("x ()")]
    [InlineData("x(1)")]
    [InlineData("sam@example.com")]
    [InlineData("10.0.0.1:27015")]
    [InlineData("C:\\Users\\Riley")]
    [InlineData("node\nINFO forged")]
    [InlineData("Sám")]
    public void A_name_a_sentence_or_an_address_reads_as_its_length(string text)
    {
        Assert.False(HostText.IsKey(text), text);
        Assert.Equal($"a key of {text.Length} characters", HostText.KeyForLog(text));
    }

    [Fact]
    public void Unitys_name_endings_may_repeat_after_an_identifier()
    {
        Assert.True(HostText.IsKey("Card (2)(Clone)"));
        Assert.True(HostText.IsKey("Card(Clone)(Clone)"));
        Assert.True(HostText.IsKey("Card(Clone) (12)"));
    }

    [Fact]
    public void A_view_value_may_be_a_long_map_path()
    {
        // A path of 40 points (the decoder's most), each up to "63,63".
        string path = string.Join(";", Enumerable.Repeat("63,63", 40));
        Assert.Equal("a key of 239 characters", HostText.KeyForLog(path));
        Assert.Equal(path, HostText.KeyForLog(path, HostText.MaxValueLength));
    }

    [Fact]
    public void A_players_text_reads_as_its_length_in_characters()
    {
        Assert.Equal("11 characters", HostText.TextShape("Riley Casey"));
        Assert.Equal("1 character", HostText.TextShape("R"));
        Assert.Equal("empty", HostText.TextShape(string.Empty));
        Assert.Equal("empty", HostText.TextShape(null));
        Assert.Equal("2 characters", HostText.TextShape("e\u0301a")); // a letter with its accent counts as one
    }

    [Fact]
    public void Two_of_the_games_term_keys_have_a_space_and_read_as_their_length()
    {
        // Neither is a text the mod sends (a description and the brawl screen's control label); were one sent, its key would
        // read as its length, not as a name.
        foreach (string key in new[] { "v2/Ability to use the stick to knock the puck away from an opposing skater", "v2/ui/brawl/control_fighting stance" })
        {
            Assert.StartsWith("a key of ", HostText.KeyForLog(key), StringComparison.Ordinal);
        }
    }
}
