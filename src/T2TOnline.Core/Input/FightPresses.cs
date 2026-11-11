using System;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Input;

/// <summary>
/// One player's fight verbs over one brawl, from their played frames (<see cref="FightInputMap"/>): how often each was
/// pressed and how long each was held, in fixed memory. The friend's PC keeps one for what it sent, the host one per friend
/// for what it played into their controller, so the two lines of a brawl can be laid side by side. Nothing allocates
/// until <see cref="Describe"/>.
/// </summary>
public sealed class FightPresses
{
    // The verbs are bits 0 to 3 of FightActions, in the log's order.
    private const int Verbs = 4;

    private readonly int[] _presses = new int[Verbs];
    private readonly long[] _heldMicros = new long[Verbs];
    private FightActions _held;
    private long _last;
    private long _began;
    private long _ended;

    /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public bool Running { get; private set; }

    /// <summary>When the latest block (either) was pressed, or <see cref="long.MinValue"/> before any this brawl.</summary>
    public long LastBlockPressMicros { get; private set; } = long.MinValue;

    /// <summary>The brawl's length so far (to <see cref="End"/> once it ended), in microseconds.</summary>
    public long DurationMicros => Running ? Math.Max(0, _last - _began) : Math.Max(0, _ended - _began);

    /// <summary>Starts a brawl's count at <paramref name="nowMicros"/> (anything counted before is forgotten).</summary>
    public void Begin(long nowMicros)
    {
        Array.Clear(_presses);
        Array.Clear(_heldMicros);
        _held = FightActions.None;
        _began = _last = _ended = nowMicros;
        LastBlockPressMicros = long.MinValue;
        Running = true;
    }

    /// <summary>
    /// One played frame: the verbs it holds (<paramref name="held"/>) and those tapped since the last frame
    /// (<paramref name="tapped"/>: let go and pressed again, a press too). Returns the verbs pressed in it. Ignored
    /// (returns none) while not running; a clock that goes back adds no held time.
    /// </summary>
    public FightActions Feed(FightActions held, FightActions tapped, long nowMicros)
    {
        if (!Running)
        {
            return FightActions.None;
        }

        AddHeld(nowMicros);
        held &= FightActions.All;
        FightActions pressed = ((held & ~_held) | tapped) & FightActions.All;
        for (int i = 0; i < Verbs; i++)
        {
            _presses[i] += ((int)pressed >> i) & 1;
        }

        if ((pressed & FightActions.Blocks) != 0)
        {
            LastBlockPressMicros = nowMicros;
        }

        _held = held;
        return pressed;
    }

    /// <summary>Ends the brawl's count at <paramref name="nowMicros"/> (what's still held counts up to then).</summary>
    public void End(long nowMicros)
    {
        if (!Running)
        {
            return;
        }

        AddHeld(nowMicros);
        _ended = Math.Max(_last, nowMicros);
        _held = FightActions.None;
        Running = false;
    }

    /// <summary>Times <paramref name="verb"/> (one verb) was pressed this brawl.</summary>
    public int PressesOf(FightActions verb) => FightInputMap.IndexOf(verb) is int i and >= 0 ? _presses[i] : 0;

    /// <summary>How long <paramref name="verb"/> (one verb) was held this brawl, in microseconds.</summary>
    public long HeldMicrosOf(FightActions verb) => FightInputMap.IndexOf(verb) is int i and >= 0 ? _heldMicros[i] : 0;

    /// <summary>
    /// "block high 3 time(s) (held 2.1 s) on RightBumper, block low 0 on LeftBumper, jab 12 on X, uppercut 5 on B", each
    /// verb's elements from <paramref name="where"/> ("nothing" for an unbound one).
    /// </summary>
    public string Describe(Func<FightActions, string> where)
    {
        ArgumentNullException.ThrowIfNull(where);
        var text = new StringBuilder();
        for (int i = 0; i < Verbs; i++)
        {
            var verb = (FightActions)(1 << i);
            text.Append(text.Length == 0 ? string.Empty : ", ").Append(FightInputMap.NameOf(verb)).Append(' ')
                .Append(_presses[i].ToString(CultureInfo.InvariantCulture)).Append(" time(s)");
            if ((verb & FightActions.Blocks) != 0 && _heldMicros[i] > 0)
            {
                text.Append(string.Create(CultureInfo.InvariantCulture, $" (held {_heldMicros[i] / 1_000_000.0:0.0} s)"));
            }

            text.Append(" on ").Append(where(verb));
        }

        return text.ToString();
    }

    private void AddHeld(long nowMicros)
    {
        long step = nowMicros - _last;
        if (step > 0)
        {
            for (int i = 0; i < Verbs; i++)
            {
                _heldMicros[i] += (((int)_held >> i) & 1) * step;
            }

            _last = nowMicros;
        }
    }
}
