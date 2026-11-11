#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// The Online tab of the game's own Settings menu (D139): an eighth tab after Stats, grafted into each Settings instance by one postfix on
/// <c>SettingsMenu.InitData</c>, which the UI module's generic <c>LoadMenuPrefab</c> calls on every open: after <c>InitMenu</c> for a fresh instance,
/// through <c>PrepareRetainedShow</c> for the title's retained one (verified in the game's code; <c>T2TMenu.InitMenu</c> never calls it).
/// <para>
/// The graft, all or nothing, the first time an instance is seen: the tab cloned from the menu's own <c>Toggle_System</c> (asleep, named
/// <c>Toggle_Online</c>, after <c>Toggle_Stats</c>, in the bar's toggle group and font matcher); the panel cloned from its own <c>GameplayMenu</c> with the
/// Gameplay script and every row gone before it wakes, inactive among the other panels; its rows cloned from the menu's own rows; then the tab
/// array <c>m_SubMenusInOrder</c> grown to n+1, every reference written through the runtime's write barrier (<see cref="NativeFields"/>), read back,
/// and the four new objects held by strong GC handles for the instance's life; last, the panel's rows added to the menu's default selections
/// (<c>m_DefaultNavigables</c>, where each of the game's panels has its own). Every tab switch, LB and RB, the panel's intro and focus are
/// then the game's own code over the array's length, and so is the cursor's way back to the tab's last row when a prompt over the menu closes,
/// the mouse is left for the pad or a direction is pressed with no cursor (the game's navigation walks the default selections). Any failure
/// puts the old arrays and toggles back, destroys the clones, and turns the tab off for this game: the menu has the game's own seven tabs.
/// </para>
/// <para>
/// Every open: a request for the Online tab (<see cref="RequestOnlineTab"/>, the Online group's row) taken within 2 s sets the opening tab; the game
/// sets it from the open's own data otherwise, so a plain open never lands on Online. "Already grafted" is read from the instance (its
/// <c>Toggle_Online</c>), never from a pointer kept here.
/// </para>
/// </summary>
internal static class SettingsTab
{
    /// <summary>The tab's object name.</summary>
    public const string TabName = "Toggle_Online";

    /// <summary>The panel's object name.</summary>
    public const string PanelName = "OnlineMenu";

    // ui_parts.txt's source for the menu, checked against each live instance before its graft.
    private const string PartsSource = "Settings";
    private const string TabsPath = "ScaledContainer/Canvas/Tabs";
    private const string PanelsPath = "ScaledContainer/Canvas/MainContent/Options/SettingsContainer";
    private const string TabLabelPath = "Container/Background/Text (TMP)";
    private const string RowsPath = "Scroll View/Viewport/Container";
    private const string ScrollPath = "Scroll View";
    private const int MostTabs = 16;
    private const int SubMenuFields = 4;

    // Why a held Insert makes a report once the tab is off: its Problem Report row isn't there.
    private const string HoldWhy = "the Settings menu's Online tab is off, and with it Problem Report";

    // The scripts each clone keeps (UiClone strips the rest): the tab's toggle and its sound; the panel's own select-on-enable and font matchers
    // (their lists are made anew for the Online rows) and, since its rows go whole before it wakes, theirs.
    private static readonly HashSet<string> TabKeep = Keep(UiClone.Widgets, "UnselectableToggle", "UnselectableButtonSFXFMOD");
    private static readonly HashSet<string> PanelKeep = Keep(UiClone.Widgets, "SelectOnEnable", "Toggle", "T2TTooltipDefinition", "CycleList", "CycleListDot", "UnselectableColorButton");

    private static readonly SettingsOpenRequest Request = new();
    private static readonly List<Graft> Grafts = new();
    private static readonly string[] FieldNames = { "Button", "Panel", "CachedAnim", "CachedNav" };

    private static bool _installed;
    private static string? _offReason;
    private static int[] _offsets = Array.Empty<int>();
    private static int _elementSize;
    private static AbstractNavigable? _backupsRow;

    /// <summary>Instances grafted so far this game (the check tour reads it).</summary>
    public static int Grafted { get; private set; }

    /// <summary>Why the tab is off for this game, or null.</summary>
    public static string? OffReason => _offReason;

    /// <summary>The last refusal of Save Backups (the check tour reads it).</summary>
    public static SettingsRowRefusal LastBackupsRefusal => TabRows.LastBackupsRefusal;

    /// <summary>The patch, once a game. A failure is logged; the Settings menu then has the game's own tabs.</summary>
    public static void Install(HarmonyLib.Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        try
        {
            ReadLayout();
        }
        catch (Exception ex)
        {
            _offReason = $"the tab array's layout ({ModLog.SafeErrorText(ex)})";
            ModLog.Warn($"Settings menu: the Online tab isn't installed: {_offReason}; the menu has the game's own tabs.");
            BugReports.HoldMakesToo(HoldWhy);
            return;
        }

        var failed = new List<string>();
        Campaign.Scenes.ScenePatches.Postfix(harmony, failed, typeof(SettingsMenu), nameof(SettingsMenu.InitData), typeof(SettingsTab), nameof(InitDataPostfix));
        if (failed.Count > 0)
        {
            _offReason = $"its patch couldn't go in ({string.Join(", ", failed)})";
            ModLog.Warn($"Settings menu: the Online tab isn't installed: {_offReason}; the menu has the game's own tabs.");
            BugReports.HoldMakesToo(HoldWhy);
            return;
        }

        LobbyShell.Closed += OnShellClosed;
        BackupsScreen.Install(); // the screen Save Backups opens (D141)
        MelonLoader.MelonEvents.OnUpdate.Subscribe(Tick);
        ModLog.Info($"Settings menu: the Online tab is installed (GC incremental: {NativeFields.Mode()}; SubMenu {_elementSize} bytes, fields at {string.Join(", ", Inside())}).");
    }

    /// <summary>The next Settings open, within 2 s, is on the Online tab. <paramref name="from"/> names the asker for the log.</summary>
    public static void RequestOnlineTab(string from) => Request.Ask(Now(), from);

    /// <summary>A request whose open didn't come (the game's open refused): forgotten, so a later plain open is the game's own.</summary>
    public static void ForgetOnlineTab() => Request.Clear();

    /// <summary>The Online tab's place in <paramref name="menu"/>'s tabs, or -1 when it isn't in.</summary>
    public static int OnlineIndex(SettingsMenu? menu)
    {
        if (menu == null || menu.WasCollected)
        {
            return -1;
        }

        UnselectableToggle? tab = menu.m_TabTogglesParent?.transform.Find(TabName)?.GetComponent<UnselectableToggle>();
        return tab == null ? -1 : IndexOf(menu, tab.Pointer);
    }

    /// <summary>How many <c>Toggle_Online</c> tabs <paramref name="menu"/>'s bar has (one when grafted, never two).</summary>
    public static int OnlineTabs(SettingsMenu menu)
    {
        Transform? bar = menu.m_TabTogglesParent?.transform;
        int count = 0;
        for (int i = 0; bar != null && i < bar.childCount; i++)
        {
            count += bar.GetChild(i).name == TabName ? 1 : 0;
        }

        return count;
    }

    /// <summary>The navigable of one of the tab's rows in <paramref name="menu"/> (what the cursor lands on), or null.</summary>
    public static AbstractNavigable? Row(SettingsMenu menu, SettingsRowId row) => Find(menu)?.Rows.Navigable(row);

    /// <summary>True when the row shows in <paramref name="menu"/> now.</summary>
    public static bool Shows(SettingsMenu menu, SettingsRowId row) => Find(menu)?.Rows.Shows(row) == true;

    /// <summary>Save Backups is locked in <paramref name="menu"/> now (out of the title), or null with no graft.</summary>
    public static bool? BackupsLocked(SettingsMenu menu) => Find(menu)?.Rows.BackupsLocked;

    /// <summary>The Play Online With list's index in <paramref name="menu"/> (-1 before it woke).</summary>
    public static int ChoiceIndex(SettingsMenu menu) => Find(menu)?.Rows.ChoiceIndex ?? -1;

    /// <summary>Save Backups opened the Backups screen from <paramref name="row"/>: the cursor goes back there when it closes.</summary>
    public static void BackupsOpenedFrom(AbstractNavigable row) => _backupsRow = row;

    /// <summary>The Online tab's panel in <paramref name="menu"/>, or null.</summary>
    public static GameObject? Panel(SettingsMenu menu) => Find(menu)?.Panel;

    // ---- The patch ----

    private static void InitDataPostfix(SettingsMenu __instance)
    {
        try
        {
            Purge();
            if (__instance == null)
            {
                return;
            }

            if (_offReason != null)
            {
                if (Request.Take(Now()).Taken)
                {
                    ModLog.Warn($"Settings menu: asked to open on the Online tab, but the tab is off for this game ({_offReason}); the menu opens on its own first tab.");
                }

                return;
            }

            Graft? graft = Find(__instance);
            if (graft == null && !IsGrafted(__instance))
            {
                graft = Make(__instance);
            }

            Open(__instance, graft);
            graft?.Rows.Open();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Settings menu: the Online tab's step on an open failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>The request, taken or lapsed: the opening tab set to Online when one was taken and the tab is in.</summary>
    private static void Open(SettingsMenu menu, Graft? graft)
    {
        SettingsOpenTake take = Request.Take(Now());
        if (take.Lapsed)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Settings menu: an open on the Online tab was asked for {take.Age.TotalSeconds:0.0} s ago ({take.From}) and lapsed (Settings didn't open in time)."));
            return;
        }

        if (!take.Taken)
        {
            return;
        }

        int online = graft == null ? -1 : IndexOf(menu, graft.Tab.Pointer);
        int index = SettingsOpenRequest.OpeningIndex(menu.m_OpeningIndex, online, taken: true);
        if (online < 0)
        {
            ModLog.Warn($"Settings menu: asked to open on the Online tab ({take.From}), but it isn't in; the menu opens on its own first tab.");
            return;
        }

        menu.m_OpeningIndex = index;
        ModLog.Info($"Settings menu: opened on the Online tab ({take.From}).");
    }

    // ---- The graft ----

    /// <summary>All or nothing: the graft, or the instance as it was with the tab off for this game.</summary>
    private static Graft? Make(SettingsMenu menu)
    {
        LobbyLocalization.Install();
        var a = new Attempt(menu);
        string step = "the parts";
        try
        {
            string? missing = UiAssets.CheckParts(PartsSource, menu.gameObject);
            if (missing != null)
            {
                throw new InvalidOperationException(missing);
            }

            step = "the game's tabs";
            Transform root = menu.transform;
            a.Group = menu.m_TabTogglesParent ?? throw new InvalidOperationException("the menu has no tab bar");
            a.OldArray = menu.m_SubMenusInOrder ?? throw new InvalidOperationException("the menu has no tab array");
            int count = a.OldArray.Length;
            if (count is < 1 or > MostTabs)
            {
                throw new InvalidOperationException($"the menu has {count} tabs");
            }

            for (int i = 0; i < count; i++)
            {
                if (NativeFields.Read(FieldAddress(a.OldArray.Pointer, i, 0)) == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"tab {i + 1} has no button");
                }
            }

            Transform system = Part(root, $"{TabsPath}/Toggle_System");
            Transform stats = Part(root, $"{TabsPath}/Toggle_Stats");
            Transform gameplay = Part(root, $"{PanelsPath}/GameplayMenu");
            if (system.parent.Pointer != a.Group.transform.Pointer)
            {
                throw new InvalidOperationException("the tabs aren't the toggle group's");
            }

            step = "the tab";
            MakeTab(a, system, stats);
            step = "the panel";
            Transform rows = MakePanel(a, gameplay);
            step = "the rows";
            a.Rows = TabRows.Make(gameplay.parent, rows, a.PanelObject!);
            UiClone.Place(a.PanelObject!, gameplay.parent);
            step = "the tab array";
            GrowArray(a, count);
            step = "the default selections";
            GrowDefaults(a, gameplay.parent);
            var graft = new Graft(menu, a.Tab!, a.PanelObject!, a.Rows, a.Handles);
            Grafts.Add(graft);
            Grafted++;
            ModLog.Info($"Settings menu: the Online tab is in (tab {count + 1} of {count + 1}).");
            return graft;
        }
        catch (Exception ex)
        {
            Rollback(a);
            _offReason = $"it failed at {step}";
            ModLog.Warn($"Settings menu: the Online tab failed at {step} ({ModLog.SafeErrorText(ex)}); the menu has the game's own tabs.");
            ModLog.Warn("Settings menu: the Online tab stays off for this game.");
            BugReports.HoldMakesToo(HoldWhy);
            return null;
        }
    }

    private static void MakeTab(Attempt a, Transform system, Transform stats)
    {
        a.TabObject = UiClone.Make(system.gameObject, TabKeep, "the Online tab", out string problem) ?? throw new InvalidOperationException(problem);
        a.TabObject.name = TabName;
        a.Tab = a.TabObject.GetComponent<UnselectableToggle>() ?? throw new InvalidOperationException("the tab's clone has no toggle");
        a.Label = a.TabObject.transform.Find(TabLabelPath)?.GetComponent<TMP_Text>() ?? throw new InvalidOperationException($"the tab has no {TabLabelPath}");
        LobbyLocalization.Apply(a.Label, SettingsTabWords.OnlineTab);
        UnselectableToggleGroup group = a.Group!;
        UiClone.Place(a.TabObject, group.transform, stats.GetSiblingIndex() + 1);

        // A bar told its toggles by hand wouldn't find the new one (RefreshChildren reads m_ForceToggles first; verified in the game's code).
        Il2CppReferenceArray<UnselectableToggle>? forced = group.m_ForceToggles;
        if (forced is { Length: > 0 })
        {
            UnselectableToggle? after = stats.GetComponent<UnselectableToggle>();
            var grown = new Il2CppReferenceArray<UnselectableToggle>(forced.Length + 1);
            int at = 0;
            bool placed = false;
            for (int i = 0; i < forced.Length; i++)
            {
                grown[at++] = forced[i];
                if (!placed && after != null && forced[i] != null && forced[i].Pointer == after.Pointer)
                {
                    grown[at++] = a.Tab;
                    placed = true;
                }
            }

            if (!placed)
            {
                grown[at] = a.Tab;
            }

            a.OldForced = forced;
            group.m_ForceToggles = grown;
        }

        a.Refreshed = true;
        group.RefreshChildren();

        // The bar's labels share one size (its FontSizeMatcher), the new one too.
        a.Matcher = group.GetComponent<FontSizeMatcher>();
        a.Matcher?.AddTextToMatch(a.Label);
    }

    /// <summary>The panel, asleep and inactive, with no rows; its rows' parent.</summary>
    private static Transform MakePanel(Attempt a, Transform gameplay)
    {
        a.PanelObject = UiClone.Make(gameplay.gameObject, PanelKeep, "the Online panel", out string problem) ?? throw new InvalidOperationException(problem);
        a.PanelObject.name = PanelName;
        a.PanelObject.SetActive(false);
        Transform rows = a.PanelObject.transform.Find(RowsPath) ?? throw new InvalidOperationException($"the panel has no {RowsPath}");
        for (int i = rows.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.DestroyImmediate(rows.GetChild(i).gameObject);
        }

        if (rows.childCount != 0)
        {
            throw new InvalidOperationException("the Gameplay rows couldn't be taken out");
        }

        a.Nav = rows.GetComponent<NavigableParent>() ?? throw new InvalidOperationException("the panel's rows have no navigable parent");
        a.Anim = a.PanelObject.GetComponent<Animator>() ?? throw new InvalidOperationException("the panel has no animator");
        FromTheTop(a.PanelObject, rows);
        return rows;
    }

    /// <summary>
    /// The rows from the panel's top, at the Gameplay tab's own width. The Gameplay panel's rows overfill its scroll view, so its content's centre pivot never shows; these
    /// are few, and a scroll view lays content shorter than itself by that pivot (verified in the game's code: <c>ScrollRect.AdjustBounds</c>), which centred them a third of the
    /// way down. Pivoted at its top, as the game's Play Now panel's content is, the content starts under the tab bar. Its bar, never needed here, hides without widening the
    /// view (the prefab's mode widens it): the rows keep the width the Gameplay tab's have beside its bar. Before the panel wakes, like the rest of the clone.
    /// </summary>
    private static void FromTheTop(GameObject panel, Transform rows)
    {
        RectTransform content = rows.GetComponent<RectTransform>() ?? throw new InvalidOperationException("the panel's rows aren't laid out");
        content.pivot = new Vector2(content.pivot.x, 1);
        content.anchoredPosition = new Vector2(content.anchoredPosition.x, 0);
        UnityEngine.UI.ScrollRect scroll = panel.transform.Find(ScrollPath)?.GetComponent<UnityEngine.UI.ScrollRect>() ?? throw new InvalidOperationException($"the panel has no {ScrollPath}");
        scroll.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.AutoHide;
    }

    /// <summary>
    /// The tab array to n+1: the old elements' references and the new element's four, each written through the barrier, read back, the four new
    /// objects held, then the array set on the menu (the interop's setter has its own barrier).
    /// </summary>
    private static void GrowArray(Attempt a, int count)
    {
        Il2CppSystem.Array grown = Il2CppSystem.Array.CreateInstance(Il2CppType.Of<SettingsMenu.SubMenu>(), count + 1);
        IntPtr to = grown.Pointer;
        int size = IL2CPP.il2cpp_array_element_size(IL2CPP.il2cpp_object_get_class(to));
        if (size != _elementSize)
        {
            throw new InvalidOperationException($"the new array's elements are {size} bytes, not {_elementSize}");
        }

        IntPtr from = a.OldArray!.Pointer;
        IntPtr[] mine = { a.Tab!.Pointer, a.PanelObject!.Pointer, a.Anim!.Pointer, a.Nav!.Pointer };
        for (int i = 0; i <= count; i++)
        {
            for (int f = 0; f < SubMenuFields; f++)
            {
                IntPtr value = i < count ? NativeFields.Read(FieldAddress(from, i, f)) : mine[f];
                NativeFields.Write(to, FieldAddress(to, i, f), value);
            }
        }

        for (int i = 0; i <= count; i++)
        {
            for (int f = 0; f < SubMenuFields; f++)
            {
                IntPtr wanted = i < count ? NativeFields.Read(FieldAddress(from, i, f)) : mine[f];
                if (NativeFields.Read(FieldAddress(to, i, f)) != wanted)
                {
                    throw new InvalidOperationException($"tab {i + 1}'s {FieldNames[f]} didn't hold");
                }
            }
        }

        a.Handles.Add(NativeFields.Hold(a.Tab));
        a.Handles.Add(NativeFields.Hold(a.PanelObject));
        a.Handles.Add(NativeFields.Hold(a.Anim));
        a.Handles.Add(NativeFields.Hold(a.Nav));
        a.Menu.m_SubMenusInOrder = new Il2CppReferenceArray<SettingsMenu.SubMenu>(to);
        a.ArraySet = true;
        if (a.Menu.m_SubMenusInOrder?.Pointer != to)
        {
            throw new InvalidOperationException("the menu didn't take the grown array");
        }
    }

    /// <summary>
    /// The panel's rows among the menu's default selections (<see cref="SettingsDefaults"/>), after its own panels': the list the game's navigation walks when nothing
    /// valid is selected, taking the first that is alive, active and navigable (verified in the game's code: <c>NavigationController.FindFallbackNavigable</c>, from
    /// <c>EnsureValidSelection</c> a frame after the selected control is disabled, from a direction pressed with no cursor, and from the mouse left for the pad). The
    /// game's list names each panel's rows (verified in its Settings prefab), so a prompt closing over one of its tabs puts the cursor back on that tab's last row;
    /// without this entry one closing over the Online tab leaves no cursor. The game makes the shown tab's rows navigable and the others' not
    /// (<c>SelectSubMenu_Internal</c>, over the tab array), these too. A plain reference array: the interop stores its elements in raw (no barrier), which is safe
    /// here because each is a live component the scene already holds (the menu's own navigables, the panel's rows); the menu's field is set through the interop's
    /// setter, which has the barrier.
    /// </summary>
    private static void GrowDefaults(Attempt a, Transform panels)
    {
        Il2CppReferenceArray<AbstractNavigable>? old = a.Menu.m_DefaultNavigables;
        int count = old?.Length ?? 0;
        if (count > SettingsDefaults.MostEntries)
        {
            throw new InvalidOperationException($"the menu has {count} default selections");
        }

        var isPanel = new bool[count];
        for (int i = 0; i < count; i++)
        {
            AbstractNavigable? entry = old![i];
            isPanel[i] = entry != null && !entry.WasCollected && entry.transform.IsChildOf(panels);
        }

        int at = SettingsDefaults.InsertIndex(isPanel);
        var grown = new Il2CppReferenceArray<AbstractNavigable>(count + 1);
        for (int i = 0, from = 0; i <= count; i++)
        {
            grown[i] = i == at ? a.Nav! : old![from++];
        }

        a.OldDefaults = old;
        a.DefaultsSet = true;
        a.Menu.m_DefaultNavigables = grown;
        Il2CppReferenceArray<AbstractNavigable>? now = a.Menu.m_DefaultNavigables;
        if (now == null || now.Length != count + 1 || now[at]?.Pointer != a.Nav!.Pointer)
        {
            throw new InvalidOperationException("the menu didn't take the grown default selections");
        }
    }

    /// <summary>Back to how it was: the old arrays first (so nothing indexes the new ones), the bar's list, the clones, then the bar's toggles again. Never throws.</summary>
    private static void Rollback(Attempt a)
    {
        Step("the default selections", () =>
        {
            if (a.DefaultsSet)
            {
                a.Menu.m_DefaultNavigables = a.OldDefaults!; // what it was: the game's list, or none
            }
        });
        Step("the tab array", () =>
        {
            if (a.ArraySet && a.OldArray != null)
            {
                a.Menu.m_SubMenusInOrder = a.OldArray;
            }
        });
        Step("the bar's toggles", () =>
        {
            if (a.OldForced != null && a.Group != null)
            {
                a.Group.m_ForceToggles = a.OldForced;
            }
        });
        Step("the bar's font match", () =>
        {
            if (a.Matcher != null && a.Label != null && !a.Matcher.WasCollected)
            {
                a.Matcher.RemoveTextToMatch(a.Label);
            }
        });
        UiClone.Destroy(a.PanelObject);
        UiClone.Destroy(a.TabObject);
        Step("the bar", () =>
        {
            if (a.Refreshed && a.Group != null && !a.Group.WasCollected)
            {
                a.Group.RefreshChildren();
            }
        });
        NativeFields.Free(a.Handles);
    }

    private static void Step(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Settings menu: putting {what} back failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    // ---- The Backups screen ----

    /// <summary>The Backups screen has gone: the cursor back on Save Backups while Settings is on top (the module's open gives no focus back by itself).</summary>
    private static void OnShellClosed(ShellKind kind, ShellClosed how)
    {
        if (kind != ShellKind.Backups)
        {
            return;
        }

        AbstractNavigable? row = _backupsRow;
        _backupsRow = null;
        AbstractNavigable? current = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        string was = current == null || current.WasCollected ? "nothing" : (current.Pointer == row?.Pointer ? "Save Backups" : UiObjectName.ForLog(current.gameObject.name));
        bool back = row != null && !row.WasCollected && row.gameObject.activeInHierarchy && LobbyShell.TopShown()?.TryCast<SettingsMenu>() != null;
        if (back)
        {
            AbstractNavigableUtils.SelectOnNextFrame(row!, AbstractNavigable.ESelectFx.Default);
        }

        ModLog.Info($"Settings menu: the Backups screen closed ({how}); {(back ? "the cursor goes back to Save Backups" : "Settings isn't on top: the cursor is left")} (it was on {was}).");
    }

    // ---- Reading ----

    /// <summary>The graft whose tab is <paramref name="menu"/>'s own <c>Toggle_Online</c>, or null.</summary>
    private static Graft? Find(SettingsMenu menu)
    {
        Transform? tab = menu.m_TabTogglesParent?.transform.Find(TabName);
        if (tab == null)
        {
            return null;
        }

        IntPtr tabObject = tab.gameObject.Pointer;
        foreach (Graft graft in Grafts)
        {
            if (!graft.TabObject.WasCollected && graft.TabObject.Pointer == tabObject)
            {
                return graft;
            }
        }

        return null;
    }

    private static bool IsGrafted(SettingsMenu menu) => menu.m_TabTogglesParent?.transform.Find(TabName) != null;

    /// <summary>The element of <paramref name="menu"/>'s tab array whose button is <paramref name="button"/>, or -1. Read in place: no copies.</summary>
    private static int IndexOf(SettingsMenu menu, IntPtr button)
    {
        Il2CppReferenceArray<SettingsMenu.SubMenu>? tabs = menu.m_SubMenusInOrder;
        for (int i = 0; tabs != null && i < tabs.Length; i++)
        {
            if (NativeFields.Read(FieldAddress(tabs.Pointer, i, 0)) == button)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Every frame (the game's update): the rows of each instance that is up (the choice list as its tab comes up, the report button). While none is
    /// up (the title's retained Settings closed, a pause menu's gone) it reads one field per instance and calls nothing in the game: the menu's own
    /// <c>m_IsShown</c>, the field its <c>IsHidden</c> is (verified in the game's code), read in place. Never throws.
    /// </summary>
    private static void Tick()
    {
        for (int i = 0; i < Grafts.Count; i++)
        {
            Graft graft = Grafts[i];
            try
            {
                SettingsMenu menu = graft.Menu;
                if (!menu.WasCollected && menu.m_IsShown && menu != null)
                {
                    graft.Rows.Tick();
                }
            }
            catch (Exception ex)
            {
                if (!graft.TickFailed)
                {
                    graft.TickFailed = true;
                    ModLog.Warn($"Settings menu: the Online tab's rows' step failed ({ModLog.SafeErrorText(ex)}); said once.");
                }
            }
        }
    }

    /// <summary>Instances gone: their handles freed.</summary>
    private static void Purge()
    {
        for (int i = Grafts.Count - 1; i >= 0; i--)
        {
            Graft graft = Grafts[i];
            if (graft.Menu == null || graft.Menu.WasCollected)
            {
                int handles = graft.Handles.Count;
                NativeFields.Free(graft.Handles);
                Grafts.RemoveAt(i);
                ModLog.Info($"Settings menu: a grafted instance has gone; its {handles} GC handles are freed.");
            }
        }
    }

    /// <summary>The struct's size and its four fields' places, from the runtime, checked to be four references and nothing else.</summary>
    private static void ReadLayout()
    {
        IntPtr klass = Il2CppClassPointerStore<SettingsMenu.SubMenu>.NativeClassPtr;
        if (klass == IntPtr.Zero)
        {
            throw new InvalidOperationException("the game has no SettingsMenu.SubMenu");
        }

        uint align = 0;
        int size = IL2CPP.il2cpp_class_value_size(klass, ref align);
        int[] offsets = new int[FieldNames.Length];
        for (int f = 0; f < FieldNames.Length; f++)
        {
            offsets[f] = NativeFields.FieldOffset(klass, FieldNames[f]);
        }

        string? problem = ValueArrayLayout.Problem(size, offsets, IntPtr.Size);
        if (problem != null)
        {
            throw new InvalidOperationException(problem);
        }

        _elementSize = size;
        _offsets = offsets;
    }

    private static IEnumerable<int> Inside()
    {
        foreach (int boxed in _offsets)
        {
            yield return boxed - ValueArrayLayout.ObjectHeaderSize(IntPtr.Size);
        }
    }

    private static IntPtr FieldAddress(IntPtr array, int index, int field) =>
        new(ValueArrayLayout.ElementFieldAddress(array.ToInt64(), index, _elementSize, _offsets[field], IntPtr.Size));

    private static Transform Part(Transform root, string path) => root.Find(path) ?? throw new InvalidOperationException($"the menu has no {path}");

    private static TimeSpan Now() => TimeSpan.FromSeconds(FrameClock.Seconds);

    private static HashSet<string> Keep(IEnumerable<string> start, params string[] more)
    {
        var keep = new HashSet<string>(start, StringComparer.Ordinal);
        keep.UnionWith(more);
        return keep;
    }

    /// <summary>One instance's graft, for its life: its parts and the handles that hold the four new objects.</summary>
    private sealed class Graft
    {
        public Graft(SettingsMenu menu, UnselectableToggle tab, GameObject panel, TabRows? rows, List<nint> handles)
        {
            Menu = menu;
            Tab = tab;
            TabObject = tab.gameObject;
            Panel = panel;
            Rows = rows ?? throw new ArgumentNullException(nameof(rows));
            Handles = new List<nint>(handles);
        }

        public SettingsMenu Menu { get; }

        public UnselectableToggle Tab { get; }

        public GameObject TabObject { get; }

        public GameObject Panel { get; }

        public TabRows Rows { get; }

        public List<nint> Handles { get; }

        public bool TickFailed { get; set; }
    }

    /// <summary>What a graft has made so far, for its rollback.</summary>
    private sealed class Attempt
    {
        public Attempt(SettingsMenu menu) => Menu = menu;

        public SettingsMenu Menu { get; }

        public UnselectableToggleGroup? Group { get; set; }

        public Il2CppReferenceArray<SettingsMenu.SubMenu>? OldArray { get; set; }

        public Il2CppReferenceArray<UnselectableToggle>? OldForced { get; set; }

        public Il2CppReferenceArray<AbstractNavigable>? OldDefaults { get; set; }

        public bool DefaultsSet { get; set; }

        public bool ArraySet { get; set; }

        public bool Refreshed { get; set; }

        public GameObject? TabObject { get; set; }

        public UnselectableToggle? Tab { get; set; }

        public TMP_Text? Label { get; set; }

        public FontSizeMatcher? Matcher { get; set; }

        public GameObject? PanelObject { get; set; }

        public Animator? Anim { get; set; }

        public NavigableParent? Nav { get; set; }

        public TabRows? Rows { get; set; }

        public List<nint> Handles { get; } = new();
    }
}
#endif
