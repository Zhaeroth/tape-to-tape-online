namespace T2TOnline.Core.Lobby;

/// <summary>
/// What the session holds back while the developer's check tour runs, so a tour on a player's own PC never reaches
/// another person: its lobby is private, the session shows no rich presence, its host lets in only the tour's one-PC
/// stand-in (this same account), and nothing joins another lobby but the tour's own lobby that isn't there.
/// </summary>
public static class UiCheckHolds
{
    /// <summary>
    /// What the host session's <c>AdmitOnly</c> is: 0 (everyone the other rules let in) with no tour; with one, only
    /// <paramref name="self"/>, and nobody at all when this account isn't known (no sender has the id
    /// <see cref="ulong.MaxValue"/>).
    /// </summary>
    public static ulong AdmitOnly(bool tourRunning, ulong self) => !tourRunning ? 0 : self != 0 ? self : ulong.MaxValue;

    /// <summary>True when a join of <paramref name="lobby"/> is refused: during a tour, every lobby but <paramref name="toursOwnLobby"/> (a lobby id with no lobby behind it, for the tour's failure step).</summary>
    public static bool RefusesJoin(bool tourRunning, ulong lobby, ulong toursOwnLobby) => tourRunning && (lobby != toursOwnLobby || toursOwnLobby == 0);

    /// <summary>True when the session shows no rich presence (no join key, no status): while a tour runs.</summary>
    public static bool HoldsPresence(bool tourRunning) => tourRunning;

    /// <summary>True when a lobby made now is private (invite only, shown to no friend): while a tour runs.</summary>
    public static bool PrivateLobby(bool tourRunning) => tourRunning;
}
