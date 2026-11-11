#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MelonLoader.Utils;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online.Panel;
using T2TOnline.Plugin.Startup;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// The mod's panel (F1, D46), drawn over the game with Unity's immediate-mode GUI and used with the
/// mouse. Six tabs: Online (the session, its players and their ping; Host a game, Find games and friends' games to
/// join; Leave; while hosting, Invite friends (Steam's invite window) and the seats kept for friends who dropped, to
/// free), Co-op slot (where the host's co-op runs are kept, apart from their own run), Backups (back up now, restore
/// with a confirmation that asks what: your saves, a co-op slot, or both; scrolled with the mouse wheel; the last save
/// check and where they're kept), Settings (the switches friends care about, saved at once), Controller (a check of
/// this PC's pad before a session, and while hosting what each friend's controller gave the game; ModPanel.Controller.cs)
/// and Help (bug reports, the logs and data folders, the keys). No session starts from it while a restored backup waits for a restart. While
/// T2T Online is off (<see cref="Runtime.OfflineShell"/>) the Online tab says why and what to do, and the rest still
/// works (a restore only where the game build is a tested one).
/// </summary>
/// <remarks>
/// While it's open the game gets no keyboard or mouse (<see cref="PanelInput"/>); pads still play. F1 or Esc closes it, and
/// the game gets them back once the key that closed it is let go (at most 2 s later), so the game never sees that press.
/// Nothing here throws into the game: the draw and the per-frame update are guarded, and an error closes the panel, gives
/// the keyboard and mouse back and is logged (after three, the panel is off for the session). Clicks are queued and run in
/// the next frame's update, like the keys, each guarded and logged ("Panel: ..."). The panel calls public APIs only
/// (<see cref="OnlineSession"/>, <see cref="CoopSlots"/>, <see cref="SaveBackups"/>, <see cref="ModSettings"/>); it never
/// touches a save itself. What it shows is read in the update (the session, friends' games and kept seats four times a
/// second, the backups every 3 s while their tab is open, the slots when theirs opens, and all after each action), never
/// while drawing, and its text is made there too: drawing allocates nothing.
/// </remarks>
internal sealed partial class ModPanel
{
    /// <summary>The longest the game waits for its keyboard and mouse after the panel closes (the closing key still down).</summary>
    private const long ReleaseWaitMs = 2000;

    /// <summary>Frames the panel may stay open without being drawn before it closes (the game's GUI isn't running).</summary>
    private const int UnseenFrames = 120;

    /// <summary>How often the session is read while the panel is open.</summary>
    private const long SessionEveryMs = 250;

    /// <summary>How often the co-op slots and backups are read while their tab is open.</summary>
    private const long ListsEveryMs = 3000;

    /// <summary>Errors after which the panel is off for the session.</summary>
    private const int MaxFailures = 3;

    /// <summary>Friends' games shown at most, so the rest of the tab stays on it.</summary>
    private const int MaxGames = 4;

    private static readonly string[] TabNames = { "Online", "Co-op slot", "Backups", "Settings", "Controller", "Help" };

    /// <summary>The logs folder as the panel and its log lines name it.</summary>
    private const string LogsLabel = @"UserData\T2TOnline\logs";

    /// <summary>Where the mod keeps things, for the Help tab (labels: never a full path on screen, where a screenshot may be posted).</summary>
    private static readonly string StorageLine =
        @"Logs, bug reports and settings: the game folder's UserData\T2TOnline. Save backups, a host's co-op runs and the shared " +
        $"progress ledger: {CampaignFiles.DataFolderLabel}.";

    /// <summary>What a bug report is, for the Help tab.</summary>
    private static readonly string ReportLine =
        "One zip of your logs to attach to a bug report, cleaned of Steam names, Steam IDs, folder names and local times. When something goes " +
        $"wrong, press {BugReports.MarkKeyName} (or Mark this moment) so the report shows where. With friends, each player makes one.";

    // The Help tab's words (made once: the tab is drawn every frame).
    private const string OfflineKeysLine =
        $"Online is off this session (the Online tab says why), so F7, F9, {OnlineSession.DropKey} and {OnlineSession.SoloKey} do nothing until it's back.";

    private const string IntroLine =
        "Playing together is Online on the title screen: Host Lobby, or Join Lobby. This panel is the back room: backups and restores, settings, the " +
        "controller check and bug reports. The keys below are fallbacks, and F1 itself.";

    private const string FocusLine =
        "The keys work only while the game window is focused (click it once if they do nothing). From Steam, Join Game on a " +
        "hosting friend's name, or accepting their invite, joins as F7 does (and starts the game if it isn't running).";

    private const string KeyboardLine =
        "Playing with the keyboard as a friend (Settings, Online input): your own key bindings go to the host; Space or Return joins, " +
        "Q+E accepts a brawl offer (LB+RB on a pad), and the keys stop while the game window isn't focused.";

    // The last row's words, short enough for its room (about 24 units of the smaller text) at any window size.
    private const string HelpMore = "More below: the wheel, arrow keys or Page Down.";
    private const string HelpEnd = "The end of Help: the wheel, arrow keys or Page Up.";

    /// <summary>The Help tab's blocks, at most (<see cref="HelpPart"/>).</summary>
    private const int HelpParts = (int)HelpPart.Controller + 1;

    private readonly IPanelSession _online;
    private readonly string _version;
    private readonly bool _devTools;
    private readonly string? _restoreOff;
    private readonly string _logFolder;
    private readonly PanelDraw _draw = new();
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);
    private bool _open;
    private bool _closing;
    private long _releaseBy;
    private int _unseenFrames;
    private bool _f1Up;
    private Tab _tab;
    private int _failures;
    private bool _offNoted;
    private (string What, Action Run)? _pending;
    private Confirmation? _confirm;
    private string _message = string.Empty;
    private Color _messageColour = PanelDraw.Muted;
    private string _header = string.Empty;
    private Color _headerColour = PanelDraw.Muted;
    private SessionText? _session;
    private Switches _switches = new(true, true, true, true, false, OnlineInputChoice.Auto, true);
    private long _sessionAt = long.MinValue;
    private IReadOnlyList<SlotRow> _slots = Array.Empty<SlotRow>();
    private int _selectedSlot;
    private IReadOnlyList<BackupRow>? _backups;
    private string _backupNotice = string.Empty;
    private string _backupNotes = string.Empty;
    private string _sessionsBlocked = string.Empty;
    private int _firstBackup;
    private (int First, int Rows, int Count) _scrollShown = (-1, -1, -1);
    private string _scrollText = string.Empty;
    private long _listsAt = long.MinValue;
    private IReadOnlyList<JoinableGame>? _gamesFrom;
    private IReadOnlyList<GameRow> _games = Array.Empty<GameRow>();
    private string _gamesText = string.Empty;
    private bool _findGames;
    private bool _canInvite;
    private IReadOnlyList<SeatRow> _seats = Array.Empty<SeatRow>();

    // The Help tab (DrawHelp): the blocks there this frame and their heights, in fixed buffers; the first one shown; what the
    // blocks read once per draw.
    private readonly HelpPart[] _helpParts = new HelpPart[HelpParts];
    private readonly float[] _helpHeights = new float[HelpParts];
    private int _helpFirst;
    private string _helpStatus = string.Empty;
    private bool _helpOffline;
    private bool _helpReportReady;

    /// <summary>Creates the panel and patches what it needs to hold the game's input (a failure is logged; the panel still works).</summary>
    /// <param name="online">The online session the panel shows (<see cref="OfflinePanelSession"/> while T2T Online is off).</param>
    /// <param name="version">The mod's version, for the header.</param>
    /// <param name="devTools">True when the developer keys are on, so the help lists them.</param>
    /// <param name="harmony">
    /// The mod's Harmony instance, to hold the game's UI events and mouse messages while the panel is open; null patches
    /// nothing (an untested game build: Rewired's keyboard and mouse and the cursor are still held).
    /// </param>
    /// <param name="restoreOff">Why a backup can't be restored this session (the Backups tab says so), or null when it can.</param>
    public ModPanel(IPanelSession online, string version, bool devTools, HarmonyLib.Harmony? harmony, string? restoreOff = null)
    {
        _online = online ?? throw new ArgumentNullException(nameof(online));
        _version = version;
        _devTools = devTools;
        _restoreOff = restoreOff;
        _padCheckOff = harmony == null
            ? "The controller check is off this session: this game build isn't one the mod has tested, and the check reads the game's input code."
            : null;
        _logFolder = Path.Combine(MelonEnvironment.UserDataDirectory, "T2TOnline", "logs");
        try
        {
            if (ModLog.FilePath is { } file && Path.GetDirectoryName(file) is { Length: > 0 } folder)
            {
                _logFolder = folder;
            }

            if (harmony == null)
            {
                ModLog.Info("Panel: ready (F1), without patching the game (T2T Online is off for this game build): while it's open the game gets no keyboard, " +
                    "but its menus under the panel may react to the panel's clicks.");
            }
            else
            {
                IReadOnlyList<string> failed = PanelInput.Install(harmony);
                ModLog.Info(failed.Count == 0
                    ? "Panel: ready (F1). While it's open the game gets no keyboard or mouse; pads still play."
                    : $"Panel: ready (F1), but {string.Join(" and ", failed)} can't be held while it's open (see above); the game's menus under it may react to its clicks.");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Panel: setting up failed; F1 still opens it, but the game's menus under it may react to its clicks", ex);
        }
    }

    /// <summary>The panel's tabs, in <see cref="TabNames"/> order.</summary>
    private enum Tab
    {
        Online,
        Slot,
        Backups,
        Settings,
        Controller,
        Help,
    }

    /// <summary>The Help tab's blocks, top to bottom: each is shown whole or not at all when the tab scrolls.</summary>
    private enum HelpPart
    {
        Reports,
        KeysHeading,
        Intro,
        KeyF1,
        KeyF7,
        KeyShiftF7,
        KeyF9,
        KeyDrop,
        KeySolo,
        KeyDev,
        KeyEvents,
        Focus,
        Keyboard,
        Controller,
    }

    /// <summary>True while a panel is open (one is drawn at a time: the runtime's, or the offline shell's): a finished bug report shows itself in Explorer only then.</summary>
    public static bool AnyOpen { get; private set; }

    /// <summary>Call once per frame (from the runtime's update) with whether F1 was pressed: opens the panel, runs what was clicked, and refreshes what it shows.</summary>
    public void Update(bool f1Pressed)
    {
        try
        {
            long now = Environment.TickCount64;
            if (_closing)
            {
                // The game gets the keyboard and mouse back once the key that closed the panel is up: given them while it's
                // down, it would see a new press (Esc could open the pause menu).
                if (now >= _releaseBy || !PanelInput.ClosingKeysDown())
                {
                    _closing = false;
                    PanelInput.Release();
                }

                return;
            }

            if (!_open)
            {
                if (f1Pressed)
                {
                    Open(now);
                }

                return;
            }

            if (++_unseenFrames > UnseenFrames)
            {
                // The game's GUI isn't drawing the panel (it can't be seen or used): never keep the game's input meanwhile.
                _open = AnyOpen = false;
                _pending = null;
                _confirm = null;
                PanelInput.Release();
                ModLog.Warn($"Panel: closed, as the game's GUI hasn't drawn it for {UnseenFrames} frames; the game has its keyboard and mouse back.");
                EndController("panel closed: not drawn");
                return;
            }

            if (!_f1Up && !PanelInput.ClosingKeysDown())
            {
                _f1Up = true;
            }

            if (_pending is { } pending)
            {
                _pending = null;
                Run(pending.What, pending.Run);
                _sessionAt = long.MinValue;
                _listsAt = long.MinValue;
            }

            Refresh(now);
            RefreshOwnMenu(now);
            UpdateController(now);
        }
        catch (Exception ex)
        {
            Fail("updating", ex);
        }
    }

    /// <summary>Call from OnGUI, after the overlays (so it's drawn over them). Draws the panel while it's open and takes the mouse and key events it gets.</summary>
    public void OnGUI()
    {
        if (!_open)
        {
            return;
        }

        Color colour = Color.white;
        Color content = Color.white;
        bool saved = false;
        try
        {
            Event current = Event.current;
            if (current == null)
            {
                return;
            }

            colour = GUI.color;
            content = GUI.contentColor;
            saved = true;
            EventType type = current.type;
            if (type == EventType.Repaint)
            {
                _unseenFrames = 0;
                PanelInput.KeepCursorFree();
            }

            if (current.isKey)
            {
                KeyCode key = current.keyCode;
                if (type == EventType.KeyUp && key == KeyCode.F1)
                {
                    _f1Up = true;
                }
                else if (type == EventType.KeyDown && (key == KeyCode.Escape || (key == KeyCode.F1 && _f1Up)))
                {
                    current.Use();
                    if (key == KeyCode.Escape && _confirm != null)
                    {
                        _confirm = null;
                        ModLog.Info("Panel: restore cancelled (Esc).");
                    }
                    else
                    {
                        Close(key == KeyCode.Escape ? "Esc" : "F1");
                    }

                    return;
                }
            }

            _draw.Begin(current);
            DrawPanel();

            // Whatever the panel didn't take itself is the panel's too: nothing else drawn with the GUI after it reacts.
            if (current.type is EventType.MouseDown or EventType.MouseUp or EventType.MouseDrag or EventType.ScrollWheel or EventType.KeyDown or EventType.KeyUp)
            {
                current.Use();
            }
        }
        catch (Exception ex)
        {
            Fail("drawing", ex);
        }
        finally
        {
            // A draw cut short by an error mustn't leave the GUI tinted for whatever is drawn next.
            if (saved)
            {
                RestoreColours(colour, content);
            }
        }
    }

    /// <summary>Closes the panel and gives the keyboard and mouse back at once (the game is closing, or the runtime stopped). Never throws.</summary>
    public void Shutdown()
    {
        EndController("the game is closing");
        _open = AnyOpen = false;
        _closing = false;
        _pending = null;
        _confirm = null;
        PanelInput.Release();
    }

    /// <summary>F1: opens the panel once the game's keyboard and mouse are held (not after <see cref="MaxFailures"/> errors).</summary>
    private void Open(long now)
    {
        if (_failures >= MaxFailures)
        {
            if (!_offNoted)
            {
                _offNoted = true;
                ModLog.Warn($"Panel (F1): off for this session after {MaxFailures} errors (see above).");
            }

            return;
        }

        if (!PanelInput.Hold())
        {
            return;
        }

        _open = AnyOpen = true;
        _f1Up = false;
        _unseenFrames = 0;
        _pending = null;
        _confirm = null;
        _message = string.Empty;
        _sessionAt = long.MinValue;
        _listsAt = long.MinValue;
        _findGames = true;
        _helpFirst = 0;
        Refresh(now);
        ModLog.Info("Panel: opened (F1).");
    }

    /// <summary>Closes the panel; the game gets its keyboard and mouse back in the update, once the closing key is up.</summary>
    private void Close(string how)
    {
        _open = AnyOpen = false;
        _pending = null;
        _confirm = null;
        _closing = true;
        _releaseBy = Environment.TickCount64 + ReleaseWaitMs;
        ModLog.Info($"Panel: closed ({how}).");
        EndController("panel closed");
    }

    /// <summary>An error in the panel: it closes, the game gets its keyboard and mouse back at once, and the error is logged.</summary>
    private void Fail(string what, Exception ex)
    {
        _failures++;
        _open = AnyOpen = false;
        _closing = false;
        _pending = null;
        _confirm = null;
        PanelInput.Release();
        ModLog.Error($"Panel: {what} failed; the panel is closed and the game has its keyboard and mouse back" +
            (_failures < MaxFailures ? " (F1 opens it again)" : $"; after {MaxFailures} errors it's off for this session"), ex);
        EndController("panel closed after an error");
    }

    /// <summary>Puts the GUI's tints back as they were before the panel drew; never throws.</summary>
    private static void RestoreColours(Color colour, Color content)
    {
        try
        {
            GUI.color = colour;
            GUI.contentColor = content;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Panel: the GUI's colours couldn't be put back ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Runs a click's action; a failure is logged and shown, and the panel stays open.</summary>
    private void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Error($"Panel: {what} failed", ex);
            Show($"{what} failed: {ModLog.SafeErrorText(ex)}", PanelDraw.Bad); // its kind and message, folders as labels: the panel shows no path
        }
    }

    /// <summary>A click's action, run in the next update.</summary>
    private void Queue(string what, Action action) => _pending = (what, action);

    /// <summary>Shows <paramref name="message"/> in the footer (the last action's result) until the next one.</summary>
    private void Show(string message, Color colour)
    {
        _message = message;
        _messageColour = colour;
    }

    /// <summary>
    /// Reads what the panel shows, and makes its text: the session, friends' games and kept seats, and the settings four
    /// times a second (friends' games are looked for when the Online tab is shown, and fill in as Steam answers); the
    /// backups every 3 s while their tab is open; the slots when their tab opens; and all of them after each action.
    /// </summary>
    private void Refresh(long now)
    {
        if (_sessionAt == long.MinValue || now - _sessionAt >= SessionEveryMs)
        {
            _sessionAt = now;
            SessionSummary? session = Read("the session", _online.Summarize, null);
            _session = session == null ? null : SessionText.Of(session);
            RefreshGames(session);
            _seats = session?.Phase is SessionPhase.Hosting or SessionPhase.OnePcTest
                ? Read("the dropped friends' seats", () => _online.ParkedSeats.Select(s => new SeatRow(s.SteamId, s.Name)).ToList(), new List<SeatRow>())
                : Array.Empty<SeatRow>();
            _canInvite = session?.Phase is SessionPhase.Hosting or SessionPhase.OnePcTest && Read("whether friends can be invited", () => _online.CanInvite, false);
            _switches = Read("the settings", () => new Switches(ModSettings.ShareAchievements, ModSettings.ShareProgress, ModSettings.FightGraceWindow,
                ModSettings.AiStandIn, _online.NetOverlayVisible, ModSettings.OnlineInput, ModSettings.NativeLobby), _switches);
            (string online, _headerColour) = session?.Phase switch
            {
                SessionPhase.Unavailable => ("online is off this session (the Online tab says why)", PanelDraw.Warn),
                SessionPhase.Broken => ("online is off after an error (the Online tab says what to do)", PanelDraw.Bad),
                _ when !T2TOnlinePlugin.OnlineAvailable => ("online is off this session (the Online tab says why)", PanelDraw.Warn),
                _ => ("online is available", PanelDraw.Muted),
            };
            _header = $"Version {ReleaseVersion.ForScreen(_version)} · {online}";
            _sessionsBlocked = Read("whether sessions may start", () => SaveBackups.BlocksSessions(out string why) ? why : string.Empty, string.Empty);
        }

        // The co-op slots (their run files are read) when their tab opens and after each action; the backups also every 3 s
        // while their tab is open.
        bool fresh = _listsAt == long.MinValue;
        if (fresh || (now - _listsAt >= ListsEveryMs && _tab == Tab.Backups))
        {
            _listsAt = now;
            if (fresh && _tab == Tab.Slot)
            {
                IReadOnlyList<CoopSlotInfo>? slots = Read("the co-op slots", CoopSlots.Describe, null);
                _selectedSlot = Read("the selected co-op slot", () => CoopSlots.Selected, 0);
                _slots = Enumerable.Range(1, CoopSlots.Count).Select(slot => SlotRow.Of(slot, slots)).ToList();
            }

            if (_tab == Tab.Backups)
            {
                _backups = Read("the backups", SaveBackups.List, null)?.Select(b => new BackupRow(b, When(b.TakenAt), Contents(b))).ToList();
                _backupNotice = Read("the backups' notice", () => SaveBackups.Notice, string.Empty);
                _backupNotes = Read("the last save check", () => SaveBackups.LastCheck.Length > 0
                    ? $"{SaveBackups.LastCheck} The backups are kept in {SaveBackups.FolderLabel}."
                    : $"The backups are kept in {SaveBackups.FolderLabel}.", string.Empty);
            }
        }
    }

    /// <summary>
    /// Friends' games for the Online tab: looked for once when the tab is shown outside a session (<see cref="FindGames"/>),
    /// and their rows made again whenever the session's list changes. Not while a mouse button is down: rows that changed
    /// between a press and its release could join another game.
    /// </summary>
    private void RefreshGames(SessionSummary? session)
    {
        if (_tab != Tab.Online || !_online.ChoosesGames || session == null || session.InLobby)
        {
            return;
        }

        if (_findGames && T2TOnlinePlugin.OnlineAvailable && _sessionsBlocked.Length == 0)
        {
            _findGames = false;
            FindGames();
        }

        if (PanelInput.ClosingKeysDown())
        {
            return;
        }

        IReadOnlyList<JoinableGame>? games = null;
        try
        {
            games = _online.JoinableGames;
        }
        catch (Exception ex)
        {
            if (_noted.Add("friends' games"))
            {
                ModLog.Error("Panel: reading friends' games failed (the panel says so where it would show them)", ex);
            }
        }

        if (games == null)
        {
            _games = Array.Empty<GameRow>();
            _gamesText = "Friends' games couldn't be read (see the log).";
            return;
        }

        if (ReferenceEquals(games, _gamesFrom))
        {
            return;
        }

        _gamesFrom = games;
        _games = games.Take(MaxGames).Select(GameRow.Of).ToList();
        _gamesText = games.Count == 0 ? "No friend's game found. Find games looks again once a friend is hosting."
            : games.Count > MaxGames ? $"{games.Count} friends' games are open; the first {MaxGames} are shown."
            : string.Empty;
    }

    /// <summary>Looks for friends' games (the list fills in as Steam answers); a failure is logged once.</summary>
    private void FindGames()
    {
        try
        {
            _online.FindGames();
        }
        catch (Exception ex)
        {
            if (_noted.Add("finding friends' games"))
            {
                ModLog.Error("Panel: finding friends' games failed", ex);
            }
        }
    }

    /// <summary>One thing the panel shows, read; a failure is logged once per thing and gives <paramref name="otherwise"/>.</summary>
    private T Read<T>(string what, Func<T> read, T otherwise)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            if (_noted.Add(what))
            {
                ModLog.Error($"Panel: reading {what} failed (the panel says so where it would show it)", ex);
            }

            return otherwise;
        }
    }

    /// <summary>
    /// The whole panel, centred on a dimmed screen: 44 by 40 units (about half the screen's width and three quarters of its
    /// height at 16:9, so the Settings tab's five switches fit), less on a small window. The header, the tabs and the
    /// footer (two lines for the last action's result), and the open tab's body between them.
    /// </summary>
    private void DrawPanel()
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float width = Math.Min(Screen.width - (2 * u), 44 * u);
        float height = Math.Min(Screen.height - (2 * u), 40 * u);
        float bar = Math.Max(2f, MathF.Round(u * 0.18f));
        var panel = new Rect((Screen.width - width) / 2, (Screen.height - height) / 2, width, height);
        d.Backdrop();
        d.Fill(panel, PanelDraw.Background);
        d.Fill(new Rect(panel.x, panel.y, width, bar), PanelDraw.Accent);
        float pad = 1.2f * u;
        float x = panel.x + pad;
        float inner = width - (2 * pad);
        float y = panel.y + (0.9f * u);

        // Header: the title, the version and whether online is on, and Close.
        d.Label(new Rect(x, y, inner - (7 * u), 1.9f * u), "T2T Online", d.Title, PanelDraw.Text);
        if (d.Button(new Rect(x + inner - (6 * u), y + (0.1f * u), 6 * u, 1.7f * u), "Close"))
        {
            Close("Close clicked");
            return;
        }

        y += 1.9f * u;
        d.Label(new Rect(x, y, inner, 1.3f * u), _header, d.Small, _headerColour);
        y += 2.0f * u;

        // Tabs.
        float tabWidth = inner / TabNames.Length;
        for (int i = 0; i < TabNames.Length; i++)
        {
            var tab = new Rect(x + (i * tabWidth), y, tabWidth, 2.2f * u);
            bool active = (int)_tab == i;
            if (d.Clickable(tab, enabled: !active))
            {
                if (_tab == Tab.Controller)
                {
                    EndController("tab closed");
                }

                _tab = (Tab)i;
                _helpFirst = 0;
                _confirm = null;
                _listsAt = long.MinValue;
                _sessionAt = long.MinValue;
                _findGames = _tab == Tab.Online;
            }

            d.Label(tab, TabNames[i], d.Center, active ? PanelDraw.Text : PanelDraw.Muted);
            if (active)
            {
                d.Fill(new Rect(tab.x + (0.6f * u), y + (2.2f * u) - bar, tabWidth - (1.2f * u), bar), PanelDraw.Accent);
            }
        }

        y += 2.2f * u;
        d.Line(x, y, inner);
        y += 0.9f * u;

        // Footer: the last action's result (wrapped: a refusal's reason is often longer than a line), and how to close.
        float footer = panel.y + height - (3.2f * u);
        d.Line(x, footer, inner);
        d.Label(new Rect(x, footer + (0.4f * u), inner * 0.74f, 2.6f * u), _message, d.Wrap, _messageColour);
        d.Label(new Rect(x + (inner * 0.74f), footer + (0.4f * u), inner * 0.26f, 1.3f * u), "F1 or Esc closes", d.SmallRight, PanelDraw.Dim);

        var body = new Rect(x, y, inner, footer - (0.6f * u) - y);
        switch (_tab)
        {
            case Tab.Online:
                DrawOnline(body);
                break;
            case Tab.Slot:
                DrawSlots(body);
                break;
            case Tab.Backups:
                DrawBackups(body);
                break;
            case Tab.Settings:
                DrawSettings(body);
                break;
            case Tab.Controller:
                DrawController(body);
                break;
            default:
                DrawHelp(body);
                break;
        }
    }

    /// <summary>
    /// The Online tab: the session's state; outside a session, Host a game, Find games and friends' games each with Join
    /// (off, with why, while a restored backup waits for a restart); in one, Leave; while hosting, the seats kept for
    /// friends who dropped, each with Free this seat; and the players with their ping.
    /// </summary>
    private void DrawOnline(Rect body)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float x = body.x;
        float w = body.width;
        float y = body.y;
        Heading("Online play", x, ref y, w);
        SessionText? session = _session;
        if (session == null)
        {
            d.Label(new Rect(x, y, w, 1.6f * u), "The session couldn't be read (see the log).", d.Body, PanelDraw.Bad);
            return;
        }

        d.Label(new Rect(x, y, w, 1.7f * u), session.State, d.Bold, session.StateColour);
        y += 1.8f * u;
        if (session.Detail.Length > 0)
        {
            Paragraph(session.Detail, x, ref y, w, PanelDraw.Muted);
        }

        if (!_online.ChoosesGames)
        {
            return; // online is off: the state and its detail say why and what to do
        }

        y += 0.8f * u;
        SessionPhase phase = session.Summary.Phase;
        bool inLobby = session.Summary.InLobby;
        // A session can't start while a restored backup waits for the game to restart (SaveBackups.BlocksSessions).
        bool usable = phase is not (SessionPhase.Unavailable or SessionPhase.Broken or SessionPhase.WaitingForSteam or SessionPhase.NoSteam)
            && _sessionsBlocked.Length == 0;

        // The fixed buttons first, the lists after them: a list that changes length never moves these buttons' control ids.
        if (d.Button(new Rect(x, y, 10 * u, 2 * u), "Host a game", usable && !inLobby))
        {
            Queue("Host a game", () => StartSession(null));
        }

        if (d.Button(new Rect(x + (10.6f * u), y, 9 * u, 2 * u), "Find games", usable && !inLobby))
        {
            Queue("Finding friends' games", FindGames);
        }

        if (d.Button(new Rect(x + (20.2f * u), y, 7 * u, 2 * u), "Leave", inLobby))
        {
            Queue("Leave", Leave);
        }

        // Steam's own invite window for the hosted lobby: a friend who accepts joins as with F7 (their game starts for it).
        if (d.Button(new Rect(x + (27.8f * u), y, 10 * u, 2 * u), "Invite friends", _canInvite))
        {
            Queue("Inviting friends", Invite);
        }

        y += 2.6f * u;
        if (!inLobby && _sessionsBlocked.Length > 0)
        {
            Paragraph(_sessionsBlocked, x, ref y, w, PanelDraw.Bad);
            y += 0.6f * u;
        }

        if (!inLobby)
        {
            DrawGames(usable, x, ref y, w);
            Paragraph("Playing together is Online on the title screen: Host Lobby, or Join Lobby for a friend who's hosting. These buttons do the same, " +
                "and stay as a fallback (so do F7, which joins a friend who's hosting or else hosts, and Shift+F7, which hosts). From Steam: Join Game on a " +
                $"hosting friend's name, or accept their invite (the game starts for it). The one-PC test ({OnlineSession.SoloKey}) and the drop test ({OnlineSession.DropKey}) stay on the keyboard.",
                x, ref y, w, PanelDraw.Muted);
        }

        if (_seats.Count > 0)
        {
            DrawSeats(x, ref y, w);
        }

        if (session.Players.Count == 0)
        {
            return;
        }

        y += 1.0f * u;
        float seat = x + (14 * u);
        float ping = x + w - (6 * u);
        d.Label(new Rect(x + (0.5f * u), y, 13 * u, 1.4f * u), "Player", d.Small, PanelDraw.Muted);
        d.Label(new Rect(seat, y, ping - seat, 1.4f * u), "Seat", d.Small, PanelDraw.Muted);
        d.Label(new Rect(ping, y, 5.5f * u, 1.4f * u), "Ping", d.SmallRight, PanelDraw.Muted);
        y += 1.5f * u;
        float bottom = body.y + body.height;
        for (int i = 0; i < session.Players.Count && y + (1.8f * u) <= bottom; i++)
        {
            PlayerRow player = session.Players[i];
            if (i % 2 == 0)
            {
                d.Fill(new Rect(x, y, w, 1.8f * u), PanelDraw.RowShade);
            }

            d.Label(new Rect(x + (0.5f * u), y, 13 * u, 1.8f * u), player.Name, d.Bold, PanelDraw.Text);
            d.Label(new Rect(seat, y, ping - seat - u, 1.8f * u), player.Seat, d.Body, player.SeatColour);
            d.Label(new Rect(ping, y, 5.5f * u, 1.8f * u), player.Ping, d.Right, PanelDraw.Text);
            y += 1.8f * u;
        }
    }

    /// <summary>Friends' games, each with who hosts it, its version and players (or why it can't be joined) and Join.</summary>
    private void DrawGames(bool usable, float x, ref float y, float w)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        foreach (GameRow game in _games)
        {
            d.Fill(new Rect(x, y, w, 2.6f * u), PanelDraw.RowShade);
            d.Label(new Rect(x + (0.5f * u), y + (0.1f * u), w - (9 * u), 1.4f * u), game.HostName, d.Bold, PanelDraw.Text);
            d.Label(new Rect(x + (0.5f * u), y + (1.3f * u), w - (9 * u), 1.2f * u), game.Details, d.Small, game.DetailsColour);
            if (d.Button(new Rect(x + w - (7.5f * u), y + (0.3f * u), 7.3f * u, 2 * u), "Join", usable && game.Joinable, primary: true))
            {
                GameRow chosen = game;
                Queue("Joining a friend's game", () => StartSession(chosen)); // Run logs these words should it fail: no name in them
            }

            y += 2.8f * u;
        }

        if (_gamesText.Length > 0)
        {
            Paragraph(_gamesText, x, ref y, w, PanelDraw.Muted);
        }

        y += 0.4f * u;
    }

    /// <summary>Hosting: the seats kept for friends who dropped, each with Free this seat.</summary>
    private void DrawSeats(float x, ref float y, float w)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        y += 0.4f * u;
        Paragraph("A dropped friend's seat is kept (30 s in the lobby, 2 minutes in a game) so they can rejoin as the same player; free it if they're not coming back.",
            x, ref y, w, PanelDraw.Muted);
        foreach (SeatRow seat in _seats)
        {
            d.Label(new Rect(x + (0.5f * u), y, w - (12 * u), 2 * u), seat.Name, d.Bold, PanelDraw.Text);
            if (d.Button(new Rect(x + w - (11 * u), y, 10.8f * u, 2 * u), "Free this seat"))
            {
                SeatRow chosen = seat;
                Queue("Freeing a kept seat", () => FreeSeat(chosen)); // Run logs these words should it fail: no name in them
            }

            y += 2.3f * u;
        }
    }

    /// <summary>The session's state as a line, its colour, and a line more (while online is off: why, and what to do).</summary>
    private static (string State, Color Colour, string Detail) Describe(SessionSummary session) => session.Phase switch
    {
        SessionPhase.Unavailable => ("Online is off this session.", PanelDraw.Warn, OffDetail("The log says why.")),
        SessionPhase.Broken => ("Online is off after an error.", PanelDraw.Bad, OffDetail("Restart the game to play online; the log has the details.")),
        SessionPhase.WaitingForSteam => ("Waiting for Steam to start...", PanelDraw.Muted, "Try again in a few seconds."),
        SessionPhase.NoSteam => ("Steam isn't available, so online play is off.", PanelDraw.Warn, string.Empty),
        SessionPhase.Creating => ("Creating a lobby...", PanelDraw.Text, string.Empty),
        SessionPhase.Joining => (session.HostName.Length > 0 ? $"Joining {session.HostName}'s game..." : "Joining a friend's game...", PanelDraw.Text, string.Empty),
        SessionPhase.Hosting => (session.Friends == 0 ? "Hosting, waiting for friends." : $"Hosting with {session.Friends} friend{(session.Friends == 1 ? string.Empty : "s")}.",
            PanelDraw.Good, session.Friends == 0
                ? "Friends join from Online > Join Lobby on their title screen (or this panel's Online tab, or F7), with Join Game on your name in Steam's friends list, or from your invite (Invite friends)."
                : string.Empty),
        SessionPhase.OnePcTest => ("Hosting, with the one-PC test running.", PanelDraw.Good, $"{OnlineSession.SoloKey} stops the test."),
        SessionPhase.Joined => session.PlayOn
            ? ($"Joined {session.HostName}'s game.", PanelDraw.Good, "You play on their game.")
            : ($"In {session.HostName}'s lobby.", PanelDraw.Good, "Your game, device and saves stay yours until they start."),
        SessionPhase.Rejoining => ($"Lost {session.HostName}; rejoining...", PanelDraw.Warn, "You come back as the same player by yourself."),
        _ => ("Not connected.", PanelDraw.Muted, string.Empty),
    };

    /// <summary>
    /// While online is off, why and what to do (<see cref="T2TOnlinePlugin.OfflineReason"/>); <paramref name="otherwise"/>
    /// when no reason was recorded.
    /// </summary>
    private static string OffDetail(string otherwise)
    {
        string reason = T2TOnlinePlugin.OfflineReason;
        string advice = T2TOnlinePlugin.OfflineAdvice;
        return reason.Length == 0 ? otherwise : advice.Length == 0 ? $"Why: {reason}." : $"Why: {reason}. {advice}";
    }

    /// <summary>Leave: what F7 does in a session, when the session is still on.</summary>
    private void Leave()
    {
        if (!_online.Summarize().InLobby)
        {
            ModLog.Info("Panel: Leave clicked, but the session had ended meanwhile; nothing done.");
            return;
        }

        ModLog.Info("Panel: Leave clicked (as F7).");
        _online.Toggle();
        Show(_online.Summarize().InLobby ? "Still in the session; the log says why." : "Left the online session.",
            _online.Summarize().InLobby ? PanelDraw.Warn : PanelDraw.Muted);
    }

    /// <summary>
    /// Host a game (<paramref name="game"/> null), or join <paramref name="game"/>: only when not in a session already (it
    /// may have changed since the click) and no restored backup waits for a restart.
    /// </summary>
    private void StartSession(GameRow? game)
    {
        string what = game == null ? "Host a game" : $"Join for the game in lobby {ModLog.Scrub.Tags.Lobby(game.LobbyId)}"; // for the log: the host by their lobby's tag
        if (_online.Summarize().InLobby)
        {
            ModLog.Info($"Panel: {what} clicked, but a session started meanwhile; nothing done.");
            return;
        }

        if (SaveBackups.BlocksSessions(out string why))
        {
            ModLog.Info($"Panel: {what} clicked, but no session may start: {why}");
            Show(why, PanelDraw.Warn);
            return;
        }

        if (game != null && RunSync.BlocksJoining(out string ownRun))
        {
            // This player's own campaign run is in play: a session would replace it.
            ModLog.Info($"Panel: {what} clicked, but not now: {ownRun}");
            Show(ownRun, PanelDraw.Warn);
            return;
        }

        ModLog.Info(game == null ? "Panel: Host a game clicked (as Shift+F7)." : $"Panel: Join clicked for the game in lobby {ModLog.Scrub.Tags.Lobby(game.LobbyId)}.");
        if (game == null)
        {
            _online.HostGame();
        }
        else
        {
            _online.JoinGame(game.LobbyId);
        }

        if (_online.Summarize().InLobby)
        {
            Show(game == null ? "Hosting: friends can join now." : $"Joining {game.HostName}'s game.", PanelDraw.Muted);
        }
        else
        {
            Show("Nothing started; the log says why.", PanelDraw.Warn);
        }
    }

    /// <summary>Invite friends: Steam's invite window for the hosted lobby, or why it can't open (and what friends can do instead).</summary>
    private void Invite()
    {
        ModLog.Info("Panel: Invite friends clicked.");
        (bool opened, string message) = _online.InviteFriends();
        Show(message, opened ? PanelDraw.Good : PanelDraw.Warn);
    }

    /// <summary>Frees a dropped friend's kept seat, or says it's no longer kept.</summary>
    private void FreeSeat(SeatRow seat)
    {
        if (_online.FreeSeat(seat.SteamId))
        {
            ModLog.Info($"Panel: {ModLog.Scrub.Tags.Player(seat.SteamId)}'s seat freed.");
            Show($"{seat.Name}'s seat is free: their player goes now, or when the match in play is over.", PanelDraw.Good);
        }
        else
        {
            ModLog.Info($"Panel: Free this seat clicked for {ModLog.Scrub.Tags.Player(seat.SteamId)}, but their seat isn't kept any more.");
            Show($"{seat.Name}'s seat isn't kept any more (they're back, or their time ran out).", PanelDraw.Muted);
        }
    }

    /// <summary>The Co-op slot tab: the three slots (each one's run, or empty), the selected one marked; clicking another selects it.</summary>
    private void DrawSlots(Rect body)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float x = body.x;
        float w = body.width;
        float y = body.y;
        Heading("Co-op save slot", x, ref y, w);
        d.Label(new Rect(x, y, w, 1.6f * u), "Co-op runs are kept here, apart from your own run.", d.Body, PanelDraw.Text);
        y += 2.2f * u;
        foreach (SlotRow slot in _slots)
        {
            var row = new Rect(x, y, w, 2.8f * u);
            bool selected = slot.Slot == _selectedSlot;
            d.Fill(row, selected ? PanelDraw.Selected : PanelDraw.RowShade);
            if (d.Clickable(row, enabled: !selected))
            {
                int chosen = slot.Slot;
                Queue($"Selecting co-op slot {chosen}", () => SelectSlot(chosen));
            }

            d.Marker(new Rect(x + (0.8f * u), y + (1.0f * u), 0.8f * u, 0.8f * u), selected);
            d.Label(new Rect(x + (2.2f * u), y, 5.6f * u, row.height), slot.Label, d.Bold, PanelDraw.Text);
            d.Label(new Rect(x + (8 * u), y, w - (16 * u), row.height), slot.Details, d.Body, slot.HasRun ? PanelDraw.Text : PanelDraw.Muted);
            if (selected)
            {
                d.Label(new Rect(x + w - (7.6f * u), y, 6.8f * u, row.height), "Selected", d.Right, PanelDraw.Accent);
            }

            y += 3.1f * u;
        }

        y += 0.5f * u;
        Paragraph("Used when you host a campaign. Pick it here at the title screen, or on the lobby's Co-op Save row.", x, ref y, w, PanelDraw.Muted);
    }

    /// <summary>Makes <paramref name="slot"/> the co-op slot, or says why not (e.g. not at the title screen).</summary>
    private void SelectSlot(int slot)
    {
        if (CoopSlots.TrySelect(slot, out string problem))
        {
            ModLog.Info($"Panel: co-op slot {slot} selected.");
            Show($"Co-op runs now go in slot {slot}.", PanelDraw.Good);
        }
        else
        {
            ModLog.Info($"Panel: co-op slot {slot} not selected: {problem}.");
            Show($"Slot {slot} wasn't selected: {problem}.", PanelDraw.Warn);
        }
    }

    /// <summary>
    /// The Backups tab: Back up now, and the backups newest first, each with Restore (which asks first). When they don't
    /// all fit, the list scrolls (the mouse wheel over it, or Newer and Older), so the oldest, kept on purpose (the saves
    /// from before the first session), can always be reached.
    /// </summary>
    private void DrawBackups(Rect body)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float x = body.x;
        float w = body.width;
        float y = body.y;
        if (_confirm == null && d.Button(new Rect(x + w - (9 * u), y, 9 * u, 1.8f * u), "Back up now", primary: true))
        {
            Queue("Backing up", TakeBackup);
        }

        Heading("Backups", x, ref y, w - (10 * u));
        d.Label(new Rect(x, y, w, 1.6f * u), "Your saves and co-op slots are backed up here before every online session.", d.Body, PanelDraw.Text);
        y += 1.8f * u;
        if (_backupNotice.Length > 0)
        {
            Paragraph(_backupNotice, x, ref y, w, PanelDraw.Warn);
        }

        Paragraph(_backupNotes, x, ref y, w, PanelDraw.Muted);
        y += 0.5f * u;
        if (SaveBackups.RestartNeeded)
        {
            Paragraph(SaveBackups.RestartNotice, x, ref y, w, PanelDraw.Bad);
            return;
        }

        if (_confirm is { } chosen)
        {
            DrawConfirm(chosen, x, y, w);
            return;
        }

        IReadOnlyList<BackupRow>? backups = _backups;
        if (backups == null || backups.Count == 0)
        {
            Paragraph(backups == null ? "The backups couldn't be read (see the log)." : "No backups yet. One is taken before every online session, or press Back up now.",
                x, ref y, w, backups == null ? PanelDraw.Bad : PanelDraw.Muted);
            return;
        }

        if (_restoreOff != null)
        {
            Paragraph(_restoreOff, x, ref y, w, PanelDraw.Warn);
            y += 0.4f * u;
        }

        float bottom = body.y + body.height;
        float rowHeight = 2.0f * u;
        float rowStep = rowHeight + (0.1f * u);
        int fit = Math.Max(1, (int)((bottom - y) / rowStep));
        bool scrolls = backups.Count > fit;
        int rows = scrolls ? Math.Max(1, fit - 1) : backups.Count; // when it scrolls, the last row's room holds Newer and Older
        int first = Math.Clamp(_firstBackup, 0, backups.Count - rows);
        int wheel = scrolls ? d.Scrolled(new Rect(x, y, w, rows * rowStep)) : 0;
        if (wheel != 0)
        {
            _firstBackup = Math.Clamp(first + wheel, 0, backups.Count - rows);
        }

        for (int i = 0; i < rows; i++)
        {
            BackupRow backup = backups[first + i];
            var row = new Rect(x, y, w, rowHeight);
            if ((first + i) % 2 == 0)
            {
                d.Fill(row, PanelDraw.RowShade);
            }

            d.Label(new Rect(x + (0.5f * u), y, 9.5f * u, rowHeight), backup.When, d.Bold, PanelDraw.Text);
            d.Label(new Rect(x + (10.5f * u), y, w - (25 * u), rowHeight), backup.Info.Reason, d.Body, PanelDraw.Muted);
            d.Label(new Rect(x + w - (14 * u), y, 5.5f * u, rowHeight), backup.Files, d.Right, PanelDraw.Muted);
            if (d.Button(new Rect(x + w - (7.5f * u), y + (0.2f * u), 7.3f * u, rowHeight - (0.4f * u)), "Restore", _restoreOff == null))
            {
                BackupRow asked = backup;
                Queue("Reading a backup", () => AskRestore(asked)); // Run logs these words should it fail: no local time in them
            }

            y += rowStep;
        }

        if (!scrolls)
        {
            return;
        }

        if (_scrollShown != (first, rows, backups.Count))
        {
            _scrollShown = (first, rows, backups.Count);
            _scrollText = $"Backups {first + 1} to {first + rows} of {backups.Count}, newest first. The wheel scrolls.";
        }

        d.Label(new Rect(x + (0.5f * u), y + (0.2f * u), w - (17 * u), 1.6f * u), _scrollText, d.Small, PanelDraw.Muted);
        if (d.Button(new Rect(x + w - (15.6f * u), y + (0.1f * u), 7.6f * u, rowHeight - (0.2f * u)), "Newer", first > 0))
        {
            _firstBackup = Math.Max(0, first - rows);
        }

        if (d.Button(new Rect(x + w - (7.5f * u), y + (0.1f * u), 7.3f * u, rowHeight - (0.2f * u)), "Older", first + rows < backups.Count))
        {
            _firstBackup = Math.Min(backups.Count - rows, first + rows);
        }
    }

    /// <summary>
    /// The confirmation a restore needs, in place of the list: what to restore (a backup with co-op slots lists your saves,
    /// on, and each slot with its run, off; a click switches one), then Restore runs it, Cancel (or Esc) goes back.
    /// </summary>
    private void DrawConfirm(Confirmation confirm, float x, float y, float w)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        bool choose = confirm.Choices.Count > 1;
        float height = (choose ? 10.4f + (confirm.Choices.Count * 2.0f) : 9.9f) * u;
        float textX = x + (1.2f * u);
        float textW = w - (2.4f * u);
        d.Fill(new Rect(x, y, w, height), PanelDraw.Selected);
        d.Fill(new Rect(x, y, Math.Max(2f, MathF.Round(u * 0.25f)), height), PanelDraw.Warn);
        float line = y + (0.6f * u);
        d.Label(new Rect(textX, line, textW, 1.7f * u), confirm.Question, d.Bold, PanelDraw.Text);
        line += 1.7f * u;
        d.Label(new Rect(textX, line, textW, 1.5f * u), choose ? "What's there now is backed up first. Restore:" : "Your current saves are backed up first.", d.Body, PanelDraw.Text);
        line += 1.6f * u;
        if (choose)
        {
            for (int i = 0; i < confirm.Choices.Count; i++)
            {
                RestoreChoice choice = confirm.Choices[i];
                bool on = confirm.IsOn(choice);
                var row = new Rect(textX, line, textW, 1.8f * u);
                if (d.Clickable(row))
                {
                    _confirm = confirm.Switched(choice);
                }

                d.Switch(new Rect(textX, line + (0.25f * u), 2.6f * u, 1.3f * u), on);
                d.Label(new Rect(textX + (3.4f * u), line, 9 * u, 1.8f * u), choice.Label, d.Bold, PanelDraw.Text);
                d.Label(new Rect(textX + (12.6f * u), line, textW - (12.6f * u), 1.8f * u), choice.Details, d.Body, on ? PanelDraw.Text : PanelDraw.Muted);
                line += 2.0f * u;
            }

            line += 0.2f * u;
        }

        string note = confirm.Saves ? SaveBackups.RestoreConfirmNote : confirm.Slots.Count > 0 ? SaveBackups.SlotsOnlyNote : "Choose what to restore.";
        d.Label(new Rect(textX, line, textW, 1.5f * u), note, d.Body, PanelDraw.Text);
        line += 1.5f * u;
        d.Label(new Rect(textX, line, textW, 1.4f * u), confirm.Details, d.Small, PanelDraw.Muted);
        line += 1.8f * u;
        if (d.Button(new Rect(textX, line, 8 * u, 2 * u), "Restore", confirm.Saves || confirm.Slots.Count > 0, primary: true))
        {
            _confirm = null;
            BackupRow backup = confirm.Backup;
            var parts = new RestoreParts(confirm.Saves, confirm.Slots);
            Queue("Restoring from the backup", () => Restore(backup, parts)); // Run logs these words should it fail: no local time in them
        }

        if (d.Button(new Rect(textX + (8.6f * u), line, 7 * u, 2 * u), "Cancel"))
        {
            _confirm = null;
            ModLog.Info("Panel: restore cancelled.");
        }
    }

    /// <summary>
    /// Restore clicked on <paramref name="row"/>: reads what the backup holds (each co-op slot's run, for the checklist) and
    /// shows the confirmation; only your saves are chosen at first (restoring them never rolls a co-op run back).
    /// </summary>
    private void AskRestore(BackupRow row)
    {
        BackupContents? contents = SaveBackups.Describe(row.Info.Name);
        var choices = new List<RestoreChoice>();
        if (row.Info.Files > 0)
        {
            choices.Add(new RestoreChoice(0, "Your saves", $"{FileCount(row.Info.Files)} from the game's save folder (profile, stats, settings, solo run)"));
        }

        foreach ((int slot, string run) in contents?.Slots ?? Array.Empty<(int, string)>())
        {
            choices.Add(new RestoreChoice(slot, $"Co-op slot {slot}", run));
        }

        if (choices.Count == 0)
        {
            ModLog.Info($"Panel: restore of {SaveBackupNames.ForLog(row.Info.Name)} asked, but it holds nothing that can be restored.");
            Show($"The backup from {row.When} holds nothing that can be restored.", PanelDraw.Warn);
            return;
        }

        bool saves = row.Info.Files > 0;
        IReadOnlyList<int> slots = saves ? Array.Empty<int>() : new[] { choices[0].Slot };
        _confirm = new Confirmation(row, $"Restore from the backup from {row.When}?", $"{row.Info.Reason} · {row.Files}", choices, saves, slots);
        ModLog.Info($"Panel: restore of the backup {SaveBackupNames.ForLog(row.Info.Name)} asked; waiting for the confirmation" +
            (choices.Count > 1 ? $" (it holds {string.Join(", ", choices.Select(c => c.Label.ToLowerInvariant()))})." : "."));
    }

    /// <summary>
    /// Back up now: a new backup, or the newest one again when the saves haven't changed since it was taken. While following a host the
    /// shared-progress ledger changes with every batch and its newest dated copy is the session's start: it gets a copy as it is first
    /// (<see cref="Campaign.Progress.ProgressLedgerFile.KeepCopy"/>), so a restore of this backup brings back the ledger its saves go with.
    /// </summary>
    private void TakeBackup()
    {
        Campaign.Progress.ProgressLedgerFile.KeepCopy();
        if (SaveBackups.TryTake("taken from the mod's panel", out BackupTaken taken, out string problem))
        {
            if (taken.Reused)
            {
                string when = _backups?.FirstOrDefault(b => string.Equals(b.Info.Name, taken.Name, StringComparison.OrdinalIgnoreCase))?.When
                    ?? (SaveBackupNames.TryParse(taken.Name, out DateTime at, out _) ? When(at) : taken.Name);
                ModLog.Info($"Panel: no new backup needed: the saves are the same as in {SaveBackupNames.ForLog(taken.Name)}.");
                Show($"Your saves are the same as the backup from {when}, so that one is their backup.", PanelDraw.Good);
            }
            else
            {
                ModLog.Info($"Panel: backup taken ({SaveBackupNames.ForLog(taken.Name)}).");
                Show("Backup taken.", PanelDraw.Good);
            }
        }
        else
        {
            ModLog.Warn($"Panel: no backup taken: {problem}.");
            Show($"No backup was taken: {problem}.", PanelDraw.Warn);
        }
    }

    /// <summary>
    /// Restores <paramref name="parts"/> of <paramref name="row"/>'s backup. <see cref="SaveBackups.TryRestore(string, RestoreParts, out string)"/> checks
    /// everything first, backs this PC's saves and co-op slots up, then restores (never pruning the backup being restored);
    /// after the saves the game must restart, and nothing is saved until it does (a co-op slot alone needs no restart). A
    /// restore that wrote some files but not all fails too, and the next start finishes it: the footer says so rather than
    /// "nothing was changed".
    /// </summary>
    private void Restore(BackupRow row, RestoreParts parts)
    {
        BackupInfo backup = row.Info;
        string logName = SaveBackupNames.ForLog(backup.Name); // an old backup's name is a local time
        if (_restoreOff != null)
        {
            ModLog.Info($"Panel: restore of {logName} refused: {_restoreOff}");
            Show(_restoreOff, PanelDraw.Warn);
            return;
        }

        if (_online.Summarize().InLobby)
        {
            ModLog.Info($"Panel: restore of {logName} refused: an online session is on.");
            Show("Leave the online session first (Online tab, or F7), then restore.", PanelDraw.Warn);
            return;
        }

        bool unfinishedBefore = SaveBackups.RestoreUnfinished;
        string what = parts.Describe();
        if (SaveBackups.TryRestore(backup.Name, parts, out string problem))
        {
            ModLog.Info($"Panel: restored {what} from the backup {logName} (what was there just before is the newest backup)" +
                (parts.NeedsRestart ? "; the game must restart." : "."));
            Show(parts.NeedsRestart ? $"Restored {what} from the backup from {row.When}. {SaveBackups.RestartNotice}" : $"Restored {what} from the backup from {row.When}.",
                parts.NeedsRestart ? PanelDraw.Warn : PanelDraw.Good);
        }
        else if (!unfinishedBefore && SaveBackups.RestoreUnfinished)
        {
            ModLog.Warn($"Panel: {what} from the backup {logName} was only partly restored: {problem}.");
            Show($"{what} from the backup from {row.When} was only partly restored: {problem}.", PanelDraw.Bad);
        }
        else
        {
            ModLog.Warn($"Panel: {what} from the backup {logName} wasn't restored: {problem}.");
            Show($"{what} from the backup from {row.When} wasn't restored: {problem}. Nothing was changed.", PanelDraw.Warn);
        }
    }

    /// <summary>The Settings tab: a switch per setting, each with a line on what it does.</summary>
    private void DrawSettings(Rect body)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float x = body.x;
        float w = body.width;
        float y = body.y;
        Heading("Settings", x, ref y, w);
        DrawOwnMenuRow(x, ref y, w); // in a friend's game: your own game's settings and remapper (ModPanel.OwnMenu.cs)
        Switches on = _switches;
        Setting(nameof(ModSettings.ShareAchievements), "Share achievements", on.ShareAchievements,
            "Achievements earned together unlock on every player's own Steam account. Off: you neither send nor receive them.", x, ref y, w);
        Setting(nameof(ModSettings.ShareProgress), "Share progress", on.ShareProgress,
            "A co-op campaign run counts on every player's own profile: stats, unlocks, bench quests, squad wins and run history. " +
            "Off: you send none, and what comes in waits until you turn it back on in the same online game.", x, ref y, w);
        Setting(nameof(ModSettings.FightGraceWindow), "Fight grace window", on.FightGraceWindow,
            "When you host, an AI punch at a friend waits up to their lag (at most 100 ms) for their block.", x, ref y, w);
        Setting(nameof(ModSettings.AiStandIn), "AI stand-in", on.AiStandIn,
            "When you host, the AI plays a friend's skater after they've been away 3 s, until they're back.", x, ref y, w);
        if (ModSettings.ShowStatusBox)
        {
            Setting(null, "Net overlay", on.NetOverlay,
                "Live connection numbers in the top-right corner (F9 does the same). A developer's tool, with the status box. For this session only.", x, ref y, w);
        }

        CompactSetting(nameof(ModSettings.NativeLobby), "Native lobby", on.NativeLobby, "The Online row on the title screen.", x, ref y, w);
        InputChoice(on.Input, x, ref y, w);
        y += 0.4f * u;
        Paragraph("Saved at once in UserData/T2TOnline/settings.cfg, and used from now on.", x, ref y, w, PanelDraw.Muted);
    }

    /// <summary>One switch row: clicking anywhere on it flips the setting (<paramref name="name"/>; null: the net overlay).</summary>
    private void Setting(string? name, string title, bool on, string explanation, float x, ref float y, float w)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float textX = x + (4 * u);
        float textW = w - (4 * u);
        float explained = d.Height(explanation, d.Wrap, textW);
        var row = new Rect(x, y, w, (1.6f * u) + explained + (0.3f * u));
        if (d.Clickable(row))
        {
            Queue($"Changing {title}", name == null ? ToggleNetOverlay : () => SetSwitch(name, title, !on));
        }

        d.Switch(new Rect(x + (0.4f * u), y + (0.25f * u), 2.6f * u, 1.3f * u), on);
        d.Label(new Rect(textX, y, textW, 1.6f * u), title, d.Bold, PanelDraw.Text);
        d.Label(new Rect(textX, y + (1.6f * u), textW, explained), explanation, d.Wrap, PanelDraw.Muted);
        y += row.height + (0.5f * u);
    }

    /// <summary>A switch on one line, its explanation beside its title: the tab has no room for another two-line row.</summary>
    private void CompactSetting(string name, string title, bool on, string explanation, float x, ref float y, float w)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float textX = x + (4 * u);
        float titleW = (title.Length * 0.6f * u) + (0.8f * u);
        var row = new Rect(x, y, w, 1.6f * u);
        if (d.Clickable(row))
        {
            Queue($"Changing {title}", () => SetSwitch(name, title, !on));
        }

        d.Switch(new Rect(x + (0.4f * u), y + (0.15f * u), 2.6f * u, 1.3f * u), on);
        d.Label(new Rect(textX, y, titleW, 1.6f * u), title, d.Bold, PanelDraw.Text);
        d.Label(new Rect(textX + titleW, y, w - (4 * u) - titleW, 1.6f * u), explanation, d.Wrap, PanelDraw.Muted);
        y += row.height + (0.5f * u);
    }

    /// <summary>
    /// The OnlineInput row: what you play with when you join a friend (Auto, Gamepad, Keyboard and mouse), the one in use
    /// marked; a click saves it, for your next join.
    /// </summary>
    private void InputChoice(OnlineInputChoice current, float x, ref float y, float w)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float textX = x + (4 * u);
        float textW = w - (4 * u);
        const string explanation = "What you play with in a friend's game: Auto takes a gamepad if one is connected, else the keyboard and mouse " +
            "(keys and mouse buttons as your own maps bind them; the pointer stays on your PC). Used from your next join; in a lobby, from the host's next Start.";
        d.Label(new Rect(textX, y, textW, 1.6f * u), "Online input", d.Bold, PanelDraw.Text);
        y += 1.7f * u;
        (OnlineInputChoice Choice, string Label)[] choices = { (OnlineInputChoice.Auto, "Auto"), (OnlineInputChoice.Gamepad, "Gamepad"), (OnlineInputChoice.KeyboardMouse, "Keyboard and mouse") };
        float bx = textX;
        foreach ((OnlineInputChoice choice, string label) in choices)
        {
            float bw = (label.Length * 0.55f * u) + (2 * u);
            if (d.Button(new Rect(bx, y, bw, 1.7f * u), label, true, primary: choice == current) && choice != current)
            {
                Queue("Changing Online input", () => SetInput(choice, label));
            }

            bx += bw + (0.6f * u);
        }

        y += 2.1f * u;
        float explained = d.Height(explanation, d.Wrap, textW);
        d.Label(new Rect(textX, y, textW, explained), explanation, d.Wrap, PanelDraw.Muted);
        y += explained + (0.5f * u);
    }

    /// <summary>Sets OnlineInput and saves it (<see cref="ModSettings.TrySetOnlineInput"/>).</summary>
    private void SetInput(OnlineInputChoice choice, string label)
    {
        if (ModSettings.TrySetOnlineInput(choice, out string problem))
        {
            ModLog.Info($"Panel: Online input {choice} (saved); used from the next join, in a lobby from the host's next Start.");
            Show($"Online input: {label}, from your next join or the host's next Start.", PanelDraw.Good);
        }
        else
        {
            ModLog.Warn($"Panel: Online input {choice}: {problem}.");
            Show($"Online input: {problem}.", PanelDraw.Warn);
        }
    }

    /// <summary>Turns a setting on or off and saves it (<see cref="ModSettings.TrySetSwitch"/>).</summary>
    private void SetSwitch(string name, string title, bool value)
    {
        string state = value ? "on" : "off";
        if (ModSettings.TrySetSwitch(name, value, out string problem))
        {
            ModLog.Info($"Panel: {title} {state} (saved).");
            Show($"{title} is {state}.", PanelDraw.Good);
        }
        else
        {
            ModLog.Warn($"Panel: {title} {state}: {problem}.");
            Show($"{title}: {problem}.", PanelDraw.Warn);
        }
    }

    /// <summary>The net overlay on or off, as F9.</summary>
    private void ToggleNetOverlay()
    {
        ModLog.Info("Panel: net overlay switched (as F9).");
        _online.ToggleNetOverlay();
        Show(_online.NetOverlayVisible ? "The net overlay is on (top right)." : "The net overlay is off.", PanelDraw.Muted);
    }

    /// <summary>
    /// The Help tab: bug reports first (make one, mark a moment, open the logs or data folder, the last report's state), then
    /// the keys. When it doesn't all fit it scrolls a whole block at a time (<see cref="T2TOnline.Core.Lobby.BlockScroll"/>), as
    /// the Backups list scrolls its rows: the mouse wheel over the tab, Up and Down on a last row, and the arrow keys, Page Up,
    /// Page Down, Home and End. Blocks are measured from cached heights into fixed buffers: nothing is allocated.
    /// </summary>
    private void DrawHelp(Rect body)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float x = body.x;
        float w = body.width;
        _helpStatus = BugReports.Status;
        _helpReportReady = BugReports.State == ReportState.Ready;
        _helpOffline = _session?.Summary.Phase is SessionPhase.Unavailable or SessionPhase.Broken;
        int count = 0;
        for (int part = 0; part < HelpParts; part++)
        {
            float height = HelpBlock((HelpPart)part, x, 0, w, draw: false);
            if (height > 0)
            {
                _helpParts[count] = (HelpPart)part;
                _helpHeights[count] = height;
                count++;
            }
        }

        ReadOnlySpan<float> heights = _helpHeights.AsSpan(0, count);
        float rowHeight = 2.0f * u;
        float room = body.height;
        bool scrolls = !T2TOnline.Core.Lobby.BlockScroll.AllFit(heights, room);
        int last = 0;
        if (scrolls)
        {
            room -= rowHeight + (0.2f * u);
            last = T2TOnline.Core.Lobby.BlockScroll.LastFirst(heights, room);
        }

        int first = Math.Clamp(_helpFirst, 0, last);
        int shown = T2TOnline.Core.Lobby.BlockScroll.Shown(heights, first, room);
        if (scrolls)
        {
            int to = first + d.Scrolled(new Rect(x, body.y, w, room));
            to = d.KeyDown switch
            {
                KeyCode.UpArrow => to - 1,
                KeyCode.DownArrow => to + 1,
                KeyCode.PageUp => to - Math.Max(1, shown),
                KeyCode.PageDown => to + Math.Max(1, shown),
                KeyCode.Home => 0,
                KeyCode.End => last,
                _ => to,
            };

            if (to != first)
            {
                first = Math.Clamp(to, 0, last);
                shown = T2TOnline.Core.Lobby.BlockScroll.Shown(heights, first, room);
            }
        }

        _helpFirst = first;
        float y = body.y;
        for (int i = first; i < first + shown; i++)
        {
            HelpBlock(_helpParts[i], x, y, w, draw: true);
            y += _helpHeights[i];
        }

        if (!scrolls)
        {
            return;
        }

        // The last row, as the Backups list's: where this is, and Up and Down a view at a time.
        float row = body.y + body.height - rowHeight;
        d.Line(x, row - (0.1f * u), w);
        d.Label(new Rect(x + (0.5f * u), row + (0.2f * u), w - (17 * u), 1.6f * u), first < last ? HelpMore : HelpEnd, d.Small, PanelDraw.Muted);
        if (d.Button(new Rect(x + w - (15.6f * u), row + (0.1f * u), 7.6f * u, rowHeight - (0.2f * u)), "Up", first > 0))
        {
            _helpFirst = Math.Max(0, first - Math.Max(1, shown));
        }

        if (d.Button(new Rect(x + w - (7.5f * u), row + (0.1f * u), 7.3f * u, rowHeight - (0.2f * u)), "Down", first < last))
        {
            _helpFirst = Math.Min(last, first + Math.Max(1, shown));
        }
    }

    /// <summary>
    /// One block of the Help tab at <paramref name="y"/>: its height (0 when it isn't there this time), and, when
    /// <paramref name="draw"/>, drawn. Measuring and drawing take the same steps, so a block is always as tall as measured.
    /// </summary>
    private float HelpBlock(HelpPart part, float x, float y, float w, bool draw)
    {
        float u = _draw.Unit;
        switch (part)
        {
            case HelpPart.Reports:
                return ReportsBlock(x, y, w, draw);
            case HelpPart.KeysHeading:
            {
                float top = y;
                if (draw)
                {
                    Heading("Keys", x, ref y, w);
                }
                else
                {
                    y += 2.2f * u;
                }

                if (_helpOffline)
                {
                    y += HelpParagraph(OfflineKeysLine, x, y, w, PanelDraw.Warn, draw) + (0.3f * u);
                }

                return y - top;
            }

            case HelpPart.Intro:
                return HelpParagraph(IntroLine, x, y, w, PanelDraw.Muted, draw) + (0.3f * u);
            case HelpPart.KeyF1:
                return HelpKey("F1", "This panel (Esc closes it too)", x, y, w, draw);
            case HelpPart.KeyF7:
                return HelpKey("F7", "Joins a friend who's hosting, or else hosts; press again to leave", x, y, w, draw);
            case HelpPart.KeyShiftF7:
                return HelpKey("Shift+F7", "Hosts a game, even when friends have games open", x, y, w, draw);
            case HelpPart.KeyF9:
                return HelpKey("F9", "The net overlay: live connection numbers (a developer's tool: needs ShowStatusBox in the settings file)", x, y, w, draw);
            case HelpPart.KeyDrop:
                return HelpKey(OnlineSession.DropKey, "As a friend: test a 15 s network drop (the AI plays for you meanwhile)", x, y, w, draw);
            case HelpPart.KeySolo:
                return HelpKey(OnlineSession.SoloKey, "While hosting: the one-PC test (your pad plays as a friend, over Steam)", x, y, w, draw);
            case HelpPart.KeyDev:
                return _devTools ? HelpKey("Dev", "DevTools is on: F2, F3, F4, F8, F10, Ctrl+F10, F11, Ctrl+F11, Ctrl+F12 and Ctrl+F9", x, y, w, draw) : 0;
            case HelpPart.KeyEvents:
                return _devTools ? HelpKey("Ctrl+F10", "On the world map, as host or alone: opens the next kind of event (a test)", x, y, w, draw) : 0;
            case HelpPart.Focus:
                return HelpParagraph(FocusLine, x, y, w, PanelDraw.Muted, draw);
            case HelpPart.Keyboard:
                return HelpParagraph(KeyboardLine, x, y, w, PanelDraw.Muted, draw);
            default:
                return HelpParagraph("Controller: check your pad in the Controller tab before a session, at the title screen.", x, y, w, PanelDraw.Muted, draw);
        }
    }

    /// <summary>The Bug reports block: its heading, the words beside a two-by-two block of buttons, the last report's state and where things are kept.</summary>
    private float ReportsBlock(float x, float y, float w, bool draw)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        float start = y;
        if (draw)
        {
            Heading("Bug reports", x, ref y, w);
        }
        else
        {
            y += 2.2f * u;
        }

        // The words on the left, a two-by-two block of buttons on the right.
        float gap = 0.5f * u;
        float buttonW = Math.Min(11.5f * u, (w - gap) / 4);
        float buttonsX = x + w - (2 * buttonW) - gap;
        float top = y;
        if (draw)
        {
            bool busy = BugReports.Busy;
            if (d.Button(new Rect(buttonsX, top, buttonW, 2 * u), busy ? "Making..." : "Make a bug report", enabled: !busy, primary: true))
            {
                Queue("Making a bug report", MakeReport);
            }

            if (d.Button(new Rect(buttonsX + buttonW + gap, top, buttonW, 2 * u), "Mark this moment"))
            {
                Queue("Marking this moment", MarkMoment);
            }

            if (d.Button(new Rect(buttonsX, top + (2.4f * u), buttonW, 2 * u), "Open the logs folder"))
            {
                Queue("Opening the logs folder", OpenLogs);
            }

            if (d.Button(new Rect(buttonsX + buttonW + gap, top + (2.4f * u), buttonW, 2 * u), "Open data folder"))
            {
                Queue("Opening the data folder", OpenDataFolder);
            }
        }

        y += HelpParagraph(ReportLine, x, y, buttonsX - x - gap, PanelDraw.Muted, draw);
        y = Math.Max(y, top + (4.4f * u)) + (0.4f * u);
        string status = _helpStatus;
        if (status.Length > 0)
        {
            Color colour = PanelDraw.Text;
            if (draw)
            {
                ReportState state = BugReports.State;
                colour = state == ReportState.Ready ? PanelDraw.Good : state == ReportState.Failed ? PanelDraw.Bad : PanelDraw.Text;
            }

            if (_helpReportReady)
            {
                // A report ready: Show in Explorer beside its line (Explorer opened by itself only if this panel was open as it got ready).
                if (draw && d.Button(new Rect(x + w - buttonW, y, buttonW, 2 * u), "Show in Explorer"))
                {
                    Queue("Showing the bug report in Explorer", ShowReport);
                }

                y += Math.Max(HelpParagraph(status, x, y, w - buttonW - gap, colour, draw), 2 * u) + (0.2f * u);
            }
            else
            {
                y += HelpParagraph(status, x, y, w, colour, draw);
            }
        }

        y += HelpParagraph(StorageLine, x, y, w, PanelDraw.Dim, draw);
        return y - start + (0.8f * u);
    }

    /// <summary>Wrapped smaller text at <paramref name="y"/>, drawn when <paramref name="draw"/>; its height either way.</summary>
    private float HelpParagraph(string text, float x, float y, float w, Color colour, bool draw)
    {
        float height = _draw.Height(text, _draw.Wrap, w);
        if (draw)
        {
            _draw.Label(new Rect(x, y, w, height), text, _draw.Wrap, colour);
        }

        return height;
    }

    /// <summary>One key's row (<see cref="Key"/>), drawn when <paramref name="draw"/>; its height either way.</summary>
    private float HelpKey(string key, string what, float x, float y, float w, bool draw)
    {
        if (draw)
        {
            Key(key, what, x, ref y, w);
        }

        return 1.7f * _draw.Unit;
    }

    /// <summary>One key and what it does.</summary>
    private void Key(string key, string what, float x, ref float y, float w)
    {
        PanelDraw d = _draw;
        float u = d.Unit;
        d.Label(new Rect(x + (0.5f * u), y, 6 * u, 1.6f * u), key, d.Bold, PanelDraw.Accent);
        d.Label(new Rect(x + (6.8f * u), y, w - (6.8f * u), 1.6f * u), what, d.Body, PanelDraw.Text);
        y += 1.7f * u;
    }

    /// <summary>Make a bug report: started in the background (<see cref="BugReports"/>); the Help tab shows how it goes.</summary>
    private void MakeReport()
    {
        if (BugReports.Make("the F1 panel's button", openExplorer: true))
        {
            Show("Making a bug report: Explorer shows it if this panel is still open when it's ready.", PanelDraw.Muted);
        }
        else
        {
            bool busy = BugReports.Busy;
            Show(busy ? "A bug report is being made: a moment." : BugReports.Status, busy ? PanelDraw.Muted : PanelDraw.Bad);
        }
    }

    /// <summary>Show in Explorer: Explorer opens with the last report selected (<see cref="BugReports.ShowLastInExplorer"/>).</summary>
    private void ShowReport()
    {
        string? problem = BugReports.ShowLastInExplorer();
        Show(problem == null ? "Explorer shows the report." : $"Couldn't show the report: {problem}.", problem == null ? PanelDraw.Good : PanelDraw.Bad);
    }

    /// <summary>Mark this moment: a MARK line in the log and the game's toast (<see cref="BugReports.Mark"/>).</summary>
    private void MarkMoment()
    {
        int number = BugReports.Mark("the F1 panel's button");
        Show(number > 0 ? $"Moment {number} marked: a bug report shows it as MARK {number}." : "The moment couldn't be marked (the log says why).",
            number > 0 ? PanelDraw.Good : PanelDraw.Bad);
    }

    /// <summary>Opens the logs folder in Windows' file explorer.</summary>
    private void OpenLogs() => OpenFolder(_logFolder, "logs folder", LogsLabel);

    /// <summary>Opens the mod's data folder (backups, co-op runs, shared progress) in Windows' file explorer.</summary>
    private void OpenDataFolder() => OpenFolder(CampaignFiles.DataFolder, "data folder", CampaignFiles.DataFolderLabel);

    /// <summary>Opens <paramref name="folder"/> in Windows' file explorer; the log names it by <paramref name="label"/>, never by its path.</summary>
    private void OpenFolder(string folder, string what, string label)
    {
        if (!Directory.Exists(folder))
        {
            ModLog.Warn($"Panel: the {what} isn't there ({label}).");
            Show($"The {what} isn't there yet.", PanelDraw.Warn);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true })?.Dispose();
            ModLog.Info($"Panel: opened the {what} ({label}).");
            Show($"Opened the {what}.", PanelDraw.Good);
        }
        catch (Exception ex)
        {
            string error = ModLog.SafeErrorText(ex);
            ModLog.Warn($"Panel: couldn't open the {what} ({error}).");
            Show($"Couldn't open the {what}: {error}", PanelDraw.Bad);
        }
    }

    /// <summary>A section's heading at <paramref name="y"/>, which moves past it.</summary>
    private void Heading(string text, float x, ref float y, float w)
    {
        _draw.Label(new Rect(x, y, w, 1.8f * _draw.Unit), text, _draw.Heading, PanelDraw.Text);
        y += 2.2f * _draw.Unit;
    }

    /// <summary>Wrapped smaller text at <paramref name="y"/>, which moves past it.</summary>
    private void Paragraph(string text, float x, ref float y, float w, Color colour)
    {
        float height = _draw.Height(text, _draw.Wrap, w);
        _draw.Label(new Rect(x, y, w, height), text, _draw.Wrap, colour);
        y += height;
    }

    /// <summary>A save or backup time for the panel, in this PC's time ("Oct 30, 21:14").</summary>
    private static string When(DateTime time)
    {
        DateTime local = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
        return local.ToString(local.Year == DateTime.Now.Year ? "MMM d, HH:mm" : "MMM d yyyy, HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"1 file", "4 files".</summary>
    private static string FileCount(int files) => files == 1 ? "1 file" : $"{files} files";

    /// <summary>What a backup holds, for its row: "6 files", "6 files · co-op slots 1, 2".</summary>
    private static string Contents(BackupInfo backup) => backup.Slots.Count == 0 ? FileCount(backup.Files)
        : $"{FileCount(backup.Files)} · co-op slot{(backup.Slots.Count == 1 ? string.Empty : "s")} {string.Join(", ", backup.Slots)}";

    /// <summary>The session as the Online tab shows it, made when it's read: its state line, colour and detail, and a row per player.</summary>
    private sealed record SessionText(SessionSummary Summary, string State, Color StateColour, string Detail, IReadOnlyList<PlayerRow> Players)
    {
        public static SessionText Of(SessionSummary session)
        {
            (string state, Color colour, string detail) = Describe(session);
            var players = new List<PlayerRow>(session.Players.Count);
            foreach (SessionPlayer player in session.Players)
            {
                players.Add(new PlayerRow(
                    player.Name,
                    player.Note.Length > 0 ? $"{player.Seat} · {player.Note}" : player.Seat,
                    player.Note.Length > 0 ? PanelDraw.Warn : PanelDraw.Text,
                    player.IsYou ? string.Empty : player.PingMs >= 0 ? $"{player.PingMs} ms" : "–"));
            }

            return new SessionText(session, state, colour, detail, players);
        }
    }

    /// <summary>One player's row: name, seat (with a note), the seat's colour, and ping.</summary>
    private sealed record PlayerRow(string Name, string Seat, Color SeatColour, string Ping);

    /// <summary>
    /// One friend's game as the Online tab shows it: who hosts it, a line about it (its version and players; "checking..."
    /// until its details arrive; why it can't be joined; or that its version isn't yours), that line's colour, and whether
    /// Join is offered.
    /// </summary>
    private sealed record GameRow(ulong LobbyId, string HostName, string Details, Color DetailsColour, bool Joinable)
    {
        public static GameRow Of(JoinableGame game)
        {
            string players = game.Players == 1 ? "1 player" : $"{game.Players} players";
            (string details, Color colour) = !game.Checked || game.Note.Length > 0
                ? (game.Note, game.Checked ? PanelDraw.Warn : PanelDraw.Muted)
                : ReleaseVersion.SameRelease(game.ModVersion, ModVersion.Release)
                    ? ($"T2T Online {ReleaseVersion.ForScreen(game.ModVersion)} · {players}", PanelDraw.Muted)
                    : ($"T2T Online {(game.ModVersion.Length > 0 ? ReleaseVersion.ForScreen(game.ModVersion) : "of an unknown version")}, you have {ReleaseVersion.ForScreen(ModVersion.Release)}: install the host's release · {players}", PanelDraw.Warn);
            return new GameRow(game.LobbyId, game.HostName, details, colour, game.Joinable);
        }
    }

    /// <summary>A seat kept for a friend who dropped: their Steam ID and name.</summary>
    private sealed record SeatRow(ulong SteamId, string Name);

    /// <summary>
    /// One co-op slot's row: its run's squad, act and floor and when it was saved. What couldn't be read isn't shown as a
    /// value: a run file the mod can't read says so (not "act 0"), and an unknown save time is left out.
    /// </summary>
    private sealed record SlotRow(int Slot, string Label, string Details, bool HasRun)
    {
        public static SlotRow Of(int slot, IReadOnlyList<CoopSlotInfo>? slots)
        {
            CoopSlotInfo? info = slots?.FirstOrDefault(s => s.Slot == slot);
            string details = slots == null ? "Couldn't be read (see the log)"
                : info == null ? "No details yet"
                : !info.HasRun ? "Empty"
                : info.Act > 0 ? $"{(info.Squad.Length > 0 ? info.Squad : "A run")} · act {info.Act}, floor {info.Floor}{Saved(info.SavedAt)}"
                : $"A run, details unknown{Saved(info.SavedAt)}";
            return new SlotRow(slot, $"Slot {slot}", details, info is { HasRun: true });
        }

        private static string Saved(DateTime at) => at == DateTime.MinValue ? string.Empty : $" · saved {When(at)}";
    }

    /// <summary>One backup's row: the backup, when it was taken and how many files it holds, as shown.</summary>
    private sealed record BackupRow(BackupInfo Info, string When, string Files);

    /// <summary>
    /// A restore waiting for its confirmation, with the text it shows, what the backup offers (<paramref name="Choices"/>:
    /// your saves and each co-op slot) and what's chosen so far.
    /// </summary>
    private sealed record Confirmation(BackupRow Backup, string Question, string Details, IReadOnlyList<RestoreChoice> Choices, bool Saves, IReadOnlyList<int> Slots)
    {
        /// <summary>True when <paramref name="choice"/> is chosen (no allocation: read while drawing).</summary>
        public bool IsOn(RestoreChoice choice)
        {
            if (choice.Slot == 0)
            {
                return Saves;
            }

            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i] == choice.Slot)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The same confirmation with <paramref name="choice"/> switched (on a click).</summary>
        public Confirmation Switched(RestoreChoice choice) => choice.Slot == 0
            ? this with { Saves = !Saves }
            : this with { Slots = IsOn(choice) ? Slots.Where(s => s != choice.Slot).ToList() : Slots.Append(choice.Slot).OrderBy(s => s).ToList() };
    }

    /// <summary>One part a backup can restore: <paramref name="Slot"/> 0 for your saves, else that co-op slot; its label and what it holds.</summary>
    private sealed record RestoreChoice(int Slot, string Label, string Details);

    /// <summary>The Settings tab's switches, as last read.</summary>
    private sealed record Switches(bool ShareAchievements, bool ShareProgress, bool FightGraceWindow, bool AiStandIn, bool NetOverlay, OnlineInputChoice Input, bool NativeLobby);
}
#endif
