#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.Input;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The controller screen (<c>ControllerAssignmentMenu</c>) as a real view on friends (v1, D69 amended). The game
/// builds the same icons on every PC (one per game player); only their state differs, and that state has public setters.
/// <list type="bullet">
/// <item>Host: every icon read as the game's own numbers (<see cref="ControllerScreenRow"/> version 2) with the screen's
/// settings (<see cref="Read"/>); every input on an icon and every ready check marks the screen changed, and it goes out
/// at most every <see cref="ChangeSeconds"/> (<see cref="HostTick"/>); every feedback the screen plays goes as a step
/// (<see cref="HostFeedback"/>).</item>
/// <item>Friend: the screens shown for the host are kept here with the host's newest state (<see cref="Mirror"/>,
/// <see cref="Want"/>), so the patches that cut their icons off this PC's input (MenuPatches.Controllers.cs, the
/// controller-type postfix in Input/VirtualPadCompat.cs) know them and give them the host's flags and colours.</item>
/// </list>
/// Nothing here throws into the game: callers guard, and every read here is guarded where the game can fail.
/// </summary>
internal static class ControllerViews
{
    /// <summary>The menu's class.</summary>
    public const string Type = "ControllerAssignmentMenu";

    /// <summary>Most often a changed screen is read and sent (a stick moved back and forth changes it many times a second).</summary>
    public const double ChangeSeconds = 0.1;

    private const int MaxDirty = 4;
    private const int MaxMirrors = 4;
    private const int HostLinesPerMinute = 30;

    // Host: screens changed since they were last sent, with when they may go next and what changed first.
    private static readonly Dictionary<IntPtr, Dirty> Changed = new();

    // Host: the exhibition screen's teams as last logged.
    private static string _teamsLogged = string.Empty;

    // Host: the exhibition screen's rules page was up at its last look.
    private static bool _inRules;

    // Host: each feedback target's step count (two equal feedbacks in a row are two values).
    private static readonly Dictionary<(IntPtr Menu, int Target), int> FeedbackCounts = new();

    // Friend: the screens shown for the host, by native object.
    private static readonly Dictionary<IntPtr, Mirrored> Mirrors = new();
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static readonly LineBudget HostLines = new("Menus (host's controller screen)", HostLinesPerMinute);

    // ---- Host ----

    /// <summary>
    /// The controller screen's data (the host's side): its settings as menu values, then a row per icon, disabled ones
    /// included, with whose player it is (a friend's virtual pad, by Steam name and ID, or one of the host's own), its side,
    /// lock and state as words for the text panel and as the game's numbers for a friend's real screen, the run's skater at
    /// the locked position, the device and the glyphs' device, its colour (its marker's), AI or not and ready; then whether
    /// everyone's ready. Its icons aren't navigables.
    /// </summary>
    public static void Read(ControllerAssignmentMenu menu, List<KeyValuePair<string, NetValue>> values, List<MenuItem> items)
    {
        values.Add(Value(ControllerScreenState.ModeKey, NetValue.FromInt((int)menu.Mode)));
        values.Add(Value(ControllerScreenState.LockedKey, NetValue.FromBool(menu.m_IsLineupLocked)));
        values.Add(Value(ControllerScreenState.OnboardingKey, NetValue.FromBool(menu.m_IsOnboarding)));
        values.Add(Value(ControllerScreenState.ReadyKey, NetValue.FromBool(menu.AreBothTeamsReady)));
        Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? players = menu.m_Players;
        bool campaign = menu.Mode == ControllerAssignmentMenu.EMode.CampaignSingleSide;
        Il2CppData.TeamData? team = campaign ? MenuGame.Campaign()?.runData?.teamData : null;
        InputSpriteMapper? glyphs = MenuGame.Module()?.InputSpriteMapper;
        int waiting = 0;
        int shown = 0;
        if (!campaign)
        {
            ExhibitionScreen.Read(menu, values);
            if (TeamsLine(menu) is { Length: > 0 } teams)
            {
                items.Add(new MenuItem(MenuPayloads.Teams, new[] { Value("text", NetValue.FromString(teams)) }));
            }
        }

        for (int i = 0; players != null && i < players.Count && i < ControllerScreenState.MaxIcons; i++)
        {
            ControllerAssignmentPlayer? player = players[i];
            if (player == null)
            {
                continue;
            }

            int slot = player.GamePlayerId;
            if (slot is < 0 or > ControllerScreenState.MaxSlot)
            {
                continue;
            }

            PadOwner? owner = MenuPayloads.PadOwnerOf?.Invoke(slot);
            string side = player.CurrentSide switch
            {
                // The game's own exit (ApplyCurrentSelection) skips a player in the middle: on the campaign's one side,
                // that player doesn't play.
                ControllerAssignmentPlayer.ESide.Left => campaign ? "playing" : "left side",
                ControllerAssignmentPlayer.ESide.Right => campaign ? "playing" : "right side",
                _ => campaign ? "not playing" : "middle",
            };
            string position = PositionName(player.LockedSkaterPosition);
            string skater = team == null ? string.Empty : MenuGame.Plain(SkaterAt(team, player.LockedSkaterPosition), 40);
            string name = MenuGame.Plain(owner?.Name, 32);
            string device = player.IsKeyboardAssigned ? "keyboard" : owner != null ? "online pad" : "gamepad";
            bool ai = !player.IsPlayerControlled;
            bool off = player.IsDisabled;
            string text = off ? string.Empty : MenuPayloads.PlayerLine(slot, name.Length > 0 ? name : "the host's PC", side, position, skater, device, ai, player.IsLockedIn);
            waiting += !off && !player.IsLockedIn && !ai && side is not ("not playing" or "middle") ? 1 : 0;
            shown += off ? 0 : 1;
            var numbers = new ControllerRowNumbers((int)player.CurrentSide, Math.Clamp((int)player.LockedSkaterPosition, 0, ControllerScreenState.NoPosition), off,
                GlyphDevice(glyphs, slot));
            items.Add(ControllerScreenRow.Player(text, slot, name, owner?.SteamId ?? 0, side, position, skater, device, ai, Colour(player),
                player.IsLockedIn, player.IsKeyboardAssigned, numbers));
        }

        ControllerGlyphViews.HostRows(menu, items); // each player's own button names, for the icons on friends' screens
        if (shown > 0)
        {
            string status = menu.AreBothTeamsReady ? "Everyone's ready: the match starts." : waiting > 0 ? $"Waiting for {waiting} player(s) to be ready." : "Waiting for everyone to be ready.";
            items.Add(new MenuItem(MenuPayloads.Line, new[] { Value("text", NetValue.FromString(status)) }));
        }
    }

    /// <summary>
    /// An exhibition screen's two teams, for the head of its text panel on a friend when it falls back to text, or
    /// "" until both are up. A team's change comes through an icon's input (<c>BroadcastChange</c>, <see cref="MarkChanged"/>). The
    /// home team is on the right (verified in the game's code: <c>IsHomeTeam</c>). Logged when it changes.
    /// </summary>
    private static string TeamsLine(ControllerAssignmentMenu menu)
    {
        Il2CppData.TeamData? visitor = menu.m_VisitorTeam?.CurrentTeam;
        Il2CppData.TeamData? home = menu.m_HomeTeam?.CurrentTeam;
        string left = MenuGame.Plain(visitor?.teamName, 40);
        string right = MenuGame.Plain(home?.teamName, 40);
        string line = left.Length > 0 && right.Length > 0 ? $"{left} on the left side, {right} on the right side" : string.Empty;
        if (line.Length > 0 && line != _teamsLogged)
        {
            _teamsLogged = line;
            // A custom team's name is player-written: the log has its shape (D127); the panel's heading keeps the name.
            ModLog.Info($"Menus: the host's exhibition controller screen: {Core.Diagnostics.TeamLogText.Of(visitor?.id, left, LogNames.TextShape)} on the left side, " +
                $"{Core.Diagnostics.TeamLogText.Of(home?.id, right, LogNames.TextShape)} on the right side (friends' screens show them; a text panel is headed by them).");
        }

        return line.Length > 0 ? line + "." : line;
    }

    /// <summary>
    /// Host: <paramref name="menu"/> changed (<paramref name="what"/>: an icon's input, or its ready check): it's read and sent
    /// at the next <see cref="HostTick"/> it may go.
    /// </summary>
    public static void MarkChanged(ControllerAssignmentMenu? menu, string what)
    {
        if (menu == null || ExhibitionScreen.PreparingTitleMenus())
        {
            return; // (a title warm-up isn't on the board: nothing to send)
        }

        if (Changed.TryGetValue(menu.Pointer, out Dirty? dirty))
        {
            dirty.Pending = true;
            dirty.What = dirty.What.Length == 0 ? what : dirty.What;
            return;
        }

        if (Changed.Count >= MaxDirty)
        {
            Changed.Clear(); // only ever one screen is up: older entries are menus that went
        }

        Changed[menu.Pointer] = new Dirty(menu) { Pending = true, What = what };
    }

    /// <summary>Host, every frame of the menu views (before their own re-read): each changed screen is read and sent through <paramref name="host"/> when it may go.</summary>
    public static void HostTick(HostMenus host, bool friends)
    {
        ExhibitionScreen.HostTick(host, friends);
        if (Changed.Count == 0)
        {
            return;
        }

        double now = FrameClock.Seconds;
        List<IntPtr>? gone = null;
        foreach (KeyValuePair<IntPtr, Dirty> entry in Changed)
        {
            Dirty dirty = entry.Value;
            if (!dirty.Pending)
            {
                if (now >= dirty.NotBefore)
                {
                    // Sent, and its wait is over: nothing to hold back any more (a kept entry would cost a game call per
                    // frame for the rest of the session). The next change starts a new one.
                    (gone ??= new List<IntPtr>()).Add(entry.Key);
                }

                continue;
            }

            if (now < dirty.NotBefore)
            {
                continue;
            }

            if (dirty.Menu == null || dirty.Menu.WasCollected)
            {
                (gone ??= new List<IntPtr>()).Add(entry.Key);
                continue;
            }

            dirty.Pending = false;
            dirty.NotBefore = now + ChangeSeconds;
            if (friends)
            {
                if (HostLines.TrySpend())
                {
                    ModLog.Info($"Menus: the host's controller screen changed ({dirty.What}); it goes to friends now.");
                }

                host.Changed(dirty.Menu, friends);
            }

            dirty.What = string.Empty;
        }

        if (gone != null)
        {
            foreach (IntPtr key in gone)
            {
                Changed.Remove(key);
            }
        }
    }

    /// <summary>
    /// Host: the screen played a feedback (<c>PlayFeedback_Internal</c>): it goes to friends as a step on the same elements (an
    /// icon's, the global ones or a team's), except the "both teams ready" one the screen's own ready check plays (a
    /// friend's own ready check plays it from the host's state).
    /// </summary>
    public static void HostFeedback(HostMenus host, ControllerAssignmentMenu menu, IntPtr elements, int type, bool negative, int options)
    {
        if (type == (int)EControllerAssignmentFeedback.Global_TeamsReady || elements == IntPtr.Zero)
        {
            return;
        }

        int target = FeedbackTarget(menu, elements);
        if (target < 0)
        {
            ReportOnce($"feedback {type}", $"Menus: the host's controller screen played feedback {(EControllerAssignmentFeedback)type} on elements friends can't find; not sent.");
            return;
        }

        int count = FeedbackCounts.TryGetValue((menu.Pointer, target), out int sent) ? sent + 1 : 0;
        if (FeedbackCounts.Count > 64)
        {
            FeedbackCounts.Clear();
        }

        FeedbackCounts[(menu.Pointer, target)] = count;
        if (ControllerFeedback.TryEncode(type, negative, options, count, out int value))
        {
            host.Item(menu, ControllerFeedback.Group, target, value);
            ReportOnce("feedback", $"Menus: the host's controller screen played feedback {(EControllerAssignmentFeedback)type} on {TargetName(target)}; friends' screens play it too (further ones aren't logged).");
        }
    }

    /// <summary>Host: hosting stopped.</summary>
    public static void ClearHost()
    {
        Changed.Clear();
        FeedbackCounts.Clear();
        _teamsLogged = string.Empty; // the next hosting's log says them again
        _inRules = false;
        ExhibitionScreen.ClearHost();
    }

    /// <summary>
    /// Host: the screen's look was refreshed (<c>RefreshAnimNow</c>, what the rules page's open and close call): when its rules
    /// page came up or went, it's read and sent shortly.
    /// </summary>
    public static void RulesLook(ControllerAssignmentMenu menu)
    {
        bool inRules = menu.m_IsInGameSettings;
        if (inRules != _inRules)
        {
            _inRules = inRules;
            MarkChanged(menu, inRules ? "its rules page came up" : "its rules page went");
        }
    }

    // ---- Friend ----

    /// <summary>
    /// Friend: <paramref name="menu"/> is shown for the host from now on (bound at its data, before its icons are made). A
    /// copy the game kept (retained) from this PC's own use still has its icons and their input: they're cut off here, since
    /// its icons' <c>Initialize</c> skips <c>RegisterInputs</c> (and so the cut-off in its prefix) when the parent, the slot
    /// and the Rewired player are the ones it had.
    /// </summary>
    public static void Mirror(ControllerAssignmentMenu menu)
    {
        if (Mirrors.Count >= MaxMirrors && !Mirrors.ContainsKey(menu.Pointer))
        {
            Mirrors.Clear();
        }

        Mirrors[menu.Pointer] = new Mirrored(menu);
        int cut = 0;
        try
        {
            Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? players = menu.m_Players;
            for (int i = 0; players != null && i < players.Count; i++)
            {
                ControllerAssignmentPlayer? icon = players[i];
                if (icon != null && icon.m_RegisteredPlayer != null)
                {
                    CutOff(icon);
                    cut++;
                }
            }
        }
        catch (Exception ex)
        {
            // Its BroadcastChange stays refused while this PC follows: this PC's input still never moves an icon.
            ModLog.Warn($"Menus: couldn't cut this PC's input off a kept controller screen's icons ({ModLog.SafeErrorText(ex)}); its changes stay refused.");
            return;
        }

        if (cut > 0)
        {
            ReportOnce("controller kept copy", $"Menus: the controller screen here is a copy the game kept from this PC's own use: {cut} icon(s) cut off this PC's input before it shows the host's players.");
        }
    }

    /// <summary>Friend: the host's newest state for <paramref name="menu"/> (what the patches give its icons).</summary>
    public static void Want(ControllerAssignmentMenu menu, ControllerScreenState state)
    {
        if (Mirrors.TryGetValue(menu.Pointer, out Mirrored? mirrored))
        {
            mirrored.State = state;
        }
    }

    /// <summary>Friend: the colour the mirror last painted the icon of <paramref name="slot"/> with (<see cref="ControllerScreenPlan.Unpainted"/> for none).</summary>
    public static long Painted(ControllerAssignmentMenu menu, int slot) =>
        Mirrors.TryGetValue(menu.Pointer, out Mirrored? mirrored) && mirrored.Painted.TryGetValue(slot, out long colour) ? colour : ControllerScreenPlan.Unpainted;

    /// <summary>Friend: the icon of <paramref name="slot"/> was painted <paramref name="colour"/> (-1: the game's own).</summary>
    public static void SetPainted(ControllerAssignmentMenu menu, int slot, long colour)
    {
        if (Mirrors.TryGetValue(menu.Pointer, out Mirrored? mirrored))
        {
            mirrored.Painted[slot] = colour;
        }
    }

    /// <summary>Friend: the screens no view shows any more (<paramref name="keep"/> says which still are) are forgotten.</summary>
    public static void Prune(Func<IntPtr, bool> keep)
    {
        List<IntPtr>? gone = null;
        foreach (KeyValuePair<IntPtr, Mirrored> entry in Mirrors)
        {
            if (!keep(entry.Key))
            {
                (gone ??= new List<IntPtr>()).Add(entry.Key);
            }
        }

        if (gone != null)
        {
            foreach (IntPtr key in gone)
            {
                Mirrors.Remove(key);
            }
        }
    }

    /// <summary>Friend: how many screens are shown for the host now.</summary>
    public static int MirrorCount => Mirrors.Count;

    /// <summary>Friend: following stopped; every screen is forgotten.</summary>
    public static void ClearFriend()
    {
        Mirrors.Clear();
        ControllerGlyphViews.ClearFriend();
    }

    /// <summary>
    /// True for a screen this PC shows for the host (or opened for one that's gone, closed once it's up): its icons take no
    /// input of this PC's and show the host's state.
    /// </summary>
    public static bool Ours(ControllerAssignmentMenu? menu) =>
        menu != null && (Mirrors.ContainsKey(menu.Pointer) || MenuViews.Instance?.Friend.Shows(menu) == true);

    /// <summary>
    /// The host's control flags for an icon of a screen shown for the host: true with them when the host's state has its
    /// player; false otherwise (left as the game set them).
    /// </summary>
    public static bool HostFlags(ControllerAssignmentPlayer icon, out bool keyboard, out bool controlled)
    {
        keyboard = false;
        controlled = false;
        ControllerAssignmentMenu? menu = icon.Parent;
        if (menu == null || !Mirrors.TryGetValue(menu.Pointer, out Mirrored? mirrored) || mirrored.State == null ||
            !mirrored.State.TryGet(icon.GamePlayerId, out ControllerIcon want))
        {
            return false;
        }

        keyboard = want.Keyboard;
        controlled = want.Controlled;
        return true;
    }

    /// <summary>The host's colour for an icon of a screen shown for the host, or null (unknown, or not one: the game's own).</summary>
    public static Color? HostColour(ControllerAssignmentPlayer icon)
    {
        ControllerAssignmentMenu? menu = icon.Parent;
        return menu != null && Mirrors.TryGetValue(menu.Pointer, out Mirrored? mirrored) && mirrored.State != null &&
            mirrored.State.TryGet(icon.GamePlayerId, out ControllerIcon want) && want.Colour >= 0
            ? MenuPayloads.FromRgba(want.Colour)
            : null;
    }

    /// <summary>
    /// An icon of a screen shown for the host is cut off this PC's input (its <c>RegisterInputs</c> is skipped): delegates a
    /// copy the game kept registered from this PC's own use are removed, and its Rewired player forgotten, so this PC's own
    /// next use of the screen registers its input again (the game skips that when the player is the one it had).
    /// </summary>
    public static void CutOff(ControllerAssignmentPlayer icon)
    {
        if (icon.m_RegisteredPlayer != null)
        {
            icon.UnregisterInputs();
        }

        icon.RewiredPlayer = null!;
    }

    /// <summary>The icons of <paramref name="menu"/> as they show now, by their order in the menu, with the mirror's colour and glyph state.</summary>
    public static List<(ControllerAssignmentPlayer Icon, ControllerIconShown Shown)> Icons(ControllerAssignmentMenu menu)
    {
        var icons = new List<(ControllerAssignmentPlayer, ControllerIconShown)>();
        Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? players = menu.m_Players;
        for (int i = 0; players != null && i < players.Count && i < ControllerScreenState.MaxIcons; i++)
        {
            ControllerAssignmentPlayer? icon = players[i];
            if (icon == null)
            {
                continue;
            }

            int slot = icon.GamePlayerId;
            icons.Add((icon, new ControllerIconShown(slot, (int)icon.CurrentSide, (int)icon.LockedSkaterPosition, icon.IsLockedIn, icon.IsDisabled,
                icon.IsKeyboardAssigned, icon.IsPlayerControlled, Painted(menu, slot), ForcedGlyphs(icon))));
        }

        return icons;
    }

    /// <summary>
    /// One step of the plan on its icon, with the game's own setters (the position with its property's own setter and the
    /// visual refresh: <c>SetLockedPosition</c> also records it in this PC's own remembered positions, which this PC's own
    /// screen restores later). Run inside the friend's guarded apply.
    /// </summary>
    public static void Run(ControllerAssignmentMenu menu, ControllerAssignmentPlayer icon, ControllerStep step)
    {
        switch (step.Kind)
        {
            case ControllerStepKind.Controls:
                // As the game's own controller change does: the flags, then the control visuals (they repaint its colour) and the state.
                icon.IsKeyboardAssigned = step.Keyboard;
                icon.IsPlayerControlled = step.Value != 0;
                icon.RefreshPlayerControlVisuals();
                icon.RefreshVisualState();
                break;
            case ControllerStepKind.Side:
                icon.SetSide((ControllerAssignmentPlayer.ESide)(int)step.Value, step.Tween);
                break;
            case ControllerStepKind.Position:
                icon.LockedSkaterPosition = (ESkaterPosition)(int)step.Value;
                icon.RefreshVisualState();
                break;
            case ControllerStepKind.Ready:
                icon.SetLockedIn(step.Value != 0);
                break;
            case ControllerStepKind.Off:
                icon.SetDisabled(step.Value != 0);
                break;
            case ControllerStepKind.Colour:
                SetPainted(menu, icon.GamePlayerId, step.Value);
                icon.RefreshPlayerColor(); // the game's own colour; the patch paints the host's over it
                break;
            case ControllerStepKind.Glyphs:
                Glyphs(icon, (int)step.Value);
                break;
        }
    }

    /// <summary>
    /// Paints an icon of a screen shown for the host in the host's colour (after the game's own <c>RefreshPlayerColor</c>
    /// painted this PC's): every graphic the game paints with the player's colour. False when it has none to paint.
    /// </summary>
    public static bool Paint(ControllerAssignmentPlayer icon)
    {
        if (HostColour(icon) is not { } colour)
        {
            return false;
        }

        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.UI.Graphic>? graphics = icon.m_PlayerColored;
        for (int i = 0; graphics != null && i < graphics.Length; i++)
        {
            UnityEngine.UI.Graphic? graphic = graphics[i];
            if (graphic != null)
            {
                graphic.color = colour;
            }
        }

        return true;
    }

    /// <summary>
    /// Plays a host feedback step on <paramref name="menu"/>'s same elements with the game's own call. False (with why) when
    /// they aren't there.
    /// </summary>
    public static bool PlayFeedback(ControllerAssignmentMenu menu, ControllerFeedbackStep step, out string problem)
    {
        problem = string.Empty;
        Il2CppSystem.Collections.Generic.IReadOnlyList<ControllerAssignmentFeedbackElement>? elements = step.Target switch
        {
            ControllerFeedback.GlobalTarget => menu.m_GlobalFeedbackElements is { } global ? new Il2CppSystem.Collections.Generic.IReadOnlyList<ControllerAssignmentFeedbackElement>(global.Pointer) : null,
            ControllerFeedback.TeamTarget => menu.m_CampaignTeam?.FeedbackElements,
            ControllerFeedback.HomeTeamTarget => menu.m_HomeTeam?.FeedbackElements,
            ControllerFeedback.VisitorTeamTarget => menu.m_VisitorTeam?.FeedbackElements,
            _ => IconOf(menu, step.Target)?.FeedbackElements,
        };
        if (elements == null)
        {
            problem = $"{TargetName(step.Target)} isn't on this PC's screen";
            return false;
        }

        menu.PlayFeedback_Internal(elements, (EControllerAssignmentFeedback)step.Type, step.Negative, (ControllerAssignmentFeedbackElement.EOptions)step.Options);
        return true;
    }

    /// <summary>The feedback's target in words ("P3's icon", "the screen", "the team", "the home team").</summary>
    public static string TargetName(int target) => target switch
    {
        ControllerFeedback.GlobalTarget => "the screen",
        ControllerFeedback.TeamTarget => "the team",
        ControllerFeedback.HomeTeamTarget => "the home team",
        ControllerFeedback.VisitorTeamTarget => "the visitor team",
        _ => $"P{target + 1}'s icon",
    };

    /// <summary>The icon of game player <paramref name="slot"/> in <paramref name="menu"/>, or null.</summary>
    private static ControllerAssignmentPlayer? IconOf(ControllerAssignmentMenu menu, int slot)
    {
        Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? players = menu.m_Players;
        for (int i = 0; players != null && i < players.Count; i++)
        {
            ControllerAssignmentPlayer? icon = players[i];
            if (icon != null && icon.GamePlayerId == slot)
            {
                return icon;
            }
        }

        return null;
    }

    /// <summary>Host: which elements a feedback played on (an icon's game player, the screen's, the team's), or -1.</summary>
    private static int FeedbackTarget(ControllerAssignmentMenu menu, IntPtr elements)
    {
        if (menu.m_GlobalFeedbackElements?.Pointer == elements)
        {
            return ControllerFeedback.GlobalTarget;
        }

        if (menu.m_CampaignTeam?.m_FeedbackElements?.Pointer == elements)
        {
            return ControllerFeedback.TeamTarget;
        }

        if (menu.m_HomeTeam?.m_FeedbackElements?.Pointer == elements)
        {
            return ControllerFeedback.HomeTeamTarget;
        }

        if (menu.m_VisitorTeam?.m_FeedbackElements?.Pointer == elements)
        {
            return ControllerFeedback.VisitorTeamTarget;
        }

        Il2CppSystem.Collections.Generic.List<ControllerAssignmentPlayer>? players = menu.m_Players;
        for (int i = 0; players != null && i < players.Count; i++)
        {
            ControllerAssignmentPlayer? icon = players[i];
            if (icon != null && icon.m_FeedbackElements?.Pointer == elements && icon.GamePlayerId is >= 0 and <= ControllerScreenState.MaxSlot)
            {
                return icon.GamePlayerId;
            }
        }

        return -1;
    }

    /// <summary>
    /// An icon's button glyphs drawn for <paramref name="device"/> (the device the host draws that player's with), or the
    /// game's own again (-1: the icon's own refresh, which this PC's prompt pin draws in this player's own device).
    /// </summary>
    private static void Glyphs(ControllerAssignmentPlayer icon, int device)
    {
        if (device < 0)
        {
            icon.RefreshInputSprites();
            return;
        }

        foreach (InputSpriteUI? sprite in new[] { icon.m_PositionLockInputSprite, icon.m_ChangeColorInputSprite, icon.m_ChangeJerseyColorInputSprite })
        {
            if (sprite != null)
            {
                // What the game's own InputSpriteUI.SetData does with a forced device, the action and player kept.
                sprite.m_ForceDeviceType = true;
                sprite.m_DeviceType = (EInputDeviceType)device;
                sprite.Refresh();
            }
        }

        ControllerGlyphViews.Drawn(icon, device); // its player's own button names over them, when the host sent them
    }

    /// <summary>The device an icon's glyphs are forced to (its first sprite's), or -1 for the game's own.</summary>
    private static int ForcedGlyphs(ControllerAssignmentPlayer icon)
    {
        InputSpriteUI? sprite = icon.m_PositionLockInputSprite ?? icon.m_ChangeColorInputSprite ?? icon.m_ChangeJerseyColorInputSprite;
        return sprite != null && sprite.m_ForceDeviceType ? (int)sprite.m_DeviceType : -1;
    }

    /// <summary>Host: the device the host's game draws <paramref name="slot"/>'s button glyphs with, or -1.</summary>
    private static int GlyphDevice(InputSpriteMapper? mapper, int slot)
    {
        try
        {
            int device = mapper != null && slot < mapper.MaxPlayers ? (int)mapper.GetCurrentDeviceType(slot) : -1;
            return device is >= 0 and <= ControllerScreenState.MaxDevice ? device : -1;
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: player {slot + 1}'s glyph device couldn't be read ({ex.GetType().Name}).");
            return -1;
        }
    }

    /// <summary>The name of the run's skater at a locked position ("" when none is locked, nobody's there, or it can't be read).</summary>
    private static string SkaterAt(Il2CppData.TeamData team, ESkaterPosition position)
    {
        try
        {
            Il2CppData.SkaterData? skater = position switch
            {
                ESkaterPosition.C => team.center,
                ESkaterPosition.LW => team.leftWinger,
                ESkaterPosition.RW => team.rightWinger,
                ESkaterPosition.LD => team.leftDefensemen,
                ESkaterPosition.RD => team.rightDefensemen,
                ESkaterPosition.Goalie => team.goalie,
                _ => null,
            };
            return skater?.FullName ?? string.Empty;
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: the skater at {position} couldn't be read ({ex.GetType().Name}).");
            return string.Empty;
        }
    }

    /// <summary>A controller screen player's colour (its marker's), as <see cref="MenuPayloads.Rgba"/>'s value; -1 when it can't be read.</summary>
    private static long Colour(ControllerAssignmentPlayer player)
    {
        try
        {
            return MenuPayloads.Rgba(player.GetPlayerColor().arrowColor);
        }
        catch (Exception ex)
        {
            ModLog.Trace($"Menus: a player's colour couldn't be read ({ex.GetType().Name}).");
            return -1;
        }
    }

    /// <summary>A locked skater position in words ("" when none is locked).</summary>
    private static string PositionName(ESkaterPosition position) => position switch
    {
        ESkaterPosition.C => "locked at centre",
        ESkaterPosition.LW => "locked at left wing",
        ESkaterPosition.RW => "locked at right wing",
        ESkaterPosition.LD => "locked at left defence",
        ESkaterPosition.RD => "locked at right defence",
        ESkaterPosition.Goalie => "locked in goal",
        _ => string.Empty,
    };

    private static void ReportOnce(string key, string message)
    {
        if (Reported.Add(key))
        {
            ModLog.Info(message);
        }
    }

    private static KeyValuePair<string, NetValue> Value(string key, NetValue value) => new(key, value);

    /// <summary>Host: a screen changed since it was last sent.</summary>
    private sealed class Dirty
    {
        public Dirty(ControllerAssignmentMenu menu) => Menu = menu;

        public ControllerAssignmentMenu Menu { get; }

        public bool Pending { get; set; }

        public double NotBefore { get; set; }

        public string What { get; set; } = string.Empty;
    }

    /// <summary>Friend: a screen shown for the host, with the host's newest state and the colours painted on it.</summary>
    private sealed class Mirrored
    {
        public Mirrored(ControllerAssignmentMenu menu) => Menu = menu;

        public ControllerAssignmentMenu Menu { get; }

        public ControllerScreenState? State { get; set; }

        public Dictionary<int, long> Painted { get; } = new();
    }
}
#endif
