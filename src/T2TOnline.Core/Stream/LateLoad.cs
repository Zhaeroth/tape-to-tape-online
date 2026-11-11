namespace T2TOnline.Core.Stream;

/// <summary>
/// A friend's session ends while their game loads a host's match: whether a match of the host's is still to be left for
/// the main menu once it is in. The game's loading screen can't be stopped, so a load it has started brings the match in
/// after the session; a load that only waited (for the host's run or custom content) started no scene and brings nothing
/// in, so the next match to load is the player's own and must stay. Game-independent.
/// </summary>
public static class LateLoad
{
    /// <summary>
    /// True when the host's match must be left once it's in.
    /// </summary>
    /// <param name="backToMenu">The session's end takes this game back to its main menu (false as the game closes).</param>
    /// <param name="sceneLoadStarted">The game's loading screen is bringing the host's match in now.</param>
    /// <param name="matchCameIn">
    /// A match has come in since the load began, which the session's own steps hadn't taken up yet (the load's last
    /// frame): it is the host's, in already.
    /// </param>
    public static bool LeaveWhenIn(bool backToMenu, bool sceneLoadStarted, bool matchCameIn) => backToMenu && (sceneLoadStarted || matchCameIn);
}
