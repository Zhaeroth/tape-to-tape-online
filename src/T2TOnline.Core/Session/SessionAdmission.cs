using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Session;

/// <summary>Where a peer stands with the host at a member poll (<see cref="SessionAdmission.Judge"/>).</summary>
public enum PeerStanding
{
    /// <summary>
    /// None of the others: a lobby member who hasn't said hello yet, or whose hello was refused, or who left the session and
    /// isn't kept a seat.
    /// </summary>
    None,

    /// <summary>In the session: their Steam session is live, and the host's own sends keep it so. It is never touched.</summary>
    Joined,

    /// <summary>Not joined now, but the host takes their hello: their hello waits for its answer, or a seat is kept for them.</summary>
    Admitted,

    /// <summary>Removed by the host for this hosting session: every hello of theirs is refused.</summary>
    Removed,
}

/// <summary>What the host does with a peer's Steam session now (<see cref="SessionAdmission.Judge"/>).</summary>
public enum SessionVerdict
{
    /// <summary>Nothing: no accept (a request of theirs waits, carrying nothing to the host), no close.</summary>
    Leave,

    /// <summary>Accept their session request, so their hello gets through (accepting an active session changes nothing).</summary>
    Accept,

    /// <summary>Close their session: the refusal (or the removal's goodbye) has left, and its grace has passed.</summary>
    CloseRefused,

    /// <summary>Close their session: the refusal still hadn't left after <see cref="SessionAdmission.CloseAtMostMicros"/>.</summary>
    CloseRefusalStuck,

    /// <summary>Close their session: in the lobby past <see cref="SessionAdmission.WindowMicros"/> with no hello taken.</summary>
    CloseNoHello,
}

/// <summary>
/// The host's rule for the Steam sessions of lobby members: it bounds how long a peer that never sits can queue messages on
/// the host (a bound on how fast any peer may send is another matter, set from a real session's numbers). Steam keeps
/// whatever an accepted peer sends on a channel nobody reads, with no limit but its size per message, until the session
/// closes, and closing it throws all of it away. So the host accepts a session only from a member it has admitted or is
/// still deciding on, and closes the session of one it refused or removed once the refusal has left; a request it doesn't
/// accept carries nothing to it (the peer's messages wait on the peer's side).
/// </summary>
/// <remarks>
/// <para>
/// Per peer, at each member poll (<see cref="Judge"/>), in order: joined, nothing (as before); removed, refused once, when first
/// seen so; admitted (a hello waiting, or a kept seat: a rejoin after a drop comes back through this), accept; refused (by
/// <see cref="Refused"/>, or removed), no accept, and a close once the refusal has left and <see cref="CloseAfterMicros"/> has
/// passed since the latest refusal, or <see cref="CloseAtMostMicros"/> after the first whatever its state; then closed for
/// <see cref="ReopenAfterMicros"/>; then open again, so a friend who asks again is judged as before and gets the refusal they
/// would have got. Anyone else in the lobby is accepted for <see cref="WindowMicros"/> from when the host first saw them, or
/// last had them admitted, or reopened them: a joining friend's own hellos stop well within it. Past it their session is
/// closed once, and nothing more happens until they leave the lobby and come back (a new entry).
/// </para>
/// <para>
/// One pass is <see cref="BeginPass"/>, a <see cref="Judge"/> for every lobby member, every removed peer and every peer
/// <see cref="CopyPending"/> lists, then <see cref="EndPass"/>, which forgets the peers not judged in it (left the lobby and
/// nothing waiting to close). At most <see cref="MaxPeers"/> peers are held; one more isn't held, and is accepted while in the
/// lobby (the rule as it was), which a lobby of four can't reach. Pure logic; not thread-safe (one game thread).
/// </para>
/// </remarks>
public sealed class SessionAdmission
{
    /// <summary>
    /// How long a lobby member who isn't admitted or refused is accepted for: a joining friend says hello from their lobby
    /// entry for at most <see cref="SessionTiming.HelloGiveUpMicros"/> and <see cref="SessionTiming.FirstJoinRetryMicros"/>,
    /// and the host can't see them before they entered; ten seconds more for a host's own stalls.
    /// </summary>
    public const long WindowMicros = SessionTiming.HelloGiveUpMicros + SessionTiming.FirstJoinRetryMicros + 10_000_000;

    /// <summary>
    /// How long after the latest refusal a session is closed at the earliest, once Steam has the refusal through: the friend's
    /// game reads it within a frame, and a close throws away anything of ours not read yet.
    /// </summary>
    public const long CloseAfterMicros = 3_000_000;

    /// <summary>How long after the first refusal a session is closed whatever its state: a refusal that hasn't left by then won't.</summary>
    public const long CloseAtMostMicros = SessionTiming.GoneAfterMicros;

    /// <summary>
    /// How long a refused peer's session stays closed (their requests not accepted) before they're open again: a friend whose
    /// refused join left the lobby and came back before Steam's member list showed them gone is then heard, their hellos already
    /// waiting in a new request.
    /// </summary>
    public const long ReopenAfterMicros = 10_000_000;

    /// <summary>The most peers held (four lobby seats, the peers the host removed, and refusals waiting to close).</summary>
    public const int MaxPeers = 32;

    private const long Never = long.MinValue;

    private readonly Dictionary<ulong, Peer> _peers = new();
    private readonly List<ulong> _scratch = new();
    private int _pass;

    /// <summary>How many peers are held.</summary>
    public int Count => _peers.Count;

    /// <summary>
    /// The host refused <paramref name="peer"/>'s hello (a refusal is on its way to them): no accept from now, and a close
    /// once it has left. Another refusal before the close moves the grace on, never the latest close.
    /// </summary>
    public void Refused(ulong peer, long nowMicros)
    {
        if (peer != 0 && Hold(peer, nowMicros) is { } held)
        {
            held.Refuse(nowMicros);
        }
    }

    /// <summary>True while <paramref name="peer"/> was refused and their session isn't closed yet (the caller then asks Steam whether the refusal left).</summary>
    public bool ClosePending(ulong peer) => _peers.TryGetValue(peer, out Peer? held) && held.ClosePending;

    /// <summary>Adds every peer whose close is pending to <paramref name="into"/> (for the ones no longer in the lobby).</summary>
    public void CopyPending(List<ulong> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach ((ulong id, Peer held) in _peers)
        {
            if (held.ClosePending)
            {
                into.Add(id);
            }
        }
    }

    /// <summary>Starts a pass: the peers judged until <see cref="EndPass"/> are the ones kept.</summary>
    public void BeginPass() => _pass++;

    /// <summary>
    /// What to do with <paramref name="peer"/>'s session now. <paramref name="inLobby"/>: in the Steam lobby's member list (only
    /// they are ever accepted); <paramref name="refusalOut"/>: Steam has nothing of ours waiting to reach them (asked only while
    /// <see cref="ClosePending"/>). A verdict to close is given once per refusal (or once per entry for
    /// <see cref="SessionVerdict.CloseNoHello"/>).
    /// </summary>
    public SessionVerdict Judge(ulong peer, bool inLobby, PeerStanding standing, bool refusalOut, long nowMicros)
    {
        if (peer == 0)
        {
            return SessionVerdict.Leave;
        }

        if (!_peers.TryGetValue(peer, out Peer? held))
        {
            if (!inLobby && standing != PeerStanding.Removed)
            {
                return SessionVerdict.Leave; // nothing of theirs is held, and only lobby members are ever accepted
            }

            held = Hold(peer, nowMicros);
            if (held == null)
            {
                // Past the limit (a lobby of four can't get here): the rule as it was, for this one.
                return inLobby && standing != PeerStanding.Joined ? SessionVerdict.Accept : SessionVerdict.Leave;
            }
        }

        held.Pass = _pass;
        switch (standing)
        {
            case PeerStanding.Joined:
                held.Admit(nowMicros);
                return SessionVerdict.Leave;
            case PeerStanding.Admitted:
                held.Admit(nowMicros);
                return inLobby ? SessionVerdict.Accept : SessionVerdict.Leave;
            case PeerStanding.Removed when !held.RemovalNoted:
                // The host's removal: its goodbye, if they were joined, is the refusal that must leave first.
                held.RemovalNoted = true;
                held.Refuse(nowMicros);
                break;
        }

        if (held.FirstRefusedAt != Never)
        {
            if (held.ClosedAt == Never)
            {
                if (nowMicros - held.FirstRefusedAt >= CloseAtMostMicros)
                {
                    held.ClosedAt = nowMicros;
                    return refusalOut ? SessionVerdict.CloseRefused : SessionVerdict.CloseRefusalStuck;
                }

                if (refusalOut && nowMicros - held.LastRefusedAt >= CloseAfterMicros)
                {
                    held.ClosedAt = nowMicros;
                    return SessionVerdict.CloseRefused;
                }

                return SessionVerdict.Leave;
            }

            if (nowMicros - held.ClosedAt < ReopenAfterMicros)
            {
                return SessionVerdict.Leave;
            }

            held.Reopen(nowMicros);
        }

        if (!inLobby || held.Expired)
        {
            return SessionVerdict.Leave;
        }

        if (nowMicros - held.Since < WindowMicros)
        {
            return SessionVerdict.Accept;
        }

        held.Expired = true;
        return SessionVerdict.CloseNoHello;
    }

    /// <summary>Ends a pass: forgets the peers not judged in it, unless their close is pending.</summary>
    public void EndPass()
    {
        _scratch.Clear();
        foreach ((ulong id, Peer held) in _peers)
        {
            if (held.Pass != _pass && !held.ClosePending)
            {
                _scratch.Add(id);
            }
        }

        foreach (ulong id in _scratch)
        {
            _peers.Remove(id);
        }

        _scratch.Clear();
    }

    /// <summary>Forgets everyone (the hosting session ended).</summary>
    public void Clear() => _peers.Clear();

    /// <summary>The peer's entry, made if it's new (its window starting now), or null when <see cref="MaxPeers"/> are held.</summary>
    private Peer? Hold(ulong peer, long nowMicros)
    {
        if (_peers.TryGetValue(peer, out Peer? held))
        {
            return held;
        }

        if (_peers.Count >= MaxPeers)
        {
            return null;
        }

        held = new Peer(nowMicros) { Pass = _pass };
        _peers[peer] = held;
        return held;
    }

    /// <summary>One peer's standing with the rule.</summary>
    private sealed class Peer
    {
        public Peer(long nowMicros) => Since = nowMicros;

        /// <summary>When their window started: first seen, last admitted, or reopened.</summary>
        public long Since { get; private set; }

        /// <summary>The first refusal not yet followed by a close, or <see cref="Never"/>.</summary>
        public long FirstRefusedAt { get; private set; } = Never;

        /// <summary>The latest refusal, or <see cref="Never"/>.</summary>
        public long LastRefusedAt { get; private set; } = Never;

        /// <summary>When their session was closed for the refusal, or <see cref="Never"/>.</summary>
        public long ClosedAt { get; set; } = Never;

        /// <summary>True once their window ran out with no hello taken (and their session was closed for it).</summary>
        public bool Expired { get; set; }

        /// <summary>True once their removal has been taken as a refusal (it is for good, so once).</summary>
        public bool RemovalNoted { get; set; }

        /// <summary>The pass that last judged them.</summary>
        public int Pass { get; set; }

        public bool ClosePending => FirstRefusedAt != Never && ClosedAt == Never;

        public void Refuse(long nowMicros)
        {
            if (!ClosePending)
            {
                FirstRefusedAt = nowMicros;
                ClosedAt = Never;
            }

            LastRefusedAt = nowMicros;
            Expired = false;
        }

        public void Admit(long nowMicros)
        {
            Since = nowMicros;
            FirstRefusedAt = LastRefusedAt = ClosedAt = Never;
            Expired = false;
        }

        public void Reopen(long nowMicros) => Admit(nowMicros);
    }
}
