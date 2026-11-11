#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.Catalogs;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppRewired;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The lobby, both roles, a pure function of <see cref="ILobbyActions.Snapshot"/>: seats in four vertical splits with lightning between them (or five by two), the host's column of rows, the status
/// band, the prompts, the countdown over it all. Nothing here keeps a truth of its own; the one thing it holds is where the cursor is, and <see cref="LobbyRules"/> says where that may be. Focus
/// doesn't move for an arrival (XAG 114); a row or seat that goes has its focus repaired, and the log says so. The invite panel is a state of this shell.
/// </summary>
internal sealed class LobbyView
{
    private const double RedrawSeconds = 1.0 / 15;
    private const double FriendsSeconds = 2;
    private const float ColumnMin = 430;
    private const float ColumnMax = 560;
    private const float ColumnSide = 24;
    private const float ModeTop = 16;
    private const float RowGap = 12;
    private const float SelectorHeight = 92;
    private const float SaveBoxHeight = 164;
    private const float ButtonHeight = 64;
    private const float StartHeight = 96;
    private const float ActionsGap = 28;

    // Start's area above the column's bottom edge: as far as Game Mode is below its top.
    private const float StartBottom = ModeTop;
    private const float BandSide = 344;
    private const float CardGap = 34;
    private const float GridRowGap = 12;
    private const double PopSeconds = 0.25;

    // How many friends' rows ask Steam for a picture: with the seats and the pane, under the avatar cache's 32.
    private const int FriendPictures = 20;

    private static readonly HashSet<string> ButtonKeep = new(UiClone.Widgets, StringComparer.Ordinal);

    private readonly LobbyScreens _owner;
    private readonly ILobbyActions _actions;
    private readonly FriendPickerRows _picker = new();
    private readonly Dictionary<ulong, ListRow> _friendRows = new();

    // The invite list's rows are named by this count (Friend1, Friend2...), never by a Steam ID: a dump, a warning or a report can't carry one.
    private int _friendRowsMade;
    private readonly List<AbstractNavigable> _friendOrder = new();
    private readonly Dictionary<IntPtr, LobbyFocus> _focusOf = new();
    private readonly List<SeatCard> _seats = new();
    private readonly List<RectTransform> _dividers = new();
    private readonly List<ulong> _seated = new();
    private ShellParts _parts = null!;
    private PromptBar _bar = null!;
    private DetailPane _pane = null!;
    private TextMeshProUGUI _band = null!;
    private TextMeshProUGUI _count = null!;
    private TextMeshProUGUI _tag = null!;
    private RectTransform _column = null!;
    private RectTransform _seatArea = null!;
    private Selector _mode = null!;
    private Selector _save = null!;
    private RectTransform _saveBox = null!;
    private TextMeshProUGUI _saveWords = null!;
    private AnimatedButton _invite = null!;
    private AnimatedButton _start = null!;
    private TextMeshProUGUI _inviteWord = null!;
    private TextMeshProUGUI _startWord = null!;
    private RectTransform _startFrame = null!;
    private RectTransform _friendNote = null!;
    private TextMeshProUGUI _friendNoteWord = null!;
    private TextMeshProUGUI _waitWord = null!;
    private RectTransform _countdown = null!;
    private TextMeshProUGUI _countdownNumber = null!;
    private TextMeshProUGUI _countdownWord = null!;
    private NavigableParent? _rowsParent;
    private ListRow? _panelBack;
    private LobbyState _state = LobbyState.None;
    private LobbySeat?[] _lastSeats = Array.Empty<LobbySeat?>();
    private string _language = string.Empty;
    private double _redrawAt;
    private double _languageAt;
    private double _friendsAt;
    private double _popAt = -1;
    private int _shownNumber;
    private uint _soundedEpoch;
    private bool _awake;
    private bool _panelOpen;
    private bool _panelRowsStale;
    private LobbyFocus _panelFrom = new(LobbyFocusKind.Unchanged);
    private LobbyFocus _focus = new(LobbyFocusKind.Unchanged);
    private IntPtr _cursor;
    private HostActivity _activity;
    private bool _allReadySaid;
    private int _capacity;
    private int _placedFor = -1;
    private float _columnWidth = ColumnMax;
    private Image _friendLock = null!;
    private UIInputShortcutBase? _inviteKey;
    private LobbyPrompt? _heldPress;
    private KeyCode _heldKey;
    private int _heldFrame;
    private double _lockedUntil;
    private double _panelOpenedAt;
    private int _artVersion;
    private IReadOnlyList<LobbySaveRow>? _slots;

    /// <summary>The screen over its shell.</summary>
    public LobbyView(LobbyScreens owner, ILobbyActions actions)
    {
        _owner = owner;
        _actions = actions;
    }

    /// <summary>The shell's parts, asleep: what stays of the frame, the column, the seats, the band, the countdown.</summary>
    public void Build(ShellParts parts)
    {
        _parts = parts;
        _state = _actions.Snapshot();
        _bar = new PromptBar(parts, Perform);
        LobbyFrame.HideTabGlyphs(parts);
        _band = LobbyFrame.BandLabel(parts);

        // Centred, and clear of the count at its left and the tag at its right (each 56 in and 280 or 260 wide): the line never runs into them.
        LobbyUi.Fill(_band.rectTransform, BandSide, 0, BandSide, 0);
        _count = LobbyUi.Label(LobbyFont.Title, parts.Tabs, "Count", 32, minSize: 26, align: TextAlignmentOptions.MidlineLeft);
        _count.rectTransform.anchorMin = new Vector2(0, 0);
        _count.rectTransform.anchorMax = new Vector2(0, 1);
        _count.rectTransform.pivot = new Vector2(0, 0.5f);
        _count.rectTransform.sizeDelta = new Vector2(280, 0);
        _count.rectTransform.anchoredPosition = new Vector2(56, 0);
        _tag = LobbyUi.Label(LobbyFont.Section, parts.Tabs, "Tag", 20, align: TextAlignmentOptions.MidlineRight, color: LobbyUi.Gold);
        _tag.rectTransform.anchorMin = new Vector2(1, 0);
        _tag.rectTransform.anchorMax = new Vector2(1, 1);
        _tag.rectTransform.pivot = new Vector2(1, 0.5f);
        _tag.rectTransform.sizeDelta = new Vector2(260, 0);
        _tag.rectTransform.anchoredPosition = new Vector2(-56, 0);
        // The canvas keeps its height, so its width follows the window's shape: a narrow window gives the column less, and the seats keep their room.
        float canvasWidth = 1080f * Screen.width / Math.Max(Screen.height, 1);
        _columnWidth = Mathf.Clamp(canvasWidth * 0.29f, ColumnMin, ColumnMax);
        LobbyFrame.FixedColumn(parts, _columnWidth);
        BuildBackdrop(parts);
        BuildColumn(parts);
        BuildSeatArea(parts);
        _pane = new DetailPane(parts);
        LobbyFrame.RowsContent(parts);
        _rowsParent = parts.ScrollContent.GetComponent<NavigableParent>();
        BuildCountdown(parts);
        ShowPanelParts(false);
    }

    /// <summary>The shell is showing: its parts are awake. The first draw, and the first focus by the table.</summary>
    public void Opened()
    {
        _awake = true;
        _bar.Wake();
        _mode.Wake();
        _save.Wake();
        BindClicks();
        _language = LobbyLocalization.Language().ToString(CultureInfo.InvariantCulture);
        _artVersion = NativeArt.Version;
        _state = _actions.Snapshot();
        Redraw(first: true);

        // The flow's half second after a return counts from the title being ready, and the shell comes after that: on a slow PC, all of it. People arrive mashing: it's the screen they're mashing at.
        // So do they from the row that opened it: a press in flight doesn't land on what's new (XAG 114): a seat's list, and from there a real invite to whoever's first in it.
        _lockedUntil = FrameClock.Seconds + (LobbyTiming.InputLockMicros / 1_000_000.0);
        LobbyFocus wanted = LobbyRules.DefaultFocus(LobbyScreen.Lobby, new LobbyScreenFacts { State = _state });
        SelectFocus(wanted);
    }

    /// <summary>Its shell's flow was backed out of: the cursor back on Start.</summary>
    public void BackedOut() => SelectFocus(_state.Role == LobbyRole.Host ? new LobbyFocus(LobbyFocusKind.StartRow) : LobbyRules.DefaultFocus(LobbyScreen.Lobby, new LobbyScreenFacts { State = _state }));

    /// <summary>Every frame while it's up: animations always, a redraw a few times a second.</summary>
    public void Tick(double now)
    {
        if (!_awake)
        {
            return;
        }

        foreach (SeatCard seat in _seats)
        {
            seat.Tick();
        }

        TickPop(now);
        if (_heldPress != null)
        {
            TickHeldPress();
        }

        AbstractNavigable? current = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        IntPtr pointer = current == null ? IntPtr.Zero : current.Pointer;
        bool moved = pointer != _cursor;
        if (moved)
        {
            _cursor = pointer;
            OnCursorMoved(current);
        }

        if (now < _redrawAt)
        {
            return;
        }

        _redrawAt = now + RedrawSeconds;
        LobbyState next = _actions.Snapshot();
        bool languageChanged = false;
        if (now >= _languageAt)
        {
            _languageAt = now + 0.5;
            string language = LobbyLocalization.Language().ToString(CultureInfo.InvariantCulture);
            languageChanged = !string.Equals(language, _language, StringComparison.Ordinal);
            _language = language;
        }

        if (_panelOpen && now >= _friendsAt)
        {
            _friendsAt = now + FriendsSeconds;
            RefreshFriends(now);
        }

        // An optional prefab has come since the last draw: its icons go on (the host's crown, a friend's lock).
        bool artChanged = _artVersion != NativeArt.Version;
        if (languageChanged)
        {
            _panelRowsStale = true;
        }

        if (languageChanged || artChanged || !Same(next, _state))
        {
            _artVersion = NativeArt.Version;
            _state = next;
            Redraw(first: false);
        }

        foreach (SeatCard seat in _seats)
        {
            seat.Refresh();
        }

        foreach (ListRow row in _friendRows.Values)
        {
            row.Refresh();
        }

        _pane.Refresh();
    }

    /// <summary>B, however it came. The table says what it does and whether it asks.</summary>
    public void Back()
    {
        if (LobbyShell.HandingOver || Locked)
        {
            return;
        }

        if (_panelOpen)
        {
            ClosePanel();
            return;
        }

        // The session's state now, as at Start: a friend who arrived a moment ago means Close asks first.
        LobbyBackRule rule = LobbyRules.Back(LobbyScreen.Lobby, new LobbyScreenFacts { State = _actions.Snapshot() });
        switch (rule.Action)
        {
            case LobbyBackAction.CancelStart:
                _actions.CancelStart();

                // Drawn with the press, not at the screen's next look: the number goes and the empty seats' plus comes back at once.
                _state = _actions.Snapshot();
                Redraw(first: false);
                SelectFocus(new LobbyFocus(LobbyFocusKind.StartRow));
                break;
            case LobbyBackAction.LeaveLobby when rule.Question != null:
                LobbyPrompts.Ask(LobbyLocalization.Say(_state.Role == LobbyRole.Host ? LobbyWords.CloseLobby.Key : LobbyWords.LeaveLobby.Key), LobbyLocalization.Say(rule.Question.ForRichText()), LobbyLocalization.Say(LobbyWords.Stay.Key), LobbyLocalization.Say(rule.OtherAnswerTerm), left =>
                {
                    _actions.Meter.LeavePromptAnswered(left);
                    if (left)
                    {
                        _owner.LeaveOrClose();
                    }
                });
                break;
            case LobbyBackAction.LeaveLobby:
                _owner.LeaveOrClose();
                break;
        }
    }

    /// <summary>Its shell has gone.</summary>
    public void Closed()
    {
        _awake = false;
        _heldPress = null;
        _bar?.Forget();
        foreach (SeatCard seat in _seats)
        {
            LobbyClicks.Forget(seat.Button);
            seat.Forget();
        }

        foreach (ListRow row in _friendRows.Values)
        {
            LobbyClicks.Forget(row.Button);
        }

        LobbyClicks.Forget(_invite);
        LobbyClicks.Forget(_start);
        if (_panelBack != null)
        {
            LobbyClicks.Forget(_panelBack.Button);
        }

        _seated.Clear();
        AvatarCache.SetSeated(null);
        _seats.Clear();
        _friendRows.Clear();
        _focusOf.Clear();
    }

    /// <summary>The tour: the cursor put on something, as the game's move would.</summary>
    public void FocusForCheck(in LobbyFocus focus) => SelectFocus(focus);

    /// <summary>The tour: the invite panel is up.</summary>
    public bool PanelOpen => _panelOpen;

    /// <summary>The tour: the cursor's place, as this screen knows it.</summary>
    public LobbyFocus Focus => _focus;

    /// <summary>The tour: how many friends the list has.</summary>
    public int FriendRowCount => _picker.Rows.Count;

    /// <summary>The tour: the save row is on screen.</summary>
    public bool SaveRowShown => _save.Root.gameObject.activeSelf;

    /// <summary>The tour: the game's shortcut of a prompt on the bar fires, as its button would. False when no such prompt is there.</summary>
    public bool TriggerForCheck(LobbyButton button) => _bar.TriggerForCheck(button);

    /// <summary>The tour: a prompt's button goes down, by the game's own press: what a human's does, held for as long as the tour likes.</summary>
    public bool PressButtonForCheck(LobbyButton button) => _bar.PressForCheck(button);

    /// <summary>The tour: the button goes up.</summary>
    public void ReleaseButtonForCheck(LobbyButton button) => _bar.ReleaseForCheck(button);

    /// <summary>The tour: a press of the prompt that does this.</summary>
    public void PressForCheck(LobbyAction action) => Perform(new LobbyPrompt(LobbyButton.Special, string.Empty, action));

    /// <summary>The room the controller screen is in, which comes next: the game's arena and its foreground, behind the frame.</summary>
    private static void BuildBackdrop(ShellParts parts)
    {
        Transform back = parts.Background.parent;
        GameObject arena = LobbyUi.Part(LobbyUi.Controllers, "BackgroundArena", back, ButtonKeep, "Arena");
        LobbyUi.Fill(LobbyUi.RectOf(arena));
        GameObject front = LobbyUi.Part(LobbyUi.Controllers, "Background", back, ButtonKeep, "Foreground");
        LobbyUi.Fill(LobbyUi.RectOf(front));

        // Dimmed, as the game's prompts dim what's behind them: the frame's words stay on something calm.
        Image shade = LobbyUi.Picture(back, "Shade", null, new Color(0.02f, 0.05f, 0.08f, 0.68f));
        LobbyUi.Fill(shade.rectTransform);
    }

    private void BuildColumn(ShellParts parts)
    {
        // The list's own place is the column's: its dark well stays, its scrolling content is for the invite panel.
        _column = LobbyUi.New(parts.ScrollView, "Column");
        LobbyUi.Fill(_column);
        _mode = Selector.Make(_column, "GameMode", OnModeChosen);
        _save = Selector.Make(_column, "CoopSave", OnSlotChosen);
        LobbyUi.TopBand(_mode.Root, ModeTop, SelectorHeight, ColumnSide);
        LobbyUi.TopBand(_save.Root, ModeTop + SelectorHeight + RowGap, SelectorHeight, ColumnSide);
        _saveBox = LobbyUi.New(_column, "SaveBox");
        LobbyUi.TopBand(_saveBox, ModeTop + (2 * SelectorHeight) + (2 * RowGap), SaveBoxHeight, ColumnSide);
        Image box = LobbyUi.Picture(_saveBox, "Well", NativeArt.Get(NativeArt.Panel), LobbyUi.Hex(0x1B2D37, 0.88f), sliced: true);
        LobbyUi.Fill(box.rectTransform);
        _saveWords = LobbyUi.Label(LobbyFont.Body, _saveBox, "Words", 26, align: TextAlignmentOptions.TopLeft, color: LobbyUi.BodyText);
        LobbyUi.Fill(_saveWords.rectTransform, 16, 8, 16, 10);
        _saveWords.textWrappingMode = TextWrappingModes.Normal;
        _saveWords.overflowMode = TextOverflowModes.Ellipsis;

        // The Start frame, the game's own: sticks and puck behind the button. It closes the column at its bottom, in the same place in every mode and for every word
        // Start shows, so the column reads as one panel from Game Mode down to Start; Invite and a friend's note come with the mode (PlaceActions), right under the rows.
        _startFrame = LobbyUi.New(_column, "StartArea");
        LobbyUi.BottomBand(_startFrame, StartBottom, StartHeight + 24, ColumnSide);
        GameObject frame = LobbyUi.Part(LobbyUi.Controllers, "ScaledContainer/Content/BottomInputsPlayNow/StartContainer", _startFrame, ButtonKeep, "StartContainer");
        UiClone.Destroy(frame.transform.Find("StartButton")?.gameObject);
        UiClone.Destroy(frame.transform.Find("StartButtonOff")?.gameObject);
        UiClone.Destroy(frame.GetComponent<LayoutElement>());
        RectTransform frameRect = LobbyUi.RectOf(frame);
        LobbyUi.At(frameRect, 0.5f, 0, 0, 0, 736, 112, 0.5f, 0);
        float frameScale = (_columnWidth - 16) / 736f;
        frameRect.localScale = new Vector3(frameScale, frameScale, 1);
        _start = MakeButton(_startFrame, "Start", out _startWord, "UI_UIStart");
        LobbyUi.At(LobbyUi.RectOf(_start.gameObject), 0.5f, 0, 0, 30, _columnWidth * 0.64f, ButtonHeight, 0.5f, 0);
        _invite = MakeButton(_column, "Invite", out _inviteWord, "UI_UISpecial2");

        // A friend's rows are the host's: a lock and the word, instead of the host's buttons.
        _friendNote = LobbyUi.New(_column, "FriendNote");
        _friendLock = LobbyUi.Picture(_friendNote, "Lock", NativeArt.Get(NativeArt.Lock), LobbyUi.White);
        LobbyUi.At(_friendLock.rectTransform, 0, 1, 4, -4, 36, 36, 0, 1);
        _friendNoteWord = LobbyUi.Label(LobbyFont.Body, _friendNote, "Word", 26, align: TextAlignmentOptions.MidlineLeft, color: LobbyUi.BodyText);
        LobbyUi.TopBand(_friendNoteWord.rectTransform, 0, 44);
        _friendNoteWord.margin = new Vector4(48, 0, 0, 0);
        _waitWord = LobbyUi.Label(LobbyFont.Button, _friendNote, "Wait", 36, minSize: 26, align: TextAlignmentOptions.Center);
        LobbyUi.BottomBand(_waitWord.rectTransform, 0, 92);
        _waitWord.textWrappingMode = TextWrappingModes.Normal;
    }

    /// <summary>A column's button: the game's button with its glyph; the game's own shortcut presses it.</summary>
    private static AnimatedButton MakeButton(Transform parent, string name, out TextMeshProUGUI word, string action)
    {
        GameObject root = LobbyUi.Part(LobbyUi.Settings, "ScaledContainer/Content/Items/ConfirmButton/Button_Confirm", parent, ButtonKeep, name);
        AnimatedButton button = root.GetComponent<AnimatedButton>() ?? throw new InvalidOperationException("the button has no button");
        button.ClearNeighbors();
        Transform text = root.transform.Find("Container/Content/Text") ?? throw new InvalidOperationException("the button has no text");
        Il2CppI2.Loc.Localize? localize = text.GetComponent<Il2CppI2.Loc.Localize>();
        if (localize != null)
        {
            UiClone.Destroy(localize);
        }

        word = text.GetComponent<TextMeshProUGUI>() ?? throw new InvalidOperationException("the button's text isn't text");
        word.fontSizeMin = 26;
        ActionOf[button.Pointer] = action;
        return button;
    }

    private static readonly Dictionary<IntPtr, string> ActionOf = new();

    private void BuildSeatArea(ShellParts parts)
    {
        // The frame's panel and wells stay for the invite panel; the seats have the whole of it.
        _seatArea = LobbyUi.New(parts.Details, "Seats");
        LayoutElement layout = _seatArea.gameObject.AddComponent(Il2CppType.Of<LayoutElement>())?.TryCast<LayoutElement>() ?? throw new InvalidOperationException("the seats can't lay out");
        layout.ignoreLayout = true;
        LobbyUi.Fill(_seatArea, 0, 0, 0, 0);
        BuildSeats(Math.Max(_state.Seats.Count, 1));
    }

    private void BuildSeats(int capacity)
    {
        foreach (SeatCard seat in _seats)
        {
            LobbyClicks.Forget(seat.Button);
            seat.Forget();
            UiClone.Destroy(seat.Root);
        }

        foreach (RectTransform divider in _dividers)
        {
            UiClone.Destroy(divider.gameObject);
        }

        _seats.Clear();
        _dividers.Clear();
        _lastSeats = new LobbySeat?[capacity];
        _capacity = capacity;
        SeatLayout layout = LobbyRules.LayoutFor(capacity);
        int columns = Math.Max(layout.Columns, 1);
        int rows = Math.Max(layout.Rows, 1);
        for (int i = 0; i < capacity; i++)
        {
            SeatCard card = SeatCard.Make(_seatArea, i, layout);
            int column = i % columns;
            int row = i / columns;
            RectTransform rect = LobbyUi.RectOf(card.Root);
            rect.anchorMin = new Vector2((float)column / columns, 1f - ((row + 1f) / rows));
            rect.anchorMax = new Vector2((column + 1f) / columns, 1f - ((float)row / rows));
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(CardGap / 2, GridRowGap / 2);
            rect.offsetMax = new Vector2(-CardGap / 2, -GridRowGap / 2);
            _seats.Add(card);
        }

        // Lightning between the splits: the controller screen's bolt, black over its two pink echoes.
        for (int i = 1; i < columns; i++)
        {
            GameObject bolt = LobbyUi.Part(LobbyUi.Controllers, "ScaledContainer/Content/Lightning", _seatArea, ButtonKeep, $"Lightning{i}");
            RectTransform rect = LobbyUi.RectOf(bolt);
            rect.anchorMin = new Vector2((float)i / columns, 0);
            rect.anchorMax = new Vector2((float)i / columns, 1);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(rows > 1 ? 40 : 56, -6);
            foreach (string part in new[] { "Lightning_Left", "Lightning_Right" })
            {
                RectTransform? echo = bolt.transform.Find(part)?.GetComponent<RectTransform>();
                if (echo != null)
                {
                    echo.anchoredPosition = new Vector2(echo.anchoredPosition.x * 0.22f, echo.anchoredPosition.y * 0.1f);
                }
            }

            _dividers.Add(rect);
        }
    }

    private void BuildCountdown(ShellParts parts)
    {
        // Over everything but the bar: its Cancel stays bright.
        _countdown = LobbyUi.New(parts.Scaled, "Countdown");
        LobbyUi.Fill(_countdown);
        _countdown.SetSiblingIndex(parts.BottomBar.GetSiblingIndex());
        Image shade = LobbyUi.Picture(_countdown, "Shade", null, new Color(0, 0, 0, 0.62f));
        LobbyUi.Fill(shade.rectTransform, -200, 0, -200, 0);
        // The number's box stays above the word's even at its pop's largest (1.55 times, TickPop: 220 x 1.55 = 341, so its bottom is 110 below the screen's centre and
        // the word's top 122): the two never share room, at any size. The digit overflows its box, drawn where a taller box would put it (the same centre).
        _countdownNumber = LobbyUi.Label(LobbyFont.Button, _countdown, "Number", 240, align: TextAlignmentOptions.Center);
        LobbyUi.At(_countdownNumber.rectTransform, 0.5f, 0.5f, 0, 60, 600, 220);
        _countdownNumber.overflowMode = TextOverflowModes.Overflow;
        _countdownWord = LobbyUi.Label(LobbyFont.Title, _countdown, "Word", 64, minSize: 32, align: TextAlignmentOptions.Center);
        LobbyUi.At(_countdownWord.rectTransform, 0.5f, 0.5f, 0, -170, 1200, 96);
        LobbyUi.Show(_countdown, false);
    }

    private void BindClicks()
    {
        _focusOf.Clear();
        for (int i = 0; i < _seats.Count; i++)
        {
            int seat = i;
            LobbyClicks.Bind(_seats[i].Button, () => OnSeatClick(seat));
            _focusOf[_seats[i].Button.Pointer] = new LobbyFocus(LobbyFocusKind.Seat, i);
        }

        _focusOf[_mode.List.Pointer] = new LobbyFocus(LobbyFocusKind.ModeRow);
        _focusOf[_save.List.Pointer] = new LobbyFocus(LobbyFocusKind.SaveRow);
        _focusOf[_invite.Pointer] = new LobbyFocus(LobbyFocusKind.InviteRow);
        _focusOf[_start.Pointer] = new LobbyFocus(LobbyFocusKind.StartRow);
        LobbyClicks.Bind(_invite, () => Perform(new LobbyPrompt(LobbyButton.Special2, LobbyWords.Invite.Key, LobbyAction.OpenInvitePanel)));
        LobbyClicks.Bind(_start, () => Perform(new LobbyPrompt(LobbyButton.Start, LobbyTerms.Start, LobbyAction.Start)));
        BindShortcut(_invite);
        BindShortcut(_start);
        _inviteKey = _invite.GetComponent<UIInputShortcutBase>();
    }

    private static void BindShortcut(AnimatedButton button)
    {
        if (ActionOf.TryGetValue(button.Pointer, out string? action))
        {
            button.GetComponent<UIInputShortcutBase>()?.RefreshInput(new InputActionsCatalogReference(action));
            ActionOf.Remove(button.Pointer);
        }
    }

    /// <summary>The whole screen from the state.</summary>
    private void Redraw(bool first)
    {
        // A Start began with the list open (a wait that came true): the list is no place to see the countdown from, and B must cancel.
        if (_panelOpen && _state.Phase != LobbyPhase.Open)
        {
            ClosePanel();
            return;
        }

        if (_state.Seats.Count != _capacity && _state.Seats.Count > 0)
        {
            BuildSeats(_state.Seats.Count);
            if (_awake)
            {
                BindClicks();
            }
        }

        bool host = _state.Role == LobbyRole.Host;
        DrawSeats(first);
        if (_panelOpen)
        {
            DrawPanel();
        }
        else
        {
            DrawColumn(host);
        }

        DrawBand();
        DrawCountdown();
        Relink();
        if (!_panelOpen)
        {
            RepairFocus(first);
        }

        DrawPrompts();
        NoteActivity();
    }

    private void DrawSeats(bool first)
    {
        bool host = _state.Role == LobbyRole.Host;
        bool covered = CountdownShown;
        int friends = 0;
        int ready = 0;
        for (int i = 0; i < _seats.Count && i < _state.Seats.Count; i++)
        {
            LobbySeat seat = _state.Seats[i];
            LobbySeat? was = _lastSeats[i];

            // Everyone who can mark themselves ready: the host has no ready mark to give (the table's prompts), and starts when they like.
            if (!seat.IsHost && !seat.IsEmpty && seat.Stage != SeatStage.Invited)
            {
                friends++;
                ready += seat.Ready ? 1 : 0;
            }

            bool focused = _focus.Kind == LobbyFocusKind.Seat && _focus.Index == i;
            _seats[i].Show(seat, LobbyScreens.ColourOf(seat.PlayerColor), focused, host, covered);
            _seats[i].Button.IsNavigable = LobbyRules.CanFocusSeat(_state, i);
            if (!first && was != null)
            {
                Moment(i, was, seat);
            }

            _lastSeats[i] = seat;
        }

        NoteSeated();
        bool allReady = friends >= 1 && ready == friends;
        if (allReady && !_allReadySaid && !first)
        {
            LobbySounds.Play(LobbySounds.AllReady);
        }

        _allReadySaid = allReady;
        LobbyUi.Say(_count, LobbyLine.Game(LobbyTerms.PlayersOf, LobbyWords.Number(_state.Players), LobbyWords.Number(_state.Seats.Count)));
        LobbyUi.Show(_tag, _state.Experimental);
        LobbyUi.Say(_tag, _state.Experimental ? new LobbyLine(LobbyWords.Experimental) : null);
    }

    /// <summary>Who sits here, for the avatar cache: theirs are the last to go, and a changed picture comes.</summary>
    private void NoteSeated()
    {
        int at = 0;
        bool same = true;
        foreach (LobbySeat seat in _state.Seats)
        {
            if (seat.IsEmpty || seat.SteamId == 0)
            {
                continue;
            }

            same &= at < _seated.Count && _seated[at] == seat.SteamId;
            at++;
        }

        if (same && at == _seated.Count)
        {
            return;
        }

        _seated.Clear();
        foreach (LobbySeat seat in _state.Seats)
        {
            if (!seat.IsEmpty && seat.SteamId != 0)
            {
                _seated.Add(seat.SteamId);
            }
        }

        AvatarCache.SetSeated(_seated);
    }

    /// <summary>What changed on a seat: an arrival's slam and sound, a leaving's sound, a ready mark's. Nothing here moves the cursor or waits.</summary>
    private void Moment(int index, LobbySeat was, LobbySeat now)
    {
        bool arrived = now.Stage == SeatStage.Here && was.Stage != SeatStage.Here && was.Stage != SeatStage.Reconnecting && was.Stage != SeatStage.Loading;
        if (arrived)
        {
            _seats[index].Slam();
            LobbySounds.Play(LobbySounds.Arrived);
        }
        else if (now.IsEmpty && !was.IsEmpty && was.Stage != SeatStage.Invited)
        {
            LobbySounds.Play(LobbySounds.Left);
        }

        // A mark going as its owner comes home from a game (OnlineSession.EndPlay) isn't someone taking it back: no sound for that.
        if (now.Ready != was.Ready && !now.IsEmpty && (now.Ready || was.Stage == SeatStage.Here))
        {
            LobbySounds.Play(now.Ready ? LobbySounds.Ready : LobbySounds.Unready);
        }
    }

    private void DrawColumn(bool host)
    {
        bool campaign = _state.Mode == LobbyMode.Campaign;
        _mode.SetLabel(LobbyTerms.GameMode);
        _mode.Set(new[] { LobbyLocalization.Say(LobbyTerms.Campaign), LobbyLocalization.Say(LobbyTerms.QuickGame) }, campaign ? 0 : 1);
        _mode.SetReadOnly(!host);
        LobbyUi.Show(_save.Root, campaign);
        LobbyUi.Show(_saveBox, campaign);
        PlaceActions(campaign);
        if (campaign)
        {
            _save.SetLabel(LobbyWords.CoopSave.Key);
            IReadOnlyList<LobbySaveRow> slots = host ? SlotNumbers() : Array.Empty<LobbySaveRow>();
            var names = new string[Math.Max(slots.Count, 1)];
            int index = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int slot = i < slots.Count ? slots[i].Slot : Math.Max((int)_state.Save.Slot, 1);
                names[i] = LobbyLocalization.Say(new LobbyLine(LobbyWords.Slot, LobbyWords.Number(slot)));
                index = slot == _state.Save.Slot ? i : index;
            }

            _save.Set(names, index);
            _save.SetReadOnly(!host);
            LobbyUi.Set(_saveWords, SaveBox(_state.Save));
        }

        LobbyUi.Show(LobbyUi.RectOf(_start.gameObject), host);
        LobbyUi.Show(_startFrame, host);
        bool empty = LobbyRules.HasRoomToInvite(_state);
        LobbyUi.Show(LobbyUi.RectOf(_invite.gameObject), host && empty);

        // The game's shortcut selects its button as it fires: dead under the countdown and the wait, or Y takes the cursor off Start, where B's table says it stays.
        if (_inviteKey != null)
        {
            _inviteKey.enabled = host && empty && _state.Phase == LobbyPhase.Open && !_state.WaitingForEveryone;
        }

        LobbyUi.Say(_inviteWord, LobbyWords.InviteFriends.Key);
        LobbyUi.Say(_startWord, LobbyRules.StartTerm(LobbyRules.StartKindOf(_state.Mode, _state.Save)));
        _startWord.color = _state.StartAllowed || _state.Phase != LobbyPhase.Open ? LobbyUi.White : LobbyUi.Disabled;
        _start.IsNavigable = host;
        LobbyUi.Show(_friendNote, !host);
        if (!host)
        {
            // The lock's prefab is optional and loads by itself: it may come after the shell. An image with no sprite is a white square.
            Sprite? locked = NativeArt.Get(NativeArt.Lock);
            _friendLock.sprite = locked;
            LobbyUi.Show(_friendLock, locked != null);
            LobbyUi.Say(_friendNoteWord, LobbyWords.HostChooses.Key);
            LobbyUi.Say(_waitWord, LobbyWords.WaitingHost.Key);
        }
    }

    /// <summary>
    /// Invite Friends under the column's last block (the mode, or in the campaign the save and its box), as the game's own Game Settings stacks its rows from the top; on a friend's,
    /// the host's note there instead. Start isn't placed here: it sits at the column's bottom in every mode (<see cref="BuildColumn"/>), so neither a mode change nor an arrival that
    /// hides Invite (a full lobby) moves it under the cursor. Only when the mode changes.
    /// </summary>
    private void PlaceActions(bool campaign)
    {
        int placeFor = campaign ? 1 : 0;
        if (placeFor == _placedFor)
        {
            return;
        }

        _placedFor = placeFor;
        float top = ModeTop + SelectorHeight + (campaign ? (2 * RowGap) + SelectorHeight + SaveBoxHeight : 0) + ActionsGap;
        LobbyUi.TopBand(LobbyUi.RectOf(_invite.gameObject), top, ButtonHeight, ColumnSide);
        LobbyUi.TopBand(_friendNote, top, 148, ColumnSide);
    }

    /// <summary>The selector's slots. Only their numbers are used here, and they don't change; the save's own row comes with the state. Asked once: each ask reads three files' stamps, and a redraw comes with every ping.</summary>
    private IReadOnlyList<LobbySaveRow> SlotNumbers() => _slots ??= _actions.Slots();

    /// <summary>The save's description, as the squad screen pairs a choice with its text: the squad, act and floor, when it was saved; or New Run for an empty slot.</summary>
    private static string SaveBox(in LobbySaveRow save)
    {
        if (!save.HasRun)
        {
            return LobbyLocalization.Say(LobbyTerms.NewRun);
        }

        string line = BrowserScreen.SquadName(save.SquadId) + "\n" +
            LobbyLocalization.Say(LobbyLine.Game(LobbyTerms.ActN, LobbyWords.Number(save.Act))) + LobbyWords.Separator + LobbyLocalization.Say(new LobbyLine(LobbyWords.FloorN, LobbyWords.Number(save.Floor)));
        if (save.SavedAtUnixSeconds > 0)
        {
            string when = DateTimeOffset.FromUnixTimeSeconds(save.SavedAtUnixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            line += "\n" + LobbyLocalization.Say(new LobbyLine(LobbyWords.SavedAt, when));
        }

        return line;
    }

    private void DrawBand()
    {
        LobbyUi.Say(_band, LobbyRules.BandLine(_state, _panelOpen, out bool news));
        _band.color = news ? LobbyUi.Gold : LobbyUi.White;
    }

    /// <summary>
    /// The countdown is drawn over the seats: from Start to the hand-over. Read by the seats in the same redraw, so an empty seat's plus and word go and come back in the frame
    /// the number comes and goes: a cancel, the countdown's end, or a countdown started again.
    /// </summary>
    private bool CountdownShown => _state.Phase is LobbyPhase.Counting or LobbyPhase.Starting or LobbyPhase.Playing;

    private void DrawCountdown()
    {
        bool counting = _state.Phase == LobbyPhase.Counting;
        bool starting = _state.Phase is LobbyPhase.Starting or LobbyPhase.Playing;
        LobbyUi.Show(_countdown, CountdownShown);
        int number = counting ? LobbyRules.CountdownNumber(_state.CountdownMs) : 0;
        if (number != _shownNumber)
        {
            _shownNumber = number;
            if (number > 0)
            {
                _popAt = FrameClock.Seconds;
                LobbySounds.Play(LobbySounds.CountdownStep);
            }
        }

        if (counting || starting)
        {
            LobbyUi.Set(_countdownNumber, number > 0 ? number.ToString(CultureInfo.InvariantCulture) : string.Empty);
            LobbyUi.Say(_countdownWord, new LobbyLine(LobbyWords.GameStarting));
        }

        if (starting && _soundedEpoch != _state.Epoch)
        {
            _soundedEpoch = _state.Epoch;
            LobbySounds.GameStarting();
        }
    }

    private void TickPop(double now)
    {
        if (_popAt < 0)
        {
            return;
        }

        float t = (float)((now - _popAt) / PopSeconds);
        if (t >= 1)
        {
            _popAt = -1;
            _countdownNumber.rectTransform.localScale = Vector3.one;
            return;
        }

        float scale = 1 + (0.55f * (1 - t) * (1 - t));
        _countdownNumber.rectTransform.localScale = new Vector3(scale, scale, 1);
    }

    /// <summary>The bottom bar: the table's prompts for this role and focus, less what a button on screen answers with its own glyph.</summary>
    private void DrawPrompts()
    {
        var facts = new LobbyScreenFacts { State = _state, OverlayOn = LobbyScreens.OverlayOn(), FriendRows = _picker.Rows.Count, FocusedFriendInvitable = FocusedRowOf(_picker)?.CanInvite ?? false };
        if (_panelOpen)
        {
            LobbyFocus focus = _picker.IsEmpty ? new LobbyFocus(LobbyFocusKind.PanelBack) : new LobbyFocus(LobbyFocusKind.FriendRow, _picker.FocusIndex);
            _bar.Show(LobbyRules.Prompts(LobbyScreen.InvitePanel, focus, facts));
            return;
        }

        IReadOnlyList<LobbyPrompt> prompts = LobbyRules.Prompts(LobbyScreen.Lobby, _focus, facts);
        _bar.Show(prompts, LobbyButton.Special2, LobbyButton.Start);
    }

    private static PickerRow? FocusedRowOf(FriendPickerRows picker) => picker.FocusIndex >= 0 && picker.FocusIndex < picker.Rows.Count ? picker.Rows[picker.FocusIndex] : null;

    /// <summary>Every link by the table's <see cref="LobbyRules.Move"/>. A selector keeps its left and right empty: its own dead-end handler turns them into a change of value.</summary>
    private void Relink()
    {
        if (_panelOpen)
        {
            return;
        }

        foreach (SeatCard seat in _seats)
        {
            seat.Button.ClearNeighbors();
        }

        for (int i = 0; i < _seats.Count; i++)
        {
            Link(_seats[i].Button, new LobbyFocus(LobbyFocusKind.Seat, i), true);
        }

        if (_state.Role != LobbyRole.Host)
        {
            return;
        }

        Link(_mode.List, new LobbyFocus(LobbyFocusKind.ModeRow), false);
        Link(_save.List, new LobbyFocus(LobbyFocusKind.SaveRow), false);
        Link(_invite, new LobbyFocus(LobbyFocusKind.InviteRow), true);
        Link(_start, new LobbyFocus(LobbyFocusKind.StartRow), true);
    }

    private void Link(AbstractNavigable navigable, in LobbyFocus focus, bool sideways)
    {
        navigable.ClearNeighbors();

        // The lobby doesn't wrap (the table): by default the game's navigation takes a dead end up or down round to the far end of the chain (verified in the game's code: MoveInDirection,
        // FindWrapAroundNeighbor).
        navigable.AllowNavigationWrapAround = false;
        foreach ((LobbyDirection direction, AbstractNavigable.EDirection game) in new[]
        {
            (LobbyDirection.Up, AbstractNavigable.EDirection.Up),
            (LobbyDirection.Down, AbstractNavigable.EDirection.Down),
            (LobbyDirection.Left, AbstractNavigable.EDirection.Left),
            (LobbyDirection.Right, AbstractNavigable.EDirection.Right),
        })
        {
            if (!sideways && direction is LobbyDirection.Left or LobbyDirection.Right)
            {
                continue;
            }

            LobbyFocus next = LobbyRules.Move(focus, direction, _state);
            AbstractNavigable? target = next == focus ? null : NavigableOf(next);
            if (target != null)
            {
                navigable.SetNeighbor(game, target);
            }
        }
    }

    private AbstractNavigable? NavigableOf(in LobbyFocus focus) => focus.Kind switch
    {
        LobbyFocusKind.Seat when focus.Index < _seats.Count => _seats[focus.Index].Button,
        LobbyFocusKind.ModeRow => _mode.List,
        LobbyFocusKind.SaveRow => _save.List,
        LobbyFocusKind.InviteRow => _invite,
        LobbyFocusKind.StartRow => _start,
        _ => null,
    };

    /// <summary>Focus put right after a change, by the table: a focus still good stays, which is what keeps an arrival from moving the cursor. A repair is said in the log and counted: any is worth a look.</summary>
    private void RepairFocus(bool first)
    {
        // Under a hand-over the cursor is the game's next menu's (or the host's mirrored one): a seat changing under it mustn't select a row of this shell. A back-out selects again (BackedOut).
        if (first || LobbyShell.HandingOver)
        {
            return;
        }

        if (_focus.Kind == LobbyFocusKind.Unchanged)
        {
            // Nothing could take focus when it opened (a friend's first board hadn't come): now something may.
            SelectFocus(LobbyRules.DefaultFocus(LobbyScreen.Lobby, new LobbyScreenFacts { State = _state }));
            return;
        }

        LobbyFocus repaired = LobbyRules.Repair(_focus, _state);
        if (repaired != _focus)
        {
            ModLog.Info($"Lobby: focus repaired: {_focus.Kind} {_focus.Index} > {repaired.Kind} {repaired.Index} ({_state.Role}, {_state.Players} of {_state.Seats.Count}).");
            _actions.Meter.FocusRepaired();
            SelectFocus(repaired);
        }
    }

    private void SelectFocus(in LobbyFocus focus)
    {
        AbstractNavigable? target = NavigableOf(focus);
        if (target == null)
        {
            return;
        }

        _focus = focus;
        LobbyShell.SetDefaultNavigables(target);
        AbstractNavigableUtils.SelectOnNextFrame(target, AbstractNavigable.ESelectFx.Default);
    }

    private void OnCursorMoved(AbstractNavigable? current)
    {
        if (current == null)
        {
            return;
        }

        if (_panelOpen)
        {
            foreach (KeyValuePair<ulong, ListRow> row in _friendRows)
            {
                if (row.Value.Button.Pointer == current.Pointer)
                {
                    _picker.Focus(row.Key);
                    DrawPanelPane();
                    DrawPrompts();
                    LobbyShell.SetDefaultNavigables(row.Value.Button);
                    return;
                }
            }

            return;
        }

        if (_focusOf.TryGetValue(current.Pointer, out LobbyFocus focus))
        {
            _focus = focus;
            LobbyShell.SetDefaultNavigables(current);
            DrawSeats(first: true);
            DrawPrompts();
            NoteActivity();
        }
    }

    private void NoteActivity()
    {
        if (_state.Role != LobbyRole.Host)
        {
            return;
        }

        HostActivity activity = _panelOpen ? HostActivity.Inviting : _focus.Kind switch
        {
            LobbyFocusKind.ModeRow => HostActivity.PickingMode,
            LobbyFocusKind.SaveRow => HostActivity.PickingSave,
            _ => HostActivity.None,
        };
        if (activity != _activity)
        {
            _activity = activity;
            _actions.NoteActivity(activity);
        }
    }

    private void OnSeatClick(int index)
    {
        if (LobbyShell.HandingOver || Locked || index >= _state.Seats.Count)
        {
            return;
        }

        _focus = new LobbyFocus(LobbyFocusKind.Seat, index);
        var facts = new LobbyScreenFacts { State = _state, OverlayOn = LobbyScreens.OverlayOn() };
        foreach (LobbyPrompt prompt in LobbyRules.Prompts(LobbyScreen.Lobby, _focus, facts))
        {
            if (prompt.Button == LobbyButton.Confirm)
            {
                Perform(prompt);
                return;
            }
        }

        // A card nothing happens on: the game's own answer to such a press is no sound (its click's sound reads this after the handlers; verified in the game's code: ForceClick).
        _seats[index].Button.m_ClickResult = AbstractNavigable.EClickResult.Ignored;
    }

    private void OnModeChosen(int index) => Perform(new LobbyPrompt(LobbyButton.Confirm, LobbyTerms.GameMode, index == 0 ? LobbyAction.ModeLeft : LobbyAction.ModeRight));

    private void OnSlotChosen(int index)
    {
        if (_state.Role != LobbyRole.Host)
        {
            return;
        }

        IReadOnlyList<LobbySaveRow> slots = SlotNumbers();
        if (index >= 0 && index < slots.Count && !_actions.SetSlot(slots[index].Slot))
        {
            LobbySounds.Play(LobbySounds.Grunt);
            DrawColumn(host: true); // the selector has moved by itself: back to what's true
        }
    }

    /// <summary>A press: what the table's prompt asks. Dead while a hand-over runs and in the half second after a return.</summary>
    private void Perform(LobbyPrompt prompt)
    {
        if (LobbyShell.HandingOver || Locked || WaitsForModifier(prompt))
        {
            return;
        }

        switch (prompt.Action)
        {
            case LobbyAction.OpenInvitePanel:
                // The Invite button and its shortcut stay up behind the countdown and the wait, where the table has only B: its Y does nothing there, as the bar says.
                if (_state.Role == LobbyRole.Host && _state.Phase == LobbyPhase.Open && !_state.WaitingForEveryone && LobbyRules.HasRoomToInvite(_state))
                {
                    OpenPanel();
                }
                else
                {
                    _invite.m_ClickResult = AbstractNavigable.EClickResult.Ignored;
                }

                break;
            case LobbyAction.Invite:
                InviteFocused();
                break;
            case LobbyAction.InviteOverlay:
                if (!_actions.OpenInviteOverlay())
                {
                    LobbySounds.Play(LobbySounds.Grunt);
                }

                break;
            case LobbyAction.OpenProfile:
                if (FocusedSeat() is { IsEmpty: false } profile && !_actions.OpenProfile(profile.SteamId))
                {
                    LobbySounds.Play(LobbySounds.Grunt);
                }

                break;
            case LobbyAction.Remove:
                AskRemove();
                break;
            case LobbyAction.Start:
                TryStart(newRun: false);
                break;
            case LobbyAction.NewRun:
                TryStart(newRun: true);
                break;
            case LobbyAction.CancelStart:
            case LobbyAction.Back:
                Back();
                break;
            case LobbyAction.ModeLeft:
            case LobbyAction.ModeRight:
                ChangeMode(prompt);
                break;
            case LobbyAction.ToggleReady:
                _actions.ToggleReady();
                break;
            case LobbyAction.OpenSettings:
                _actions.OpenOwnSettings();
                break;
        }
    }

    /// <summary>True when this press came with a key the system also uses with others, and has to wait to see what it was for (<see cref="ModifierPress"/>). The game's keyboard map; a remap away from these keys acts at once.</summary>
    private bool WaitsForModifier(in LobbyPrompt prompt)
    {
        Keyboard? keyboard = ReInput.isReady ? ReInput.controllers.Keyboard : null;
        KeyCode key = prompt.Action switch
        {
            LobbyAction.OpenSettings when keyboard != null && keyboard.GetKey(KeyCode.LeftAlt) => KeyCode.LeftAlt,
            LobbyAction.Remove when keyboard != null && keyboard.GetKey(KeyCode.LeftControl) => KeyCode.LeftControl,
            LobbyAction.OpenInvitePanel when keyboard != null && keyboard.GetKey(KeyCode.LeftShift) => KeyCode.LeftShift,
            LobbyAction.OpenInvitePanel when keyboard != null && keyboard.GetKey(KeyCode.RightShift) => KeyCode.RightShift,
            _ => KeyCode.None,
        };
        if (key == KeyCode.None)
        {
            return false;
        }

        _heldPress = prompt;
        _heldKey = key;
        _heldFrame = FrameClock.Frame;
        return true;
    }

    private void TickHeldPress()
    {
        if (_heldPress is not { } prompt || FrameClock.Frame == _heldFrame)
        {
            return;
        }

        Keyboard keyboard = ReInput.controllers.Keyboard;
        switch (ModifierPress.Step(keyboard.GetKey(_heldKey), keyboard.GetAnyButtonDown(), Application.isFocused))
        {
            case ModifierStep.Act:
                _heldPress = null;
                Perform(prompt);
                break;
            case ModifierStep.Drop:
                _heldPress = null;
                break;
        }
    }

    private void ChangeMode(in LobbyPrompt prompt)
    {
        // The session's state now, not the screen's last look at it (15 times a second): L then R inside one look, or a click on each
        // glyph, must go there and back; read from the copy, the second press asked for the mode the first had just set, and was lost.
        LobbyState live = _actions.Snapshot();
        if (live.Role != LobbyRole.Host || live.Phase != LobbyPhase.Open)
        {
            DrawColumn(_state.Role == LobbyRole.Host); // a selector's change that doesn't count: back to what's true
            return;
        }

        // A press of L or R, or the selector's own change: only two are listed, and the state has the last word.
        LobbyMode mode = LobbyRules.ModeAfter(prompt.Button, prompt.Action, live.Mode);
        if (mode != live.Mode)
        {
            if (prompt.Button != LobbyButton.Confirm)
            {
                LobbySounds.Play(LobbySounds.ModeChanged);
            }

            _actions.SetMode(mode);
        }
    }

    private void TryStart(bool newRun)
    {
        // The session's state now, not the screen's last look at it (15 times a second): a friend who arrived a moment ago is here, and isn't worth a question.
        // A local: the screen's own _state is what it last drew, and its next look must still see the change.
        LobbyState live = _actions.Snapshot();
        if (!live.StartAllowed)
        {
            LobbySounds.Play(LobbySounds.Grunt);
            _start.m_ClickResult = AbstractNavigable.EClickResult.Ignored; // the grunt says it: not the click's sound as well
            return;
        }

        LobbyLine? notHere = LobbyRules.NotHereLine(live);
        if (notHere == null)
        {
            _actions.Start(newRun, false);
            return;
        }

        // Titled by what it's about, not by the press: "Start" over a question reads as an instruction.
        LobbyPrompts.Ask(LobbyLocalization.Say(LobbyWords.Lobby.Key), LobbyLocalization.Say(notHere.ForRichText()), LobbyLocalization.Say(LobbyWords.Wait.Key), LobbyLocalization.Say(LobbyWords.StartAnyway.Key), anyway => _actions.Start(newRun, anyway));
    }

    private void AskRemove()
    {
        if (FocusedSeat() is not { IsEmpty: false, IsHost: false } seat || _state.Role != LobbyRole.Host)
        {
            return;
        }

        ulong id = seat.SteamId;
        LobbyPrompts.Ask(LobbyLocalization.Say(LobbyWords.Remove.Key), LobbyLocalization.Say(new LobbyLine(LobbyWords.AskRemove, seat.Name).ForRichText()), LobbyLocalization.Say(LobbyTerms.Cancel), LobbyLocalization.Say(LobbyWords.Remove.Key), remove =>
        {
            if (remove)
            {
                _actions.Remove(id);
            }
        });
    }

    private bool Locked => _state.InputLocked || FrameClock.Seconds < _lockedUntil;

    private LobbySeat? FocusedSeat() => _focus.Kind == LobbyFocusKind.Seat && _focus.Index < _state.Seats.Count ? _state.Seats[_focus.Index] : null;

    // ---- The invite panel: a state of this shell ----

    private void OpenPanel()
    {
        _panelFrom = _focus;
        _panelOpen = true;
        _panelRowsStale = true;
        _panelOpenedAt = FrameClock.Seconds;
        ShowPanelParts(true);
        LobbySounds.Play(LobbySounds.PanelIn);
        LobbyLocalization.Apply(_parts.Title, LobbyWords.InviteFriends.Key);
        RefreshFriends(FrameClock.Seconds);
        Redraw(first: false);
        SelectPanelDefault();
    }

    private void ClosePanel()
    {
        _panelOpen = false;
        ShowPanelParts(false);
        LobbySounds.Play(LobbySounds.PanelOut);
        LobbyLocalization.Apply(_parts.Title, LobbyWords.Lobby.Key);
        foreach (ListRow row in _friendRows.Values)
        {
            LobbyClicks.Forget(row.Button);
            UiClone.Destroy(row.Root);
        }

        _friendRows.Clear();
        _friendOrder.Clear();
        if (_panelBack != null)
        {
            LobbyClicks.Forget(_panelBack.Button);
            UiClone.Destroy(_panelBack.Root);
            _panelBack = null;
        }

        Redraw(first: false);
        SelectFocus(LobbyRules.Repair(_panelFrom, _state));
    }

    /// <summary>The frame's own wells and list for the panel; the seats and the column for the lobby.</summary>
    private void ShowPanelParts(bool panel)
    {
        LobbyUi.Show(_column, !panel);
        LobbyUi.Show(_seatArea, !panel);
        Transform? viewport = _parts.ScrollView.Find("Viewport");
        LobbyUi.Show(viewport, panel);
        Transform details = _parts.Details;
        LobbyUi.Show(details.Find("MediaContainer"), panel);
        LobbyUi.Show(details.Find("DescriptionBox"), panel);
        LobbyUi.Show(details.Find("Outline"), panel);
        Image? plate = _parts.Details.GetComponent<Image>();
        if (plate != null)
        {
            plate.enabled = panel;
        }

        VerticalLayoutGroup? layout = _parts.Details.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
        {
            layout.enabled = panel;
        }
    }

    private void RefreshFriends(double now)
    {
        _panelRowsStale |= _picker.Update(_actions.Friends(), FrameClock.NowMicros);
        if (_panelOpen)
        {
            DrawPanel();
            DrawPrompts();
        }
    }

    /// <summary>The list and the pane. The rows only when they've changed: each is a dozen calls into the game and a layout pass over them all, and the list can be long.</summary>
    private void DrawPanel()
    {
        if (_panelRowsStale)
        {
            _panelRowsStale = false;
            DrawPanelRows();
        }

        DrawPanelPane();
    }

    private void DrawPanelRows()
    {
        var seen = new HashSet<ulong>();
        _friendOrder.Clear();
        IReadOnlyList<PickerRow> rows = _picker.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            PickerRow row = rows[i];
            seen.Add(row.SteamId);
            if (!_friendRows.TryGetValue(row.SteamId, out ListRow? widget))
            {
                widget = ListRow.Make(_parts.ScrollContent, $"Friend{++_friendRowsMade}", withParts: true);
                ulong id = row.SteamId;
                LobbyClicks.Bind(widget.Button, () =>
                {
                    _picker.Focus(id);
                    InviteFocused();
                });
                _friendRows[id] = widget;
            }

            widget.Root.transform.SetSiblingIndex(i);
            widget.ShowFriend(row, LobbyScreens.ColourOf(i), i < FriendPictures);
            _friendOrder.Add(widget.Button);
        }

        var gone = new List<ulong>();
        foreach (ulong id in _friendRows.Keys)
        {
            if (!seen.Contains(id))
            {
                gone.Add(id);
            }
        }

        AbstractNavigable? current = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        bool cursorGone = false;
        foreach (ulong id in gone)
        {
            ListRow row = _friendRows[id];
            cursorGone |= current != null && current.Pointer == row.Button.Pointer;
            LobbyClicks.Forget(row.Button);
            UiClone.Destroy(row.Root);
            _friendRows.Remove(id);
        }

        if (_picker.IsEmpty && _panelBack == null)
        {
            _panelBack = ListRow.Make(_parts.ScrollContent, "PanelBack", withParts: false);
            _panelBack.ShowPlain(LobbyLocalization.Say(LobbyTerms.Back));
            LobbyClicks.Bind(_panelBack.Button, () => Perform(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Back, LobbyAction.Back)));
        }

        if (_panelBack != null)
        {
            // A friend came online with the cursor on the list's only Back: it goes, and the cursor mustn't go with it.
            cursorGone |= !_picker.IsEmpty && current != null && current.Pointer == _panelBack.Button.Pointer;
            LobbyUi.Show(LobbyUi.RectOf(_panelBack.Root), _picker.IsEmpty);
        }

        ListRow.Link(_friendOrder);
        _rowsParent?.RegisterChildren(false);
        UiClone.Rebuild(_parts.ScrollContent);
        if (cursorGone)
        {
            SelectPanelDefault();
        }
    }

    private void DrawPanelPane()
    {
        PickerRow? focused = FocusedRowOf(_picker);
        if (focused == null)
        {
            _pane.ShowLine(LobbyLocalization.Say(new LobbyLine(LobbyWords.NoFriendsOnline)));
            _pane.SetWords(LobbyLocalization.Say(new LobbyLine(LobbyWords.NotArriving)));
        }
        else
        {
            _pane.ShowPerson(focused.SteamId, focused.Name, LobbyScreens.ColourOf(_picker.FocusIndex), !focused.CanInvite, null, false, LobbyLocalization.Say(focused.Line.Line), null, 0, -1);
            _pane.SetWords(LobbyLocalization.Say(new LobbyLine(LobbyWords.NotArriving)));
        }
    }

    private void SelectPanelDefault()
    {
        AbstractNavigable? target = FocusedRowOf(_picker) is { } row && _friendRows.TryGetValue(row.SteamId, out ListRow? widget) ? widget.Button : _panelBack?.Button;
        if (target != null)
        {
            LobbyShell.SetDefaultNavigables(target);
            AbstractNavigableUtils.SelectOnNextFrame(target, AbstractNavigable.ESelectFx.Default);
        }
    }

    private void InviteFocused()
    {
        // The press that opened the list, still going: never an invite to a real person.
        if (FrameClock.Seconds - _panelOpenedAt < LobbyTiming.InputLockMicros / 1_000_000.0)
        {
            return;
        }

        PickerRow? row = FocusedRowOf(_picker);
        if (row == null || !row.CanInvite)
        {
            LobbySounds.Play(LobbySounds.Grunt);
            return;
        }

        // The session says how it went, in its toast.
        if (_actions.Invite(row.SteamId))
        {
            _picker.Invited(row.SteamId, FrameClock.NowMicros);
            RefreshFriends(FrameClock.Seconds);
        }
    }

    // ---- Change detection: a state is plain data, its seats records ----

    private static bool Same(LobbyState a, LobbyState b)
    {
        if (a.Role != b.Role || a.Phase != b.Phase || a.Mode != b.Mode || a.Epoch != b.Epoch || a.Save != b.Save || a.Activity != b.Activity || a.Result != b.Result
            || a.WhyNot != b.WhyNot || a.WaitingForEveryone != b.WaitingForEveryone || a.InputLocked != b.InputLocked || a.BackFromGame != b.BackFromGame || a.Reconnecting != b.Reconnecting
            || LobbyRules.CountdownNumber(a.CountdownMs) != LobbyRules.CountdownNumber(b.CountdownMs) || a.Seats.Count != b.Seats.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Seats.Count; i++)
        {
            if (a.Seats[i] != b.Seats[i])
            {
                return false;
            }
        }

        return true;
    }
}
#endif
