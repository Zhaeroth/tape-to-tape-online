#if HAS_INTEROP
using System;
using Il2CppI2.Loc;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>The game's text looks, each cloned from a real label of the prefab it lives on, so font, material and outline are the game's own.</summary>
internal enum LobbyFont
{
    /// <summary>Titles and big numbers: Bungee with its black outline.</summary>
    Title,

    /// <summary>Buttons, prompts, names: NotoSans-BlackItalic with its outline.</summary>
    Button,

    /// <summary>Body text: NotoSans-Black.</summary>
    Body,

    /// <summary>Section labels: NotoSans-Black, its small material.</summary>
    Section,
}

/// <summary>
/// What every widget shares: the source prefabs' paths, the game's colours, plain rect helpers, labels cloned from the game's.
/// Screens lay out by anchors, so a window of any shape keeps its layout (the canvas matches height: 1080 high, any width).
/// </summary>
internal static class LobbyUi
{
    /// <summary>Glossary's prefab: the shell, its rows and its bar.</summary>
    public const string Glossary = "Glossary";

    /// <summary>Game Settings' prefab: the selector and the button.</summary>
    public const string Settings = "GameSettings";

    /// <summary>The controller screen's prefab: the lightning, the ready banner, the device icons, the number box, the Start frame.</summary>
    public const string Controllers = "ControllerAssignment";

    /// <summary>White text.</summary>
    public static readonly Color White = Color.white;

    /// <summary>Body text, the game's #E8E8E8.</summary>
    public static readonly Color BodyText = Hex(0xE8E8E8);

    /// <summary>Secondary text on a panel, the game's #A6CADB (a selector's value).</summary>
    public static readonly Color SoftText = Hex(0xA6CADB);

    /// <summary>Disabled text, the game's #909090.</summary>
    public static readonly Color Disabled = Hex(0x909090);

    /// <summary>The game's pink accent, #FD466C.</summary>
    public static readonly Color Pink = Hex(0xFD466C);

    /// <summary>The game's gold, #FFD65A.</summary>
    public static readonly Color Gold = Hex(0xFFD65A);

    /// <summary>The game's green, #29B25E.</summary>
    public static readonly Color Green = Hex(0x29B25E);

    /// <summary>The game's blue accent, #3698E8.</summary>
    public static readonly Color Blue = Hex(0x3698E8);

    /// <summary>A panel's fill, the game's #1B2D37.</summary>
    public static readonly Color Well = Hex(0x1B2D37);

    /// <summary>A colour from its hex value.</summary>
    public static Color Hex(int rgb, float alpha = 1f) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    /// <summary>A new empty UI object.</summary>
    public static RectTransform New(Transform parent, string name)
    {
        var made = new GameObject(name);
        RectTransform rect = made.AddComponent(Il2CppType.Of<RectTransform>())?.TryCast<RectTransform>() ?? throw new InvalidOperationException($"{name} can't be a UI object");
        rect.SetParent(parent, false);
        return rect;
    }

    /// <summary>A new image; sliced when its sprite has borders and <paramref name="sliced"/>.</summary>
    public static Image Picture(Transform parent, string name, Sprite? sprite, Color color, bool sliced = false, bool raycast = false)
    {
        RectTransform rect = New(parent, name);
        Image image = rect.gameObject.AddComponent(Il2CppType.Of<Image>())?.TryCast<Image>() ?? throw new InvalidOperationException($"{name} can't be an image");
        image.sprite = sprite;
        image.color = color;
        image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        image.preserveAspect = !sliced && sprite != null;
        image.raycastTarget = raycast;
        return image;
    }

    /// <summary>A new raw image, for a Steam avatar (no sprite).</summary>
    public static RawImage Raw(Transform parent, string name)
    {
        RectTransform rect = New(parent, name);
        RawImage raw = rect.gameObject.AddComponent(Il2CppType.Of<RawImage>())?.TryCast<RawImage>() ?? throw new InvalidOperationException($"{name} can't be a raw image");
        raw.raycastTarget = false;
        return raw;
    }

    /// <summary>Fills the parent, less the given margins.</summary>
    public static void Fill(RectTransform rect, float left = 0, float bottom = 0, float right = 0, float top = 0)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>A fixed size at an anchor of the parent.</summary>
    public static void At(RectTransform rect, float anchorX, float anchorY, float x, float y, float width, float height, float pivotX = 0.5f, float pivotY = 0.5f)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(anchorX, anchorY);
        rect.pivot = new Vector2(pivotX, pivotY);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, y);
    }

    /// <summary>A band across the parent's width, a fixed height, its top <paramref name="fromTop"/> below the parent's top, less side margins.</summary>
    public static void TopBand(RectTransform rect, float fromTop, float height, float sides = 0)
    {
        rect.anchorMin = new Vector2(0, 1);
        rect.anchorMax = new Vector2(1, 1);
        rect.pivot = new Vector2(0.5f, 1);
        rect.sizeDelta = new Vector2(-2 * sides, height);
        rect.anchoredPosition = new Vector2(0, -fromTop);
    }

    /// <summary>A band across the parent's width, a fixed height, its bottom <paramref name="fromBottom"/> above the parent's bottom, less side margins.</summary>
    public static void BottomBand(RectTransform rect, float fromBottom, float height, float sides = 0)
    {
        rect.anchorMin = new Vector2(0, 0);
        rect.anchorMax = new Vector2(1, 0);
        rect.pivot = new Vector2(0.5f, 0);
        rect.sizeDelta = new Vector2(-2 * sides, height);
        rect.anchoredPosition = new Vector2(0, fromBottom);
    }

    /// <summary>A label in the game's own look: a clone of a label of the held prefab, with no <c>Localize</c> (its text comes from <c>Say</c>). Throws when the source has gone.</summary>
    public static TextMeshProUGUI Label(LobbyFont font, Transform parent, string name, float size, float minSize = 0, TextAlignmentOptions align = TextAlignmentOptions.Left, Color? color = null)
    {
        (string source, string path) = font switch
        {
            LobbyFont.Title => (Glossary, "ScaledContainer/Header/Title"),
            LobbyFont.Button => (Glossary, "ScaledContainer/BottomBar/Btn-Back/Label"),
            LobbyFont.Body => (Glossary, "ScaledContainer/Content/Details/DescriptionBox/Description"),
            _ => (Settings, "ScaledContainer/Content/Items/GameSetting/Title"),
        };
        GameObject part = UiAssets.Part(source, path) ?? throw new InvalidOperationException($"the label {source}/{path} isn't held");
        GameObject clone = UiClone.Make(part, UiClone.Widgets, name, out string problem) ?? throw new InvalidOperationException(problem);
        clone.name = name;
        Localize? localize = clone.GetComponent<Localize>();
        if (localize != null)
        {
            UiClone.Destroy(localize);
        }

        LayoutElement? layout = clone.GetComponent<LayoutElement>();
        if (layout != null)
        {
            UiClone.Destroy(layout);
        }

        UiClone.Place(clone, parent);
        TextMeshProUGUI label = clone.GetComponent<TextMeshProUGUI>() ?? throw new InvalidOperationException($"{name} has no text");
        label.text = string.Empty;
        label.fontSize = size;
        label.enableAutoSizing = minSize > 0;
        if (minSize > 0)
        {
            label.fontSizeMin = minSize;
            label.fontSizeMax = size;
        }

        label.alignment = align;
        label.color = color ?? White;
        label.raycastTarget = false;
        label.richText = false; // the mod's words and game terms, with names in them: a name's tags stay text
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Ellipsis;
        return label;
    }

    /// <summary>A label that shows a Steam name: rich text and typed escapes off, cut with an ellipsis (<see cref="DisplayName"/> has cleaned the name).</summary>
    public static void ForName(TMP_Text label)
    {
        label.richText = false;
        label.parseCtrlCharacters = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
    }

    /// <summary>Sets a label's text only when it differs; empty hides nothing: the caller decides that.</summary>
    public static void Set(TMP_Text? label, string text)
    {
        if (label != null && !string.Equals(label.text, text, StringComparison.Ordinal))
        {
            label.text = text;
        }
    }

    /// <summary>A line in this PC's language on a label; null clears it.</summary>
    public static void Say(TMP_Text? label, LobbyLine? line) => Set(label, line == null ? string.Empty : LobbyLocalization.Say(line));

    /// <summary>A term in this PC's language on a label.</summary>
    public static void Say(TMP_Text? label, string term) => Set(label, LobbyLocalization.Say(term));

    /// <summary>Turns an object on or off, only when it differs.</summary>
    public static void Show(Component? thing, bool show)
    {
        if (thing != null && thing.gameObject.activeSelf != show)
        {
            thing.gameObject.SetActive(show);
        }
    }

    /// <summary>A clone of a part of a held prefab, stripped by <paramref name="keep"/>, placed under <paramref name="parent"/>. Throws with what's wrong.</summary>
    public static GameObject Part(string source, string path, Transform parent, System.Collections.Generic.ISet<string> keep, string name)
    {
        GameObject part = UiAssets.Part(source, path) ?? throw new InvalidOperationException($"the part {source}/{path} isn't held");
        GameObject clone = UiClone.Make(part, keep, name, out string problem) ?? throw new InvalidOperationException(problem);
        clone.name = name;
        UiClone.Place(clone, parent);
        return clone;
    }

    /// <summary>A rect's transform as a RectTransform. Throws when it isn't one.</summary>
    public static RectTransform RectOf(GameObject thing) => thing.GetComponent<RectTransform>() ?? throw new InvalidOperationException($"{thing.name} isn't a UI object");
}
#endif
