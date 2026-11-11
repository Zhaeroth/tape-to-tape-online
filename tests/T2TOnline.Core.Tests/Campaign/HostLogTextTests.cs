using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// Every type a host fills writes it to a log by one rule (<see cref="HostText"/>): an honest host's ids read as before, a
/// name or a sentence a modified host puts in their place never reads at all.
/// </summary>
public class HostLogTextTests
{
    private const string Name = "Riley Casey";

    private static KeyValuePair<string, NetValue> Text(string key, string value) => new(key, NetValue.FromString(value));

    private static KeyValuePair<string, NetValue> Whole(string key, long value) => new(key, NetValue.FromInt(value));

    [Fact]
    public void A_hosts_view_reads_as_before_when_honest()
    {
        var view = new HostView(7, "map", 3, new[]
        {
            Whole("act", 1), Text("node", "0,1"), Text("path", "0,0;0,1"), Text("run", "00000000-0000-4000-8000-000000000001"), Text("sel", "1,2"),
            new KeyValuePair<string, NetValue>("tip", NetValue.FromBool(false)),
        });
        Assert.Equal("map (act=1, node=\"0,1\", path=\"0,0;0,1\", run=\"00000000-0000-4000-8000-000000000001\", sel=\"1,2\", tip=false)", view.ToString());
        Assert.Equal("map (path=\"\")", new HostView(1, "map", 0, new[] { Text("path", string.Empty) }).ToString());
        Assert.Equal("title", new HostView(1, "title", 0, Array.Empty<KeyValuePair<string, NetValue>>()).ToString());
    }

    [Fact]
    public void A_hosts_view_never_writes_a_name_in_its_screen_keys_or_values()
    {
        var view = new HostView(7, Name, 0, new[] { Text(Name, Name), Text("node", "Sam was here") });
        string line = view.ToString();
        Assert.Equal("a key of 11 characters (a key of 11 characters=a key of 11 characters, node=a key of 12 characters)", line);
        Assert.DoesNotContain("Riley", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_problem_with_a_hosts_view_names_a_point_but_not_a_name()
    {
        var view = new HostView(1, "map", 0, new[] { Text("node", "0,1"), Text("sel", Name) });
        Assert.Equal("\"0,1\"", MapViewDetails.SafeDetail(view, "node"));
        Assert.Equal("a key of 11 characters", MapViewDetails.SafeDetail(view, "sel"));
        Assert.Equal("none", MapViewDetails.SafeDetail(view, "act"));
    }

    [Fact]
    public void A_menus_id_and_its_elements_go_by_the_rule()
    {
        Assert.Equal("RelicRewardMenu #3", new MenuState(3, "RelicRewardMenu", "RelicRewardMenu", "world_map", 0, MenuData.Empty).ToString());
        Assert.Equal("a key of 11 characters #3", new MenuState(3, Name, "RelicRewardMenu", "world_map", 0, MenuData.Empty).ToString());
        Assert.Equal("3/0/2 (SkipHiddenInput (1))", MenuTarget.AtPath(new[] { 3, 0, 2 }, "SkipHiddenInput (1)").ToString());
        Assert.Equal("3/0 (RelicDetails(Clone))", MenuTarget.AtPath(new[] { 3, 0 }, "RelicDetails(Clone)").ToString());
        Assert.Equal("3/0 ()", MenuTarget.AtPath(new[] { 3, 0 }, string.Empty).ToString());
        Assert.Equal("3 (a key of 11 characters)", MenuTarget.AtPath(new[] { 3 }, Name).ToString());
        Assert.Equal("relic#1", MenuTarget.AtItem("relic", 1).ToString());
        Assert.Equal("a key of 11 characters#1", MenuTarget.AtItem(Name, 1).ToString());
        Assert.DoesNotContain("Riley", MenuEvent.Selected(3, MenuTarget.AtPath(new[] { 1 }, Name)).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_and_its_objects_and_lines_go_by_their_ids()
    {
        var set = new EventChoiceSet { EventClass = "CoachEvent", EventName = "coachEvent" };
        Assert.Equal("CoachEvent \"coachEvent\" v0 (open), 0 choice(s)", set.ToString());
        Assert.DoesNotContain("Riley", new EventChoiceSet { EventClass = Name, EventName = Name }.ToString(), StringComparison.Ordinal);

        var relic = new EventObject { Kind = EventObjectKind.Relic, Id = "indecisive", ClassName = "Indecisive", Level = 1, Owner = EventObjectOwner.Skater, OwnerId = Name };
        Assert.Equal("relic indecisive (Indecisive) level 1, a key of 11 characters's", relic.ToString());

        var line = new NpcLineData { Number = 2, Source = NpcLineSource.Visit, Index = 5, Pool = "ChaosDialoguePool", Sequence = "Visit_06" };
        Assert.Equal("line 2: \"Visit_06\" (visit 6 of pool \"ChaosDialoguePool\")", line.ToString());
        Assert.DoesNotContain("Riley", new NpcLineData { Pool = Name, Sequence = Name }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_skater_a_host_sends_goes_by_its_ids_never_its_name()
    {
        var agent = new FreeAgentData { Id = "fa-3b1c", OriginalId = "GM_Template_Ole", FirstName = "Riley", LastName = "Casey", Number = 17 };
        Assert.Equal("skater fa-3b1c #17 (from GM_Template_Ole), 0 talent(s)", agent.ToString());

        var here = new FreeAgentData { Id = "fa-3b1c", OriginalId = "GM_Template_Ole", FirstName = "Sam", LastName = "Casey", Number = 17 };
        string? differs = SkaterModelDiff.FirstDifference(agent, here);
        Assert.Equal("first name (3 characters here, 5 characters on the host's)", differs);

        Assert.Equal("talent \"speedster\" level 2", new RunStartReward { Kind = RunStartRewardKind.Talent, Id = "speedster", Level = 2 }.ToString());
        Assert.Equal("talent \"a key of 11 characters\" level 2", new RunStartReward { Kind = RunStartRewardKind.Talent, Id = Name, Level = 2 }.ToString());
        Assert.Equal("0,3 Fan a key of 11 characters", new NodeRewardEntry(new MapPoint(0, 3), NodeRewardClass.Fan, Name).ToString());
        Assert.Equal("sniper (level 2)", new NurseTalent("sniper", 2).ToString());
    }

    [Fact]
    public void A_runs_history_names_its_run_and_squad_by_id()
    {
        var run = new RunHistoryData { RunId = "00000000-0000-4000-8000-000000000001", SquadId = "violence" };
        Assert.StartsWith("run 00000000-0000-4000-8000-000000000001: lost, squad violence difficulty 0", run.Words(), StringComparison.Ordinal);
        Assert.DoesNotContain("Riley", new RunHistoryData { RunId = Name, SquadId = Name }.Words(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_recipe_writes_numbers_and_ids_but_not_a_name()
    {
        Assert.Equal("format(term \"v2ui/injury_toast/desc\", injury 2's name, \"2\")",
            TextRecipe.Format(TextRecipe.Term("v2ui/injury_toast/desc"), TextRecipe.InjuryName(2), TextRecipe.Literal("2")).ToString());
        Assert.Equal("format(term \"a key of 11 characters\", a text (11 characters))",
            TextRecipe.Format(TextRecipe.Term(Name), TextRecipe.Literal(Name)).ToString());
        Assert.Equal("name of relic \"bean_diet\" level 2", TextRecipe.AssetName(ToastObjectKind.Relic, "bean_diet", 2).ToString());
        Assert.True(TextRecipe.Format(TextRecipe.Term("t"), TextRecipe.ObjectName(ChoiceObjectRef.Object1), TextRecipe.Literal("+2")).IsGameWords());
        Assert.False(TextRecipe.Format(TextRecipe.Term("t"), TextRecipe.Literal(Name)).IsGameWords());
        Assert.False(TextRecipe.TeamName(ToastTeam.Run).IsGameWords());
    }

    [Fact]
    public void A_menus_reads_say_the_first_apart_from_the_rest()
    {
        var reads = new MenuReadTimes();
        Assert.Equal("read 0 time(s)", reads.ToString());
        reads.Add(9.24);
        Assert.Equal("read 1 time(s): 9.2 ms", reads.ToString());
        reads.Add(0.4);
        reads.Add(0.8);
        reads.Add(double.NaN);
        Assert.Equal("read 4 time(s): the first 9.2 ms, the rest 0.4 ms on average, 0.8 ms at most", reads.ToString());
        Assert.Equal(9.24, reads.SlowestMs, 3);
    }

    [Fact]
    public void The_locks_pan_check_names_the_maps_left_on_and_the_ones_that_pan()
    {
        Assert.Equal("Friend UI: after the lock, keyboard/mouse maps still on: none; the map's pan actions (27, 30, 37, 38) are bound on none.",
            MapPanCheck.Describe(Array.Empty<(string, bool)>()));
        Assert.Equal(
            "Friend UI: after the lock, keyboard/mouse maps still on: Default (keyboard) 2, Default (mouse) 1; the map's pan actions (27, 30, 37, 38) are bound on Default (keyboard) 1.",
            MapPanCheck.Describe(new[] { ("Default (keyboard)", true), ("Default (keyboard)", false), ("Default (mouse)", false) }));
        Assert.StartsWith("Friend UI: after the lock, the keyboard and mouse maps still on couldn't be checked (", MapPanCheck.Failed("x"), StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_progress_names_a_hosts_ids_by_the_rule()
    {
        // Ids a host sends pass the decoders' check (printable, no space), so a name glued with punctuation could pass too.
        const string Glued = "Riley!Casey";
        Assert.Equal("dialogue chaosNode/visit_4 seen", ProfileMarkEntry.DialogueSeen("chaosNode/visit_4").Describe());
        Assert.Equal("dialogue a key of 11 characters seen", ProfileMarkEntry.DialogueSeen(Glued).Describe());
        Assert.Equal("dialogue a key of 11 characters not marked seen: it isn't an event NPC's line", ProfileMarks.Refusal(ProfileMarkEntry.DialogueSeen(Glued)));

        var batch = new ProgressBatch(1, 0, string.Empty, new ProgressEntry[]
        {
            new SquadEntry(SquadOutcome.Lost, "violence", 1, 600), new SquadEntry(SquadOutcome.Won, Glued, 2, 600),
        });
        string described = batch.Describe();
        Assert.Contains("squad loss violence on difficulty 1", described, StringComparison.Ordinal);
        Assert.DoesNotContain("Riley", described, StringComparison.Ordinal);

        var ends = new ProgressRunEnds();
        ends.Squad("violence", won: false, 1);
        ends.Squad(Glued, won: true, 2);
        ends.Unlocked("00000000-0000-4000-8000-0000000000aa");
        ends.Unlocked(Glued);
        string part = ends.Part(0);
        Assert.Contains("violence loss on difficulty 1", part, StringComparison.Ordinal);
        Assert.Contains("00000000-0000-4000-8000-0000000000aa", part, StringComparison.Ordinal);
        Assert.DoesNotContain("Riley", part, StringComparison.Ordinal);
    }

    [Fact]
    public void A_friends_reason_in_the_hosts_log_is_written_only_when_it_is_a_fixed_one()
    {
        Assert.Equal(FollowReasons.MatchNotLoading, FollowReasons.ForLog(FollowReasons.MatchNotLoading));
        Assert.Equal(FollowReasons.SceneLoadUnfinished, FollowReasons.ForLog("a scene load didn't finish"));
        Assert.Equal("a reason of 11 character(s)", FollowReasons.ForLog(Name));
        Assert.Equal("a reason of 30 character(s)", FollowReasons.ForLog("Riley was here, and Casey also"));
        Assert.Equal("(none)", FollowReasons.ForLog(string.Empty));
        Assert.Equal("(none)", SentText.Reason(null, Array.Empty<string>()));
        Assert.Equal("one", SentText.Reason("one", new[] { "one" }));
        Assert.Equal("a reason of 3 character(s)", SentText.Reason("One", new[] { "one" }));
    }

    [Fact]
    public void A_friends_reason_with_a_part_that_varies_keeps_its_first_words_and_only_the_length_of_the_rest()
    {
        // The reasons a friend's followers make with a time, a number or an error's text in them, as they write them: the
        // host's log says what kind of failure it was, and nothing of what came after the fixed words.
        Assert.Equal("couldn't load the world map: [32 more character(s)]", FollowReasons.ForLog("couldn't load the world map: this scene has no loading screen"));
        Assert.Equal("the title screen didn't load within [4 more character(s)]", FollowReasons.ForLog("the title screen didn't load within 60 s"));
        Assert.Equal("an error ([39 more character(s)]", FollowReasons.ForLog("an error (NullReferenceException: no object here)"));
        Assert.Equal("couldn't load the host's run[19 more character(s)]", FollowReasons.ForLog("couldn't load the host's run 12: it was refused"));
        Assert.Equal("the host's run moved on ([27 more character(s)]", FollowReasons.ForLog("the host's run moved on (its run 13 is loading here)"));
        Assert.Equal("the host's run [26 more character(s)]", FollowReasons.ForLog("the host's run 12 didn't load within 90 s"));

        // A modified friend's own words after a lead-in never read; the fixed sentences read whole.
        string forged = FollowReasons.ForLog("couldn't load the title screen: Riley was here");
        Assert.Equal("couldn't load the title screen: [14 more character(s)]", forged);
        Assert.DoesNotContain("Riley", forged, StringComparison.Ordinal);
        Assert.Equal(FollowReasons.ErrorSeeLog, FollowReasons.ForLog("an error (see the log)"));
        Assert.Equal(FollowReasons.RunSyncNotRunning, FollowReasons.ForLog("run sync isn't running"));
        Assert.Equal(FollowReasons.SaveGuardOff, FollowReasons.ForLog(FollowReasons.SaveGuardOff));
        Assert.Equal(FollowReasons.NotFollowing, FollowReasons.ForLog(FollowReasons.NotFollowing));

        // The longest lead-in that fits is the one written; an empty one, or one the reason only holds further on, is none.
        Assert.Equal("ab[1 more character(s)]", SentText.Reason("abc", Array.Empty<string>(), new[] { "a", "ab", string.Empty }));
        Assert.Equal("abc[0 more character(s)]", SentText.Reason("abc", Array.Empty<string>(), new[] { "abc" }));
        Assert.Equal("a reason of 3 character(s)", SentText.Reason("abc", Array.Empty<string>(), new[] { "bc", "abcd" }));
        Assert.Equal("abc", SentText.Reason("abc", new[] { "abc" }, new[] { "a" }));
        Assert.All(FollowReasons.LeadIns, leadIn => Assert.DoesNotContain(leadIn, FollowReasons.Fixed));
    }
}
