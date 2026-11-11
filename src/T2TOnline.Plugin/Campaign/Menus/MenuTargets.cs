#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// Names a menu element the same way on two PCs: nothing in a navigable is a stable id, but the reward cards, event choices and list
/// selectors are made in data order under the menu's root, so the sibling indices from the root lead to the same element when both PCs built
/// the menu from the same data. The element's object name rides along as a check.
/// </summary>
internal static class MenuTargets
{
    /// <summary>Host: <paramref name="element"/>'s path from <paramref name="root"/>, or null when it isn't under it (or too deep).</summary>
    public static MenuTarget? PathTo(Transform root, Component element)
    {
        var steps = new List<int>();
        Transform? at = element.transform;
        while (at != null && at.Pointer != root.Pointer)
        {
            int index = at.GetSiblingIndex();
            if (steps.Count >= MenuTarget.MaxDepth || index < 0 || index > MenuTarget.MaxSiblingIndex)
            {
                return null;
            }

            steps.Add(index);
            at = at.parent;
        }

        if (at == null || steps.Count == 0)
        {
            return null;
        }

        steps.Reverse();
        return MenuTarget.AtPath(steps, element.gameObject.name ?? string.Empty);
    }

    /// <summary>
    /// Friend: the navigable <paramref name="target"/> names under <paramref name="root"/>, or null. The path is followed
    /// and its end checked by name; failing that, the one navigable of that name at that depth (a list that grew or
    /// shrank by an element the friend's game shows differently).
    /// </summary>
    public static AbstractNavigable? Find(Transform root, MenuTarget target)
    {
        if (target.Kind != MenuTargetKind.Path)
        {
            return null;
        }

        Transform? at = root;
        foreach (int step in target.Path)
        {
            if (step >= at.childCount)
            {
                at = null;
                break;
            }

            at = at.GetChild(step);
        }

        AbstractNavigable? found = at?.GetComponent<AbstractNavigable>();
        if (found != null && SameName(found.gameObject.name, target.Leaf))
        {
            return found;
        }

        AbstractNavigable? only = null;
        foreach (AbstractNavigable candidate in root.GetComponentsInChildren<AbstractNavigable>(true))
        {
            if (candidate == null || !SameName(candidate.gameObject.name, target.Leaf) || Depth(root, candidate.transform) != target.Path.Count)
            {
                continue;
            }

            if (only != null)
            {
                return null; // two of that name: can't tell which
            }

            only = candidate;
        }

        return only;
    }

    private static int Depth(Transform root, Transform element)
    {
        int depth = 0;
        for (Transform? at = element; at != null && at.Pointer != root.Pointer; at = at.parent)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>The local object name against the host's (made safe, and cut to <see cref="MenuTarget.MaxLeafBytes"/> on the way).</summary>
    private static bool SameName(string? local, string sent)
    {
        string mine = SafeText.Clean(local, 255);
        return mine == sent || (sent.Length >= MenuTarget.MaxLeafBytes / 2 && mine.StartsWith(sent, StringComparison.Ordinal));
    }
}
#endif
