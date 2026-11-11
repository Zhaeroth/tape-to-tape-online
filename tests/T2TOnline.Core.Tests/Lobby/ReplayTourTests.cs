using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Tests.Lobby;

/// <summary>The replay tour: its plan, its goal's rules, its lines and verdicts.</summary>
public class ReplayTourTests
{
    [Fact]
    public void The_replay_tours_plan_has_no_problem_and_goes_in_its_order()
    {
        Assert.Null(UiCheckPlan.Problem(ReplayTour.Steps));
        Assert.Equal(new[] { "rp_playnow", "rp_record", "rp_playback", "rp_quit", "rp_rehearsal", "rp_campaign", "finish" }, ReplayTour.Steps.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Every_step_runs_in_the_players_own_settings_and_none_changes_them()
    {
        Assert.All(ReplayTour.Steps.Take(ReplayTour.Steps.Count - 1), s =>
        {
            Assert.Equal(UiCheckStepKind.Plain, s.Kind);
            Assert.True(s.Needs.HasFlag(UiCheckNeeds.PlayersSettings), s.Id);
        });
    }

    [Theory]
    [InlineData("replay")]
    [InlineData(" Replay ")]
    public void Run_txt_picks_the_replay_tour_with_its_eight_minutes_and_no_keys(string line)
    {
        Assert.True(UiCheckRequest.TryParse(line, UiCheckPlan.Tours, out UiCheckRequest? request, out string problem), problem);
        Assert.Same(ReplayTour.Steps, request!.Tour.Steps);
        Assert.Equal(TimeSpan.FromMinutes(8), request.Tour.Limit);
        Assert.Empty(request.Tour.TakesKeys);
        Assert.False(UiCheckRequest.TryParse("replay seed=3", UiCheckPlan.Tours, out _, out problem));
        Assert.Contains("takes no arguments", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_timings_leave_the_goal_inside_the_recording_and_the_tour_inside_its_limit()
    {
        Assert.True(ReplayTour.GoalAtSeconds + ReplayTour.GoalWindowSeconds + ReplayTour.GoalScoredSeconds < ReplayTour.RecordSeconds);
        Assert.True(ReplayTour.PlaybackSeconds < ReplayTour.RecordSeconds);
        Assert.InRange(ReplayTour.MinRecordedSeconds, ReplayTour.GoalAtSeconds + 1, ReplayTour.RecordSeconds);
        Assert.True(ReplayTour.RecordSeconds + ReplayTour.PlaybackSeconds + ReplayTour.RecordSeconds < ReplayTour.Limit.TotalSeconds / 2);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    public void The_puck_is_loose_during_a_shot_a_pass_and_when_nobody_has_it(int state, bool loose)
    {
        Assert.Equal(loose, ReplayTour.IsLoose(state));
    }

    [Fact]
    public void The_puck_goes_to_the_nets_centre_across_the_ice_at_its_own_height_held_inside_the_box()
    {
        (float X, float Y, float Z) center = (10f, 0.6f, -2f);
        (float X, float Y, float Z) min = (9f, 0f, -2.5f);
        (float X, float Y, float Z) max = (11f, 1.2f, -1.5f);
        Assert.Equal((10f, 0.1f, -2f), ReplayTour.PuckTarget(center, min, max, 0.1f));
        Assert.Equal((10f, ReplayTour.NetMargin, -2f), ReplayTour.PuckTarget(center, min, max, -3f));
        Assert.Equal((10f, 1.2f - ReplayTour.NetMargin, -2f), ReplayTour.PuckTarget(center, min, max, 4f));
        Assert.Equal((10f, 0.6f, -2f), ReplayTour.PuckTarget(center, min, max, float.NaN));
        // A box thinner than the margins: its middle.
        Assert.Equal((10f, 0.5f, -2f), ReplayTour.PuckTarget((10f, 0.5f, -2f), (9f, 0.49f, -2.5f), (11f, 0.51f, -1.5f), 0.1f));
    }

    [Fact]
    public void The_lines_have_their_fixed_shapes()
    {
        Assert.Equal("replay: recorded 2400 frames in 40.0 s (3.2 MB): dev/ui-check/x/f.t2tstream", ReplayTour.RecordedLine(2400, 40.04, 3_400_000, "dev/ui-check/x/f.t2tstream"));
        Assert.Equal("replay: played back 1200 frames, 0 failed (F4 pressed)", ReplayTour.PlayedLine(1200, 0, "F4 pressed"));
        Assert.Equal("replay: rehearsal loaded A vs B and played 2400 frames, 1 failed", ReplayTour.RehearsalLine("A vs B", 2400, 1));
        Assert.Equal("640 KB", ReplayTour.Size(640 * 1024));
        Assert.Equal("0 KB", ReplayTour.Size(-5));
    }

    [Theory]
    [InlineData(true, 2400, 40.0, null)]
    [InlineData(true, 2400, 35.0, null)]
    [InlineData(false, 0, 0.0, "the recording doesn't read back")]
    [InlineData(true, 0, 40.0, "the recording has no frames")]
    [InlineData(true, 600, 10.0, "the recording is 10.0 s, under 35 s")]
    public void A_recording_passes_when_it_reads_back_with_frames_over_most_of_its_time(bool read, int frames, double seconds, string? problem)
    {
        Assert.Equal(problem, ReplayTour.RecordingProblem(read, frames, seconds));
    }

    [Theory]
    [InlineData(1200, 0, null)]
    [InlineData(0, 0, "no frame was shown")]
    [InlineData(1200, 3, "3 object states failed to apply")]
    public void A_playback_passes_with_frames_shown_and_none_failed(long frames, long failed, string? problem)
    {
        Assert.Equal(problem, ReplayTour.PlaybackProblem(frames, failed));
    }

    [Fact]
    public void The_guards_refusals_during_the_match_are_what_grew_sorted_with_their_total()
    {
        var before = new Dictionary<string, long> { ["Save(statsV2.es3)"] = 2, ["Save(config.es3)"] = 1 };
        var after = new Dictionary<string, long> { ["Save(statsV2.es3)"] = 6, ["Save(config.es3)"] = 1, ["CommitBackup(statsV2.es3)"] = 2 };
        (IReadOnlyList<KeyValuePair<string, long>> grown, long total) = ReplayTour.RefusedSince(before, after);
        Assert.Equal(new[] { "CommitBackup(statsV2.es3)", "Save(statsV2.es3)" }, grown.Select(g => g.Key).ToArray());
        Assert.Equal(new long[] { 2, 4 }, grown.Select(g => g.Value).ToArray());
        Assert.Equal(6, total);
        Assert.Equal("replay: the save guard refused 6 writes during the Play Now match: CommitBackup(statsV2.es3) 2x, Save(statsV2.es3) 4x", ReplayTour.RefusedLine(grown, total));
        (grown, total) = ReplayTour.RefusedSince(after, after);
        Assert.Empty(grown);
        Assert.Equal("replay: the save guard refused no write during the Play Now match", ReplayTour.RefusedLine(grown, total));
    }

    [Fact]
    public void The_newest_campaign_recording_is_picked_and_the_looks_are_bounded()
    {
        var day = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        var files = new[]
        {
            new RecordingFile("old-campaign", day),
            new RecordingFile("new-exhibition", day.AddHours(3)),
            new RecordingFile("mid-campaign", day.AddHours(2)),
        };
        var read = new List<string>();
        bool IsCampaign(string path)
        {
            read.Add(path);
            return path.EndsWith("campaign", StringComparison.Ordinal);
        }

        Assert.Equal("mid-campaign", ReplayTour.NewestCampaign(files, IsCampaign, 12, out int looked)?.Path);
        Assert.Equal(2, looked);
        Assert.Equal(new[] { "new-exhibition", "mid-campaign" }, read);
        Assert.Null(ReplayTour.NewestCampaign(files, IsCampaign, 1, out looked));
        Assert.Equal(1, looked);
        Assert.Null(ReplayTour.NewestCampaign(Array.Empty<RecordingFile>(), IsCampaign, 12, out looked));
        Assert.Equal(0, looked);
    }
}
