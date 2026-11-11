using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class ForwardSlotsTests
{
    [Fact]
    public void The_hosts_frogs_are_read_through_the_presentation_values()
    {
        var values = new PresentationValues();
        Assert.True(values.Take("frog.who.0", NetValue.FromRef(14)));
        Assert.True(values.Take("frog.is.0", NetValue.FromInt(12)));

        Assert.True(values.Frogs.Changed);
        Assert.True(values.Frogs.TryGet(0, out NetValue who, out NetValue seconds));
        Assert.Equal(NetValue.FromRef(14), who);
        Assert.Equal(12, seconds.WholeValue);
        Assert.False(values.Frogs.TryGet(1, out _, out _));

        values.EndMessage();
        Assert.False(values.Frogs.Changed);
        values.Take("frog.is.0", NetValue.Null); // the frog is over on the host
        Assert.True(values.Frogs.Changed);
        Assert.False(values.Frogs.TryGet(0, out _, out _));
    }

    [Fact]
    public void A_zap_carries_the_skater_its_forward_is_now()
    {
        var values = new PresentationValues();
        values.Take("zap.who.3", NetValue.FromRef(20));
        values.Take("zap.is.3", NetValue.FromString("baaf33ce343d6914496504171dee644f"));

        Assert.True(values.Zaps.TryGet(3, out NetValue who, out NetValue form));
        Assert.Equal(NetValue.FromRef(20), who);
        Assert.Equal("baaf33ce343d6914496504171dee644f", form.Text);
    }

    [Fact]
    public void Bad_slot_values_are_refused_and_change_nothing()
    {
        ForwardSlots frogs = ForwardSlots.Frogs();
        ForwardSlots zaps = ForwardSlots.Zaps();
        Assert.True(frogs.Take("frog.is.0", NetValue.FromInt(ForwardSlots.MaxFrogSeconds + 1)));
        Assert.True(frogs.Take("frog.is.1", NetValue.FromString("12")));
        Assert.True(frogs.Take("frog.who.0", NetValue.FromInt(3)));
        Assert.True(zaps.Take("zap.is.0", NetValue.FromString("id with spaces")));
        Assert.True(zaps.Take("zap.is.1", NetValue.FromString(new string('a', ForwardSlots.MaxIdLength + 1))));
        Assert.True(zaps.Take("zap.is.2", NetValue.FromInt(1)));

        Assert.Equal(3, frogs.Refused);
        Assert.Equal(3, zaps.Refused);
        Assert.False(frogs.Changed || zaps.Changed);
    }

    [Fact]
    public void Other_keys_arent_a_slots_and_unknown_ones_of_its_kind_are_ignored()
    {
        ForwardSlots frogs = ForwardSlots.Frogs();
        Assert.False(frogs.Take("zap.who.0", NetValue.FromRef(1)));
        Assert.True(frogs.Take("frog.who.99", NetValue.FromRef(1)));
        Assert.Equal(0, frogs.Refused);
        Assert.False(frogs.Changed);
        Assert.Equal("frog.who.15", frogs.WhoKey(15));
        Assert.Equal("frog.is.15", frogs.ValueKey(15));
    }

    [Fact]
    public void The_bench_borrowers_pick_is_a_talent_pick()
    {
        var pick = new TalentPick(PickSource.BenchBorrower, "abc", 1);
        Assert.True(TalentPick.TryParse(pick.Format(), out TalentPick read));
        Assert.Equal(PickSource.BenchBorrower, read.Source);
    }
}
