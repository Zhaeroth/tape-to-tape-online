#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby.Widgets;

/// <summary>
/// A left and right selector with its label: Game Settings' own <c>GameSetting</c> (a title over a <c>CycleList</c>, its arrows, dots, sounds and animator). Left and right come from the list's
/// own dead-end handler, so its left and right neighbours stay empty. On a friend it's read-only: no arrows, not navigable, the value following the host's.
/// </summary>
internal sealed class Selector
{
    private static readonly HashSet<string> Keep = MakeKeep();

    private readonly TextMeshProUGUI _label;
    private readonly Action<int> _chose;
    private Il2CppSystem.Action<Il2CppSystem.Object>? _changed; // kept alive for the game's event
    private string[] _items = Array.Empty<string>();
    private bool _setting;
    private bool _registered;

    private Selector(RectTransform root, CycleList list, TextMeshProUGUI label, Action<int> chose)
    {
        Root = root;
        List = list;
        _label = label;
        _chose = chose;
    }

    /// <summary>Its object: a label over a selector.</summary>
    public RectTransform Root { get; }

    /// <summary>The game's list: what the cursor lands on.</summary>
    public CycleList List { get; }

    /// <summary>Makes one. <paramref name="chose"/> is told a choice the player made, not one <see cref="Set"/> made.</summary>
    public static Selector Make(Transform parent, string name, Action<int> chose)
    {
        GameObject clone = LobbyUi.Part(LobbyUi.Settings, "ScaledContainer/Content/Items/GameSetting", parent, Keep, name);
        Transform title = clone.transform.Find("Title") ?? throw new InvalidOperationException("the selector has no title");
        Il2CppI2.Loc.Localize? localize = title.GetComponent<Il2CppI2.Loc.Localize>();
        if (localize != null)
        {
            UiClone.Destroy(localize);
        }

        TextMeshProUGUI label = title.GetComponent<TextMeshProUGUI>() ?? throw new InvalidOperationException("the selector's title isn't text");
        label.alignment = TextAlignmentOptions.Left;
        label.enableAutoSizing = true;
        label.fontSizeMax = 26;
        label.fontSizeMin = 20;
        label.fontSize = 26;
        CycleList list = clone.GetComponentInChildren(Il2CppType.Of<CycleList>(), true)?.TryCast<CycleList>() ?? throw new InvalidOperationException("the selector has no list");
        list.ClearNeighbors();

        // The game's list stops at its ends (verified in the game's code: OnNavigationDeadEnd; seen with a pad): left of the first and right of the last do nothing.
        return new Selector(LobbyUi.RectOf(clone), list, label, chose);
    }

    /// <summary>The label's text.</summary>
    public void SetLabel(string term) => LobbyUi.Say(_label, term);

    /// <summary>The woken list starts telling us its choices.</summary>
    public void Wake()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        _changed = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Il2CppSystem.Object>>(new Action<Il2CppSystem.Object>(OnChanged))
            ?? throw new InvalidOperationException("the selector's delegate couldn't be made");
        List.OnSelectionChanged.Register(_changed, "T2TOnline");
    }

    /// <summary>The items, redone if they changed, and the one showing. A choice made here tells nobody.</summary>
    public void Set(string[] items, int index)
    {
        _setting = true;
        try
        {
            if (!Same(items, _items))
            {
                _items = items;
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (string item in items)
                {
                    list.Add(item);
                }

                List.Init(list.Cast<Il2CppSystem.Collections.IList>());
            }

            if (List.CurrentIndex != index)
            {
                List.SelectIndex(index, false);
            }
        }
        finally
        {
            _setting = false;
        }
    }

    /// <summary>A friend's rows can't be changed: no arrows, not navigable.</summary>
    public void SetReadOnly(bool readOnly)
    {
        List.IsNavigable = !readOnly;
        LobbyUi.Show(List.transform.Find("ArrowLeft"), !readOnly);
        LobbyUi.Show(List.transform.Find("ArrowRight"), !readOnly);
    }

    private static bool Same(string[] a, string[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void OnChanged(Il2CppSystem.Object _)
    {
        try
        {
            if (!_setting)
            {
                _chose(List.CurrentIndex);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: a selector's change failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static HashSet<string> MakeKeep()
    {
        var keep = new HashSet<string>(UiClone.Widgets, StringComparer.Ordinal)
        {
            "CycleList", "CycleListDot", "UnselectableColorButton",
        };
        return keep;
    }
}
#endif
