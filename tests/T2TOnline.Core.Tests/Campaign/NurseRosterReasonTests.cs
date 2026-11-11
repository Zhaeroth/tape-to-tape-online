using System;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>A roster the host can't send goes as its reason, cut to fit a name: whatever the reason holds, what's left can be sent.</summary>
public class NurseRosterReasonTests
{
    [Theory]
    [InlineData(1, 40)] // one byte, then 40 four-byte characters: the cut lands between a pair's two halves
    [InlineData(2, 40)]
    [InlineData(3, 40)]
    [InlineData(0, 40)]
    [InlineData(0, 60)]
    public void A_reason_cut_to_fit_never_ends_in_half_a_character(int ascii, int emoji)
    {
        string why = new string('a', ascii) + string.Concat(Enumerable.Repeat("\U0001F600", emoji));
        NurseRosterData unavailable = NurseRosterData.Unavailable(why);

        Assert.Null(unavailable.Check());
        Assert.False(char.IsHighSurrogate(unavailable.Problem![^1]));
        Assert.StartsWith(new string('a', ascii) + "\U0001F600", unavailable.Problem, StringComparison.Ordinal);

        // It goes, and reads back as it was written.
        var writer = new BitWriter();
        unavailable.Write(writer);
        var reader = new BitReader(writer.ToArray());
        Assert.True(NurseRosterData.TryRead(ref reader, out NurseRosterData? read));
        Assert.Equal(unavailable.Problem, read!.Problem);
    }
}
