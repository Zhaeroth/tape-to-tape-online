using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Input;

/// <summary>One of the 20 elements of a standard pad the controller check shows (<see cref="PadCheck.Elements"/>).</summary>
/// <param name="Name">Its name on the panel and in the log ("A", "LB", "RT", "D-pad left", "Left stick X").</param>
/// <param name="IsAxis">True for an axis (a stick's X or Y, a trigger).</param>
/// <param name="Index">Its bit in <see cref="PadButtons"/> for a button, its <see cref="PadAxis"/> for an axis.</param>
public readonly record struct PadCheckElement(string Name, bool IsAxis, int Index)
{
    /// <summary>True for a stick's X or Y, read in both directions (a trigger goes one way).</summary>
    public bool TwoWay => IsAxis && Index <= (int)PadAxis.RightStickY;

    /// <summary>The element as a binding names it (gamepad-template terms).</summary>
    public BindingElement Binding => IsAxis ? BindingElement.Axis((PadAxis)Index) : BindingElement.PadButton(Index);

    /// <summary>Its Rewired gamepad template element id.</summary>
    public int TemplateElementId => IsAxis ? GamepadTemplateLayout.AxisElementId((PadAxis)Index) : GamepadTemplateLayout.ButtonElementId(Index);

    /// <summary>A two-way axis's negative direction: "left" for an X, "down" for a Y (-1 is down, <see cref="PadAxis"/>).</summary>
    public string NegativeName => Index is (int)PadAxis.LeftStickX or (int)PadAxis.RightStickX ? "left" : "down";

    /// <summary>A two-way axis's positive direction: "right" for an X, "up" for a Y.</summary>
    public string PositiveName => Index is (int)PadAxis.LeftStickX or (int)PadAxis.RightStickX ? "right" : "up";
}

/// <summary>How far a controller check has come (<see cref="PadCheck.Verdict"/>).</summary>
public enum PadCheckOutcome
{
    /// <summary>Nothing read yet.</summary>
    NothingYet,

    /// <summary>Some elements read, some not yet.</summary>
    Partial,

    /// <summary>All 20 read.</summary>
    Complete,

    /// <summary>Everything the pad has read, but its gamepad template lacks some of a standard pad's elements.</summary>
    Lacking,
}

/// <summary>A controller check's one-line verdict and what it means.</summary>
/// <param name="Outcome">How far the check has come.</param>
/// <param name="Text">The line the panel shows.</param>
public readonly record struct PadCheckVerdict(PadCheckOutcome Outcome, string Text);

/// <summary>Where the actions a controller check shows come from.</summary>
public enum PadCheckMaps
{
    /// <summary>They couldn't be read.</summary>
    Unknown,

    /// <summary>The pad's maps are the game's defaults: a host binds its virtual pad from the game's gamepad template maps.</summary>
    Defaults,

    /// <summary>The player's own maps (remapped): a host binds its virtual pad from them.</summary>
    Own,
}

/// <summary>What the controller check's log line says about the pad besides what was read (all text already cleaned).</summary>
/// <param name="Name">Rewired's name for the pad.</param>
/// <param name="HardwareName">Rewired's hardware name.</param>
/// <param name="Family">The device family the game sees ("a generic pad").</param>
/// <param name="HostReadsAs">The family a host reads it as ("Xbox").</param>
/// <param name="Online">Whether it's what this PC plays with online, and why ("used online", "not used online: ...").</param>
/// <param name="Maps">Where its actions come from.</param>
/// <param name="Differences">With <see cref="PadCheckMaps.Own"/>: how many bindings differ from the game's defaults.</param>
public sealed record PadCheckPad(string Name, string HardwareName, string Family, string HostReadsAs, string Online, PadCheckMaps Maps, int Differences)
{
    /// <summary>The game actions each element gives under its maps (index as in <see cref="PadCheck.Elements"/>), or null: the log line names the triggers'.</summary>
    public IReadOnlyList<string>? ElementActions { get; init; }
}

/// <summary>
/// The F1 panel's controller check (its Controller tab): which of a standard pad's 20 elements (14 buttons, 6 axes, as
/// Rewired's gamepad template gives them and a friend's pad sends them to the host) a pad has, which are active now,
/// which were read at least once since the check started, and the verdict and log line that sum it up. A stick's X or
/// Y counts as read once it went past <see cref="SeenThreshold"/> both ways; a trigger once it went past it. It is there
/// because a pad's trigger can fail to reach the host's game (a Steam Input layout that doesn't send it), and the check
/// lets the player see that beforehand. Each axis's peak is kept too: a trigger that moved but stopped short of the
/// game's 0.50 is named with it, and one that never moved while the other trigger was read gets the fix
/// (<see cref="TriggerCheck.Advice"/>). Reading allocates nothing; the texts are made when asked.
/// </summary>
public sealed class PadCheck
{
    /// <summary>The elements a check covers.</summary>
    public const int ElementCount = PadState.ButtonCount + PadState.AxisCount;

    /// <summary>How far an axis must move to light up as active.</summary>
    public const float ActiveThreshold = 0.25f;

    /// <summary>How far an axis must move to count as read (each way, for a stick).</summary>
    public const float SeenThreshold = PadUsage.AxisThreshold;

    private const int AllElements = (1 << ElementCount) - 1;

    // The panel's order: face buttons, shoulders and triggers, the middle, the stick clicks, the d-pad, the sticks.
    private static readonly PadCheckElement[] Table =
    {
        new("A", false, 0),
        new("B", false, 1),
        new("X", false, 2),
        new("Y", false, 3),
        new("LB", false, 4),
        new("RB", false, 5),
        new("LT", true, (int)PadAxis.LeftTrigger),
        new("RT", true, (int)PadAxis.RightTrigger),
        new("Back", false, 6),
        new("Start", false, 7),
        new("LS click", false, 8),
        new("RS click", false, 9),
        new("D-pad up", false, 10),
        new("D-pad down", false, 11),
        new("D-pad left", false, 12),
        new("D-pad right", false, 13),
        new("Left stick X", true, (int)PadAxis.LeftStickX),
        new("Left stick Y", true, (int)PadAxis.LeftStickY),
        new("Right stick X", true, (int)PadAxis.RightStickX),
        new("Right stick Y", true, (int)PadAxis.RightStickY),
    };

    private readonly float[] _values = new float[ElementCount];
    private readonly float[] _peaks = new float[ElementCount];
    private readonly int[] _peakSteps = new int[ElementCount];
    private int _active;
    private int _seen;
    private int _seenNegative;
    private int _seenPositive;

    /// <summary>
    /// A check of a pad whose template lacks the elements in <paramref name="missing"/> (a mask over <see cref="Elements"/>;
    /// see <see cref="MissingMask"/>). <paramref name="steamInput"/>: the pad is set up by Steam Input (Rewired reads it as its
    /// "Steam Controller" profile), whose layout for the game is where a trigger that doesn't come is fixed.
    /// </summary>
    public PadCheck(int missing = 0, bool steamInput = false)
    {
        Missing = missing & AllElements;
        SteamInput = steamInput;
    }

    /// <summary>True when the pad is set up by Steam Input.</summary>
    public bool SteamInput { get; }

    /// <summary>The 20 elements, in the panel's order (an element's index in a check is its index here).</summary>
    public static IReadOnlyList<PadCheckElement> Elements => Table;

    /// <summary>The elements the pad's gamepad template lacks (a mask over <see cref="Elements"/>): never active, never read.</summary>
    public int Missing { get; }

    /// <summary>How many elements the pad's template lacks.</summary>
    public int MissingCount => Count(Missing);

    /// <summary>How many elements the pad has (20 less <see cref="MissingCount"/>).</summary>
    public int Readable => ElementCount - MissingCount;

    /// <summary>How many elements were read.</summary>
    public int SeenCount => Count(_seen);

    /// <summary>True once anything was read (a stick one way counts).</summary>
    public bool AnyRead => (_seen | _seenNegative | _seenPositive) != 0;

    /// <summary>The element that became active last, or -1: the panel names its actions.</summary>
    public int LastActive { get; private set; } = -1;

    /// <summary>Goes up whenever what was read changes (a new element, or a stick's other way), so a caller can remake its texts only then.</summary>
    public int Version { get; private set; }

    /// <summary>The mask over <see cref="Elements"/> of the buttons and axes a pad's template lacks.</summary>
    /// <param name="missingButtons">The buttons it lacks.</param>
    /// <param name="missingAxes">The axes it lacks, one bit per <see cref="PadAxis"/>.</param>
    public static int MissingMask(PadButtons missingButtons, int missingAxes)
    {
        int mask = 0;
        for (int i = 0; i < ElementCount; i++)
        {
            PadCheckElement element = Table[i];
            bool missing = element.IsAxis ? (missingAxes & (1 << element.Index)) != 0 : ((int)missingButtons & (1 << element.Index)) != 0;
            mask |= missing ? 1 << i : 0;
        }

        return mask;
    }

    /// <summary>Notes one reading of the pad (buttons held; sticks -1 to 1, triggers 0 to 1). Allocates nothing.</summary>
    public void Observe(PadButtons held, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger)
    {
        int active = 0;
        bool peakMoved = false;
        int seen = _seen;
        int negative = _seenNegative;
        int positive = _seenPositive;
        for (int i = 0; i < ElementCount; i++)
        {
            if ((Missing & (1 << i)) != 0)
            {
                _values[i] = 0f;
                continue;
            }

            PadCheckElement element = Table[i];
            float value;
            if (element.IsAxis)
            {
                value = Axis((PadAxis)element.Index, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger);
                value = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
                float size = Math.Abs(value);
                if (size > _peaks[i])
                {
                    _peaks[i] = size;
                    int step = (int)(size * 20);
                    if (step > _peakSteps[i])
                    {
                        // Each 0.05 further remakes the texts (they name a trigger's peak), at most 20 times an axis.
                        _peakSteps[i] = step;
                        peakMoved = true;
                    }
                }

                active |= size >= ActiveThreshold ? 1 << i : 0;
                if (element.TwoWay)
                {
                    negative |= value <= -SeenThreshold ? 1 << i : 0;
                    positive |= value >= SeenThreshold ? 1 << i : 0;
                    seen |= negative & positive & (1 << i);
                }
                else
                {
                    seen |= value >= SeenThreshold ? 1 << i : 0;
                }
            }
            else
            {
                bool down = ((int)held & (1 << element.Index)) != 0;
                value = down ? 1f : 0f;
                active |= down ? 1 << i : 0;
                seen |= down ? 1 << i : 0;
            }

            _values[i] = value;
        }

        int rising = active & ~_active;
        if (rising != 0)
        {
            LastActive = LowestBit(rising);
        }

        if (seen != _seen || negative != _seenNegative || positive != _seenPositive || peakMoved)
        {
            Version++;
        }

        _active = active;
        _seen = seen;
        _seenNegative = negative;
        _seenPositive = positive;
    }

    /// <summary>Starts the check again: nothing read (what the pad lacks stays).</summary>
    public void Reset()
    {
        Array.Clear(_values);
        Array.Clear(_peaks);
        Array.Clear(_peakSteps);
        _active = 0;
        _seen = 0;
        _seenNegative = 0;
        _seenPositive = 0;
        LastActive = -1;
        Version++;
    }

    /// <summary>True while element <paramref name="index"/> is held, or moved past <see cref="ActiveThreshold"/>.</summary>
    public bool IsActive(int index) => (_active & Bit(index)) != 0;

    /// <summary>True once element <paramref name="index"/> was read (both ways, for a stick).</summary>
    public bool IsSeen(int index) => (_seen & Bit(index)) != 0;

    /// <summary>True when the pad's template lacks element <paramref name="index"/>.</summary>
    public bool IsMissing(int index) => (Missing & Bit(index)) != 0;

    /// <summary>True once a stick's element <paramref name="index"/> went past <see cref="SeenThreshold"/> its negative way (left or down).</summary>
    public bool SeenNegative(int index) => (_seenNegative & Bit(index)) != 0;

    /// <summary>True once a stick's element <paramref name="index"/> went past <see cref="SeenThreshold"/> its positive way (right or up).</summary>
    public bool SeenPositive(int index) => (_seenPositive & Bit(index)) != 0;

    /// <summary>Element <paramref name="index"/>'s last value: 1 or 0 for a button, -1 to 1 for a stick, 0 to 1 for a trigger.</summary>
    public float Value(int index) => (uint)index < ElementCount ? _values[index] : 0f;

    /// <summary>The furthest axis <paramref name="index"/> moved since the check started, either way (0 to 1); 0 for a button.</summary>
    public float Peak(int index) => (uint)index < ElementCount ? _peaks[index] : 0f;

    /// <summary>The index of the trigger <paramref name="axis"/> (LT or RT) in <see cref="Elements"/>.</summary>
    public static int IndexOf(PadAxis axis)
    {
        for (int i = 0; i < ElementCount; i++)
        {
            if (Table[i].IsAxis && Table[i].Index == (int)axis)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// What the player should do about a trigger (<see cref="TriggerCheck.Advice"/>): RT's first, then LT's; null when neither
    /// needs anything (or the template lacks it: the verdict says that).
    /// </summary>
    public string? TriggerAdvice()
    {
        int lt = IndexOf(PadAxis.LeftTrigger);
        int rt = IndexOf(PadAxis.RightTrigger);
        string? right = IsMissing(rt) || IsSeen(rt) ? null : TriggerCheck.Advice("RT", _peaks[rt], "LT", _peaks[lt], SteamInput);
        return right ?? (IsMissing(lt) || IsSeen(lt) ? null : TriggerCheck.Advice("LT", _peaks[lt], "RT", _peaks[rt], SteamInput));
    }

    /// <summary>
    /// The elements not read yet, by name (a stick read one way named with the other: "Left stick Y down"); what the
    /// template lacks isn't among them. Empty when there are none.
    /// </summary>
    public List<string> NotYet()
    {
        var names = new List<string>();
        for (int i = 0; i < ElementCount; i++)
        {
            if (!IsMissing(i) && !IsSeen(i))
            {
                PadCheckElement element = Table[i];
                names.Add(!element.TwoWay
                    ? element.IsAxis && _peaks[i] >= TriggerCheck.MovedFloor ? $"{element.Name} ({TriggerCheck.Format(_peaks[i])} at most)" : element.Name
                    : !SeenNegative(i) && !SeenPositive(i) ? element.Name
                    : $"{element.Name} {(SeenNegative(i) ? element.PositiveName : element.NegativeName)}");
            }
        }

        return names;
    }

    /// <summary>The elements read, by name, in the panel's order.</summary>
    public List<string> Read() => Names(_seen);

    /// <summary>The elements the pad's template lacks, by name.</summary>
    public List<string> Lacks() => Names(Missing);

    /// <summary>
    /// The check's one line: "All 20 read: this controller sends everything a standard pad has", what's not read yet, or,
    /// for a pad whose template lacks elements, that those never reach a host.
    /// </summary>
    public PadCheckVerdict Verdict()
    {
        string lacks = MissingCount == 0 ? string.Empty
            : $"Its gamepad template lacks {Join(Lacks())}: online {(MissingCount == 1 ? "that never reaches" : "those never reach")} the host.";
        List<string> notYet = NotYet();
        string advice = TriggerAdvice() is { } trigger ? $" {trigger}" : string.Empty;
        if (notYet.Count == 0)
        {
            return MissingCount == 0
                ? new PadCheckVerdict(PadCheckOutcome.Complete, $"All {ElementCount} read: this controller sends everything a standard pad has.")
                : new PadCheckVerdict(PadCheckOutcome.Lacking, $"All {Readable} it has read. {lacks}");
        }

        if (!AnyRead)
        {
            return new PadCheckVerdict(PadCheckOutcome.NothingYet,
                "Nothing read yet: press every button, pull both triggers and move both sticks all the way, each way." + (lacks.Length > 0 ? $" {lacks}" : string.Empty) + advice);
        }

        string read = MissingCount == 0 ? $"{SeenCount} of {ElementCount} read" : $"{SeenCount} of the {Readable} it has read";
        return new PadCheckVerdict(PadCheckOutcome.Partial, $"{read}; not yet: {Join(notYet)}." + (lacks.Length > 0 ? $" {lacks}" : string.Empty) + advice);
    }

    /// <summary>
    /// The check's log line, said when it ends (the tab or panel closed, Reset, another pad): the pad, its family here and
    /// on a host, its template's coverage, what was read and what never was, and its maps.
    /// </summary>
    /// <param name="pad">The pad.</param>
    /// <param name="ending">Why the check ended ("tab closed").</param>
    /// <param name="took">How long it ran.</param>
    public string Summary(PadCheckPad pad, string ending, TimeSpan took)
    {
        ArgumentNullException.ThrowIfNull(pad);
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"Controller check ({ending}, {Seconds(took)} s): \"{pad.Name}\" (hardware \"{pad.HardwareName}\"; this game sees {pad.Family}; ")
            .Append(CultureInfo.InvariantCulture, $"a host reads it as {pad.HostReadsAs}; {pad.Online}); gamepad template: ");
        line.Append(MissingCount == 0 ? $"all {ElementCount} elements" : $"{Readable} of {ElementCount}, lacks {Join(Lacks())} (online never pressed)");
        List<string> read = Read();
        line.Append(read.Count == 0 ? "; read nothing" : $"; read {read.Count} of {Readable}: {Join(read)}");
        List<string> notYet = NotYet();
        line.Append(notYet.Count == 0 ? "; nothing missed" : $"; never: {Join(notYet)}");
        line.Append(PeaksNotRead());
        if (pad.ElementActions is { Count: ElementCount } actions)
        {
            int lt = IndexOf(PadAxis.LeftTrigger);
            int rt = IndexOf(PadAxis.RightTrigger);
            line.Append(CultureInfo.InvariantCulture, $"; LT gives {Gives(actions[lt])}; RT gives {Gives(actions[rt])}");
        }

        line.Append(pad.Maps switch
        {
            PadCheckMaps.Defaults => "; maps: the game's defaults (a host binds it from the game's gamepad maps).",
            PadCheckMaps.Own => $"; maps: its own, {pad.Differences} binding{(pad.Differences == 1 ? string.Empty : "s")} differ{(pad.Differences == 1 ? "s" : string.Empty)} from the game's defaults (a host binds it from them).",
            _ => "; maps: couldn't be read.",
        });
        return line.ToString();
    }

    /// <summary>The check's log line when there was no pad to check: why, and what's used online instead.</summary>
    /// <param name="ending">Why the check ended ("tab closed").</param>
    /// <param name="why">What the tab said ("no gamepad connected; online you'd play with the keyboard and mouse (OnlineInput Auto)").</param>
    public static string NoPadSummary(string ending, string why) => $"Controller check ({ending}): {why}.";

    /// <summary>"; peaks of axes not read: RT 0.00, Left stick X 0.31": each trigger not read, and each stick that moved but wasn't read both ways.</summary>
    private string PeaksNotRead()
    {
        var peaks = new List<string>();
        for (int i = 0; i < ElementCount; i++)
        {
            PadCheckElement element = Table[i];
            if (element.IsAxis && !IsMissing(i) && !IsSeen(i) && (!element.TwoWay || _peaks[i] >= TriggerCheck.MovedFloor))
            {
                peaks.Add($"{element.Name} {TriggerCheck.Format(_peaks[i])}");
            }
        }

        return peaks.Count == 0 ? string.Empty : $"; peaks of axes not read: {Join(peaks)} (the game needs {TriggerCheck.Format(SeenThreshold)})";
    }

    private static string Gives(string actions) => actions.Length > 0 ? actions : "no action";

    private static float Axis(PadAxis axis, float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger) => axis switch
    {
        PadAxis.LeftStickX => leftX,
        PadAxis.LeftStickY => leftY,
        PadAxis.RightStickX => rightX,
        PadAxis.RightStickY => rightY,
        PadAxis.LeftTrigger => leftTrigger,
        _ => rightTrigger,
    };

    private static List<string> Names(int mask)
    {
        var names = new List<string>();
        for (int i = 0; i < ElementCount; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                names.Add(Table[i].Name);
            }
        }

        return names;
    }

    private static string Join(List<string> names) => string.Join(", ", names);

    private static string Seconds(TimeSpan took) => Math.Max(0, (long)took.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static int Bit(int index) => (uint)index < ElementCount ? 1 << index : 0;

    private static int LowestBit(int mask)
    {
        for (int i = 0; i < ElementCount; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int Count(int mask)
    {
        int count = 0;
        for (; mask != 0; mask &= mask - 1)
        {
            count++;
        }

        return count;
    }
}
