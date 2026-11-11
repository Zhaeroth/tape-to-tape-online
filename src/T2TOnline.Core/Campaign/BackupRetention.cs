using System;
using System.Collections.Generic;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Which save backups go when a new one is taken (D46, D102): all but the newest few, the first of each of the newest few days that
/// have one, and those that must always stay (the first ever taken, the one just taken, the session's, the one being restored).
/// While a restore's journal is on disk (<see cref="RestoreJournal"/>: a restore under way, or one the game stopped in the middle
/// of, which the next start finishes from its restored backup or undoes from the one taken before it), those two stay too: without
/// them the recovery could neither finish the restore nor undo it. Game-independent.
/// </summary>
public static class BackupRetention
{
    /// <summary>
    /// The backups among <paramref name="names"/> to remove: those beyond the newest <paramref name="kept"/> (by when each was taken),
    /// except the first of each of the newest <paramref name="keptDays"/> days that have one, those in <paramref name="keepAlways"/>
    /// (any case; null entries ignored), and <paramref name="journal"/>'s restored backup and the one taken before it (null: no
    /// journal on disk). Old names are read by <paramref name="clock"/> (null: <see cref="BackupClock.Local"/>).
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<string> names, int kept, int keptDays, IEnumerable<string?> keepAlways, RestoreJournal? journal,
        BackupClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(keepAlways);
        List<string> all = names.ToList();
        var keep = new List<string?>(keepAlways);
        keep.AddRange(SaveBackupNames.FirstOfEachDay(all, keptDays, clock));
        if (journal != null)
        {
            keep.Add(journal.Backup);
            keep.Add(journal.Before);
        }

        return SaveBackupNames.Expired(all, kept, keep, clock);
    }
}
