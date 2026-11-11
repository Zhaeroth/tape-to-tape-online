#if HAS_INTEROP
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The reward menus' fade to black on a friend, with the game's own cover
/// (<c>RogueRewardsHandler.FadeCover</c>, what <c>FadeToBlackAfterGame</c> calls). The game's own call does
/// nothing on a friend: its <c>IsPostGameReward</c> reads <c>GameManager.isGameEnded</c>, which only
/// <c>GameManager.EndOfGame</c> sets, a listener client mode detaches (not in presentation.txt), so the host's data says
/// whether the reward is a post-game one. The cover sits above every menu (its canvas's sorting order is 29000) and
/// nothing on a friend fades it back (the host's <c>DisplayRelicRewardMenu</c> does, after its next menu's show):
/// it's put back here with the game's own <c>FadeInPendingRewardMenu</c> as soon as the host's next screen is up,
/// or (<see cref="RewardFadeRules.GoesBack"/>) this PC's scene changed, nothing came, or following stopped. A scene's load takes
/// the cover with it (it's made in the handler's scene).
/// <para>
/// The fade before the host's first reward menu after a match (<c>FadeToBlackBeforeRewardMenu</c>, faded
/// back after the menu's show): the same cover goes up when the host's HUD says the host's did
/// (<see cref="BeginBeforeMenu"/>). Nothing waits for it: a friend's copy opens when it opens, under the cover, and the cover
/// goes back by <see cref="RewardFadeRules.BeforeMenuGoesBack"/>, so it's up for seconds at most on any path.
/// </para>
/// Game thread only; the callers guard against exceptions.
/// </summary>
internal static class RewardFade
{
    private static RogueRewardsHandler? _handler;
    private static double _since;
    private static int _scene;
    private static bool _beforeMenu;
    private static double _hostMenuAt = double.NaN;

    /// <summary>True while a cover put up here hasn't been put back.</summary>
    public static bool Up => _handler != null;

    /// <summary>True while the cover up is the one before a reward menu (its fade back goes with the menu's first frames: looked at every frame).</summary>
    public static bool BeforeMenu => _beforeMenu;

    /// <summary>The host's menu whose close put it up: a newer menu's screen is the next screen.</summary>
    public static uint Serial { get; private set; }

    /// <summary>
    /// The host's reward menu <paramref name="serial"/> closed behind its fade: the game's cover fades in here. False when this
    /// PC's scene has no reward handler (nothing to fade with: the copy just closes).
    /// </summary>
    public static bool Begin(uint serial)
    {
        RogueRewardsHandler? handler = RogueRewardsHandler.s_Instance;
        if (handler == null)
        {
            return false;
        }

        _ = handler.FadeCover(1f);
        handler.m_FadeInPending = true; // what the game's own fade back looks at
        _handler = handler;
        Serial = serial;
        _since = FrameClock.Seconds;
        _scene = WorldMap.SceneHandle;
        _beforeMenu = false;
        _hostMenuAt = double.NaN;
        return true;
    }

    /// <summary>
    /// The host's cover started going black before a reward menu; the newest of the host's menus here is
    /// <paramref name="serial"/>: the game's cover fades in here until the next one is up (the same rules, tighter). False when
    /// this PC's scene has no reward handler (the menu just opens, as it did before).
    /// </summary>
    public static bool BeginBeforeMenu(uint serial)
    {
        bool began = Begin(serial);
        _beforeMenu = began;
        return began;
    }

    /// <summary>The host's own menu is up (its cover came off): this PC's, put up before its menu, goes soon even if its copy isn't up.</summary>
    public static void HostMenuUp()
    {
        if (_beforeMenu && double.IsNaN(_hostMenuAt))
        {
            _hostMenuAt = FrameClock.Seconds;
        }
    }

    /// <summary>
    /// A look while the cover is up: goes back when the rules say so. Returns why it went, or "" (still up, or gone with its
    /// scene). <paramref name="nextScreen"/>: the host's next screen is up here.
    /// </summary>
    public static string Tick(bool nextScreen)
    {
        RogueRewardsHandler? handler = _handler;
        if (handler == null)
        {
            return string.Empty;
        }

        if (handler.WasCollected || handler == null)
        {
            _handler = null; // gone with its scene, and the cover with it
            return string.Empty;
        }

        bool scene = WorldMap.SceneHandle != _scene;
        double black = FrameClock.Seconds - _since;
        double hostMenu = double.IsNaN(_hostMenuAt) ? -1 : FrameClock.Seconds - _hostMenuAt;
        if (_beforeMenu ? !RewardFadeRules.BeforeMenuGoesBack(nextScreen, scene, black, hostMenu) : !RewardFadeRules.GoesBack(nextScreen, scene, WorldMap.Loading, black))
        {
            return string.Empty;
        }

        bool before = _beforeMenu;
        End();
        return nextScreen ? "the host's next screen is up here"
            : scene ? "this PC's scene changed"
            : !before ? $"nothing followed within {RewardFadeRules.MaxBlackSeconds:0} s"
            : hostMenu >= 0 ? $"the host's menu is up and this PC's copy isn't, {RewardFadeRules.AfterHostMenuSeconds:0} s on"
            : $"the host's menu didn't come within {RewardFadeRules.MaxBeforeMenuSeconds:0} s";
    }

    /// <summary>Puts the cover back with the game's own fade (following stopped, or <see cref="Tick"/>).</summary>
    public static void End()
    {
        RogueRewardsHandler? handler = _handler;
        _handler = null;
        _beforeMenu = false;
        _hostMenuAt = double.NaN;
        if (handler != null && !handler.WasCollected)
        {
            handler.FadeInPendingRewardMenu();
        }
    }

    /// <summary>The game is closing: no game calls.</summary>
    public static void Forget()
    {
        _handler = null;
        _beforeMenu = false;
        _hostMenuAt = double.NaN;
    }
}
#endif
