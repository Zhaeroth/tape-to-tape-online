namespace T2TOnline.Core.Hosting;

/// <summary>
/// Whether a friend's player may be taken for the one controlling the host's menus when nobody is (D45; HostDecides in the
/// plugin). The game asks at every menu press of a player that isn't on a keyboard (<c>UIInputSource.SetControllerIfNone</c>,
/// from both <c>IsControlledBy</c>): with nobody on its controlling list it hands that player the menus and answers yes,
/// whoever ended up with them. The mod hands them to the host's own player instead. A host's pad player then goes on the
/// list, so the next ask finds somebody there and the friend is refused by the game itself. A host's keyboard player never
/// goes on the list (the game keeps keyboard players off it: they always control), so the list stays empty and the game
/// would answer yes to the friend at every press. That ask is refused here. The game asks only where one player has the
/// menus: while a menu any player drives is up (the pause menu, the map's own menu, the lineup, Settings) it takes every
/// player's press without asking, as on a couch, and nothing is refused.
/// </summary>
public static class FriendControlAsk
{
    /// <summary>
    /// True when the ask is answered no without running the game's own: this PC is <paramref name="hosting"/>, the player
    /// asked about is a friend's (<paramref name="friendsPlayer"/>), and the host's own player (<paramref name="host"/>) is
    /// on its keyboard, with no pad player of its own. Otherwise the game's own answer stands: outside hosting, for the
    /// host's own players, with a host's pad player to hand the menus to, and with no host player at all (the game then
    /// leaves the menus with whoever asked, as it would on a couch).
    /// </summary>
    public static bool Refused(bool hosting, bool friendsPlayer, HostPlayerChoice host) =>
        hosting && friendsPlayer && host.Chosen >= 0 && !host.HasPad;
}
