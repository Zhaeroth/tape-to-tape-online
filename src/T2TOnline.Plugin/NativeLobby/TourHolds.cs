using System.Threading;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The one switch every hold of the developer's check tour reads (<see cref="On"/>), so a tour on a player's own PC, with
/// their real profile, saves, co-op slots, settings and Steam account, changes none of them, whatever it drives: the save
/// guard refuses every game save but the tour's own run file (<see cref="RunFile"/>) and every one-off allowance; achievement
/// unlocks are held back; the mod's settings change in memory only (Direct Connections not at all); no backup is taken by
/// hand and none restored; the mod's own records stay in memory; the title's own prompts wait; and the session publishes no
/// rich presence, lets in only the tour's one-PC stand-in, sends no invite, opens no Steam invite window and joins no other
/// lobby (a held invite or a refused join fails the tour). On from before a tour's first step (the save guard taken first)
/// until after its last restore, and through the quit when the tour quits the game. Every refusal is logged and counted for
/// the tour's summary (<see cref="SummaryText"/>). Reads are a field read; nothing here throws.
/// </summary>
internal static partial class TourHolds
{
    private static readonly TourHoldTally Tally = new();
    private static volatile bool _on;
    private static string? _runFile;

    /// <summary>True while a check tour holds the player's data (from before its first step until after its last restore).</summary>
    public static bool On => _on;

    /// <summary>
    /// The tour's own run file (a full path in its folder), which stands in for every co-op slot's run file while set (a
    /// campaign tour's, <c>UseTourRunFile</c>), and the only save file the guard lets through during a tour; null
    /// when none is set.
    /// </summary>
    public static string? RunFile => Volatile.Read(ref _runFile);

    /// <summary>
    /// True while a prompt the mod would open at the title by itself (the offer after a session's error, the install check's)
    /// must wait: a tour is on, and the walker it may drive answers any prompt with yes.
    /// </summary>
    public static bool HoldsTitlePrompts => _on;

    /// <summary>
    /// The tour's summary part: <c>achievements held back h; allowances refused r; writes refused w; settings held s; invites
    /// held i; joins refused j</c>.
    /// </summary>
    public static string SummaryText => Tally.Summary();

    /// <summary>The tour's counts so far: achievements held back, allowances refused, writes refused (the campaign tour's last line).</summary>
    public static (long Achievements, long Allowances, long Writes) Counts => (Tally.AchievementsHeld, Tally.AllowancesRefused, Tally.WritesRefused);

    /// <summary>
    /// The refused or held writes this tour that left some of the player's data in memory only (Core's
    /// <see cref="TourHoldTally.PlayerDataHeld"/>): the game's next save would write it once the holds let go.
    /// </summary>
    public static long PlayerDataHeld => Tally.PlayerDataHeld;

    /// <summary>An achievement unlock was held back (counted; said once per achievement and tour).</summary>
    public static void AchievementHeld(string? apiName)
    {
        Tally.AchievementHeld();
        string achievement = SafeText.Clean(apiName, 64);
        if (Tally.First("achievement " + achievement))
        {
            ModLog.Info($"Achievements: {achievement} held back: the check tour runs; Steam isn't told.");
        }
    }

    /// <summary>A one-off allowance of the save guard was refused (counted; said each time). <paramref name="what"/> names it and its files, never a path.</summary>
    public static void AllowanceRefused(string what)
    {
        Tally.AllowanceRefused();
        ModLog.Info($"Save guard: {what} refused: the check tour runs.");
    }

    /// <summary>
    /// The save guard refused a game save while a tour held it (counted; the guard names the first few itself);
    /// <paramref name="playerData"/> when it left some of the player's data in memory only (Core's <see cref="TourEnd.HoldsPlayerData"/>).
    /// </summary>
    public static void WriteRefused(bool playerData) => Tally.WriteRefused(playerData);

    /// <summary>
    /// True when one of the mod's own records (<paramref name="label"/>, a name, never a path) stays in memory now, a tour being
    /// on: counted with the writes, said once per record and tour as "<paramref name="area"/>: <paramref name="label"/> held in
    /// memory". False when no tour runs (the caller writes as ever).
    /// </summary>
    public static bool HoldsFile(string area, string label)
    {
        if (!_on)
        {
            return false;
        }

        Tally.WriteRefused();
        if (Tally.First("file " + label))
        {
            ModLog.Info($"{area}: {label} held in memory: the check tour runs.");
        }

        return true;
    }

    /// <summary>A setting changed in memory only (counted; said once per tour).</summary>
    public static void SettingHeld()
    {
        Tally.SettingHeld();
        if (Tally.First("settings"))
        {
            ModLog.Info("Settings: held in memory: the check tour runs.");
        }
    }

    /// <summary>
    /// An invite, or Steam's invite window, was asked for while a tour held (<paramref name="what"/> says which, never who):
    /// refused, counted and said each time. A tour never invites anyone, so the summary fails on it (<see cref="ReachProblem"/>).
    /// </summary>
    public static void InviteHeld(string what)
    {
        Tally.InviteHeld();
        ModLog.Warn($"Lobby check: {what}: refused, as the tour never sends one.");
    }

    /// <summary>A join was refused or dropped while a tour held (the caller says it): counted; the summary fails on it (<see cref="ReachProblem"/>).</summary>
    public static void JoinRefused() => Tally.JoinRefused();

    /// <summary>Why the tour fails for an invite held or a join refused, or null when there was none (Core's <see cref="TourHoldTally.ReachProblem"/>).</summary>
    public static string? ReachProblem => Tally.ReachProblem();

    /// <summary>A setting a tour may never change was asked to change: refused, said each time.</summary>
    public static void SettingRefused(string name) =>
        ModLog.Warn($"Settings: {SafeText.Clean(name, 48)} refused: the check tour runs; a tour never changes it.");

    /// <summary>The switch on, the counts back to 0, no run file yet: a tour begins (the save guard already holds).</summary>
    private static void Begin()
    {
        Tally.Reset();
        Volatile.Write(ref _runFile, null);
        _on = true;
    }

    /// <summary>
    /// Run sync has made the game's solo run its run file again: a tour's run file kept after the tour (the game still played
    /// its run, <c>Release</c>) is no longer the co-op run file. Nothing while a tour holds (its run file is its own).
    /// </summary>
    public static void SoloRunBack()
    {
        if (_on || Volatile.Read(ref _runFile) == null)
        {
            return;
        }

        Volatile.Write(ref _runFile, null);
        ModLog.Info("Run sync: the check tour's own run file is no longer the co-op run file; the co-op slots are again.");
    }

    /// <summary>The switch off, returning the run file there was; it goes too unless <paramref name="keepRunFile"/>.</summary>
    private static string? End(bool keepRunFile)
    {
        string? runFile = keepRunFile ? Volatile.Read(ref _runFile) : Interlocked.Exchange(ref _runFile, null);
        _on = false;
        return runFile;
    }
}
