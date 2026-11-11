#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// One seat of the lobby: a split. The frame is the game's own button (<c>Button_Confirm</c>: its panel and outline, its animator and sounds), stretched; on it the game's number box and
/// device icons from the controller screen, its ready banner, its crown; a Steam avatar and name. Every state has an icon and a word: colour is never alone. An arrival slams the card's contents in for a
/// fifth of a second, and a press in flight lands as it would have: nothing here takes input, and the cursor isn't touched.
/// <para>
/// Selected, a seat looks as the game's Settings tabs do under the cursor: the pink contour (<c>background-tooltip-priority-04-outline-highlight</c>, #ED4B6E) and the faint wash
/// (#E7E7E7 at 0.06) over its own panel. The button's animator would fill the whole card with its solid blue (its Highlight clip colours <c>Container/Background</c> and whites its
/// outline): right for a 64-high button, a slab on a card the height of the screen. So the panel is renamed before the card wakes, which leaves those curves nothing to bind, and the
/// button's own state change (<c>AbstractButton.OnStateChanged</c>, raised as the animator is given the state) draws the contour and the wash instead. The animator keeps the rest:
/// the card's small nudge and its sounds.
/// </para>
/// </summary>
internal sealed class SeatCard
{
    private const double SlamSeconds = 0.22;
    private const float SlamScale = 1.14f;
    private const double BannerSeconds = 0.18;
    private const float MarkSide = 96;
    private const float FlashAlpha = 0.32f;

    // The panel's name once renamed: the animator's colour curves are bound to "Container/Background".
    private const string PanelName = "Panel";

    private static readonly Color Contour = LobbyUi.Hex(0xED4B6E);
    private static readonly Color Wash = LobbyUi.Hex(0xE7E7E7, 0.06f);
    private static readonly Color Unlit = LobbyUi.Hex(0x000000);

    // Each seat by its button's native object: the game's event is told one delegate, kept alive here.
    private static readonly Dictionary<IntPtr, SeatCard> ByButton = new();
    private static Il2CppSystem.Action<AbstractButton>? _stateChanged;
    private static bool _stateFailedSaid;

    private static readonly HashSet<string> FrameKeep = Without("ButtonInputShortcut");
    private static readonly HashSet<string> PartKeep = Without("Animator", "ButtonInputShortcut", "LayoutElementAspectRatio");

    private readonly RectTransform _root;
    private readonly RectTransform _body;
    private readonly RectTransform _emptyBody;
    private readonly AvatarView _avatar;
    private readonly Image _colourBand;
    private readonly TextMeshProUGUI _number;
    private readonly Image _numberBox;
    private readonly TextMeshProUGUI _you;
    private readonly Image _crown;
    private readonly TextMeshProUGUI _name;
    private readonly TextMeshProUGUI _state;
    private readonly SignalBarsView _signal;
    private readonly RectTransform? _gamepad;
    private readonly RectTransform? _keyboard;
    private readonly Image? _gamepadFill;
    private readonly Image? _keyboardFill;
    private readonly Image? _bigMark;
    private readonly RectTransform? _dots;
    private readonly Image[] _dot;
    private readonly RectTransform _banner;
    private readonly TextMeshProUGUI _plusWord;
    private readonly Image _flash;
    private readonly Image? _outline;
    private readonly Image? _wash;
    private readonly IntPtr _buttonPointer;
    private readonly bool _big;
    private float _arrangedFor = -1;
    private float _avatarSide;
    private double _slamAt = -1;
    private double _bannerAt = -1;
    private bool _wasReady;
    private bool _dotsOn;

    private SeatCard(GameObject root, AnimatedButton button, bool big, RectTransform body, RectTransform emptyBody, AvatarView avatar, Image colourBand, TextMeshProUGUI number, Image numberBox, TextMeshProUGUI you, Image crown, TextMeshProUGUI name, TextMeshProUGUI state, SignalBarsView signal, RectTransform? gamepad, RectTransform? keyboard, Image? gamepadFill, Image? keyboardFill, Image? bigMark, RectTransform? dots, Image[] dot, RectTransform banner, TextMeshProUGUI plusWord, Image flash, Image? outline, Image? wash)
    {
        Root = root;
        _root = LobbyUi.RectOf(root);
        Button = button;
        _buttonPointer = button.Pointer;
        _big = big;
        _body = body;
        _emptyBody = emptyBody;
        _avatar = avatar;
        _colourBand = colourBand;
        _number = number;
        _numberBox = numberBox;
        _you = you;
        _crown = crown;
        _name = name;
        _state = state;
        _signal = signal;
        _gamepad = gamepad;
        _keyboard = keyboard;
        _gamepadFill = gamepadFill;
        _keyboardFill = keyboardFill;
        _bigMark = bigMark;
        _dots = dots;
        _dot = dot;
        _banner = banner;
        _plusWord = plusWord;
        _flash = flash;
        _outline = outline;
        _wash = wash;
    }

    /// <summary>Its object: anchor it into the seats' area.</summary>
    public GameObject Root { get; }

    /// <summary>Its button: what the cursor lands on.</summary>
    public AnimatedButton Button { get; }

    /// <summary>The controller screen's player, from its menu's own field; its number box and device icons are cloned from there.</summary>
    public static GameObject PlayerPrefab()
    {
        GameObject? menu = UiAssets.Prefab(LobbyUi.Controllers);
        ControllerAssignmentPlayer? player = menu?.GetComponent<ControllerAssignmentMenu>()?.m_PlayerPrefab;
        return player != null ? player.gameObject : throw new InvalidOperationException("the controller screen has no player prefab");
    }

    /// <summary>Makes the card for seat <paramref name="index"/>, laid out for <paramref name="layout"/>. Its parts' places come from its width at <see cref="Show"/>.</summary>
    public static SeatCard Make(Transform parent, int index, in SeatLayout layout)
    {
        // Made asleep and placed only once its panel is renamed: an animator binds its curves as it wakes, and a seat made for a lobby that grew wakes at once.
        string seatName = $"Seat{index}";
        GameObject source = UiAssets.Part(LobbyUi.Settings, "ScaledContainer/Content/Items/ConfirmButton/Button_Confirm") ?? throw new InvalidOperationException("the seat's button isn't held");
        GameObject root = UiClone.Make(source, FrameKeep, seatName, out string made) ?? throw new InvalidOperationException(made);
        root.name = seatName;
        AnimatedButton button = root.GetComponent<AnimatedButton>() ?? throw new InvalidOperationException("the seat has no button");
        button.ClearNeighbors();
        Transform container = root.transform.Find("Container") ?? throw new InvalidOperationException("the seat has no Container");

        // The frame stays; its one line of content goes, and so do its hold fill and its highlight line (the contour marks the cursor).
        UiClone.Destroy(container.Find("Content")?.gameObject);
        UiClone.Destroy(container.Find("Background/Mask/HoldFill")?.gameObject);
        UiClone.Destroy(container.Find("Background/Mask/Highlight-Line")?.gameObject);
        Transform? panel = container.Find("Background");
        Image? outline = panel?.Find("Outline")?.GetComponent<Image>();
        Image? panelImage = panel?.GetComponent<Image>();
        Image? wash = null;
        if (panel == null || outline == null || panelImage == null)
        {
            SayStateFailed("the button has no panel or outline");
            outline = null;
        }
        else if (!Watch(button))
        {
            outline = null;
        }
        else
        {
            panel.name = PanelName;
            outline.color = Unlit;
            wash = LobbyUi.Picture(panel, "BackgroundHighlighted", panelImage.sprite, Wash, sliced: true);
            LobbyUi.Fill(wash.rectTransform);
            wash.transform.SetSiblingIndex(outline.transform.GetSiblingIndex());
            LobbyUi.Show(wash, false);
        }

        UiClone.Place(root, parent);

        bool big = layout.ShowsDevice;
        RectTransform body = LobbyUi.New(container, "Body");
        LobbyUi.Fill(body);
        body.gameObject.AddComponent(Il2CppType.Of<RectMask2D>());
        Image colourBand = LobbyUi.Picture(body, "ColourBand", NativeArt.Get(NativeArt.PanelMask), LobbyUi.White, sliced: true);
        LobbyUi.TopBand(colourBand.rectTransform, 5, big ? 52 : 44, 5);

        // The number box, the game's own, in the player's colour: colour is never the only mark.
        GameObject prefab = PlayerPrefab();
        GameObject boxSource = UiClone.Find(prefab.transform, "HorizontalyMovedContainer/PlayerIDContainer/PlayerIDBox")?.gameObject ?? throw new InvalidOperationException("no number box");
        GameObject boxClone = UiClone.Make(boxSource, PartKeep, "NumberBox", out string problem) ?? throw new InvalidOperationException(problem);
        UiClone.Place(boxClone, body);
        LobbyUi.At(LobbyUi.RectOf(boxClone), 0, 1, 16, big ? -16 : -11, 52, 32, 0, 1);
        Image numberBox = boxClone.GetComponent<Image>() ?? throw new InvalidOperationException("the number box has no image");
        TextMeshProUGUI number = boxClone.GetComponentInChildren<TextMeshProUGUI>() ?? throw new InvalidOperationException("the number box has no text");
        number.enableAutoSizing = false;
        number.fontSize = 26;

        // "You" on one's own card, at the band's right end.
        TextMeshProUGUI you = LobbyUi.Label(LobbyFont.Button, body, "You", 26, align: TextAlignmentOptions.MidlineRight);
        you.rectTransform.anchorMin = new Vector2(0, 1);
        you.rectTransform.anchorMax = new Vector2(1, 1);
        you.rectTransform.pivot = new Vector2(1, 1);
        you.rectTransform.sizeDelta = new Vector2(-84, 48);
        you.rectTransform.anchoredPosition = new Vector2(-16, big ? -8 : -3);

        // Avatar, name, state, signal: placed by Arrange.
        AvatarView avatar = AvatarView.Make(body, "Avatar", big ? 232 : 120, AvatarSize.Large);
        avatar.Root.anchorMin = avatar.Root.anchorMax = new Vector2(0.5f, 1);
        avatar.Root.pivot = new Vector2(0.5f, 1);
        TextMeshProUGUI name = LobbyUi.Label(LobbyFont.Button, body, "Name", Math.Max(layout.NameSize, 26), minSize: 26, align: TextAlignmentOptions.Center);
        LobbyUi.ForName(name);
        TextMeshProUGUI state = LobbyUi.Label(LobbyFont.Body, body, "State", 26, align: TextAlignmentOptions.Top, color: LobbyUi.BodyText);
        state.textWrappingMode = TextWrappingModes.Normal;
        state.overflowMode = TextOverflowModes.Overflow;

        // A narrow window's card: smaller before a word breaks in two ("Reconnecti / ng 0:29" seen at 4:3).
        state.enableAutoSizing = true;
        state.fontSizeMin = LobbyRules.TextFloorCaps;
        state.fontSizeMax = 26;
        Image crown = LobbyUi.Picture(body, "Crown", NativeArt.Get(NativeArt.Crown), LobbyUi.White);
        crown.preserveAspect = true;
        crown.rectTransform.anchorMin = crown.rectTransform.anchorMax = new Vector2(0.5f, 1);
        crown.rectTransform.pivot = new Vector2(1, 1);
        crown.rectTransform.sizeDelta = new Vector2(42, 32);
        var signal = new SignalBarsView(body);
        signal.Root.anchorMin = signal.Root.anchorMax = new Vector2(0.5f, 1);
        signal.Root.pivot = new Vector2(0, 1);
        signal.Root.sizeDelta = new Vector2(SignalBarsView.Width, 26);

        // The device, tinted with the player's colour, on the big cards; there, too, a state's icon when nobody's here yet.
        RectTransform? gamepad = null;
        RectTransform? keyboard = null;
        Image? gamepadFill = null;
        Image? keyboardFill = null;
        Image? bigMark = null;
        RectTransform? dots = null;
        var dot = Array.Empty<Image>();
        if (big)
        {
            gamepad = Device(body, prefab, "GamepadRoot", out gamepadFill);
            keyboard = Device(body, prefab, "KeyboardRoot", out keyboardFill);
            bigMark = LobbyUi.Picture(body, "Mark", null, LobbyUi.BodyText);
            bigMark.preserveAspect = true;
            bigMark.rectTransform.anchorMin = bigMark.rectTransform.anchorMax = new Vector2(0.5f, 1);
            bigMark.rectTransform.pivot = new Vector2(0.5f, 1);
            bigMark.rectTransform.sizeDelta = new Vector2(MarkSide, MarkSide);
            dots = LobbyUi.New(body, "Dots");
            dots.anchorMin = dots.anchorMax = new Vector2(0.5f, 1);
            dots.pivot = new Vector2(0.5f, 1);
            dots.sizeDelta = new Vector2(120, 36);
            dot = new Image[3];
            for (int i = 0; i < dot.Length; i++)
            {
                dot[i] = LobbyUi.Picture(dots, $"Dot{i}", NativeArt.Get(NativeArt.Dots), LobbyUi.White);
                LobbyUi.At(dot[i].rectTransform, 0.5f, 0.5f, (i - 1) * 40, 0, 28, 28);
            }
        }

        // The ready banner: the game's own, moving arrows and all, scaled to the card, sliding in from the card's edge.
        GameObject banner = LobbyUi.Part(LobbyUi.Controllers, "ScaledContainer/Content/Team_Left/Team Container/ReadyBanner", body, PartKeep, "ReadyBanner");
        UiClone.Destroy(banner.GetComponent<LayoutElement>());
        RectTransform bannerRect = LobbyUi.RectOf(banner);
        LobbyUi.BottomBand(bannerRect, big ? 10 : 6, Math.Max(layout.BannerHeight, big ? 72 : 52), 2);
        Transform? bannerWord = banner.transform.Find("txt-read");
        TextMeshProUGUI? ready = bannerWord == null ? null : bannerWord.GetComponent<TextMeshProUGUI>();
        if (ready != null)
        {
            LobbyUi.Fill(ready.rectTransform, 12, 6, 36, 6);
            ready.enableAutoSizing = true;
            ready.fontSizeMin = 20;
            ready.fontSizeMax = 40;
            LobbyLocalization.Apply(ready, LobbyTerms.Ready);
        }

        // An empty seat: the plus and its word.
        RectTransform emptyBody = LobbyUi.New(container, "EmptyBody");
        LobbyUi.Fill(emptyBody);
        float plusSide = big ? 128 : 84;
        Image plus = LobbyUi.Picture(emptyBody, "Plus", NativeArt.Get(NativeArt.Plus), new Color(1, 1, 1, 0.85f));
        LobbyUi.At(plus.rectTransform, 0.5f, 0.5f, 0, big ? 40 : 28, plusSide, plusSide);
        TextMeshProUGUI plusWord = LobbyUi.Label(LobbyFont.Button, emptyBody, "Word", 32, minSize: 26, align: TextAlignmentOptions.Center);
        plusWord.rectTransform.anchorMin = new Vector2(0, 0.5f);
        plusWord.rectTransform.anchorMax = new Vector2(1, 0.5f);
        plusWord.rectTransform.sizeDelta = new Vector2(-24, 44);
        plusWord.rectTransform.anchoredPosition = new Vector2(0, big ? -56 : -38);
        Image flash = LobbyUi.Picture(container, "Flash", NativeArt.Get(NativeArt.PanelMask), new Color(1, 1, 1, 0), sliced: true);
        LobbyUi.Fill(flash.rectTransform);
        LobbyUi.Show(flash, false);
        var card = new SeatCard(root, button, big, body, emptyBody, avatar, colourBand, number, numberBox, you, crown, name, state, signal, gamepad, keyboard, gamepadFill, keyboardFill, bigMark, dots, dot, bannerRect, plusWord, flash, outline, wash);
        if (wash != null)
        {
            // Told from the button's next state on; asleep, it has had none yet. A pointer a gone card left behind is simply taken over.
            ByButton[card._buttonPointer] = card;
        }

        return card;
    }

    /// <summary>Forgets this seat: its card is going (by the native object it had, which may be gone already).</summary>
    public void Forget()
    {
        if (ByButton.TryGetValue(_buttonPointer, out SeatCard? known) && ReferenceEquals(known, this))
        {
            ByButton.Remove(_buttonPointer);
        }
    }

    /// <summary>
    /// The selected look follows the button's own state, as its animator does: told by the button as the state changes (a pad's move, the mouse over it, a prompt taking the
    /// cursor away). Asked while the card is asleep, before its panel is renamed: false (said once in the log) leaves the panel's name, so the button's own highlight stays.
    /// </summary>
    private static bool Watch(AnimatedButton button)
    {
        try
        {
            _stateChanged ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractButton>>(new Action<AbstractButton>(OnStateChanged))
                ?? throw new InvalidOperationException("the state delegate couldn't be made");
            (button.OnStateChanged ?? throw new InvalidOperationException("the button has no state event")).Register(_stateChanged, "T2TOnline");
            return true;
        }
        catch (Exception ex)
        {
            SayStateFailed(ModLog.SafeErrorText(ex));
            return false;
        }
    }

    private static void SayStateFailed(string why)
    {
        if (!_stateFailedSaid)
        {
            _stateFailedSaid = true;
            ModLog.Warn($"Lobby: a selected seat can't show the game's contour ({why}); it keeps the button's own highlight.");
        }
    }

    private static void OnStateChanged(AbstractButton button)
    {
        try
        {
            if (button != null && ByButton.TryGetValue(button.Pointer, out SeatCard? card))
            {
                card.Light(button.CurrentState is AbstractButton.EButtonState.Highlight or AbstractButton.EButtonState.Down);
            }
        }
        catch (Exception ex)
        {
            SayStateFailed(ModLog.SafeErrorText(ex));
        }
    }

    private void Light(bool selected)
    {
        if (_outline != null && !_outline.WasCollected)
        {
            _outline.color = selected ? Contour : Unlit;
        }

        if (_wash != null && !_wash.WasCollected)
        {
            LobbyUi.Show(_wash, selected);
        }
    }

    /// <summary>
    /// Draws a seat. <paramref name="focused"/>: the cursor is here: the ms show. <paramref name="canInvite"/>: an empty seat says Invite (the host's), else Open seat.
    /// <paramref name="covered"/>: the start countdown is drawn over the seats: an empty seat shows its panel only, its plus and word would sit under the number.
    /// </summary>
    public void Show(LobbySeat seat, Color colour, bool focused, bool canInvite, bool covered)
    {
        Arrange();
        bool empty = seat.IsEmpty;
        LobbyUi.Show(_body, !empty);
        LobbyUi.Show(_emptyBody, empty && !covered);
        LobbyUi.Set(_number, seat.PlayerNumber.ToString(CultureInfo.InvariantCulture));
        if (empty)
        {
            LobbyUi.Say(_plusWord, canInvite ? new LobbyLine(LobbyWords.Invite) : new LobbyLine(LobbyWords.OpenSeat));
            _dotsOn = false;
            return;
        }

        _colourBand.color = new Color(colour.r, colour.g, colour.b, 0.55f);
        _numberBox.color = colour;
        bool here = seat.Stage == SeatStage.Here;
        _avatar.Show(seat.SteamId, seat.Name, colour, !here);
        LobbyUi.Set(_name, seat.Name);
        _name.color = seat.Stage is SeatStage.Invited or SeatStage.Joining ? LobbyUi.BodyText : LobbyUi.White;
        LobbyUi.Say(_you, seat.IsSelf ? new LobbyLine(LobbyWords.You) : null);
        DrawState(seat, here);
        _signal.Show(seat.IsHost || seat.Stage is SeatStage.Invited or SeatStage.Joining ? 0 : seat.Bars, seat.PingMs, focused);
        CentreSignal(focused && seat.PingMs >= 0);
        bool pad = seat.DeviceKind != InputKind.KeyboardMouse;
        LobbyUi.Show(_gamepad, here && pad);
        LobbyUi.Show(_keyboard, here && !pad);
        if (_gamepadFill != null)
        {
            _gamepadFill.color = colour;
        }

        if (_keyboardFill != null)
        {
            _keyboardFill.color = colour;
        }

        LobbyUi.Show(_banner, seat.Ready);
        if (seat.Ready && !_wasReady)
        {
            _bannerAt = FrameClock.Seconds;
        }

        _wasReady = seat.Ready;
    }

    /// <summary>An arrival: the card's contents slam in, for a fifth of a second. Nothing waits for it.</summary>
    public void Slam()
    {
        _slamAt = FrameClock.Seconds;
        _flash.color = new Color(1, 1, 1, FlashAlpha);
        LobbyUi.Show(_flash, true);
    }

    /// <summary>Its picture, when the cache changed.</summary>
    public void Refresh() => _avatar.Refresh();

    /// <summary>Every frame: nothing but a few compares unless a slam, a banner or the dots are running.</summary>
    public void Tick()
    {
        double now = FrameClock.Seconds;
        if (_slamAt >= 0)
        {
            float t = (float)((now - _slamAt) / SlamSeconds);
            if (t >= 1)
            {
                _slamAt = -1;
                _body.localScale = Vector3.one;
                LobbyUi.Show(_flash, false);
            }
            else
            {
                float scale = 1 + ((SlamScale - 1) * (1 - t) * (1 - t));
                _body.localScale = new Vector3(scale, scale, 1);
                _flash.color = new Color(1, 1, 1, FlashAlpha * (1 - t));
            }
        }

        if (_bannerAt >= 0)
        {
            float t = (float)((now - _bannerAt) / BannerSeconds);
            if (t >= 1)
            {
                _bannerAt = -1;
                _banner.anchoredPosition = new Vector2(0, _banner.anchoredPosition.y);
            }
            else
            {
                _banner.anchoredPosition = new Vector2(-_banner.rect.width * (1 - t) * (1 - t), _banner.anchoredPosition.y);
            }
        }

        if (_dotsOn)
        {
            for (int i = 0; i < _dot.Length; i++)
            {
                float phase = Mathf.Repeat((float)now * 1.4f - (i * 0.22f), 1);
                _dot[i].color = new Color(1, 1, 1, phase < 0.4f ? 1 : 0.28f);
            }
        }
    }

    /// <summary>The sprite of a state's icon, or null: the word alone then.</summary>
    public static Sprite? IconOf(LobbyIcon icon) => icon switch
    {
        LobbyIcon.Plus => NativeArt.Get(NativeArt.Plus),
        LobbyIcon.Invited => NativeArt.Get(NativeArt.Tick),
        LobbyIcon.Dots => NativeArt.Get(NativeArt.Dots),
        LobbyIcon.Clock => NativeArt.Get(NativeArt.Warning),
        LobbyIcon.Crown => NativeArt.Get(NativeArt.Crown),
        LobbyIcon.Ready => NativeArt.Get(NativeArt.ReadyTick),
        LobbyIcon.Lock => NativeArt.Get(NativeArt.Lock),
        LobbyIcon.InvitedYou => NativeArt.Get(NativeArt.Tooltip),
        _ => null,
    };

    /// <summary>The state row: the host's crown and word; else the state's word, with Here left to the device's own icon. On a big card, a state with no device has its icon in the device's place.</summary>
    private void DrawState(LobbySeat seat, bool here)
    {
        LobbyMark mark = seat.Mark;
        LobbyLine? word = seat.IsHost && here ? LobbyMarks.Host.Line : here ? (_big ? null : mark.Line) : mark.Line;
        LobbyUi.Say(_state, word);
        // Set at each draw: its prefab is optional and loads by itself, and an image with no sprite is a white square.
        bool crown = seat.IsHost;
        Sprite? crownSprite = crown ? NativeArt.Get(NativeArt.Crown) : null;
        _crown.sprite = crownSprite;
        LobbyUi.Show(_crown, crownSprite != null);
        if (crown)
        {
            float half = Math.Min(_state.preferredWidth, _state.rectTransform.rect.width) / 2;
            _crown.rectTransform.anchoredPosition = new Vector2(-half - 6, _state.rectTransform.anchoredPosition.y - 1);
        }

        bool dots = _big && mark.Icon == LobbyIcon.Dots && !here;
        Sprite? icon = _big && !here && !dots ? IconOf(mark.Icon) : null;
        _dotsOn = dots;
        LobbyUi.Show(_dots, dots);
        if (_bigMark != null)
        {
            _bigMark.sprite = icon;
            LobbyUi.Show(_bigMark, icon != null);
        }
    }

    /// <summary>The parts' places from the card's width: the avatar never wider than the card, what's below it following. Only when the width changed.</summary>
    private void Arrange()
    {
        float width = _root.rect.width;
        if (width <= 0 || Mathf.Approximately(width, _arrangedFor))
        {
            return;
        }

        _arrangedFor = width;
        float top = _big ? 66 : 54;
        _avatarSide = Mathf.Min(width - 28, _big ? 232 : 120);
        _avatar.Root.sizeDelta = new Vector2(_avatarSide, _avatarSide);
        _avatar.Root.anchoredPosition = new Vector2(0, -top);
        float y = top + _avatarSide + (_big ? 10 : 4);
        LobbyUi.TopBand(_name.rectTransform, y, _big ? 46 : 38, 10);
        y += _big ? 46 : 36;
        LobbyUi.TopBand(_state.rectTransform, y, 36, 8);
        y += _big ? 44 : 36;
        if (_big)
        {
            float deviceHeight = Mathf.Min(128, (width - 28) * 0.85f);
            float deviceWidth = deviceHeight * 128f / 108f;
            foreach (RectTransform? device in new[] { _gamepad, _keyboard })
            {
                if (device != null)
                {
                    device.sizeDelta = new Vector2(deviceWidth, deviceHeight);
                    device.anchoredPosition = new Vector2(0, -y);
                }
            }

            if (_bigMark != null)
            {
                _bigMark.rectTransform.anchoredPosition = new Vector2(0, -y - 14 - ((deviceHeight - MarkSide) / 2));
            }

            if (_dots != null)
            {
                _dots.anchoredPosition = new Vector2(0, -y - 14 - ((deviceHeight - 36) / 2));
            }

            y += deviceHeight + 16;
        }

        _signalTop = y;
        CentreSignal(false);
    }

    private float _signalTop;

    private void CentreSignal(bool withMs)
    {
        float wide = SignalBarsView.Width + (withMs ? 90 : 0);
        _signal.Root.anchoredPosition = new Vector2(-wide / 2, -_signalTop);
    }

    private static RectTransform Device(Transform parent, GameObject prefab, string which, out Image? fill)
    {
        GameObject source = UiClone.Find(prefab.transform, $"HorizontalyMovedContainer/{which}")?.gameObject ?? throw new InvalidOperationException($"no {which}");
        GameObject clone = UiClone.Make(source, PartKeep, which, out string problem) ?? throw new InvalidOperationException(problem);
        clone.SetActive(true);
        UiClone.Place(clone, parent);
        RectTransform rect = LobbyUi.RectOf(clone);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
        rect.pivot = new Vector2(0.5f, 1);
        fill = clone.transform.Find("FillColor")?.GetComponent<Image>();
        return rect;
    }

    private static HashSet<string> Without(params string[] names)
    {
        var keep = new HashSet<string>(UiClone.Widgets, StringComparer.Ordinal);
        foreach (string name in names)
        {
            keep.Remove(name);
        }

        return keep;
    }
}
#endif
