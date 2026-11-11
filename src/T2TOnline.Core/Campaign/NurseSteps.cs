using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>What one step on the host's Nurse procedure screen was. Values are part of the protocol: only add.</summary>
public enum NurseStepKind : byte
{
    /// <summary>Nothing (never sent).</summary>
    None = 0,

    /// <summary>The confirm press (the screen's <c>OnNativeConfirm</c>).</summary>
    Confirm = 1,

    /// <summary>The back press (<c>OnNativeBack</c>).</summary>
    Back = 2,

    /// <summary>A direction press (<c>OnNativeDirection</c>; <see cref="NurseStep.Arg"/>: the game's direction number).</summary>
    Direction = 3,

    /// <summary>A named action held down (<c>OnNativeInputPressed</c>, the details hold; <see cref="NurseStep.Action"/>).</summary>
    Press = 4,

    /// <summary>A named action let go (<c>OnNativeInputReleased</c>, the details hold; <see cref="NurseStep.Action"/>).</summary>
    Release = 5,

    /// <summary>The focus moved with no press of its own (a held direction repeating; <see cref="NurseStep.Arg"/>: the option).</summary>
    Focus = 6,

    /// <summary>The stage moved with no press of its own (the screen's own automatic steps, as a transplant's).</summary>
    Auto = 7,

    /// <summary>A press that only ended the sacrifice's reveal (any press does while it runs).</summary>
    SkipReveal = 8,

    /// <summary>The lineup opened from the screen (its own menu, shown to friends as its own view; never replayed).</summary>
    Lineup = 9,
}

/// <summary>
/// One step on the host's Nurse procedure screen: a press the screen took (its own input handlers, past their own gates),
/// or a change it made with none; then the screen's stage and focused option right after it, to check a friend's screen
/// against. Checked when it's written and when it's read; immutable.
/// </summary>
public sealed class NurseStep
{
    /// <summary>Longest action name kept (the game's input action names are short).</summary>
    public const int MaxActionBytes = 48;

    /// <summary>Highest argument kept (a direction number, an option).</summary>
    public const int MaxArg = 255;

    /// <summary>Highest stage number kept (the game's stages and room for more).</summary>
    public const int MaxStage = 63;

    /// <summary>The step's number in its takeover, from 1.</summary>
    public int Number { get; init; } = 1;

    /// <summary>What it was.</summary>
    public NurseStepKind Kind { get; init; }

    /// <summary>A direction's number, or a <see cref="NurseStepKind.Focus"/> step's option; else 0.</summary>
    public int Arg { get; init; }

    /// <summary>A <see cref="NurseStepKind.Press"/> or <see cref="NurseStepKind.Release"/> step's action name; else null.</summary>
    public string? Action { get; init; }

    /// <summary>The sacrifice's reveal was running on the host's screen when the step was pressed.</summary>
    public bool Reveal { get; init; }

    /// <summary>The screen's stage right after it (the game's stage number), or -1 when it had none.</summary>
    public int Stage { get; init; } = -1;

    /// <summary>The focused option right after it, or -1 for none.</summary>
    public int Focus { get; init; } = -1;

    /// <summary>For logs: "step 3: Confirm (stage 2, option 0)", "step 4: Press UIDetails".</summary>
    public override string ToString()
    {
        string what = Kind switch
        {
            NurseStepKind.Direction => $"Direction {Arg}",
            NurseStepKind.Focus => $"Focus {Arg}",
            NurseStepKind.Press or NurseStepKind.Release => $"{Kind} {HostText.KeyForLog(Action)}",
            _ => Kind.ToString(),
        };
        return $"step {Number}: {what}{(Reveal ? " (during the reveal)" : string.Empty)} (stage {Stage}, option {Focus})";
    }

    /// <summary>Why the step can't be sent, or null when it can.</summary>
    public string? Check()
    {
        if (Number is < 1 or > ushort.MaxValue)
        {
            return $"step number {Number} is out of range";
        }

        if (Kind is < NurseStepKind.Confirm or > NurseStepKind.Lineup)
        {
            return $"step kind {Kind} is no step";
        }

        if (Arg is < 0 or > MaxArg || (Arg != 0 && Kind is not (NurseStepKind.Direction or NurseStepKind.Focus)))
        {
            return $"argument {Arg} is out of range for a {Kind} step";
        }

        bool named = Kind is NurseStepKind.Press or NurseStepKind.Release;
        if (named != (Action != null))
        {
            return named ? $"a {Kind} step has no action" : $"a {Kind} step has an action";
        }

        if (Action != null && (Action.Length is 0 or > MaxActionBytes || Action.Any(c => c is < ' ' or > '~')))
        {
            return "the action isn't a short printable name";
        }

        return Stage is < -1 or > MaxStage || Focus is < -1 or > MaxArg ? "the stage or focus after it is out of range" : null;
    }

    internal void Write(BitWriter writer)
    {
        writer.WriteUInt16((ushort)Number);
        writer.WriteByte((byte)Kind);
        writer.WriteVarUInt((ulong)Arg);
        writer.WriteBool(Reveal);
        if (Action != null)
        {
            EventWire.Name(writer, Action);
        }

        writer.WriteByte((byte)(Stage + 1));
        writer.WriteVarUInt((ulong)(Focus + 1));
    }

    internal static NurseStep? Read(ref BitReader reader)
    {
        bool ok = true;
        int number = reader.ReadUInt16();
        var kind = (NurseStepKind)reader.ReadByte();
        int arg = EventWire.Count(ref reader, MaxArg, ref ok);
        bool reveal = reader.ReadBool();
        string? action = kind is NurseStepKind.Press or NurseStepKind.Release ? EventWire.ReadName(ref reader) : null;
        int stage = reader.ReadByte() - 1;
        int focus = EventWire.Count(ref reader, MaxArg + 1, ref ok) - 1;
        if (!ok || reader.IsOverrun)
        {
            return null;
        }

        var step = new NurseStep { Number = number, Kind = kind, Arg = arg, Action = action, Reveal = reveal, Stage = stage, Focus = focus };
        return step.Check() == null ? step : null;
    }
}

/// <summary>
/// The host's newest steps on its Nurse procedure screen, sent as a <see cref="MenuAttachmentKind.NurseSteps"/> attachment of the
/// event menu, format <see cref="MenuAttachment.NurseStepsFormat"/>: the procedure and the screen's first stage and focus (sent
/// when the takeover starts, with no steps), then the newest <see cref="MaxSteps"/> steps, again with each new one (the newest
/// attachment replaces the one before), so a friend's game replays them in order through its own screen's handlers. Steps are
/// numbered one after another. Checked when it's written and when it's read; immutable.
/// <code>
/// u8 procedure · u8 first stage + 1 · varuint first focus + 1 · varuint steps · each: u16 number · u8 kind · varuint arg ·
///   bool reveal · (Press, Release: name action) · u8 stage + 1 · varuint focus + 1
/// </code>
/// </summary>
public sealed class NurseStepsData
{
    /// <summary>Most steps sent (the newest; a friend further behind shows the screen as text).</summary>
    public const int MaxSteps = 32;

    /// <summary>The procedure the screen does (the game's procedure number).</summary>
    public int Procedure { get; init; }

    /// <summary>The screen's stage when the takeover started, or -1.</summary>
    public int StartStage { get; init; } = -1;

    /// <summary>The focused option when the takeover started, or -1.</summary>
    public int StartFocus { get; init; } = -1;

    /// <summary>The newest steps, oldest first, numbered one after another.</summary>
    public IReadOnlyList<NurseStep> Steps { get; init; } = Array.Empty<NurseStep>();

    /// <summary>The newest step's number (0: none yet).</summary>
    public int Newest => Steps.Count == 0 ? 0 : Steps[^1].Number;

    /// <summary>The step numbered <paramref name="number"/>, or null when it isn't in the window.</summary>
    public NurseStep? Find(int number)
    {
        if (Steps.Count == 0 || number < Steps[0].Number || number > Newest)
        {
            return null;
        }

        return Steps[number - Steps[0].Number];
    }

    /// <summary>For logs: "procedure 4 (stage 1, option 0 at the start), steps 3 to 7".</summary>
    public override string ToString() =>
        $"procedure {Procedure} (stage {StartStage}, option {StartFocus} at the start), " +
        (Steps.Count == 0 ? "no steps yet" : Steps.Count == 1 ? $"step {Steps[0].Number}" : $"steps {Steps[0].Number} to {Newest}");

    /// <summary>Why the steps can't be sent, or null when they can.</summary>
    public string? Check()
    {
        if (Procedure is < 0 or > NurseRosterData.MaxProcedure || StartStage is < -1 or > NurseStep.MaxStage || StartFocus is < -1 or > NurseStep.MaxArg)
        {
            return "the procedure or the first stage or focus is out of range";
        }

        if (Steps.Count > MaxSteps)
        {
            return $"{Steps.Count} steps";
        }

        for (int i = 0; i < Steps.Count; i++)
        {
            string? problem = Steps[i] == null ? "a step is missing" : Steps[i].Check();
            if (problem == null && i > 0 && Steps[i].Number != Steps[i - 1].Number + 1)
            {
                problem = $"step {Steps[i].Number} doesn't follow step {Steps[i - 1].Number}";
            }

            if (problem != null)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Writes the steps. Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteByte((byte)Procedure);
        writer.WriteByte((byte)(StartStage + 1));
        writer.WriteVarUInt((ulong)(StartFocus + 1));
        writer.WriteVarUInt((ulong)Steps.Count);
        foreach (NurseStep step in Steps)
        {
            step.Write(writer);
        }
    }

    /// <summary>Reads steps. False for anything malformed or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out NurseStepsData? data)
    {
        data = null;
        bool ok = true;
        int procedure = reader.ReadByte();
        int stage = reader.ReadByte() - 1;
        int focus = EventWire.Count(ref reader, NurseStep.MaxArg + 1, ref ok) - 1;
        int count = EventWire.Count(ref reader, MaxSteps, ref ok);
        if (!ok)
        {
            return false;
        }

        var steps = new List<NurseStep>(count);
        for (int i = 0; i < count; i++)
        {
            NurseStep? step = NurseStep.Read(ref reader);
            if (step == null)
            {
                return false;
            }

            steps.Add(step);
        }

        var read = new NurseStepsData { Procedure = procedure, StartStage = stage, StartFocus = focus, Steps = steps };
        if (reader.IsOverrun || read.Check() != null)
        {
            return false;
        }

        data = read;
        return true;
    }
}

/// <summary>
/// The host's side of one takeover's steps: numbers each new step, keeps the newest <see cref="NurseStepsData.MaxSteps"/>,
/// and says when something new should go to friends. A step's stage and focus right after it are filled in a frame later
/// (<see cref="Settle"/>: the press's effects are done by then); a stage or focus that moves with no press becomes an
/// <see cref="NurseStepKind.Auto"/> or <see cref="NurseStepKind.Focus"/> step there. Pure logic; the game glue reads the
/// screen.
/// </summary>
public sealed class NurseStepWindow
{
    private readonly List<NurseStep> _steps = new(NurseStepsData.MaxSteps);
    private readonly List<(NurseStepKind Kind, int Arg, string? Action, bool Reveal)> _pending = new(4);
    private int _stage = -1;
    private int _focus = -1;

    /// <summary>The window for a takeover of <paramref name="procedure"/> that started at <paramref name="stage"/> and <paramref name="focus"/>.</summary>
    public NurseStepWindow(int procedure, int stage, int focus)
    {
        Procedure = Math.Clamp(procedure, 0, NurseRosterData.MaxProcedure);
        StartStage = Math.Clamp(stage, -1, NurseStep.MaxStage);
        StartFocus = Math.Clamp(focus, -1, NurseStep.MaxArg);
        _stage = StartStage;
        _focus = StartFocus;
    }

    /// <summary>
    /// True when a takeover's steps can start from a procedure screen whose event screen is <paramref name="takenOver"/> and
    /// whose state is doing <paramref name="procedure"/> (the game's number, -1 for none). The screen the Nurse's cards warm
    /// is bound to the same event screen and takes the same presses before the pick, with no procedure yet (stage 0): its
    /// steps would start a friend's screen from a stage the procedure never opens at. The game's <c>TakeOver</c> sets the
    /// procedure, after the event screen is marked taken over.
    /// </summary>
    public static bool CanStart(bool takenOver, int procedure) => takenOver && procedure >= 0;

    /// <summary>The procedure.</summary>
    public int Procedure { get; }

    /// <summary>The first stage.</summary>
    public int StartStage { get; }

    /// <summary>The first focus.</summary>
    public int StartFocus { get; }

    /// <summary>Every step numbered so far (the newest's number).</summary>
    public int Count { get; private set; }

    /// <summary>True once the numbers ran out (65535 steps): nothing more is recorded.</summary>
    public bool Full => Count >= ushort.MaxValue;

    /// <summary>A press the screen took this frame; numbered and settled at the next <see cref="Settle"/>.</summary>
    public void Pressed(NurseStepKind kind, int arg = 0, string? action = null, bool reveal = false)
    {
        if (_pending.Count < 8)
        {
            _pending.Add((kind, Math.Clamp(arg, 0, NurseStep.MaxArg), action, reveal));
        }
    }

    /// <summary>
    /// A frame after: the screen's <paramref name="stage"/> and <paramref name="focus"/> now. Each press taken since the last
    /// call becomes a step with them; with none, a change becomes an <see cref="NurseStepKind.Auto"/> step (the stage moved)
    /// or a <see cref="NurseStepKind.Focus"/> step (only the focus did). Returns the steps added (empty: nothing new to send).
    /// A quiet frame (nothing pressed, nothing moved) allocates nothing.
    /// </summary>
    public IReadOnlyList<NurseStep> Settle(int stage, int focus)
    {
        stage = Math.Clamp(stage, -1, NurseStep.MaxStage);
        focus = Math.Clamp(focus, -1, NurseStep.MaxArg);
        if (_pending.Count == 0 && stage == _stage && (focus == _focus || focus < 0))
        {
            _focus = focus;
            return Array.Empty<NurseStep>();
        }

        var added = new List<NurseStep>(_pending.Count + 1);
        foreach ((NurseStepKind kind, int arg, string? action, bool reveal) in _pending)
        {
            Add(added, new NurseStep { Kind = kind, Arg = arg, Action = action, Reveal = reveal, Stage = stage, Focus = focus });
        }

        _pending.Clear();
        if (added.Count == 0 && stage != _stage)
        {
            Add(added, new NurseStep { Kind = NurseStepKind.Auto, Stage = stage, Focus = focus });
        }
        else if (added.Count == 0 && focus != _focus && focus >= 0)
        {
            Add(added, new NurseStep { Kind = NurseStepKind.Focus, Arg = focus, Stage = stage, Focus = focus });
        }

        _stage = stage;
        _focus = focus;
        return added;
    }

    /// <summary>The newest steps, for friends.</summary>
    public NurseStepsData Data() => new() { Procedure = Procedure, StartStage = StartStage, StartFocus = StartFocus, Steps = _steps.ToList() };

    private void Add(List<NurseStep> added, NurseStep unnumbered)
    {
        if (Full)
        {
            return;
        }

        Count++;
        var step = new NurseStep
        {
            Number = Count,
            Kind = unnumbered.Kind,
            Arg = unnumbered.Arg,
            Action = unnumbered.Action,
            Reveal = unnumbered.Reveal,
            Stage = unnumbered.Stage,
            Focus = unnumbered.Focus,
        };
        if (step.Check() != null)
        {
            Count--;
            return; // an action name that can't travel: left out (the friend's check catches what it changed)
        }

        if (_steps.Count == NurseStepsData.MaxSteps)
        {
            _steps.RemoveAt(0);
        }

        _steps.Add(step);
        added.Add(step);
    }
}
