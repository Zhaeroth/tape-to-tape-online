using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Lobby;

/// <summary>What must hold for a step of the check tour, for the order rules.</summary>
[Flags]
public enum UiCheckNeeds
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The step opens real menus that write settings or tell the game to re-prepare when a language differs: it runs in the player's own language and window.</summary>
    PlayersSettings = 1,
}

/// <summary>What a step of the check tour changes for the steps after it.</summary>
public enum UiCheckStepKind
{
    /// <summary>A plain step.</summary>
    Plain,

    /// <summary>Changes the language (the block's last step puts the player's back).</summary>
    Language,

    /// <summary>Changes the window's size (the block's last step puts the player's back).</summary>
    Window,

    /// <summary>The last step: puts everything back and ends the tour.</summary>
    Finish,
}

/// <summary>One step of the check tour.</summary>
/// <param name="Id">Its id: the plugin runs the step of this id, and files are named after it.</param>
/// <param name="What">What it checks, for the log and the summary.</param>
/// <param name="Kind">What it changes.</param>
/// <param name="Needs">What it needs.</param>
public sealed record UiCheckStep(string Id, string What, UiCheckStepKind Kind = UiCheckStepKind.Plain, UiCheckNeeds Needs = UiCheckNeeds.None);

/// <summary>
/// The check tour's steps, in order (the plugin's <c>LobbyUiCheck</c> runs them, one iterator per id). To add a step, add a row here and
/// its iterator there. Order is safety, not taste: real menus that write settings run before any language or window change (their close
/// saves <c>config.es3</c> with the window's size then, and a menu closed in another language makes the game re-prepare the title's
/// menus), and the last step restores everything.
/// </summary>
public static class UiCheckPlan
{
    /// <summary>The shell's languages on the tour, by the game's language names (a name starts the language's own name in the game's list).</summary>
    public static readonly IReadOnlyList<string> Languages = new[] { "German", "Chinese", "Japanese", "Russian" };

    /// <summary>The window shapes on the tour: width to height. An ultrawide display is near enough 21:9.</summary>
    public static readonly IReadOnlyList<(int Width, int Height)> Aspects = new[] { (16, 9), (16, 10), (21, 9), (4, 3) };

    /// <summary>How long a tour may take before it stops, restores and ends, unless its declaration says otherwise.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromMinutes(8);

    /// <summary>The window sizes for the tour's <see cref="Aspects"/> on a display of this size: each as large as the display allows (a window clamps to it), even numbers, the shape kept.</summary>
    public static IReadOnlyList<(int Width, int Height)> SizesFor(int displayWidth, int displayHeight)
    {
        var sizes = new List<(int, int)>();
        int width = Math.Max(displayWidth, 2);
        int height = Math.Max(displayHeight, 2);
        foreach ((int w, int h) in Aspects)
        {
            int fitted = width * h / w;
            (int, int) size = fitted <= height ? (width, Even(fitted)) : (Even(height * w / h), height);
            if (!sizes.Contains(size))
            {
                sizes.Add(size);
            }
        }

        return sizes;
    }

    private static int Even(int value) => Math.Max(value & ~1, 2);

    // What the Settings walks check, the pad's and the keyboard's.
    private const string SettingsWalk = "The title's Settings by its row: on Video, eight tabs; to Online by the tab button, the panel's intro, the tooltip; every shown row by up and down; both toggles and the choice changed and put back (held in memory), Direct Connections only checked shown by the rule, never pressed; back to the title";
    private const string OnlineSettingsWalk = "The Online group's Online Settings: the game's Settings on its Online tab; back on the row; the title's own Settings on Video again; one graft for every title open";
    private const string BackupsWalk = "Save Backups: the Backups screen over Settings, both tabs, every row read with its details, one row's Restore asked and cancelled, back on the row and on Save Backups; nothing restored, nothing backed up";
    private const string ReportWalk = "A problem report from Settings > Online's Problem Report and from the Online menu's Report a Problem: each the game's prompt naming its zip, Show File offered at the title; closed";

    // The new native screens on the fake session, shared by the screens tours: declared before the lists that hold them (static initializers run in the
    // file's order). Each opens the game's real Settings, so it runs in the player's own language and window, as the lobby's real menus do.
    private static readonly UiCheckStep ScrOnlineGroup = new("scr_online_group", "The Online group's five rows in order, each with the cursor on it: Host Lobby, Join Lobby, Online Settings, Report a Problem, Back");
    private static readonly UiCheckStep ScrSettingsOnline = new("scr_settings_online", "Settings > Online in every state: the title, the lobby's RT, a match's pause, a friend's own menu; Direct Connections shown and hidden, Save Backups locked and not, Leave and End; each row's tooltip", Needs: UiCheckNeeds.PlayersSettings);
    private static readonly UiCheckStep ScrReport = new("scr_report", "The problem report's prompts: ready at the title (Close, Show File), ready away from it (Close), failed; the offer after an error and after a launch that closed during an online game (Not Now, Make Report)", Needs: UiCheckNeeds.PlayersSettings);
    private static readonly UiCheckStep ScrBackups = new("scr_backups", "The Backups screen over Settings: both tabs, their rows, the details pane, an empty tab, the restore's question", Needs: UiCheckNeeds.PlayersSettings);

    /// <summary>The steps of stage one, in order.</summary>
    public static IReadOnlyList<UiCheckStep> StageOne { get; } = new[]
    {
        new UiCheckStep("title", "The title with the Online row"),
        new UiCheckStep("mainwalk", "The main group: every row in turn, wrapping both ways"),
        new UiCheckStep("group", "The Online group by the game's own SetActiveGroup"),
        new UiCheckStep("selection", "The Online group's first row selected exactly once"),
        new UiCheckStep("back", "Back to the main group by the game's own back handler"),
        new UiCheckStep("shell", "The shell opening: mid-intro and at rest, then closing"),
        new UiCheckStep("tenopens", "The shell opened and closed ten times, with times"),
        new UiCheckStep("widgets", "The shell with a label, a button with a glyph, a prompt bar shortcut, a prompt over it and a toast"),
        new UiCheckStep("handover", "Close for Start: Play Now's controller screen over the shell, backed out, the shell still live", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("unload", "The shell, Glossary, Compendium, the game's own prefab release, the shell again: no white sprites", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("languages", "The title's rows and the shell in other languages; the player's language back", UiCheckStepKind.Language),
        new UiCheckStep("sizes", "The title's rows and the shell at other window sizes; the player's size back", UiCheckStepKind.Window),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>
    /// The self-driving solo run of the session's flow as host, with no friend: the real Join list, a real private lobby, a Quick Game started and played a few seconds under the shell, the
    /// game's own quit, the return, a New Run backed out of, a countdown cancelled, a real failure. The save guard holds through all of it.
    /// </summary>
    public static IReadOnlyList<UiCheckStep> SelfDrive { get; } = new[]
    {
        new UiCheckStep("sd_period", "A friend's two steps on this PC with no host: BeginPlay, then EndPlay: lock, guard, device and prompts all back", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_browser", "The Online group, then the real Join list, with what Steam says today", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_host", "Host Lobby for real, from the Join list if it's empty: the swap of screens, the flow Open, the board, a held bumper, the real friends list, a toast, mode Quick Game", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_start", "Start: the countdown, the game's own LaunchPlayNow under the shell, its controller screen, left forward", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_match", "The match loads and plays a few seconds; the game's own quit", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_return", "Back at the lobby: the new-run flag cleared, no squad screen, the shell over the title, focus, the ribbon, the input lock", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_backout", "Campaign, New Run: the game's flow under the shell, backed out, the lobby live again", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_cancel", "A countdown cancelled: no game, a new epoch next time", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_close", "The lobby closed: no session, a live title", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_fail", "A real failure: a lobby that isn't there: its prompt, the Join list, Back on the Online group", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("sd_report", "A bug report made while the game runs (no Explorer): its zip's name and what it cleaned, for the scan"),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>
    /// The walk by a pad alone: a virtual pad on Rewired, pressed and let go as a person does; nothing calls into a screen. From press-to-start through the title's groups, the real Join list and
    /// the real lobby, every direction, bumper, selector and prompt the tables say, the game's controller screen backed out of, the real Settings, a notice, the close, a Quick Game's return mashed
    /// through. Each press's effect is asserted once. The save guard holds through all of it.
    /// </summary>
    public static IReadOnlyList<UiCheckStep> PadWalk { get; } = new[]
    {
        new UiCheckStep("pw_start", "Press-to-start by the pad's first press: the game's own join, the pad the controlling player, the main group", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_group", "Down, down to Online; A: the Online group, focus by the rule; B: back on Online; again", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_join", "Join Lobby by A: the real Join list; B: back on Join Lobby", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_host", "Host Lobby by A: a real session, the lobby, the cursor on the first empty seat", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_moves", "All four directions through the seats, the column and both ends: the table's neighbours, no wrap", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_tabs", "RB and LB once each: the mode changes once per tap, the save row goes and comes, a cursor on it is put right", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_selectors", "Left and right on the mode and slot selectors: each press one change, the player's slot back", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_invite", "A on an empty seat: the friends list; down and up; B back on that seat. Y: the list; B", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_hold", "A direction held past the game's repeat, on the D-pad and the stick: the repeat's timing, the ends hold, nothing opens", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_count", "Start: the countdown; Y and the bumpers do nothing; B cancels, the cursor on Start; A on Start the same", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_backout", "Start again: the game's controller screen; B as a person would: the lobby alive, the cursor on Start", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_newrun", "Campaign, Start on an empty slot: the game's squad screen over the shell; B as a person would: the lobby alive", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_lobby_settings", "RT: the game's real Settings over the lobby; RB to Online: End Online Game shown, never pressed, Save Backups refused in a session; B closes it: the lobby, no phase change", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_notice", "A notice as a one-button prompt over the lobby, titled Online: A through one, B through another; a toast in the lobby", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_close", "B on the lobby, host alone: it closes with no question: the Online group on Host Lobby; B: Main, the cursor on Online", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_mash", "A mashed on Online and on Join Lobby: one group, one list; a toast at the title; B back", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_settings", SettingsWalk, Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_online_settings", OnlineSettingsWalk, Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_backups", BackupsWalk, Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_report", ReportWalk, Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("pw_quick", "A Quick Game from the lobby by the pad; in the match the pause menu's Settings > Online (Save Backups locked, a report's one-button prompt) and My Settings' look; the game's quit; A and B mashed through the return: the input lock holds", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>
    /// The one-PC test with its friend really seated: this game as its own friend over Steam's loopback. The host's side for real: the seat's stages on the real card, the board to the friend's
    /// session and its member messages back, the barrier with someone in it, game players at Start, the controller screen with them, a match and its return, a drop kept on the card, a kick. The
    /// friend's own following can't run in one game: its flow runs with the following left out.
    /// </summary>
    public static IReadOnlyList<UiCheckStep> OnePc { get; } = new[]
    {
        new UiCheckStep("op_host", "The pad joined, Host Lobby by the pad: a real lobby, host alone", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_arrive", "The friend arrives: Joining on the card, Start's wait by the pad, Here: the avatar, the name, the bars, the toast; the wait starts by itself; B cancels", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_board", "A real board to the friend's session, a real member message back: the ready mark on the card, both ways", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_close_prompt", "B with a friend in: the close prompt: B stays, the focus back; A on Stay stays", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_start", "Start with a friend in: the barrier waits for their ack, they become a game player in seat order, the controller screen with them; B backs out", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_match", "A Quick Game with the friend following (their flow): a toast in the match; the quit; Loading on the card; Here", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_drop", "A simulated drop: Reconnecting on the card with its seconds, the seat kept, Start's wait; back: the same seat", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_failload", "The friend's game reports it couldn't load the next match: the host's AI plays their skater at once; the game's quit; Here again", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_kick", "LT on the card: the question, Stay first; Remove: the seat free; they're refused coming back", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("op_close", "The lobby closed by the pad: a live title, nothing held", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>
    /// The walk by a keyboard: a virtual keyboard made with this PC's own keyboard maps, joined by its own key at press-to-start and the controlling player from
    /// then on, so every screen shows keyboard glyphs. The new screens and the lobby are walked by the keys their actions are bound to (the arrows, Enter, Esc,
    /// the tab keys), as the pad walk presses its buttons; then each row's and bar shortcut's hit area is raycast and clicked in the game's own mouse mode. Raw
    /// keys (Alt, Shift, Tab) and a real mouse stay a person's check. The save guard and the tour's holds keep everything as it was.
    /// </summary>
    public static IReadOnlyList<UiCheckStep> KbWalk { get; } = new[]
    {
        new UiCheckStep("kb_start", "Press-to-start by the keyboard's own join key: the game's own join, the keyboard the controlling player, keyboard glyphs, the main group", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("kb_settings", SettingsWalk + "; then each row by the mouse", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("kb_online_settings", OnlineSettingsWalk + "; the Online group's rows by the mouse", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("kb_backups", BackupsWalk + "; the tabs, the rows and the bar by the mouse", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("kb_report", "A problem report from the Online menu's Report a Problem by Enter: the game's prompt naming its zip; Esc closes it", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("kb_lobby", "Host Lobby by Enter: a real session, the lobby; the arrows through the seats and the column, the tab keys on the mode; the seats, the column and the bar by the mouse; Esc closes it, host alone", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>A look round the screens on the fake session: every state, every language, every shape.</summary>
    public static IReadOnlyList<UiCheckStep> Screens { get; } = new[]
    {
        new UiCheckStep("title", "The title with the Online row"),
        new UiCheckStep("scr_group", "The Online group: its live line and smart focus"),
        ScrOnlineGroup,
        new UiCheckStep("scr_browser", "The Join list: empty, five rows, greyed rows explained, joining, failures"),
        new UiCheckStep("scr_lobby", "The lobby: host alone, four seats, every stage, friend, invite panel, countdown, prompts, ribbon, ten seats"),
        ScrSettingsOnline,
        ScrReport,
        ScrBackups,
        new UiCheckStep("scr_langs", "The worst screens in all 15 languages", UiCheckStepKind.Language),
        new UiCheckStep("scr_sizes", "The screens at each shape, English and German", UiCheckStepKind.Window),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>The same in English alone.</summary>
    public static IReadOnlyList<UiCheckStep> ScreensEnglish { get; } = new[]
    {
        new UiCheckStep("title", "The title with the Online row"),
        new UiCheckStep("scr_group", "The Online group: its live line and smart focus"),
        ScrOnlineGroup,
        new UiCheckStep("scr_browser", "The Join list: empty, five rows, greyed rows explained, joining, failures"),
        new UiCheckStep("scr_lobby", "The lobby: host alone, four seats, every stage, friend, invite panel, countdown, prompts, ribbon, ten seats"),
        ScrSettingsOnline,
        ScrReport,
        ScrBackups,
        new UiCheckStep("scr_sizes", "The screens at each shape, English and German", UiCheckStepKind.Window),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>The scenes in English at one shape: a look round in about three minutes.</summary>
    public static IReadOnlyList<UiCheckStep> ScreensQuick { get; } = new[]
    {
        new UiCheckStep("title", "The title with the Online row"),
        new UiCheckStep("scr_group", "The Online group: its live line and smart focus"),
        ScrOnlineGroup,
        new UiCheckStep("scr_browser", "The Join list: empty, five rows, greyed rows explained, joining, failures"),
        new UiCheckStep("scr_lobby", "The lobby: host alone, four seats, every stage, friend, invite panel, countdown, prompts, ribbon, ten seats"),
        ScrSettingsOnline,
        ScrReport,
        ScrBackups,
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    // ---- The keys that add steps to the tours above (each off by default: without its key a tour runs exactly the steps it lists) ----

    /// <summary>The key that adds <see cref="EndOnlineStep"/> and <see cref="TitleNewRunStep"/> to <c>padwalk</c> (<c>endonline=on</c>).</summary>
    public const string EndOnlineKey = "endonline";

    /// <summary>The key that plays <c>selfdrive</c>'s Quick Game on this PC's first custom team (<c>team=custom</c>; <c>team=last</c>: the teams the game last had, as without it).</summary>
    public const string TeamKey = "team";

    /// <summary>The value of <see cref="TeamKey"/> that adds <see cref="CustomTeamStep"/> and <see cref="CustomNamesStep"/>.</summary>
    public const string CustomTeam = "custom";

    /// <summary>The key that adds <see cref="DirectConnectionsStep"/> to <c>onepc</c> (<c>dc=on</c>).</summary>
    public const string DirectConnectionsKey = "dc";

    /// <summary>
    /// <c>padwalk</c>'s End Online Game: a lobby hosted by the pad, RT, the Online tab, End Online Game and its yes; Settings still up over the
    /// title; the hosted co-op slot's run file let go by the call a restore makes (no restore runs); Settings closed; back to the title's Main.
    /// </summary>
    public const string EndOnlineStep = "pw_end_online";

    /// <summary>
    /// <c>padwalk</c>'s title New Run, at once after <see cref="EndOnlineStep"/>: Campaign, New Run by the pad; its question answered by B
    /// (Cancel) or its squad screen backed out of by B; anything else stops the tour with no press. Always the tour's last step before
    /// <c>finish</c>: it acts on the player's own solo run's title buttons.
    /// </summary>
    public const string TitleNewRunStep = "pw_title_newrun";

    /// <summary><c>selfdrive</c>'s custom team: the first of this PC's custom teams read, its typed names held in memory only, armed for the Quick Game's start.</summary>
    public const string CustomTeamStep = "sd_custom_team";

    /// <summary><c>selfdrive</c>'s check after the match: this launch's session log read, no line holding a typed name the step holds; the cleaner's line and the setup's custom team there.</summary>
    public const string CustomNamesStep = "sd_custom_names";

    /// <summary><c>onepc</c>'s Direct Connections with the one-PC friend in: the row pressed, the game's notice to leave first, the setting off.</summary>
    public const string DirectConnectionsStep = "op_direct";

    /// <summary>The keys <c>padwalk</c> takes.</summary>
    public static IReadOnlyList<UiCheckKey> PadWalkKeys { get; } = new[]
    {
        UiCheckKey.Choice(EndOnlineKey, "adds End Online Game pressed in the lobby, then the title's own New Run answered Cancel (off by default: the walk runs as before)", "on", "off"),
    };

    /// <summary>The keys <c>selfdrive</c> takes.</summary>
    public static IReadOnlyList<UiCheckKey> SelfDriveKeys { get; } = new[]
    {
        UiCheckKey.Choice(TeamKey, "custom: the Quick Game on this PC's first custom team, and no typed name of it in the session log (last by default: the tour runs as before)", CustomTeam, "last"),
    };

    /// <summary>The keys <c>onepc</c> takes.</summary>
    public static IReadOnlyList<UiCheckKey> OnePcKeys { get; } = new[]
    {
        UiCheckKey.Choice(DirectConnectionsKey, "adds Direct Connections pressed with the friend in: refused, the game's notice (off by default: the tour runs as before)", "on", "off"),
    };

    /// <summary><see cref="PadWalk"/> with <see cref="EndOnlineStep"/> and <see cref="TitleNewRunStep"/> last before <c>finish</c> (<c>endonline=on</c>).</summary>
    public static IReadOnlyList<UiCheckStep> PadWalkWithEndOnline { get; } = Before(PadWalk, "finish",
        new UiCheckStep(EndOnlineStep, "Host Lobby by the pad; RT: Settings over the lobby; RB to Online; End Online Game, its yes by the pad: the session over, Settings still up over the title; the hosted slot's run file let go by the call a restore makes, with no restore; B: Settings closed; back to Main", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep(TitleNewRunStep, "At once: Campaign, New Run by the pad; its question answered by B (Cancel) or its squad screen backed out of by B, never anything else; the hold line if the title wasn't reloaded yet, and New Run answering once it is; back to Main", Needs: UiCheckNeeds.PlayersSettings));

    /// <summary><see cref="SelfDrive"/> with <see cref="CustomTeamStep"/> before the Quick Game's start and <see cref="CustomNamesStep"/> after its return (<c>team=custom</c>).</summary>
    public static IReadOnlyList<UiCheckStep> SelfDriveWithCustomTeam { get; } = Before(
        Before(SelfDrive, "sd_start", new UiCheckStep(CustomTeamStep, "This PC's custom teams counted; the first one's typed names held in memory only (none is logged); its pick armed for the Quick Game's start", Needs: UiCheckNeeds.PlayersSettings)),
        "sd_backout",
        new UiCheckStep(CustomNamesStep, "This launch's session log read: no line holds a typed name of the custom team played (its name, city, short name, custom skaters', goalie's, logo's file); the cleaner's line and the setup's custom team there; counts only", Needs: UiCheckNeeds.PlayersSettings));

    /// <summary><see cref="OnePc"/> with <see cref="DirectConnectionsStep"/> after the friend's board, before the close prompt (<c>dc=on</c>).</summary>
    public static IReadOnlyList<UiCheckStep> OnePcWithDirectConnections { get; } = Before(OnePc, "op_close_prompt",
        new UiCheckStep(DirectConnectionsStep, "With the friend in: RT, the Online tab with Direct Connections shown through the check-only rows context (Steam's relay refusal stood in), A on it: refused for the session, the game's notice to leave first, the setting off; B, B: the lobby", Needs: UiCheckNeeds.PlayersSettings));

    /// <summary>Every plan a key makes (the plans of <see cref="Tours"/> are the plans without their keys).</summary>
    public static IReadOnlyList<IReadOnlyList<UiCheckStep>> KeyedPlans { get; } = new[] { PadWalkWithEndOnline, SelfDriveWithCustomTeam, OnePcWithDirectConnections };

    /// <summary>True when <paramref name="steps"/> is <see cref="SelfDrive"/> or its keyed plan.</summary>
    public static bool IsSelfDrivePlan(IReadOnlyList<UiCheckStep> steps) => ReferenceEquals(steps, SelfDrive) || ReferenceEquals(steps, SelfDriveWithCustomTeam);

    /// <summary>True when <paramref name="steps"/> is a walk by a virtual pad or keyboard on the real session (<c>padwalk</c>, <c>onepc</c>, <c>kbwalk</c>), keyed or not.</summary>
    public static bool IsWalkPlan(IReadOnlyList<UiCheckStep> steps) =>
        ReferenceEquals(steps, PadWalk) || ReferenceEquals(steps, PadWalkWithEndOnline) || ReferenceEquals(steps, OnePc) || ReferenceEquals(steps, OnePcWithDirectConnections)
        || ReferenceEquals(steps, KbWalk);

    /// <summary>
    /// The steps <paramref name="request"/>'s tour runs: <c>padwalk</c>, <c>selfdrive</c> and <c>onepc</c> with the steps their keys add,
    /// a campaign tour's by <see cref="CampaignTour.StepsFor"/>, any other tour's as its plan has them.
    /// </summary>
    public static IReadOnlyList<UiCheckStep> StepsFor(UiCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<UiCheckStep> steps = request.Tour.Steps;
        return ReferenceEquals(steps, PadWalk) && IsOn(request, EndOnlineKey) ? PadWalkWithEndOnline
            : ReferenceEquals(steps, SelfDrive) && string.Equals(request.Value(TeamKey), CustomTeam, StringComparison.Ordinal) ? SelfDriveWithCustomTeam
            : ReferenceEquals(steps, OnePc) && IsOn(request, DirectConnectionsKey) ? OnePcWithDirectConnections
            : CampaignTour.StepsFor(request);
    }

    /// <summary>The plan's line at a keyed tour's start (the steps its key added, after which step), or null for any other plan.</summary>
    public static string? PlanLine(IReadOnlyList<UiCheckStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return ReferenceEquals(steps, PadWalkWithEndOnline) ? string.Create(CultureInfo.InvariantCulture, $"padwalk: the plan has {steps.Count} steps, {EndOnlineStep} and {TitleNewRunStep} after pw_quick ({EndOnlineKey}=on)")
            : ReferenceEquals(steps, SelfDriveWithCustomTeam) ? string.Create(CultureInfo.InvariantCulture, $"selfdrive: the plan has {steps.Count} steps, {CustomTeamStep} before sd_start and {CustomNamesStep} after sd_return ({TeamKey}={CustomTeam})")
            : ReferenceEquals(steps, OnePcWithDirectConnections) ? string.Create(CultureInfo.InvariantCulture, $"onepc: the plan has {steps.Count} steps, {DirectConnectionsStep} after op_board ({DirectConnectionsKey}=on)")
            : null;
    }

    private static bool IsOn(UiCheckRequest request, string key) => string.Equals(request.Value(key), "on", StringComparison.Ordinal);

    /// <summary><paramref name="steps"/> with <paramref name="added"/> just before the step <paramref name="id"/> (a list declared above holds it).</summary>
    private static UiCheckStep[] Before(IReadOnlyList<UiCheckStep> steps, string id, params UiCheckStep[] added)
    {
        var list = new List<UiCheckStep>(steps.Count + added.Length);
        bool found = false;
        foreach (UiCheckStep step in steps)
        {
            if (string.Equals(step.Id, id, StringComparison.Ordinal))
            {
                list.AddRange(added);
                found = true;
            }

            list.Add(step);
        }

        return found ? list.ToArray() : throw new InvalidOperationException($"no step {id}");
    }

    /// <summary>Stage one's steps with the screens' before its language and window blocks: everything but the screens at each shape, which <see cref="Screens"/> has.</summary>
    public static IReadOnlyList<UiCheckStep> All { get; } = new[]
    {
        StageOne[0], StageOne[1], StageOne[2], StageOne[3], StageOne[4], StageOne[5], StageOne[6], StageOne[7], StageOne[8], StageOne[9],
        Step(Screens, "scr_group"), ScrOnlineGroup, Step(Screens, "scr_browser"), Step(Screens, "scr_lobby"), ScrSettingsOnline, ScrReport, ScrBackups,
        StageOne[10], Step(Screens, "scr_langs"), StageOne[11], StageOne[12],
    };

    /// <summary>
    /// Every tour <c>run.txt</c> can name (<see cref="UiCheckRequest.TryParse"/>), each with its time limit and the keys it takes: <c>all</c> the whole tour (also
    /// a blank line, and Ctrl+F12), <c>screens</c> a look round the screens on the fake session, <c>screens en</c> the same in English alone, <c>screens quick</c>
    /// that at one shape, <c>selfdrive</c> the session's flow driven through its actions, <c>padwalk</c> the same screens pressed by a pad alone, <c>kbwalk</c>
    /// the new screens by a keyboard and the mouse, <c>onepc</c> a friend really in a seat, <c>replay</c> a friend's match drawn on one PC, the offline start's
    /// walk (<see cref="OfflineStart"/>), <c>campaign act1</c> and <c>campaign full</c> a hosted co-op run. A tour not here is refused. To add one: its steps,
    /// then a row here with its limit and keys. A key that adds steps makes a plan of its own (<see cref="KeyedPlans"/>, <see cref="StepsFor"/>); the
    /// plan here is always the tour without it. Declared after the step lists: static initializers run in the file's order.
    /// </summary>
    public static IReadOnlyList<UiCheckTour> Tours { get; } = new[]
    {
        new UiCheckTour("all", "The title, the shell and its widgets, the screens on the fake session, languages and sizes", All, TimeSpan.FromMinutes(12)),
        new UiCheckTour("screens", "Every screen on the fake session, in all 15 languages and at every shape", Screens, TimeSpan.FromMinutes(12)),
        new UiCheckTour("screens en", "Every screen on the fake session, in English, at every shape", ScreensEnglish, DefaultLimit),
        new UiCheckTour("screens quick", "Every screen on the fake session, in English, at one shape", ScreensQuick, DefaultLimit),
        new UiCheckTour("selfdrive", "The session's flow as host, driven through its actions", SelfDrive, DefaultLimit, SelfDriveKeys),
        new UiCheckTour("padwalk", "The screens pressed by a pad alone: the title, the lobby, Settings > Online, the Backups screen, the problem report, a match", PadWalk, TimeSpan.FromMinutes(10), PadWalkKeys),
        new UiCheckTour("kbwalk", "The new screens and the lobby by a keyboard's keys, and by the mouse in the game's own mouse mode", KbWalk, TimeSpan.FromMinutes(6)),
        new UiCheckTour("onepc", "A friend really in a seat: this game over Steam's loopback", OnePc, DefaultLimit, OnePcKeys),
        new UiCheckTour(ReplayTour.Name, "A friend's match drawing on one PC: a Play Now match recorded (F3), played back in place (F4), loaded fresh and played (F2)", ReplayTour.Steps, ReplayTour.Limit),
        OfflineStart.Tour,
        new UiCheckTour(CampaignTour.Act1Name, "A hosted co-op run through act 1 by the game's own walker, the one-PC friend seated; Continue once, a drop, a concede", CampaignTour.Steps, CampaignTour.Act1Limit, CampaignTour.Keys, CampaignTour.KeysCheck),
        new UiCheckTour(CampaignTour.FullName, "A hosted co-op run to its end by the game's own walker, the one-PC friend seated; Continue once, a drop", CampaignTour.Steps, CampaignTour.FullLimit, CampaignTour.Keys, CampaignTour.KeysCheck),
    };

    /// <summary>The step of <paramref name="steps"/> with the id <paramref name="id"/> (a list declared above holds it).</summary>
    private static UiCheckStep Step(IReadOnlyList<UiCheckStep> steps, string id)
    {
        foreach (UiCheckStep step in steps)
        {
            if (string.Equals(step.Id, id, StringComparison.Ordinal))
            {
                return step;
            }
        }

        throw new InvalidOperationException($"no step {id}");
    }

    /// <summary>What's wrong with a list of steps (null when nothing is): ids repeated or unusable, or the order rules broken.</summary>
    public static string? Problem(IReadOnlyList<UiCheckStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return "no steps";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        bool changed = false;
        for (int i = 0; i < steps.Count; i++)
        {
            UiCheckStep step = steps[i];
            if (step.Id.Length == 0 || !IsFileName(step.Id))
            {
                return $"step {i + 1}: the id \"{step.Id}\" can't name a file";
            }

            if (!ids.Add(step.Id))
            {
                return $"the id \"{step.Id}\" is there twice";
            }

            if (step.Kind == UiCheckStepKind.Finish && i != steps.Count - 1)
            {
                return $"\"{step.Id}\" ends the tour but isn't last";
            }

            if (changed && step.Needs.HasFlag(UiCheckNeeds.PlayersSettings))
            {
                return $"\"{step.Id}\" needs the player's own language and window, but runs after they change";
            }

            changed |= step.Kind is UiCheckStepKind.Language or UiCheckStepKind.Window;
        }

        return steps[^1].Kind == UiCheckStepKind.Finish ? null : "the last step doesn't end the tour";
    }

    private static bool IsFileName(string id)
    {
        foreach (char c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
