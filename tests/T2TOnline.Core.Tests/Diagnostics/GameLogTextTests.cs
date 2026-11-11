using System;
using System.Linq;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class GameLogTextTests
{
    // Player-written text as its length only, so a test reads what was replaced.
    private static readonly Func<string?, string> Shape = text => $"{text?.Length ?? 0} characters";

    [Theory]
    [InlineData("Loaded custom team from team id: Riley Bay ", "Loaded custom team from team id: <a name: 9 characters> ")]
    [InlineData("Could not find logo: custom_my face.png, returning default logo", "Could not find logo: <a name: 18 characters>, returning default logo")]
    [InlineData("TEAMDATA is NULL: Alex Casey", "TEAMDATA is NULL: <a name: 10 characters>")]
    [InlineData("[TeamData] SetDefensePair on '': 'Sam Riley' DROPPED from the roster", "[TeamData] SetDefensePair on '': '<a name: 9 characters>' DROPPED from the roster")]
    [InlineData("Player Alex Casey was hit", "Player <a name: 10 characters> was hit")]
    [InlineData("Player was hit, going to jabbed state Sam Riley", "Player was hit, going to jabbed state <a name: 9 characters>")]
    [InlineData("Alex Casey cannot punch without a Fighter and FighterConfig.", "<a name: 10 characters> cannot punch without a Fighter and FighterConfig.")]
    [InlineData("[PostShotBrake] Sam Riley GOALIE CONTACT t=0.40s relSpeed=3.1 centerDist=0.20", "[PostShotBrake] <a name: 9 characters> GOALIE CONTACT t=0.40s relSpeed=3.1 centerDist=0.20")]
    [InlineData("[PostShotBrake] Sam Riley result braked=True minDist=0.20", "[PostShotBrake] <a name: 9 characters> result braked=True minDist=0.20")]
    [InlineData("Error while equipping Helmet to Alex Casey: boom", "Error while equipping <a name: 6 characters> to <a name: 10 characters>: boom")]
    [InlineData("TeamColorsData is null for Sam Riley", "TeamColorsData is null for <a name: 9 characters>")]
    [InlineData("Alex Casey: System.NullReferenceException: Object reference not set to an instance of an object.", "<a name: 10 characters>: System.NullReferenceException: Object reference not set to an instance of an object.")]
    public void The_games_lines_that_name_a_skater_a_team_or_a_logo_keep_only_its_shape(string line, string expected)
    {
        Assert.Equal(expected, GameLogText.Reduce(line, Shape, out int reduced));
        Assert.Equal(expected.Split("<a name:").Length - 1, reduced);
    }

    [Fact]
    public void The_mod_taps_copy_in_a_session_log_is_reduced_too()
    {
        Assert.Equal("05:19:00.000 WARN  [game Error] TEAMDATA is NULL: <a name: 10 characters>",
            GameLogText.Reduce("05:19:00.000 WARN  [game Error] TEAMDATA is NULL: Alex Casey", Shape, out int reduced));
        Assert.Equal(1, reduced);
    }

    [Fact]
    public void A_skaters_object_in_a_scene_path_keeps_only_its_shape_and_the_games_objects_stay()
    {
        string skater = "If you absolutely need to use negative scaling you can use the convex MeshCollider. Scene hierarchy path \"Hockey/Teams/NorthTeam/Alex Casey/Body/Stick/collider\"";
        Assert.Equal("If you absolutely need to use negative scaling you can use the convex MeshCollider. Scene hierarchy path \"Hockey/Teams/NorthTeam/<a name: 10 characters>/Body/Stick/collider\"",
            GameLogText.Reduce(skater, Shape, out int reduced));
        Assert.Equal(1, reduced);

        string goalie = "Scene hierarchy path \"Hockey/Teams/NorthTeam/GoaltenderNorth/Body/SpineStandardGoalie/SkeletonUtility-SkeletonRoot/root/Bodybone/Arm_right/Blocker/Stick/collider\"";
        Assert.Same(goalie, GameLogText.Reduce(goalie, Shape, out int none));
        Assert.Equal(0, none);
    }

    [Theory]
    [InlineData("Steam Initialized")]
    [InlineData("No files matching *.png found in directory: CustomLogos")]
    [InlineData("Could not find custom team. Using default")]
    [InlineData("NullReferenceException: Object reference not set to an instance of an object")]
    [InlineData("  at Skaters.Forward.OnDestroy () [0x00000] in <00000000000000000000000000000000>:0 ")]
    [InlineData("Skaters.Forward:OnDestroy()")]
    [InlineData("UnityEngine.DebugLogHandler:Internal_Log(LogType, LogOption, String, Object)")]
    [InlineData("05:19:00.000 INFO  Online: a custom team (11 characters, #4be1) was loaded.")]
    [InlineData("")]
    public void Other_lines_stay_as_they_are(string line)
    {
        Assert.Same(line, GameLogText.Reduce(line, Shape, out int reduced));
        Assert.Equal(0, reduced);
    }

    [Fact]
    public void A_name_never_survives_in_any_of_the_lines()
    {
        const string Name = "Ryléy 凯西 ⚽";
        string[] lines =
        {
            $"Loaded custom team from team id: {Name} ",
            $"Player {Name} was hit",
            $"TEAMDATA is NULL: {Name}",
            $"Scene hierarchy path \"Hockey/Teams/SouthTeam/{Name}/Body\"",
        };

        foreach (string line in lines)
        {
            string reduced = GameLogText.Reduce(line, PlayerTextShape.ForLaunch().Describe, out int count);
            Assert.DoesNotContain("Ryléy", reduced, StringComparison.Ordinal);
            Assert.DoesNotContain("凯西", reduced, StringComparison.Ordinal);
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public void A_shape_that_throws_leaves_the_line_out_instead_of_keeping_it()
    {
        string reduced = GameLogText.Reduce("Player Alex Casey was hit", _ => throw new InvalidOperationException("no"), out int count);
        Assert.Equal(GameLogText.Unchecked, reduced);
        Assert.Equal(1, count);
    }

    [Fact]
    public void A_long_line_without_a_name_is_quick_and_the_same()
    {
        string line = string.Concat(Enumerable.Repeat("Exception ", 50_000));
        Assert.Same(line, GameLogText.Reduce(line, Shape, out _));
    }
}
