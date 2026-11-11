#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// Makes a copy of a live or prefab UI object safely: instantiated under an inactive
/// holder with the non-generic overload (a generic instance may not be in this build), so nothing in it wakes; every component outside an
/// allow-list destroyed, in reverse order (dependants first) and checked gone, else the copy is destroyed and the caller told; then moved
/// to its place. An allow-list and not a block-list, because a patch adding a new script must get it stripped, not woken: the scripts kept
/// are known to need nothing but Unity (no injected fields, their module from <c>BishopModule</c>, events made in constructors).
/// </summary>
/// <remarks>
/// New to this mod and how they're called, from the interop assemblies: <c>Object.Instantiate(Object, Transform)</c>,
/// <c>Object.DestroyImmediate(Object)</c>, <c>Transform.SetParent(Transform, bool)</c> and
/// <c>LayoutRebuilder.ForceRebuildLayoutImmediate(RectTransform)</c> are plain unsafe static or instance wrappers over the game's own
/// methods: reference and bool arguments, no by-value struct to trip on. The game itself calls <c>SetParent(parent, false)</c> when it takes a
/// preinstantiated menu.
/// </remarks>
internal static class UiClone
{
    /// <summary>The scripts a plain clone keeps (a row, a label, a bar's shortcut).</summary>
    public static readonly HashSet<string> Widgets = new(StringComparer.Ordinal)
    {
        // Engine
        "Transform", "RectTransform", "CanvasRenderer", "Canvas", "CanvasGroup", "Animator", "CanvasScaler", "GraphicRaycaster",

        // UGUI and TextMeshPro
        "Image", "RawImage", "Mask", "RectMask2D", "TextMeshProUGUI", "LayoutElement", "VerticalLayoutGroup", "HorizontalLayoutGroup",
        "GridLayoutGroup", "ContentSizeFitter", "ScrollRect", "Scrollbar",

        // The game's own, with nothing behind them but Unity
        "Localize", "AnimatedButton", "UnselectableAnimatedButton", "NavigableSFXFMOD", "InputShortcutSFXFMOD", "UIInputShortcut", "ButtonInputShortcut",
        "LayoutElementAspectRatio", "InputSpriteUI", "UnscaledTimeMaterial", "FontSizeMatcher", "AutoHideScrollbar", "AutoScrollOnNavigableHighlight",
        "NavigableParentSelectClosest", "NavigableParentSelectDefault", "NavigableParentSelectPrevious", "NavigableParentGrid",
    };

    private static GameObject? _holder;

    /// <summary>The inactive holder clones wake under (made on demand; it goes with its scene, and the next call makes another).</summary>
    public static Transform Holder()
    {
        if (_holder == null || _holder.WasCollected)
        {
            _holder = new GameObject("T2T lobby parts");
            _holder.SetActive(false);
        }

        return _holder.transform;
    }

    /// <summary>
    /// A stripped copy of <paramref name="source"/> under the inactive holder, or null with why (nothing is left behind). It's asleep until
    /// the caller moves it under something active.
    /// </summary>
    public static GameObject? Make(GameObject source, ISet<string> keep, string what, out string problem)
    {
        ArgumentNullException.ThrowIfNull(source);
        GameObject? clone = null;
        try
        {
            // The non-generic overload: a generic instance may not be in this build.
            clone = Object.Instantiate((Object)source, Holder())?.TryCast<GameObject>();
            if (clone == null)
            {
                problem = $"{what}: Instantiate gave no object";
                return null;
            }

            if (clone.activeInHierarchy)
            {
                problem = $"{what}: awake under the inactive holder";
                Object.DestroyImmediate(clone);
                return null;
            }

            if (!Strip(clone, keep, out int stripped, out problem))
            {
                problem = $"{what}: {problem}";
                Object.DestroyImmediate(clone);
                return null;
            }

            Stripped += stripped;
            problem = string.Empty;
            return clone;
        }
        catch (Exception ex)
        {
            problem = $"{what}: {ModLog.SafeErrorText(ex)}";
            Destroy(clone);
            return null;
        }
    }

    /// <summary>The scripts stripped so far (for the log).</summary>
    public static int Stripped { get; private set; }

    /// <summary>
    /// Destroys every component under <paramref name="root"/> whose class isn't in <paramref name="keep"/>, in reverse order, checking each is
    /// gone. A second pass catches one another component required on the first. False, with the class, when one can't go.
    /// </summary>
    public static bool Strip(GameObject root, ISet<string> keep, out int stripped, out string problem)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(keep);
        stripped = 0;
        problem = string.Empty;
        for (int pass = 0; pass < 2; pass++)
        {
            string? stuck = null;
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Component> all = root.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
            for (int i = all.Length - 1; i >= 0; i--)
            {
                Component? component = all[i];
                if (component == null)
                {
                    continue; // a missing script: nothing to run
                }

                string name = component.GetIl2CppType().Name;
                if (keep.Contains(name))
                {
                    continue;
                }

                Object.DestroyImmediate(component);
                if (component != null)
                {
                    stuck = name;
                }
                else
                {
                    stripped++;
                }
            }

            if (stuck == null)
            {
                return true;
            }

            problem = $"the script {stuck} can't be destroyed";
        }

        return false;
    }

    /// <summary>Moves a clone to its place (it wakes there if the place is active); its local layout stays.</summary>
    public static void Place(GameObject clone, Transform parent, int siblingIndex = -1)
    {
        clone.transform.SetParent(parent, false);
        if (siblingIndex >= 0)
        {
            clone.transform.SetSiblingIndex(siblingIndex);
        }
    }

    /// <summary>The child at <paramref name="path"/> under <paramref name="root"/>, or null; "" is the root itself.</summary>
    public static Transform? Find(Transform root, string path) => path.Length == 0 ? root : root.Find(path);

    /// <summary>The layout under <paramref name="root"/> made now, not next frame.</summary>
    public static void Rebuild(Component root)
    {
        RectTransform? rect = root.GetComponent<RectTransform>();
        if (rect != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }
    }

    /// <summary>Destroys an object if it's still there. Never throws.</summary>
    public static void Destroy(Object? thing)
    {
        try
        {
            if (thing != null && !thing.WasCollected)
            {
                Object.DestroyImmediate(thing);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: destroying a part failed ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
