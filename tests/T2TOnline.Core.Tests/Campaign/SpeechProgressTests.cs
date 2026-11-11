using System;
using System.Linq;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

public class SpeechProgressTests
{
    [Fact]
    public void A_line_and_its_stage_round_trip_as_one_menu_value()
    {
        foreach (int key in new[] { 1, 2, 3, 254, SpeechProgress.MaxKey })
        {
            foreach (SpeechStage stage in Enum.GetValues<SpeechStage>())
            {
                SpeechProgress line = SpeechProgress.Line(key, stage);
                Assert.Equal((key * 4) + (int)stage, line.Value);
                Assert.True(SpeechProgress.TryRead(line.Value, out SpeechProgress read));
                Assert.Equal(line, read);
                Assert.False(read.IsNone);
            }
        }

        Assert.Equal(1022, SpeechProgress.MaxValue);
        Assert.True(SpeechProgress.MaxValue <= MenuTarget.MaxIndex);
        Assert.Equal(14, SpeechProgress.Line(3, SpeechStage.Advanced).Value);
        Assert.Equal("line 3 advanced", SpeechProgress.Line(3, SpeechStage.Advanced).ToString());
        Assert.Equal("line 2 shown in full", SpeechProgress.Line(2, SpeechStage.Shown).ToString());
        Assert.Equal("line 1 started", SpeechProgress.Line(1, SpeechStage.Started).ToString());
    }

    [Fact]
    public void Value_0_is_a_speech_with_no_line_yet()
    {
        Assert.True(SpeechProgress.TryRead(0, out SpeechProgress none));
        Assert.True(none.IsNone);
        Assert.Equal(SpeechProgress.None, none);
        Assert.Equal(0, default(SpeechProgress).Value);
        Assert.Equal("no line yet", none.ToString());
    }

    [Fact]
    public void Every_other_value_reads_as_nothing()
    {
        int[] refused = { -1, 1, 2, 3, 7, 11, 1023, 1024, int.MaxValue, int.MinValue };
        foreach (int value in refused)
        {
            Assert.False(SpeechProgress.TryRead(value, out SpeechProgress read));
            Assert.True(read.IsNone);
        }

        // Every value a menu value can hold: either a line in range at a known stage, or nothing.
        foreach (int value in Enumerable.Range(0, MenuTarget.MaxIndex + 1))
        {
            if (SpeechProgress.TryRead(value, out SpeechProgress read))
            {
                Assert.Equal(value, read.Value);
                Assert.InRange(read.Key, 0, SpeechProgress.MaxKey);
                Assert.True(read.Stage is SpeechStage.Started or SpeechStage.Shown or SpeechStage.Advanced);
            }
        }
    }

    [Fact]
    public void Lines_out_of_range_are_refused_when_made()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechProgress.Line(0, SpeechStage.Started));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechProgress.Line(SpeechProgress.MaxKey + 1, SpeechStage.Started));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechProgress.Line(-1, SpeechStage.Shown));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechProgress.Line(1, (SpeechStage)3));
    }

    [Fact]
    public void Progress_only_moves_forward()
    {
        SpeechProgress line = SpeechProgress.None.Raised(1, SpeechStage.Started);
        Assert.Equal(SpeechProgress.Line(1, SpeechStage.Started), line);

        // A press during typing, then another that advances; a repeat of either changes nothing.
        line = line.Raised(1, SpeechStage.Shown);
        Assert.Equal(SpeechStage.Shown, line.Stage);
        line = line.Raised(1, SpeechStage.Advanced);
        Assert.Equal(SpeechStage.Advanced, line.Stage);
        Assert.Equal(line, line.Raised(1, SpeechStage.Shown));
        Assert.Equal(line, line.Raised(1, SpeechStage.Advanced));

        // An earlier line changes nothing; the next line starts over at its start.
        Assert.Equal(line, line.Raised(0, SpeechStage.Advanced));
        SpeechProgress next = line.Raised(2, SpeechStage.Started);
        Assert.Equal(SpeechProgress.Line(2, SpeechStage.Started), next);
        Assert.Equal(next, next.Raised(1, SpeechStage.Advanced));

        // Keys past the cap stay at the cap.
        Assert.Equal(SpeechProgress.MaxKey, next.Raised(1000, SpeechStage.Started).Key);
    }
}
