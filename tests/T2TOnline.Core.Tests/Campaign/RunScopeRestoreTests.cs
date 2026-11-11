using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// The per-run counters through backups and restores: what a backup keeps, and what a restore of
/// the saves, a co-op slot, or both makes of the store, in every case.
/// </summary>
public class RunScopeRestoreTests
{
    private const string Solo = "RunDataV2.es3";
    private const string Coop1 = "coop-1.es3";
    private const string Coop2 = "coop-2.es3";

    private static RunScope Scope(int goals, int injuries = 0) =>
        new(new[] { 0, goals, 0, 0, 0 }, injuries == 0
            ? new Dictionary<int, ScopeValues>()
            : new Dictionary<int, ScopeValues> { [58] = new(StatValue.Whole(injuries), StatValue.Whole(injuries)) });

    private static RunScopeStore Store(string? live, params (string File, RunScope Scope)[] scopes)
    {
        var store = new RunScopeStore();
        if (live != null)
        {
            store.SetLive(live);
        }

        foreach ((string file, RunScope scope) in scopes)
        {
            store.Set(file, scope);
        }

        return store;
    }

    private static RunScope Get(RunScopeStore store, string file)
    {
        Assert.True(store.TryGet(file, out RunScope scope), $"{file} kept");
        return scope;
    }

    [Fact]
    public void Reload_is_kept_in_the_text_and_needs_a_live_run_file()
    {
        RunScopeStore store = Store(Solo, (Solo, Scope(3)));
        store.Reload = true;
        Assert.True(store.Changed);
        RunScopeStore read = RunScopeStore.Parse(store.Format());
        Assert.True(read.Reload);
        Assert.Equal(Solo, read.Live);
        Assert.False(read.Changed);

        // An older build's text (no reload line) and a reload without a live run file read as no reload.
        Assert.False(RunScopeStore.Parse("live RunDataV2.es3\nscope RunDataV2.es3 0 3 0 0 0\n").Reload);
        Assert.False(RunScopeStore.Parse("reload\nscope RunDataV2.es3 0 3 0 0 0\n").Reload);
    }

    [Fact]
    public void A_clone_is_independent_and_scopes_compare_by_value()
    {
        RunScopeStore store = Store(Coop1, (Solo, Scope(4, 25)));
        RunScopeStore copy = store.Clone();
        copy.Set(Coop1, Scope(9));
        copy.Reload = true;
        Assert.False(store.TryGet(Coop1, out _));
        Assert.False(store.Reload);
        Assert.Equal(new[] { Solo }, store.Files);
        Assert.Equal(new[] { Solo, Coop1 }, copy.Files);

        // A stat one side doesn't list counts as zero; a whole and a real number compare by value.
        var zeros = new RunScope(new int[5], new Dictionary<int, ScopeValues> { [12] = new(StatValue.Whole(0), StatValue.Whole(0)) });
        Assert.True(RunScope.Empty.SameAs(zeros));
        var real = new RunScope(new[] { 0, 4, 0, 0, 0 }, new Dictionary<int, ScopeValues> { [58] = new(StatValue.Real(25f), StatValue.Real(25f)) });
        Assert.True(Scope(4, 25).SameAs(real));
        Assert.False(Scope(4, 25).SameAs(Scope(4, 24)));
        Assert.False(Scope(4).SameAs(Scope(5)));
    }

    [Fact]
    public void A_backup_keeps_the_live_run_files_counters_from_memory()
    {
        // The store's own entry for the live run file is from when it was last put aside: memory is newer.
        RunScopeStore store = Store(Coop1, (Coop1, Scope(1)), (Solo, Scope(4)));
        RunScopeStore snapshot = RunScopeRestore.Snapshot(store, Coop1, Scope(7));
        Assert.Equal(7, Get(snapshot, Coop1).Quests[1]);
        Assert.Equal(4, Get(snapshot, Solo).Quests[1]);
        Assert.Equal(1, Get(store, Coop1).Quests[1]); // the store itself is untouched

        // What such a backup keeps, to the byte: the other run files' counters as kept, then the live one's from memory.
        Assert.Equal(Store(Coop1, (Solo, Scope(4)), (Coop1, Scope(7))).Format(), snapshot.Format());

        // Never switched: memory is the solo run's.
        RunScopeStore fresh = RunScopeRestore.Snapshot(new RunScopeStore(), Solo, Scope(2));
        Assert.Equal(Solo, fresh.Live);
        Assert.Equal(2, Get(fresh, Solo).Quests[1]);

        // A pending reload: its live entry is the right one, memory isn't, with a capture or without.
        store.Reload = true;
        RunScopeStore reloading = RunScopeRestore.Snapshot(store, Coop1, Scope(99));
        Assert.Equal(1, Get(reloading, Coop1).Quests[1]);
        Assert.False(reloading.Reload);
        Assert.Equal(1, Get(RunScopeRestore.Snapshot(store, Coop1, null), Coop1).Quests[1]);
        Assert.Equal(reloading.Format(), RunScopeRestore.Snapshot(store, Coop1, null).Format());
    }

    [Fact]
    public void A_backup_taken_without_a_capture_leaves_the_live_run_files_counters_to_the_save_files()
    {
        // This PC hosted once: the solo run was put aside at 5 goals when the co-op slot went live, and came back. That run ended
        // since and a new one is at 2 goals, which only the game's memory and its save files know: the store's entry for the live
        // run file is the old run's. Then it joins a friend's game, and the backup before following can't read the game's counters
        // (nor can one taken while a restored backup waits for the restart, or at the mod's start-up).
        RunScopeStore store = Store(Solo, (Solo, Scope(5)), (Coop1, Scope(9)));
        RunScopeStore backup = RunScopeStore.Parse(RunScopeRestore.Snapshot(store, Solo, null).Format());

        // The files that backup copies hold the live run file's counters: its entry is left out, the others stay.
        Assert.False(backup.TryGet(Solo, out _));
        Assert.Equal(Solo, backup.Live);
        Assert.Equal(9, Get(backup, Coop1).Quests[1]);
        Assert.Equal(5, Get(store, Solo).Quests[1]); // the store itself is untouched

        // Later the player restores that backup's saves, and a co-op slot of it: the restored files' own 2 goals stay in memory
        // after the restart (no reload of the old run's 5), and the slot gets its counters.
        RunScopeStore now = Store(Solo, (Solo, Scope(3)));
        RunScopePlan plan = RunScopeRestore.Plan(now, backup, true, true, new[] { Coop1 }, Solo);
        Assert.False(plan.Store.Reload);
        Assert.Equal(Solo, plan.Store.Live);
        Assert.Equal(9, Get(plan.Store, Coop1).Quests[1]);
        Assert.Contains(plan.Notes, n => n.Contains("the restored files' own", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("go into memory", StringComparison.Ordinal));

        // The game closed on a co-op slot: the files hold the slot's counters, so the slot's entry goes and the solo run's stays.
        RunScopeStore onSlot = RunScopeRestore.Snapshot(Store(Coop1, (Coop1, Scope(1)), (Solo, Scope(4))), Coop1, null);
        Assert.False(onSlot.TryGet(Coop1, out _));
        Assert.Equal(Coop1, onSlot.Live);
        Assert.Equal(4, Get(onSlot, Solo).Quests[1]);

        // Never switched and nothing kept: nothing to leave out.
        RunScopeStore fresh = RunScopeRestore.Snapshot(new RunScopeStore(), Solo, null);
        Assert.Equal(0, fresh.Count);
        Assert.Null(fresh.Live);
    }

    [Fact]
    public void A_restored_slot_takes_the_backups_counters_and_the_rest_stays()
    {
        RunScopeStore current = Store(Solo, (Solo, Scope(4)), (Coop1, Scope(9)), (Coop2, Scope(6)));
        RunScopeStore backup = Store(Solo, (Solo, Scope(1)), (Coop1, Scope(2)));

        RunScopePlan plan = RunScopeRestore.Plan(current, backup, false, false, new[] { Coop1 }, Solo);

        Assert.Equal(2, Get(plan.Store, Coop1).Quests[1]);
        Assert.Equal(6, Get(plan.Store, Coop2).Quests[1]);
        Assert.Equal(4, Get(plan.Store, Solo).Quests[1]);
        Assert.Equal(Solo, plan.Store.Live);
        Assert.False(plan.Store.Reload); // nothing in memory changes
        Assert.Contains(plan.Notes, n => n.StartsWith("coop-1.es3's per-run counters are back as in the restored backup", StringComparison.Ordinal));
    }

    [Fact]
    public void A_slot_from_a_backup_without_counters_or_without_that_slots_starts_from_zero()
    {
        RunScopeStore current = Store(Solo, (Coop1, Scope(9)), (Coop2, Scope(6)));

        RunScopePlan old = RunScopeRestore.Plan(current, null, false, false, new[] { Coop1 }, Solo);
        Assert.True(Get(old.Store, Coop1).SameAs(RunScope.Empty));
        Assert.Contains(old.Notes, n => n.Contains("predates per-run counters", StringComparison.Ordinal));

        RunScopePlan none = RunScopeRestore.Plan(current, Store(Solo, (Solo, Scope(1))), false, false, new[] { Coop2 }, Solo);
        Assert.True(Get(none.Store, Coop2).SameAs(RunScope.Empty));
        Assert.Equal(9, Get(none.Store, Coop1).Quests[1]);
        Assert.Contains(none.Notes, n => n.Contains("kept none for it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_saves_restore_reloads_the_backups_solo_counters_after_the_restart()
    {
        RunScopeStore current = Store(Solo, (Solo, Scope(4, 25)), (Coop1, Scope(9)));
        RunScopeStore backup = Store(Solo, (Solo, Scope(1, 3)), (Coop1, Scope(2)));

        RunScopePlan plan = RunScopeRestore.Plan(current, backup, true, true, Array.Empty<string>(), Solo);

        Assert.Equal(Solo, plan.Store.Live);
        Assert.True(plan.Store.Reload);
        Assert.True(Get(plan.Store, Solo).SameAs(Scope(1, 3)));
        Assert.Equal(9, Get(plan.Store, Coop1).Quests[1]); // the slot wasn't restored: its counters are the current ones
    }

    [Fact]
    public void A_saves_restore_from_a_backup_taken_with_a_slot_live_gives_the_solo_run_its_own_counters()
    {
        // The backup's files hold coop-1's counters (its live run file); the solo run's were put aside in its store.
        RunScopeStore current = Store(Solo, (Solo, Scope(4)));
        RunScopeStore backup = Store(Coop1, (Coop1, Scope(8)), (Solo, Scope(3)));

        RunScopePlan both = RunScopeRestore.Plan(current, backup, true, true, new[] { Coop1 }, Solo);

        Assert.Equal(Solo, both.Store.Live);
        Assert.True(both.Store.Reload);
        Assert.Equal(3, Get(both.Store, Solo).Quests[1]);
        Assert.Equal(8, Get(both.Store, Coop1).Quests[1]);
    }

    [Fact]
    public void A_saves_restore_whose_backup_lacks_the_solo_run_keeps_the_current_runs_counters()
    {
        // The restore leaves RunDataV2.es3 as it was, but the stats file it puts back holds the backup's counters.
        RunScopeStore current = Store(Solo, (Solo, Scope(5)));
        RunScopeStore backup = Store(Solo, (Solo, Scope(1)));

        RunScopePlan plan = RunScopeRestore.Plan(current, backup, true, false, Array.Empty<string>(), Solo);

        Assert.True(plan.Store.Reload);
        Assert.Equal(5, Get(plan.Store, Solo).Quests[1]);
        Assert.Contains(plan.Notes, n => n.Contains("its own from before the restore", StringComparison.Ordinal));
    }

    [Fact]
    public void A_saves_restore_from_an_old_backup_trusts_the_restored_files()
    {
        RunScopeStore current = Store(Solo, (Solo, Scope(5)), (Coop1, Scope(9)));

        RunScopePlan plan = RunScopeRestore.Plan(current, null, true, true, Array.Empty<string>(), Solo);

        Assert.False(plan.Store.Reload);
        Assert.Equal(Solo, plan.Store.Live);
        Assert.Equal(9, Get(plan.Store, Coop1).Quests[1]);
        Assert.Contains(plan.Notes, n => n.Contains("the restored files' own", StringComparison.Ordinal));

        // The same when the backup's live run file was the solo run and its counters there weren't captured.
        RunScopePlan uncaptured = RunScopeRestore.Plan(current, Store(Solo, (Coop1, Scope(2))), true, true, Array.Empty<string>(), Solo);
        Assert.False(uncaptured.Store.Reload);
    }

    [Fact]
    public void A_slot_restore_while_a_slot_is_live_makes_the_solo_run_live_as_the_switch_would()
    {
        // The game closed on coop-1 and hasn't switched back yet: memory holds coop-1's counters.
        RunScopeStore current = Store(Coop1, (Coop1, Scope(9)), (Solo, Scope(4)));
        RunScopeStore backup = Store(Solo, (Coop1, Scope(2)));

        RunScopePlan plan = RunScopeRestore.Plan(current, backup, false, false, new[] { Coop1 }, Solo);

        Assert.Equal(Solo, plan.Store.Live);
        Assert.True(plan.Store.Reload);
        Assert.Equal(4, Get(plan.Store, Solo).Quests[1]);
        Assert.Equal(2, Get(plan.Store, Coop1).Quests[1]);

        // A slot that's live and not restored keeps what memory held.
        RunScopePlan other = RunScopeRestore.Plan(current, backup, false, false, new[] { Coop2 }, Solo);
        Assert.Equal(9, Get(other.Store, Coop1).Quests[1]);
        Assert.True(other.Store.Reload);
    }

    [Fact]
    public void Planning_twice_gives_the_same_store()
    {
        // A restore the game stopped after its plan ran is finished again at the next start.
        RunScopeStore current = Store(Solo, (Solo, Scope(4)), (Coop1, Scope(9)));
        RunScopeStore backup = Store(Solo, (Solo, Scope(1)), (Coop1, Scope(2)));
        RunScopePlan first = RunScopeRestore.Plan(current, backup, true, true, new[] { Coop1 }, Solo);
        RunScopePlan again = RunScopeRestore.Plan(first.Store, backup, true, true, new[] { Coop1 }, Solo);
        Assert.Equal(first.Store.Format(), again.Store.Format());
    }

    [Fact]
    public void A_manifest_keeps_its_counters_entry_and_compares_it()
    {
        var taken = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        SaveFileHash counters = SaveFileHash.Of(RunScopeRestore.FileName, Encoding.UTF8.GetBytes("live RunDataV2.es3\n"));
        var files = new[] { SaveFileHash.Of("profileV2.es3", Encoding.UTF8.GetBytes("p")) };
        var manifest = new SaveBackupManifest("host", "before hosting", taken, files, Array.Empty<CoopSlotHash>(), counters);

        Assert.True(SaveBackupManifest.TryParse(manifest.ToJson(), out SaveBackupManifest? read));
        Assert.Equal(counters, read!.RunScopes);
        Assert.True(read.HoldsSame(files, Array.Empty<CoopSlotHash>(), counters));
        Assert.False(read.HoldsSame(files, Array.Empty<CoopSlotHash>(), null));
        Assert.True(read.HoldsSame(files, Array.Empty<CoopSlotHash>())); // the saves alone are the same

        // A backup without counters (0.7's) reads as none, and isn't the same as one with them.
        var old = new SaveBackupManifest("host", "before hosting", taken, files);
        Assert.DoesNotContain("runScopes", old.ToJson(), StringComparison.Ordinal);
        Assert.True(SaveBackupManifest.TryParse(old.ToJson(), out SaveBackupManifest? oldRead));
        Assert.Null(oldRead!.RunScopes);
        Assert.False(oldRead.HoldsSame(files, Array.Empty<CoopSlotHash>(), counters));
    }

    [Theory]
    [InlineData("\"runScopes\":[]")]
    [InlineData("\"runScopes\":{\"bytes\":-1,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}")]
    [InlineData("\"runScopes\":{\"bytes\":3,\"sha256\":\"xyz\"}")]
    [InlineData("\"runScopes\":{\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}")]
    public void Damaged_counters_in_a_manifest_read_as_none_and_the_saves_stay_restorable(string field)
    {
        string json = "{\"version\":1,\"role\":\"host\",\"reason\":\"r\",\"takenAtUtc\":\"2026-10-30T19:15:02Z\",\"files\":[" +
            "{\"name\":\"a.es3\",\"bytes\":3,\"sha256\":\"BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD\"}]," + field + "}";
        Assert.True(SaveBackupManifest.TryParse(json, out SaveBackupManifest? manifest));
        Assert.Null(manifest!.RunScopes);
        Assert.Single(manifest.Files);
    }

    [Fact]
    public void Plans_never_throw_on_odd_stores()
    {
        var random = new Random(11);
        string[] files = { Solo, Coop1, Coop2, "coop-3.es3" };
        for (int i = 0; i < 500; i++)
        {
            RunScopeStore Pick() => Store(random.Next(3) == 0 ? null : files[random.Next(files.Length)],
                files.Where(_ => random.Next(2) == 0).Select(f => (f, Scope(random.Next(5)))).ToArray());
            RunScopeStore current = Pick();
            RunScopeStore? backup = random.Next(4) == 0 ? null : Pick();
            string[] slots = files.Skip(1).Where(_ => random.Next(2) == 0).ToArray();
            RunScopePlan plan = RunScopeRestore.Plan(current, backup, random.Next(2) == 0, random.Next(2) == 0, slots, Solo);
            Assert.True(plan.Store.Count <= RunScopeStore.MaxScopes);
            Assert.All(slots, s => Assert.True(plan.Store.TryGet(s, out _)));
            Assert.True(!plan.Store.Reload || plan.Store.TryGet(Solo, out _));
        }
    }

    [Fact]
    public void A_launch_that_cannot_follow_the_run_file_says_what_waits_for_one_that_can()
    {
        // Nothing kept, or the solo run live: nothing waits.
        Assert.Null(RunScopeRestore.WaitingNote(new RunScopeStore(), Solo));
        Assert.Null(RunScopeRestore.WaitingNote(Store(Solo, (Solo, Scope(2))), Solo));

        // A restore's reload: its counters go in at the next patchable start, over what is played meanwhile.
        RunScopeStore reload = Store(Solo, (Solo, Scope(2)));
        reload.Reload = true;
        string? note = RunScopeRestore.WaitingNote(reload, Solo);
        Assert.NotNull(note);
        Assert.Contains($"a restore's counters for {Solo} go into memory at the first start T2T Online can patch", note, StringComparison.Ordinal);

        // The game closed on a co-op slot: its counters are in the game's files until then.
        note = RunScopeRestore.WaitingNote(Store(Coop2, (Solo, Scope(1))), Solo);
        Assert.NotNull(note);
        Assert.StartsWith($"the game's files hold {Coop2}'s counters", note, StringComparison.Ordinal);
        Assert.Contains($"brings {Solo}'s back", note, StringComparison.Ordinal);

        Assert.Throws<ArgumentNullException>(() => RunScopeRestore.WaitingNote(null!, Solo));
    }
}
