#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.Input;
using Il2CppRewired;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;
using RewiredInputUtils = Il2CppTape2Tape.RewiredInputUtils;

namespace T2TOnline.Plugin.Input;

/// <summary>
/// The panel's controller check on this PC's pad (the F1 panel's Controller tab, <see cref="PadCheck"/>): which pad it is
/// (the one the mod holds in a session, else the one <see cref="LocalPad.Find"/> would take: <see cref="LocalPad.Peek"/>),
/// what the game and a host read it as, which of a standard pad's 20 elements its gamepad template has, each element read
/// every frame through the template (as a friend's pad is read for the host), and the game actions each gives under the
/// player's own maps (<see cref="PadRemapReader.ReadControls"/>; the game's gamepad template maps when those are the
/// defaults, as a host binds them). The pad is looked for again every second; another pad starts a new check. Only reads:
/// the pad stays with whoever has it. Never throws: a failure is shown and logged once, and the check stops reading.
/// </summary>
internal sealed class PadCheckReader
{
    /// <summary>How often the pad is looked for again.</summary>
    private const long ChooseEveryMs = 1000;

    /// <summary>Joysticks whose template readers are kept at most (a reader keeps its joystick alive).</summary>
    private const int MaxReaders = 16;

    private static readonly string[] NoTexts = Enumerable.Repeat(string.Empty, PadCheck.ElementCount).ToArray();

    private readonly Dictionary<IntPtr, GamepadTemplateReader?> _readers = new();
    private readonly List<string> _notGamepads = new();
    private readonly Func<Joystick, GamepadTemplateReader?> _readerOf;
    private GamepadTemplateReader? _reader;
    private IntPtr _joystick;
    private long _chosenAt = long.MinValue;
    private long _startedAt;
    private int _verdictFor = int.MinValue;
    private int _failures;
    private bool _errorLogged;
    private bool _running;
    private string _online = string.Empty;
    private string _noPadWhy = string.Empty;
    private string _others = string.Empty;
    private readonly string?[] _peakDetails = new string?[PadCheck.ElementCount];
    private readonly int[] _peakDetailSteps = new int[PadCheck.ElementCount];
    private string _steamNote = string.Empty;

    /// <summary>Creates the reader (nothing is read until <see cref="Update"/>).</summary>
    public PadCheckReader() => _readerOf = ReaderOf;

    /// <summary>The check of the pad being read, or null while there's none.</summary>
    public PadCheck? Check { get; private set; }

    /// <summary>What the log line says about the pad being read, or null while there's none.</summary>
    public PadCheckPad? Pad { get; private set; }

    /// <summary>The pad's name, for the tab's first line ("" while there's none).</summary>
    public string NameLine { get; private set; } = string.Empty;

    /// <summary>"1 of 2 gamepads" when several are connected, else "".</summary>
    public string CountLine { get; private set; } = string.Empty;

    /// <summary>Its hardware, the family the game sees, what a host reads it as, and whether it's what this PC plays online with.</summary>
    public string IdentityLine { get; private set; } = string.Empty;

    /// <summary>The template's coverage and where the actions come from.</summary>
    public string CoverageLine { get; private set; } = string.Empty;

    /// <summary>True when <see cref="CoverageLine"/> names elements the template lacks.</summary>
    public bool Lacks { get; private set; }

    /// <summary>A note about what's used online instead (the OnlineInput setting, or the keyboard in this session), or "".</summary>
    public string OnlineNote { get; private set; } = string.Empty;

    /// <summary>Why there's no pad to check, and what to do ("" while there is one).</summary>
    public string NoPadText { get; private set; } = string.Empty;

    /// <summary>Connected controllers Rewired doesn't read as gamepads (the mod can't send them), or "".</summary>
    public string OthersLine { get; private set; } = string.Empty;

    /// <summary>What went wrong reading the pad, or "".</summary>
    public string Problem { get; private set; } = string.Empty;

    /// <summary>The game actions each element gives under the pad's maps (index as in <see cref="PadCheck.Elements"/>).</summary>
    public IReadOnlyList<string> Actions { get; private set; } = NoTexts;

    /// <summary>The line under the grid for each element: its name and every action it gives.</summary>
    public IReadOnlyList<string> Details { get; private set; } = NoTexts;

    /// <summary>
    /// The line under the grid for element <paramref name="index"/> (<see cref="Details"/>); a trigger's adds how far it was
    /// pulled at most and the 0.50 the game's trigger actions need, remade only when that peak moves by 0.05.
    /// </summary>
    public string DetailOf(int index)
    {
        if (Check == null || (uint)index >= PadCheck.ElementCount)
        {
            return string.Empty;
        }

        PadCheckElement element = PadCheck.Elements[index];
        if (!element.IsAxis || element.TwoWay || Check.IsMissing(index))
        {
            return Details[index];
        }

        int step = (int)MathF.Round(Check.Peak(index) * 20);
        if (_peakDetails[index] == null || _peakDetailSteps[index] != step)
        {
            _peakDetailSteps[index] = step;
            _peakDetails[index] = $"{Details[index]} · pulled {TriggerCheck.Format(step / 20f)} at most; the game's trigger actions need {TriggerCheck.Format(PadCheck.SeenThreshold)}";
        }

        return _peakDetails[index]!;
    }

    /// <summary>The check's verdict, remade only when what was read changes.</summary>
    public PadCheckVerdict Verdict { get; private set; }

    /// <summary>True while the mod holds the pad (an online session, the one-PC test, the F8 loopback): it plays there.</summary>
    public bool Held { get; private set; }

    /// <summary>True while there's a pad to check.</summary>
    public bool HasPad => Check != null;

    /// <summary>
    /// Call every frame while the tab is shown: starts the check if it isn't running, looks for the pad again every second
    /// (another pad, or none, ends the check of the one before, logged), and reads the pad. Never throws.
    /// </summary>
    /// <param name="now">Milliseconds (<see cref="Environment.TickCount64"/>).</param>
    /// <param name="playsWith">In a session this PC joined, what it plays with there; else null.</param>
    public void Update(long now, InputKind? playsWith)
    {
        if (!_running)
        {
            _running = true;
            _chosenAt = long.MinValue;
            _startedAt = now;
        }

        if (_failures >= 3)
        {
            return;
        }

        try
        {
            if (_chosenAt == long.MinValue || now - _chosenAt >= ChooseEveryMs)
            {
                _chosenAt = now;
                Choose(now, playsWith);
            }

            if (_reader != null && Check != null)
            {
                GamepadTemplateReader reader = _reader;
                Check.Observe(reader.ReadButtons(), reader.ReadAxis(PadAxis.LeftStickX), reader.ReadAxis(PadAxis.LeftStickY), reader.ReadAxis(PadAxis.RightStickX),
                    reader.ReadAxis(PadAxis.RightStickY), reader.ReadAxis(PadAxis.LeftTrigger), reader.ReadAxis(PadAxis.RightTrigger));
                if (Check.Version != _verdictFor)
                {
                    _verdictFor = Check.Version;
                    Verdict = Check.Verdict();
                }
            }

            if (_failures > 0)
            {
                _failures = 0; // read again after a failure: the problem is gone
                Problem = string.Empty;
            }
        }
        catch (Exception ex)
        {
            // Three failures in a row stop the check until the tab opens again; the first error and the stop are logged.
            _failures++;
            Problem = $"The pad couldn't be read ({ModLog.SafeErrorText(ex)})" + (_failures >= 3 ? "; the check has stopped (see the log)." : "; trying again.");
            if (!_errorLogged || _failures == 3)
            {
                _errorLogged = true;
                ModLog.Error(_failures == 3 ? "Panel: the controller check failed three times in a row; it has stopped until the tab is opened again" : "Panel: the controller check couldn't read the pad", ex);
            }

            _chosenAt = long.MinValue; // look for the pad again
        }
    }

    /// <summary>Starts the check of the same pad again (logs the check that ends first, when anything was read). Never throws.</summary>
    public void Reset(long now)
    {
        try
        {
            if (Check != null && Pad != null && Check.AnyRead)
            {
                ModLog.Info($"Panel: {Check.Summary(Pad, "reset", TimeSpan.FromMilliseconds(now - _startedAt))}");
            }

            // The pad is looked for again at once, and its check starts afresh, its maps read again too (a remap since).
            Forget();
            _failures = 0;
            Problem = string.Empty;
            _chosenAt = long.MinValue;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Panel: resetting the controller check failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Ends the check (the tab or the panel closed): its log line, and nothing more is read until <see cref="Update"/>. Never throws.</summary>
    public void End(string ending, long now)
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        try
        {
            ModLog.Info($"Panel: {Summary(ending, now)}");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Panel: the controller check's summary couldn't be made ({ModLog.SafeErrorText(ex)}).");
        }

        Forget();
        _readers.Clear();
        _failures = 0;
        _errorLogged = false;
        Problem = string.Empty;
        _noPadWhy = string.Empty;
    }

    /// <summary>The check's log line now.</summary>
    private string Summary(string ending, long now) => Check != null && Pad != null
        ? Check.Summary(Pad, ending, TimeSpan.FromMilliseconds(now - _startedAt))
        : PadCheck.NoPadSummary(ending, _noPadWhy.Length > 0 ? _noPadWhy : "no gamepad was read");

    /// <summary>
    /// Finds the pad to check: the one the mod holds, else the one <see cref="LocalPad.Find"/> would take. Another one than
    /// before ends the check before (logged) and starts one of it: its identity, template and maps read once.
    /// </summary>
    private void Choose(long now, InputKind? playsWith)
    {
        OnlineInputChoice choice = ModSettings.OnlineInput;
        LocalPad? held = LocalPad.Held;
        GamepadTemplateReader? reader;
        int owner;
        int candidates;
        _notGamepads.Clear();
        if (held != null)
        {
            reader = held.IsConnected ? held.Reader : null;
            owner = held.OwnerPlayerId;
            candidates = reader != null ? 1 : 0;
        }
        else
        {
            if (_readers.Count > MaxReaders)
            {
                _readers.Clear();
            }

            reader = LocalPad.Peek(_readerOf, out owner, out candidates);
        }

        string others = _notGamepads.Count == 0 ? string.Empty
            : $"Also connected, but not a gamepad to Rewired (no gamepad template, so the mod can't send it online): {string.Join(", ", _notGamepads.Select(n => $"\"{n}\""))}.";
        if (others != _others)
        {
            _others = others;
            OthersLine = others;
        }

        IntPtr joystick = reader?.Joystick.Pointer ?? IntPtr.Zero;
        if (joystick != _joystick || (reader != null && Check == null))
        {
            if (Check != null && Pad != null)
            {
                ModLog.Info($"Panel: {Check.Summary(Pad, joystick == IntPtr.Zero ? "the pad went" : "another pad", TimeSpan.FromMilliseconds(now - _startedAt))}");
            }

            Forget();
            _joystick = joystick;
            if (reader != null)
            {
                Start(reader, owner, held);
                _reader = reader;
                _startedAt = now;
            }
        }

        if (reader != null)
        {
            CountLine = candidates > 1 ? $"the pad pressed last of {candidates} gamepads" : string.Empty;
        }

        Held = held != null && reader != null;
        string online = OnlineUse(choice, playsWith, held, reader != null);
        if (online != _online)
        {
            _online = online;
            OnlineNote = Note(choice, playsWith, reader != null);
            if (Pad != null)
            {
                Pad = Pad with { Online = online };
                IdentityLine = Identity(Pad) + _steamNote;
            }
        }

        if (reader == null)
        {
            _noPadWhy = held != null ? "the pad the mod holds is unplugged" : NoPadWhy(choice, playsWith);
            NoPadText = held != null
                ? "The pad the mod holds for this session is unplugged: plug it back in (it's picked up again by itself)."
                : NoPadMessage(choice, playsWith);
        }
        else
        {
            NoPadText = string.Empty;
            _noPadWhy = string.Empty;
        }
    }

    /// <summary>Starts the check of <paramref name="reader"/>'s pad: what it is, its template, and the actions its maps give.</summary>
    private void Start(GamepadTemplateReader reader, int owner, LocalPad? held)
    {
        Joystick joystick = reader.Joystick;
        bool steamInput = PadProfile.IsSteamInput(joystick);
        var check = new PadCheck(PadCheck.MissingMask(reader.MissingButtons, reader.MissingAxes), steamInput);
        _steamNote = steamInput ? " · set up by Steam Input" : string.Empty;
        EInputDeviceType family = held != null ? held.DeviceType : RewiredInputUtils.GetDeviceTypeFromController(joystick);
        (PadCheckMaps maps, int differences, IReadOnlyList<InputBinding> bindings) = ReadMaps(joystick, owner);
        Pad = new PadCheckPad(
            SafeText.Clean(joystick.name, 48),
            SafeText.Clean(joystick.hardwareName, 48),
            VirtualPad.FamilyName(family),
            VirtualPad.FamilyName(VirtualPad.GamepadTypeFrom((byte)family)),
            _online,
            maps,
            differences);
        string[] actions = PadCheckActions.ByElement(bindings, RewiredBindings.ActionName, RewiredBindings.SkipsAction);
        Pad = Pad with { ElementActions = actions };
        Array.Clear(_peakDetails);
        var details = new string[actions.Length];
        for (int i = 0; i < actions.Length; i++)
        {
            details[i] = PadCheckActions.Detail(i, actions[i], check.IsMissing(i));
        }

        Actions = actions;
        Details = details;
        NameLine = $"\"{Pad.Name}\"";
        IdentityLine = Identity(Pad) + _steamNote;
        Lacks = check.MissingCount > 0;
        string coverage = check.MissingCount == 0 ? $"Gamepad template: all {PadCheck.ElementCount} buttons and axes"
            : $"Gamepad template: lacks {string.Join(", ", check.Lacks())} (online never pressed)";
        CoverageLine = coverage + maps switch
        {
            PadCheckMaps.Own => $" · actions: your own maps ({differences} binding{(differences == 1 ? string.Empty : "s")} differ{(differences == 1 ? "s" : string.Empty)} from the game's defaults)",
            PadCheckMaps.Defaults => " · actions: the game's gamepad maps (yours are the defaults)",
            _ => " · actions: your maps couldn't be read (see the log)",
        };
        Check = check;
        Verdict = check.Verdict();
        _verdictFor = check.Version;
    }

    /// <summary>
    /// The pad's bindings as a host would bind its virtual pad: the player's own maps when they differ from the game's
    /// defaults, else the game's gamepad template maps. A failure is logged and leaves no actions.
    /// </summary>
    private static (PadCheckMaps Maps, int Differences, IReadOnlyList<InputBinding> Bindings) ReadMaps(Joystick joystick, int owner)
    {
        try
        {
            PadControls controls = PadRemapReader.ReadControls(joystick, owner);
            return controls.Differences == 0
                ? (PadCheckMaps.Defaults, 0, PadRemapReader.TemplateDefaults())
                : (PadCheckMaps.Own, controls.Differences, controls.Own);
        }
        catch (Exception ex)
        {
            ModLog.Error("Panel: the controller check couldn't read your pad's maps; it shows no actions", ex);
            return (PadCheckMaps.Unknown, 0, Array.Empty<InputBinding>());
        }
    }

    /// <summary>Drops the pad being read.</summary>
    private void Forget()
    {
        _reader = null;
        _joystick = IntPtr.Zero;
        Check = null;
        Pad = null;
        NameLine = string.Empty;
        CountLine = string.Empty;
        IdentityLine = string.Empty;
        CoverageLine = string.Empty;
        Lacks = false;
        Actions = NoTexts;
        Details = NoTexts;
        Verdict = default;
        _online = string.Empty;
        _steamNote = string.Empty;
        Array.Clear(_peakDetails);
    }

    /// <summary>A template reader for a connected joystick (kept per joystick), or null; a connected one without the template is named.</summary>
    private GamepadTemplateReader? ReaderOf(Joystick joystick)
    {
        if (!joystick.isConnected)
        {
            return null;
        }

        IntPtr key = joystick.Pointer;
        if (!_readers.TryGetValue(key, out GamepadTemplateReader? reader))
        {
            reader = GamepadTemplateReader.TryCreate(joystick);
            _readers[key] = reader;
        }

        if (reader == null)
        {
            _notGamepads.Add(SafeText.Clean(joystick.name, 40));
        }

        return reader;
    }

    /// <summary>The identity line: hardware, the family here and on a host, and its use online.</summary>
    private static string Identity(PadCheckPad pad) =>
        $"Hardware: {(pad.HardwareName.Length > 0 ? pad.HardwareName : "unknown")} · this game sees {pad.Family} · a host reads it as {pad.HostReadsAs} · {pad.Online}";

    /// <summary>Whether the pad is what this PC plays with online, for the identity line and the log.</summary>
    private static string OnlineUse(OnlineInputChoice choice, InputKind? playsWith, LocalPad? held, bool pad) =>
        !pad ? string.Empty
        : held != null ? "the mod holds it now: it plays online (or in the F8 test)"
        : playsWith == InputKind.KeyboardMouse ? "not used in this session (you joined with the keyboard and mouse)"
        : choice == OnlineInputChoice.KeyboardMouse ? "not used online (Online input: Keyboard and mouse)"
        : "what you'd play with online";

    /// <summary>A note shown above the grid when online uses something else, or "".</summary>
    private static string Note(OnlineInputChoice choice, InputKind? playsWith, bool pad) =>
        !pad ? string.Empty
        : playsWith == InputKind.KeyboardMouse ? "In this session you play with the keyboard and mouse: this pad isn't sent to the host."
        : choice == OnlineInputChoice.KeyboardMouse ? "Online input is Keyboard and mouse (Settings), so this pad isn't sent online; checked anyway."
        : string.Empty;

    /// <summary>Why there's no pad, for the log.</summary>
    private static string NoPadWhy(OnlineInputChoice choice, InputKind? playsWith) =>
        playsWith == InputKind.KeyboardMouse ? "no gamepad connected; this session plays with the keyboard and mouse"
        : choice switch
        {
            OnlineInputChoice.KeyboardMouse => "no gamepad connected; online you play with the keyboard and mouse (OnlineInput KeyboardMouse)",
            OnlineInputChoice.Gamepad => "no gamepad connected; OnlineInput is Gamepad, so joining needs one",
            _ => "no gamepad connected; online you'd play with the keyboard and mouse (OnlineInput Auto)",
        };

    /// <summary>The tab's text when there's no pad.</summary>
    private static string NoPadMessage(OnlineInputChoice choice, InputKind? playsWith) =>
        playsWith == InputKind.KeyboardMouse ? "No gamepad connected. In this session you play with the keyboard and mouse."
        : choice switch
        {
            OnlineInputChoice.KeyboardMouse => "No gamepad connected. Online you play with the keyboard and mouse (Settings, Online input).",
            OnlineInputChoice.Gamepad => "No gamepad connected, and Online input is Gamepad: joining a friend needs one. Plug one in: it shows here within a second.",
            _ => "No gamepad connected: online you'd play with the keyboard and mouse (Online input is Auto). Plug a pad in: it shows here within a second.",
        };
}
#endif
