using System;

namespace T2TOnline.Core.Campaign;

/// <summary>What a friend's way home after a host's run does about this player's own new-unlocks screen (<see cref="OwnUnlocksScreen.Decide"/>).</summary>
public enum OwnUnlocksChoice : byte
{
    /// <summary>No screen of this player's own: the way home goes on.</summary>
    GoHome = 0,

    /// <summary>Shared progress that's already here applies first: ask again next frame.</summary>
    Wait = 1,

    /// <summary>This game's own new-unlocks screen shows this player's new unlocks of the run before the way home.</summary>
    Show = 2,
}

/// <summary>
/// What a friend's game knows as its way home after a host's run starts (<see cref="OwnUnlocksScreen.Decide"/>).
/// </summary>
/// <param name="RunEnded">The host's run ended here: its run-end menu at the run's end came up on this PC.</param>
/// <param name="HostScreenCame">The host's new-unlocks menu came up here after it (this player's own unlocks showed on its copy).</param>
/// <param name="OwnUnlocks">This player's own new unlocks of the run (this session's and the progress ledger's).</param>
/// <param name="BatchesWaiting">The host's shared progress batches still waiting here (they may hold more of this player's unlocks).</param>
/// <param name="WaitedMicros">How long the way home has waited for them so far.</param>
/// <param name="MenuReady">This game's new-unlocks menu can be shown and followed safely (its patches are in, its UI is up).</param>
public readonly record struct OwnUnlocksFacts(bool RunEnded, bool HostScreenCame, int OwnUnlocks, int BatchesWaiting, long WaitedMicros, bool MenuReady);

/// <summary>Where a friend's own new-unlocks screen is (<see cref="OwnUnlocksScreen"/>).</summary>
public enum OwnUnlocksState : byte
{
    /// <summary>None this run's end yet.</summary>
    Idle = 0,

    /// <summary>Asked for; waiting for the game to show it. The way home waits.</summary>
    Opening = 1,

    /// <summary>Up, driven by the player's own pad, keyboard or mouse. The way home waits.</summary>
    Open = 2,

    /// <summary>Closed, given up or not wanted: this run's end has had its turn (never twice).</summary>
    Done = 3,
}

/// <summary>What <see cref="OwnUnlocksScreen.Update"/> says needs doing.</summary>
public enum OwnUnlocksTick : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The screen didn't show within <see cref="OwnUnlocksScreen.OpenTimeoutMicros"/>: give it up, once.</summary>
    OpenTimedOut = 1,

    /// <summary>The screen has been up <see cref="OwnUnlocksScreen.MaxOpenMicros"/>: close it with its own close, once.</summary>
    OpenTooLong = 2,
}

/// <summary>
/// A friend's own new-unlocks screen at the end of a host's run that the host's game showed none for (D42: each player's own
/// progress). The game shows its new-unlocks menu only when the run unlocked something on the profile that played it, so a
/// host whose profile unlocked nothing new shows none, while a friend whose own profile did would, on a couch of their own,
/// see theirs. The host's run-end menu's exit says which (<see cref="HostShowsNone"/>): then this game's own menu shows
/// them, after the friend's copy of that run-end menu exits as the game's does before its new-unlocks menu
/// (<see cref="ExitBefore"/>), driven by the friend's own device; the friend's way home waits for it to close (a run-end
/// close the friend didn't see is decided as the way home begins); the host's game never waits. When the host's game
/// shows its menu, the friend's copy of it shows this player's own unlocks: never a second screen. This keeps the
/// decision, the order and the time limits; showing the menu, lending the device and the way home are the plugin's.
/// Game-independent; the caller supplies the time (microseconds). Never throws on a call out of order: it answers false
/// (or <see cref="OwnUnlocksTick.None"/>).
/// </summary>
public sealed class OwnUnlocksScreen
{
    /// <summary>The longest the way home waits for shared progress already here before it decides (it may hold this player's unlocks).</summary>
    public const long BatchWaitMicros = 3_000_000;

    /// <summary>How long the menu may take to show once asked for.</summary>
    public const long OpenTimeoutMicros = 10_000_000;

    /// <summary>The longest the screen holds the way home: then it closes with its own close (a player who walked away).</summary>
    public const long MaxOpenMicros = 600_000_000;

    private long _startedAt;
    private long _shownAt;
    private bool _tickSaid;

    /// <summary>Where the screen is.</summary>
    public OwnUnlocksState State { get; private set; }

    /// <summary>True while the way home waits for the screen (asked for, or up).</summary>
    public bool Holding => State is OwnUnlocksState.Opening or OwnUnlocksState.Open;

    /// <summary>Screens shown in all.</summary>
    public int Shown { get; private set; }

    /// <summary>
    /// What the way home does now, and why when it goes home after a run that ended here with no new-unlocks menu of the
    /// host's (empty when there's nothing to say: no run's end here, or the host's menu showed this player's own).
    /// </summary>
    public static OwnUnlocksChoice Decide(in OwnUnlocksFacts facts, out string why)
    {
        why = string.Empty;
        if (!facts.RunEnded || facts.HostScreenCame)
        {
            return OwnUnlocksChoice.GoHome;
        }

        if (facts.BatchesWaiting > 0 && facts.WaitedMicros < BatchWaitMicros)
        {
            return OwnUnlocksChoice.Wait;
        }

        if (facts.OwnUnlocks <= 0)
        {
            why = "this player unlocked nothing new in the run either";
            return OwnUnlocksChoice.GoHome;
        }

        if (!facts.MenuReady)
        {
            why = "this game's new-unlocks menu can't be shown safely here (see the menus' start-up line)";
            return OwnUnlocksChoice.GoHome;
        }

        return OwnUnlocksChoice.Show;
    }

    /// <summary>
    /// True when the host's run-end menu's exit (<paramref name="hostExit"/>, at a run's end) says no new-unlocks menu follows
    /// on its screen: the game's run-end menu exits with the loading transition then, and with the progress one when its
    /// new-unlocks menu comes next (<c>RunEndMenu.Hide</c>, by <c>UnlockUIHelper.HasVisiblePermanentUnlocks</c>).
    /// </summary>
    public static bool HostShowsNone(MenuTransition hostExit) => hostExit == MenuTransition.Loading;

    /// <summary>
    /// The transition the friend's copy of the host's run-end menu exits with: the game's progress transition when this
    /// player's own new-unlocks screen comes next (<paramref name="choice"/>), as the game's own run-end menu exits before
    /// its new-unlocks menu; otherwise the host's (<paramref name="hostExit"/>).
    /// </summary>
    public static MenuTransition ExitBefore(MenuTransition hostExit, OwnUnlocksChoice choice) =>
        HostShowsNone(hostExit) && choice == OwnUnlocksChoice.Show ? MenuTransition.Progress : hostExit;

    /// <summary>The screen is asked for (the caller lends the device and asks the game next). False unless idle.</summary>
    public bool TryStart(long nowMicros)
    {
        if (State != OwnUnlocksState.Idle)
        {
            return false;
        }

        State = OwnUnlocksState.Opening;
        _startedAt = nowMicros;
        _shownAt = 0;
        _tickSaid = false;
        return true;
    }

    /// <summary>The game shows it. False unless it was opening.</summary>
    public bool Up(long nowMicros)
    {
        if (State != OwnUnlocksState.Opening)
        {
            return false;
        }

        State = OwnUnlocksState.Open;
        _shownAt = nowMicros;
        _tickSaid = false;
        Shown++;
        return true;
    }

    /// <summary>
    /// This run's end has had its turn: the screen closed, was given up, or wasn't wanted (decided against). Whatever the
    /// state; false when it was done already.
    /// </summary>
    public bool Finish()
    {
        if (State == OwnUnlocksState.Done)
        {
            return false;
        }

        State = OwnUnlocksState.Done;
        _tickSaid = false;
        return true;
    }

    /// <summary>A new run's end here (the host's run-end menu at its run's end came up again): its own turn. Not while one holds the way home.</summary>
    public bool NextRunEnd()
    {
        if (Holding)
        {
            return false;
        }

        State = OwnUnlocksState.Idle;
        _startedAt = 0;
        _shownAt = 0;
        _tickSaid = false;
        return true;
    }

    /// <summary>How long it has been asked for, or up, in microseconds (0 when it isn't).</summary>
    public long HeldMicros(long nowMicros) => Holding ? Math.Max(0, nowMicros - _startedAt) : 0;

    /// <summary>How long it has been up, in microseconds (0 unless open).</summary>
    public long UpMicros(long nowMicros) => State == OwnUnlocksState.Open ? Math.Max(0, nowMicros - _shownAt) : 0;

    /// <summary>Call every frame while it holds the way home: a screen that didn't show, or has been up too long (each said once).</summary>
    public OwnUnlocksTick Update(long nowMicros)
    {
        switch (State)
        {
            case OwnUnlocksState.Opening when !_tickSaid && nowMicros - _startedAt > OpenTimeoutMicros:
                _tickSaid = true;
                return OwnUnlocksTick.OpenTimedOut;
            case OwnUnlocksState.Open when !_tickSaid && nowMicros - _shownAt > MaxOpenMicros:
                _tickSaid = true;
                return OwnUnlocksTick.OpenTooLong;
            default:
                return OwnUnlocksTick.None;
        }
    }
}
