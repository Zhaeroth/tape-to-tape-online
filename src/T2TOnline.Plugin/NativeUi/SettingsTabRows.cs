#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTMPro;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Startup;
using UnityEngine;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// The Online tab's rows (D139), one set per Settings instance: each the game's own row cloned from the instance's Gameplay or System panel, with
/// the game's tooltip, in <see cref="SettingsRows.Order"/>. Every open shows the rows the state allows (<see cref="SettingsRows"/>), links their up
/// and down as the game's panels do, and reads every setting into them again. Presses go through <c>ModSettings</c> (a check tour holds them in
/// memory); a refusal puts the row back and says why on the game's prompt.
/// </summary>
internal sealed class TabRows
{
    /// <summary>The scene the game's main-menu-only lock opens in (<c>MainMenuOnlyButton.OnEnable</c>; verified in the game's code).</summary>
    public const string TitleScene = "start_screen";

    private const string RowsPath = "Scroll View/Viewport/Container";
    private const string SystemRows = "SystemMenu/Container";

    // The scripts a row's clone keeps: the toggle, the choice list, the tooltip. The game's main-menu lock isn't kept: Save Backups locks as it does
    // (the same scene, the same dimming) with a tooltip in both states, which the lock alone can't give (it has none while open).
    private static readonly HashSet<string> RowKeep = Keep(UiClone.Widgets, "Toggle", "T2TTooltipDefinition", "CycleList", "CycleListDot", "UnselectableColorButton");

    private static bool _landingSaid;

    private readonly Dictionary<SettingsRowId, Row> _rows = new();
    private readonly NavigableParentSelectPrevious? _parent;
    private readonly float _lockedAlpha;
    private Il2CppSystem.Action<Il2CppBishopGames.UI.Toggle, bool>? _toggled;
    private Il2CppSystem.Action<AbstractNavigable>? _clicked;
    private Il2CppSystem.Action<Il2CppSystem.Object>? _chose;
    private bool _choiceAwake;
    private bool _choiceShown;
    private bool _setting;
    private bool _making;
    private SettingsRole _openRole;
    private string _itemsLanguage = string.Empty;

    private TabRows(NavigableParentSelectPrevious? parent, float lockedAlpha)
    {
        _parent = parent;
        _lockedAlpha = lockedAlpha;
    }

    /// <summary>The last Save Backups refusal (the check tour reads it).</summary>
    public static SettingsRowRefusal LastBackupsRefusal { get; private set; }

    /// <summary>
    /// The check tour's own context for the rows, or null: the screens tour pictures the states its fake session can't make (a friend's own menu, a host in a
    /// match, Steam refusing relay only, online off, after an error). Read only while a tour holds the player's data (<see cref="TourHolds.On"/>); the tour
    /// clears it after each picture and at every restore.
    /// </summary>
    public static SettingsContext? ForCheck { get; set; }

    /// <summary>
    /// Every row, asleep, under <paramref name="rows"/> (the Online panel's container, its own rows gone), cloned from <paramref name="panels"/>
    /// (the instance's <c>SettingsContainer</c>). Throws with what's wrong; the graft then undoes everything.
    /// </summary>
    public static TabRows Make(Transform panels, Transform rows, GameObject panel)
    {
        Transform gameplay = Part(panels, $"GameplayMenu/{RowsPath}");
        Transform system = Part(panels, SystemRows);
        MainMenuOnlyButton? gameLock = Part(system, "ButtonWithTitle_DeleteEverything/Button").GetComponent<MainMenuOnlyButton>();
        var made = new TabRows(rows.GetComponent<NavigableParentSelectPrevious>(), gameLock?.m_LockedAlpha ?? 0.5f);
        made.MakeToggle(SettingsRowId.ShareAchievements, Part(gameplay, "Toggle_SkipReplay"), rows, LobbyWords.ShareAchievements.Key, null, LobbyWords.ShareAchievementsTip.Key);
        made.MakeToggle(SettingsRowId.ShareProgress, Part(gameplay, "Toggle_SkipReplay"), rows, LobbyWords.ShareProgress.Key, null, LobbyWords.ShareProgressTip.Key);
        made.MakeChoice(Part(gameplay, "PlayerHUD_CycleList"), rows);
        made.MakeToggle(SettingsRowId.DirectConnections, Part(gameplay, "Toggle_StickShooting"), rows, LobbyWords.DirectConnections.Key, LobbyWords.FromNextStart.Key, LobbyWords.DirectConnectionsTip.Key);
        made.MakeButton(SettingsRowId.SaveBackups, Part(system, "ButtonWithTitle_DeleteEverything"), rows, LobbyWords.SaveBackups.Key, LobbyWords.View.Key, LobbyWords.SaveBackupsTip.Key);
        made.MakeButton(SettingsRowId.ProblemReport, Part(system, "ButtonWithTitle_Credits"), rows, LobbyWords.ProblemReport.Key, LobbyWords.MakeReport.Key, LobbyWords.ProblemReportTip.Key);
        made.MakeButton(SettingsRowId.LeaveOnlineGame, Part(system, "ButtonWithTitle_Credits"), rows, LobbyWords.LeaveOnlineGame.Key, LobbyWords.LeaveButton.Key, LobbyWords.LeaveOnlineGameTip.Key);
        made.MakeButton(SettingsRowId.EndOnlineGame, Part(system, "ButtonWithTitle_Credits"), rows, LobbyWords.EndOnlineGame.Key, LobbyWords.End.Key, LobbyWords.EndOnlineGameTip.Key);
        made.MatchSizes(Part(panels, "GameplayMenu"), panel);
        made.Open(); // the first state, before anything wakes
        return made;
    }

    /// <summary>The navigable of <paramref name="id"/> (the cursor lands on it), or null.</summary>
    public AbstractNavigable? Navigable(SettingsRowId id) => _rows.TryGetValue(id, out Row? row) ? row.Navigable : null;

    /// <summary>True when <paramref name="id"/> shows now.</summary>
    public bool Shows(SettingsRowId id) => _rows.TryGetValue(id, out Row? row) && row.Root.activeSelf;

    /// <summary>True when Save Backups is locked now (out of the title, as the game's own lock).</summary>
    public bool BackupsLocked { get; private set; }

    /// <summary>The choice list's index (-1 before it woke).</summary>
    public int ChoiceIndex => _choiceAwake && _rows.TryGetValue(SettingsRowId.PlayOnlineWith, out Row? row) && row.List != null ? row.List.CurrentIndex : -1;

    /// <summary>
    /// Each open: which rows show (the session, the Direct Connections rule), their up and down, every setting read in again, Save Backups' lock and
    /// tooltip for where the menu is, Problem Report's button text.
    /// </summary>
    public void Open()
    {
        SettingsContext context = Context();
        _openRole = context.SessionRole; // first: a step below that fails isn't tried again every frame (Tick)
        var shown = new List<AbstractNavigable>();
        foreach (SettingsRowId id in SettingsRows.Order)
        {
            if (!_rows.TryGetValue(id, out Row? row))
            {
                continue;
            }

            bool show = SettingsRows.Shown(id, context);
            if (row.Root.activeSelf != show)
            {
                row.Root.SetActive(show);
            }

            if (show)
            {
                shown.Add(row.Navigable);
            }
            else if (_parent != null && !_parent.WasCollected && _parent.m_LastHighlighted?.Pointer == row.Navigable.Pointer)
            {
                _parent.m_LastHighlighted = null; // the panel's remembered row has gone: its first row then
            }
        }

        LinkRows(shown);
        Land(context);
        _setting = true;
        try
        {
            ShowToggle(SettingsRowId.ShareAchievements, ModSettings.ShareAchievements);
            ShowToggle(SettingsRowId.ShareProgress, ModSettings.ShareProgress);
            ShowToggle(SettingsRowId.DirectConnections, ModSettings.AllowDirectConnections);
            ShowChoice();
        }
        finally
        {
            _setting = false;
        }

        BackupsLocked = !context.AtTitle;
        if (_rows.TryGetValue(SettingsRowId.SaveBackups, out Row? backups))
        {
            if (backups.Dim != null)
            {
                backups.Dim.alpha = BackupsLocked ? _lockedAlpha : 1f;
            }

            if (backups.Tip != null)
            {
                NativeFields.WriteTerm(backups.Tip, TipClass, "m_Description", BackupsLocked ? LobbyWords.SaveBackupsLocked.Key : LobbyWords.SaveBackupsTip.Key);
            }
        }

        ShowReportButton();
    }

    /// <summary>
    /// Every frame while the instance is up (<see cref="SettingsTab"/>): the choice list each time its tab comes up (its first time it wakes, its items
    /// need it awake; after that its items in the language of the moment and the setting, as the game fills its own lists each time their panel comes
    /// up), the report button when its zip is done; and the rows shown again when the online game starts or ends under the open menu (Leave or End
    /// answered on it, a host closing its lobby): a row that no longer applies goes, as on the next open. A row hidden under the cursor gives the
    /// cursor to the tab's first row (the game's own repair of the selection, through the menu's default selections).
    /// </summary>
    public void Tick()
    {
        SettingsRole role = SessionRoleNow();
        if (role != _openRole)
        {
            ModLog.Info($"Settings menu: the online game {(role == SettingsRole.None ? "ended" : "started")} under the open menu; the Online tab shows the rows for it.");
            Open();
        }

        if (_rows.TryGetValue(SettingsRowId.PlayOnlineWith, out Row? choice) && choice.List is { } list && !list.WasCollected)
        {
            bool shown = choice.Root.activeInHierarchy;
            if (shown && !_choiceShown)
            {
                if (_choiceAwake)
                {
                    ShowChoiceQuietly();
                }
                else
                {
                    WakeChoice(list);
                }
            }

            _choiceShown = shown;
        }

        if (_making && !ReportFlow.Busy)
        {
            ShowReportButton();
        }
    }

    // ---- Making ----

    private void MakeToggle(SettingsRowId id, Transform source, Transform rows, string title, string? line2, string tip)
    {
        GameObject root = Clone(source, rows, id);
        LobbyLocalization.Apply(Text(root.transform, "Text/Line1"), title);
        if (line2 != null)
        {
            LobbyLocalization.Apply(Text(root.transform, "Text/Line2"), line2);
        }

        T2TTooltipDefinition definition = root.GetComponent<T2TTooltipDefinition>() ?? throw new InvalidOperationException($"{id} has no tooltip");
        WriteTip(definition, title, tip);
        Il2CppBishopGames.UI.Toggle toggle = root.GetComponent<Il2CppBishopGames.UI.Toggle>() ?? throw new InvalidOperationException($"{id} has no toggle");
        _toggled ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Il2CppBishopGames.UI.Toggle, bool>>(new Action<Il2CppBishopGames.UI.Toggle, bool>(OnToggled))
            ?? throw new InvalidOperationException("the toggles' delegate couldn't be made");
        (toggle.OnSelectionChanged ?? throw new InvalidOperationException($"{id}'s toggle has no event")).Register(_toggled, "T2TOnline");
        _rows[id] = new Row(id, root, Navigable(root, id)) { Toggle = toggle, Tip = definition, Title = title };
    }

    private void MakeChoice(Transform source, Transform rows)
    {
        GameObject root = Clone(source, rows, SettingsRowId.PlayOnlineWith);
        LobbyLocalization.Apply(Text(root.transform, "Title"), LobbyWords.PlayOnlineWith.Key);
        T2TTooltipDefinition definition = root.GetComponent<T2TTooltipDefinition>() ?? throw new InvalidOperationException("the choice row has no tooltip");
        WriteTip(definition, LobbyWords.PlayOnlineWith.Key, LobbyWords.PlayOnlineWithTip.Key);
        CycleList list = root.GetComponent<CycleList>() ?? throw new InvalidOperationException("the choice row has no list");
        _rows[SettingsRowId.PlayOnlineWith] = new Row(SettingsRowId.PlayOnlineWith, root, list) { List = list, Tip = definition, Title = LobbyWords.PlayOnlineWith.Key };
    }

    private void MakeButton(SettingsRowId id, Transform source, Transform rows, string title, string button, string tip)
    {
        GameObject root = Clone(source, rows, id);
        LobbyLocalization.Apply(Text(root.transform, "Title"), title);
        Transform press = Part(root.transform, "Button");
        TMP_Text text = Text(press, "Text (TMP)");
        LobbyLocalization.Apply(text, button);
        press.Find("icon-warning")?.gameObject.SetActive(false); // the warning is Delete Everything's: nothing here deletes

        // A button row of the game's has no tooltip while open; these get the game's own tooltip definition, as its toggle rows carry one.
        T2TTooltipDefinition definition = press.gameObject.AddComponent(Il2CppType.Of<T2TTooltipDefinition>())?.TryCast<T2TTooltipDefinition>()
            ?? throw new InvalidOperationException($"{id}'s tooltip couldn't be added");
        definition.m_IconColor = Color.white;
        WriteTip(definition, title, tip);
        AnimatedButton click = press.GetComponent<AnimatedButton>() ?? throw new InvalidOperationException($"{id}'s button isn't a button");
        _clicked ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<AbstractNavigable>>(new Action<AbstractNavigable>(OnClicked))
            ?? throw new InvalidOperationException("the buttons' delegate couldn't be made");
        click.OnClick.Register(_clicked, "T2TOnline");
        _rows[id] = new Row(id, root, click) { Tip = definition, ButtonText = text, Dim = press.GetComponent<CanvasGroup>(), Title = title };
    }

    /// <summary>
    /// The panel's two font matchers, as the Gameplay panel's: the rows' titles one size, their second lines another (verified in its prefab). Which
    /// is which is read from the live Gameplay panel's own (the clone's lists named rows that are gone), in the same component order.
    /// </summary>
    private void MatchSizes(Transform source, GameObject panel)
    {
        var lineMatchers = new List<bool>();
        foreach (Component? component in source.gameObject.GetComponents(Il2CppType.Of<FontSizeMatcher>()))
        {
            FontSizeMatcher? original = component?.TryCast<FontSizeMatcher>();
            lineMatchers.Add(original != null && Holds(original, "Line2"));
        }

        int index = -1;
        foreach (Component? component in panel.GetComponents(Il2CppType.Of<FontSizeMatcher>()))
        {
            index++;
            FontSizeMatcher? matcher = component?.TryCast<FontSizeMatcher>();
            if (matcher == null)
            {
                continue;
            }

            bool lines = index < lineMatchers.Count && lineMatchers[index];
            matcher.ClearTextsToMatch();
            foreach (Row row in _rows.Values)
            {
                foreach (string path in lines ? new[] { "Text/Line2" } : new[] { "Text/Line1", "Title" })
                {
                    TMP_Text? text = row.Root.transform.Find(path)?.GetComponent<TMP_Text>();
                    if (text != null && (!lines || text.gameObject.activeSelf))
                    {
                        matcher.AddTextToMatch(text);
                    }
                }
            }
        }
    }

    private static bool Holds(FontSizeMatcher matcher, string name)
    {
        Il2CppSystem.Collections.Generic.List<TMP_Text>? texts = matcher._textsToMatch;
        for (int i = 0; texts != null && i < texts.Count; i++)
        {
            if (texts[i] != null && texts[i].gameObject.name == name)
            {
                return true;
            }
        }

        return false;
    }

    // ---- Showing ----

    private void ShowToggle(SettingsRowId id, bool on)
    {
        if (!_rows.TryGetValue(id, out Row? row) || row.Toggle == null || row.Toggle.WasCollected)
        {
            return;
        }

        if (row.Toggle.m_HasSetOnce)
        {
            row.Toggle.SetSelected(on, false);
        }
        else
        {
            row.Toggle.m_IsSelected = on; // asleep: its own Awake shows it
        }
    }

    private void ShowChoice()
    {
        if (!_choiceAwake || !_rows.TryGetValue(SettingsRowId.PlayOnlineWith, out Row? row) || row.List == null || row.List.WasCollected)
        {
            return;
        }

        string language = LobbyLocalization.Language().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (language != _itemsLanguage)
        {
            _itemsLanguage = language;
            var items = new Il2CppSystem.Collections.Generic.List<string>();
            items.Add(LobbyLocalization.Say(LobbyTerms.Auto));
            items.Add(LobbyLocalization.Say(LobbyTerms.Controller));
            items.Add(LobbyLocalization.Say(LobbyTerms.Keyboard));
            row.List.Init(items.Cast<Il2CppSystem.Collections.IList>());
        }

        int index = (int)ModSettings.OnlineInput;
        if (row.List.CurrentIndex != index)
        {
            row.List.SelectIndex(index, false);
        }
    }

    /// <summary>The list woke (its tab shown): its items, the setting, and its changes heard from now on (as the game's own lists start).</summary>
    private void WakeChoice(CycleList list)
    {
        _choiceAwake = true;
        ShowChoiceQuietly();
        _chose ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Il2CppSystem.Object>>(new Action<Il2CppSystem.Object>(OnChose))
            ?? throw new InvalidOperationException("the choice's delegate couldn't be made");
        list.OnSelectionChanged.Register(_chose, "T2TOnline");
    }

    /// <summary>The choice list's items and setting shown, its own change events not taken for a press.</summary>
    private void ShowChoiceQuietly()
    {
        _setting = true;
        try
        {
            ShowChoice();
        }
        finally
        {
            _setting = false;
        }
    }

    /// <summary>
    /// After an error the tab opens on Problem Report (<see cref="SettingsStates.Landing"/>): the panel's remembered row, which its
    /// <c>NavigableParentSelectPrevious</c> selects when the tab is chosen (verified in the game's code: <c>set_IsSelected</c> selects
    /// <c>m_LastHighlighted</c> when it's alive). In the other states the panel keeps the player's last row, as the game's own panels do.
    /// </summary>
    private void Land(in SettingsContext context)
    {
        if (context.State != SettingsState.AfterError || _parent == null || _parent.WasCollected)
        {
            return;
        }

        if (SettingsStates.Landing(context, _rows.Keys) is not { } landing || !_rows.TryGetValue(landing, out Row? row) || !row.Root.activeSelf)
        {
            return;
        }

        _parent.m_LastHighlighted = row.Navigable;
        if (!_landingSaid)
        {
            _landingSaid = true;
            ModLog.Info($"Settings menu: after an error, the Online tab's cursor starts on {(landing == SettingsRowId.ProblemReport ? "Problem Report" : landing.ToString())}.");
        }
    }

    private void ShowReportButton()
    {
        _making = ReportFlow.Busy;
        if (_rows.TryGetValue(SettingsRowId.ProblemReport, out Row? row) && row.ButtonText != null && !row.ButtonText.WasCollected)
        {
            LobbyLocalization.Apply(row.ButtonText, _making ? LobbyWords.Making.Key : LobbyWords.MakeReport.Key);
        }
    }

    // ---- Presses ----

    private void OnToggled(Il2CppBishopGames.UI.Toggle toggle, bool on)
    {
        if (_setting || toggle == null)
        {
            return;
        }

        try
        {
            Row? row = Find(r => r.Toggle?.Pointer == toggle.Pointer);
            if (row == null)
            {
                return;
            }

            switch (row.Id)
            {
                case SettingsRowId.ShareAchievements:
                    Switch(row, nameof(ModSettings.ShareAchievements), "Share Achievements", on, ModSettings.ShareAchievements);
                    break;
                case SettingsRowId.ShareProgress:
                    Switch(row, nameof(ModSettings.ShareProgress), "Share Progress", on, ModSettings.ShareProgress);
                    break;
                case SettingsRowId.DirectConnections:
                    DirectConnections(row, on);
                    break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Settings: a toggle's press failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>A switch's press: saved (or held for a check tour), or the row back to what the setting is, with why on the game's prompt.</summary>
    private void Switch(Row row, string setting, string rowLabel, bool on, bool now)
    {
        // The toggle's own Awake says its state once; only a change is a press.
        if (on == now)
        {
            return;
        }

        if (ModSettings.TrySetSwitch(setting, on, out string problem))
        {
            ModLog.Info($"Settings: {rowLabel} {(on ? "on" : "off")} ({(TourHolds.On ? "held for the check tour" : "saved")}, Settings > Online).");
            return;
        }

        Refused(row, rowLabel, problem);
    }

    /// <summary>
    /// Direct Connections: on only after the game's yes/no (Cancel first, then Turn On), and only then saved; off saved at once; never during a check
    /// tour. The toggle shows what is saved until the answer. Turning it on is this player's own consent to show their address: with someone else in
    /// this PC's online game the question isn't put (a friend's press can answer a menu on the host's screen); the game's notice says to leave first,
    /// and a Turn On that comes after someone joined under the question isn't saved. Turning it off is allowed at any time.
    /// </summary>
    private void DirectConnections(Row row, bool on)
    {
        bool saved = ModSettings.AllowDirectConnections;
        DirectConnectionsPress press = DirectConnectionsRule.OnPress(saved, on, TourHolds.On, OthersInSession());
        switch (press)
        {
            case DirectConnectionsPress.Nothing:
                return;
            case DirectConnectionsPress.Refuse:
                PutBack(row, saved);
                ModLog.Info("Settings: Direct Connections isn't changed while a check tour runs.");
                return;
            case DirectConnectionsPress.TurnOff:
                Switch(row, nameof(ModSettings.AllowDirectConnections), "Direct Connections", false, saved);
                return;
            case DirectConnectionsPress.RefuseInSession:
                PutBack(row, saved);
                LeaveFirstForDirectConnections("asked to turn on");
                return;
        }

        PutBack(row, saved);
        bool asked = LobbyPrompts.Ask(LobbyLocalization.Say(LobbyWords.DirectConnections.Key), LobbyLocalization.Say(LobbyWords.DirectConnectionsAsk.Key),
            LobbyLocalization.Say(LobbyTerms.Cancel), LobbyLocalization.Say(LobbyWords.TurnOn.Key), turnOn =>
            {
                try
                {
                    if (!turnOn)
                    {
                        ModLog.Info("Settings: Direct Connections stays off (Cancel).");
                        return;
                    }

                    if (!DirectConnectionsRule.MayTurnOn(TourHolds.On, OthersInSession()))
                    {
                        // Someone joined while the question was up: its answer may be their press.
                        LeaveFirstForDirectConnections("Turn On answered");
                        return;
                    }

                    if (!ModSettings.TrySetSwitch(nameof(ModSettings.AllowDirectConnections), true, out string problem))
                    {
                        Refused(row, "Direct Connections", problem);
                        return;
                    }

                    PutBack(row, true);
                    ModLog.Info("Settings: Direct Connections on (saved, Settings > Online; from the next start, the players you connect to can see your IP address).");
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"Settings: Direct Connections' answer failed ({ModLog.SafeErrorText(ex)}).");
                }
            });
        ModLog.Info(asked ? "Settings: Direct Connections asked to turn on: the game's yes/no asks first." : "Settings: Direct Connections asked to turn on, but a question is open already; it stays off.");
    }

    /// <summary>
    /// True when someone else is in this PC's online game (<see cref="DirectConnectionsRule.OthersInSession"/>): it is in a host's, or it hosts one
    /// with anyone in it. The rows' own session role (none unless online is on) and the session's count: asked at a press and at an answer.
    /// </summary>
    private static bool OthersInSession() =>
        DirectConnectionsRule.OthersInSession(SessionRoleNow(), NativeUiLayer.Session?.OthersHosted ?? 0);

    /// <summary>Direct Connections stays off with others in the online game: the game's notice says to leave first, and one line says why.</summary>
    private static void LeaveFirstForDirectConnections(string what)
    {
        bool told = LobbyPrompts.Tell(LobbyLocalization.Say(LobbyWords.DirectConnections.Key), LobbyLocalization.Say(LobbyWords.LeaveFirst.Key),
            LobbyLocalization.Say(LobbyTerms.Ok), static () => { });
        ModLog.Info($"Settings: Direct Connections {what} with others in this online game; it stays off (it is turned on out of an online game: the question is this player's own to answer)" +
            (told ? "; the game's notice says to leave first." : "; another prompt is up, so no notice."));
    }

    private void OnChose(Il2CppSystem.Object _)
    {
        if (_setting)
        {
            return;
        }

        try
        {
            if (!_rows.TryGetValue(SettingsRowId.PlayOnlineWith, out Row? row) || row.List == null || row.List.WasCollected)
            {
                return;
            }

            int index = row.List.CurrentIndex;
            if (index is < 0 or > 2 || index == (int)ModSettings.OnlineInput)
            {
                return;
            }

            var choice = (OnlineInputChoice)index;
            if (ModSettings.TrySetOnlineInput(choice, out string problem))
            {
                ModLog.Info($"Settings: Play Online With {choice} ({(TourHolds.On ? "held for the check tour" : "saved")}).");
                return;
            }

            Refused(row, "Play Online With", problem);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Settings: Play Online With's change failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private void OnClicked(AbstractNavigable nav)
    {
        try
        {
            Row? row = nav == null ? null : Find(r => r.Navigable.Pointer == nav.Pointer);
            switch (row?.Id)
            {
                case SettingsRowId.SaveBackups:
                    SaveBackups(nav!);
                    break;
                case SettingsRowId.ProblemReport:
                    if (ReportFlow.Ask(Core.Diagnostics.ReportSources.SettingsOnline))
                    {
                        ShowReportButton();
                    }
                    else
                    {
                        nav!.SetClickResult(AbstractNavigable.EClickResult.InvalidNavigation);
                    }

                    break;
                case SettingsRowId.LeaveOnlineGame:
                case SettingsRowId.EndOnlineGame:
                    Leave(nav!, row.Id == SettingsRowId.EndOnlineGame);
                    break;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Settings: a button's press failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Save Backups: the Backups screen over Settings; out of the title the game's own refusal; in a session the game's prompt says to leave first.</summary>
    private static void SaveBackups(AbstractNavigable nav)
    {
        SettingsRowRefusal refusal = SettingsRows.Refusal(SettingsRowId.SaveBackups, Context());
        LastBackupsRefusal = refusal;
        if (refusal != SettingsRowRefusal.None)
        {
            nav.SetClickResult(AbstractNavigable.EClickResult.InvalidNavigation);
            ModLog.Info($"Settings menu: Save Backups refused ({SettingsRows.Describe(refusal)}).");
            if (refusal == SettingsRowRefusal.InSession)
            {
                LobbyPrompts.Tell(LobbyLocalization.Say(LobbyWords.SaveBackups.Key), LobbyLocalization.Say(LobbyWords.LeaveFirst.Key), LobbyLocalization.Say(LobbyTerms.Ok), static () => { });
            }

            return;
        }

        var options = new ShellOptions { HeaderTerm = LobbyWords.SaveBackups.Key, AnyPlayer = true, BackAsks = false };
        if (!LobbyShell.RequestOpen(ShellKind.Backups, options, out string problem))
        {
            nav.SetClickResult(AbstractNavigable.EClickResult.InvalidNavigation);
            ModLog.Warn($"Settings menu: Save Backups refused ({problem})");
            return;
        }

        SettingsTab.BackupsOpenedFrom(nav);
        ModLog.Info("Settings menu: Save Backups opens the Backups screen.");
    }

    /// <summary>Leave Online Game / End Online Game: the game's yes/no, Stay first; then F7's own leave.</summary>
    private static void Leave(AbstractNavigable nav, bool end)
    {
        Online.OnlineSession? session = NativeUiLayer.Session;
        if (session == null || !session.InOnlineGame)
        {
            nav.SetClickResult(AbstractNavigable.EClickResult.InvalidNavigation);
            ModLog.Info("Online: Leave from Settings > Online: no session is on.");
            return;
        }

        string what = end ? "End Online Game" : "Leave Online Game";
        bool asked = LobbyPrompts.Ask(LobbyLocalization.Say(end ? LobbyWords.EndOnlineGame.Key : LobbyWords.LeaveOnlineGame.Key),
            LobbyLocalization.Say(end ? LobbyWords.AskEndOnlineGame.Key : LobbyWords.AskLeaveOnlineGame.Key),
            LobbyLocalization.Say(LobbyWords.Stay.Key), LobbyLocalization.Say(end ? LobbyWords.End.Key : LobbyWords.LeaveButton.Key), leave =>
            {
                ModLog.Info($"Online: {what} from Settings > Online: {(leave ? "confirmed" : "stayed")}.");
                if (leave)
                {
                    session.LeaveFromSettings();
                }
            });
        if (!asked)
        {
            nav.SetClickResult(AbstractNavigable.EClickResult.InvalidNavigation);
            ModLog.Info($"Online: {what} from Settings > Online: a question is open already.");
        }
    }

    private void Refused(Row row, string rowLabel, string problem)
    {
        _setting = true;
        try
        {
            ShowToggle(SettingsRowId.ShareAchievements, ModSettings.ShareAchievements);
            ShowToggle(SettingsRowId.ShareProgress, ModSettings.ShareProgress);
            ShowToggle(SettingsRowId.DirectConnections, ModSettings.AllowDirectConnections);
            ShowChoice();
        }
        finally
        {
            _setting = false;
        }

        ModLog.Warn($"Settings: {rowLabel} couldn't change ({problem}); the row shows the setting as it is.");
        LobbyPrompts.Tell(LobbyLocalization.Say(row.Title), problem, LobbyLocalization.Say(LobbyTerms.Ok), static () => { });
    }

    private void PutBack(Row row, bool on)
    {
        _setting = true;
        try
        {
            if (row.Toggle != null && !row.Toggle.WasCollected && row.Toggle.IsSelected != on)
            {
                row.Toggle.SetSelected(on, false);
            }
        }
        finally
        {
            _setting = false;
        }
    }

    // ---- Reading ----

    /// <summary>
    /// Where the menu is now, for the rows: the title (the scene the game's lock opens in), the session (the native screens' layer's: none with online off
    /// or after an error), the mod's screens, Direct Connections, and what the mod runs.
    /// </summary>
    public static SettingsContext Context()
    {
        // The screens check tour's pictures of states its fake session can't make; never while no tour holds the player's data.
        if (ForCheck is { } check && TourHolds.On)
        {
            return check;
        }

        bool atTitle = string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, TitleScene, StringComparison.Ordinal);
        return new SettingsContext(atTitle, RoleNow(), LobbyShell.IsOpen, ModSettings.AllowDirectConnections, NativeUiLayer.RelayRefused, NativeUiLayer.State);
    }

    /// <summary>
    /// The part in a session the rows show for now, as <see cref="Open"/> remembers it from its context (<see cref="SettingsContext.SessionRole"/>): a check tour's own
    /// context first, else this PC's part while online is on and none otherwise. The same rule on both sides of <see cref="Tick"/>'s comparison: after an error a flow
    /// stopped half-way may still say host or friend, and the rows, shown for no session then, are not shown again every frame for it. A few field reads: asked every
    /// frame a menu is up.
    /// </summary>
    private static SettingsRole SessionRoleNow() =>
        ForCheck is { } check && TourHolds.On ? check.SessionRole : SettingsStates.SessionRole(NativeUiLayer.State, RoleNow());

    /// <summary>This PC's part in an online game now, whatever the mod runs (<see cref="Context"/> gives it to the rows' rule with the state). A few field reads.</summary>
    private static SettingsRole RoleNow()
    {
        Online.OnlineSession? session = NativeUiLayer.Session;
        return session is { InOnlineGame: true } ? (session.HostsOnlineGame ? SettingsRole.Host : SettingsRole.Friend)
            : Online.CampaignRelay.Current?.Following == true ? SettingsRole.Friend
            : MenuViews.Instance?.Hosting == true ? SettingsRole.Host
            : SettingsRole.None;
    }

    /// <summary>
    /// The rows' up and down, as the game's own panels link theirs: each row's serialized neighbours name the next and the previous row (the first's up
    /// and the last's down none; left and right none), and the panel's parent registers its rows without finding neighbours by itself
    /// (<c>NavigableParent.BehaviourAwake</c> calls <c>RegisterChildren(false)</c>; verified in the game's code and its Settings prefab). A clone keeps
    /// its source's links, which name rows of the Gameplay and System panels (inactive under ours): they're cleared first.
    /// </summary>
    private static void LinkRows(IReadOnlyList<AbstractNavigable> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].ClearNeighbors();
            if (i > 0)
            {
                rows[i].SetNeighbor(AbstractNavigable.EDirection.Up, rows[i - 1]);
            }

            if (i < rows.Count - 1)
            {
                rows[i].SetNeighbor(AbstractNavigable.EDirection.Down, rows[i + 1]);
            }
        }
    }

    private Row? Find(Func<Row, bool> match)
    {
        foreach (Row row in _rows.Values)
        {
            if (match(row))
            {
                return row;
            }
        }

        return null;
    }

    private static IntPtr TipClass => Il2CppClassPointerStore<T2TTooltipDefinition>.NativeClassPtr;

    private static void WriteTip(T2TTooltipDefinition definition, string title, string text)
    {
        NativeFields.WriteTerm(definition, TipClass, "m_Title", title);
        NativeFields.WriteTerm(definition, TipClass, "m_Description", text);
    }

    private static GameObject Clone(Transform source, Transform rows, SettingsRowId id)
    {
        GameObject root = UiClone.Make(source.gameObject, RowKeep, $"the {id} row", out string problem) ?? throw new InvalidOperationException(problem);
        root.name = $"Row_{id}";
        UiClone.Place(root, rows);
        return root;
    }

    private static AbstractNavigable Navigable(GameObject root, SettingsRowId id) =>
        root.GetComponent<AbstractNavigable>() ?? throw new InvalidOperationException($"{id} isn't navigable");

    private static TMP_Text Text(Transform root, string path) =>
        Part(root, path).GetComponent<TMP_Text>() ?? throw new InvalidOperationException($"{root.name}/{path} isn't text");

    private static Transform Part(Transform root, string path) => root.Find(path) ?? throw new InvalidOperationException($"{root.name} has no {path}");

    private static HashSet<string> Keep(IEnumerable<string> start, params string[] more)
    {
        var keep = new HashSet<string>(start, StringComparer.Ordinal);
        keep.UnionWith(more);
        return keep;
    }

    /// <summary>One row: its object, what the cursor lands on, and what it's made of.</summary>
    private sealed class Row
    {
        public Row(SettingsRowId id, GameObject root, AbstractNavigable navigable)
        {
            Id = id;
            Root = root;
            Navigable = navigable;
        }

        public SettingsRowId Id { get; }

        public GameObject Root { get; }

        public AbstractNavigable Navigable { get; }

        public string Title { get; init; } = string.Empty;

        public Il2CppBishopGames.UI.Toggle? Toggle { get; init; }

        public CycleList? List { get; init; }

        public T2TTooltipDefinition? Tip { get; init; }

        public TMP_Text? ButtonText { get; init; }

        public CanvasGroup? Dim { get; init; }
    }
}
#endif
