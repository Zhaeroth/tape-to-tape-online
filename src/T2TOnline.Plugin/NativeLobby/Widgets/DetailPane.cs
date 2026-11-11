#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>One delegate for every button's click, told apart by the native object: the game holds the delegate, and these keep it alive.</summary>
internal static class LobbyClicks
{
    private static readonly Dictionary<IntPtr, Action> Handlers = new();
    private static Il2CppSystem.Action<AbstractNavigable>? _clicked;

    /// <summary>What a click on <paramref name="navigable"/> does. Once per button: the game's event keeps the registration.</summary>
    public static void Bind(AbstractNavigable navigable, Action action)
    {
        _clicked ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractNavigable>>(new Action<AbstractNavigable>(OnClicked))
            ?? throw new InvalidOperationException("the click delegate couldn't be made");
        if (Handlers.TryAdd(navigable.Pointer, action))
        {
            navigable.OnClick.Register(_clicked, "T2TOnline");
        }
        else
        {
            Handlers[navigable.Pointer] = action;
        }
    }

    /// <summary>Forgets a button that went.</summary>
    public static void Forget(AbstractNavigable navigable)
    {
        if (navigable is not null && !navigable.WasCollected)
        {
            Handlers.Remove(navigable.Pointer);
        }
    }

    private static void OnClicked(AbstractNavigable navigable)
    {
        try
        {
            if (navigable != null && Handlers.TryGetValue(navigable.Pointer, out Action? action))
            {
                action();
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: a click failed ({ModLog.SafeErrorText(ex)}).");
        }
    }
}

/// <summary>
/// The right-hand panel of the Glossary's frame, as the browser and the invite panel fill it: its media well shows a person (a large avatar, a name, a title line and a big number) or a big centred
/// line for an empty state; its description box the words. The frame's wells are the game's own, filled with the game's fonts.
/// </summary>
internal sealed class DetailPane
{
    private const float PersonWidth = 880;

    private readonly AvatarView _avatar;
    private readonly TextMeshProUGUI _name;
    private readonly TextMeshProUGUI _role;
    private readonly Image _crown;
    private readonly TextMeshProUGUI _lineA;
    private readonly TextMeshProUGUI _count;
    private readonly SignalBarsView _signal;
    private readonly TextMeshProUGUI _big;
    private readonly RectTransform _person;
    private readonly TMP_Text _words;
    private readonly RectTransform _well;
    private float _centredFor = -1;

    /// <summary>Fills the shell's wells.</summary>
    public DetailPane(ShellParts parts)
    {
        Transform well = parts.Details.Find("MediaContainer") ?? throw new InvalidOperationException("the frame has no media well");
        _well = well.GetComponent<RectTransform>() ?? throw new InvalidOperationException("the media well has no rect");
        _words = parts.Description;
        _words.fontSize = 28;
        _words.richText = false; // a greyed row's words name the host
        _words.textWrappingMode = TextWrappingModes.Normal;
        _person = LobbyUi.New(well, "Person");
        LobbyUi.Fill(_person, 40, 32, 40, 32);
        const float side = 232;
        _avatar = AvatarView.Make(_person, "Avatar", side, AvatarSize.Large);
        LobbyUi.At(_avatar.Root, 0, 0.5f, 0, 0, side, side, 0, 0.5f);
        float x = side + 32;
        _crown = LobbyUi.Picture(_person, "Crown", NativeArt.Get(NativeArt.Crown), LobbyUi.White);
        _crown.preserveAspect = true;
        LobbyUi.At(_crown.rectTransform, 0, 0.5f, x, 112, 44, 36, 0, 0.5f);
        _role = LobbyUi.Label(LobbyFont.Button, _person, "Role", 32, minSize: 26, align: TextAlignmentOptions.MidlineLeft);
        Across(_role.rectTransform, x + 54, 112, 44);
        _name = LobbyUi.Label(LobbyFont.Button, _person, "Name", 56, minSize: 26, align: TextAlignmentOptions.MidlineLeft);
        Across(_name.rectTransform, x, 56, 72);
        LobbyUi.ForName(_name);
        _lineA = LobbyUi.Label(LobbyFont.Body, _person, "State", 28, minSize: 26, align: TextAlignmentOptions.MidlineLeft, color: LobbyUi.BodyText);
        Across(_lineA.rectTransform, x, 0, 72);
        _lineA.textWrappingMode = TextWrappingModes.Normal;
        _count = LobbyUi.Label(LobbyFont.Title, _person, "Count", 48, align: TextAlignmentOptions.MidlineLeft);
        Across(_count.rectTransform, x, -64, 60);
        _signal = new SignalBarsView(_person);
        LobbyUi.At(_signal.Root, 0, 0.5f, x + 136, -78, SignalBarsView.Width, 26, 0, 0);
        _big = LobbyUi.Label(LobbyFont.Button, well, "Big", 48, minSize: 28, align: TextAlignmentOptions.Center);
        LobbyUi.Fill(_big.rectTransform, 48, 32, 48, 32);
        _big.textWrappingMode = TextWrappingModes.Normal;
    }

    /// <summary>A person in the well; a null title line hides it. <paramref name="count"/> and bars show when they're given.</summary>
    public void ShowPerson(ulong steamId, string name, Color colour, bool dim, string? role, bool crown, string? line, string? count, int bars, int pingMs)
    {
        LobbyUi.Show(_person, true);
        LobbyUi.Show(_big, false);
        Centre();
        _avatar.Show(steamId, name, colour, dim);
        LobbyUi.Set(_name, name);

        // Set at each draw: its prefab is optional and loads by itself, and an image with no sprite is a white square.
        Sprite? crownSprite = crown ? NativeArt.Get(NativeArt.Crown) : null;
        _crown.sprite = crownSprite;
        LobbyUi.Show(_crown, crownSprite != null);
        LobbyUi.Set(_role, role ?? string.Empty);
        _role.rectTransform.anchoredPosition = new Vector2(crown ? _name.rectTransform.anchoredPosition.x + 54 : _name.rectTransform.anchoredPosition.x, _role.rectTransform.anchoredPosition.y);
        LobbyUi.Set(_lineA, line ?? string.Empty);
        LobbyUi.Set(_count, count ?? string.Empty);
        _signal.Show(bars, pingMs, true);
    }

    /// <summary>The person's block keeps its size on a wide well, in its middle.</summary>
    private void Centre()
    {
        float width = _well.rect.width;
        if (width > 0 && !Mathf.Approximately(width, _centredFor))
        {
            _centredFor = width;
            float side = Math.Max(40, (width - PersonWidth) / 2);
            LobbyUi.Fill(_person, side, 32, side, 32);
        }
    }

    /// <summary>A big centred line in the well: an empty state, or a wait.</summary>
    public void ShowLine(string line)
    {
        LobbyUi.Show(_person, false);
        LobbyUi.Show(_big, true);
        LobbyUi.Set(_big, line);
    }

    /// <summary>The words in the box under the well.</summary>
    public void SetWords(string words) => LobbyUi.Set(_words, words);

    /// <summary>Its picture, when the cache changed.</summary>
    public void Refresh() => _avatar.Refresh();

    private static void Across(RectTransform rect, float left, float y, float height)
    {
        rect.anchorMin = new Vector2(0, 0.5f);
        rect.anchorMax = new Vector2(1, 0.5f);
        rect.pivot = new Vector2(0, 0.5f);
        rect.anchoredPosition = new Vector2(left, y);
        rect.sizeDelta = new Vector2(-left, height);
    }
}
#endif
