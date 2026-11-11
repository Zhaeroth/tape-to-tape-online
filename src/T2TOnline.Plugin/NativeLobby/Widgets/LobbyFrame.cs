#if HAS_INTEROP
using System;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// What every screen does to the Glossary's frame. Its animator binds the header, tabs, content, list and details by name and keys their anchored positions, so those keep their names and are resized
/// by anchors and size only.
/// </summary>
internal static class LobbyFrame
{
    /// <summary>The tab band's LB and RB glyphs and its tabs' row go: no tabs here (UX: one screen, no tabs).</summary>
    public static void HideTabGlyphs(ShellParts parts)
    {
        UiClone.Destroy(parts.Tabs.Find("UIInputShortcut_V2-PrevTab")?.gameObject);
        UiClone.Destroy(parts.Tabs.Find("UIInputShortcut_V2-NextTab")?.gameObject);
        UiClone.Destroy(parts.Tabs.Find("TabsContainer")?.gameObject);
    }

    /// <summary>A centred line across the tab band: its one line of news. Empty, the band is plain.</summary>
    public static TextMeshProUGUI BandLabel(ShellParts parts)
    {
        TextMeshProUGUI band = LobbyUi.Label(LobbyFont.Button, parts.Tabs, "Band", 36, minSize: 26, align: TextAlignmentOptions.Center);
        LobbyUi.Fill(band.rectTransform, 200, 0, 200, 0);
        return band;
    }

    /// <summary>
    /// The list's column to a fixed width, the details filling the rest: the frame's own 35% would make a very long row on a wide window. The list sits at the frame's left; the details keep their
    /// animated x, measured from the right edge (pivot 1), so both edges stay put at any width.
    /// </summary>
    public static void FixedColumn(ShellParts parts, float width)
    {
        const float listLeft = 48;
        const float gap = 16;
        RectTransform list = parts.ScrollView;
        list.anchorMin = new Vector2(0, 0);
        list.anchorMax = new Vector2(0, 1);
        list.pivot = new Vector2(0, 0.5f);
        list.sizeDelta = new Vector2(width, list.sizeDelta.y);
        list.anchoredPosition = new Vector2(listLeft, list.anchoredPosition.y);
        RectTransform details = parts.Details;
        float y = details.sizeDelta.y;
        details.anchorMin = new Vector2(0, details.anchorMin.y);
        details.anchorMax = new Vector2(1, details.anchorMax.y);
        details.pivot = new Vector2(1, details.pivot.y);
        details.sizeDelta = new Vector2(-(listLeft + width + gap + DetailsRestX), y);
    }

    /// <summary>How far left of the content's right edge the animator rests the details.</summary>
    public const float DetailsRestX = 64;

    /// <summary>The list's content laid out as the Glossary's script would: rows down, the game's own padding and spacing. Its group fades the rows while a join is on.</summary>
    public static CanvasGroup RowsContent(ShellParts parts)
    {
        GameObject content = parts.ScrollContent.gameObject;
        VerticalLayoutGroup layout = content.AddComponent(Il2CppType.Of<VerticalLayoutGroup>())?.TryCast<VerticalLayoutGroup>() ?? throw new InvalidOperationException("the list can't lay out");
        layout.spacing = 4;
        layout.padding = new RectOffset(28, 28, 12, 12);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        ContentSizeFitter fitter = content.AddComponent(Il2CppType.Of<ContentSizeFitter>())?.TryCast<ContentSizeFitter>() ?? throw new InvalidOperationException("the list can't fit");
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return content.GetComponent<CanvasGroup>() ?? content.AddComponent(Il2CppType.Of<CanvasGroup>())?.TryCast<CanvasGroup>() ?? throw new InvalidOperationException("the list can't fade");
    }
}
#endif
