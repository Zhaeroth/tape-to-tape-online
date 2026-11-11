using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Tests.Diagnostics;

public class ModTemporariesTests
{
    [Theory]
    [InlineData("coop-1.es3.t2t-part", true)]
    [InlineData("progress-ledger.txt.t2t-part", true)]
    [InlineData("README.txt.T2T-PART", true)]
    [InlineData("progress-ledger.txt.tmp", true)]
    [InlineData(".t2t-part", false)]
    [InlineData("coop-1.es3", false)]
    [InlineData("coop-1.es3.tmp", false)]
    [InlineData("coop-1.es3.bac", false)]
    [InlineData("coop-1.es3.tmp.bak", false)]
    [InlineData("run-scopes.txt.tmp", false)]
    [InlineData("progress\\x.t2t-part", false)]
    [InlineData("progress/x.t2t-part", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_mods_own_temporaries_are_swept(string? name, bool temporary)
    {
        Assert.Equal(temporary, ModTemporaries.IsTemporary(name));
    }

    [Theory]
    [InlineData("coop-1.es3.t2t-part", "coop-1.es3")]
    [InlineData("README.txt.T2T-PART", "README.txt")]
    [InlineData("progress-ledger.txt.t2t-part", "progress-ledger.txt")]
    [InlineData("progress-ledger.txt.tmp", "progress-ledger.txt")]
    [InlineData("PROGRESS-LEDGER.TXT.TMP", "PROGRESS-LEDGER.TXT")]
    [InlineData("coop-1.es3", null)]
    [InlineData("run-scopes.txt.tmp", null)]
    [InlineData(null, null)]
    public void A_temporary_names_the_file_it_was_written_for(string? name, string? finished)
    {
        Assert.Equal(finished, ModTemporaries.FinishedName(name));
    }

    [Fact]
    public void A_temporary_goes_only_when_its_finished_file_is_there()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "coop-1.es3", "README.txt" };

        Assert.True(ModTemporaries.IsStale("coop-1.es3.t2t-part", present.Contains));
        Assert.True(ModTemporaries.IsStale("README.txt.t2t-part", present.Contains));

        // A first ledger save that stopped before its move: the temporary is the only copy, which the ledger reads at start-up.
        Assert.False(ModTemporaries.IsStale("progress-ledger.txt.t2t-part", present.Contains));
        Assert.False(ModTemporaries.IsStale("progress-ledger.txt.tmp", present.Contains));
        present.Add("progress-ledger.txt");
        Assert.True(ModTemporaries.IsStale("progress-ledger.txt.t2t-part", present.Contains));
        Assert.True(ModTemporaries.IsStale("progress-ledger.txt.tmp", present.Contains));

        // Never anything that isn't one of the mod's temporaries, whatever is there.
        Assert.False(ModTemporaries.IsStale("coop-1.es3", _ => true));
        Assert.False(ModTemporaries.IsStale("coop-1.es3.tmp", _ => true));
        Assert.Throws<ArgumentNullException>(() => ModTemporaries.IsStale("x.t2t-part", null!));
    }
}
