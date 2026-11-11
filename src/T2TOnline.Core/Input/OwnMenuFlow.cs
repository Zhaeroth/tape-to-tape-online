using System;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Input;

/// <summary>Which of its own game's menus a friend opened while joined (<see cref="OwnMenuFlow"/>).</summary>
public enum OwnMenuKind : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>
    /// "Change my controls": the game's Settings menu with its remapper opened over it at once, as its Remap button opens
    /// it; both close together when the remapper does.
    /// </summary>
    Remapper = 1,

    /// <summary>"Your game's settings": the game's whole Settings menu (volume, video, controls), navigated by the friend.</summary>
    Settings = 2,
}

/// <summary>Where an <see cref="OwnMenuFlow"/> is.</summary>
public enum OwnMenuState : byte
{
    /// <summary>No menu of the friend's own: their device plays online.</summary>
    Idle = 0,

    /// <summary>Their device is lent back to their own game and the menu was asked for; waiting for it to show.</summary>
    Opening = 1,

    /// <summary>The menu shows; their device drives it.</summary>
    Open = 2,

    /// <summary>The menu closed (or is closing); their controls are read again and their device taken back.</summary>
    Closing = 3,
}

/// <summary>What <see cref="OwnMenuFlow.Update"/> says needs doing.</summary>
public enum OwnMenuTick : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The menu didn't show within <see cref="OwnMenuFlow.OpenTimeoutMicros"/>: give up (the fallback), once.</summary>
    OpenTimedOut = 1,

    /// <summary>The menu didn't finish closing within <see cref="OwnMenuFlow.CloseTimeoutMicros"/>: take the device back anyway, once.</summary>
    CloseTimedOut = 2,

    /// <summary>The settings files' grace after the menu closed is over: their allowance can go, once.</summary>
    AllowanceOver = 3,
}

/// <summary>
/// A friend's own menu while joined: their device lent back to their own game, the game's Settings menu (and maybe its remapper) shown, then
/// their controls read again and their device taken back for the session. This keeps the steps' order, the time limits and the save allowance's
/// grace; showing the menu, lending and taking the device back are the plugin's. Game-independent; the caller supplies the time (microseconds).
/// Never throws on a call out of order: it answers false (or <see cref="OwnMenuTick.None"/>).
/// </summary>
public sealed class OwnMenuFlow
{
    /// <summary>How long the menu (and, for <see cref="OwnMenuKind.Remapper"/>, the remapper over it) may take to show.</summary>
    public const long OpenTimeoutMicros = 10_000_000;

    /// <summary>How long a closing menu may take before the device is taken back anyway.</summary>
    public const long CloseTimeoutMicros = 5_000_000;

    /// <summary>
    /// How long the settings files stay allowed after the menu closed: the game stores some settings a moment later
    /// (an async save), and those must still reach the disk.
    /// </summary>
    public const long AllowanceGraceMicros = 3_000_000;

    private long _startedAt;
    private long _shownAt;
    private long _closedAt;
    private long _allowanceUntil = long.MinValue;
    private bool _timeoutSaid;

    /// <summary>Where the flow is.</summary>
    public OwnMenuState State { get; private set; }

    /// <summary>The menu asked for (<see cref="OwnMenuKind.None"/> while <see cref="OwnMenuState.Idle"/>).</summary>
    public OwnMenuKind Kind { get; private set; }

    /// <summary>True while the friend's device is lent to their own game (any state but <see cref="OwnMenuState.Idle"/>).</summary>
    public bool HoldsDevice => State != OwnMenuState.Idle;

    /// <summary>The reason the host is told while the device is lent: <see cref="PadAwayReason.Unplugged"/> while idle.</summary>
    public PadAwayReason AwayReason => Kind switch
    {
        _ when State == OwnMenuState.Idle => PadAwayReason.Unplugged,
        OwnMenuKind.Remapper => PadAwayReason.ChangingControls,
        _ => PadAwayReason.InSettings,
    };

    /// <summary>Menus opened this session.</summary>
    public int Opened { get; private set; }

    /// <summary>Menus that didn't show (the fallback was offered) this session.</summary>
    public int FellBack { get; private set; }

    /// <summary>How long the last finished menu held the device, start to finish, in microseconds (0 before one finished).</summary>
    public long LastHeldMicros { get; private set; }

    /// <summary>When the menu was asked for (meaningful while not <see cref="OwnMenuState.Idle"/>).</summary>
    public long StartedAtMicros => _startedAt;

    /// <summary>
    /// Starts a menu of <paramref name="kind"/>: the caller lends the device and asks for the menu next. False while one is
    /// already under way (the first goes on), or for <see cref="OwnMenuKind.None"/>.
    /// </summary>
    public bool TryStart(OwnMenuKind kind, long nowMicros)
    {
        if (State != OwnMenuState.Idle || kind == OwnMenuKind.None)
        {
            return false;
        }

        State = OwnMenuState.Opening;
        Kind = kind;
        _startedAt = nowMicros;
        _shownAt = 0;
        _closedAt = 0;
        _timeoutSaid = false;
        _allowanceUntil = long.MaxValue; // while it's under way; set to the grace's end when it finishes
        return true;
    }

    /// <summary>The menu shows (for <see cref="OwnMenuKind.Remapper"/>: the remapper does). False unless it was opening.</summary>
    public bool Shown(long nowMicros)
    {
        if (State != OwnMenuState.Opening)
        {
            return false;
        }

        State = OwnMenuState.Open;
        _shownAt = nowMicros;
        _timeoutSaid = false;
        Opened++;
        return true;
    }

    /// <summary>The menu closed or is closing (the friend backed out, or the game took it away). False unless it was opening or open.</summary>
    public bool Closed(long nowMicros)
    {
        if (State is not (OwnMenuState.Opening or OwnMenuState.Open))
        {
            return false;
        }

        State = OwnMenuState.Closing;
        _closedAt = nowMicros;
        _timeoutSaid = false;
        return true;
    }

    /// <summary>
    /// The device is taken back (the controls read again): idle again, the settings files allowed for
    /// <see cref="AllowanceGraceMicros"/> more. False unless it was closing.
    /// </summary>
    public bool Finish(long nowMicros)
    {
        if (State != OwnMenuState.Closing)
        {
            return false;
        }

        LastHeldMicros = Math.Max(0, nowMicros - _startedAt);
        _allowanceUntil = nowMicros + AllowanceGraceMicros;
        Reset();
        return true;
    }

    /// <summary>
    /// The menu couldn't show (<paramref name="fellBack"/>: the fallback was offered) or the session ended under it: idle at
    /// once, whatever the state, with the same grace for the settings files. False when it was idle already.
    /// </summary>
    public bool Abort(long nowMicros, bool fellBack)
    {
        if (State == OwnMenuState.Idle)
        {
            return false;
        }

        FellBack += fellBack ? 1 : 0;
        LastHeldMicros = Math.Max(0, nowMicros - _startedAt);
        _allowanceUntil = nowMicros + AllowanceGraceMicros;
        Reset();
        return true;
    }

    /// <summary>
    /// True while the friend's own settings files may be written: while a menu is under way, and for
    /// <see cref="AllowanceGraceMicros"/> after it finished.
    /// </summary>
    public bool AllowsSettingsSaves(long nowMicros) => State != OwnMenuState.Idle || nowMicros < _allowanceUntil;

    /// <summary>How long the menu has shown, in microseconds (0 unless open or closing after it showed).</summary>
    public long ShownMicros(long nowMicros) => _shownAt == 0 || State == OwnMenuState.Idle ? 0 : Math.Max(0, (_closedAt != 0 ? _closedAt : nowMicros) - _shownAt);

    /// <summary>Call every frame: a menu that didn't show or didn't finish closing in time, and the end of the settings files' grace (each said once).</summary>
    public OwnMenuTick Update(long nowMicros)
    {
        switch (State)
        {
            case OwnMenuState.Opening when !_timeoutSaid && nowMicros - _startedAt > OpenTimeoutMicros:
                _timeoutSaid = true;
                return OwnMenuTick.OpenTimedOut;
            case OwnMenuState.Closing when !_timeoutSaid && nowMicros - _closedAt > CloseTimeoutMicros:
                _timeoutSaid = true;
                return OwnMenuTick.CloseTimedOut;
            case OwnMenuState.Idle when _allowanceUntil != long.MinValue && nowMicros >= _allowanceUntil:
                _allowanceUntil = long.MinValue;
                return OwnMenuTick.AllowanceOver;
            default:
                return OwnMenuTick.None;
        }
    }

    /// <summary>The generation after <paramref name="current"/> for a friend's controls: 1 to 255, then round again (0 is never used).</summary>
    public static byte NextGeneration(byte current) => (byte)((current % byte.MaxValue) + 1);

    private void Reset()
    {
        State = OwnMenuState.Idle;
        Kind = OwnMenuKind.None;
        _shownAt = 0;
        _closedAt = 0;
        _timeoutSaid = false;
    }
}
