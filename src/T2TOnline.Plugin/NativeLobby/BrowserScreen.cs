#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Text;
using Il2CppBishopGames.UI;
using Il2CppTMPro;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.NativeLobby.Widgets;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// Join Lobby: the friends browser on the Glossary's frame. A function of <see cref="BrowserList"/>, itself fed from <see cref="ILobbyActions.Listings"/>: rows stay in a stable order and each keeps
/// its object, so an auto-refresh never moves the cursor; a greyed row can be focused and explains itself; an empty list offers Host Lobby; a row pressed turns the panel into Joining with a Cancel.
/// </summary>
internal sealed class BrowserScreen
{
    private const double RefreshSeconds = 0.5;

    // What's still a press of the row that opened the list, not a choice on it: the lobby's own half second after a return.
    private const double ArrivalSeconds = LobbyTiming.InputLockMicros / 1_000_000.0;

    private readonly LobbyScreens _owner;
    private readonly ILobbyActions _actions;
    private readonly BrowserList _list;
    private readonly Dictionary<ulong, ListRow> _rows = new();

    // The Join list's rows are named by this count (Lobby1, Lobby2...), never by a lobby ID: a dump, a warning or a report can't carry one.
    private int _rowsMade;
    private readonly List<AbstractNavigable> _order = new();
    private ShellParts _parts = null!;
    private PromptBar _bar = null!;
    private DetailPane _pane = null!;
    private TextMeshProUGUI _band = null!;
    private double _openedAt;
    private ListRow _hostRow = null!;
    private ListRow _cancelRow = null!;
    private CanvasGroup? _rowsGroup;
    private NavigableParent? _parent;
    private IReadOnlyList<LobbyListing>? _lastListings;
    private double _refreshAt;
    private IntPtr _cursor;
    private ulong _joining;
    private string _joiningName = string.Empty;
    private ulong _landOn;
    private bool _awake;
    private int _language;
    private int _artVersion;

    /// <summary>The screen over its shell; <paramref name="list"/> is the one the Online group reads too.</summary>
    public BrowserScreen(LobbyScreens owner, ILobbyActions actions, BrowserList list)
    {
        _owner = owner;
        _actions = actions;
        _list = list;
    }

    /// <summary>The shell's parts, asleep: empty what the Glossary's frame had, add what this screen needs.</summary>
    public void Build(ShellParts parts)
    {
        _parts = parts;
        _bar = new PromptBar(parts, OnPrompt);
        LobbyFrame.HideTabGlyphs(parts);
        _band = LobbyFrame.BandLabel(parts);
        LobbyFrame.FixedColumn(parts, 620);
        _pane = new DetailPane(parts);
        _rowsGroup = LobbyFrame.RowsContent(parts);
        _parent = parts.ScrollContent.GetComponent<NavigableParent>();
        _hostRow = ListRow.Make(parts.ScrollContent, "HostLobby", withParts: false);
        LobbyClicks.Bind(_hostRow.Button, () => OnPrompt(new LobbyPrompt(LobbyButton.Confirm, LobbyWords.HostLobby.Key, LobbyAction.HostLobby)));
        _cancelRow = ListRow.Make(parts.Description.transform.parent, "Cancel", withParts: false);
        LobbyUi.At(LobbyUi.RectOf(_cancelRow.Root), 0.5f, 0.5f, 0, 0, 320, ListRow.PlainHeight);
        LobbyClicks.Bind(_cancelRow.Button, () => OnPrompt(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Cancel, LobbyAction.CancelJoin)));
        LobbyUi.Show(LobbyUi.RectOf(_cancelRow.Root), false);
    }

    /// <summary>The shell is showing: its parts are awake.</summary>
    public void Opened()
    {
        _awake = true;
        _openedAt = FrameClock.Seconds;
        _language = LobbyLocalization.Language();
        _artVersion = NativeArt.Version;
        _bar.Wake();
        _actions.SetBrowserOpen(true);
        UpdateList(true);
        Redraw();
        SelectDefault();
    }

    /// <summary>Lands on a lobby's row when it's there, next redraw.</summary>
    public void LandOn(ulong lobby) => _landOn = lobby;

    /// <summary>The join being waited for has ended, however: rows are live again.</summary>
    public void JoinEnded()
    {
        _joining = 0;
        if (_awake)
        {
            Redraw();
            SelectDefault();
        }
    }

    /// <summary>A row is being joined from outside (a Try again).</summary>
    public void Joining(ulong lobby, string hostName)
    {
        _joining = lobby;
        _joiningName = hostName;
        if (_awake)
        {
            Redraw();
            Select(_cancelRow.Button);
        }
    }

    /// <summary>Every frame while it's up.</summary>
    public void Tick(double now)
    {
        if (!_awake)
        {
            return;
        }

        if (now >= _refreshAt)
        {
            _refreshAt = now + RefreshSeconds;
            int language = LobbyLocalization.Language();
            bool languageChanged = language != _language;
            _language = language;

            // An optional prefab has come since the last draw: its icons go on (the host's crown, a greyed row's lock).
            bool artChanged = _artVersion != NativeArt.Version;
            _artVersion = NativeArt.Version;
            if (UpdateList(false) || languageChanged || artChanged)
            {
                Redraw();
            }

            foreach (ListRow row in _rows.Values)
            {
                row.Refresh();
            }

            _pane.Refresh();
        }


        // The cursor is the one thing here that isn't drawn from the list: the list follows it.
        AbstractNavigable? current = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        IntPtr pointer = current == null ? IntPtr.Zero : current.Pointer;
        if (pointer != _cursor)
        {
            _cursor = pointer;
            OnCursorMoved();
        }
    }

    /// <summary>B, however it came.</summary>
    public void Back()
    {
        LobbyBackRule rule = LobbyRules.Back(_joining != 0 ? LobbyScreen.Joining : LobbyScreen.JoinList, default);
        if (rule.Action == LobbyBackAction.CancelJoin)
        {
            OnPrompt(new LobbyPrompt(LobbyButton.Back, LobbyTerms.Cancel, LobbyAction.CancelJoin));
        }
        else
        {
            _owner.CloseBrowser();
        }
    }

    /// <summary>Its shell has gone.</summary>
    public void Closed()
    {
        _awake = false;
        _actions.SetBrowserOpen(false);
        _bar?.Forget();
        foreach (ListRow row in _rows.Values)
        {
            LobbyClicks.Forget(row.Button);
        }

        if (_hostRow != null)
        {
            LobbyClicks.Forget(_hostRow.Button);
        }

        if (_cancelRow != null)
        {
            LobbyClicks.Forget(_cancelRow.Button);
        }

        _rows.Clear();
        _order.Clear();
        _joining = 0;
    }

    private bool UpdateList(bool force)
    {
        IReadOnlyList<LobbyListing> listings = _actions.Listings();
        bool fading = false;
        foreach (BrowserRow row in _list.Rows)
        {
            fading |= row.Fading;
        }

        if (!force && ReferenceEquals(listings, _lastListings) && !fading)
        {
            return false;
        }

        _lastListings = listings;
        return _list.Update(listings, LobbyScreens.Identity(), FrameClock.NowMicros) || force;
    }

    /// <summary>The whole screen from the list. Rows keep their objects; the cursor isn't moved unless its row went.</summary>
    private void Redraw()
    {
        AbstractNavigable? cursor = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        bool cursorOnHost = cursor == null || cursor.Pointer == _hostRow.Button.Pointer;
        SyncRows();
        bool joining = _joining != 0;
        bool empty = _list.IsEmpty;
        LobbyUi.Show(LobbyUi.RectOf(_hostRow.Root), empty && !joining);
        _hostRow.ShowPlain(LobbyLocalization.Say(LobbyWords.HostLobby.Key));
        LobbyUi.Show(LobbyUi.RectOf(_cancelRow.Root), joining);
        _cancelRow.ShowPlain(LobbyLocalization.Say(LobbyTerms.Cancel));
        if (_rowsGroup != null)
        {
            _rowsGroup.alpha = joining ? 0.4f : 1;
        }

        LobbyLine? live = _list.HostingLine();
        LobbyUi.Say(_band, joining ? new LobbyLine(LobbyWords.JoiningLobby, _joiningName) : empty ? new LobbyLine(LobbyWords.NoFriendsHosting) : live);
        DrawDetails();
        DrawPrompts();

        // The list came or went under an empty state's Host Lobby: the cursor can't stay on what's hidden, or on nothing.
        bool lost = !joining && _awake && !empty && cursorOnHost;
        if (lost)
        {
            SelectDefault();
        }

        if (_landOn != 0 && !joining && _list.Focus(_landOn) && _rows.TryGetValue(_landOn, out ListRow? landed))
        {
            _landOn = 0;
            Select(landed.Button);
        }
    }

    private void SyncRows()
    {
        var seen = new HashSet<ulong>();
        IReadOnlyList<BrowserRow> rows = _list.Rows;
        int colourAt = 0;
        _order.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            BrowserRow row = rows[i];
            seen.Add(row.LobbyId);
            if (!_rows.TryGetValue(row.LobbyId, out ListRow? widget))
            {
                widget = ListRow.Make(_parts.ScrollContent, $"Lobby{++_rowsMade}", withParts: true);
                ulong id = row.LobbyId;
                LobbyClicks.Bind(widget.Button, () => Press(id));
                _rows[id] = widget;
            }

            widget.Root.transform.SetSiblingIndex(i);
            widget.ShowLobby(row, DetailLine(row), LobbyScreens.ColourOf(colourAt++));
            if (!row.Fading)
            {
                _order.Add(widget.Button);
            }
        }

        var gone = new List<ulong>();
        foreach (KeyValuePair<ulong, ListRow> known in _rows)
        {
            if (!seen.Contains(known.Key))
            {
                gone.Add(known.Key);
            }
        }

        bool cursorGone = false;
        AbstractNavigable? current = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        foreach (ulong id in gone)
        {
            ListRow row = _rows[id];
            cursorGone |= current != null && current.Pointer == row.Button.Pointer;
            LobbyClicks.Forget(row.Button);
            UiClone.Destroy(row.Root);
            _rows.Remove(id);
        }

        ListRow.Link(_order);
        _parent?.RegisterChildren(false);
        UiClone.Rebuild(_parts.ScrollContent);
        if (_joining != 0)
        {
            return;
        }

        // The row with the cursor went: the list has moved on to the next. Fading rows lose it too: they can't be pressed.
        BrowserRow? focused = _list.FocusedRow;
        bool onFading = current != null && Fading(current.Pointer);
        if (cursorGone || onFading)
        {
            ModLog.Info($"Lobby: focus moved: a row of the Join list went; on {(focused == null ? "Host Lobby" : "the next row")}.");
            if (focused != null && _rows.TryGetValue(focused.LobbyId, out ListRow? next))
            {
                Select(next.Button);
            }
            else
            {
                Select(_hostRow.Button);
            }
        }
    }

    private bool Fading(IntPtr button)
    {
        foreach (BrowserRow row in _list.Rows)
        {
            if (row.Fading && _rows.TryGetValue(row.LobbyId, out ListRow? widget) && widget.Button.Pointer == button)
            {
                return true;
            }
        }

        return false;
    }

    private void DrawDetails()
    {
        if (_joining != 0)
        {
            ulong host = 0;
            int place = 0;
            IReadOnlyList<BrowserRow> rows = _list.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].LobbyId == _joining)
                {
                    host = rows[i].HostId;
                    place = i;
                }
            }

            _pane.ShowPerson(host, _joiningName, LobbyScreens.ColourOf(place), false, LobbyLocalization.Say(LobbyMarks.Host.Line), true, LobbyLocalization.Say(new LobbyLine(LobbyWords.Joining)), null, 0, -1);
            _pane.SetWords(string.Empty);
            return;
        }

        BrowserRow? row = _list.FocusedRow;
        if (row == null || _list.IsEmpty)
        {
            _pane.ShowLine(LobbyLocalization.Say(new LobbyLine(LobbyWords.NoFriendsHosting)));
            _pane.SetWords(LobbyLocalization.Say(new LobbyLine(LobbyWords.EmptyHint)));
            return;
        }

        bool greyed = row.Block != BrowserBlock.None;
        string role = LobbyLocalization.Say(LobbyMarks.Host.Line);
        string state = LobbyLocalization.Say(row.Mark.Line);
        // Its ping is an estimate from the host's published location: the bars say that much, and no number claims more.
        _pane.ShowPerson(row.HostId, row.HostName, LobbyScreens.ColourOf(RowPlace(row)), greyed, role, true, state, row.Block == BrowserBlock.Checking ? null : row.Count, greyed ? 0 : row.Bars, -1);
        _pane.SetWords(Words(row));
    }

    private int RowPlace(BrowserRow row)
    {
        IReadOnlyList<BrowserRow> rows = _list.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].LobbyId == row.LobbyId)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The row's second line: what's greyed says why; else what the lobby is up to.</summary>
    private static string DetailLine(BrowserRow row)
    {
        if (row.Block != BrowserBlock.None || row.InvitedUs)
        {
            return LobbyLocalization.Say(row.Mark.Line);
        }

        string state = LobbyLocalization.Say(row.Mark.Line);
        string? save = SaveWords(row.Mode, row.Save, compact: true);
        return string.IsNullOrEmpty(save) ? state : state + LobbyWords.Separator + save;
    }

    /// <summary>The description box: the mode and the save in full, and for a greyed row what to do, in the failure's own words.</summary>
    private static string Words(BrowserRow row)
    {
        var words = new StringBuilder();
        string? save = SaveWords(row.Mode, row.Save, compact: false);
        if (!string.IsNullOrEmpty(save))
        {
            words.Append(save);
        }

        FailureMessage? why = JoinFailures.ForRow(row.Block, row.HostName);
        if (why != null)
        {
            if (words.Length > 0)
            {
                words.Append('\n');
            }

            words.Append(LobbyLocalization.Say(why.Line));
        }

        return words.ToString();
    }

    /// <summary>"Campaign • Act 2 • Floor 5", as far as the lobby says; compact: without the mode when there's a save, as a row has little room. Names the squad in full.</summary>
    public static string? SaveWords(LobbyMode? mode, in LobbySaveRow save, bool compact)
    {
        var parts = new List<string>();
        if (mode != null && (!compact || !save.HasRun))
        {
            parts.Add(LobbyLocalization.Say(LobbyRules.ModeTerm(mode.Value)));
        }

        if (save.HasRun)
        {
            if (!compact && save.SquadId.Length > 0)
            {
                parts.Add(SquadName(save.SquadId));
            }

            parts.Add(LobbyLocalization.Say(LobbyLine.Game(LobbyTerms.ActN, LobbyWords.Number(save.Act))));
            parts.Add(LobbyLocalization.Say(new LobbyLine(LobbyWords.FloorN, LobbyWords.Number(save.Floor))));
        }

        return parts.Count == 0 ? null : string.Join(LobbyWords.Separator, parts);
    }

    /// <summary>A squad's name in this PC's language; its id when this PC can't name it.</summary>
    public static string SquadName(string id)
    {
        try
        {
            string name = CampaignGame.SquadName(id);
            return string.IsNullOrEmpty(name) ? SafeText.Clean(id, 32) : name;
        }
        catch (Exception)
        {
            return SafeText.Clean(id, 32);
        }
    }

    private void DrawPrompts()
    {
        if (_joining != 0)
        {
            _bar.Show(LobbyRules.Prompts(LobbyScreen.Joining, new LobbyFocus(LobbyFocusKind.JoinCancel), default));
            return;
        }

        BrowserRow? row = _list.IsEmpty ? null : _list.FocusedRow;
        LobbyFocus focus = row == null ? new LobbyFocus(LobbyFocusKind.EmptyStateHost) : new LobbyFocus(LobbyFocusKind.JoinRow, _list.FocusIndex);
        var facts = new LobbyScreenFacts { JoinRows = _list.Rows.Count, FirstJoinRow = _list.FocusIndex, FocusedRowJoinable = row?.Joinable ?? false, FocusedRowChecking = row?.Block == BrowserBlock.Checking };
        _bar.Show(LobbyRules.Prompts(LobbyScreen.JoinList, focus, facts));
    }

    private void SelectDefault()
    {
        if (_joining != 0)
        {
            Select(_cancelRow.Button);
            return;
        }

        var facts = new LobbyScreenFacts { JoinRows = _order.Count, FirstJoinRow = Math.Max(_list.FocusIndex, 0) };
        LobbyFocus focus = LobbyRules.DefaultFocus(LobbyScreen.JoinList, facts);
        AbstractNavigable? target = focus.Kind == LobbyFocusKind.JoinRow && _list.FocusedRow is { } row && _rows.TryGetValue(row.LobbyId, out ListRow? widget) ? widget.Button : _hostRow.Button;
        Select(target);
    }

    private static void Select(AbstractNavigable target)
    {
        LobbyShell.SetDefaultNavigables(target);
        AbstractNavigableUtils.SelectOnNextFrame(target, AbstractNavigable.ESelectFx.Default);
    }

    private void OnCursorMoved()
    {
        AbstractNavigable? current = MenuGame.Module()?.NavigationController?.CurrentNavigable;
        if (current == null)
        {
            return;
        }

        foreach (KeyValuePair<ulong, ListRow> row in _rows)
        {
            if (row.Value.Button.Pointer == current.Pointer)
            {
                _list.Focus(row.Key);
                LobbyShell.SetDefaultNavigables(row.Value.Button);
                DrawDetails();
                DrawPrompts();
                return;
            }
        }
    }

    private void Press(ulong lobby)
    {
        if (_joining != 0 || !_list.Focus(lobby) || _list.FocusedRow is not { } row)
        {
            return;
        }

        if (row.Joinable)
        {
            OnPrompt(new LobbyPrompt(LobbyButton.Confirm, LobbyWords.Join.Key, LobbyAction.Join));
        }
        else
        {
            OnPrompt(new LobbyPrompt(LobbyButton.Confirm, LobbyWords.Why.Key, LobbyAction.Explain));
        }
    }

    private void OnPrompt(LobbyPrompt prompt)
    {
        // A mash of A on Join Lobby would land here: hosting, or joining whoever comes first, with the list never seen.
        if (prompt.Button == LobbyButton.Confirm && FrameClock.Seconds - _openedAt < ArrivalSeconds)
        {
            return;
        }

        switch (prompt.Action)
        {
            case LobbyAction.Join:
                if (_list.FocusedRow is { Joinable: true } row)
                {
                    _actions.Meter.Press();
                    Joining(row.LobbyId, row.HostName);
                    _owner.JoinFromBrowser(row.LobbyId, row.HostName);
                }

                break;
            case LobbyAction.Explain:
                if (_list.FocusedRow is { } greyed)
                {
                    FailureMessage? why = JoinFailures.ForRow(greyed.Block, greyed.HostName);
                    if (why != null)
                    {
                        _owner.ShowFailure(why with { Landing = FailureLanding.Stay });
                    }
                    else
                    {
                        LobbySounds.Play(LobbySounds.Grunt);
                    }
                }

                break;
            case LobbyAction.Refresh:
                _actions.RefreshListings();
                break;
            case LobbyAction.HostLobby:
                _owner.HostFromBrowser();
                break;
            case LobbyAction.CancelJoin:
                _actions.CancelJoin();
                JoinEnded();
                break;
            case LobbyAction.Back:
                Back();
                break;
        }
    }
}
#endif
