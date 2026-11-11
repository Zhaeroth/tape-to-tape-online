using System;
using System.Globalization;
using System.Text;
using T2TOnline.Core.Input;

namespace T2TOnline.Core.Hosting;

/// <summary>How an AI punch's apex met a remote friend's fighter in its reach (the plugin's FightGrace decides; D35).</summary>
public enum ApexMeeting
{
    /// <summary>Their block was up, of the type that stops the punch: the game blocked it at once.</summary>
    BlockUp,

    /// <summary>Their block was up, of the other type: the game dealt partial damage at once.</summary>
    BlockUpOtherWay,

    /// <summary>They couldn't block (punching, staggered or knocked down): the punch went on at once.</summary>
    CouldNotBlock,

    /// <summary>The punch waited for their block (the fight grace window); its end comes with <see cref="BrawlRecord.WaitEnded"/>.</summary>
    Waited,

    /// <summary>The punch would have waited, but the fight grace window is switched off: it went on at once.</summary>
    WindowOff,
}

/// <summary>
/// The host's record of one friend in one brawl (the log's "Brawl:" line at its end): their fight verbs as played into
/// their controller (<see cref="Presses"/>), how each AI punch's apex met their fighter, how each wait for their block
/// ended, and, for a wait that timed out, how long after the window's end their next block arrived (up to
/// <see cref="LateBlockMaxMicros"/>): the number that says whether the window is too short for what they see. Fixed
/// memory; nothing allocates until <see cref="DescribeApexes"/>.
/// </summary>
public sealed class BrawlRecord
{
    /// <summary>The longest after a timed-out wait that a block still counts as that wait's late block.</summary>
    public const long LateBlockMaxMicros = 1_000_000;

    /// <summary>Late blocks named one by one in the line; the rest are in its range.</summary>
    public const int LateBlocksNamed = 6;

    private readonly int[] _meetings = new int[Enum.GetValues<ApexMeeting>().Length];
    private readonly int[] _outcomes = new int[Enum.GetValues<GraceOutcome>().Length];
    private readonly long[] _late = new long[LateBlocksNamed];
    private int _lateCount;
    private long _lateMin;
    private long _lateMax;
    private int _noLateBlock;
    private long _pendingDeadline = long.MinValue;

    /// <summary>Their fight verbs as played into their controller this brawl.</summary>
    public FightPresses Presses { get; } = new();

    /// <summary>AI punches whose apex met their fighter this brawl.</summary>
    public int Reached { get; private set; }

    /// <summary>Starts a brawl's record at <paramref name="nowMicros"/>.</summary>
    public void Begin(long nowMicros)
    {
        Presses.Begin(nowMicros);
        Array.Clear(_meetings);
        Array.Clear(_outcomes);
        Array.Clear(_late);
        _lateCount = _noLateBlock = 0;
        _lateMin = _lateMax = 0;
        _pendingDeadline = long.MinValue;
        Reached = 0;
    }

    /// <summary>An AI punch's apex met their fighter.</summary>
    public void Met(ApexMeeting meeting)
    {
        if ((uint)meeting < (uint)_meetings.Length)
        {
            _meetings[(int)meeting]++;
            Reached++;
        }
    }

    /// <summary>Times an apex met their fighter in this way this brawl.</summary>
    public int MetCount(ApexMeeting meeting) => (uint)meeting < (uint)_meetings.Length ? _meetings[(int)meeting] : 0;

    /// <summary>Waits that ended with <paramref name="outcome"/> this brawl.</summary>
    public int Ended(GraceOutcome outcome) => (uint)outcome < (uint)_outcomes.Length ? _outcomes[(int)outcome] : 0;

    /// <summary>Late blocks measured this brawl (see <see cref="BrawlRecord"/>).</summary>
    public int LateBlocks => _lateCount;

    /// <summary>Timed-out waits after which no block came within <see cref="LateBlockMaxMicros"/> (or before the next one timed out, or the brawl ended).</summary>
    public int NoLateBlock => _noLateBlock;

    /// <summary>The <paramref name="index"/>th late block measured, in microseconds after the window's end (negative: pressed in its last frame, too late for the game to raise it).</summary>
    public long LateBlock(int index) => index >= 0 && index < Math.Min(_lateCount, LateBlocksNamed) ? _late[index] : 0;

    /// <summary>
    /// A wait for their block ended (<paramref name="outcome"/>; its window ran from <paramref name="startedMicros"/> to
    /// <paramref name="deadlineMicros"/>, on the clock of <see cref="Feed"/>; told when the timeout was seen, a frame at most
    /// after the deadline). A timeout starts the late-block measure: a block pressed since the wait began (in the frame the
    /// timeout was seen, too late for the game to raise it first) counts at once; otherwise the next one does.
    /// </summary>
    public void WaitEnded(GraceOutcome outcome, long startedMicros, long deadlineMicros)
    {
        if ((uint)outcome < (uint)_outcomes.Length)
        {
            _outcomes[(int)outcome]++;
        }

        if (outcome != GraceOutcome.TimedOut)
        {
            return;
        }

        if (_pendingDeadline != long.MinValue)
        {
            _noLateBlock++; // the earlier one got no block before this one timed out
            _pendingDeadline = long.MinValue;
        }

        long lastBlock = Presses.LastBlockPressMicros;
        if (lastBlock != long.MinValue && lastBlock >= startedMicros)
        {
            NoteLate(lastBlock - deadlineMicros);
            return;
        }

        _pendingDeadline = deadlineMicros;
    }

    /// <summary>One played frame (<see cref="FightPresses.Feed"/>); a block in it may be a timed-out wait's late block.</summary>
    public FightActions Feed(FightActions held, FightActions tapped, long nowMicros)
    {
        FightActions pressed = Presses.Feed(held, tapped, nowMicros);
        if (_pendingDeadline == long.MinValue)
        {
            return pressed;
        }

        long after = nowMicros - _pendingDeadline;
        if (after > LateBlockMaxMicros)
        {
            _noLateBlock++;
            _pendingDeadline = long.MinValue;
        }
        else if ((pressed & FightActions.Blocks) != 0)
        {
            NoteLate(after);
            _pendingDeadline = long.MinValue;
        }

        return pressed;
    }

    /// <summary>Ends the brawl's record at <paramref name="nowMicros"/>.</summary>
    public void End(long nowMicros)
    {
        Presses.End(nowMicros);
        if (_pendingDeadline != long.MinValue)
        {
            _noLateBlock++;
            _pendingDeadline = long.MinValue;
        }
    }

    /// <summary>
    /// "9 AI punch(es) had their fighter in reach at the apex: their block was up for 2 (1 of them the other way: partial damage), 1 came
    /// while they couldn't block, 6 waited for their block: 0 blocked in time, 0 the other way, 6 timed out (their next
    /// block came +140, +380 ms after the window; none within 1 s after 4)"; "no AI punch had their fighter in reach at its apex".
    /// </summary>
    public string DescribeApexes()
    {
        if (Reached == 0)
        {
            return "no AI punch had their fighter in reach at its apex";
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        int up = MetCount(ApexMeeting.BlockUp) + MetCount(ApexMeeting.BlockUpOtherWay);
        var text = new StringBuilder(string.Create(c, $"{Reached} AI punch(es) had their fighter in reach at the apex: their block was up for {up}"));
        if (MetCount(ApexMeeting.BlockUpOtherWay) > 0)
        {
            text.Append(string.Create(c, $" ({MetCount(ApexMeeting.BlockUpOtherWay)} of them the other way: partial damage)"));
        }

        text.Append(string.Create(c, $", {MetCount(ApexMeeting.CouldNotBlock)} came while they couldn't block"));
        if (MetCount(ApexMeeting.WindowOff) > 0)
        {
            text.Append(string.Create(c, $", {MetCount(ApexMeeting.WindowOff)} went on at once (the fight grace window is off)"));
        }

        text.Append(string.Create(c, $", {MetCount(ApexMeeting.Waited)} waited for their block"));
        if (MetCount(ApexMeeting.Waited) == 0)
        {
            return text.ToString();
        }

        text.Append(string.Create(c, $": {Ended(GraceOutcome.Blocked)} blocked in time, {Ended(GraceOutcome.PartlyBlocked)} the other way, {Ended(GraceOutcome.TimedOut)} timed out"));
        if (Ended(GraceOutcome.TimedOut) > 0)
        {
            text.Append(" (").Append(DescribeLate()).Append(')');
        }

        int early = Ended(GraceOutcome.Unblockable);
        int letGo = Ended(GraceOutcome.SwitchedOff) + Ended(GraceOutcome.SessionEnded);
        int interrupted = Ended(GraceOutcome.Interrupted);
        text.Append(early > 0 ? string.Create(c, $", {early} went on early (they couldn't block any more)") : string.Empty)
            .Append(letGo > 0 ? string.Create(c, $", {letGo} let go (the window switched off or the session ended)") : string.Empty)
            .Append(interrupted > 0 ? string.Create(c, $", {interrupted} interrupted") : string.Empty);
        return text.ToString();
    }

    private string DescribeLate()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string none = _noLateBlock > 0 ? string.Create(c, $"none within {LateBlockMaxMicros / 1_000_000.0:0.#} s after {_noLateBlock}") : string.Empty;
        if (_lateCount == 0)
        {
            return none.Length > 0 ? $"no block came after the window: {none}" : "no block came after the window";
        }

        var named = new StringBuilder();
        for (int i = 0; i < Math.Min(_lateCount, LateBlocksNamed); i++)
        {
            named.Append(i == 0 ? string.Empty : ", ").Append(string.Create(c, $"{_late[i] / 1000.0:+0;-0;0}"));
        }

        string more = _lateCount > LateBlocksNamed ? string.Create(c, $" and {_lateCount - LateBlocksNamed} more (range {_lateMin / 1000.0:+0;-0;0} to {_lateMax / 1000.0:+0;-0;0})") : string.Empty;
        return $"their next block came {named} ms after the window{more}{(none.Length > 0 ? $"; {none}" : string.Empty)}";
    }

    private void NoteLate(long micros)
    {
        if (_lateCount < LateBlocksNamed)
        {
            _late[_lateCount] = micros;
        }

        _lateMin = _lateCount == 0 ? micros : Math.Min(_lateMin, micros);
        _lateMax = _lateCount == 0 ? micros : Math.Max(_lateMax, micros);
        _lateCount++;
    }
}
