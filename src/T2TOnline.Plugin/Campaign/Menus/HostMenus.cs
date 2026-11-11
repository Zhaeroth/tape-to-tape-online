#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's side of the menu views: every menu the host's game shows, from its <c>T2TMenu.Show</c> to its <c>Hide_Internal</c>,
/// with its data (<see cref="MenuPayloads"/>), the one shared cursor (<c>NavigationController.SetCurrentNavigable</c>), click results
/// (<c>ForceClick</c>), list selectors (<c>CycleList.SelectIndex</c>), the menus' own steps and rerolled offers, as
/// <see cref="MenuEvent"/>s for the friends; a run-end menu at the run's end also gets its run summary as a
/// <see cref="MenuAttachment"/> (<see cref="RunEndView"/>), sent after its open, and an event menu the choices its event rolled
/// (<see cref="EventCapture"/>), at its open and again at each page or reroll, with its intro, map peek, details and takeover (the
/// Nurse's procedure screen, whose text then goes in the menu's data, <see cref="NurseScreen"/>) as menu values; an old squad menu
/// gets the host's new run and its offers once its rolls are in, its picks as menu values (<see cref="RunStartCapture"/>). Menus are
/// tracked while hosting; their data is read and sent once a friend is in, and a friend who joins gets every open menu, its values
/// and the selection (<see cref="Join"/>), then their attachments (<see cref="Attachments"/>). The newest menu (the one on top, the
/// one a friend's panel shows) is read again while a friend is in, so text that changes on the host's screen (event pages, the
/// controller assignment screen) reaches the panel: every <see cref="RefreshSeconds"/> while it changes or the host acts in it, less
/// often (up to <see cref="MaxRefreshSeconds"/>) while it stays the same, and never more than a small share of the frame time a read
/// costs (big screens are slow to read). A <see cref="MenuPacer"/> holds back text that changes faster than the link should carry.
/// New offers (a reroll) always go out.
/// </summary>
internal sealed class HostMenus
{
    /// <summary>Value group: the power-up reward menu's picked power-up (index), sent before its step.</summary>
    public const string PowerUpChoice = "powerUpChoice";

    /// <summary>Value group: a menu's step (the item index carries the step's flag, the value the step).</summary>
    public const string State = "state";

    /// <summary>Value group: the run-end menu's sort (item 0: what it sorts by, item 1: the order).</summary>
    public const string Sort = "sort";

    /// <summary>Value group: the open tab of the in-match menu (its index among the menu's tabs) or of the glossary.</summary>
    public const string Tab = "tab";

    /// <summary>Value group: the glossary entry shown (its index among the glossary's entries ordered by id).</summary>
    public const string Entry = "entry";

    private const double RefreshSeconds = 0.4;
    private const double MaxRefreshSeconds = 1.6;

    // A read may take at most this share of the time until the next (a 10 ms read waits half a second at least).
    private const double ReadShare = 0.02;
    private const int LinesPerMinute = 60;

    // Menus given an attachment before they showed (an InitData postfix runs before T2TMenu.Show), at most this many, for
    // at most this long (a menu shows within frames of its InitData; one that never did is forgotten, so another object
    // at its address later can't take its attachments).
    private const int MaxEarly = 8;
    private const double EarlySeconds = 10;

    // While the Nurse's procedure screen has taken an event menu over, a move on it is read and sent within this long.
    private const double TakeoverRefreshSeconds = 0.25;

    private readonly Action<MenuEvent> _send;
    private readonly Action<HostAttachment> _attach;
    private readonly Action<IntPtr, int, int> _speechValue;
    private readonly MenuBoard _board = new();
    private readonly MenuPacer _pacer = new();
    private readonly Dictionary<IntPtr, Tracked> _byMenu = new();
    private readonly Dictionary<IntPtr, Tracked> _byRoot = new();
    private readonly List<Tracked> _gone = new();
    private readonly Dictionary<IntPtr, (SortedDictionary<MenuAttachmentKind, Attachable> Kinds, double Since)> _early = new();
    private readonly LineBudget _lines = new("Menus (host)", LinesPerMinute);

    /// <summary>Map peeks from an event screen named one by one in a hosting; the rest are counted.</summary>
    private const int PeeksLogged = 5;

    // The kinds of menu whose first details hold was logged (later ones are counted in each menu's close line), and that
    // first hold's look at the host's screen a moment later, waiting.
    private readonly HashSet<string> _holdKinds = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _holdsByKind = new(StringComparer.Ordinal); // this hosting's holds, for its total
    private int _peeks; // this hosting's map peeks from an event screen
    private readonly List<Readout> _readouts = new();

    // A trigger being fired (between FireEvent's prefix and postfix): its handlers may close its menu.
    private Tracked? _firingIn;
    private MenuTarget? _firingTarget;
    private UIInputShortcutBase? _firing;
    private int _firingFrame = -1;

    /// <summary>
    /// The host's side; <paramref name="send"/> queues an event for every friend, <paramref name="attach"/> an attachment
    /// (it goes after the events queued before it, so after its menu's open).
    /// </summary>
    public HostMenus(Action<MenuEvent> send, Action<HostAttachment> attach)
    {
        _send = send;
        _attach = attach;
        _speechValue = SpeechValue;
    }

    /// <summary>What went to friends this session, for its line when it ends.</summary>
    public HostMenuTally Tally { get; } = new();

    /// <summary>The menu lines the log's budget only counted since this was last asked.</summary>
    public int TakeLinesLeftOut() => _lines.TakeLeftOut();

    /// <summary>Forgets everything (hosting stopped); this hosting's holds and map peeks are said in all first, to compare with each friend's.</summary>
    public void Clear()
    {
        if (_holdsByKind.Count > 0)
        {
            ModLog.Info($"Menus: the host's details holds this session: {_holdsByKind.Values.Sum()} ({string.Join(", ", _holdsByKind.Select(k => $"{k.Key} {k.Value}"))}); " +
                "each went to friends.");
        }

        if (_peeks > 0)
        {
            ModLog.Info($"Scenes: the host put the event screen aside to look at the map {_peeks} time(s) this session; friends' maps followed each.");
        }

        _holdsByKind.Clear();
        _peeks = 0;
        _firing = null;
        _firingIn = null;
        _byMenu.Clear();
        _byRoot.Clear();
        _board.Clear();
        _early.Clear();
        _holdKinds.Clear();
        _readouts.Clear();
        EventSpeech.ClearHost();
        SpeechMirror.ClearHost();
        MenuPatches.ClearHostCards();
        Scenes.NodeRewardsMirror.ClearHost();
        GlossaryScrolls.Clear();
        TipViews.ClearHost();
        NurseScreen.Forget(); // a takeover still up when hosting stopped: its screen, focus and log marks go with it
        RunStartCapture.Clear();
        ControllerViews.ClearHost();
        RunHistoryPages.ClearHost();
        ControllerGlyphViews.ClearHost();
        HostTransitions.Clear();
    }

    /// <summary>
    /// The host's event menus showing now, with their serials (0 for one not on the board yet: no friend has been in since it
    /// opened), for their NPC's lines (<see cref="EventSpeech"/>).
    /// </summary>
    public List<(EventChoiceMenu Menu, uint Serial)> EventMenus()
    {
        var menus = new List<(EventChoiceMenu, uint)>(1);
        foreach (Tracked shown in _byMenu.Values)
        {
            if (shown.EventMenu != null && shown.Menu != null)
            {
                menus.Add((shown.EventMenu, shown.Serial));
            }
        }

        return menus;
    }

    /// <summary>The host's menus showing now, friends in or not, newest first (a prompt's opener is among them, <see cref="PromptViews"/>).</summary>
    public List<AbstractMenu> OpenMenus() =>
        _byMenu.Values.Where(s => s.Menu != null).OrderByDescending(s => s.OpenedAt).Select(s => s.Menu).ToList();

    /// <summary>A tracked menu's data changed outside its own events (its run-start data can't be sent, say): read and sent again now.</summary>
    public void Reread(AbstractMenu menu, bool friends)
    {
        if (_byMenu.TryGetValue(menu.Pointer, out Tracked? shown))
        {
            Refresh(shown, friends, mustSend: true);
        }
    }

    /// <summary>A menu is showing (<c>T2TMenu.Show</c>). With friends in, its data goes out now.</summary>
    public void Showing(AbstractMenu menu, bool friends)
    {
        string id = MenuGame.Name(MenuGame.MenuId(menu), string.Empty);
        string type = MenuGame.Name(MenuGame.TypeName(menu), "T2TMenu");
        SortedDictionary<MenuAttachmentKind, Attachable>? early =
            _early.Remove(menu.Pointer, out var given) && FrameClock.Seconds - given.Since <= EarlySeconds ? given.Kinds : null;
        if (id.Length == 0 || MenuPayloads.Skipped(id, type) || ExhibitionScreen.IsWarmUp(type) || NativeLobby.Widgets.LobbyPrompts.IsAskedFor(type))
        {
            return;
        }

        // Its speakers' lines and presses go to friends (the speech mirror: a Canada curse menu's two).
        SpeechMirror.HostShowing(menu);
        if (_byMenu.TryGetValue(menu.Pointer, out Tracked? known))
        {
            // Shown again without a close in between (a retained menu): its data may be new.
            Adopt(known, early, friends);
            Refresh(known, friends, mustSend: true);
            return;
        }

        var shown = new Tracked(menu, id, type, MenuGame.Name(MenuGame.SceneName(), string.Empty));
        _byMenu[menu.Pointer] = shown;
        _byRoot[shown.Root] = shown;
        Adopt(shown, early, friends: false);
        if (friends)
        {
            Track(shown, send: true);
        }
    }

    /// <summary>
    /// Attaches data of <paramref name="kind"/> to the host's <paramref name="menu"/> for friends (an event menu's NPC line, a
    /// prompt's data): <paramref name="make"/> builds the attachment for the menu's serial, now when the menu is on the board,
    /// else once it is (a friend joins). It replaces the menu's earlier attachment of that kind (a friend keeps the newest of
    /// each kind), and with <paramref name="friends"/> in it goes to them at once (after the menu's open: one channel keeps
    /// the order); a friend who joins later gets it too. A menu that hasn't shown yet (its <c>InitData</c> runs before its
    /// <c>T2TMenu.Show</c>) keeps it until it shows (at most <see cref="MaxEarly"/> such menus, each for
    /// <see cref="EarlySeconds"/>). <paramref name="about"/> is a few words for the "sent" log line. False, with why, when
    /// <paramref name="make"/> throws (data out of its kind's limits: logged by the caller). The run summary and the event
    /// choices are attached here by their own capture.
    /// </summary>
    public bool Attach(AbstractMenu menu, MenuAttachmentKind kind, Func<uint, MenuAttachment> make, string about, bool friends, out string problem)
    {
        ArgumentNullException.ThrowIfNull(make);
        problem = string.Empty;
        if (_byMenu.TryGetValue(menu.Pointer, out Tracked? shown))
        {
            return Put(shown, kind, make, about, friends, out problem);
        }

        // Not showing yet: kept for its Show. Menus that never showed are forgotten after a while (or the oldest, with too
        // many).
        double now = FrameClock.Seconds;
        foreach (IntPtr old in _early.Where(e => now - e.Value.Since > EarlySeconds).Select(e => e.Key).ToList())
        {
            _early.Remove(old);
        }

        if (!_early.TryGetValue(menu.Pointer, out var early))
        {
            if (_early.Count >= MaxEarly)
            {
                _early.Remove(_early.OrderBy(e => e.Value.Since).First().Key);
            }

            _early[menu.Pointer] = early = (new SortedDictionary<MenuAttachmentKind, Attachable>(), now);
        }

        early.Kinds[kind] = new Attachable(make, about);
        return true;
    }

    /// <summary>Takes the attachments a menu was given before it showed; made and sent now when it's on the board and <paramref name="friends"/> are in.</summary>
    private void Adopt(Tracked shown, SortedDictionary<MenuAttachmentKind, Attachable>? early, bool friends)
    {
        if (early == null)
        {
            return;
        }

        foreach (KeyValuePair<MenuAttachmentKind, Attachable> entry in early)
        {
            if (!Put(shown, entry.Key, entry.Value.Make, entry.Value.About, friends, out string problem))
            {
                ModLog.Warn($"Menus: the host's {shown.Id} {entry.Key} attachment can't be sent ({problem}); friends go without it.");
            }
        }
    }

    /// <summary>
    /// Puts an attachment of <paramref name="kind"/> on a tracked menu in place of its earlier one; made now when the menu has
    /// a serial (sent when <paramref name="send"/>), else when it gets one (<see cref="Track"/>). False, with why, when it
    /// can't be made (it's dropped).
    /// </summary>
    private bool Put(Tracked shown, MenuAttachmentKind kind, Func<uint, MenuAttachment> make, string about, bool send, out string problem)
    {
        var entry = new Attachable(make, about);
        shown.Attachments[kind] = entry;
        return Make(shown, kind, entry, send, out problem);
    }

    /// <summary>Makes <paramref name="entry"/> for the menu's serial (nothing yet without one), and queues it for every friend when <paramref name="send"/>.</summary>
    private bool Make(Tracked shown, MenuAttachmentKind kind, Attachable entry, bool send, out string problem)
    {
        problem = string.Empty;
        if (shown.Serial == 0)
        {
            return true;
        }

        HostAttachment? made = entry.Made;
        if (made?.Attachment.Serial != shown.Serial)
        {
            try
            {
                entry.Made = made = new HostAttachment(entry.Make(shown.Serial), shown.Id, entry.About);
            }
            catch (ArgumentException ex)
            {
                shown.Attachments.Remove(kind);
                problem = ModLog.SafeErrorText(ex);
                return false;
            }
        }

        if (send)
        {
            _attach(made!);
        }

        return true;
    }

    /// <summary>A menu is closing (<c>Hide_Internal</c>, every close).</summary>
    public void Hiding(AbstractMenu menu)
    {
        if (_byMenu.Remove(menu.Pointer, out Tracked? shown))
        {
            Forget(shown, gone: false);
        }
    }

    /// <summary>The cursor moved (<c>SetCurrentNavigable</c>): sent when it's in a tracked menu.</summary>
    public void Selected(NavigationController navigation)
    {
        AbstractNavigable? navigable = navigation.CurrentNavigable;
        if (navigable == null || Owner(navigable.transform) is not { Serial: not 0 } shown)
        {
            return;
        }

        MenuTarget? target = MenuTargets.PathTo(shown.Menu.transform, navigable);
        if (target != null && _board.Selected(shown.Serial, target) is { } changed)
        {
            // The navigation mode first: a friend's game plays the select sound and shows the highlight only outside mouse
            // mode, as the host's does.
            NavMode(shown, (int)navigation.CurrentNavigationMode, send: true);
            shown.Selections++;
            Active(shown);
            _send(changed);
        }
    }

    /// <summary>
    /// A shortcut in a shared menu fired its trigger (<c>UIInputShortcutBase.FireEvent</c>, after its handlers): when its
    /// sound plays on the host (its effect isn't one that skips it), the press goes to friends as a value on its path, so a
    /// friend's copy plays the same shortcut's trigger or failure sound (<see cref="MenuPress"/>).
    /// </summary>
    public void Pressed(UIInputShortcutBase shortcut)
    {
        _firing = null;
        _firingIn = null;
        if (Owner(shortcut.transform) is not { Serial: not 0 } shown)
        {
            return;
        }

        MenuTarget? target = MenuTargets.PathTo(shown.Menu.transform, shortcut);
        if (target != null)
        {
            SendPress(shown, target, shortcut);
        }
    }

    /// <summary>
    /// A shortcut in a shared menu is about to fire its trigger (<c>FireEvent</c>'s prefix). A handler may close the menu at
    /// once (a prompt's answer): then <see cref="Pressed"/> finds it off the board, and the press goes just before its close
    /// instead (<see cref="Forget"/>), so a friend's copy still has the shortcut when it plays its sound.
    /// </summary>
    public void PressStarting(UIInputShortcutBase shortcut)
    {
        _firing = null;
        _firingIn = null;
        if (Owner(shortcut.transform) is { Serial: not 0 } shown && MenuTargets.PathTo(shown.Menu.transform, shortcut) is { } target)
        {
            _firing = shortcut;
            _firingIn = shown;
            _firingTarget = target;
            _firingFrame = Time.frameCount;
        }
    }

    /// <summary>
    /// A press on a disabled shortcut in a shared menu (the post-input event <c>UIInputShortcutBase.Release</c> raises). Its
    /// sound component then plays the shortcut's trigger sound when it doesn't check a press's success, and nothing when it does
    /// (verified in the game's code: <c>OnPostInput</c>): only the first has a sound to send, as a
    /// press that worked, which a friend's copy of the same component plays the same way.
    /// </summary>
    /// <returns>True when it went.</returns>
    public bool PressedDisabled(UIInputShortcutBase shortcut)
    {
        if (Owner(shortcut.transform) is { Serial: not 0 } shown && MenuAudio.SoundOf(shown.Menu, shortcut) is { m_CheckClickSuccess: false } &&
            MenuTargets.PathTo(shown.Menu.transform, shortcut) is { } target)
        {
            SendPress(shown, target, success: true);
            return true;
        }

        return false;
    }

    /// <summary>A trigger's press, as a value on its path; not when its effect skips its sound.</summary>
    private void SendPress(Tracked shown, MenuTarget target, UIInputShortcutBase shortcut)
    {
        if (shortcut.m_InputFx == AbstractNavigable.EClickFx.Default)
        {
            SendPress(shown, target, shortcut.m_InputSuccess);
        }
    }

    /// <summary>A press with a sound, as a value on its path (alternating, so each press is a change).</summary>
    private void SendPress(Tracked shown, MenuTarget target, bool success)
    {
        int count = shown.Presses.TryGetValue(target, out int presses) ? presses : 0;
        shown.Presses[target] = count + 1;
        if (_board.ValueSet(shown.Serial, target, MenuPress.Value(success, count)) is { } value)
        {
            shown.Values++;
            Active(shown); // what a press changes (a details panel's tab, say) is read again soon
            _send(value);
        }
    }

    /// <summary>The host's game played its back sound without closing a shared menu: it goes to friends on the newest menu.</summary>
    public void BackSound()
    {
        Tracked? newest = null;
        foreach (Tracked shown in _byMenu.Values)
        {
            if (shown.Serial != 0 && shown.Menu != null && (newest == null || shown.Serial > newest.Serial))
            {
                newest = shown;
            }
        }

        if (newest != null && _board.ValueSet(newest.Serial, MenuTarget.AtItem(MenuSounds.BackGroup, 0), MenuSounds.BackValue(newest.Backs++)) is { } value)
        {
            newest.Values++;
            _send(value);
        }
    }

    /// <summary>The host's navigation mode as a value of the menu, when it changed (sent when <paramref name="send"/>).</summary>
    private void NavMode(Tracked shown, int mode, bool send)
    {
        if (mode is < 0 or > MenuSounds.MaxNavMode || mode == shown.NavMode)
        {
            return;
        }

        shown.NavMode = mode;
        Record(shown, MenuSounds.NavModeGroup, 0, mode, send);
    }

    /// <summary>An element was clicked (<c>ForceClick</c>, after its handlers): its result, for the friends' feedback.</summary>
    public void Clicked(AbstractNavigable navigable)
    {
        if (Owner(navigable.transform) is not { Serial: not 0 } shown)
        {
            return;
        }

        MenuTarget? target = MenuTargets.PathTo(shown.Menu.transform, navigable);
        if (target != null && _board.Clicked(shown.Serial, target, (byte)navigable.m_ClickResult, (byte)navigable.m_ClickFx) is { } click)
        {
            shown.Clicks++;
            Active(shown);
            _send(click);
            NamePick(shown, navigable);
        }
    }

    /// <summary>
    /// A click that picks something the friends' logs name too: an event card (once per set of choices) or a Canada curse, in
    /// the same words as their lines. One log line a pick. Never throws: the click has gone out, and a log
    /// line can't count against the views.
    /// </summary>
    private void NamePick(Tracked shown, AbstractNavigable navigable)
    {
        try
        {
            if (navigable.m_ClickResult != AbstractNavigable.EClickResult.Success)
            {
                return;
            }

            if (shown.EventMenu is { } menu && shown.EventSet is { } set && shown.PickedVersion != set.Version)
            {
                int choice = EventCapture.ChoiceOf(menu, navigable);
                if (choice >= 0)
                {
                    shown.PickedVersion = set.Version;
                    ModLog.Info($"Menus: the host picked {set.PickWords(choice, own: true, LogNames.TextShape)} in {shown.Id} (#{shown.Serial}).");
                }
            }
            else if (shown.CurseMenu is { } curses && !shown.CursePicked && _board.TryGet(shown.Serial, out MenuState state))
            {
                int card = CanadaViews.CardOf(curses, navigable);
                if (card >= 0)
                {
                    shown.CursePicked = true;
                    ModLog.Info($"Menus: the host picked {CanadaViews.CurseWords(state.Data, card)} in {shown.Id} (#{shown.Serial}, pick {CanadaViews.PickOf(state.Data)}).");
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: the host's pick in {shown.Id} wasn't named ({ex.GetType().Name}).");
        }
    }

    /// <summary>The host answered a prompt on the board (<paramref name="answer"/>: yes, no, cancel, dismissed): named once.</summary>
    public void Answered(AbstractMenu menu, string answer)
    {
        if (_byMenu.TryGetValue(menu.Pointer, out Tracked? shown) && shown.Serial != 0 && !shown.Answered)
        {
            shown.Answered = true;
            ModLog.Info($"Menus: the host answered {shown.Id} (#{shown.Serial}): {answer}.");
        }
    }

    /// <summary>
    /// A shortcut in a shared menu went down (<paramref name="held"/>) or up as a hold (<c>UIInputShortcutBase.FireEvent</c>
    /// with its hold start or release; <see cref="DetailsHolds"/>): sent as a value on its path, so a friend's copy fires the
    /// same shortcut's own hold event. The first hold in each kind of menu is logged with what it shows, and what's up on the
    /// host's screen a moment later (<see cref="DetailsHolds.Readout"/>: the same words a friend's log uses, to compare).
    /// </summary>
    public void Held(UIInputShortcutBase shortcut, bool held)
    {
        if (Owner(shortcut.transform) is not { Serial: not 0 } shown)
        {
            return;
        }

        MenuTarget? element = MenuTargets.PathTo(shown.Menu.transform, shortcut);
        if (element == null || _board.ValueSet(shown.Serial, element, MenuHold.Value(held)) is not { } value)
        {
            return;
        }

        shown.Values++;
        Active(shown); // a hold may switch what the menu shows (its panels' detailed stats): read again soon
        if (held)
        {
            shown.Holds++;
            _holdsByKind[shown.Type] = _holdsByKind.GetValueOrDefault(shown.Type) + 1;
            if (_holdKinds.Add(shown.Type))
            {
                string shows = DetailsHolds.Shows(shown.Menu, shortcut);
                ModLog.Info($"Menus: the host held {element} in {shown.Id} (#{shown.Serial}); friends' copies hold it too" +
                    $" ({(shows.Length > 0 ? $"it shows {shows}" : "no details panel of the game's listens to it: its sound only")}). Further holds are counted.");
                _readouts.Add(new Readout(shown, shortcut, element, FrameClock.Seconds + DetailsHolds.ReadoutSeconds));
            }
        }

        _send(value);
    }

    /// <summary>The first hold in each kind of menu: what the host's screen shows a moment later, logged (still held or not).</summary>
    private void ReadOut()
    {
        double now = FrameClock.Seconds;
        for (int i = _readouts.Count - 1; i >= 0; i--)
        {
            Readout due = _readouts[i];
            if (now < due.At)
            {
                continue;
            }

            _readouts.RemoveAt(i);
            if (due.Shown.Menu == null || due.Shortcut == null || !_byMenu.ContainsKey(due.Shown.Pointer))
            {
                continue; // closed by then
            }

            string state = due.Shortcut.m_IsButtonPressed ? "still held" : "let go by then";
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Menus: the host's {due.Shown.Id} (#{due.Shown.Serial}) {DetailsHolds.ReadoutSeconds:0.0} s into its first hold of {due.Target} ") +
                $"({state}): {DetailsHolds.Readout(due.Shown.Menu, due.Shortcut)}.");
        }
    }

    /// <summary>A list selector took a value (<c>CycleList.SelectIndex</c>).</summary>
    public void Value(CycleList list)
    {
        int index = list.m_CurIndex;
        if (index < 0 || index > MenuHold.MaxListIndex || Owner(list.transform) is not { Serial: not 0 } shown)
        {
            return;
        }

        MenuTarget? target = MenuTargets.PathTo(shown.Menu.transform, list);
        if (target != null && _board.ValueSet(shown.Serial, target, index) is { } value)
        {
            shown.Values++;
            Active(shown); // the item it shows is read again soon (a friend without that choice shows the host's)
            _send(value);
        }
    }

    /// <summary>A menu's step changed (<paramref name="group"/> <see cref="State"/> or <see cref="PowerUpChoice"/>).</summary>
    public void Step(AbstractMenu menu, string group, int flag, int value)
    {
        if (value < 0 || value > MenuTarget.MaxIndex || !_byMenu.TryGetValue(menu.Pointer, out Tracked? shown) || shown.Serial == 0)
        {
            return;
        }

        if (_board.ValueSet(shown.Serial, MenuTarget.AtItem(group, Math.Clamp(flag, 0, 1)), value) is { } step)
        {
            shown.Values++;
            _send(step);
        }
    }

    /// <summary>
    /// A menu's step on one of its elements (<paramref name="group"/>, item <paramref name="item"/>: the controller screen's
    /// feedback on an icon, <see cref="ControllerViews.HostFeedback"/>), with <paramref name="value"/>.
    /// </summary>
    public void Item(AbstractMenu menu, string group, int item, int value)
    {
        if (item < 0 || item > MenuTarget.MaxIndex || value < 0 || value > MenuTarget.MaxIndex || !_byMenu.TryGetValue(menu.Pointer, out Tracked? shown) || shown.Serial == 0)
        {
            return;
        }

        if (_board.ValueSet(shown.Serial, MenuTarget.AtItem(group, item), value) is { } step)
        {
            shown.Values++;
            _send(step);
        }
    }

    /// <summary>
    /// A menu moved its elements under the cursor (the run-end menu's sort re-orders its rows): the cursor's path is read
    /// again and sent when it changed.
    /// </summary>
    public void Reselect()
    {
        if (MenuGame.Module()?.NavigationController is { } navigation)
        {
            Selected(navigation);
        }
    }

    /// <summary>
    /// The attachments of the menus open now, every kind of each, oldest menu first and its kinds in order (for a friend who
    /// joins: after <see cref="Join"/>'s events).
    /// </summary>
    public IReadOnlyList<HostAttachment> Attachments() =>
        _byMenu.Values.Where(s => s.Menu != null && s.Serial != 0 && _board.TryGet(s.Serial, out _))
            .OrderBy(s => s.OpenedAt)
            .SelectMany(s => s.Attachments.Values.Select(a => a.Made).Where(made => made != null && made.Attachment.Serial == s.Serial))
            .Select(made => made!)
            .ToList();

    /// <summary>A reward menu rerolled its offers (<c>RerollChoicesNow</c>): the new data goes out now.</summary>
    public void Rerolled(AbstractMenu menu, bool friends)
    {
        if (_byMenu.TryGetValue(menu.Pointer, out Tracked? shown))
        {
            ModLog.Info($"Menus: host rerolled {shown.Id} (#{shown.Serial}).");
            Refresh(shown, friends, mustSend: true);
        }
    }

    /// <summary>A menu changed what its data shows (a lineup's swap or toggle): its data is read again and goes out now.</summary>
    public void Changed(AbstractMenu menu, bool friends)
    {
        if (_byMenu.TryGetValue(menu.Pointer, out Tracked? shown))
        {
            Active(shown);
            Refresh(shown, friends, mustSend: true);
        }
    }

    /// <summary>
    /// An event menu moved to a new set of choices (<paramref name="step"/>: a page or a reroll; its event already rolled it,
    /// and its cards aren't made yet): the new set goes out now, as the menu's attachment. A set that can't go sends the
    /// event to friends as text from here on (its data says why).
    /// </summary>
    public void EventChoices(EventChoiceMenu menu, EventSetStep step, bool friends)
    {
        if (!_byMenu.TryGetValue(menu.Pointer, out Tracked? shown) || shown.EventMenu == null || !shown.EventTried || shown.EventSet == null)
        {
            return; // not shared yet (it's read when a friend joins), or already text
        }

        int version = Math.Min(shown.EventSet.Version + 1, EventChoiceSet.MaxVersion);
        EventChoiceSet? set;
        string problem;
        try
        {
            set = EventCapture.Read(menu, step, version, out problem);
        }
        catch (Exception ex)
        {
            set = null;
            problem = $"{ModLog.SafeErrorText(ex)}";
        }

        if (set == null)
        {
            shown.EventSet = null;
            shown.EventProblem = $"its next choices couldn't be read ({problem})";
            ModLog.Warn($"Menus: the host's {shown.Id} (#{shown.Serial}) {step.ToString().ToLowerInvariant()}: {shown.EventProblem}; friends see it as text from here.");
            Refresh(shown, friends, mustSend: true);
            return;
        }

        shown.EventSet = set;
        if (shown.Serial == 0)
        {
            return;
        }

        Log($"Menus: host's {shown.Id} (#{shown.Serial}) {(step == EventSetStep.Reroll ? "rerolled its choices" : "moved to its next choices")}: {set}: {set.ChoiceWords(own: true, LogNames.TextShape)}.");
        AttachEvent(shown, friends);

        // A Nurse's reroll ran its Initialize again (a new roster): read and sent again (NurseHost).
        NurseHost.Rolled(this, menu, friends);
    }

    /// <summary>
    /// An event menu ran its reroll (<c>RerollChoices</c>, its one way in). Its event has rolled, its count of rerolls went
    /// down, and it's fading its old cards out: the new set goes out now. With none left it did nothing, and a Fan Number 1's or
    /// General Manager's reroll is a page (<c>GoToNextSetOfChoices</c>, verified in the game's code), already gone out
    /// with the new count: the count tells them apart (only <c>InitData</c> and the reroll write it: checked across the game's code).
    /// </summary>
    public void EventRerolled(EventChoiceMenu menu, bool friends)
    {
        if (_byMenu.TryGetValue(menu.Pointer, out Tracked? shown) && shown.EventSet is { } set && set.RerolledSince(menu.m_RerollCount))
        {
            EventChoices(menu, EventSetStep.Reroll, friends);
        }
    }

    /// <summary>
    /// Every frame: closes menus whose objects are gone, sends an open event menu's intro, map peek and details when they
    /// change, and reads the newest menu's data again now and then (only the newest: it's the one on top, and the one a
    /// friend's panel shows; a menu under it is read again when it's on top).
    /// </summary>
    public void Update(bool friends)
    {
        ControllerViews.HostTick(this, friends); // a controller screen an input changed goes out now
        RunHistoryPages.HostTick(friends); // the logo of the history page the host rests on goes to friends
        ControllerGlyphViews.HostTick(friends); // a player's button names that changed go to friends on the controller screen
        if (SpeechMirror.HostSpeaking)
        {
            // The mirrored speakers' new lines (their presses come through the press patch); changed values go on the board.
            SpeechMirror.HostTick(_speechValue);
        }

        Tracked? newest = null;
        foreach (Tracked shown in _byMenu.Values)
        {
            if (shown.Menu == null)
            {
                _gone.Add(shown);
            }
            else if (shown.Serial != 0 && (newest == null || shown.Serial > newest.Serial))
            {
                newest = shown;
            }

            if (friends && shown.Serial != 0 && shown.EventMenu != null && shown.Menu != null)
            {
                EventStates(shown, send: true);
            }

            if (friends && shown.Serial != 0 && shown.CurseMenu != null && !shown.CurseCardsSaid && shown.Menu != null && CanadaViews.CardsUp(shown.CurseMenu))
            {
                // Its dialogue is over: a friend's copy, whose cards come after its own, runs out whatever still speaks there.
                shown.CurseCardsSaid = true;
                Record(shown, CanadaViews.CardsGroup, 0, 1, send: true);
            }
        }

        foreach (Tracked shown in _gone)
        {
            _byMenu.Remove(shown.Pointer);
            Forget(shown, gone: true);
        }

        _gone.Clear();
        if (_readouts.Count > 0)
        {
            ReadOut();
        }

        if (HostTransitions.Checking)
        {
            HostTransitions.Tick();
        }

        if (friends && newest != null && FrameClock.Seconds >= newest.RefreshAt)
        {
            Refresh(newest, friends, mustSend: false);
        }

        if (friends && newest?.Type == MenuPayloads.Glossary && newest.Menu != null)
        {
            // Its list's scroll: a move of its row reads and sends its data again (v1).
            GlossaryScrolls.Watch(this, newest.Menu);
        }

        TipViews.HostTick(this, friends); // the host's campaign tip, a board item of its own while it's up (v1)
    }

    /// <summary>
    /// Puts a screen of the host's that isn't one of the game's menus on the board (the host's campaign tip,
    /// <see cref="TipViews"/>): it goes to every friend as an open menu of <paramref name="type"/> with <paramref name="data"/>,
    /// and to a friend who joins while it's up. Its serial, or 0 when the board is full.
    /// </summary>
    public uint OpenItem(string id, string type, MenuData data)
    {
        MenuEvent? opened = _board.Opened(id, type, MenuGame.Name(MenuGame.SceneName(), string.Empty), data);
        if (opened == null)
        {
            Tally.BoardFull();
            return 0;
        }

        _send(opened);
        return opened.Serial;
    }

    /// <summary>
    /// A value of a screen put on the board with <see cref="OpenItem"/> (the progress of a tip's speech): it goes to every
    /// friend, and to a friend who joins while it's up.
    /// </summary>
    public void ItemValue(uint serial, string group, int index, int value)
    {
        if (value is >= 0 and <= MenuTarget.MaxIndex && _board.ValueSet(serial, MenuTarget.AtItem(group, index), value) is { } recorded)
        {
            _send(recorded);
        }
    }

    /// <summary>Takes a screen put on the board with <see cref="OpenItem"/> off it (its close goes to every friend).</summary>
    public void CloseItem(uint serial)
    {
        if (serial != 0 && _board.Closed(serial) is { } closed)
        {
            _send(closed);
        }
    }

    /// <summary>
    /// A friend joined: every menu shown before any friend was in is put on the board now (oldest first), the others'
    /// data and the current selection are read again, and the friend gets everything open, in order.
    /// </summary>
    public IReadOnlyList<MenuEvent> Join()
    {
        foreach (Tracked shown in _byMenu.Values.Where(s => s.Menu != null).OrderBy(s => s.OpenedAt).ToList())
        {
            if (shown.Serial == 0)
            {
                Track(shown, send: false);
            }
            else
            {
                // Only the newest is read again as it changes: the others' data may be old by now (the friends who are
                // in get it too).
                Refresh(shown, friends: true, mustSend: true);
                if (shown.Menu.TryCast<UnlockableMenu>() is { } unlockables)
                {
                    // Its tab and row: steps go only with friends in, so the host may have moved since the last one dropped.
                    RecordUnlockables(shown, unlockables, send: true);
                }
            }
        }

        NavigationController? navigation = MenuGame.Module()?.NavigationController;
        if (navigation != null)
        {
            AbstractNavigable? current = navigation.CurrentNavigable;
            if (current != null && Owner(current.transform) is { Serial: not 0 } owner && MenuTargets.PathTo(owner.Menu.transform, current) is { } target)
            {
                _ = _board.Selected(owner.Serial, target);
            }
        }

        return _board.Snapshot();
    }

    /// <summary>
    /// Puts a shown menu on the board with its data, and its attachment (a run-end menu's run summary) after it. Logged
    /// when the board is full.
    /// </summary>
    private void Track(Tracked shown, bool send)
    {
        PrepareSummary(shown);
        PrepareEvent(shown);
        MenuData data = Read(shown) ?? MenuData.Empty;
        MenuEvent? opened = _board.Opened(shown.Id, shown.Type, shown.Scene, data);
        if (opened == null)
        {
            Tally.BoardFull();
            Log($"Menus: host opened {shown.Id}, but {MenuBoard.MaxOpen} menus are already open; friends won't see this one.");
            return;
        }

        shown.Serial = opened.Serial;
        shown.RefreshAt = FrameClock.Seconds + RefreshSeconds;
        Tally.Opened(shown.Type);
        Log($"Menus: host opened {shown.Id} (#{shown.Serial}, {shown.Type}{Context(data)} in \"{shown.Scene}\"): {Describe(data)}.");
        if (shown.CurseMenu != null)
        {
            CanadaViews.NameForLog(shown.Id, shown.Serial, data);
        }
        else if (shown.Type == MenuPayloads.PermanentUnlock)
        {
            UnlockViews.NameForLog(shown.Id, shown.Serial, data);
        }

        if (send)
        {
            _pacer.Charge(data.ByteLength, FrameClock.Seconds);
            _send(opened);
        }

        if (shown.Menu.TryCast<RunEndMenu>() is { } runEnd)
        {
            // The sort it opens with (a menu the game keeps keeps its last sort): a friend's copy sorts the same way.
            Record(shown, Sort, 0, (int)runEnd.m_CurSortType, send);
            Record(shown, Sort, 1, (int)runEnd.m_CurSortOrder, send);
        }
        else if (shown.Menu.TryCast<UnlockableMenu>() is { } unlockables)
        {
            RecordUnlockables(shown, unlockables, send);
        }

        if (MenuGame.Module()?.NavigationController is { } navigation)
        {
            // The navigation mode it opens in (then with each selection that finds it changed).
            NavMode(shown, (int)navigation.CurrentNavigationMode, send);
        }

        if (shown.EventMenu != null)
        {
            // Its intro, map peek and details as they are now; then as they change (Update).
            EventStates(shown, send);
            AttachEvent(shown, send);

            // A Nurse's roster, right after its choices (a friend rebuilds it for the real procedure screen).
            NurseHost.Attach(this, shown.EventMenu, shown.EventSet != null, send);
        }

        // Its speakers' progress as it is now (the speech mirror); then as it changes (Update).
        SpeechMirror.HostCurrent(shown.Pointer, (slot, value) => Record(shown, SpeechProgress.Group, slot, value, send));

        AttachSummary(shown, send);

        // What else it was given before it had a serial (an NPC line, a prompt's data), now made for it.
        foreach (KeyValuePair<MenuAttachmentKind, Attachable> entry in shown.Attachments.Where(a => a.Value.Made == null).ToList())
        {
            if (!Make(shown, entry.Key, entry.Value, send, out string problem))
            {
                ModLog.Warn($"Menus: the host's {shown.Id} {entry.Key} attachment can't be sent ({problem}); friends go without it.");
            }
        }
    }

    /// <summary>
    /// The unlockables' tab and row as they show now, on the board: one shown before any friend was in sent no steps, and a
    /// friend who joins would see its first tab until the host moved. Sent when <paramref name="send"/>.
    /// </summary>
    private void RecordUnlockables(Tracked shown, UnlockableMenu menu, bool send)
    {
        (int tab, int row) = UnlockablesView.HostNow(menu);
        Record(shown, UnlockablesView.TabGroup, 0, tab, send);
        Record(shown, UnlockablesView.RowGroup, 0, row, send);
    }

    /// <summary>
    /// An event menu: what its event rolled, read once per opening (its cards are built by its <c>T2TMenu.Show</c>), before
    /// its data is first read (its data says whether the choices go with it, or why not). Never throws: a failure is logged
    /// and the event goes as text.
    /// </summary>
    private static void PrepareEvent(Tracked shown)
    {
        if (shown.EventTried || shown.EventMenu is not { } menu)
        {
            return;
        }

        shown.EventTried = true;
        shown.EventRun = MenuPayloads.HostRun();
        string problem;
        try
        {
            shown.EventClass = menu.SourceNode == null ? string.Empty : MenuGame.TypeName(menu.SourceNode);
            shown.EventSet = EventCapture.Read(menu, EventSetStep.Open, 0, out problem);
        }
        catch (Exception ex)
        {
            shown.EventSet = null;
            problem = $"{ModLog.SafeErrorText(ex)}";
        }

        shown.EventProblem = shown.EventSet == null ? problem : string.Empty;
        string what = $"{EventCapture.Words(shown.EventClass)} event \"{Core.Diagnostics.SafeText.Clean(menu.SourceNode?.name, 48)}\"";
        if (shown.EventSet != null)
        {
            ModLog.Info($"Menus: the host's {shown.Id} is a {what}: {shown.EventSet.Choices.Count} choice(s) go to friends for the real event screen: {shown.EventSet.ChoiceWords(own: true, LogNames.TextShape)}.");
        }
        else
        {
            ModLog.Info($"Menus: the host's {shown.Id} is a {what}; friends see it as text: {problem}.");
        }
    }

    /// <summary>
    /// An event menu's choices as its attachment, made for its newest set (again after a page or reroll); queued for every
    /// friend when <paramref name="send"/>. A set that can't be sent leaves the event as text.
    /// </summary>
    private void AttachEvent(Tracked shown, bool send)
    {
        if (shown.EventSet is not { } set || shown.Serial == 0)
        {
            return;
        }

        if (!Put(shown, MenuAttachmentKind.EventChoices, serial => MenuAttachment.EventChoices(serial, set), set.ToString(), send, out string problem))
        {
            shown.EventSet = null;
            shown.EventProblem = $"its choices can't be sent ({problem})";
            ModLog.Warn($"Menus: the host's {shown.Id} choices can't be sent ({problem}); friends see it as text.");
        }
    }

    /// <summary>
    /// An event menu's intro, map peek, details and takeover as menu values: each one set when it changed (all of them for a
    /// menu just put on the board), sent when <paramref name="send"/>. While the Nurse's procedure screen has taken it over,
    /// the menu's data carries that screen as text (<see cref="NurseScreen"/>): read again at once when the takeover starts,
    /// and soon after each move on it.
    /// </summary>
    private void EventStates(Tracked shown, bool send)
    {
        EventChoiceMenu menu = shown.EventMenu!;
        Span<int> now = stackalloc int[EventCapture.StateItems];

        // The intro: not started yet, playing, or over (a friend ends its own only once the host's has played and ended). This
        // runs only with friends in: a menu first read after the host played it alone has its cards in, and its intro is over.
        bool playing = menu.m_IsPlayingIntro;
        bool cards = MenuPatches.HostCardsShown(menu) == 1;
        shown.IntroPlayed |= playing || cards;
        now[EventCapture.Intro] = playing ? EventCapture.IntroPlaying : shown.IntroPlayed ? EventCapture.IntroOver : EventCapture.IntroNotYet;
        now[EventCapture.Peek] = menu.IsVisible ? 1 : 0;
        now[EventCapture.Details] = menu.m_IsShowingDetails ? 1 : 0;
        now[EventCapture.Takeover] = menu.m_IsTakenOver ? 1 : 0;
        now[EventCapture.Cards] = cards ? 1 : 0;
        now[EventCapture.Exit] = MenuPatches.HostExiting(menu) ? 1 : 0;
        int takeover = shown.EventStates[EventCapture.Takeover];
        for (int item = 0; item < now.Length; item++)
        {
            if (shown.EventStates[item] != now[item])
            {
                if (item == EventCapture.Exit && now[item] == 1)
                {
                    Log($"Menus: the host's {shown.Id} (#{shown.Serial}) exits; its NPC says goodbye, and friends' NPCs with it.");
                }

                if (item == EventCapture.Peek && shown.EventStates[item] == 1 && now[item] == 0 && ++_peeks <= PeeksLogged)
                {
                    Log($"Scenes: the host put the event screen aside to look at the map (peek {_peeks}); friends' maps follow" +
                        $"{(_peeks == PeeksLogged ? "; further ones are counted" : string.Empty)}.");
                }

                shown.EventStates[item] = now[item];
                Record(shown, EventCapture.StateGroup, item, now[item], send);
                shown.Values++;
            }
        }

        if (now[EventCapture.Takeover] == 1)
        {
            // Its procedure screen's steps for friends' real screens (NurseHost).
            NurseHost.Tick(this, menu, send);
        }

        if (now[EventCapture.Takeover] != takeover && (takeover == 1 || now[EventCapture.Takeover] == 1))
        {
            // Taken over (or no longer): its data says so, read again now.
            shown.Interval = RefreshSeconds;
            shown.RefreshAt = FrameClock.Seconds;
            if (now[EventCapture.Takeover] == 1)
            {
                Log($"Menus: the host's Nurse took {shown.Id} (#{shown.Serial}) over; friends open their own procedure screen and follow its steps (its text goes too, for one that shows it as text).");
            }
            else
            {
                NurseScreen.Forget(menu);
                Log($"Menus: the host's Nurse procedure screen on {shown.Id} (#{shown.Serial}) closed.");
            }
        }
        else if (now[EventCapture.Takeover] == 1 && NurseScreen.Changed(menu))
        {
            shown.Interval = RefreshSeconds;
            shown.RefreshAt = Math.Min(shown.RefreshAt, FrameClock.Seconds + TakeoverRefreshSeconds);
        }
    }

    /// <summary>
    /// A mirrored speaker's value changed (<see cref="SpeechMirror.HostTick"/>): put on the board of its menu
    /// <paramref name="menu"/> (item <paramref name="slot"/> of <see cref="SpeechProgress.Group"/>) and sent, when that menu
    /// is on it (a friend who joins later gets the newest from the board).
    /// </summary>
    private void SpeechValue(IntPtr menu, int slot, int value)
    {
        if (_byMenu.TryGetValue(menu, out Tracked? shown) && shown.Serial != 0 && shown.Menu != null)
        {
            Record(shown, SpeechProgress.Group, slot, value, send: true);
            shown.Values++;
        }
    }

    /// <summary>A value of a menu just put on the board, sent with it when <paramref name="send"/> (else a friend who joins gets it from the board).</summary>
    private void Record(Tracked shown, string group, int flag, int value, bool send)
    {
        if (value is >= 0 and <= MenuTarget.MaxIndex && _board.ValueSet(shown.Serial, MenuTarget.AtItem(group, flag), value) is { } recorded && send)
        {
            _send(recorded);
        }
    }

    /// <summary>
    /// A run-end menu at the run's end: the run's history entry, made once per opening, before its data is first read (its
    /// data says whether a summary goes with it). Never throws: a failure is logged and the menu goes as text.
    /// </summary>
    private static void PrepareSummary(Tracked shown)
    {
        if (shown.SummaryTried || shown.Menu.TryCast<RunEndMenu>() is not { } runEnd || !RunEndView.IsRunsEnd(runEnd))
        {
            return;
        }

        shown.SummaryTried = true;
        long start = Stopwatch.GetTimestamp();
        string problem;
        try
        {
            shown.Summary = RunEndView.MakeSummary(runEnd, out problem);
        }
        catch (Exception ex)
        {
            shown.Summary = null;
            problem = $"{ModLog.SafeErrorText(ex)}";
        }

        shown.SummaryMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (shown.Summary == null)
        {
            ModLog.Warn($"Menus: couldn't make the run summary for {shown.Id} ({problem}); friends see the run's end as text.");
        }
    }

    /// <summary>A run-end menu's run summary as its attachment, made once it has a serial; queued for every friend when <paramref name="send"/>.</summary>
    private void AttachSummary(Tracked shown, bool send)
    {
        if (shown.Summary is not { } summary || shown.Serial == 0)
        {
            return;
        }

        string about = string.Create(CultureInfo.InvariantCulture, $"{summary.Words()}; made in {shown.SummaryMs:0.0} ms");
        if (!Put(shown, MenuAttachmentKind.RunSummary, serial => MenuAttachment.RunSummary(serial, summary), about, send, out string problem))
        {
            shown.Summary = null;
            ModLog.Warn($"Menus: the run summary for {shown.Id} can't be sent ({problem}); friends see the run's end as text.");
        }
    }

    /// <summary>
    /// Reads a tracked menu's data again; a change goes out as an update, when the pacer lets it (<paramref name="mustSend"/>:
    /// regardless, for new offers).
    /// </summary>
    private void Refresh(Tracked shown, bool friends, bool mustSend)
    {
        if (!friends)
        {
            return;
        }

        if (shown.Serial == 0)
        {
            Track(shown, send: true);
            return;
        }

        double now = FrameClock.Seconds;
        long started = Stopwatch.GetTimestamp();
        MenuData? data = shown.ReadRetry.MayRead(now) ? Read(shown) : null;
        double readSeconds = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        bool same = data == null || !_board.TryGet(shown.Serial, out MenuState state) || state.Data.ContentHash == data.ContentHash;

        // Read less often while nothing changes, and never so often that reading costs more than its share of the time.
        shown.Interval = same ? Math.Min(shown.Interval * 2, MaxRefreshSeconds) : RefreshSeconds;
        shown.RefreshAt = now + Math.Max(shown.Interval, readSeconds / ReadShare);
        if (same || data == null)
        {
            return;
        }

        if (mustSend)
        {
            _pacer.Charge(data.ByteLength, now);
        }
        else if (!_pacer.TrySend(data.ByteLength, now))
        {
            shown.Held++; // read again at the next refresh, with whatever it says then
            return;
        }

        if (_board.Updated(shown.Serial, data) is { } updated)
        {
            shown.Updates++;
            _send(updated);
        }
    }

    private void Forget(Tracked shown, bool gone)
    {
        _byRoot.Remove(shown.Root);
        SpeechMirror.HostForget(shown.Pointer);
        ExhibitionScreen.HostClosed(shown.Pointer);
        MenuPatches.ForgetHostCards(shown.Pointer);
        if (shown.EventMenu != null)
        {
            NurseScreen.Forget(shown.EventMenu);
        }

        // A shortcut's handler closing its own menu: its press goes first, while a friend's copy still has the shortcut.
        if (!gone && ReferenceEquals(_firingIn, shown) && _firing != null && _firingTarget != null && _firingFrame == Time.frameCount)
        {
            SendPress(shown, _firingTarget, _firing);
            _firing = null;
            _firingIn = null;
        }

        // Why it closed, and the transition its own exit shows (HostTransitions): a friend's copy closes the same way.
        MenuCloseReason reason = gone ? MenuCloseReason.Other : HostTransitions.ReasonNow();
        MenuTransition transition = gone || shown.Serial == 0 ? MenuTransition.None : HostTransitions.AtClose(shown.Menu, shown.Id, shown.Type, shown.OpenedAt);
        if (shown.Serial != 0 && _board.Closed(shown.Serial, reason, transition) is { } closed)
        {
            _send(closed);
            string how = (reason == MenuCloseReason.Back ? ", backed out of" : string.Empty) +
                (transition != MenuTransition.None ? $", its exit showing {MenuExits.Name(transition)}" : string.Empty);
            string what = gone ? $"{shown.Id} (#{shown.Serial}) went with the host's scene" : $"host closed {shown.Id} (#{shown.Serial}{how})";
            string held = shown.Held > 0 ? $", {shown.Held} held back by the link budget" : string.Empty;
            string holds = shown.Holds > 0 ? $", {shown.Holds} hold(s) of a details input" : string.Empty;
            Log(string.Create(CultureInfo.InvariantCulture, $"Menus: {what} after {FrameClock.Seconds - shown.OpenedAt:0.0} s ({shown.Selections} selection(s), {shown.Values} value(s){holds}, ") +
                $"{shown.Clicks} click(s); {shown.ReadTimes}; {shown.Updates} text update(s){held}).");
            if (shown.Type == MenuPayloads.RunEnd && OwnUnlocksScreen.HostShowsNone(transition) && shown.Menu.TryCast<RunEndMenu>() is { } runEnd && RunEndView.IsRunsEnd(runEnd))
            {
                // The game's run-end menu exits with the loading transition only when no new-unlocks menu follows.
                ModLog.Info("Menus: no new-unlocks menu follows this run's end on this PC (this profile unlocked nothing new the game shows in it); a friend whose own " +
                    "profile did sees theirs on their own game, before their way home.");
            }
        }

        if (!gone && shown.Type == LineupViews.Type)
        {
            // Its leave has applied the roster to the team by now: a change on the world map is saved for friends.
            LineupViews.HostClosed();
        }
    }

    /// <summary>Every open menu's close, for friends (the menu views are switching off); then everything is forgotten.</summary>
    public IReadOnlyList<MenuEvent> CloseAll()
    {
        var closed = _board.Open.Select(s => MenuEvent.Closed(s.Serial)).ToList();
        Clear();
        return closed;
    }

    /// <summary>
    /// The menu's data, timed (a big menu read on the game's frame is a hitch; its slowest read is logged at its close).
    /// Null when it can't be read: a menu read in its first frames may not be filled yet (the squad screen's selectors), so
    /// it's read again after a wait that grows with each failure (<see cref="MenuReadRetry"/>, from the refresh interval),
    /// and friends get its text once a read works; after <see cref="MenuReadRetry.MaxFailures"/> failures in a row it goes to
    /// friends without its text from then on. The first failure, the give-up and the first recovery are logged, once each.
    /// </summary>
    private static MenuData? Read(Tracked shown)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            EventInfo? events = shown.EventMenu == null ? null
                : new EventInfo(shown.EventClass, shown.EventSet != null, shown.EventProblem, shown.EventRun, shown.EventStates[EventCapture.Takeover] == 1);
            MenuData data = MenuPayloads.Read(shown.Menu, shown.Summary != null, events);
            int failed = shown.ReadRetry.Failures;
            if (shown.ReadRetry.Succeeded())
            {
                ModLog.Info($"Menus: read {shown.Id}'s data after {failed} failed read(s); friends get its text now.");
            }

            return data;
        }
        catch (Exception ex)
        {
            switch (shown.ReadRetry.Failed(FrameClock.Seconds))
            {
                case MenuReadFailure.First:
                    ModLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Menus: couldn't read {shown.Id}'s data ({ModLog.SafeErrorText(ex)}); reading it again in {shown.ReadRetry.Wait:0.0} s ") +
                        "(friends see it without its text meanwhile).");
                    break;
                case MenuReadFailure.GaveUp:
                    ModLog.Warn($"Menus: couldn't read {shown.Id}'s data {shown.ReadRetry.Failures} times in a row ({ModLog.SafeErrorText(ex)}); " +
                        "friends see it without its text from now on.");
                    break;
            }

            return null;
        }
        finally
        {
            shown.ReadTimes.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }
    }

    /// <summary>The tracked menu <paramref name="element"/> is in (the nearest one up its parents), or null.</summary>
    private Tracked? Owner(Transform element)
    {
        int depth = 0;
        for (Transform? at = element; at != null && depth < 64; at = at.parent, depth++)
        {
            if (_byRoot.TryGetValue(at.Pointer, out Tracked? shown))
            {
                return shown;
            }
        }

        return null;
    }

    /// <summary>The in-match menu's context, and whether its numbers went with it (" EndOfGame with its numbers"), or "".</summary>
    private static string Context(MenuData data)
    {
        string context = data.Text("context");
        if (context.Length == 0)
        {
            return string.Empty;
        }

        return data.TryGet(GameMenuModel.VersionKey, out _) ? $" {context} with its numbers" : $" {context} without its numbers";
    }

    private static string Describe(MenuData data)
    {
        var groups = data.Items.GroupBy(i => i.Group).Select(g => $"{g.Count()} {g.Key}(s)");
        string rows = string.Join(", ", groups);
        return rows.Length > 0 ? rows : "no rows";
    }

    /// <summary>The host acted in <paramref name="shown"/> (a selection, a click): its text is read again soon, as it may have changed.</summary>
    private static void Active(Tracked shown)
    {
        shown.Interval = RefreshSeconds;
        shown.RefreshAt = Math.Min(shown.RefreshAt, FrameClock.Seconds + RefreshSeconds);
    }

    /// <summary>One line for the log, up to <see cref="LinesPerMinute"/> a minute (a long run opens hundreds of menus).</summary>
    private void Log(string line)
    {
        if (_lines.TrySpend())
        {
            ModLog.Info(line);
        }
    }

    /// <summary>One menu the host's game is showing.</summary>
    private sealed class Tracked
    {
        public Tracked(AbstractMenu menu, string id, string type, string scene)
        {
            Menu = menu;
            Pointer = menu.Pointer;
            Root = menu.transform.Pointer;
            Id = id;
            Type = type;
            Scene = scene;
            OpenedAt = FrameClock.Seconds;
            EventMenu = menu.TryCast<EventChoiceMenu>();
            CurseMenu = menu.TryCast<CanadaCurseMenu>();
        }

        public AbstractMenu Menu { get; }

        /// <summary>The menu as an event menu, or null for any other.</summary>
        public EventChoiceMenu? EventMenu { get; }

        /// <summary>The menu as a Canada curse menu, or null for any other.</summary>
        public CanadaCurseMenu? CurseMenu { get; }

        /// <summary>A Canada curse menu's cards came up and friends were told (<see cref="CanadaViews.CardsGroup"/>).</summary>
        public bool CurseCardsSaid { get; set; }

        public IntPtr Pointer { get; }

        public IntPtr Root { get; }

        public string Id { get; }

        public string Type { get; }

        public string Scene { get; }

        public double OpenedAt { get; }

        public uint Serial { get; set; }

        public double RefreshAt { get; set; }

        public double Interval { get; set; } = RefreshSeconds;

        public int Selections { get; set; }

        public int Values { get; set; }

        public int Clicks { get; set; }

        /// <summary>How long its reads took: the first apart from the rest (a menu's first opening reads slower).</summary>
        public MenuReadTimes ReadTimes { get; } = new();

        public int Updates { get; set; }

        public int Held { get; set; }

        /// <summary>The host's holds of a shortcut in it (a details input), sent to friends.</summary>
        public int Holds { get; set; }

        /// <summary>The host's presses of each shortcut in it (each press's value alternates, <see cref="MenuPress"/>).</summary>
        public Dictionary<MenuTarget, int> Presses { get; } = new();

        /// <summary>The host's back sounds sent on it (their value alternates).</summary>
        public int Backs { get; set; }

        /// <summary>The navigation mode last sent with it (-1: none yet).</summary>
        public int NavMode { get; set; } = -1;

        /// <summary>Its reads' failures: when it may be read again, and when it isn't any more.</summary>
        public MenuReadRetry ReadRetry { get; } = new(RefreshSeconds, MaxRefreshSeconds);

        /// <summary>A run-end menu at the run's end: its summary was tried (once per opening).</summary>
        public bool SummaryTried { get; set; }

        /// <summary>The run's history entry for friends, or null (not a run's end, or it couldn't be made).</summary>
        public RunHistoryData? Summary { get; set; }

        /// <summary>How long the game's builder took, for the log.</summary>
        public double SummaryMs { get; set; }

        /// <summary>What goes with the menu for friends, by kind (each made once it has a serial; the newest of each kind).</summary>
        public SortedDictionary<MenuAttachmentKind, Attachable> Attachments { get; } = new();

        /// <summary>An event menu: its choices were read (once per opening).</summary>
        public bool EventTried { get; set; }

        /// <summary>An event menu's event class ("CoachEvent"), or "".</summary>
        public string EventClass { get; set; } = string.Empty;

        /// <summary>An event menu's newest choices for friends, or null (it goes as text).</summary>
        public EventChoiceSet? EventSet { get; set; }

        /// <summary>Why an event menu goes as text ("" when it doesn't).</summary>
        public string EventProblem { get; set; } = string.Empty;

        /// <summary>The host's newest run checkpoint when the event opened (a friend needs it for the event's skaters).</summary>
        public long EventRun { get; set; }

        /// <summary>An event menu's intro, map peek, details, takeover and cards as last put on the board (-1: not yet).</summary>
        public int[] EventStates { get; } = Enumerable.Repeat(-1, EventCapture.StateItems).ToArray();

        /// <summary>An event menu's intro has been seen playing.</summary>
        public bool IntroPlayed { get; set; }

        /// <summary>The version of an event menu's choices whose pick was named in the log (-1: none).</summary>
        public int PickedVersion { get; set; } = -1;

        /// <summary>A Canada curse menu's pick was named in the log.</summary>
        public bool CursePicked { get; set; }

        /// <summary>A prompt's answer was named in the log.</summary>
        public bool Answered { get; set; }
    }

    /// <summary>A first details hold in a kind of menu, to read out at <paramref name="At"/>.</summary>
    private sealed record Readout(Tracked Shown, UIInputShortcutBase Shortcut, MenuTarget Target, double At);

    /// <summary>One kind of attachment for a menu: how it's made for the menu's serial, and the one made (null until it has one).</summary>
    private sealed class Attachable
    {
        public Attachable(Func<uint, MenuAttachment> make, string about)
        {
            Make = make;
            About = about;
        }

        public Func<uint, MenuAttachment> Make { get; }

        public string About { get; }

        public HostAttachment? Made { get; set; }
    }
}

/// <summary>An attachment for friends, with the menu it goes with and a few words about it for the log.</summary>
/// <param name="Attachment">The attachment.</param>
/// <param name="MenuId">Its menu's id ("RunEnd").</param>
/// <param name="About">What it is ("run 9f0c..., made in 3.1 ms").</param>
internal sealed record HostAttachment(MenuAttachment Attachment, string MenuId, string About);
#endif
