using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

/// <summary>The window's focus while online (a host's Alt-Tab costs friends' input; the log must tie the two).</summary>
public class FocusSpellsTests
{
    [Fact]
    public void A_spell_without_focus_has_its_length_frames_and_longest_frame()
    {
        var spells = new FocusSpells();
        spells.Lost(100);
        spells.Frame(0.016);
        spells.Frame(0.052);
        spells.Frame(0.017);
        FocusSpell? spell = spells.Regained(103.9);
        Assert.NotNull(spell);
        Assert.Equal(3.9, spell!.Value.Seconds, 6);
        Assert.Equal(3, spell.Value.Frames);
        Assert.Equal(52, spell.Value.LongestFrameMs, 6);
        Assert.Equal(3 / 3.9, spell.Value.FramesPerSecond, 6);
    }

    [Fact]
    public void Frames_with_focus_aren_t_counted_and_a_regain_without_a_loss_is_nothing()
    {
        var spells = new FocusSpells();
        spells.Frame(0.5);
        Assert.Null(spells.Regained(1));
        Assert.Equal(0, spells.Count);
    }

    [Fact]
    public void A_second_loss_while_unfocused_keeps_the_first_start()
    {
        var spells = new FocusSpells();
        spells.Lost(10);
        spells.Lost(12);
        Assert.Equal(5, spells.Regained(15)!.Value.Seconds, 6);
    }

    [Fact]
    public void The_session_totals_add_up_and_clear_starts_over()
    {
        var spells = new FocusSpells();
        spells.Lost(0);
        spells.Frame(0.030);
        spells.Regained(2);
        spells.Lost(10);
        spells.Frame(0.070);
        spells.Regained(11);
        Assert.Equal(2, spells.Count);
        Assert.Equal(3, spells.TotalSeconds, 6);
        Assert.Equal(70, spells.LongestFrameMs, 6);
        Assert.True(spells.PauseSkipped());
        Assert.False(spells.PauseSkipped());
        Assert.Equal(2, spells.PausesSkipped);

        spells.Lost(20);
        spells.Clear();
        Assert.False(spells.Unfocused);
        Assert.Equal(0, spells.Count);
        Assert.Equal(0, spells.PausesSkipped);
    }

    [Fact]
    public void A_bad_frame_length_counts_as_nothing()
    {
        var spells = new FocusSpells();
        spells.Lost(0);
        spells.Frame(double.NaN);
        spells.Frame(-1);
        Assert.Equal(0, spells.Regained(1)!.Value.LongestFrameMs);
    }

    [Fact]
    public void A_spell_too_short_to_tell_reads_no_frame_rate() =>
        Assert.Equal(0, new FocusSpell(0, 0, 0).FramesPerSecond);
}
