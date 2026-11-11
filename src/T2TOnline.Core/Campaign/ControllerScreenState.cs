using System.Collections.Generic;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The host's controller screen as a friend's real screen takes it: the menu's settings (its mode, lineup lock, onboarding,
/// both teams ready; on an exhibition's, its game mode, teams and rules page) and every icon's state, read from
/// the host's menu data (<see cref="ControllerScreenRow"/> version 2). The friend's game builds the same icons itself (one per
/// game player, the same on every PC) and puts this state into them with the game's own setters
/// (<see cref="ControllerScreenPlan"/>). Read only from data a friend got: <see cref="TryRead"/> never throws and refuses
/// anything out of the game's ranges.
/// </summary>
public sealed class ControllerScreenState
{
    /// <summary>Value: the menu's mode (<c>ControllerAssignmentMenu.EMode</c>: team selection 0, side selection 1, campaign 2).</summary>
    public const string ModeKey = "ca.mode";

    /// <summary>Value: the menu's lineup is locked (flag).</summary>
    public const string LockedKey = "ca.locked";

    /// <summary>Value: the menu is the onboarding one (flag).</summary>
    public const string OnboardingKey = "ca.onboard";

    /// <summary>Value: every player is ready (the game's <c>AreBothTeamsReady</c>, flag).</summary>
    public const string ReadyKey = "ca.ready";

    /// <summary>Value: the exhibition's game mode the screen was opened for (<c>GameMode</c>: normal 0, shootout 2).</summary>
    public const string GameModeKey = "ca.gameMode";

    /// <summary>Value: the exhibition's game type the screen was opened for (<c>GameType</c>: quick game 0).</summary>
    public const string GameTypeKey = "ca.gameType";

    /// <summary>Value: the screen has its rules page (<c>m_DisplayGameSettings</c>: Play Now's, flag).</summary>
    public const string RulesPageKey = "ca.rulesPage";

    /// <summary>Value: the host's screen is on its rules page now (<c>m_IsInGameSettings</c>, flag).</summary>
    public const string InRulesKey = "ca.inRules";

    /// <summary>Value: each rule's choice on the rules page (<see cref="ControllerRules"/>), when its panel is loaded.</summary>
    public const string RulesKey = "ca.rules";

    /// <summary>Value: the players on the home team when the rules page opened (its skater count's choices depend on it).</summary>
    public const string HomePlayersKey = "ca.homeN";

    /// <summary>Value: the players on the visitor team when the rules page opened.</summary>
    public const string VisitorPlayersKey = "ca.visitorN";

    /// <summary>Value: the home team's id (<c>TeamData.id</c>, the right side).</summary>
    public const string HomeTeamKey = "ca.home";

    /// <summary>Value: the visitor team's id (the left side).</summary>
    public const string VisitorTeamKey = "ca.visitor";

    /// <summary>Value: the home team's name, as the host's game shows it.</summary>
    public const string HomeNameKey = "ca.homeName";

    /// <summary>Value: the visitor team's name, as the host's game shows it.</summary>
    public const string VisitorNameKey = "ca.visitorName";

    /// <summary>Value: the home team's tab (<c>TeamSelectionScreen.TeamSource</c>: campaign 0, league 1, custom 2, all 3).</summary>
    public const string HomeTabKey = "ca.homeTab";

    /// <summary>Value: the visitor team's tab.</summary>
    public const string VisitorTabKey = "ca.visitorTab";

    /// <summary>Value: the home side wears its home colours (<c>IsColorHome</c>, flag).</summary>
    public const string HomeColorKey = "ca.homeColor";

    /// <summary>Value: the visitor side wears its home colours (flag; a jersey swap turns it on).</summary>
    public const string VisitorColorKey = "ca.visitorColor";

    /// <summary>Team selection, Play Now's mode: both teams can be changed.</summary>
    public const int TeamSelectionMode = 0;

    /// <summary>Side selection, an exhibition's pause menu: the match's two teams, no team changes.</summary>
    public const int SideSelectionMode = 1;

    /// <summary>The campaign's one-side mode.</summary>
    public const int CampaignMode = 2;

    /// <summary>The highest mode.</summary>
    public const int MaxMode = 2;

    /// <summary>The highest team tab (<c>TeamSource.All</c>).</summary>
    public const int MaxTab = 3;

    /// <summary>The most players a side can have (the exhibition's prefab: six a side).</summary>
    public const int MaxSidePlayers = MaxIcons;

    /// <summary>Longest team id or name kept here (a longer one reads as unknown: that side shows nothing new).</summary>
    public const int MaxTeamChars = 96;

    /// <summary>The highest game mode or game type read (the game's enums are small; anything above isn't one of the game's).</summary>
    public const int MaxGameNumber = 15;

    /// <summary>Most icons (the game makes ten: five players a side).</summary>
    public const int MaxIcons = 16;

    /// <summary>The highest game player (slot).</summary>
    public const int MaxSlot = 15;

    /// <summary>The highest side (right).</summary>
    public const int MaxSide = 2;

    /// <summary>The centre side: a player there doesn't play (campaign), and the game clears its lock when it moves there.</summary>
    public const int CentreSide = 1;

    /// <summary>The highest locked position, which is also "none" (<c>ESkaterPosition.Invalid</c>).</summary>
    public const int NoPosition = 6;

    /// <summary>
    /// The highest prompt device kept: the game's <c>EInputDeviceType</c>, MouseKeyboard 0 to Other 5 (for which the game
    /// draws no glyphs, as the host's screen then shows none; <see cref="Input.PromptPinState"/>). Anything above isn't one
    /// of the game's and reads as unknown (that icon keeps the game's own glyphs), so a value the game has no device for
    /// never reaches its glyph code.
    /// </summary>
    public const int MaxDevice = Input.PromptPinState.Other;

    private ControllerScreenState(int mode, bool lineupLocked, bool onboarding, bool bothReady, IReadOnlyList<ControllerIcon> icons)
    {
        Mode = mode;
        LineupLocked = lineupLocked;
        Onboarding = onboarding;
        BothReady = bothReady;
        Icons = icons;
    }

    /// <summary>The menu's mode (<see cref="CampaignMode"/> in a run).</summary>
    public int Mode { get; }

    /// <summary>The menu's lineup is locked.</summary>
    public bool LineupLocked { get; }

    /// <summary>The menu is the onboarding one.</summary>
    public bool Onboarding { get; }

    /// <summary>Every player is ready on the host's screen.</summary>
    public bool BothReady { get; }

    /// <summary>Every icon of the host's screen, in its order (slots unique).</summary>
    public IReadOnlyList<ControllerIcon> Icons { get; }

    /// <summary>The exhibition's game mode the screen was opened for (0 when it doesn't say: the campaign's).</summary>
    public int GameMode { get; private init; }

    /// <summary>The exhibition's game type the screen was opened for (0 when it doesn't say).</summary>
    public int GameType { get; private init; }

    /// <summary>The screen has its rules page.</summary>
    public bool RulesPage { get; private init; }

    /// <summary>The host's screen is on its rules page now.</summary>
    public bool InRules { get; private init; }

    /// <summary>Each rule's choice, in <see cref="ControllerRules"/>' order, or empty when the host's panel isn't loaded.</summary>
    public IReadOnlyList<int> Rules { get; private init; } = System.Array.Empty<int>();

    /// <summary>The players on the home team when the rules page opened (-1 when it doesn't say).</summary>
    public int HomePlayers { get; private init; } = -1;

    /// <summary>The players on the visitor team when the rules page opened (-1 when it doesn't say).</summary>
    public int VisitorPlayers { get; private init; } = -1;

    /// <summary>The home team as the host's screen shows it (no id: the screen shows no team of its own, or it doesn't say).</summary>
    public ControllerTeam HomeTeam { get; private init; }

    /// <summary>The visitor team as the host's screen shows it.</summary>
    public ControllerTeam VisitorTeam { get; private init; }

    /// <summary>Which of the game's controller screens it is, and so what a friend's game does with it.</summary>
    public ControllerScreenKind Kind =>
        Onboarding ? ControllerScreenKind.Tutorial
        : Mode == CampaignMode ? ControllerScreenKind.Campaign
        : Mode == SideSelectionMode ? ControllerScreenKind.InMatch
        : GameMode == 0 && GameType == 0 ? ControllerScreenKind.PlayNow
        : ControllerScreenKind.NotStreamed;

    /// <summary>True when the host's screen has an icon that isn't disabled (its first read, before its icons are made, has none).</summary>
    public bool HasIcons
    {
        get
        {
            foreach (ControllerIcon icon in Icons)
            {
                if (!icon.Off)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The icon of game player <paramref name="slot"/>, or false.</summary>
    public bool TryGet(int slot, out ControllerIcon icon)
    {
        foreach (ControllerIcon each in Icons)
        {
            if (each.Slot == slot)
            {
                icon = each;
                return true;
            }
        }

        icon = default;
        return false;
    }

    /// <summary>
    /// Reads the host's controller screen from its menu data. False, with why, when it isn't one this version reads (no
    /// mode: an older host's or another menu's data), when a row lacks its numbers (an older host's row) or anything is
    /// out of the game's ranges (a slot twice, too many icons). Never throws.
    /// </summary>
    public static bool TryRead(MenuData data, out ControllerScreenState? state, out string problem)
    {
        state = null;
        problem = string.Empty;
        if (data == null)
        {
            problem = "no data";
            return false;
        }

        long mode = data.Whole(ModeKey, -1);
        if (mode is < 0 or > MaxMode)
        {
            problem = mode == -1 ? "the host's data has no controller screen settings (an older host)" : $"the host's controller screen mode {mode} is unknown here";
            return false;
        }

        var icons = new List<ControllerIcon>();
        var slots = new HashSet<int>();
        foreach (MenuItem row in data.Group(ControllerScreenRow.Group))
        {
            if (icons.Count >= MaxIcons)
            {
                problem = $"the host's controller screen has more than {MaxIcons} players";
                return false;
            }

            if (!TryReadIcon(row, out ControllerIcon icon, out problem))
            {
                return false;
            }

            if (!slots.Add(icon.Slot))
            {
                problem = $"the host's controller screen has player {icon.Slot + 1} twice";
                return false;
            }

            icons.Add(icon);
        }

        long gameMode = data.Whole(GameModeKey);
        long gameType = data.Whole(GameTypeKey);
        if (gameMode is < 0 or > MaxGameNumber || gameType is < 0 or > MaxGameNumber)
        {
            problem = $"the host's controller screen is for game mode {gameMode}, game type {gameType}, out of the game's ranges";
            return false;
        }

        state = new ControllerScreenState((int)mode, data.Flag(LockedKey), data.Flag(OnboardingKey), data.Flag(ReadyKey), icons)
        {
            GameMode = (int)gameMode,
            GameType = (int)gameType,
            RulesPage = data.Flag(RulesPageKey),
            InRules = data.Flag(InRulesKey),
            Rules = ControllerRules.TryParse(data.Text(RulesKey), out IReadOnlyList<int> rules) ? rules : System.Array.Empty<int>(),
            HomePlayers = Players(data, HomePlayersKey),
            VisitorPlayers = Players(data, VisitorPlayersKey),
            HomeTeam = Team(data, HomeTeamKey, HomeNameKey, HomeTabKey, HomeColorKey),
            VisitorTeam = Team(data, VisitorTeamKey, VisitorNameKey, VisitorTabKey, VisitorColorKey),
        };
        return true;
    }

    private static int Players(MenuData data, string key)
    {
        long players = data.Whole(key, -1);
        return players is >= 0 and <= MaxSidePlayers ? (int)players : -1;
    }

    /// <summary>A side's team: one out of range reads as no team, and that side shows nothing new (a bad value never reaches the game).</summary>
    private static ControllerTeam Team(MenuData data, string id, string name, string tab, string color)
    {
        string team = data.Text(id);
        long place = data.Whole(tab, -1);
        return team.Length is 0 or > MaxTeamChars || place is < 0 or > MaxTab
            ? default
            : new ControllerTeam(team, data.Text(name) is { Length: <= MaxTeamChars } shown ? shown : string.Empty, (int)place, data.Flag(color));
    }

    private static bool TryReadIcon(MenuItem row, out ControllerIcon icon, out string problem)
    {
        icon = default;
        problem = string.Empty;
        long slot = row.Whole(ControllerScreenRow.SlotKey, -1);
        if (slot is < 0 or > MaxSlot)
        {
            problem = "a controller screen row has no player number in range";
            return false;
        }

        if (!row.TryGet(ControllerScreenRow.SideKey, out _))
        {
            problem = "the host's controller screen rows are an older version's (no numbers)";
            return false;
        }

        long side = row.Whole(ControllerScreenRow.SideKey, -1);
        long position = row.Whole(ControllerScreenRow.PositionKey, -1);
        long colour = row.Whole(ControllerScreenRow.ColourKey, -1);
        long device = row.Whole(ControllerScreenRow.DeviceKey, -1);
        if (side is < 0 or > MaxSide || position is < 0 or > NoPosition || colour is < -1 or > uint.MaxValue)
        {
            problem = $"player {slot + 1}'s row on the host's controller screen is out of the game's ranges";
            return false;
        }

        icon = new ControllerIcon(
            (int)slot,
            (int)side,
            (int)position,
            row.Flag(ControllerScreenRow.ReadyKey),
            row.Flag(ControllerScreenRow.OffKey),
            row.Flag(ControllerScreenRow.KeyboardKey),
            !row.Flag(ControllerScreenRow.AiKey),
            colour,
            device is >= 0 and <= MaxDevice ? (int)device : -1,
            unchecked((ulong)row.Whole(ControllerScreenRow.SteamKey)));
        return true;
    }
}

/// <summary>Which of the game's controller screens the host's is.</summary>
public enum ControllerScreenKind
{
    /// <summary>The campaign's (the map's menu, the pause menu, a host pad's disconnect): a friend needs the host's run.</summary>
    Campaign,

    /// <summary>Play Now's, at the title: both teams, the sides, then the rules (<c>GameStartMenusHandler.LaunchGame</c>).</summary>
    PlayNow,

    /// <summary>An exhibition's, from its pause menu or a host pad's disconnect: the match's two teams, while a friend shows the match.</summary>
    InMatch,

    /// <summary>The tutorial's: its speech waits for a press, and a tutorial's match isn't streamed.</summary>
    Tutorial,

    /// <summary>A mode the mod doesn't stream (a shootout's, a tournament's): a friend's game has no match to go on to.</summary>
    NotStreamed,
}

/// <summary>A side's team on the host's controller screen.</summary>
/// <param name="Id">Its id (<c>TeamData.id</c>; empty: the host's screen shows none, or didn't say).</param>
/// <param name="Name">Its name as the host's game shows it (a friend shows it while a custom team of the host's isn't here yet).</param>
/// <param name="Tab">The team tab it's on (<c>TeamSource</c>).</param>
/// <param name="ColorHome">It wears its home colours (a jersey swap changes this).</param>
public readonly record struct ControllerTeam(string Id, string Name, int Tab, bool ColorHome)
{
    /// <summary>True when the host's screen said which team it is.</summary>
    public bool Known => !string.IsNullOrEmpty(Id);
}

/// <summary>One icon of the host's controller screen, as the host's game shows it.</summary>
/// <param name="Slot">The game player.</param>
/// <param name="Side">Its side (left 0, centre 1, right 2).</param>
/// <param name="Position">Its locked position (0 to 6; 6 is none).</param>
/// <param name="Ready">Locked in.</param>
/// <param name="Off">Disabled.</param>
/// <param name="Keyboard">The game's keyboard flag (its keyboard look).</param>
/// <param name="Controlled">Someone controls it (false: the AI plays it).</param>
/// <param name="Colour">Its colour as one RGBA value, or -1.</param>
/// <param name="Device">The prompt device the host draws its glyphs with, or -1.</param>
/// <param name="Steam">The friend whose controller it is (0 for the host's own).</param>
public readonly record struct ControllerIcon(int Slot, int Side, int Position, bool Ready, bool Off, bool Keyboard, bool Controlled, long Colour, int Device, ulong Steam);
