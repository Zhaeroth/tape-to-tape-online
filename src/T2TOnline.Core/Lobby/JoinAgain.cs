namespace T2TOnline.Core.Lobby;

/// <summary>What the Join list does about a join asked for again (<see cref="JoinAgain.Step"/>).</summary>
public enum JoinAgainStep
{
    /// <summary>Nothing yet (or nothing wanted).</summary>
    Wait,

    /// <summary>Join the lobby's row now, as a press of it would.</summary>
    Join,

    /// <summary>The row didn't come in time: the player has the Join list.</summary>
    GaveUp,

    /// <summary>Another session began meanwhile: the wish goes.</summary>
    Dropped,
}

/// <summary>
/// A friend's Try again after a failed match load: their session has ended (their game goes home to its title), and the same
/// lobby is joined again from the Join list once it's up, idle and lists that lobby as one it can join again (<see cref="Fits"/>),
/// as the player would press its row. Given up after <see cref="WaitMicros"/> (past the longest a game takes to leave the host's
/// scenes); dropped when another session begins first or the list is left. Pure logic; not thread-safe (one game thread).
/// </summary>
public sealed class JoinAgain
{
    /// <summary>How long the lobby's row is waited for.</summary>
    public const long WaitMicros = 150_000_000;

    private long _since;

    /// <summary>The lobby to join again, or 0.</summary>
    public ulong Lobby { get; private set; }

    /// <summary>Its host's name, as the session had it.</summary>
    public string HostName { get; private set; } = string.Empty;

    /// <summary>Join <paramref name="lobby"/> again once its row can be pressed (0: nothing).</summary>
    public void Want(ulong lobby, string hostName, long nowMicros)
    {
        Lobby = lobby;
        HostName = hostName ?? string.Empty;
        _since = nowMicros;
    }

    /// <summary>
    /// True when <paramref name="row"/> is the wanted lobby's and a join again can press it: joinable, or greyed only as full. A
    /// player who leaves a game in play keeps their seat on the host for a while (their player is parked), and the lobby's count
    /// includes that seat, so with every other seat taken the row reads full because of this very player: the host lets its
    /// parked player back in, and refuses, said as any refusal is, if the seat has gone meanwhile. Not a row still being checked,
    /// another version's, or one fading out.
    /// </summary>
    public bool Fits(BrowserRow? row) =>
        Lobby != 0 && row is not null && row.LobbyId == Lobby && !row.Fading && row.Block is BrowserBlock.None or BrowserBlock.Full;

    /// <summary>Forgets the wish (the list was left, another row or choice was pressed).</summary>
    public void Drop()
    {
        Lobby = 0;
        HostName = string.Empty;
    }

    /// <summary>
    /// Once a frame while a lobby is wanted: <paramref name="inSession"/> when a session runs; <paramref name="listUp"/> when the
    /// Join list is shown, idle, and nothing is over it; <paramref name="rowJoinable"/> when that list has the lobby's row and it
    /// can be joined.
    /// </summary>
    public JoinAgainStep Step(long nowMicros, bool inSession, bool listUp, bool rowJoinable)
    {
        if (Lobby == 0)
        {
            return JoinAgainStep.Wait;
        }

        if (inSession)
        {
            Drop();
            return JoinAgainStep.Dropped;
        }

        if (listUp && rowJoinable)
        {
            Lobby = 0; // the name stays for the caller's line
            return JoinAgainStep.Join;
        }

        if (nowMicros >= _since && unchecked((ulong)(nowMicros - _since)) >= WaitMicros)
        {
            Lobby = 0;
            return JoinAgainStep.GaveUp;
        }

        return JoinAgainStep.Wait;
    }
}
