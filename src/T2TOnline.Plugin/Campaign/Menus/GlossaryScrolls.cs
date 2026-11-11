#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppUICore.ReusableList;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using ScrollRect = UnityEngine.UI.ScrollRect;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The glossary's list scrolled as the host's (v1). The glossary's rows are a recycled list on a <c>ScrollRect</c>, its
/// entries in the order of their names in each PC's language, and a friend's own <c>SelectIndex</c> puts the row it moves to
/// at the top: so the host's game measures where its highlighted row sits in the list's visible area
/// (<see cref="GlossaryScroll"/>, value <see cref="Key"/> of the glossary's data, read with it), and a friend's moves its own
/// list so its row of the same entry sits there.
/// <list type="bullet">
/// <item>Host: while a glossary is its newest menu with a friend in, every frame compares the list's content position and the
/// entry shown with the last (no allocation); when either moved, the row is measured again, and a move of half a percent or
/// more reads and sends the glossary's data again (at most four times a second while it moves; once more when it
/// settles).</item>
/// <item>Friend: each time the host's entry is applied (its step, the host's data, the view's re-apply frames), after its row
/// is highlighted, the list's content moves by the difference (world height) and stops; the list's own scroll handler makes
/// its rows again. Display only.</item>
/// </list>
/// Nothing here throws into the game: the callers guard it, and a failure leaves the list where it is.
/// </summary>
internal static class GlossaryScrolls
{
    /// <summary>Value: the host's highlighted row's place in its list, in thousandths of the list's height (<see cref="GlossaryScroll"/>).</summary>
    public const string Key = "gl.rowY";

    private const double SendEverySeconds = 0.25;
    private const int NeighbourCells = 20;

    private static readonly Il2CppStructArray<Vector3> Corners = new(4);
    private static readonly Dictionary<IntPtr, int> LastRead = new();
    private static readonly HashSet<IntPtr> LoggedViews = new();

    // Host: the glossary watched, its list's content and what it showed last frame.
    private static IntPtr _watched;
    private static GlossaryMenu? _glossary;
    private static RectTransform? _content;
    private static Vector2 _lastPosition;
    private static IntPtr _lastEntry;
    private static bool _pending;
    private static double _sentAt;
    private static int _shownEntryOffset = -1;
    private static int _errors;

    // The last entry found in a tab's entries (the list's address, the entry's, its place).
    private static IntPtr _foundIn;
    private static IntPtr _foundEntry;
    private static int _foundAt;

    /// <summary>
    /// The glossary's highlighted row's place in its list (thousandths of the visible area's height, from its top), or null
    /// when it can't be measured: the row of <paramref name="entry"/> (the entry shown when null); a row the list hasn't made
    /// (scrolled out of sight) placed from its nearest made neighbour, rows being alike.
    /// </summary>
    public static int? Measure(GlossaryMenu menu, GlossaryEntrySO? entry = null)
    {
        GlossaryScrollList? list = menu.m_List;
        ScrollRect? scroll = list?.ScrollRect;
        RectTransform? area = scroll == null ? null : scroll.viewport != null ? scroll.viewport : scroll.transform.TryCast<RectTransform>();
        entry ??= menu.m_ShownEntry;
        if (list == null || area == null || entry == null || IndexOf(menu, entry) is not int index)
        {
            return null;
        }

        area.GetWorldCorners(Corners);
        float areaTop = Corners[1].y;
        float areaBottom = Corners[0].y;
        float? rowTop = RowTop(list, index);
        return rowTop is float top ? GlossaryScroll.Milli(areaTop, areaBottom, top) : null;
    }

    /// <summary>Host: the glossary's data is being read: its row's place goes with it (and is what later moves are measured from).</summary>
    public static void Read(GlossaryMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        if (Measure(menu) is int milli)
        {
            LastRead[menu.Pointer] = milli;
            values.Add(new(Key, NetValue.FromInt(milli)));
        }
    }

    /// <summary>
    /// Host, every frame while <paramref name="menu"/> (a glossary) is its newest menu and a friend is in: a move of its row
    /// since its data was last read reads and sends its data again (<see cref="HostMenus.Changed"/>), at most every
    /// <see cref="SendEverySeconds"/>. Never throws: after three failures it stops watching until the game restarts.
    /// </summary>
    public static void Watch(HostMenus host, Il2CppBishopGames.UI.AbstractMenu menu)
    {
        if (_errors >= 3)
        {
            return;
        }

        try
        {
            WatchCore(host, menu);
        }
        catch (Exception ex)
        {
            if (++_errors <= 3)
            {
                ModLog.Warn($"Menus: the host's glossary scroll couldn't be watched ({ModLog.SafeErrorText(ex)}){(_errors == 3 ? "; not watched again until the game restarts" : string.Empty)}.");
            }
        }
    }

    private static void WatchCore(HostMenus host, Il2CppBishopGames.UI.AbstractMenu menu)
    {
        if (menu.Pointer != _watched || _glossary == null || _glossary.WasCollected)
        {
            _watched = menu.Pointer;
            _glossary = menu.TryCast<GlossaryMenu>();
            _content = null;
            _pending = true;
        }

        if (_glossary == null)
        {
            return;
        }

        if (_content == null)
        {
            _content = _glossary.m_List?.ScrollRect?.content;
            if (_content == null)
            {
                return;
            }
        }

        Vector2 position = _content.anchoredPosition;
        IntPtr entry = ShownEntry(_glossary);
        if (!_pending && position == _lastPosition && entry == _lastEntry)
        {
            return;
        }

        _lastPosition = position;
        _lastEntry = entry;
        _pending = false;
        if (Measure(_glossary) is not int now || !GlossaryScroll.Moved(LastRead.TryGetValue(menu.Pointer, out int read) ? read : null, now))
        {
            return;
        }

        if (FrameClock.Seconds - _sentAt < SendEverySeconds)
        {
            _pending = true; // measured again next frame, and sent once the wait is over
            return;
        }

        _sentAt = FrameClock.Seconds;
        host.Changed(menu, friends: true);
    }

    /// <summary>
    /// Friend: moves this PC's glossary list so the row of <paramref name="entry"/> (highlighted) sits where the host's sits
    /// (<paramref name="data"/>'s <see cref="Key"/>); nothing when the host didn't say or it's within half a percent. Logged
    /// once per glossary object shown here (the game keeps it between opens).
    /// </summary>
    public static void Follow(GlossaryMenu glossary, GlossaryEntrySO entry, MenuData data)
    {
        if (!data.TryGet(Key, out NetValue value) || value.Kind != NetValueKind.Whole || !GlossaryScroll.TryRead(value.WholeValue, out int hostMilli))
        {
            return;
        }

        ScrollRect? scroll = glossary.m_List?.ScrollRect;
        RectTransform? content = scroll?.content;
        RectTransform? area = scroll == null ? null : scroll.viewport != null ? scroll.viewport : scroll.transform.TryCast<RectTransform>();
        if (scroll == null || content == null || area == null || Measure(glossary, entry) is not int here || !GlossaryScroll.Moved(here, hostMilli))
        {
            return;
        }

        area.GetWorldCorners(Corners);
        float shift = GlossaryScroll.Shift(hostMilli, here, Corners[1].y - Corners[0].y);
        if (shift == 0)
        {
            return;
        }

        content.position += new Vector3(0, shift, 0);
        scroll.StopMovement();
        if (LoggedViews.Count < 64 && LoggedViews.Add(glossary.Pointer))
        {
            ModLog.Info($"Menus: the glossary's list sits as on {LogNames.HostTag()}'s screen (row at {GlossaryScroll.Describe(hostMilli)}; it was at {GlossaryScroll.Describe(here)} here).");
        }
    }

    /// <summary>Forgets what was watched and logged (hosting or following stopped).</summary>
    public static void Clear()
    {
        LastRead.Clear();
        LoggedViews.Clear();
        _watched = IntPtr.Zero;
        _glossary = null;
        _content = null;
    }

    /// <summary>
    /// The entry's place in the open tab's entries (the list's data, in its order), or null; the last one found is checked
    /// first (one look while the host scrolls, not one per entry).
    /// </summary>
    private static int? IndexOf(GlossaryMenu menu, GlossaryEntrySO entry)
    {
        Il2CppSystem.Collections.Generic.List<GlossaryEntrySO>? entries = menu.m_Entries;
        if (entries == null)
        {
            return null;
        }

        int count = entries.Count;
        if (_foundIn == entries.Pointer && _foundEntry == entry.Pointer && _foundAt < count && entries[_foundAt]?.Pointer == entry.Pointer)
        {
            return _foundAt;
        }

        for (int i = 0; i < count; i++)
        {
            if (entries[i]?.Pointer == entry.Pointer)
            {
                (_foundIn, _foundEntry, _foundAt) = (entries.Pointer, entry.Pointer, i);
                return i;
            }
        }

        return null;
    }

    /// <summary>The top (world height) of the row at <paramref name="index"/>, from its cell or, when it isn't made, its nearest made neighbour's.</summary>
    private static float? RowTop(GlossaryScrollList list, int index)
    {
        if (CellBounds(list.GetCellByGlobalIndex(index)) is (float top, _))
        {
            return top;
        }

        for (int step = 1; step <= NeighbourCells; step++)
        {
            foreach (int other in new[] { index - step, index + step })
            {
                if (other < 0 || CellBounds(list.GetCellByGlobalIndex(other)) is not (float otherTop, float otherBottom))
                {
                    continue;
                }

                // Rows are alike: a row's pitch is its height and the list's spacing (in the cell's own scale).
                BaseCell cell = list.GetCellByGlobalIndex(other)!;
                float height = otherTop - otherBottom;
                float local = cell.rectTransform.rect.height;
                float pitch = height + (local > 0 ? list.spacing * height / local : 0);
                return otherTop - ((index - other) * pitch);
            }
        }

        return null;
    }

    /// <summary>A made cell's top and bottom (world height), or null.</summary>
    private static (float Top, float Bottom)? CellBounds(BaseCell? cell)
    {
        RectTransform? rect = cell?.rectTransform;
        if (rect == null)
        {
            return null;
        }

        rect.GetWorldCorners(Corners);
        return (Corners[1].y, Corners[0].y);
    }

    /// <summary>The glossary's shown entry's address (read in place: no object made each frame), or 0.</summary>
    private static IntPtr ShownEntry(GlossaryMenu menu)
    {
        if (_shownEntryOffset < 0)
        {
            IntPtr type = Il2CppClassPointerStore<GlossaryMenu>.NativeClassPtr;
            IntPtr field = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(type, "m_ShownEntry");
            int offset = field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);
            _shownEntryOffset = offset is >= 0x10 and <= 0x1000 ? offset : 0;
        }

        return _shownEntryOffset > 0 ? Marshal.ReadIntPtr(menu.Pointer, _shownEntryOffset) : IntPtr.Zero;
    }
}
#endif
