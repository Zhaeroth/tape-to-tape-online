#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppRogue.NPC.NurseNPC;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The Nurse (v1). Its cards are a real view like any other event's (<see cref="EventCapture"/>, <see cref="EventViews"/>);
/// picking one hands the event screen over to the game's own procedure screen (<c>NurseProcedureScreen</c>: a takeover with its
/// own roster, state and input, and a commit that changes the run). A friend's game opens the same screen for real when it can
/// (v1: <see cref="NurseHost"/>, <see cref="NurseRosters"/>, <see cref="NurseFollow"/>); the text here is what it shows
/// otherwise, over its real event screen.
/// <list type="bullet">
/// <item>Host: while its event menu is taken over (<c>EventChoiceMenu.m_IsTakenOver</c>), the menu's data carries the
/// procedure screen as <see cref="NurseScreenText"/>: its stage, procedure, focused option and window (the screen's own
/// developer probes, <c>DebugStage</c>, <c>DebugSelectedIndex</c>...), its rows (<c>NurseRowView</c>, with the highlighted
/// one) and its other visible texts (<see cref="Read"/>); the screen is found by its host menu, and its focus is watched
/// each frame so a move goes out within a quarter of a second (<see cref="Changed"/>).</item>
/// <item>Friend: the text over the real event screen (<see cref="Title"/>, drawn by <see cref="FriendMenus"/>), the procedure
/// named in this PC's language; and the Nurse event asset's state from an earlier visit in this game is cleared before
/// its cards are made (<see cref="ClearStale"/>): it would warm a procedure screen bound to this PC's menu and, when the
/// cards show, mark the procedure offered on this player's profile and save it.</item>
/// </list>
/// </summary>
internal static class NurseScreen
{
    private const int RowChars = 150;
    private const int CellChars = 60;

    // A screen not found is looked for again at most this often (a search walks every procedure screen object).
    private const double SearchSeconds = 0.5;

    // A takeover's screen not found yet is said so only after this long (it's made in the same step, but may come up a
    // frame later): until then friends see it opening.
    private const double GraceSeconds = 1;

    // CurrentProcedure's value in its Nullable (hasValue first; verified in the game's code: NurseProcedureScreen.TakeOver writes the flag and the procedure into the state).
    private const int NullableValueOffset = 4;

    // The state's CurrentProcedure backing field offset (-1: not found), looked up once.
    private static int _procedureOffset = -1;
    private static bool _procedureLooked;

    // The procedure screen found for an event menu (one takeover at a time), its focus last seen, when it may be looked for
    // again, when the takeover was first looked at, and whether its first read (or failure to read) was logged.
    private static IntPtr _menu;
    private static NurseProcedureScreen? _screen;
    private static (int Index, int Window, int Options) _focus = (-1, -1, -1);
    private static double _searchAt;
    private static double _since = -1;
    private static bool _loggedRead;
    private static bool _loggedProblem;

    /// <summary>
    /// Host: the procedure screen that has taken <paramref name="menu"/> over (the one whose host menu it is, with a state;
    /// an active one first), or null with why. Found once per takeover; kept while it stays that menu's.
    /// </summary>
    public static NurseProcedureScreen? Screen(EventChoiceMenu menu, out string problem)
    {
        problem = string.Empty;
        if (_since < 0)
        {
            _since = FrameClock.Seconds;
        }

        if (_screen != null && _menu == menu.Pointer && Taking(_screen, menu))
        {
            return _screen;
        }

        _screen = null;
        _menu = IntPtr.Zero;
        double now = FrameClock.Seconds;
        if (now < _searchAt)
        {
            problem = "no procedure screen is up for its event screen";
            return null;
        }

        _searchAt = now + SearchSeconds;
        var found = new List<NurseProcedureScreen>(1);
        foreach (UnityEngine.Object any in Resources.FindObjectsOfTypeAll(Il2CppType.Of<NurseProcedureScreen>()))
        {
            NurseProcedureScreen? screen = any?.TryCast<NurseProcedureScreen>();
            if (screen != null && Taking(screen, menu))
            {
                // One that's up first (a warmed one bound to the same menu may wait beside it).
                found.Insert(screen.gameObject.activeInHierarchy ? 0 : found.Count, screen);
            }
        }

        if (found.Count == 0)
        {
            problem = "no procedure screen is up for its event screen";
            return null;
        }

        _screen = found[0];
        _menu = menu.Pointer;
        if (found.Count > 1)
        {
            ModLog.Warn($"Menus: {found.Count} Nurse procedure screens are bound to the host's event screen; friends see {(_screen.gameObject.activeInHierarchy ? "the one that's up" : "the first")}.");
        }

        return _screen;
    }

    /// <summary>
    /// Host, each frame while <paramref name="menu"/> is taken over: true when the procedure screen's focus (option,
    /// window, number of options) moved since the last look, so its text is read again soon. The option and the window are
    /// read in place (the fields the game's <c>DebugSelectedIndex</c> and <c>DebugWindowStart</c> return, verified in the
    /// game's code); the number of options is the game's own probe, one call through the runtime (which boxes its int).
    /// </summary>
    public static bool Changed(EventChoiceMenu menu)
    {
        NurseProcedureScreen? screen = _screen != null && _menu == menu.Pointer ? _screen : Screen(menu, out _);
        if (screen == null)
        {
            return false;
        }

        (int, int, int) focus = (screen._selectedIndex, screen._windowStart, screen.DebugOptionCount);
        if (focus == _focus)
        {
            return false;
        }

        _focus = focus;
        return true;
    }

    /// <summary>
    /// Host: the takeover of <paramref name="menu"/> ended (or the menu closed): the screen it found is forgotten, and what
    /// went to friends for the real screen with it (<see cref="NurseHost.Forget"/>).
    /// </summary>
    public static void Forget(EventChoiceMenu? menu = null)
    {
        NurseHost.Forget(menu);
        if (menu == null || _menu == menu.Pointer || _screen == null)
        {
            _screen = null;
            _menu = IntPtr.Zero;
            _focus = (-1, -1, -1);
            _searchAt = 0;
            _since = -1;
            _loggedRead = false;
            _loggedProblem = false;
        }
    }

    /// <summary>
    /// Host: the procedure screen that has taken <paramref name="menu"/> over, into its menu data (<see cref="NurseScreenText"/>:
    /// values, then rows and lines ahead of the menu's own text). One that can't be found or read says why
    /// (<see cref="NurseScreenText.ProblemKey"/>, logged once per takeover); a friend's panel says so.
    /// </summary>
    public static void Read(EventChoiceMenu menu, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        NurseProcedureScreen? screen;
        string problem;
        try
        {
            screen = Screen(menu, out problem);
        }
        catch (Exception ex)
        {
            screen = null;
            problem = $"{ModLog.SafeErrorText(ex)}";
        }

        // Read whole before any of it goes in: a screen that fails half way says why instead (the menu's own text still goes).
        var read = new List<KeyValuePair<string, NetValue>>(5);
        var rows = new List<MenuItem>(NurseScreenText.MaxRows + NurseScreenText.MaxLines);
        if (screen != null)
        {
            try
            {
                ReadScreen(screen, read, rows);
            }
            catch (Exception ex)
            {
                screen = null;
                problem = $"{ModLog.SafeErrorText(ex)}";
            }
        }

        if (screen == null)
        {
            if (problem.StartsWith("no procedure screen", StringComparison.Ordinal) && FrameClock.Seconds - _since < GraceSeconds)
            {
                return; // still coming up: friends see it opening
            }

            values.Add(Value(NurseScreenText.ProblemKey, NetValue.FromString(MenuGame.Plain(problem, RowChars))));
            if (!_loggedProblem)
            {
                _loggedProblem = true;
                ModLog.Warn($"Menus: couldn't read the host's Nurse procedure screen for friends ({problem}); they see why instead of its text.");
            }

            return;
        }

        values.AddRange(read);
        items.AddRange(rows);
        if (!_loggedRead)
        {
            _loggedRead = true;
            int procedure = CurrentProcedure(screen._state);
            ModLog.Info($"Menus: the host's Nurse procedure screen's text goes to friends, for one that shows it as text ({Title(procedure)}, procedure {procedure}; {NurseScreenText.From(new MenuData(read, rows))}).");
        }
    }

    /// <summary>The procedure screen's probes into <paramref name="values"/>, its rows and other lines into <paramref name="items"/>.</summary>
    private static void ReadScreen(NurseProcedureScreen screen, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        NurseProcedureState state = screen._state;
        int procedure = CurrentProcedure(state);
        values.Add(Value(NurseScreenText.StageKey, NetValue.FromString(MenuGame.Plain(screen.DebugStage, NurseScreenText.MaxStageChars))));
        values.Add(Value(NurseScreenText.ProcedureKey, NetValue.FromInt(procedure)));
        values.Add(Value(NurseScreenText.IndexKey, NetValue.FromInt(screen.DebugSelectedIndex)));
        values.Add(Value(NurseScreenText.WindowKey, NetValue.FromInt(screen.DebugWindowStart)));
        values.Add(Value(NurseScreenText.OptionsKey, NetValue.FromInt(screen.DebugOptionCount)));

        // Its rows (a skater, a talent...), in order, with the highlighted one; every row's text is kept out of the lines.
        Transform root = screen.transform;
        var rows = new HashSet<IntPtr>();
        int rowCount = 0;
        foreach (Component? found in root.GetComponentsInChildren(Il2CppType.Of<NurseRowView>(), false))
        {
            NurseRowView? row = found?.TryCast<NurseRowView>();
            if (row == null)
            {
                continue;
            }

            rows.Add(row.transform.Pointer);
            string text = rowCount < NurseScreenText.MaxRows ? Texts(row.transform, null) : string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            GameObject? stripe = row._selectedStripe;
            items.Add(new MenuItem(NurseScreenText.RowGroup, new[]
            {
                Value(NurseScreenText.TextKey, NetValue.FromString(text)),
                Value(NurseScreenText.SelectedKey, NetValue.FromBool(stripe != null && stripe.activeInHierarchy)),
            }));
            rowCount++;
        }

        Lines(root, rows, items);
    }

    /// <summary>
    /// The procedure <paramref name="state"/> is doing (the game's number), or -1 for none: its <c>CurrentProcedure</c>
    /// (a <c>Nullable&lt;Procedure&gt;</c>) read in place. The interop's getter can't be used: it gets the value through
    /// <c>il2cpp_runtime_invoke</c>, which boxes a <c>Nullable</c> as IL2CPP boxes one (null without a value, else the bare
    /// enum; verified in the game's code: <c>Object::Box</c>), and its <c>Nullable</c> wrapper then reads the flag from
    /// the enum's first byte and the value from past the box's end (or throws for null).
    /// </summary>
    internal static int CurrentProcedure(NurseProcedureState? state)
    {
        int offset = ProcedureOffset();
        if (state == null || offset < 0)
        {
            return -1;
        }

        return Marshal.ReadByte(state.Pointer, offset) != 0 ? Marshal.ReadInt32(state.Pointer, offset + NullableValueOffset) : -1;
    }

    /// <summary>The procedure's field offset in the state (from the game's own class), or -1 (logged once).</summary>
    private static int ProcedureOffset()
    {
        if (!_procedureLooked)
        {
            _procedureLooked = true;
            IntPtr type = Il2CppClassPointerStore<NurseProcedureState>.NativeClassPtr;
            IntPtr field = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(type, "<CurrentProcedure>k__BackingField");
            int offset = field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);

            // Past the object header, the Nullable's 8 bytes inside the state (verified in the game's code).
            _procedureOffset = offset is >= 0x10 and <= 0x200 ? offset : -1;
            if (_procedureOffset < 0)
            {
                // Without it the host's steps never start (NurseStepWindow.CanStart needs the procedure TakeOver sets).
                ModLog.Warn($"Menus: the Nurse procedure state's procedure field wasn't found in this game (offset {offset}); friends see the procedure screen as text, " +
                    "titled \"Nurse\".");
            }
        }

        return _procedureOffset;
    }

    /// <summary>
    /// Friend: the procedure's name in this PC's language (the game's own <c>NurseProcedureScreen.GetProcedureName</c>), or
    /// "Nurse" for none or a number this game doesn't know.
    /// </summary>
    public static string Title(int procedure)
    {
        if (procedure < 0 || !Enum.IsDefined(typeof(NurseProcedureState.Procedure), procedure))
        {
            return "Nurse";
        }

        try
        {
            string name = MenuGame.Plain(NurseProcedureScreen.GetProcedureName((NurseProcedureState.Procedure)procedure), CellChars);
            return name.Length > 0 ? name : "Nurse";
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: couldn't name the Nurse's procedure {procedure} in this PC's language ({ModLog.SafeErrorText(ex)}).");
            return "Nurse";
        }
    }

    /// <summary>
    /// Friend, as the Nurse event's cards are about to be made on this PC: the event asset is shared by every Nurse visit
    /// in this game, so a procedure state an earlier visit left there (this player's own Nurse before following, or a Nurse
    /// opened in its tutorial) is cleared. With it, its cards would warm a procedure screen bound to this PC's menu
    /// (<c>OnChoiceItemCreated</c> calls <c>Prewarm</c> when it has a state) and, once they show, mark the procedure offered
    /// on this player's profile and save it (<c>OnChoicesShown</c>). Its roster stays: this player's next own visit destroys
    /// its unused talents, as the game does. Its offer stays too: nothing reads it without the state.
    /// </summary>
    public static void ClearStale(EventNode node)
    {
        NurseEvent? nurse = node.TryCast<NurseEvent>();
        if (nurse == null || nurse._state == null)
        {
            return;
        }

        nurse._state = null!;
        ModLog.Info("Menus: EventChoice: this PC's Nurse event still held a procedure state from an earlier visit; cleared (it would warm its procedure screen and mark this profile's offered procedures).");
    }

    /// <summary>A transform's visible texts, joined (up to four, each cut), skipping those under <paramref name="skip"/>.</summary>
    private static string Texts(Transform root, HashSet<IntPtr>? skip)
    {
        var cells = new List<string>(4);
        foreach (Component? found in root.GetComponentsInChildren(Il2CppType.Of<TMP_Text>(), false))
        {
            TMP_Text? text = found?.TryCast<TMP_Text>();
            if (text == null || (skip != null && Under(root, text.transform, skip)))
            {
                continue;
            }

            string shown = MenuGame.Shown(text, CellChars);
            if (shown.Length > 0 && !cells.Contains(shown) && cells.Count < 4)
            {
                cells.Add(shown);
            }
        }

        return MenuGame.Plain(string.Join(" · ", cells), RowChars);
    }

    /// <summary>The screen's other text, a line per row of its layout (texts sharing a parent), outside its rows.</summary>
    private static void Lines(Transform root, HashSet<IntPtr> rows, List<MenuItem> items)
    {
        int count = 0;
        IntPtr parent = IntPtr.Zero;
        var cells = new List<string>();
        string previous = string.Empty;
        void Flush()
        {
            string text = MenuGame.Plain(string.Join(" · ", cells), RowChars);
            cells.Clear();
            if (text.Length > 0 && text != previous && count < NurseScreenText.MaxLines && items.Count < MenuData.MaxItems)
            {
                items.Add(new MenuItem(NurseScreenText.LineGroup, new[] { Value(NurseScreenText.TextKey, NetValue.FromString(text)) }));
                previous = text;
                count++;
            }
        }

        foreach (Component? found in root.GetComponentsInChildren(Il2CppType.Of<TMP_Text>(), false))
        {
            TMP_Text? text = found?.TryCast<TMP_Text>();
            if (count >= NurseScreenText.MaxLines)
            {
                break;
            }

            if (text == null || Under(root, text.transform, rows))
            {
                continue;
            }

            string shown = MenuGame.Shown(text, CellChars);
            if (shown.Length == 0)
            {
                continue;
            }

            IntPtr at = text.transform.parent?.Pointer ?? IntPtr.Zero;
            if (at != parent)
            {
                Flush();
                parent = at;
            }

            if (!cells.Contains(shown))
            {
                cells.Add(shown);
            }
        }

        Flush();
    }

    /// <summary>True when <paramref name="element"/> is under one of <paramref name="roots"/> (below <paramref name="top"/>).</summary>
    private static bool Under(Transform top, Transform element, HashSet<IntPtr> roots)
    {
        for (Transform? at = element; at != null && at.Pointer != top.Pointer; at = at.parent)
        {
            if (roots.Contains(at.Pointer))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for a procedure screen bound to <paramref name="menu"/> with a state (the one its takeover opened).</summary>
    private static bool Taking(NurseProcedureScreen screen, EventChoiceMenu menu) =>
        screen._hostMenu is { } host && host.Pointer == menu.Pointer && screen._state != null;

    private static KeyValuePair<string, NetValue> Value(string key, NetValue value) => new(key, value);
}
#endif
