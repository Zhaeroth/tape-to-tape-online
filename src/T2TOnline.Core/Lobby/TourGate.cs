using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// Where the game is, as a check tour reads it before and after each of its steps. A read is one of these; a step's start is
/// a set of them (<see cref="TourStarts"/>).
/// </summary>
[Flags]
public enum TourPlace
{
    /// <summary>Nowhere: no step starts here.</summary>
    None = 0,

    /// <summary>The title at press-to-start, nothing over it.</summary>
    PressStart = 1,

    /// <summary>The title's Main group, nothing over it.</summary>
    Main = 2,

    /// <summary>Another of the title's groups (the Online group, the Play group...), nothing over it.</summary>
    Group = 4,

    /// <summary>
    /// A screen the tours open over the title themselves (<see cref="TourStarts.IsTourScreen"/>): the game's Settings, its
    /// prompts, Glossary, Compendium, Play Now's controller screen, the mod's Join list or Backups screen.
    /// </summary>
    TourScreen = 8,

    /// <summary>The mod's lobby on top.</summary>
    Lobby = 16,

    /// <summary>The mod's lobby open under a game screen (Settings, a prompt, the controller screen, the squad screen of the lobby's start).</summary>
    LobbyCovered = 32,

    /// <summary>A match.</summary>
    Match = 64,

    /// <summary>The campaign's world map (with whatever is over it).</summary>
    Map = 128,

    /// <summary>Between two places: a scene loading, a transition on top, the title changing group or leaving.</summary>
    Loading = 256,

    /// <summary>A game screen over the title the tours never open (the squad screen of a run the tour didn't start, a run's start screens, the new-unlocks screen).</summary>
    OtherScreen = 512,

    /// <summary>The place couldn't be read.</summary>
    Unknown = 1024,

    /// <summary>The title with nothing over it: press-to-start, Main or another group.</summary>
    Title = PressStart | Main | Group,

    /// <summary>The title, or a screen the tours open over it (where the walk home knows what Back does).</summary>
    Home = Title | TourScreen,

    /// <summary>Anywhere the tour can read and knows (never <see cref="OtherScreen"/> or <see cref="Unknown"/>).</summary>
    Any = Title | TourScreen | Lobby | LobbyCovered | Match | Map | Loading,
}

/// <summary>
/// Where every step of every check tour must start, and which must end at the title's Main: one table for all tours, so the
/// place every step runs from can gate each one (<see cref="TourGate"/>). A step that isn't declared never starts.
/// </summary>
public static class TourStarts
{
    // The game's own menus the tours open over the title, by their definition's name (the mod's shell is told by its id).
    private static readonly string[] TourScreens =
    {
        "Settings", "CustomizableYesNoPrompt", "CustomizableDismissablePrompt", "Glossary", "Compendium", "ControllerAssignment",
    };

    private static readonly Dictionary<string, (TourPlace Start, bool EndsAtMain)> Table = Build();

    /// <summary>Every declared step id, with its start and whether it must end at the title's Main.</summary>
    public static IReadOnlyDictionary<string, (TourPlace Start, bool EndsAtMain)> All => Table;

    /// <summary>Where the step <paramref name="stepId"/> may start; <see cref="TourPlace.None"/> when it isn't declared.</summary>
    public static TourPlace Of(string stepId) => stepId != null && Table.TryGetValue(stepId, out var row) ? row.Start : TourPlace.None;

    /// <summary>True when the step <paramref name="stepId"/> must end at the title's Main (it walks home itself, or its last press leads there).</summary>
    public static bool EndsAtMain(string stepId) => stepId != null && Table.TryGetValue(stepId, out var row) && row.EndsAtMain;

    /// <summary>
    /// True when a game menu named <paramref name="menuId"/> (its definition's name) is one the tours open over the title
    /// themselves: the game's Settings, its yes/no and one-button prompts, Glossary, Compendium, Play Now's controller screen.
    /// </summary>
    public static bool IsTourScreen(string? menuId) => menuId != null && Array.IndexOf(TourScreens, menuId) >= 0;

    /// <summary>True when a menu named <paramref name="menuId"/> is one of the game's scene transitions (they go by themselves).</summary>
    public static bool IsTransition(string? menuId) => menuId != null && menuId.StartsWith("Woosh", StringComparison.Ordinal);

    /// <summary>A place, or a set of them, in the summary's words.</summary>
    public static string Describe(TourPlace places)
    {
        if (places == TourPlace.None)
        {
            return "nowhere";
        }

        if (places == TourPlace.Any)
        {
            return "anywhere the tour knows";
        }

        if (places == TourPlace.Home)
        {
            return "the title or a screen the tours open over it";
        }

        if (places == TourPlace.Title)
        {
            return "the title (press-to-start, Main or another group)";
        }

        var words = new List<string>();
        foreach (TourPlace one in Singles)
        {
            if ((places & one) != 0)
            {
                words.Add(Word(one));
            }
        }

        return string.Join(" or ", words);
    }

    private static readonly TourPlace[] Singles =
    {
        TourPlace.PressStart, TourPlace.Main, TourPlace.Group, TourPlace.TourScreen, TourPlace.Lobby, TourPlace.LobbyCovered,
        TourPlace.Match, TourPlace.Map, TourPlace.Loading, TourPlace.OtherScreen, TourPlace.Unknown,
    };

    private static string Word(TourPlace one) => one switch
    {
        TourPlace.PressStart => "the title's press-to-start",
        TourPlace.Main => "the title's Main",
        TourPlace.Group => "another of the title's groups",
        TourPlace.TourScreen => "a screen the tours open over the title",
        TourPlace.Lobby => "the lobby",
        TourPlace.LobbyCovered => "a game screen over the lobby",
        TourPlace.Match => "a match",
        TourPlace.Map => "the world map",
        TourPlace.Loading => "a scene load or a transition",
        TourPlace.OtherScreen => "a game screen the tours never open",
        TourPlace.Unknown => "a place that can't be read",
        _ => one.ToString(),
    };

    private static Dictionary<string, (TourPlace, bool)> Build()
    {
        var table = new Dictionary<string, (TourPlace, bool)>(StringComparer.Ordinal);
        void Add(TourPlace start, bool endsAtMain, params string[] ids)
        {
            foreach (string id in ids)
            {
                table.Add(id, (start, endsAtMain));
            }
        }

        // Every tour's end: puts everything back, from anywhere the tour knows.
        Add(TourPlace.Any, false, "finish");

        // The fake session's tours (all, screens): their leftovers closed first, then the title.
        Add(TourPlace.Title, false, "title", "mainwalk", "group", "selection", "back", "shell", "tenopens", "widgets", "handover", "unload", "languages", "sizes",
            "scr_group", "scr_online_group", "scr_browser", "scr_lobby", "scr_settings_online", "scr_report", "scr_backups", "scr_langs", "scr_sizes");

        // selfdrive: the session's flow through its actions.
        Add(TourPlace.Title, false, "sd_period", "sd_browser", "sd_fail");
        Add(TourPlace.Title | TourPlace.TourScreen, false, "sd_host", "sd_report");
        Add(TourPlace.Lobby, false, "sd_start", "sd_backout", "sd_cancel", "sd_close");
        Add(TourPlace.Lobby, false, UiCheckPlan.CustomTeamStep, UiCheckPlan.CustomNamesStep); // team=custom: before the start, after the return
        Add(TourPlace.Match | TourPlace.Loading | TourPlace.LobbyCovered, false, "sd_match");
        Add(TourPlace.Match | TourPlace.Loading | TourPlace.Title | TourPlace.TourScreen | TourPlace.Lobby | TourPlace.LobbyCovered, false, "sd_return");

        // The tours' first steps that press A on press-to-start, then on Main's rows: never from another of the title's groups,
        // where A is on the player's own rows (the Campaign group's Continue).
        const TourPlace TitleStart = TourPlace.PressStart | TourPlace.Main;

        // padwalk: the title, the Online group, the lobby, the Settings walks (each walks home itself), a Quick Game.
        Add(TitleStart, false, "pw_start");
        Add(TourPlace.Main, false, "pw_group", "pw_join");
        Add(TourPlace.Group, false, "pw_host");
        Add(TourPlace.Lobby, false, "pw_moves", "pw_tabs", "pw_selectors", "pw_invite", "pw_hold", "pw_count", "pw_backout", "pw_newrun", "pw_lobby_settings", "pw_notice");
        Add(TourPlace.Lobby, true, "pw_close");
        Add(TourPlace.Main, true, "pw_mash", "pw_quick");
        Add(TourPlace.Home, true, "pw_settings", "pw_online_settings", "pw_backups", "pw_report");
        Add(TourPlace.Main, true, UiCheckPlan.EndOnlineStep, UiCheckPlan.TitleNewRunStep); // endonline=on: each from Main and back to it

        // kbwalk: the keyboard's join, then the Settings walks and the lobby, each from the title's Main and back to it.
        Add(TitleStart, false, "kb_start");
        Add(TourPlace.Home, true, "kb_settings", "kb_online_settings", "kb_backups", "kb_report", "kb_lobby");

        // onepc: a real lobby with the one-PC friend.
        Add(TitleStart, false, "op_host");
        Add(TourPlace.Lobby, false, "op_arrive", "op_board", "op_close_prompt", "op_start", "op_match", "op_drop", "op_failload", "op_kick");
        Add(TourPlace.Lobby, false, UiCheckPlan.DirectConnectionsStep); // dc=on: from the lobby with the friend in, back to it
        Add(TourPlace.Lobby, true, "op_close");

        // replay: Play Now from the title, a match, back at the title for F2 (a call, from any of its groups).
        Add(TitleStart, false, "rp_playnow");
        Add(TourPlace.Title, false, "rp_rehearsal", "rp_campaign");
        Add(TourPlace.Match, false, "rp_record", "rp_playback", "rp_quit");

        // offline: the title's screens with online off (a notice or prompt may be up at its start).
        Add(TourPlace.Home, false, "off_state", "off_settings", "off_report", "off_backups", "off_offer", "off_notice");

        // campaign: hosted from the title, the run from the lobby, its nodes on the map, its end back at the lobby.
        Add(TitleStart, false, "cp_host");
        Add(TourPlace.Lobby, false, "cp_newrun");
        Add(TourPlace.Map, false, "cp_first", CampaignTour.KeyboardHostStep, "cp_continue", "cp_nodes");
        Add(TourPlace.Map, false, CampaignTour.EventsStep, CampaignTour.LateJoinerStep, CampaignTour.SidesStep); // the steps keys add, on the run's map
        Add(TourPlace.Map | TourPlace.Lobby, false, CampaignTour.BrawlStep); // after the nodes, where cp_end starts too; it presses nothing
        Add(TourPlace.Map | TourPlace.Lobby, false, "cp_end");
        Add(TourPlace.Lobby, true, "cp_close");
        return table;
    }
}

/// <summary>What the gate says before a step.</summary>
public enum TourGateKind
{
    /// <summary>The step runs.</summary>
    Run,

    /// <summary>The step can't start here: it makes no press, click or call and fails as "can't start".</summary>
    CantStart,

    /// <summary>The tour stops: no further step runs, and it quits with every hold on.</summary>
    Stop,
}

/// <summary>The gate's word before a step, and why (empty for <see cref="TourGateKind.Run"/>).</summary>
/// <param name="Kind">Run, can't start, or stop.</param>
/// <param name="Why">Why, in the summary's words.</param>
public readonly record struct TourGateVerdict(TourGateKind Kind, string Why);

/// <summary>
/// A check tour's safety at the place every step runs from: a step starts only where it declares
/// (<see cref="TourStarts"/>); two steps in a row that can't start, a step that must end at the title's Main and doesn't, or
/// the game on a screen the tours never open (or can't read) stop the tour. Once stopped it stays stopped. One per tour, one
/// thread.
/// </summary>
public sealed class TourGate
{
    /// <summary>Steps in a row that can't start before the tour stops.</summary>
    public const int CantStartsToStop = 2;

    private readonly List<string> _cantStarts = new();

    /// <summary>Why the tour stopped, or null while it goes on.</summary>
    public string? StopReason { get; private set; }

    /// <summary>The step before which (or after which) the tour stopped, and which of the two; empty while it goes on.</summary>
    public (string When, string StepId) StoppedAt { get; private set; } = (string.Empty, string.Empty);

    /// <summary>True once the tour has stopped.</summary>
    public bool Stopped => StopReason != null;

    /// <summary>A new tour: nothing counted, not stopped.</summary>
    public void Reset()
    {
        _cantStarts.Clear();
        StopReason = null;
        StoppedAt = (string.Empty, string.Empty);
    }

    /// <summary>
    /// Before the step <paramref name="stepId"/>, the game at <paramref name="now"/> (<paramref name="detail"/>, the menu or
    /// scene it is, for the words; may be empty): run it, refuse it ("can't start"), or stop the tour.
    /// </summary>
    public TourGateVerdict Before(string stepId, TourPlace now, string? detail)
    {
        ArgumentNullException.ThrowIfNull(stepId);
        if (StopReason != null)
        {
            return new TourGateVerdict(TourGateKind.Stop, StopReason);
        }

        string at = At(now, detail);
        if (now is TourPlace.OtherScreen or TourPlace.Unknown or TourPlace.None)
        {
            return StopBefore(stepId, $"the game is at {at}, a screen the tours never open or can't read");
        }

        TourPlace wanted = TourStarts.Of(stepId);
        if (wanted != TourPlace.None && IsOne(now) && (wanted & now) == now)
        {
            _cantStarts.Clear();
            return new TourGateVerdict(TourGateKind.Run, string.Empty);
        }

        string why = wanted == TourPlace.None ? $"it declares no start; the game at {at}" : $"wanted {TourStarts.Describe(wanted)}, the game at {at}";
        _cantStarts.Add($"{stepId}: {why}");
        if (_cantStarts.Count >= CantStartsToStop)
        {
            return StopBefore(stepId, $"{CantStartsToStop} steps in a row can't start ({string.Join("; ", _cantStarts)})");
        }

        return new TourGateVerdict(TourGateKind.CantStart, $"can't start: {why}; nothing was pressed");
    }

    /// <summary>
    /// After the step <paramref name="stepId"/> ran, the game at <paramref name="now"/>: null to go on, else why the tour
    /// stops (it ended away from the title's Main when it must end there, or on a screen the tours never open or can't read).
    /// </summary>
    public string? After(string stepId, TourPlace now, string? detail)
    {
        ArgumentNullException.ThrowIfNull(stepId);
        if (StopReason != null)
        {
            return StopReason;
        }

        string at = At(now, detail);
        string? why = now is TourPlace.OtherScreen or TourPlace.Unknown or TourPlace.None ? $"the game is at {at}, a screen the tours never open or can't read"
            : TourStarts.EndsAtMain(stepId) && now != TourPlace.Main ? $"it ended away from the title's Main (the game at {at})"
            : null;
        if (why != null)
        {
            StopReason = why;
            StoppedAt = ("after", stepId);
        }

        return why;
    }

    /// <summary>
    /// The step <paramref name="stepId"/> itself can't leave the game where the next step starts (<paramref name="why"/>): the
    /// tour stops after it, as after a step that ended away from where it must. A tour already stopped keeps its first reason.
    /// </summary>
    public void StopAfter(string stepId, string why)
    {
        ArgumentNullException.ThrowIfNull(stepId);
        ArgumentNullException.ThrowIfNull(why);
        if (StopReason != null)
        {
            return;
        }

        StopReason = why;
        StoppedAt = ("after", stepId);
    }

    /// <summary>
    /// The summary's line for a stopped tour: <c>STOPPED: after pw_report: why; not run: pw_quick, finish; the game quits with
    /// every hold on.</c> (<c>not run: none</c> when no step is left).
    /// </summary>
    public static string StoppedLine(string when, string stepId, string why, IReadOnlyList<string> notRun)
    {
        ArgumentNullException.ThrowIfNull(notRun);
        string left = notRun.Count == 0 ? "none" : string.Join(", ", notRun);
        return string.Create(CultureInfo.InvariantCulture, $"STOPPED: {when} {stepId}: {why}; not run: {left}; the game quits with every hold on.");
    }

    private TourGateVerdict StopBefore(string stepId, string why)
    {
        StopReason = why;
        StoppedAt = ("before", stepId);
        return new TourGateVerdict(TourGateKind.Stop, why);
    }

    private static bool IsOne(TourPlace place) => place != TourPlace.None && (place & (place - 1)) == 0;

    private static string At(TourPlace now, string? detail) =>
        string.IsNullOrEmpty(detail) ? TourStarts.Describe(now) : $"{TourStarts.Describe(now)} ({detail})";
}
