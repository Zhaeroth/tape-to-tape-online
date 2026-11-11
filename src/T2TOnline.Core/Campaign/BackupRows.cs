using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Lobby;

namespace T2TOnline.Core.Campaign;

/// <summary>One backup: its folder name, when it was taken (this PC's local time), why, how many save files it holds and which co-op slots.</summary>
/// <param name="Name">Its folder's name (<see cref="SaveBackupNames"/>).</param>
/// <param name="TakenAt">When it was taken, in this PC's local time (what a screen shows).</param>
/// <param name="Reason">Why, as its manifest says (English, from the mod).</param>
/// <param name="Files">How many of the game's save files it holds.</param>
/// <param name="Slots">The co-op slots it holds.</param>
public sealed record BackupInfo(string Name, DateTime TakenAt, string Reason, int Files, IReadOnlyList<int> Slots);

/// <summary>What a co-op slot's copy in a backup holds, as its run file reads.</summary>
public enum BackupRunKind
{
    /// <summary>A run the game can continue.</summary>
    Run,

    /// <summary>A lost run: the game won't continue it.</summary>
    Lost,

    /// <summary>Its run couldn't be read (too big, not a run file, or not readable now).</summary>
    Unreadable,
}

/// <summary>A co-op slot's copy in a backup.</summary>
/// <param name="Slot">The slot (1 to 3).</param>
/// <param name="Sha256">The copy's hash, as the manifest records it: two copies with one hash are the same file.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="SquadId">The run's squad's id (empty when it couldn't be read).</param>
/// <param name="Act">The run's act, or 0.</param>
/// <param name="Floor">The run's floor, or 0.</param>
public sealed record BackupSlotRun(int Slot, string Sha256, BackupRunKind Kind, string SquadId, int Act, int Floor);

/// <summary>The Backups screen's tabs (D141), in their order.</summary>
public enum BackupTab
{
    /// <summary>Your Saves: one row per backup holding the game's save files.</summary>
    Saves,

    /// <summary>Co-op Runs: one row per backup and co-op slot.</summary>
    CoopRuns,
}

/// <summary>Why a backup was taken, as the screen words it.</summary>
public enum BackupReasonKind
{
    /// <summary>As hosting began.</summary>
    Hosting,

    /// <summary>Before a later Start in the same lobby.</summary>
    Start,

    /// <summary>As a friend followed a host.</summary>
    Following,

    /// <summary>As a restore began, from what it replaced.</summary>
    Restore,

    /// <summary>By hand (a developer's function).</summary>
    Manual,

    /// <summary>Anything else (an older version's).</summary>
    Other,
}

/// <summary>Who asks for a restore, for the title it may happen over (<see cref="BackupRestoreTitle"/>).</summary>
public enum RestoreAsker
{
    /// <summary>The mod's developer panel, at the idle title.</summary>
    Panel,

    /// <summary>The Backups screen, over the game's Settings menu at the title. It tells the player to restart itself.</summary>
    BackupsScreen,
}

/// <summary>One row of the Backups screen: what it restores, and what it says.</summary>
/// <param name="Tab">Its tab.</param>
/// <param name="Backup">The backup's folder name.</param>
/// <param name="TakenAt">When the backup was taken, local time.</param>
/// <param name="Reason">Why it was taken.</param>
/// <param name="Files">How many save files it holds.</param>
/// <param name="Run">The co-op slot's copy, on a Co-op Runs row; null on a Your Saves row.</param>
public sealed record BackupRow(BackupTab Tab, string Backup, DateTime TakenAt, BackupReasonKind Reason, int Files, BackupSlotRun? Run)
{
    /// <summary>The co-op slot, or 0 on a Your Saves row.</summary>
    public int Slot => Run?.Slot ?? 0;

    /// <summary>Exactly what the row restores: the save files alone, or that one slot alone.</summary>
    public RestoreParts Parts => Run == null ? RestoreParts.SavesOnly : new RestoreParts(false, new[] { Run.Slot });

    /// <summary>What the row restores, as a log says it: <c>saves</c> or <c>slot 2</c>.</summary>
    public string PartForLog => Run == null ? "saves" : $"slot {Run.Slot.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The row's first line: its time, and on a Co-op Runs row its slot.</summary>
    public IReadOnlyList<LobbyLine> Title => Run == null
        ? new[] { Plain(BackupRows.TimeLabel(TakenAt)) }
        : new[] { Plain(BackupRows.TimeLabel(TakenAt)), new LobbyLine(LobbyWords.Slot, LobbyWords.Number(Run.Slot)) };

    /// <summary>
    /// The row's second line: on a Your Saves row why the backup was taken and its files; on a Co-op Runs row the run's squad (its id: each PC names it in its own
    /// language), act and floor, or nothing when the run couldn't be read.
    /// </summary>
    public IReadOnlyList<LobbyLine> Detail
    {
        get
        {
            if (Run == null)
            {
                return new[] { new LobbyLine(BackupRows.ReasonWord(Reason)), new LobbyLine(LobbyWords.BackupFiles, LobbyWords.Number(Files)) };
            }

            if (Run.Kind != BackupRunKind.Run || Run.SquadId.Length == 0)
            {
                return Array.Empty<LobbyLine>();
            }

            return new[]
            {
                LobbyLine.Game(BackupRows.SquadTerm, Run.SquadId),
                LobbyLine.Game(LobbyTerms.ActN, LobbyWords.Number(Run.Act)),
                new LobbyLine(LobbyWords.FloorN, LobbyWords.Number(Run.Floor)),
            };
        }
    }

    /// <summary>The details pane's words: what restoring it does.</summary>
    public LobbyWord DetailWord => Run == null ? LobbyWords.RestoreSavesDetail : LobbyWords.RestoreSlotDetail;

    /// <summary>The game's yes/no before it's restored, Cancel first.</summary>
    public LobbyLine Question => Run == null
        ? new LobbyLine(LobbyWords.AskRestoreSaves, BackupRows.TimeLabel(TakenAt))
        : new LobbyLine(LobbyWords.AskRestoreSlot, LobbyWords.Number(Run.Slot), BackupRows.TimeLabel(TakenAt));

    private static LobbyLine Plain(string text) => LobbyLine.Game(BackupRows.PlainTerm, text);
}

/// <summary>The Backups screen's rows (D141), both tabs, and what was left out of them.</summary>
public sealed class BackupRowSet
{
    internal BackupRowSet(IReadOnlyList<BackupRow> saves, IReadOnlyList<BackupRow> coopRuns, int lostRuns, int repeats)
    {
        Saves = saves;
        CoopRuns = coopRuns;
        LostRuns = lostRuns;
        Repeats = repeats;
    }

    /// <summary>No rows at all.</summary>
    public static BackupRowSet Empty { get; } = new(Array.Empty<BackupRow>(), Array.Empty<BackupRow>(), 0, 0);

    /// <summary>Your Saves, newest first.</summary>
    public IReadOnlyList<BackupRow> Saves { get; }

    /// <summary>Co-op Runs, newest first, slots in order within a backup.</summary>
    public IReadOnlyList<BackupRow> CoopRuns { get; }

    /// <summary>Co-op slot copies left out for holding a lost run.</summary>
    public int LostRuns { get; }

    /// <summary>Co-op slot copies left out for being the same file as a newer backup's copy of that slot.</summary>
    public int Repeats { get; }

    /// <summary>The rows of <paramref name="tab"/>.</summary>
    public IReadOnlyList<BackupRow> Of(BackupTab tab) => tab == BackupTab.Saves ? Saves : CoopRuns;
}

/// <summary>
/// The Backups screen's model (D141): which rows each tab has, their order and their words. Your Saves: one row per backup holding save files.
/// Co-op Runs: one row per backup and co-op slot; a slot copy holding a lost run is left out (the game won't continue it), and of copies that
/// are the same file (one hash) for the same slot only the newest is listed (restoring any of them gives the same file). Newest first, in the
/// order the backups are given: <c>SaveBackups.List</c> orders them by the moment each was taken (UTC), then by name. They aren't sorted again
/// here: a row carries only the local time it shows, and in the hour a clock change repeats the later of two backups reads earlier.
/// </summary>
public static class BackupRows
{
    /// <summary>The term of a line whose one argument is plain text, already in the player's language (a time, a squad's name): it's said as it is.</summary>
    public const string PlainTerm = "{0}";

    /// <summary>The term of a line whose one argument is a squad's id: the screen names it in this PC's language.</summary>
    public const string SquadTerm = "t2t-squad";

    /// <summary>The menu id the game's Settings menu has on the title's list while it's open over the title (seen in the game's log).</summary>
    public const string SettingsMenuId = "Settings";

    /// <summary>The time a screen shows: local, <c>yyyy-MM-dd HH:mm</c>, as the lobby's save box shows a save's (digits only: every language and font has them).</summary>
    public static string TimeLabel(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// The rows of <paramref name="backups"/>, which come newest first and keep their order. <paramref name="runs"/> gives each backup's co-op slot copies
    /// by its name (a backup missing from it lists no Co-op Runs rows); slots above <paramref name="slotCount"/> are left out (a newer version's).
    /// </summary>
    public static BackupRowSet Build(IReadOnlyList<BackupInfo> backups, IReadOnlyDictionary<string, IReadOnlyList<BackupSlotRun>> runs, int slotCount)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(runs);
        var saves = new List<BackupRow>();
        var coop = new List<BackupRow>();
        var seen = new HashSet<(int Slot, string Sha256)>();
        int lost = 0;
        int repeats = 0;
        foreach (BackupInfo backup in backups)
        {
            if (backup == null)
            {
                continue;
            }

            BackupReasonKind reason = ReasonOf(backup.Name, backup.Reason);
            if (backup.Files > 0)
            {
                saves.Add(new BackupRow(BackupTab.Saves, backup.Name, backup.TakenAt, reason, backup.Files, null));
            }

            if (!runs.TryGetValue(backup.Name, out IReadOnlyList<BackupSlotRun>? copies) || copies == null)
            {
                continue;
            }

            foreach (BackupSlotRun run in copies.Where(r => r != null && r.Slot >= 1 && r.Slot <= slotCount && backup.Slots.Contains(r.Slot)).OrderBy(r => r.Slot))
            {
                if (run.Kind == BackupRunKind.Lost)
                {
                    lost++;
                    continue;
                }

                if (run.Sha256.Length > 0 && !seen.Add((run.Slot, run.Sha256)))
                {
                    repeats++;
                    continue;
                }

                coop.Add(new BackupRow(BackupTab.CoopRuns, backup.Name, backup.TakenAt, reason, backup.Files, run));
            }
        }

        return new BackupRowSet(saves, coop, lost, repeats);
    }

    /// <summary>
    /// Why a backup was taken, from its manifest's reason (<c>before hosting</c>, <c>before this Start</c>, <c>before following ...</c>, <c>before restoring ...</c>) and
    /// its name's role (<c>manual</c>, whatever the reason says); anything else is <see cref="BackupReasonKind.Other"/>.
    /// </summary>
    public static BackupReasonKind ReasonOf(string? name, string? reason)
    {
        string why = reason ?? string.Empty;
        if (why.StartsWith("before restoring ", StringComparison.Ordinal))
        {
            return BackupReasonKind.Restore;
        }

        if (why.StartsWith("before following ", StringComparison.Ordinal))
        {
            return BackupReasonKind.Following;
        }

        if (string.Equals(why, "before hosting", StringComparison.Ordinal))
        {
            return BackupReasonKind.Hosting;
        }

        if (string.Equals(why, "before this Start", StringComparison.Ordinal))
        {
            return BackupReasonKind.Start;
        }

        return name != null && SaveBackupNames.TryParse(name, out _, out string role) && string.Equals(role, "manual", StringComparison.Ordinal)
            ? BackupReasonKind.Manual
            : BackupReasonKind.Other;
    }

    /// <summary>The word for a backup's reason.</summary>
    public static LobbyWord ReasonWord(BackupReasonKind reason) => reason switch
    {
        BackupReasonKind.Hosting => LobbyWords.BackupReasonHosting,
        BackupReasonKind.Start => LobbyWords.BackupReasonStart,
        BackupReasonKind.Following => LobbyWords.BackupReasonFollowing,
        BackupReasonKind.Restore => LobbyWords.BackupReasonRestore,
        BackupReasonKind.Manual => LobbyWords.BackupReasonManual,
        _ => LobbyWords.BackupReasonOther,
    };
}

/// <summary>
/// Where a restore may happen (D141): at the title, not starting a run, leaving, in a button's click or loading its campaign state, with no menu over it but the mod's
/// own screens; and, for the Backups screen, the game's Settings menu too, which it is opened over (the title's list holds <c>Settings</c> then).
/// </summary>
public static class BackupRestoreTitle
{
    /// <summary>
    /// True when a restore may run over a title that is <paramref name="busy"/> or not, with <paramref name="menusOver"/> on its list; <paramref name="isShell"/> tells the mod's own
    /// screens' ids.
    /// </summary>
    public static bool Allows(RestoreAsker asker, bool busy, IReadOnlyList<string> menusOver, Func<string, bool> isShell)
    {
        ArgumentNullException.ThrowIfNull(menusOver);
        ArgumentNullException.ThrowIfNull(isShell);
        if (busy)
        {
            return false;
        }

        foreach (string id in menusOver)
        {
            if (id != null && isShell(id))
            {
                continue;
            }

            if (asker == RestoreAsker.BackupsScreen && string.Equals(id, BackupRows.SettingsMenuId, StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
