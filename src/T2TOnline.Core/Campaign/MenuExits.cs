using System;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The game's menu exits that show a transition: six menus' own exits show one of the game's three "Woosh" transitions
/// (<see cref="MenuTransition"/>), from what the menu holds when it closes; a friend's copy of the menu, closed with the base
/// close, shows the same one in the same order. Game-independent.
/// </summary>
public static class MenuExits
{
    /// <summary>The squad screen (its exit: on to the run start, a General Manager run's load, or back to the title).</summary>
    public const string ChooseMeta = "ChooseMetaMenu";

    /// <summary>The lineup with benchwarmers (its exit: the run's first load, or back).</summary>
    public const string Benchwarmer = "BenchwarmerMenu";

    /// <summary>The old squad menus (the superstar pick, the old school draft).</summary>
    public const string OldSquad = "OldSquadMenu";

    /// <summary>The new-unlocks menu (its exit shows the loading transition first, unless told to skip it).</summary>
    public const string PermanentUnlock = "PermanentUnlockMenu";

    /// <summary>The run-end menu (at the run's end, its exit shows a transition first).</summary>
    public const string RunEnd = "RunEndMenu";

    /// <summary>
    /// The controller screen (Play Now's exit towards the match shows the loading transition first: <c>FinishExitMenu</c>'s
    /// <c>ShowLoading</c>; a back-out or the campaign's shows none, and so nothing was shown since it opened).
    /// </summary>
    public const string ControllerScreen = "ControllerAssignmentMenu";

    /// <summary>The old squad menu's superstar pick mode (<c>OldSquadMenu.Mode</c> 1); any other mode is the old school draft.</summary>
    public const int SuperStarOnlyMode = 1;

    /// <summary>True for a menu class whose own exit can show a transition.</summary>
    public static bool ShowsTransition(string type) => type is ChooseMeta or Benchwarmer or OldSquad or PermanentUnlock or RunEnd or ControllerScreen;

    /// <summary>
    /// True for a menu class whose exit shows its transition before the menu hides (it waits for the transition's show,
    /// then hides behind it); the others hide first, then show it.
    /// </summary>
    public static bool TransitionFirst(string type) => type is PermanentUnlock or RunEnd or ControllerScreen;

    /// <summary>
    /// The squad screen's transition (its <c>Hide</c>): back to the title when it wasn't confirmed; else a load when
    /// <paramref name="loadsRun"/> (the campaign tutorial is on, or the squad is the General Manager's), else on.
    /// </summary>
    public static MenuTransition ForChooseMeta(bool confirmed, bool loadsRun) =>
        !confirmed ? MenuTransition.Regress : loadsRun ? MenuTransition.Loading : MenuTransition.Progress;

    /// <summary>The lineup's transition: the run's load when it was confirmed, else back.</summary>
    public static MenuTransition ForBenchwarmer(bool confirmed) => confirmed ? MenuTransition.Loading : MenuTransition.Regress;

    /// <summary>The old squad menu's transition: on for the superstar pick, a load for the old school draft.</summary>
    public static MenuTransition ForOldSquad(int mode) => mode == SuperStarOnlyMode ? MenuTransition.Progress : MenuTransition.Loading;

    /// <summary>The new-unlocks menu's transition: a load unless it was told to skip it.</summary>
    public static MenuTransition ForPermanentUnlock(bool skipTransition) => skipTransition ? MenuTransition.None : MenuTransition.Loading;

    /// <summary>
    /// The run-end menu's transition: none unless it plays one (at the run's end); then on to the new unlocks when there
    /// are some to show, else a load.
    /// </summary>
    public static MenuTransition ForRunEnd(bool playsTransition, bool visibleUnlocks) =>
        !playsTransition ? MenuTransition.None : visibleUnlocks ? MenuTransition.Progress : MenuTransition.Loading;

    /// <summary>The game's name of a transition ("WooshLoading"), for logs.</summary>
    public static string Name(MenuTransition transition) => transition switch
    {
        MenuTransition.Progress => "WooshProgress",
        MenuTransition.Loading => "WooshLoading",
        MenuTransition.Regress => "WooshRegress",
        _ => "no transition",
    };
}

/// <summary>
/// The guard on one transition a friend's views showed. The loading transition is taken down by the game's own code on the
/// friend's PC too (its loading screen, the map's appear, the run-end menu's show): if it's still up once this PC's screen has
/// settled for <see cref="StraySeconds"/>, it's a stray. The on and back transitions are taken down on the host by
/// <c>TitleScreen.StartNewRun</c> (and two menus' shows), which doesn't run on a friend: each goes as soon as its show is
/// complete and this PC's screen has settled for <see cref="SettleSeconds"/> (the next host screen, when one binds, sooner: the
/// caller). Any goes after <see cref="MaxSeconds"/> up while nothing loads, and after <see cref="MaxLoadingSeconds"/> up whatever
/// happens (a load that never ends must not leave the screen covered). Pure logic; not thread-safe (one game thread).
/// </summary>
public sealed class StrayTransition
{
    /// <summary>How long the loading transition may stay up once this PC's screen has settled.</summary>
    public const double StraySeconds = 4;

    /// <summary>How long a transition may stay up while nothing loads, settled or not.</summary>
    public const double MaxSeconds = 12;

    /// <summary>How long a transition may stay up whatever this PC says it's doing.</summary>
    public const double MaxLoadingSeconds = 90;

    private readonly double _settleSeconds;
    private double _settledSince = double.NaN;
    private double _idleFor;
    private double _last;

    /// <summary>A transition shown at <paramref name="shownAt"/> (seconds), allowed <paramref name="settleSeconds"/> once the screen has settled.</summary>
    public StrayTransition(double shownAt, double settleSeconds = StraySeconds)
    {
        ShownAt = shownAt;
        _last = shownAt;
        _settleSeconds = settleSeconds;
    }

    /// <summary>When it showed.</summary>
    public double ShownAt { get; }

    /// <summary>True once it's due to go (it stays so).</summary>
    public bool Due { get; private set; }

    /// <summary>How long the screen had been settled when it became due (-1 when it went by a limit instead).</summary>
    public double SettledFor { get; private set; } = -1;

    /// <summary>
    /// How long a transition of <paramref name="kind"/> stays once its show is complete and this PC's screen has settled: a
    /// way back has nothing to wait for; a way on gives the host's next menu a moment to reach this PC (it takes the
    /// transition down when it opens); the loading one is the game's own to take down.
    /// </summary>
    public static double SettleSeconds(MenuTransition kind) => kind switch
    {
        MenuTransition.Regress => 0,
        MenuTransition.Progress => 1,
        _ => StraySeconds,
    };

    /// <summary>
    /// One look at <paramref name="now"/>: <paramref name="loading"/> while this PC loads a scene (time spent loading counts
    /// only against <see cref="MaxLoadingSeconds"/>), <paramref name="settled"/> while its screen is settled (no load, its
    /// scene ready, no menu opening). True when it's due.
    /// </summary>
    public bool Watch(double now, bool loading, bool settled)
    {
        if (Due)
        {
            return true;
        }

        double step = Math.Max(0, now - _last);
        _last = now;
        if (now - ShownAt >= MaxLoadingSeconds)
        {
            Due = true;
            return true;
        }

        if (loading)
        {
            _settledSince = double.NaN;
            return false;
        }

        _idleFor += step;
        if (!settled)
        {
            _settledSince = double.NaN;
        }
        else if (double.IsNaN(_settledSince))
        {
            _settledSince = now;
        }

        if (!double.IsNaN(_settledSince) && now - _settledSince >= _settleSeconds)
        {
            SettledFor = now - _settledSince;
            Due = true;
        }
        else if (_idleFor >= MaxSeconds)
        {
            Due = true;
        }

        return Due;
    }
}

/// <summary>
/// The reward menus' fade to black on a friend. The host's reward menu fades to black, then hides
/// (<c>RelicRewardMenu.&lt;Hide&gt;d__61</c> unless it's a match's forced pick, <c>SkaterPowerupRewardMenu.&lt;Hide&gt;d__92</c>)
/// when the reward is a post-game one; the next reward menu fades back in once it's up, and after the last the scene's load
/// takes the cover with it. A friend's copy does the same with the game's own cover, and never stays black for long: the
/// cover goes back as soon as the host's next screen is up here, this PC's scene changed, or after
/// <see cref="MaxBlackSeconds"/> with no load going on. The host's first reward menu after a match fades to black before it
/// opens and back once it's up (<c>RogueRewardsHandler.&lt;DisplayRelicRewardMenu&gt;d__24</c>): a friend's cover follows the
/// host's by a HUD value and never holds its menu back; it only draws over what's there (<see cref="BeforeMenuGoesBack"/>).
/// Pure logic.
/// </summary>
public static class RewardFadeRules
{
    /// <summary>The game's fade (<c>RogueRewardsHandler.FADE_DURATION</c>, 0.3 s: read in the game's code).</summary>
    public const double FadeSeconds = 0.3;

    /// <summary>How long the cover may stay up here with no next screen and no load (a host's scene load can take this long).</summary>
    public const double MaxBlackSeconds = 20;

    /// <summary>
    /// The longest the cover put up before a reward menu stays with no next screen (a slower host's menu then opens over the
    /// arena here, as it did before this fade was mirrored).
    /// </summary>
    public const double MaxBeforeMenuSeconds = 5;

    /// <summary>How long after the host's own menu took its cover away this PC's cover waits for its copy of that menu.</summary>
    public const double AfterHostMenuSeconds = 1;

    /// <summary>The menu data's flag: the host's reward is a post-game one (<c>RogueRewardsHandler.IsPostGameReward</c>).</summary>
    public const string PostGameKey = "postGame";

    /// <summary>True for a reward menu whose close fades to black on the host (the relic menu unless it's a match's forced pick; the power-up menu always; both only post-game).</summary>
    public static bool FadesOnClose(bool relicMenu, bool powerUpMenu, bool forcedMatchPick, bool postGame) =>
        postGame && (powerUpMenu || (relicMenu && !forcedMatchPick));

    /// <summary>True when a cover up for <paramref name="blackFor"/> seconds goes back now.</summary>
    public static bool GoesBack(bool nextScreen, bool sceneChanged, bool loading, double blackFor) =>
        nextScreen || sceneChanged || (!loading && blackFor >= MaxBlackSeconds);

    /// <summary>
    /// True when a cover put up before a reward menu (the host's first after a match) goes back now: its next screen is up
    /// here, this PC's scene changed, the host's own menu has been up for <see cref="AfterHostMenuSeconds"/>
    /// (<paramref name="hostMenuUpFor"/>; negative while it isn't), or it has been up <see cref="MaxBeforeMenuSeconds"/>
    /// whatever else happens: nothing this PC is waiting for may keep it black, a load included.
    /// </summary>
    public static bool BeforeMenuGoesBack(bool nextScreen, bool sceneChanged, double blackFor, double hostMenuUpFor) =>
        nextScreen || sceneChanged || hostMenuUpFor >= AfterHostMenuSeconds || blackFor >= MaxBeforeMenuSeconds;
}
