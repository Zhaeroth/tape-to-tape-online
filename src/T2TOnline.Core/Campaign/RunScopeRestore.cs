using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>What a restore makes of the per-run counters (<see cref="RunScopeRestore.Plan"/>): the next store, and each decision in words.</summary>
/// <param name="Store">The store to keep from now on.</param>
/// <param name="Notes">Each decision, worded to follow "Progress: " in the log.</param>
public sealed record RunScopePlan(RunScopeStore Store, IReadOnlyList<string> Notes);

/// <summary>
/// The per-run counters through backups and restores (D46, D47). The game keeps every stat's "this run"
/// and "this act" value and the bench quests' per-run counts in the profile and stats files, not in the run, and a host
/// keeps them per run file (<see cref="RunScopeStore"/>). So a backup also keeps the store, with the live run file's
/// counters as they are in memory (<see cref="Snapshot"/>), and a restore sets each restored run file's counters from
/// it (<see cref="Plan"/>): without that a restored co-op slot would go on with the newer counters of the run it replaced.
/// Game-independent.
/// </summary>
public static class RunScopeRestore
{
    /// <summary>The store's file name, in a backup's <c>coop</c> folder (beside the co-op slots).</summary>
    public const string FileName = "run-scopes.txt";

    /// <summary>
    /// A backup's copy of <paramref name="store"/>: every run file's counters as they are now, the live run file's
    /// (<paramref name="liveFile"/>) from memory (<paramref name="liveCapture"/>), so each entry stands for its run file.
    /// Without a capture (the game's counters can't be read now: this PC follows a host, a restored backup waits for the
    /// restart, the profile or stats aren't loaded, or the game hasn't started) the live run file's entry is left out. The
    /// save files the backup copies hold that run file's counters themselves, and the store's entry for it is from when
    /// the run file last became live: older than the files, and an earlier run's once a new run has started since. A
    /// restore of the backup then keeps the restored files' own counters for it (<see cref="Plan"/>) rather than put
    /// older ones into memory over them. While a restore's reload is pending, with a capture or without, the live entry
    /// is that restore's plan, which neither memory nor the files hold yet: it stays. Never changes <paramref name="store"/>.
    /// </summary>
    public static RunScopeStore Snapshot(RunScopeStore store, string? liveFile, RunScope? liveCapture)
    {
        ArgumentNullException.ThrowIfNull(store);
        RunScopeStore copy = store.Clone();
        if (RunScopeStore.IsRunFile(liveFile) && !store.Reload)
        {
            if (liveCapture != null)
            {
                copy.Set(liveFile!, liveCapture);
                copy.SetLive(liveFile!);
            }
            else
            {
                copy.Forget(liveFile!);
            }
        }

        copy.Reload = false;
        return copy;
    }

    /// <summary>
    /// The store after a restore. <paramref name="current"/> is a <see cref="Snapshot"/> from just before the restore (the
    /// "before" backup's copy), <paramref name="backup"/> the restored backup's (null: it predates per-run counters in
    /// backups, or its copy is damaged). Each co-op slot in <paramref name="restoredSlotFiles"/> (run file names) gets the
    /// backup's counters, or none (zero: undercounting can't make a false "most ... in a run" record, keeping counters of
    /// play the restore took away could). When <paramref name="savesRestored"/>, the game's files now hold the counters of
    /// the backup's live run file, so the solo run file <paramref name="soloRunFile"/> (the game's run file at the title
    /// outside a session) becomes live with <see cref="RunScopeStore.Reload"/>: its counters are the backup's when the
    /// backup held the solo run (<paramref name="backupHoldsSoloRun"/>), else the current ones. With co-op slots alone,
    /// nothing in memory changes, unless a co-op slot is live now (the game closed on it and hasn't switched back yet):
    /// then the solo run file becomes live with a reload, as the switch would have made it.
    /// </summary>
    public static RunScopePlan Plan(RunScopeStore current, RunScopeStore? backup, bool savesRestored, bool backupHoldsSoloRun,
        IReadOnlyList<string> restoredSlotFiles, string soloRunFile)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(restoredSlotFiles);
        if (!RunScopeStore.IsRunFile(soloRunFile))
        {
            throw new ArgumentException("A run file's name.", nameof(soloRunFile));
        }

        RunScopeStore next = current.Clone();
        next.Reload = false;
        var notes = new List<string>();
        string liveNow = current.Live ?? soloRunFile;
        string backupLive = backup?.Live ?? soloRunFile;
        foreach (string slot in restoredSlotFiles.Where(RunScopeStore.IsRunFile).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (backup == null)
            {
                next.Set(slot, RunScope.Empty);
                notes.Add($"{slot}'s per-run counters start from zero: the restored backup predates per-run counters in backups.");
            }
            else if (backup.TryGet(slot, out RunScope kept))
            {
                next.Set(slot, kept);
                notes.Add($"{slot}'s per-run counters are back as in the restored backup ({Describe(kept)}).");
            }
            else
            {
                next.Set(slot, RunScope.Empty);
                notes.Add($"{slot}'s per-run counters start from zero: the restored backup kept none for it (a new run's).");
            }
        }

        RunScope? solo = null;
        string soloWhy = string.Empty;
        if (savesRestored)
        {
            if (backupHoldsSoloRun)
            {
                if (backup != null && backup.TryGet(soloRunFile, out RunScope kept))
                {
                    solo = kept;
                    soloWhy = $"the restored backup's ({Describe(kept)})";
                }
                else if (backup == null || string.Equals(backupLive, soloRunFile, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(backup == null
                        ? $"the per-run counters in memory after the restart are the restored files' own ({soloRunFile}'s as the backup's files hold them; the backup predates per-run counters in backups)."
                        : $"the per-run counters in memory after the restart are the restored files' own ({soloRunFile} was the backup's live run file).");
                }
                else
                {
                    solo = RunScope.Empty;
                    soloWhy = "none (the restored backup kept none for it)";
                }
            }
            else if (current.TryGet(soloRunFile, out RunScope now))
            {
                solo = now;
                soloWhy = $"its own from before the restore, which left {soloRunFile} as it was ({Describe(now)})";
            }
            else if (!string.Equals(liveNow, soloRunFile, StringComparison.OrdinalIgnoreCase))
            {
                solo = RunScope.Empty;
                soloWhy = "none (a new run's: none were kept for it)";
            }
            else
            {
                notes.Add($"{soloRunFile}'s per-run counters from before the restore couldn't be read, so after the restart memory keeps the restored files' ({backupLive}'s).");
            }

            next.SetLive(soloRunFile);
        }
        else if (!string.Equals(liveNow, soloRunFile, StringComparison.OrdinalIgnoreCase))
        {
            solo = current.TryGet(soloRunFile, out RunScope now) ? now : RunScope.Empty;
            soloWhy = $"its own ({Describe(solo)}), as the switch back from {liveNow} would have made them";
            next.SetLive(soloRunFile);
        }

        if (solo != null)
        {
            next.Set(soloRunFile, solo);
            next.Reload = true;
            notes.Add($"{soloRunFile} is the live run file, and its per-run counters go into memory {(savesRestored ? "once the game has restarted and loaded its files" : "at once")}: {soloWhy}.");
        }

        return new RunScopePlan(next, notes);
    }

    /// <summary>
    /// What in <paramref name="store"/> waits for a launch whose game build the mod may patch (only there do the per-run
    /// counters follow the run file), worded to follow "the per-run counters aren't followed on this launch (...): "; null
    /// when nothing does. A pending reload: the restore's counters for the live run file go into memory at that start, over
    /// whatever was played meanwhile. A co-op slot live (the game closed on it): the game's files hold its counters, the
    /// solo run counts on from them, and that start puts them aside as the slot's.
    /// </summary>
    public static string? WaitingNote(RunScopeStore store, string soloRunFile)
    {
        ArgumentNullException.ThrowIfNull(store);
        string live = store.Live ?? soloRunFile;
        if (store.Reload)
        {
            return $"a restore's counters for {live} go into memory at the first start T2T Online can patch; until then the game counts on " +
                "from the counters its files hold, and that start puts the restore's back over what was played meanwhile";
        }

        if (store.Live != null && !string.Equals(store.Live, soloRunFile, StringComparison.OrdinalIgnoreCase))
        {
            return $"the game's files hold {store.Live}'s counters (the game closed on that run file), so {soloRunFile} counts on from them; " +
                $"the first start T2T Online can patch puts them aside as {store.Live}'s, with what was played meanwhile, and brings {soloRunFile}'s back";
        }

        return null;
    }

    /// <summary>"12 stats with a value, bench quest counts 0/4/0/0/0", for the log.</summary>
    public static string Describe(RunScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return string.Create(CultureInfo.InvariantCulture,
            $"{scope.Stats.Count} stat(s) with a value, bench quest counts {string.Join("/", scope.Quests.Select(q => q.ToString(CultureInfo.InvariantCulture)))}");
    }
}
