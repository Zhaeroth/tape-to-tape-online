using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>
/// The texts Core builds for the log read the same on every PC: on a PC whose culture writes decimals with a comma the
/// numbers still have a point, so logs from two PCs read alike (a log line is plain, exact English).
/// </summary>
public class LogCultureTests
{
    /// <summary>Runs <paramref name="body"/> with the current culture writing decimals with a comma and groups with a point.</summary>
    private static void OnACommaPc(Action body)
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        comma.NumberFormat.NumberGroupSeparator = ".";
        try
        {
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture)); // the test's own premise
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_free_agents_random_leaving_chance_has_a_point()
    {
        OnACommaPc(() => Assert.Equal("leaves at random (0.25)", FreeAgentCondition.Random(0.25f).ToString()));
    }

    [Fact]
    public void The_tours_drop_line_has_points_in_both_times()
    {
        OnACommaPc(() =>
        {
            string line = CampaignTour.DropLine(1.5, 2.5, "seat 2");
            Assert.Contains("skater 1.5 s after the cut", line, StringComparison.Ordinal);
            Assert.Contains("the friend back after 2.5 s", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_screen_check_flag_writes_its_sizes_with_a_point()
    {
        OnACommaPc(() =>
        {
            var canvas = new UiRect(0, 0, 1920, 1080);
            var root = new UiElement { Path = "root", Rect = canvas };
            var over = new UiElement
            {
                Path = "root/a",
                Parent = 0,
                Rect = new UiRect(0, 0, 100, 40),
                Text = new UiTextFacts { Shown = "Online", PreferredWidth = 180.5f, AvailableWidth = 100.5f },
            };
            List<UiCheckHit> hits = UiCheckRules.Compute(new[] { root, over }, canvas);
            string said = string.Join(" | ", hits.Select(h => h.Detail));
            Assert.Contains("wants 180.5, has 100.5", said, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_sound_loops_refused_values_are_written_with_points()
    {
        OnACommaPc(() =>
        {
            var message = new SoundLoopMessage(0, SoundLoopOp.Stop, default, 1.5f, 2.5f, 3.5f);
            var error = Assert.Throws<InvalidOperationException>(() => message.Write(new BitWriter()));
            Assert.Contains("(1.5, 2.5, 3.5)", error.Message, StringComparison.Ordinal);
        });
    }
}
