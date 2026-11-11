using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

public class MatchFollowValuesTests
{
    private const string TalentId = "ba0af0a4-06f6-4025-811f-6b24e78aec41";

    [Fact]
    public void A_talent_pick_round_trips_as_text()
    {
        var pick = new TalentPick(PickSource.HotCake, TalentId, 2);

        Assert.Equal($"1|{TalentId}|2", pick.Format());
        Assert.True(TalentPick.TryParse(pick.Format(), out TalentPick read));
        Assert.Equal(pick, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4|abc|1")] // no such relic
    [InlineData("1||1")]
    [InlineData("1|abc")]
    [InlineData("1|abc|1|2")]
    [InlineData("1|ab c|1")]
    [InlineData("1|abc\n|1")]
    [InlineData("1|abc|100")]
    [InlineData("1|abc|-1")]
    [InlineData("x|abc|1")]
    public void A_bad_pick_is_refused(string? text)
    {
        Assert.False(TalentPick.TryParse(text, out _));
    }

    [Fact]
    public void An_overlong_id_is_refused()
    {
        Assert.False(TalentPick.TryParse($"1|{new string('a', TalentPick.MaxIdLength + 1)}|1", out _));
    }

    [Fact]
    public void The_hosts_picks_are_read_and_their_count_bounds_them()
    {
        var values = new PresentationValues();
        Assert.True(values.Take(PresentationKeys.PickWho(0), NetValue.FromRef(14)));
        Assert.True(values.Take(PresentationKeys.PickTalent(0), NetValue.FromString(new TalentPick(PickSource.HotCake, TalentId, 1).Format())));
        Assert.True(values.Take(PresentationKeys.PickWho(1), NetValue.FromRef(15)));
        Assert.True(values.Take(PresentationKeys.PickTalent(1), NetValue.FromString(new TalentPick(PickSource.HotCold, "cold", 1).Format())));
        Assert.True(values.Take(PresentationKeys.PickCount, NetValue.FromInt(1)));

        Assert.True(values.PicksChanged);
        Assert.Equal(1, values.PickCount);
        Assert.True(values.TryPick(0, out NetValue who, out TalentPick pick));
        Assert.Equal(NetValue.FromRef(14), who);
        Assert.Equal(TalentId, pick.TalentId);
        Assert.False(values.TryPick(1, out _, out _)); // past the count

        values.EndMessage();
        Assert.False(values.PicksChanged);
        Assert.True(values.Take(PresentationKeys.PickCount, NetValue.FromInt(1)));
        Assert.False(values.PicksChanged); // the same again
    }

    [Fact]
    public void Bad_pick_values_are_refused_and_change_nothing()
    {
        var values = new PresentationValues();
        values.Take(PresentationKeys.PickTalent(0), NetValue.FromString("1|ab c|1"));
        values.Take(PresentationKeys.PickWho(0), NetValue.FromInt(3));
        values.Take(PresentationKeys.PickCount, NetValue.FromInt(PresentationKeys.MaxPicks + 1));

        Assert.Equal(3, values.Refused);
        Assert.Equal(0, values.PickCount);
        Assert.False(values.PicksChanged);
    }

    [Fact]
    public void A_cultists_form_is_followed_by_its_forward()
    {
        var values = new PresentationValues();
        Assert.True(values.Take(PresentationKeys.CultistWho(2), NetValue.FromRef(21)));
        Assert.True(values.Take(PresentationKeys.CultistTurned(2), NetValue.FromBool(true)));

        Assert.True(values.CultistsChanged);
        Assert.True(values.TryCultist(2, out NetValue who, out bool turned));
        Assert.Equal(NetValue.FromRef(21), who);
        Assert.True(turned);
        Assert.False(values.TryCultist(0, out _, out _));

        values.Reset();
        Assert.False(values.TryCultist(2, out _, out _));
    }

    [Fact]
    public void Unknown_pick_or_cultist_keys_are_taken_and_ignored()
    {
        var values = new PresentationValues();
        Assert.True(values.Take("pick.something", NetValue.FromInt(1)));
        Assert.True(values.Take("cultist.who.99", NetValue.FromRef(1)));
        Assert.Equal(0, values.Refused);
        Assert.False(values.PicksChanged || values.CultistsChanged);
    }
}
