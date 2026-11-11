#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// The Backups screen (D141), in the <see cref="ShellKind.Backups"/> shell over the game's Settings: the Glossary's frame with its own tabs, <b>Your Saves | Co-op Runs</b>
/// (the game's <c>GlossaryTab</c>, LB and RB on the band's own glyphs), rows as the Join list's (newest first, <see cref="BackupRows"/>), a details pane, and the bar
/// <b>[A] Restore [B] Back</b>. A asks on the game's yes/no, Cancel first, then restores exactly that row through <see cref="SaveBackups.TryRestore(string, RestoreParts, RestoreAsker, out string)"/>
/// (what's there now is backed up first); after the saves the game's prompt says to restart, <b>Later / Quit</b>, Quit being the title's own <c>ExitGame</c>; a
/// restore that didn't happen says why. Refused during a check tour; it closes itself when an online game starts. Nothing here throws into the game: a step that
/// fails closes the screen, logged.
/// </summary>
internal sealed class BackupsScreen
{
    /// <summary><c>ui_parts.txt</c>'s rows for the tab band's parts, checked against the shell before they're used.</summary>
    public const string TabParts = "BackupsTabs";

    /// <summary><c>ui_parts.txt</c>'s rows for the game's tab prefab.</summary>
    public const string TabPrefabParts = "GlossaryTab";

    private const double LookSeconds = 0.5;
    private const double RestartAskSeconds = 5;
    private const float ListWidth = 620;
    private const float TabWidth = 275;
    private const float RowSide = 28;

    // What's still the press of the row that opened the screen, not a choice on it.
    private const double ArrivalSeconds = LobbyTiming.InputLockMicros / 1_000_000.0;

    private static readonly HashSet<string> TabKeep = new(UiClone.Widgets, StringComparer.Ordinal) { "GlossaryTab" };
    private static readonly BackupTab[] Tabs = { BackupTab.Saves, BackupTab.CoopRuns };
    private static bool _installed;
    private static Il2CppSystem.Action<UIInputShortcutBase>? _tabShortcut;

    private readonly ShellParts _parts;
    private readonly List<RowView> _rows = new();
    private readonly int[] _focus = new int[Tabs.Length];
    private readonly GlossaryTab?[] _tabs = new GlossaryTab?[Tabs.Length];
    private readonly IntPtr[] _tabButtons = new IntPtr[Tabs.Length];
    private PromptBar _bar = null!;
    private DetailPane _pane = null!;
    private NavigableParent? _parent;
    private UIInputShortcutBase? _prevTab;
    private UIInputShortcutBase? _nextTab;
    private IntPtr _prevClick;
    private IntPtr _nextClick;
    private BackupRowSet _set = BackupRowSet.Empty;
    private BackupTab _tab = BackupTab.Saves;
    private bool _awake;
    private double _openedAt;
    private double _lookAt;
    private int _language = -1;
    private IntPtr _cursor;
    private bool _asking;
    private BackupRow? _restartFor;
    private double _restartSince = -1;
    private double _readMs;

    private BackupsScreen(ShellParts parts) => _parts = parts;

    /// <summary>The screen while it's up (the check tour reads it), or null.</summary>
    public static BackupsScreen? Current { get; private set; }

    /// <summary>The tab shown.</summary>
    public BackupTab Tab => _tab;

    /// <summary>The rows of the tab shown.</summary>
    public IReadOnlyList<BackupRow> Rows => _set.Of(_tab);

    /// <summary>The bar (the check tour presses its prompts).</summary>
    public PromptBar Bar => _bar;

    /// <summary>A question of this screen's is up or on its way.</summary>
    public bool Asking => _asking;

    /// <summary>Its listeners on the shell, once a game (from the Settings tab's install, in every state the native UI runs in).</summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        LobbyShell.Built += OnBuilt;
        LobbyShell.Opened += OnOpened;
        LobbyShell.Ticked += OnTicked;
        LobbyShell.Closed += OnClosed;
    }

    /// <summary>Shows <paramref name="tab"/> (LB, RB, a click on a tab; the check tour). False while it isn't up.</summary>
    public bool SelectTab(BackupTab tab)
    {
        if (!_awake)
        {
            return false;
        }

        ShowTab(tab, select: true);
        return true;
    }

    // ---- The shell's life ----

    /// <summary>The shell's parts, asleep: a failure here is the shell's preflight failing (the clone goes, and only this screen is off for the game).</summary>
    private static void OnBuilt(ShellKind kind, ShellParts parts)
    {
        if (kind != ShellKind.Backups)
        {
            return;
        }

        Current?.Forget();
        Current = null;
        var screen = new BackupsScreen(parts);
        try
        {
            screen.Build();
        }
        catch (Exception)
        {
            screen.Forget(); // the shell's preflight logs why, destroys the clone and turns this screen off for the game
            throw;
        }

        Current = screen;
    }

    private static void OnOpened(ShellKind kind, ShellParts parts)
    {
        if (kind == ShellKind.Backups)
        {
            Guard("opening", () => Current?.Opened());
        }
    }

    private static void OnTicked(ShellKind kind)
    {
        if (kind == ShellKind.Backups)
        {
            Guard("its step", () => Current?.Tick(FrameClock.Seconds));
        }
    }

    private static void OnClosed(ShellKind kind, ShellClosed how)
    {
        if (kind != ShellKind.Backups)
        {
            return;
        }

        BackupsScreen? screen = Current;
        Current = null;
        if (screen != null)
        {
            try
            {
                screen.Closed();
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Backups: the screen's close failed ({ModLog.SafeErrorText(ex)}).");
            }
        }
    }

    /// <summary>A step that threw leaves the screen half drawn: it closes (Settings is under it, as it was).</summary>
    private static void Guard(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: the screen failed at {what} ({ModLog.SafeErrorText(ex)}); it closes.");
            Current?.Forget();
            if (LobbyShell.IsOpen && LobbyShell.Kind == ShellKind.Backups)
            {
                LobbyShell.Close("the Backups screen failed");
            }
        }
    }

    // ---- Building ----

    private void Build()
    {
        _bar = new PromptBar(_parts, OnPrompt);
        LobbyFrame.FixedColumn(_parts, ListWidth);
        _pane = new DetailPane(_parts);
        LobbyFrame.RowsContent(_parts);
        _parent = _parts.ScrollContent.GetComponent<NavigableParent>();
        BuildTabs();
        Load();
    }

    /// <summary>The game's own tabs in the Glossary's own band, and its LB and RB glyphs: the game's menu script went with the clone, so this screen answers them.</summary>
    private void BuildTabs()
    {
        string? missing = UiAssets.CheckParts(TabParts, _parts.Root.gameObject);
        if (missing != null)
        {
            throw new InvalidOperationException(missing);
        }

        Transform container = _parts.Tabs.Find("TabsContainer") ?? throw new InvalidOperationException("the band has no TabsContainer");
        HorizontalLayoutGroup row = container.GetComponent<HorizontalLayoutGroup>() ?? throw new InvalidOperationException("the band's tabs aren't laid out");
        row.childForceExpandWidth = false; // two tabs at the prefab's own width, centred, not half the band each

        GameObject prefab = UiAssets.Prefab(LobbyUi.Glossary)?.GetComponent<GlossaryMenu>()?.m_TabsPrefab?.gameObject
            ?? throw new InvalidOperationException("the Glossary has no tab prefab");
        missing = UiAssets.CheckParts(TabPrefabParts, prefab);
        if (missing != null)
        {
            throw new InvalidOperationException(missing);
        }

        for (int i = 0; i < Tabs.Length; i++)
        {
            BackupTab tab = Tabs[i];
            GameObject clone = UiClone.Make(prefab, TabKeep, $"the {tab} tab", out string problem) ?? throw new InvalidOperationException(problem);
            clone.name = $"Tab{tab}";
            LayoutElement size = clone.GetComponent<LayoutElement>() ?? clone.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<LayoutElement>())?.TryCast<LayoutElement>()
                ?? throw new InvalidOperationException("a tab can't lay out");
            size.preferredWidth = size.minWidth = TabWidth;
            UiClone.Place(clone, container);
            _tabs[i] = clone.GetComponent<GlossaryTab>() ?? throw new InvalidOperationException("a tab's clone has no tab script");
            UnselectableButtonBase button = clone.GetComponent<UnselectableButtonBase>() ?? throw new InvalidOperationException("a tab's clone has no button");
            _tabButtons[i] = button.Pointer;
            PointerClicks.Bind(button, () => Clicked(() => ShowTab(tab, select: true)));
        }

        _prevTab = _parts.Tabs.Find("UIInputShortcut_V2-PrevTab")?.GetComponent<UIInputShortcutBase>() ?? throw new InvalidOperationException("the band has no LB");
        _nextTab = _parts.Tabs.Find("UIInputShortcut_V2-NextTab")?.GetComponent<UIInputShortcutBase>() ?? throw new InvalidOperationException("the band has no RB");
        _tabShortcut ??= Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIInputShortcutBase>>(new Action<UIInputShortcutBase>(OnTabShortcut))
            ?? throw new InvalidOperationException("the tabs' delegate couldn't be made");
        _prevTab.OnInputShortcutTriggered.Register(_tabShortcut, "T2TOnline");
        _nextTab.OnInputShortcutTriggered.Register(_tabShortcut, "T2TOnline");
        UIInputShortcutBase prev = _prevTab;
        UIInputShortcutBase next = _nextTab;
        _prevClick = PointerClicks.Make(prev.gameObject, null, () => Clicked(prev.Trigger)).Pointer;
        _nextClick = PointerClicks.Make(next.gameObject, null, () => Clicked(next.Trigger)).Pointer;
    }

    /// <summary>The backups as they are on disk now, and a row for each (the rows of a load before go).</summary>
    private void Load()
    {
        long started = Stopwatch.GetTimestamp();
        foreach (RowView view in _rows)
        {
            LobbyClicks.Forget(view.Row.Button);
            UiClone.Destroy(view.Row.Root);
        }

        _rows.Clear();
        _set = Read();
        foreach (BackupTab tab in Tabs)
        {
            IReadOnlyList<BackupRow> rows = _set.Of(tab);
            for (int i = 0; i < rows.Count; i++)
            {
                _rows.Add(MakeRow(rows[i], i));
            }
        }

        _readMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    }

    private static BackupRowSet Read()
    {
        try
        {
            IReadOnlyList<BackupInfo> backups = SaveBackups.List();
            var runs = new Dictionary<string, IReadOnlyList<BackupSlotRun>>(StringComparer.OrdinalIgnoreCase);
            foreach (BackupInfo backup in backups)
            {
                if (backup.Slots.Count > 0)
                {
                    runs[backup.Name] = SaveBackups.SlotRuns(backup);
                }
            }

            return BackupRows.Build(backups, runs, CoopSlots.Count);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: the backups couldn't be read ({ModLog.SafeErrorText(ex)}); the screen shows none.");
            return BackupRowSet.Empty;
        }
    }

    /// <summary>A row as the Join list's (the Glossary's own row), with a second line under its title.</summary>
    private RowView MakeRow(BackupRow model, int index)
    {
        ListRow row = ListRow.Make(_parts.ScrollContent, $"{model.Tab}{index}", withParts: false);
        LayoutElement? size = row.Root.GetComponent<LayoutElement>();
        if (size != null)
        {
            size.minHeight = size.preferredHeight = ListRow.Height;
        }

        Transform container = row.Root.transform.Find("Container") ?? throw new InvalidOperationException("the row has no Container");
        TMP_Text title = container.Find("MainText/Title")?.GetComponent<TMP_Text>() ?? throw new InvalidOperationException("the row has no title");
        RectTransform mainText = container.Find("MainText")?.GetComponent<RectTransform>() ?? throw new InvalidOperationException("the row has no MainText");
        LobbyUi.Fill(mainText, RowSide, 42, RowSide, 6);
        title.alignment = TextAlignmentOptions.MidlineLeft;
        TextMeshProUGUI detail = LobbyUi.Label(LobbyFont.Body, container, "Detail", 26, align: TextAlignmentOptions.MidlineLeft, color: LobbyUi.BodyText);
        LobbyUi.Fill(detail.rectTransform, RowSide, 6, RowSide, 48);
        var view = new RowView(model, index, row, title, detail);
        LobbyClicks.Bind(row.Button, () => Press(view));
        LobbyUi.Show(LobbyUi.RectOf(row.Root), false);
        return view;
    }

    // ---- Showing ----

    private void Opened()
    {
        _awake = true;
        _openedAt = FrameClock.Seconds;
        _bar.Wake();
        _language = LobbyLocalization.Language();
        SayTabs();
        ShowTab(BackupTab.Saves, select: true);
        string left = _set.LostRuns + _set.Repeats == 0 ? string.Empty : $"; {_set.LostRuns} lost run(s) and {_set.Repeats} repeat(s) left out";
        ModLog.Info($"Backups: screen open ({_set.Saves.Count} with saves, {_set.CoopRuns.Count} co-op runs; read in {_readMs:0} ms{left}).");
    }

    private void Tick(double now)
    {
        if (!_awake)
        {
            return;
        }

        if (now >= _lookAt)
        {
            _lookAt = now + LookSeconds;

            // An online game started under it (an invite taken from Steam's overlay): nothing may be restored now.
            if (TabRows.Context().Role != SettingsRole.None)
            {
                ModLog.Info("Backups: the screen closes: an online game started.");
                if (_asking)
                {
                    LobbyPrompts.CloseNow();
                    _asking = false;
                }

                LobbyShell.Close("an online game started");
                return;
            }

            int language = LobbyLocalization.Language();
            if (language != _language)
            {
                _language = language;
                SayTabs();
                foreach (RowView view in _rows)
                {
                    view.Say();
                }

                Draw();
            }
        }

        if (_restartFor != null)
        {
            AskRestartAgain(now);
        }

        // The cursor is the one thing not drawn from the rows: the details follow it. Only its native object is compared (a row of this screen's, or not): no call
        // into the game to ask whether it's alive.
        AbstractNavigable? current = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        IntPtr pointer = current is null ? IntPtr.Zero : current.Pointer;
        if (pointer != _cursor)
        {
            _cursor = pointer;
            RowView? view = Find(pointer);
            if (view != null && view.Model.Tab == _tab)
            {
                _focus[(int)_tab] = view.Index;
                LobbyShell.SetDefaultNavigables(view.Row.Button);
                Draw();
            }
        }
    }

    private void Closed()
    {
        _awake = false;
        if (_restartFor != null)
        {
            // Its prompt never came: the session's notice says it (online on); the save guard holds every write until the restart anyway.
            RestartFallback("the screen closed first");
        }

        Forget();
    }

    /// <summary>Lets go of what the game's events point at: the rows', tabs' and bar's handlers.</summary>
    private void Forget()
    {
        _awake = false;
        _bar?.Forget();
        foreach (RowView view in _rows)
        {
            LobbyClicks.Forget(view.Row.Button);
        }

        foreach (IntPtr button in _tabButtons)
        {
            PointerClicks.Forget(button);
        }

        PointerClicks.Forget(_prevClick);
        PointerClicks.Forget(_nextClick);
        _rows.Clear();
    }

    private void SayTabs()
    {
        for (int i = 0; i < Tabs.Length; i++)
        {
            _tabs[i]?.SetTitle(LobbyLocalization.Say((Tabs[i] == BackupTab.Saves ? LobbyWords.YourSaves : LobbyWords.CoopRuns).Key));
        }
    }

    /// <summary>The rows of <paramref name="tab"/> up, linked and navigable, the others hidden; its tab lit; the cursor on its row (or on nothing in an empty tab).</summary>
    private void ShowTab(BackupTab tab, bool select)
    {
        _tab = tab;
        var order = new List<AbstractNavigable>();
        foreach (RowView view in _rows)
        {
            bool shown = view.Model.Tab == tab;
            LobbyUi.Show(LobbyUi.RectOf(view.Row.Root), shown);
            view.Row.Button.IsNavigable = shown;
            if (shown)
            {
                view.Say();
                order.Add(view.Row.Button);
            }
        }

        for (int i = 0; i < Tabs.Length; i++)
        {
            _tabs[i]?.SetSelected(Tabs[i] == tab);
        }

        ListRow.Link(order);
        _parent?.RegisterChildren(false);
        UiClone.Rebuild(_parts.ScrollContent);
        int focus = Math.Clamp(_focus[(int)tab], 0, Math.Max(order.Count - 1, 0));
        _focus[(int)tab] = focus;
        if (select)
        {
            if (order.Count > 0)
            {
                Select(order[focus]);
            }
            else
            {
                LobbyShell.SetDefaultNavigables();
                MenuGame.Module()?.NavigationController?.ForceSelectionToNull();
            }
        }

        Draw();
    }

    /// <summary>The details pane and the bar, for the focused row of the tab shown.</summary>
    private void Draw()
    {
        BackupRow? row = Focused()?.Model;
        if (row == null)
        {
            _pane.ShowLine(LobbyLocalization.Say(LobbyWords.NoBackups.Key));
            _pane.SetWords(string.Empty);
            _bar.Show(new[] { new LobbyPrompt(LobbyButton.Back, LobbyTerms.Back, LobbyAction.Back) });
            return;
        }

        _pane.ShowLine(Say(row.Title));
        string detail = Say(row.Detail);
        string what = LobbyLocalization.Say(row.DetailWord.Key);
        _pane.SetWords(detail.Length == 0 ? what : detail + "\n\n" + what);
        _bar.Show(new[]
        {
            new LobbyPrompt(LobbyButton.Confirm, LobbyWords.Restore.Key, LobbyAction.Open),
            new LobbyPrompt(LobbyButton.Back, LobbyTerms.Back, LobbyAction.Back),
        });
    }

    private RowView? Focused()
    {
        int index = _focus[(int)_tab];
        foreach (RowView view in _rows)
        {
            if (view.Model.Tab == _tab && view.Index == index)
            {
                return view;
            }
        }

        return null;
    }

    private RowView? Find(IntPtr button)
    {
        if (button == IntPtr.Zero)
        {
            return null;
        }

        foreach (RowView view in _rows)
        {
            if (view.Button == button)
            {
                return view;
            }
        }

        return null;
    }

    private static void Select(AbstractNavigable target)
    {
        LobbyShell.SetDefaultNavigables(target);
        AbstractNavigableUtils.SelectOnNextFrame(target, AbstractNavigable.ESelectFx.Default);
    }

    /// <summary>The cursor back on the row a prompt came from (the same backup and part, after a reload too); the tab's first row if it has gone.</summary>
    private void Reselect(BackupRow asked)
    {
        if (!_awake || Current != this)
        {
            return;
        }

        foreach (RowView view in _rows)
        {
            if (view.Model.Tab == _tab && view.Model.Backup == asked.Backup && view.Model.Slot == asked.Slot)
            {
                _focus[(int)_tab] = view.Index;
                Select(view.Row.Button);
                Draw();
                return;
            }
        }

        ShowTab(_tab, select: true);
    }

    // ---- Presses ----

    /// <summary>A mouse click on a tab or a glyph: only while nothing covers the screen, as a pad's press.</summary>
    private void Clicked(Action action)
    {
        if (_awake && LobbyShell.TakesInput && !_asking)
        {
            action();
        }
    }

    private static void OnTabShortcut(UIInputShortcutBase shortcut)
    {
        try
        {
            BackupsScreen? screen = Current;
            if (screen == null || !screen._awake || screen._asking || shortcut == null)
            {
                return;
            }

            int step = screen._prevTab != null && shortcut.Pointer == screen._prevTab.Pointer ? -1 : 1;
            int next = (((int)screen._tab + step) % Tabs.Length + Tabs.Length) % Tabs.Length; // the game's tabs wrap (GlossaryMenu.WrapTabIndex)
            screen.ShowTab(Tabs[next], select: true);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: a tab change failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>The bar: a click on [A] Restore with no row of the screen focused (the row's own click is the press otherwise).</summary>
    private void OnPrompt(LobbyPrompt prompt)
    {
        if (prompt.Action == LobbyAction.Open && Focused() is { } view)
        {
            Press(view);
        }
    }

    /// <summary>A on a row (or its click).</summary>
    private void Press(RowView view)
    {
        if (!_awake || view.Model.Tab != _tab || FrameClock.Seconds - _openedAt < ArrivalSeconds)
        {
            return;
        }

        _focus[(int)_tab] = view.Index;
        Ask(view.Model);
    }

    /// <summary>The game's yes/no for <paramref name="row"/>, Cancel first and focused; B is Cancel.</summary>
    private void Ask(BackupRow row)
    {
        if (_asking || LobbyPrompts.IsOpen)
        {
            return;
        }

        if (SaveBackups.RestartNeeded)
        {
            // A restore of the saves waits for the restart already: that's what to do now.
            ModLog.Info($"Backups: restore of {SaveBackupNames.ForLog(row.Backup)} ({row.PartForLog}) not asked: the game must restart first; the restart prompt again.");
            AskRestart(row);
            return;
        }

        string title = LobbyLocalization.Say(LobbyWords.SaveBackups.Key);
        string text = LobbyLocalization.Say(row.Question.ForRichText());
        if (!LobbyPrompts.AskChoice(title, text, LobbyLocalization.Say(LobbyTerms.Cancel), LobbyLocalization.Say(LobbyWords.Restore.Key), (_, restore) => Answered(row, restore)))
        {
            LobbySounds.Play(LobbySounds.Grunt);
            ModLog.Info($"Backups: restore of {SaveBackupNames.ForLog(row.Backup)} ({row.PartForLog}) not asked: another prompt is up.");
            return;
        }

        _asking = true;
    }

    private void Answered(BackupRow row, bool confirmed)
    {
        _asking = false;
        ModLog.Info($"Backups: restore of {SaveBackupNames.ForLog(row.Backup)} ({row.PartForLog}) asked; {(confirmed ? "confirmed" : "cancelled")}.");
        if (confirmed)
        {
            Restore(row);
        }
        else
        {
            Reselect(row);
        }
    }

    /// <summary>Exactly <paramref name="row"/>'s part, if the screen is still up and nothing forbids it; then what happened, on the game's prompt.</summary>
    private void Restore(BackupRow row)
    {
        string backupForLog = SaveBackupNames.ForLog(row.Backup);
        if (!_awake || Current != this || !LobbyShell.IsShown || LobbyShell.Kind != ShellKind.Backups)
        {
            ModLog.Info($"Backups: restore of {backupForLog} ({row.PartForLog}) refused: the Backups screen isn't up.");
            return;
        }

        if (TourHolds.On)
        {
            ModLog.Info($"Backups: restore of {backupForLog} ({row.PartForLog}) refused: the check tour runs.");
            Failed(row, "a check tour runs, and it restores nothing");
            return;
        }

        if (TabRows.Context().Role != SettingsRole.None)
        {
            ModLog.Info($"Backups: restore of {backupForLog} ({row.PartForLog}) refused: an online game is on.");
            Tell(LobbyLocalization.Say(LobbyWords.LeaveFirst.Key), row);
            return;
        }

        bool unfinishedBefore = SaveBackups.RestoreUnfinished;
        bool restored;
        string problem;
        try
        {
            restored = SaveBackups.TryRestore(row.Backup, row.Parts, RestoreAsker.BackupsScreen, out problem);
        }
        catch (Exception ex)
        {
            restored = false;
            problem = $"it failed ({ModLog.SafeErrorText(ex)})";
            ModLog.Warn($"Backups: the restore of {backupForLog} ({row.PartForLog}) threw ({ModLog.SafeErrorText(ex)}).");
        }

        // What's there now was backed up first: the list has a new row, and a partial restore may have left one too.
        Load();
        ShowTab(_tab, select: false);
        if (!restored)
        {
            string how = !unfinishedBefore && SaveBackups.RestoreUnfinished ? "was only partly done" : "didn't happen";
            ModLog.Warn($"Backups: the restore of {backupForLog} ({row.PartForLog}) {how}: {problem}.");
            Failed(row, problem);
            return;
        }

        if (row.Run == null)
        {
            AskRestart(row);
            return;
        }

        ModLog.Info($"Backups: co-op slot {row.Slot} is restored from {backupForLog}; the game goes on (no restart needed for a co-op slot).");
        Reselect(row);
    }

    private void Failed(BackupRow row, string problem) =>
        Tell(LobbyLocalization.Say(new LobbyLine(LobbyWords.RestoreFailed, problem).ForRichText()), row);

    private void Tell(string text, BackupRow row)
    {
        if (LobbyPrompts.Tell(LobbyLocalization.Say(LobbyWords.SaveBackups.Key), text, LobbyLocalization.Say(LobbyTerms.Ok), () => Reselect(row)))
        {
            return;
        }

        LobbySounds.Play(LobbySounds.Grunt);
        Reselect(row);
    }

    // ---- The restart ----

    /// <summary>The saves are back: the game must restart, and every save write is refused until it does. Later, or the game's own quit.</summary>
    private void AskRestart(BackupRow row)
    {
        string title = LobbyLocalization.Say(LobbyWords.SaveBackups.Key);
        string text = LobbyLocalization.Say(LobbyWords.SavesRestored.Key);
        if (LobbyPrompts.AskChoice(title, text, LobbyLocalization.Say(LobbyWords.Later.Key), LobbyLocalization.Say(LobbyTerms.Quit), (_, quit) => RestartAnswered(row, quit)))
        {
            _asking = true;
            _restartFor = null;
            _restartSince = -1;
            ModLog.Info("Backups: the restart prompt is up (Later or Quit).");
            return;
        }

        // Another prompt is up this frame: asked again as soon as it's gone.
        if (_restartFor == null)
        {
            _restartFor = row;
            _restartSince = FrameClock.Seconds;
        }
    }

    private void AskRestartAgain(double now)
    {
        if (now - _restartSince > RestartAskSeconds)
        {
            RestartFallback($"another prompt stayed up for {RestartAskSeconds:0} s");
            return;
        }

        if (!LobbyPrompts.IsOpen && !_asking && _restartFor is { } row)
        {
            AskRestart(row);
        }
    }

    /// <summary>The restart prompt couldn't show: the session's own restart notice (online on), else the log; the save guard holds until the restart either way.</summary>
    private void RestartFallback(string why)
    {
        _restartFor = null;
        _restartSince = -1;
        if (RunSync.Instance is { } sync)
        {
            ModLog.Warn($"Backups: the restart prompt didn't come ({why}); the session's restart notice says it instead.");
            sync.Notify(NoticeId.RestoredRestart, "Restored your saves from the backup.");
        }
        else
        {
            ModLog.Warn($"Backups: the restart prompt didn't come ({why}); the save guard refuses every save until the game restarts.");
        }
    }

    private void RestartAnswered(BackupRow row, bool quit)
    {
        _asking = false;
        if (!quit)
        {
            ModLog.Info("Backups: Later chosen: the game goes on, saving nothing until it restarts.");
            Reselect(row);
            return;
        }

        ModLog.Info("Backups: Quit chosen after the restore: the game's own quit.");
        try
        {
            // The title's own exit (its platform service's QuitApplication: Steam's saves the profile, refused by the guard now, shuts Steam down and quits);
            // called directly, as the title's click refuses while Settings is open over it.
            TitleScreen? title = CampaignGame.Title();
            if (title != null)
            {
                title.ExitGame();
                return;
            }

            ModLog.Warn("Backups: no title to quit through; Unity's own quit.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Backups: the title's quit failed ({ModLog.SafeErrorText(ex)}); Unity's own quit.");
        }

        Application.Quit();
    }

    private static string Say(IReadOnlyList<LobbyLine> lines)
    {
        var text = new StringBuilder();
        foreach (LobbyLine line in lines)
        {
            if (text.Length > 0)
            {
                text.Append(LobbyWords.Separator);
            }

            text.Append(line.Term switch
            {
                BackupRows.PlainTerm => line.Args.Count > 0 ? line.Args[0] : string.Empty,
                BackupRows.SquadTerm => line.Args.Count > 0 ? BrowserScreen.SquadName(line.Args[0]) : string.Empty,
                _ => LobbyLocalization.Say(line),
            });
        }

        return text.ToString();
    }

    /// <summary>A row's widget and what it restores.</summary>
    private sealed class RowView
    {
        private readonly TMP_Text _title;
        private readonly TMP_Text _detail;

        public RowView(BackupRow model, int index, ListRow row, TMP_Text title, TMP_Text detail)
        {
            Model = model;
            Index = index;
            Row = row;
            Button = row.Button.Pointer;
            _title = title;
            _detail = detail;
        }

        public BackupRow Model { get; }

        public ListRow Row { get; }

        public IntPtr Button { get; }

        /// <summary>Its place in its tab.</summary>
        public int Index { get; }

        public void Say()
        {
            LobbyUi.Set(_title, BackupsScreen.Say(Model.Title));
            LobbyUi.Set(_detail, BackupsScreen.Say(Model.Detail));
        }
    }
}
#endif
