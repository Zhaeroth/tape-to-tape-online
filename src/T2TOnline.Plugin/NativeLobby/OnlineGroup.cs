#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTape2Tape.Hockey.UI;
using Il2CppTMPro;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.UI;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>The Online group's two choices.</summary>
internal enum OnlineChoice
{
    /// <summary>Host Lobby.</summary>
    Host,

    /// <summary>Join Lobby.</summary>
    Join,
}

/// <summary>
/// The Online row on the title and the real title group behind it: the row cloned from Main's Play
/// row, at the sibling index under it; <c>PlayButtons</c> cloned as <c>OnlineButtons</c>; <c>_groupVisuals</c> grown to 7, so the game's own
/// <c>SetActiveGroup(6)</c>, its transition and its Back all work (no code but <c>SelectDefaultButton</c> switches on the group, and that's the one
/// patch). Once per title: each return from a match brings a new title. All or nothing: any step failing takes the clones out, keeps the old array,
/// says which step, and leaves the title as it was. <c>BeginGroupTransition</c> is only reachable once the array has grown (the row's click goes in
/// after it), since with the old array it would leave the title deaf: it clears selection and input before it indexes the array.
/// <para>
/// The cursor after a prompt closes over the group (a problem report's, the report's offer, a notice) is the game's: its navigation, with nothing valid selected,
/// asks the title's navigation source, where the group is a default (<c>Link</c>), and the group, a <c>NavigableParentSelectDefault</c> as the Play group it is
/// cloned from, selects its default row: Host Lobby, or Join Lobby while someone hosts (<see cref="SetDefaultSelection"/>). The game's own groups do the same with
/// their first row (verified in the game's code: the Campaign group's New Run question answered No selects nothing itself; only Main remembers its last row).
/// The Join list gives the cursor back to Join Lobby the same way: the default row is set to it before its screen closes. One prompt hands the cursor back to
/// its own row instead: the problem report's, asked from Report a Problem (<see cref="SelectReportRow"/>; why: <see cref="Core.Diagnostics.ReportReturn"/>).
/// </para>
/// </summary>
internal static class OnlineGroup
{
    /// <summary>The group's value. <c>TitleScreen.EButtonGroup</c> has 0 to 5; to the native code it's an int.</summary>
    public const int GroupValue = 6;

    /// <summary>The prefabs the screens clone from, loaded when the group opens so Host and Join are instant.</summary>
    public static readonly string[] Sources = { "Glossary", "GameSettings", "ControllerAssignment" };

    /// <summary>Prefabs held only for an icon, and whose failure turns nothing off: the Compendium's has the game's lock sprite (a greyed row), the squad screen's its gold crown (the host's mark; it isn't loaded after a match).</summary>
    public static readonly string[] OptionalSources = { "Compendium", "ChooseMetaMenu" };

    private const int OldGroups = 6;
    private const double FindSeconds = 0.5;
    private const float LiveLineHeight = 30f;
    private const float LiveLineWidth = 480f;
    private const float LiveLineIndent = -14f; // under the rows' text
    private const float LiveLineSize = 26f;

    private static readonly Dictionary<IntPtr, (OnlineChoice Choice, string What)> Choices = new();
    private static IntPtr _title;
    private static IntPtr _failedFor;
    private static double _findAt;
    private static bool _settingOn = true;
    private static AnimatedButton? _row;
    private static NavigableParent? _group;
    private static AnimatedButton? _host;
    private static AnimatedButton? _join;
    private static AnimatedButton? _settings;
    private static AnimatedButton? _report;
    private static AnimatedButton? _back;
    private static TextMeshProUGUI? _liveLine;
    private static TitleScreen? _current;

    // Kept alive for the game: the game holds them for each title's buttons.
    private static Il2CppSystem.Action<AbstractNavigable>? _clickRow;
    private static Il2CppSystem.Action<AbstractNavigable>? _clickChoice;
    private static Il2CppSystem.Action<AbstractNavigable>? _clickBack;
    private static Il2CppSystem.Action<AbstractNavigable>? _clickSettings;
    private static Il2CppSystem.Action? _openSettings;
    private static Il2CppSystem.Action<AbstractNavigable>? _clickReport;
    private static Il2CppSystem.Action? _askReport;
    private static Il2CppSystem.Action? _goToGroup;
    private static Il2CppSystem.Action? _chosen;
    private static OnlineChoice _pending;

    /// <summary>Host Lobby or Join Lobby was clicked (the seam the lobby's screens listen on: LobbyScreens). With no listener, the kit opens its shell with a placeholder.</summary>
    public static event Action<OnlineChoice>? Clicked;

    /// <summary>The Online row was pressed: the play test's count starts.</summary>
    public static event Action? RowClicked;

    /// <summary>The group's rows are in on this title.</summary>
    public static bool IsIn => _title != IntPtr.Zero && _group != null && !_group.WasCollected;

    /// <summary>The title it's in on, or null.</summary>
    public static TitleScreen? Title => IsIn ? _current : null;

    /// <summary>The Online row, or null.</summary>
    public static AbstractButton? Row => IsIn ? _row : null;

    /// <summary>The Online group, or null.</summary>
    public static NavigableParent? Group => IsIn ? _group : null;

    /// <summary>The group's rows, top to bottom (empty when it isn't in). Online Settings and Report a Problem are there when their rows went in.</summary>
    public static AbstractButton[] Rows => IsIn && _host != null && _join != null && _back != null
        ? Ordered(_host, _join, _settings, _report, _back)
        : Array.Empty<AbstractButton>();

    /// <summary>The title's button called <paramref name="name"/> by <see cref="Campaign.Scenes.TitleCursor"/>'s table, or null.</summary>
    public static AbstractButton? Button(TitleScreen title, string name)
    {
        if (!IsIn || title.Pointer != _title)
        {
            return null;
        }

        return name switch
        {
            "online" => _row,
            "hostLobby" => _host,
            "joinLobby" => _join,
            "onlineSettings" => _settings,
            "reportProblem" => _report,
            "onlineBack" => _back,
            _ => null,
        };
    }

    /// <summary>True when <paramref name="title"/>'s array holds the group, so <c>SetActiveGroup(6)</c> is safe on it.</summary>
    public static bool IsGrown(TitleScreen title) => title._groupVisuals is { Length: > GroupValue };

    /// <summary>Which row of the group the game selects first (Join when anyone hosts). No effect until it's in.</summary>
    public static void SetDefaultSelection(OnlineChoice choice)
    {
        if (IsIn)
        {
            _group!.m_DefaultNavigable = choice == OnlineChoice.Join ? _join : _host;
        }
    }

    /// <summary>The live line under Join ("Alex is hosting"): the lobby's screens drive it (LobbyScreens). Null or empty hides it.</summary>
    public static void SetLiveLine(string? text)
    {
        if (_liveLine == null || _liveLine.WasCollected)
        {
            return;
        }

        bool show = !string.IsNullOrEmpty(text);
        if (show)
        {
            _liveLine.text = text;
        }

        if (_liveLine.gameObject.activeSelf != show)
        {
            _liveLine.gameObject.SetActive(show);
        }
    }

    /// <summary>The live line's label, or null (its text comes from <see cref="SetLiveLine"/>).</summary>
    public static TMP_Text? LiveLine => _liveLine == null || _liveLine.WasCollected ? null : _liveLine;

    /// <summary>Report a Problem reads "Making..." while a report is made (as Settings > Online's button does), its own words again after. Never throws.</summary>
    public static void ShowReportBusy(bool busy)
    {
        try
        {
            if (_report != null && !_report.WasCollected)
            {
                Label(_report.transform, busy ? Core.Lobby.LobbyWords.Making.Key : Core.Lobby.LobbyWords.ReportAProblem.Key);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: Report a Problem's label couldn't change ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>True while its title shows the Online group, at rest (not between two groups).</summary>
    public static bool IsShown
    {
        get
        {
            TitleScreen? title = _current;
            return IsIn && title != null && !title.WasCollected && (int)title.m_CurrentButtonGroup == GroupValue && !title._isTransitioningGroup;
        }
    }

    /// <summary>True while the Report a Problem row is in and active.</summary>
    public static bool ReportRowThere => IsIn && _report != null && !_report.WasCollected && _report.gameObject.activeInHierarchy;

    /// <summary>
    /// The cursor onto Report a Problem on the next frame, as the game's title selects a row after a prompt of its own (New Run's, confirmed): for the
    /// report's prompt, answered (<see cref="NativeUi.ReportFlow"/>). False when the row isn't there.
    /// </summary>
    public static bool SelectReportRow()
    {
        if (!ReportRowThere)
        {
            return false;
        }

        AbstractNavigableUtils.SelectOnNextFrame(_report!, AbstractNavigable.ESelectFx.Default);
        return true;
    }

    /// <summary>The game's <c>SelectDefaultButton</c> for group 6: what its cases 1-5 do. Not while the shell is over the title: the selection belongs to it.</summary>
    public static void SelectDefault(TitleScreen title)
    {
        if (IsIn && title.Pointer == _title && !LobbyShell.IsOpen)
        {
            AbstractNavigableUtils.SelectOnNextFrame(_group!, AbstractNavigable.ESelectFx.Default);
        }
    }

    /// <summary>The game's own group change to the Online group (the tour's way in; the row's click runs the same).</summary>
    public static bool Enter(TitleScreen title)
    {
        if (!IsIn || title.Pointer != _title || !IsGrown(title))
        {
            return false;
        }

        UiAssets.Request(Sources);
        UiAssets.RequestOptional(OptionalSources);
        title.SetActiveGroup((TitleScreen.EButtonGroup)GroupValue);
        return true;
    }

    /// <summary>Every frame: installs on a new title once it's up; follows the setting. The rest of a frame does nothing.</summary>
    public static void Tick()
    {
        if (!LobbyPatches.Installed)
        {
            return;
        }

        double now = FrameClock.Seconds;
        if (now < _findAt)
        {
            return;
        }

        _findAt = now + FindSeconds;
        if (_title == IntPtr.Zero && !ModSettings.NativeLobby)
        {
            return;
        }

        TitleScreen? title = _current != null && !_current.WasCollected && _current.isActiveAndEnabled ? _current : CampaignGame.Title();
        if (title == null)
        {
            Forget();
            return;
        }

        if (title.Pointer != _title)
        {
            Forget();
            if (title.Pointer != _failedFor && title.m_HasStarted && title.m_NavSource != null && ModSettings.NativeLobby && LobbyShell.Available && T2TOnlinePlugin.OnlineAvailable)
            {
                Install(title);
            }

            return;
        }

        FollowSetting(title);
    }

    /// <summary>The title has gone: its clones went with it.</summary>
    private static void Forget()
    {
        _title = IntPtr.Zero;
        _current = null;
        _row = _host = _join = _settings = _report = _back = null;
        _group = null;
        _liveLine = null;
        Choices.Clear();
    }

    /// <summary>The NativeLobby setting's switch (a developer's, in the F1 panel or the settings file; a player's build always has it on): the row comes and goes on the title that's up (from the main group; a menu or the group itself is left alone until it's closed).</summary>
    private static void FollowSetting(TitleScreen title)
    {
        bool on = ModSettings.NativeLobby;
        if (on == _settingOn || _row == null)
        {
            return;
        }

        var showing = (int)title.m_CurrentButtonGroup;
        if (showing == GroupValue || title._isTransitioningGroup || LobbyShell.IsOpen)
        {
            return;
        }

        _settingOn = on;
        _row.gameObject.SetActive(on);
        title.SetVerticalNavigation(title.m_MainButtonsContainer);
        UiClone.Rebuild(title.m_MainButtonsContainer);
        ModLog.Info($"Lobby: the title's Online row is {(on ? "on" : "off")} (the NativeLobby setting).");
    }

    /// <summary>All or nothing; a failure leaves the title as it was and names the step.</summary>
    private static void Install(TitleScreen title)
    {
        LobbyLocalization.Install();
        string step = "start";
        Attempt attempt = new(title);
        try
        {
            step = "the row's clone";
            MakeRow(attempt);
            step = "the group's clone";
            MakeGroup(attempt);
            step = "the group's array";
            GrowArray(attempt);
            step = "the navigation";
            Link(attempt);
            step = "the clicks";
            WireClicks(attempt);
            step = "the layout";
            UiClone.Rebuild(title.m_MainButtonsContainer);
            Adopt(attempt);
            int rows = title.m_MainButtonsContainer.transform.childCount;
            int place = attempt.Row!.transform.GetSiblingIndex() + 1;
            ModLog.Info($"Lobby: the title's Online group is in (row {place} of {rows}, group {GroupValue}).");
        }
        catch (Exception ex)
        {
            Rollback(attempt);
            _failedFor = title.Pointer;
            ModLog.Warn($"Lobby: the title's Online group failed at {step} ({ModLog.SafeErrorText(ex)}); " +
                $"the clones are out and the title is as it was. {Core.OnlineSettings.PlayersBuild.WithoutLobby(ModSettings.DevTools)}.");
        }
    }

    private static void MakeRow(Attempt a)
    {
        AbstractButton play = a.Title.m_PlayButton ?? throw new InvalidOperationException("the title has no Play row");
        GameObject row = UiClone.Make(play.gameObject, UiClone.Widgets, "the Online row", out string problem) ?? throw new InvalidOperationException(problem);
        a.RowObject = row;
        row.name = "Online";
        a.Row = row.GetComponent<AnimatedButton>() ?? throw new InvalidOperationException("the clone has no AnimatedButton");
        a.Row.ClearNeighbors();
        Label(row.transform, LobbyLocalization.Online);
        NavigableParent main = a.Title.m_MainButtonsContainer ?? throw new InvalidOperationException("the title has no main group");
        a.Main = main;
        UiClone.Place(row, main.transform, play.transform.GetSiblingIndex() + 1);

        // A parent told its children by hand wouldn't find the row (RegisterChildren reads m_ForceChildren first).
        Il2CppReferenceArray<AbstractNavigable>? forced = main.m_ForceChildren;
        if (forced is { Length: > 0 })
        {
            a.OldForced = forced;
            var grown = new Il2CppReferenceArray<AbstractNavigable>(forced.Length + 1);
            int at = 0;
            for (int i = 0; i < forced.Length; i++)
            {
                grown[at++] = forced[i];
                if (forced[i] != null && forced[i].Pointer == play.Pointer)
                {
                    grown[at++] = a.Row;
                }
            }

            if (at != grown.Length)
            {
                throw new InvalidOperationException("the main group lists its rows by hand, without the Play row");
            }

            main.m_ForceChildren = grown;
        }
    }

    private static void MakeGroup(Attempt a)
    {
        NavigableParent source = a.Title.m_PlayButtonsContainer ?? throw new InvalidOperationException("the title has no Play group");
        GameObject group = UiClone.Make(source.gameObject, UiClone.Widgets, "the Online group", out string problem) ?? throw new InvalidOperationException(problem);
        a.GroupObject = group;
        group.name = "OnlineButtons";
        a.Group = group.GetComponent<NavigableParent>() ?? throw new InvalidOperationException("the clone has no NavigableParent");
        a.Host = Child(group.transform, "PlayNow", "HostLobby");
        a.Join = Child(group.transform, "Practice", "JoinLobby");
        a.Back = Child(group.transform, "PlayBack", "OnlineBack");
        a.Report = MakeReportRow(group.transform); // before Online Settings takes the Tournament row: a copy of it, right after it
        a.Settings = MakeSettingsRow(group.transform);
        Label(a.Host.transform, LobbyLocalization.HostLobby);
        Label(a.Join.transform, LobbyLocalization.JoinLobby);
        a.Group.m_DefaultNavigable = a.Host;
        a.Group.m_ForceChildren = new Il2CppReferenceArray<AbstractNavigable>(0);
        a.LiveLine = MakeLiveLine(a);

        // It's off, as the real groups are here, and takes their place beside them.
        group.SetActive(false);
        UiClone.Place(group, source.transform.parent);
        RectTransform? rect = group.GetComponent<RectTransform>();
        Il2CppReferenceArray<TitleScreen.ButtonGroupVisual>? visuals = a.Title._groupVisuals;
        const int play = 3; // EButtonGroup.Play: this is its clone, and rests where it does
        if (rect != null && visuals != null && visuals.Length > play && visuals[play] != null)
        {
            rect.anchoredPosition = visuals[play].RestPosition;
        }
    }

    /// <summary>
    /// Online Settings: the Play group's own Tournament row kept (third, before Back), opening the game's Settings on its Online tab. Its own failure drops only
    /// this row, logged; the group goes in without it.
    /// </summary>
    private static AnimatedButton? MakeSettingsRow(Transform group)
    {
        Transform? tournament = group.Find("Tournament");
        if (tournament == null)
        {
            ModLog.Warn("Lobby: the Online group's Online Settings row isn't in (the Play group has no Tournament row to make it from); Settings still has the Online tab.");
            return null;
        }

        try
        {
            AnimatedButton row = Child(group, "Tournament", "OnlineSettings");
            Label(row.transform, NativeUi.SettingsTabWords.OnlineSettings);
            row.gameObject.SetActive(true);
            return row;
        }
        catch (Exception ex)
        {
            UiClone.Destroy(tournament.gameObject);
            ModLog.Warn($"Lobby: the Online group's Online Settings row isn't in ({ModLog.SafeErrorText(ex)}); Settings still has the Online tab.");
            return null;
        }
    }

    /// <summary>
    /// Report a Problem (D140): a second copy of the Play group's own Tournament row, after it (fourth, before Back), making a problem report. The
    /// game has no smaller row that fits the group: its title rows are one size (480x62), its bottom group holds only the Steam branch row of that
    /// size, and its social links are images that open web pages. Its own failure drops only this row, logged; the group goes in without it.
    /// </summary>
    private static AnimatedButton? MakeReportRow(Transform group)
    {
        Transform? tournament = group.Find("Tournament");
        if (tournament == null)
        {
            ModLog.Warn("Lobby: the Online group's Report a Problem row isn't in (the Play group has no Tournament row to make it from); Settings > Online still has Problem Report.");
            return null;
        }

        GameObject? row = null;
        try
        {
            row = UiClone.Make(tournament.gameObject, UiClone.Widgets, "the Report a Problem row", out string problem) ?? throw new InvalidOperationException(problem);
            row.name = "ReportAProblem";
            AnimatedButton button = row.GetComponent<AnimatedButton>() ?? throw new InvalidOperationException("the clone has no AnimatedButton");
            button.ClearNeighbors();
            Label(row.transform, Core.Lobby.LobbyWords.ReportAProblem.Key);
            row.SetActive(true);
            UiClone.Place(row, group, tournament.GetSiblingIndex() + 1);
            return button;
        }
        catch (Exception ex)
        {
            UiClone.Destroy(row);
            ModLog.Warn($"Lobby: the Online group's Report a Problem row isn't in ({ModLog.SafeErrorText(ex)}); Settings > Online still has Problem Report.");
            return null;
        }
    }

    private static AnimatedButton Child(Transform group, string from, string name)
    {
        Transform child = group.Find(from) ?? throw new InvalidOperationException($"{group.name} has no {from}");
        child.gameObject.name = name;
        AnimatedButton button = child.GetComponent<AnimatedButton>() ?? throw new InvalidOperationException($"{from} has no AnimatedButton");
        button.ClearNeighbors();
        return button;
    }

    /// <summary>A row's label to a term, its text by the term.</summary>
    private static void Label(Transform row, string term)
    {
        Transform text = row.Find("Container/Content/Text") ?? throw new InvalidOperationException($"{row.name} has no Container/Content/Text");
        TextMeshProUGUI label = text.GetComponent<TextMeshProUGUI>() ?? throw new InvalidOperationException($"{row.name}'s text isn't a TextMeshPro");
        LobbyLocalization.Apply(label, term);
    }

    /// <summary>A label under Join, off until the lobby's screens give it words. Its own font and colour, from the row's text.</summary>
    private static TextMeshProUGUI MakeLiveLine(Attempt a)
    {
        Transform text = a.Join!.transform.Find("Container/Content/Text") ?? throw new InvalidOperationException("Join Lobby has no text");
        GameObject line = UiClone.Make(text.gameObject, UiClone.Widgets, "the live line", out string problem) ?? throw new InvalidOperationException(problem);
        line.name = "LiveLine";
        Localize(line);
        TextMeshProUGUI label = line.GetComponent<TextMeshProUGUI>() ?? throw new InvalidOperationException("the live line isn't a TextMeshPro");
        label.text = string.Empty;
        label.richText = false; // "Alex is hosting": a Steam name
        label.fontSize = LiveLineSize;
        label.enableAutoSizing = false;
        label.raycastTarget = false;
        label.margin = new Vector4(LiveLineIndent, 0, 0, 0);
        LayoutElement layout = line.GetComponent<LayoutElement>() ?? line.AddComponent(Il2CppType.Of<LayoutElement>())?.TryCast<LayoutElement>()
            ?? throw new InvalidOperationException("the live line can't lay out");
        layout.preferredHeight = LiveLineHeight;
        layout.preferredWidth = LiveLineWidth;
        layout.minHeight = LiveLineHeight;
        layout.flexibleHeight = 0;
        line.SetActive(false);
        UiClone.Place(line, a.GroupObject!.transform, a.Join.transform.GetSiblingIndex() + 1);
        return label;
    }

    private static void Localize(GameObject line)
    {
        Il2CppI2.Loc.Localize? localize = line.GetComponent<Il2CppI2.Loc.Localize>();
        if (localize != null)
        {
            UiClone.Destroy(localize);
        }
    }

    /// <summary>The array to 7, its first 6 copied as they are (Array.Copy keeps the write barriers), then the game's own caching for the new one.</summary>
    private static void GrowArray(Attempt a)
    {
        Il2CppReferenceArray<TitleScreen.ButtonGroupVisual> old = a.Title._groupVisuals ?? throw new InvalidOperationException("the title has no group array");
        if (old.Length != OldGroups)
        {
            throw new InvalidOperationException($"the title has {old.Length} groups, not {OldGroups}");
        }

        Il2CppSystem.Array grown = Il2CppSystem.Array.CreateInstance(Il2CppType.Of<TitleScreen.ButtonGroupVisual>(), OldGroups + 1);
        Il2CppSystem.Array.Copy(new Il2CppSystem.Array(old.Pointer), grown, OldGroups);
        a.OldVisuals = old;
        a.Title._groupVisuals = new Il2CppReferenceArray<TitleScreen.ButtonGroupVisual>(grown.Pointer);
        a.Title.CacheGroupVisual((TitleScreen.EButtonGroup)GroupValue, a.GroupObject!, null!);
    }

    private static void Link(Attempt a)
    {
        ExternalNavigationSource source = a.Title.m_NavSource ?? throw new InvalidOperationException("the title has no navigation source");
        source.AddNavigable(a.Row!, false);
        source.AddNavigable(a.Group!, true);
        foreach (AbstractButton row in a.Rows())
        {
            source.AddNavigable(row, false);
        }

        a.Linked = true;
        a.Title.SetVerticalNavigation(a.Main!);
        a.Title.SetVerticalNavigation(a.Group!);
        a.Title.m_NavParent?.RegisterChild(a.Group!, false);
    }

    private static void WireClicks(Attempt a)
    {
        _clickRow ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractNavigable>>(new Action<AbstractNavigable>(OnRowClick))
            ?? throw new InvalidOperationException("the row's click delegate couldn't be made");
        _clickChoice ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractNavigable>>(new Action<AbstractNavigable>(OnChoiceClick))
            ?? throw new InvalidOperationException("the choice's click delegate couldn't be made");
        _clickBack ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractNavigable>>(new Action<AbstractNavigable>(OnBackClick))
            ?? throw new InvalidOperationException("the Back click delegate couldn't be made");
        _goToGroup ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(GoToGroup))
            ?? throw new InvalidOperationException("the group change delegate couldn't be made");
        _chosen ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(Chosen))
            ?? throw new InvalidOperationException("the choice delegate couldn't be made");
        _clickSettings ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractNavigable>>(new Action<AbstractNavigable>(OnSettingsClick))
            ?? throw new InvalidOperationException("the Online Settings click delegate couldn't be made");
        _openSettings ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OpenSettings))
            ?? throw new InvalidOperationException("the Online Settings open delegate couldn't be made");
        _clickReport ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractNavigable>>(new Action<AbstractNavigable>(OnReportClick))
            ?? throw new InvalidOperationException("the Report a Problem click delegate couldn't be made");
        _askReport ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(AskReport))
            ?? throw new InvalidOperationException("the Report a Problem delegate couldn't be made");
        a.Row!.OnClick.Register(_clickRow, "T2TOnline");
        a.Host!.OnClick.Register(_clickChoice, "T2TOnline");
        a.Join!.OnClick.Register(_clickChoice, "T2TOnline");
        a.Back!.OnClick.Register(_clickBack, "T2TOnline");
        a.Settings?.OnClick.Register(_clickSettings, "T2TOnline");
        a.Report?.OnClick.Register(_clickReport, "T2TOnline");
        Choices.Clear();
        Choices[a.Host.Pointer] = (OnlineChoice.Host, "Host Lobby");
        Choices[a.Join.Pointer] = (OnlineChoice.Join, "Join Lobby");
    }

    private static void Adopt(Attempt a)
    {
        _title = a.Title.Pointer;
        _current = a.Title;
        _row = a.Row;
        _group = a.Group;
        _host = a.Host;
        _join = a.Join;
        _settings = a.Settings;
        _report = a.Report;
        _back = a.Back;
        _liveLine = a.LiveLine;
        _settingOn = true;
        if (Diagnostics.BugReports.Busy)
        {
            ShowReportBusy(true); // a report from before this title is still being made
        }
    }

    /// <summary>Back to how it was: the old array first (so nothing can index the new one), then the clones, then the links over what's left. Never throws.</summary>
    private static void Rollback(Attempt a)
    {
        Choices.Clear();
        try
        {
            if (a.OldVisuals != null)
            {
                a.Title._groupVisuals = a.OldVisuals;
            }

            if (a.Linked)
            {
                ExternalNavigationSource? source = a.Title.m_NavSource;
                if (a.Row != null)
                {
                    source?.RemoveNavigable(a.Row);
                }

                if (a.Group != null)
                {
                    source?.RemoveNavigable(a.Group);
                }

                foreach (AbstractButton? row in new AbstractButton?[] { a.Host, a.Join, a.Settings, a.Report, a.Back })
                {
                    if (row != null)
                    {
                        source?.RemoveNavigable(row);
                    }
                }
            }

            if (a.Main != null && a.OldForced != null)
            {
                a.Main.m_ForceChildren = a.OldForced;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: undoing the Online group's links failed ({ModLog.SafeErrorText(ex)}).");
        }

        UiClone.Destroy(a.RowObject);
        UiClone.Destroy(a.GroupObject);
        try
        {
            if (a.Main != null)
            {
                a.Title.SetVerticalNavigation(a.Main);
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: relinking the main group failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    // The rows' clicks, as the game's own: through PerformButtonClickCallback, so its refusals (a menu over the title, a transition, a click
    // already running) and its sounds are the game's. Nothing throws into the game's event.
    private static void OnRowClick(AbstractNavigable nav)
    {
        Guard("the Online row's click", () =>
        {
            RowClicked?.Invoke();
            Current()?.PerformButtonClickCallback(nav, _goToGroup!);
        });
    }

    private static void OnChoiceClick(AbstractNavigable nav)
    {
        Guard("the Online group's click", () =>
        {
            if (nav != null && Choices.TryGetValue(nav.Pointer, out (OnlineChoice Choice, string What) choice))
            {
                _pending = choice.Choice;
                Current()?.PerformButtonClickCallback(nav, _chosen!);
            }
        });
    }

    private static void OnBackClick(AbstractNavigable nav)
    {
        Guard("the Online group's Back", () => Current()?.OnBackClicked(nav));
    }

    private static void OnSettingsClick(AbstractNavigable nav)
    {
        Guard("the Online Settings row's click", () => Current()?.PerformButtonClickCallback(nav, _openSettings!));
    }

    /// <summary>Report a Problem: refused while a report is made (as the game's locked rows refuse), else through the title's click as its own rows.</summary>
    private static void OnReportClick(AbstractNavigable nav)
    {
        Guard("the Report a Problem row's click", () =>
        {
            if (Diagnostics.BugReports.Busy)
            {
                nav?.SetClickResult(AbstractNavigable.EClickResult.InvalidNavigation);
                ModLog.Info("Lobby: Report a Problem clicked while a report is being made; it waits for that one.");
                return;
            }

            Current()?.PerformButtonClickCallback(nav, _askReport!);
        });
    }

    /// <summary>The report, as Settings > Online's row makes it; the game's prompt says where it is once it's made (<see cref="NativeUi.ReportFlow"/>).</summary>
    private static void AskReport()
    {
        Guard("Report a Problem", () =>
        {
            ModLog.Info("Lobby: Report a Problem clicked.");
            _ = NativeUi.ReportFlow.Ask(Core.Diagnostics.ReportSources.OnlineMenu);
        });
    }

    /// <summary>The rows there are, in the group's order.</summary>
    private static AbstractButton[] Ordered(params AbstractButton?[] rows)
    {
        int count = 0;
        foreach (AbstractButton? row in rows)
        {
            count += row != null ? 1 : 0;
        }

        var ordered = new AbstractButton[count];
        int at = 0;
        foreach (AbstractButton? row in rows)
        {
            if (row != null)
            {
                ordered[at++] = row;
            }
        }

        return ordered;
    }

    /// <summary>The game's own Settings open (<c>LaunchSettings</c>, as the title's Settings row and the lobby's RT), on its Online tab: the request is taken by the tab's patch within 2 s.</summary>
    private static void OpenSettings()
    {
        Guard("Online Settings", () =>
        {
            TitleScreen? title = Current();
            if (title == null)
            {
                return;
            }

            ModLog.Info("Lobby: Online Settings clicked.");
            NativeUi.SettingsTab.RequestOnlineTab("the Online group's row");
            title.LaunchSettings();
        });
    }

    private static void GoToGroup()
    {
        Guard("the Online group's opening", () =>
        {
            TitleScreen? title = Current();
            if (title != null)
            {
                Enter(title);
            }
        });
    }

    private static void Chosen()
    {
        Guard("the Online group's choice", () =>
        {
            ModLog.Info($"Lobby: {(_pending == OnlineChoice.Host ? "Host Lobby" : "Join Lobby")} clicked.");
            if (Clicked != null)
            {
                Clicked.Invoke(_pending);
            }
            else if (!LobbyShell.RequestOpen(_pending == OnlineChoice.Host ? ShellKind.Lobby : ShellKind.Browser, new ShellOptions(), out string problem))
            {
                ModLog.Warn($"Lobby: {problem}");
            }
        });
    }

    private static TitleScreen? Current() => _current != null && !_current.WasCollected && _current.Pointer == _title ? _current : null;

    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby: {what} failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>What an install has made so far, for its rollback.</summary>
    private sealed class Attempt
    {
        public Attempt(TitleScreen title) => Title = title;

        public TitleScreen Title { get; }

        public GameObject? RowObject { get; set; }

        public GameObject? GroupObject { get; set; }

        public AnimatedButton? Row { get; set; }

        public NavigableParent? Main { get; set; }

        public NavigableParent? Group { get; set; }

        public AnimatedButton? Host { get; set; }

        public AnimatedButton? Join { get; set; }

        public AnimatedButton? Settings { get; set; }

        public AnimatedButton? Report { get; set; }

        public AnimatedButton? Back { get; set; }

        public TextMeshProUGUI? LiveLine { get; set; }

        public Il2CppReferenceArray<TitleScreen.ButtonGroupVisual>? OldVisuals { get; set; }

        public Il2CppReferenceArray<AbstractNavigable>? OldForced { get; set; }

        public bool Linked { get; set; }

        /// <summary>The group's rows, top to bottom.</summary>
        public AbstractButton[] Rows() => Ordered(Host, Join, Settings, Report, Back);
    }
}
#endif
