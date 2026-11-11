#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// One row of a list: the Glossary's own row (<c>GlossaryRow</c>: <c>AnimatedButton</c>, its highlight, line and outline by its animator, its sounds), its script gone, with an avatar, a title, a
/// detail line, and at its right end a count and signal bars. A greyed row dims its words and carries a lock: colour is never alone, and it can still be focused.
/// </summary>
internal sealed class ListRow
{
    /// <summary>A row's height with an avatar.</summary>
    public const float Height = 92;

    /// <summary>A row's height with a title alone.</summary>
    public const float PlainHeight = 64;

    private const float AvatarSide = 72;

    private readonly AvatarView? _avatar;
    private readonly TextMeshProUGUI _title;
    private readonly TextMeshProUGUI? _detail;
    private readonly TextMeshProUGUI? _count;
    private readonly Image? _lock;
    private readonly SignalBarsView? _bars;
    private readonly CanvasGroup _group;

    private ListRow(GameObject root, AnimatedButton button, AvatarView? avatar, TextMeshProUGUI title, TextMeshProUGUI? detail, TextMeshProUGUI? count, Image? locked, SignalBarsView? bars, CanvasGroup group)
    {
        Root = root;
        Button = button;
        _avatar = avatar;
        _title = title;
        _detail = detail;
        _count = count;
        _lock = locked;
        _bars = bars;
        _group = group;
    }

    /// <summary>Its object.</summary>
    public GameObject Root { get; }

    /// <summary>Its button: what the cursor lands on.</summary>
    public AnimatedButton Button { get; }

    /// <summary>The Glossary's row prefab, from its list's own field.</summary>
    public static GameObject Prefab()
    {
        GameObject? list = UiAssets.Part(LobbyUi.Glossary, "ScaledContainer/Content/Scroll View");
        Il2CppUICore.ReusableList.BaseCell? cell = list?.GetComponent<GlossaryScrollList>()?.cellPrefab;
        return cell != null ? cell.gameObject : throw new InvalidOperationException("the Glossary's list has no row prefab");
    }

    /// <summary>Makes a row under a list's content: with its parts, or a title alone.</summary>
    public static ListRow Make(Transform parent, string name, bool withParts)
    {
        GameObject root = UiClone.Make(Prefab(), UiClone.Widgets, name, out string problem) ?? throw new InvalidOperationException(problem);
        root.name = name;
        AnimatedButton button = root.GetComponent<AnimatedButton>() ?? throw new InvalidOperationException("the row has no button");
        button.ClearNeighbors();
        LayoutElement layout = root.GetComponent<LayoutElement>() ?? root.AddComponent(Il2CppType.Of<LayoutElement>())?.TryCast<LayoutElement>() ?? throw new InvalidOperationException("the row can't lay out");
        layout.minHeight = layout.preferredHeight = withParts ? Height : PlainHeight;
        CanvasGroup group = root.GetComponent<CanvasGroup>() ?? root.AddComponent(Il2CppType.Of<CanvasGroup>())?.TryCast<CanvasGroup>() ?? throw new InvalidOperationException("the row can't fade");
        Transform container = root.transform.Find("Container") ?? throw new InvalidOperationException("the row has no Container");
        Transform text = container.Find("MainText/Title") ?? throw new InvalidOperationException("the row has no title");
        TextMeshProUGUI title = text.GetComponent<TextMeshProUGUI>() ?? throw new InvalidOperationException("the row's title isn't text");
        title.fontSize = 28;
        title.enableAutoSizing = false;
        title.raycastTarget = false;
        title.textWrappingMode = TextWrappingModes.NoWrap;
        title.overflowMode = TextOverflowModes.Ellipsis;
        LobbyUi.ForName(title);
        if (!withParts)
        {
            title.alignment = TextAlignmentOptions.Center;
            UiClone.Place(root, parent);
            return new ListRow(root, button, null, title, null, null, null, null, group);
        }

        // The row's title moves up beside the avatar; the rest is laid over the row's own container.
        RectTransform mainText = container.Find("MainText").GetComponent<RectTransform>();
        LobbyUi.Fill(mainText, AvatarSide + 28, 42, 92, 6);
        title.alignment = TextAlignmentOptions.MidlineLeft;
        AvatarView avatar = AvatarView.Make(container, "Avatar", AvatarSide, AvatarSize.Medium);
        LobbyUi.At(avatar.Root, 0, 0.5f, 14, 0, AvatarSide, AvatarSide, 0, 0.5f);
        TextMeshProUGUI detail = LobbyUi.Label(LobbyFont.Body, container, "Detail", 26, align: TextAlignmentOptions.MidlineLeft, color: LobbyUi.BodyText);
        LobbyUi.Fill(detail.rectTransform, AvatarSide + 28, 6, 12, 48);
        TextMeshProUGUI count = LobbyUi.Label(LobbyFont.Title, container, "Count", 26, align: TextAlignmentOptions.MidlineRight);
        count.rectTransform.anchorMin = new Vector2(1, 0.5f);
        count.rectTransform.anchorMax = new Vector2(1, 1);
        count.rectTransform.pivot = new Vector2(1, 0.5f);
        count.rectTransform.sizeDelta = new Vector2(84, -6);
        count.rectTransform.anchoredPosition = new Vector2(-14, -3);
        Image locked = LobbyUi.Picture(container, "Lock", NativeArt.Get(NativeArt.Lock), LobbyUi.White);
        LobbyUi.At(locked.rectTransform, 1, 0, -14, 10, 32, 32, 1, 0);
        var bars = new SignalBarsView(container);
        LobbyUi.At(bars.Root, 1, 0, -14 - SignalBarsView.Width, 12, SignalBarsView.Width, 26, 0, 0);
        UiClone.Place(root, parent);
        return new ListRow(root, button, avatar, title, detail, count, locked, bars, group);
    }

    /// <summary>A row of the Join list. <paramref name="detailLine"/>: its mode and save, or the reason it's greyed.</summary>
    public void ShowLobby(BrowserRow row, string detailLine, Color colour)
    {
        bool greyed = row.Block != BrowserBlock.None;
        _avatar?.Show(row.HostId, row.HostName, colour, greyed);
        LobbyUi.Set(_title, row.HostName);
        LobbyUi.Set(_detail, detailLine);

        LobbyUi.Set(_count, row.Block == BrowserBlock.Checking ? string.Empty : row.Count);
        if (_lock != null)
        {
            Sprite? locked = NativeArt.Get(NativeArt.Lock);
            _lock.sprite = locked;
            LobbyUi.Show(_lock, locked != null && row.Mark.Icon == LobbyIcon.Lock);
        }

        _bars?.Show(row.Mark.Icon == LobbyIcon.Lock ? 0 : row.Bars, row.PingMs, false);
        _group.alpha = row.Fading ? 0.3f : greyed ? 0.62f : 1;
        Button.IsNavigable = !row.Fading;
    }

    /// <summary>A row of the invite list. <paramref name="picture"/>: its Steam avatar is asked for; else its letter stands (the cache holds 32, and a longer list would evict itself, row by row, for as long as it's open).</summary>
    public void ShowFriend(PickerRow row, Color colour, bool picture)
    {
        bool can = row.CanInvite;
        _avatar?.Show(picture ? row.SteamId : 0, row.Name, colour, !can);
        LobbyUi.Set(_title, row.Name);
        _title.color = can ? LobbyUi.White : LobbyUi.BodyText;
        LobbyUi.Say(_detail, row.Line.Line);
        LobbyUi.Set(_count, string.Empty);
        if (_lock != null)
        {
            _lock.sprite = NativeArt.Get(row.Mark == PickerMark.Invited ? NativeArt.Tick : NativeArt.ReadyTick);
            LobbyUi.Show(_lock, row.Mark != PickerMark.None);
        }

        _bars?.Show(0, -1, false);
    }

    /// <summary>A row with a title alone.</summary>
    public void ShowPlain(string text) => LobbyUi.Set(_title, text);

    /// <summary>Asks for its picture again when the cache changed.</summary>
    public void Refresh() => _avatar?.Refresh();

    /// <summary>Up and down through the rows, wrapping: a linear list (XAG 112).</summary>
    public static void Link(IReadOnlyList<AbstractNavigable> rows)
    {
        int count = rows.Count;
        for (int i = 0; i < count; i++)
        {
            rows[i].ClearNeighbors();
            if (count > 1)
            {
                rows[i].SetNeighbor(AbstractNavigable.EDirection.Up, rows[(i + count - 1) % count]);
                rows[i].SetNeighbor(AbstractNavigable.EDirection.Down, rows[(i + 1) % count]);
            }
        }
    }
}
#endif
