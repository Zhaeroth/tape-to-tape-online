#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2CppBishopGames.UI;
using Il2CppI2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// Reads a screen for the check tour: one line per active rect (path, canvas rect, components; for text: term, shown text, font, size, preferred vs available) and the plain
/// facts <see cref="UiCheckRules"/> judges. Slow and allocating: only the tour calls it.
/// </summary>
internal static class UiDump
{
    private static readonly string[] Quiet = { "RectTransform", "CanvasRenderer" };

    /// <summary>What was read: the lines of the dump, and the flags.</summary>
    /// <param name="Text">The dump.</param>
    /// <param name="Hits">The flags on it.</param>
    /// <param name="Images">Active images read.</param>
    /// <param name="WithoutSprite">Those of them with no sprite: an enabled image left white.</param>
    public sealed record Result(string Text, IReadOnlyList<UiCheckHit> Hits, int Images, int WithoutSprite);

    /// <summary>Reads <paramref name="root"/> (a canvas's object: a menu's root or the title's), against its own canvas's rect.</summary>
    public static Result Read(GameObject root, IReadOnlyCollection<IntPtr> defaults)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(defaults);
        var text = new StringBuilder();
        RectTransform? canvasRect = root.GetComponent<RectTransform>();
        if (canvasRect == null)
        {
            return new Result($"{root.name}: not a UI object\n", Array.Empty<UiCheckHit>(), 0, 0);
        }

        Rect canvas = canvasRect.rect;
        var corners = new Il2CppStructArray<Vector3>(4);
        var elements = new List<UiElement>();
        var indexOf = new Dictionary<IntPtr, int>();
        var paths = new List<string>();
        int images = 0;
        int withoutSprite = 0;
        text.Append(CultureInfo.InvariantCulture, $"canvas {root.name}: {canvas.width:0}x{canvas.height:0}, window {Screen.width}x{Screen.height}, language {LocalizationManager.CurrentLanguage}\n");
        foreach (Component? found in root.GetComponentsInChildren(Il2CppType.Of<RectTransform>(), false))
        {
            RectTransform? rect = found?.TryCast<RectTransform>();
            if (rect == null)
            {
                continue;
            }

            Transform? parent = rect.parent;
            int parentIndex = parent != null && indexOf.TryGetValue(parent.Pointer, out int at) ? at : -1;
            string path = parentIndex >= 0 ? $"{paths[parentIndex]}/{rect.gameObject.name}" : rect.gameObject.name;
            indexOf[rect.Pointer] = elements.Count;
            paths.Add(path);
            rect.GetWorldCorners(corners);
            Vector3 low = canvasRect.InverseTransformPoint(corners[0]);
            Vector3 high = canvasRect.InverseTransformPoint(corners[2]);
            var place = new UiRect(low.x - canvas.x, low.y - canvas.y, high.x - low.x, high.y - low.y);
            var names = new List<string>();
            foreach (Component? component in rect.gameObject.GetComponents(Il2CppType.Of<Component>()))
            {
                if (component != null)
                {
                    names.Add(component.GetIl2CppType().Name);
                }
            }

            AbstractNavigable? navigable = rect.GetComponent<AbstractNavigable>();
            bool canNavigate = navigable != null && navigable.TryCast<NavigableParent>() == null && navigable.IsNavigable;
            Image? image = rect.GetComponent<Image>();
            if (image != null && image.enabled)
            {
                images++;
                if (image.sprite == null)
                {
                    withoutSprite++;
                }
            }

            Mask? mask = rect.GetComponent<Mask>();
            RectMask2D? mask2D = rect.GetComponent<RectMask2D>();
            UiTextFacts? facts = ReadText(rect);
            var element = new UiElement
            {
                Path = path,
                Parent = parentIndex,
                Rect = place,
                Text = facts,
                Navigable = canNavigate,
                Neighbors = canNavigate ? Neighbors(navigable!) : 0,
                Masks = (mask != null && mask.enabled) || (mask2D != null && mask2D.enabled),
                IsDefault = navigable != null && defaults.Contains(navigable.Pointer),
            };
            elements.Add(element);
            text.Append(CultureInfo.InvariantCulture, $"{path} | {place.X:0.#} {place.Y:0.#} {place.Width:0.#} {place.Height:0.#} | {string.Join(",", names.Where(n => !Quiet.Contains(n)))}");
            if (facts != null)
            {
                TMP_Text label = rect.GetComponent<TMP_Text>()!;
                text.Append(CultureInfo.InvariantCulture, $" | term={facts.Term ?? "-"} text=\"{SafeText.Clean(facts.Shown, 80)}\" font={label.font?.name} size={facts.Size:0.#} preferred={facts.PreferredWidth:0.#} available={facts.AvailableWidth:0.#}");
            }

            text.Append('\n');
        }

        var box = new UiRect(0, 0, canvas.width, canvas.height);
        List<UiCheckHit> hits = UiCheckRules.Compute(elements, box);
        foreach (UiCheckHit hit in hits)
        {
            text.Append(CultureInfo.InvariantCulture, $"FLAG {UiCheckRules.Name(hit.Flag)} {hit.Path}: {hit.Detail}\n");
        }

        return new Result(text.ToString(), hits, images, withoutSprite);
    }

    private static UiTextFacts? ReadText(RectTransform rect)
    {
        TMP_Text? label = rect.GetComponent<TMP_Text>();
        if (label == null)
        {
            return null;
        }

        string shown = label.text ?? string.Empty;
        Localize? localize = rect.GetComponent<Localize>();
        string? term = localize == null ? null : localize.Term;
        bool hasText = string.IsNullOrEmpty(term) || !string.IsNullOrEmpty(LocalizationManager.GetTranslation(term, true, 0, true, false, null, null, true));
        return new UiTextFacts
        {
            Shown = shown,
            Term = string.IsNullOrEmpty(term) ? null : term,
            TermHasText = hasText,
            PreferredWidth = label.preferredWidth,
            AvailableWidth = rect.rect.width,
            Wraps = label.textWrappingMode != TextWrappingModes.NoWrap,
            Truncated = label.isTextTruncated,
            AutoSizes = label.enableAutoSizing,
            Size = label.fontSize,
            SizeMin = label.fontSizeMin,
            MissingGlyphs = Missing(label, shown),
        };
    }

    /// <summary>
    /// The characters of the shown text its font chain lacks: the asset and its own fallbacks, then the global ones (the game's CJK fonts and Liberation hang there, by language;
    /// verified in the game's code: <c>TmpFontRegistry</c>). Tags don't count; nothing is added to an atlas.
    /// </summary>
    private static string Missing(TMP_Text label, string shown)
    {
        TMP_FontAsset? font = label.font;
        string plain = MenuGame.Plain(shown, 500);
        if (font == null || plain.Length == 0)
        {
            return string.Empty;
        }

        if (font.HasCharacters(plain, out Il2CppStructArray<uint> missing, true, false) || missing == null)
        {
            return string.Empty;
        }

        var lacking = new StringBuilder();
        for (int i = 0; i < missing.Length && i < 16; i++)
        {
            uint code = missing[i];
            if (code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) && !InGlobalFallbacks(code))
            {
                lacking.Append(char.ConvertFromUtf32((int)code));
            }
        }

        return lacking.ToString();
    }

    private static bool InGlobalFallbacks(uint code)
    {
        if (code > char.MaxValue)
        {
            return false;
        }

        var character = (char)code;
        Il2CppSystem.Collections.Generic.List<TMP_FontAsset>? fallbacks = TMP_Settings.fallbackFontAssets;
        for (int i = 0; fallbacks != null && i < fallbacks.Count; i++)
        {
            if (fallbacks[i] != null && fallbacks[i].HasCharacter(character, true, false))
            {
                return true;
            }
        }

        TMP_FontAsset? fallback = TMP_Settings.defaultFontAsset;
        return fallback != null && fallback.HasCharacter(character, true, false);
    }

    private static int Neighbors(AbstractNavigable navigable)
    {
        Il2CppReferenceArray<AbstractNavigable>? links = navigable.m_Neighbors;
        int count = 0;
        for (int i = 0; links != null && i < links.Length; i++)
        {
            if (links[i] != null)
            {
                count++;
            }
        }

        return count;
    }
}
#endif
