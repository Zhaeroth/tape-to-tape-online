#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppState;
using Il2CppState.EndOfGame.Utils;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppUnlocks;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Campaign.Progress;
using T2TOnline.Plugin.Diagnostics;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The new-unlocks menu (<c>PermanentUnlockMenu</c>) on a friend (v1; D42: each
/// player's own progress): when the host looks at its new unlocks at the run's end, a friend sees its <b>own</b> new
/// unlocks of the run for real, the ones shared progress unlocked on its profile at its own thresholds (and the squad
/// menu's grants), made into the menu's data by the game's own <c>UnlockUIHelper.ConvertStoredUnlocks</c> over this PC's
/// list in place of the run's stored ones (the host's run, here): its <c>MarkUnlocked</c> calls (ids already unlocked here)
/// are refused quietly meanwhile. As the game's own run keeps its new unlocks for its end, whatever sessions and game
/// starts it took, a friend's of a co-op run are kept in this PC's progress ledger (<c>ProgressFriend.RunUnlocks</c>) and
/// shown with this session's. A friend with none sees a note (the game shows no new-unlocks menu for a run that unlocked
/// nothing). The host's steps to its next unlock (<c>GoToNextUnlock</c>) step this PC's own while it has more; past its
/// last it stays there. It closes with the host's (the base close, its reveal, crown and sound stopped first). The list,
/// and the run's in the ledger, are cleared once the host's menu has closed.
/// <para>
/// v1: each noted unlock carries the host's run it came in
/// (<see cref="RunScopedList{T}"/>; the squad menu's grants wait for the run that starts next), and the menu shows only the
/// run it's about (the host's run this PC holds at its open): a run whose screen never opened doesn't carry its unlocks to a
/// later run's screen (they stay on the profile; logged). The host's steps are followed on the first frame this PC's menu is
/// ready (no skater loading), not every fifteenth.
/// </para>
/// </summary>
internal static class UnlockViews
{
    /// <summary>Value group: the host's new-unlocks menu's current unlock (its index).</summary>
    public const string StepGroup = "unlock";

    private const string ForceKey = "unlock.forceRelics";
    private const string TransparentKey = "unlock.transparent";
    private const string SkipExitKey = "unlock.skipExit";
    private const string CountKey = "unlock.count";
    private const int MaxOwn = 64;

    private static readonly RunScopedList<string> Own = new(MaxOwn, StringComparer.Ordinal);

    // The noted unlocks that are also kept in the progress ledger for their run's screen (they outlive the session).
    private static readonly HashSet<string> Kept = new(StringComparer.Ordinal);

    // This session: the host's run ended here (its run-end menu at the run's end came up), and the host's new-unlocks menu
    // came. The host's game shows that menu only when the run unlocked something on its own profile, so a friend's own may
    // get no screen.
    private static bool _runEnded;
    private static bool _screenCame;

    /// <summary>True while the game's converter runs over this PC's list (its unlock marks are refused quietly).</summary>
    public static bool Converting { get; private set; }

    /// <summary>
    /// How many of this player's own unlocks wait for the host's new-unlocks menu (of the host's run this PC holds: this
    /// session's, and the ledger's from the run's earlier sessions).
    /// </summary>
    public static int OwnCount => OwnOf(ScreenRun()).Count;

    /// <summary>
    /// Friend: an unlock shared progress gave this profile during the session, while it applies the host's progress of the
    /// run this PC's campaign holds for it (the host's run; an earlier one put back for its late progress, ProgressFriend).
    /// <paramref name="kept"/>: the progress ledger keeps it for that run's screen too.
    /// </summary>
    public static void Noted(string? id, bool kept)
    {
        Note(id, MenuGame.Campaign()?.runData?.runID);
        if (kept && !string.IsNullOrEmpty(id))
        {
            Kept.Add(id);
        }
    }

    /// <summary>Friend: an unlock the squad menu gave this profile at a run's start: it waits for the run that starts next.</summary>
    public static void NotedForNextRun(string? id) => Note(id, null);

    /// <summary>
    /// Friend: the host's run is over here: its run-end menu at the run's end came up, or its new-unlocks menu
    /// (<paramref name="unlocksScreen"/>). After the first without the second, a stop says this player's kept unlocks had no
    /// screen (<see cref="SayForgotten"/>).
    /// </summary>
    public static void RunEnded(bool unlocksScreen)
    {
        if (unlocksScreen)
        {
            _screenCame = true;
        }
        else
        {
            // A run's end: its new-unlocks menu, if the host's game shows one, comes after its run-end menu.
            _runEnded = true;
            _screenCame = false;
        }
    }

    /// <summary>True when the host's run ended here (its run-end menu at the run's end came up) and no new-unlocks menu of its came after it.</summary>
    public static bool RunEndedWithoutScreen => _runEnded && !_screenCame;

    /// <summary>True when the host's run ended here: its run-end menu at the run's end came up this session.</summary>
    public static bool RunEndedHere => _runEnded;

    /// <summary>True when the host's new-unlocks menu came up here after its run's end.</summary>
    public static bool HostScreenCame => _screenCame;

    /// <summary>
    /// Friend: this player's own new-unlocks screen at a run's end the host's game showed none for (<see cref="OwnUnlocks"/>)
    /// had its turn: as at the close of the host's, this player's noted unlocks go, and the progress ledger's for the run.
    /// Returns how many went from the ledger. Never throws.
    /// </summary>
    public static int OwnScreenClosed()
    {
        _screenCame = true;
        return ScreenClosed();
    }

    /// <summary>
    /// The host's new-unlocks menu closed: this player's noted unlocks are forgotten, and the progress ledger's for the run it
    /// was about (later ones of that run aren't kept for it); which runs have had their turn stays known. Returns how many
    /// went from the ledger. Never throws.
    /// </summary>
    public static int ScreenClosed()
    {
        string? run = ScreenRun();
        Own.Clear();
        Kept.Clear();
        try
        {
            return run != null && ProgressModule.Friend is { } friend ? friend.UnlockScreenShown(run) : 0;
        }
        catch (System.Exception ex)
        {
            ModLog.Warn($"Menus: the progress ledger's new unlocks of the host's run couldn't be cleared ({ModLog.SafeErrorText(ex)}).");
            return 0;
        }
    }

    /// <summary>
    /// Following started or stopped: everything noted this session goes, the runs too (the progress ledger keeps a run's
    /// unlocks for its screen in a later session). A later session can bring an earlier one's run back (its co-op slot
    /// again): its unlocks and its screen are that run's, not "an earlier run's".
    /// </summary>
    public static void Reset()
    {
        Own.Reset();
        Kept.Clear();
        _runEnded = false;
        _screenCame = false;
    }

    /// <summary>This player's unlocks for <paramref name="run"/>'s screen: this session's (<see cref="RunScopedList{T}.Peek"/>) and the ledger's.</summary>
    private static List<string> OwnOf(string? run)
    {
        List<string> own = Own.Peek(run);
        AddKept(own, run);
        return own;
    }

    /// <summary>
    /// Adds the progress ledger's unlocks of <paramref name="run"/> (from the run's earlier sessions and this game's earlier
    /// starts) that <paramref name="own"/> lacks, up to <see cref="MaxOwn"/>.
    /// </summary>
    private static void AddKept(List<string> own, string? run)
    {
        if (run == null || ProgressModule.Friend is not { } friend)
        {
            return;
        }

        foreach (string id in friend.RunUnlocks(run))
        {
            if (own.Count >= MaxOwn)
            {
                return;
            }

            if (!own.Contains(id, StringComparer.Ordinal))
            {
                own.Add(id);
            }
        }
    }

    /// <summary>
    /// Friend: this PC loaded the host's run <paramref name="run"/> (its checkpoint, <see cref="RunSync"/>). One whose turn was over is
    /// the host's again (its co-op slot, within this session; a late copy of its progress never loads this way): its unlocks and its
    /// screen are that run's, not "an earlier run's".
    /// </summary>
    public static void RunLoaded(string? run)
    {
        if (Own.BroughtBack(run, out int dropped))
        {
            ModLog.Info($"Menus: the host brought its earlier run {SafeText.Clean(run, 48)} back; this player's new unlocks show on its screen again" +
                $"{(dropped > 0 ? $" ({dropped} of the run just left stay on this profile, not on this screen)" : string.Empty)}.");
        }
    }

    /// <summary>Notes an unlock with its run (null: not known yet); an earlier run's unlocks dropped by it, or it refused as one, are logged.</summary>
    private static void Note(string? id, string? run)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        string? known = run != null && ProgressLimits.IsPlausibleId(run) ? run : null;
        RunScopedAdd added = Own.Add(id, known, out int dropped);
        if (dropped > 0)
        {
            ModLog.Info($"Menus: {dropped} of this player's new unlock(s) came in the host's earlier run, whose new-unlocks screen never opened; " +
                "not shown on a later run's (they're on this profile).");
        }

        if (added == RunScopedAdd.EarlierRun)
        {
            ModLog.Info($"Menus: this player's new unlock {SafeText.Clean(id, 48)} came with the host's earlier run's late progress; not shown on a later run's new-unlocks screen (it's on this profile).");
        }
        else if (added == RunScopedAdd.Full)
        {
            ModLog.Warn($"Menus: this player's new unlock {SafeText.Clean(id, 48)} isn't kept for the new-unlocks screen ({MaxOwn} are already); it's on this profile.");
        }
    }

    /// <summary>The run the host's new-unlocks screen is about here: the host's run this PC holds, or null when it holds none (every noted unlock shows).</summary>
    private static string? ScreenRun()
    {
        string? held = RunSync.Instance?.HoldingHostRun == true ? MenuGame.Campaign()?.runData?.runID : null;
        return held != null && ProgressLimits.IsPlausibleId(held) ? held : null;
    }

    /// <summary>Host: the new-unlocks menu's flags, for a friend's own copy.</summary>
    public static void Read(PermanentUnlockMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        values.Add(new(ForceKey, NetValue.FromBool(menu.m_ForceRelicsAsNewStartingRelic)));
        values.Add(new(SkipExitKey, NetValue.FromBool(menu.m_SkipExitLoadingTransition)));
        Il2CppReferenceArray<UnityEngine.GameObject>? background = menu.m_BackgroundObjects;
        bool transparent = background != null && background.Length > 0 && background[0] != null && !background[0].activeSelf;
        values.Add(new(TransparentKey, NetValue.FromBool(transparent)));
        values.Add(new(CountKey, NetValue.FromInt(menu.m_AllUnlocks?.Count ?? 0)));
    }

    /// <summary>
    /// Friend: the menu's data with this player's own unlocks (see <see cref="Build"/>), or null with why. Never throws: a
    /// failure is the reason.
    /// </summary>
    public static PermanentUnlockMenu.Metadata? Request(MenuData data, out string problem, out bool none)
    {
        try
        {
            return Build(data.Flag(ForceKey), data.Flag(TransparentKey), data.Flag(SkipExitKey), out problem, out none);
        }
        catch (System.Exception ex)
        {
            problem = $"this player's unlocks couldn't be made into the menu's data ({ModLog.SafeErrorText(ex)})";
            none = false;
            return null;
        }
    }

    /// <summary>
    /// Friend: the data of this game's own new-unlocks menu at a run's end the host's game showed none for
    /// (<see cref="OwnUnlocks"/>): this player's own unlocks of the run, made by the game's converter, with the flags the
    /// game's own <c>UnlockUIHelper.ShowPermanentUnlockMenu</c> gives it (none set), except its exit's loading transition:
    /// the way home shows that itself before its load, as it does after a run's end with no screen (nothing is left on this
    /// screen should the way home not come). Null with why, or with <paramref name="none"/> when there's nothing to show.
    /// Never throws: a failure is the reason.
    /// </summary>
    public static PermanentUnlockMenu.Metadata? OwnScreenRequest(out string problem, out bool none)
    {
        try
        {
            return Build(forceRelics: false, transparent: false, skipExit: true, out problem, out none);
        }
        catch (System.Exception ex)
        {
            problem = $"this player's unlocks couldn't be made into the menu's data ({ModLog.SafeErrorText(ex)})";
            none = false;
            return null;
        }
    }

    /// <summary>
    /// Friend: the menu's data with this player's own unlocks, made by the game's converter, or null with why. Null with
    /// <paramref name="none"/> when there's nothing of this player's to show (a note instead).
    /// </summary>
    private static PermanentUnlockMenu.Metadata? Build(bool forceRelics, bool transparent, bool skipExit, out string problem, out bool none)
    {
        problem = string.Empty;
        none = false;
        string? screenRun = ScreenRun();
        List<string> own = Own.Take(screenRun, out int dropped);
        AddKept(own, screenRun);
        if (dropped > 0)
        {
            ModLog.Info($"Menus: {dropped} of this player's new unlock(s) came in the host's earlier run, whose new-unlocks screen never opened; " +
                "not shown on this run's (they're on this profile).");
        }

        if (own.Count == 0)
        {
            none = true;
            return null;
        }

        CampaignState? campaign = MenuGame.Campaign();
        Il2CppData.RunData? run = campaign?.runData;
        UnlockSystem? unlocks = ProgressFriend.FromProject<UnlockSystem>();
        if (campaign == null || run == null || unlocks == null)
        {
            problem = campaign == null || run == null ? "this PC holds no run to show its unlocks with" : "the game's unlock list isn't loaded";
            return null;
        }

        Il2CppList.List<PermanentUnlockData>? converted = Convert(campaign, run, unlocks, own);
        if (converted == null || converted.Count == 0)
        {
            // Unlocks the game keeps off this screen (or ids it doesn't have): nothing to show.
            none = true;
            return null;
        }

        return new PermanentUnlockMenu.Metadata
        {
            Unlocks = converted,
            ForceRelicsAsNewStartingRelics = forceRelics,
            UseTransparentBackground = transparent,
            SkipExitLoadingTransition = skipExit,
        };
    }

    /// <summary>The game's converter over this player's list (the run's own stored set put back after, whatever happens).</summary>
    private static Il2CppList.List<PermanentUnlockData>? Convert(CampaignState campaign, Il2CppData.RunData run, UnlockSystem unlocks, List<string> own)
    {
        Il2CppList.HashSet<string>? hosts = run.storedUnlocks;
        var mine = new Il2CppList.HashSet<string>();
        foreach (string id in own)
        {
            mine.Add(id);
        }

        Converting = true;
        try
        {
            run.storedUnlocks = mine;
            return UnlockUIHelper.ConvertStoredUnlocks(campaign, unlocks);
        }
        finally
        {
            run.storedUnlocks = hosts!;
            Converting = false;
        }
    }

    /// <summary>
    /// The words for the log: "3 of this player's own 4 new unlock(s) of the host's run (a, b, c, d; 1 not on this screen: the
    /// game keeps it off; the host has 2)". The game's converter takes names in and gives its own data back, so which one it
    /// left out isn't told apart.
    /// </summary>
    public static string Describe(MenuData data, PermanentUnlockMenu.Metadata request)
    {
        int shown = request.Unlocks?.Count ?? 0;
        string? run = ScreenRun();
        List<string> ids = OwnOf(run);
        int own = ids.Count;
        int earlier = own - Own.Peek(run).Count;
        string off = own > shown ? $"; {own - shown} not on this screen: the game keeps it off" : string.Empty;
        string kept = earlier > 0 ? $"; {earlier} from the run's earlier sessions, kept in this PC's progress ledger" : string.Empty;
        return $"{shown} of this player's own {own} new unlock(s) of the host's run ({Names(ids)}{off}{kept}; the host has {data.Whole(CountKey)})";
    }

    /// <summary>
    /// Host, once a menu: what its new-unlocks menu lists, to contrast with each friend's own. The names come
    /// from the run's stored unlocks, which the game's converter made the menu's list from (hidden ones are left off its count).
    /// </summary>
    public static void NameForLog(string id, uint serial, MenuData data)
    {
        string names = string.Empty;
        try
        {
            var stored = new List<string>();
            if (MenuGame.Campaign()?.runData?.storedUnlocks is { } set)
            {
                Il2CppList.HashSet<string>.Enumerator items = set.GetEnumerator();
                while (items.MoveNext() && stored.Count < MaxOwn)
                {
                    stored.Add(items.Current);
                }
            }

            names = stored.Count > 0 ? $" ({Names(stored)} stored in the run)" : string.Empty;
        }
        catch (System.Exception ex)
        {
            names = $" (their names couldn't be read: {ex.GetType().Name})";
        }

        ModLog.Info($"Menus: the host's {id} (#{serial}) lists {data.Whole(CountKey)} unlock(s){names}.");
    }

    /// <summary>
    /// Following stops: the unlocks noted for a new-unlocks screen that hasn't come here are said, before they go from
    /// memory: those the progress ledger keeps for their run's screen (a later session of the run shows them, if this player
    /// is there for its end), and those it doesn't (the squad menu's grants before their run was known, or a run whose screen
    /// already came). When the host's run ended here and no new-unlocks menu came after it (the host's game shows one only
    /// when the run unlocked something on its own profile), that's said instead. No game call.
    /// </summary>
    public static void SayForgotten(string reason)
    {
        List<string> noted = Own.Peek(null);
        if (noted.Count == 0)
        {
            return;
        }

        List<string> kept = noted.Where(Kept.Contains).ToList();
        List<string> gone = noted.Where(id => !Kept.Contains(id)).ToList();
        if (kept.Count > 0 && _runEnded && !_screenCame)
        {
            ModLog.Info($"Menus: {kept.Count} of this player's new unlock(s) in {LogNames.HostTag()}'s run ({Names(kept)}) had no new-unlocks screen here ({reason}): that run " +
                $"is over, and no new-unlocks menu of {LogNames.HostTag()}'s came here after its run-end menu (its game shows one only when the run unlocked something on its " +
                "own profile). They're on this profile, each toasted when it came.");
        }
        else if (kept.Count > 0)
        {
            ModLog.Info($"Menus: {kept.Count} of this player's new unlock(s) in {LogNames.HostTag()}'s run ({Names(kept)}) stay in this PC's progress ledger ({reason}): " +
                "if this player is there for that run's end, its new-unlocks screen lists them, in a later session too.");
        }

        if (gone.Count > 0)
        {
            ModLog.Info($"Menus: {gone.Count} new unlock(s) noted for this player's next new-unlocks screen ({Names(gone)}) are forgotten ({reason}); " +
                $"they're on this profile, but {LogNames.HostTag()}'s new-unlocks screen at this run's end won't list them.");
        }
    }

    private static string Names(List<string> ids) => SafeText.Clean(string.Join(", ", ids), 200);

    /// <summary>
    /// Friend, every frame of an open copy: one step to its next unlock while the host's is further on and this one has more,
    /// on the first frame it's ready (not loading a skater: the game's own next press waits for that too; verified in the game's code:
    /// OnNextClicked). True when it stepped (the caller logs).
    /// </summary>
    public static bool Follow(PermanentUnlockMenu open, int hostIndex, out string step)
    {
        step = string.Empty;
        int index = open.m_CurrentUnlockIndex;
        int count = open.m_AllUnlocks?.Count ?? 0;

        // Its own Show moves to the first unlock (from -1) once it's up: only the host's later steps are followed.
        if (index < 0 || index >= hostIndex || index >= count - 1 || open.m_IsLoadingSkin)
        {
            return false;
        }

        _ = open.GoToNextUnlock();
        step = $"{open.m_CurrentUnlockIndex + 1} of {count} here, the host's {hostIndex + 1}";
        return true;
    }

    /// <summary>Friend: what the menu's own close stops (its reveal, crown and unlock sound), before the base close.</summary>
    public static void BeforeClose(PermanentUnlockMenu open)
    {
        try
        {
            open.CancelReveal();
            open.CancelCrown();
            open.StopSkaterUnlockSfx();
        }
        catch (System.Exception ex)
        {
            // The close goes on: a sound or reveal left running is the most this can leave.
            ModLog.Warn($"Menus: the new-unlocks menu's reveal or sound couldn't be stopped here ({ModLog.SafeErrorText(ex)}); closing it anyway.");
        }
    }

    /// <summary>The host's step to its next unlock, as a menu value (its index; -1 before the first is shown is left out).</summary>
    public static void HostStepped(HostMenus host, PermanentUnlockMenu menu) => host.Step(menu, StepGroup, 0, menu.m_CurrentUnlockIndex);

    /// <summary>The log's words for this player's unlocks of the run the screen is about ("none" or "3: a, b, c").</summary>
    public static string OwnText()
    {
        List<string> own = OwnOf(ScreenRun());
        return own.Count == 0 ? "none" : $"{own.Count}: {SafeText.Clean(string.Join(", ", own), 160)}";
    }
}
#endif
