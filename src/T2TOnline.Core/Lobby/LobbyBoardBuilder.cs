using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Lobby;

/// <summary>One person, as the host's session knows them: plain values, read from the session each time the board is built.</summary>
public readonly record struct LobbyPerson
{
    /// <summary>Their Steam ID.</summary>
    public ulong SteamId { get; init; }

    /// <summary>Their name, cleaned here for the board.</summary>
    public string Name { get; init; }

    /// <summary>Where they are. <see cref="SeatStage.Open"/> leaves them out.</summary>
    public SeatStage Stage { get; init; }

    /// <summary>Their ready mark.</summary>
    public bool Ready { get; init; }

    /// <summary>What they play with.</summary>
    public InputKind DeviceKind { get; init; }

    /// <summary>Their pad's family, the game's own device type.</summary>
    public byte DeviceFamily { get; init; }

    /// <summary>Their round trip in ms, or a negative number when unknown.</summary>
    public int PingMs { get; init; }

    /// <summary>Their stream's loss in percent, for the signal bars (never shown as a number).</summary>
    public double LossPercent { get; init; }

    /// <summary>Seconds their seat is still kept while <see cref="SeatStage.Reconnecting"/>.</summary>
    public int ReconnectSeconds { get; init; }

    /// <summary>A person from what the host knows of a friend's member message.</summary>
    /// <param name="member">Their member message.</param>
    /// <param name="hostPhase">The board's phase now.</param>
    /// <returns>The stage to show: Here on the lobby, or while following a game that runs; Loading while getting ready or coming home.</returns>
    public static SeatStage StageOf(in LobbyMemberMessage member, LobbyPhase hostPhase) => member.Stage switch
    {
        LobbyMemberStage.Preparing or LobbyMemberStage.Returning => SeatStage.Loading,
        LobbyMemberStage.Playing when hostPhase is LobbyPhase.Open or LobbyPhase.Counting or LobbyPhase.Returning => SeatStage.Loading,
        _ => SeatStage.Here,
    };
}

/// <summary>What the host's board is made of, apart from the people: plain values from the flow and the session.</summary>
public sealed record LobbyBoardInputs
{
    /// <summary>The flow's <see cref="LobbyFlow.BoardPhase"/>.</summary>
    public LobbyPhase Phase { get; init; }

    /// <summary>The flow's <see cref="LobbyFlow.Epoch"/>.</summary>
    public uint Epoch { get; init; }

    /// <summary>The flow's <see cref="LobbyFlow.CountdownMsLeft"/>.</summary>
    public int CountdownMs { get; init; }

    /// <summary>The mode chosen.</summary>
    public LobbyMode Mode { get; init; }

    /// <summary>The chosen slot's row; left out of the board in Quick Game.</summary>
    public LobbySaveRow Save { get; init; } = LobbySaveRow.None;

    /// <summary>What the host is busy with.</summary>
    public HostActivity Activity { get; init; }

    /// <summary>The last game's ribbon.</summary>
    public LobbyResult Result { get; init; } = LobbyResult.None;

    /// <summary>The host's own seat (seat 0 always).</summary>
    public LobbyPerson Host { get; init; }

    /// <summary>Everyone else: friends in the session, friends who dropped and whose seat is kept, friends arriving, invited ones.</summary>
    public IReadOnlyList<LobbyPerson> People { get; init; } = Array.Empty<LobbyPerson>();
}

/// <summary>
/// The host's side of the board: a stable seat for each person (the host's is 0; a friend takes the lowest free seat when
/// first seen, keeps it while they're away, and <see cref="Free"/> gives it up), and the board built from plain inputs. An
/// invited ghost takes no seat of its own: it shows on the lowest seat free this time round, and moves if someone arrives.
/// </summary>
public sealed class LobbyBoardBuilder
{
    private readonly Dictionary<ulong, int> _seats = new();
    private readonly bool[] _taken;
    private readonly List<ulong> _order = new();
    private readonly List<LobbyBoardSeat> _build = new(LobbyState.MaxSeats);

    /// <summary>Creates a builder for a lobby of <paramref name="capacity"/> seats (clamped to 1 to <see cref="LobbyState.MaxSeats"/>), this PC on seat 0.</summary>
    public LobbyBoardBuilder(ulong hostId, int capacity = LobbyState.TunedSeats)
    {
        Capacity = Math.Clamp(capacity, 1, LobbyState.MaxSeats);
        HostId = hostId;
        _taken = new bool[Capacity];
        _taken[0] = true;
    }

    /// <summary>The host's Steam ID.</summary>
    public ulong HostId { get; }

    /// <summary>The lobby's seats.</summary>
    public int Capacity { get; }

    /// <summary>The seat of <paramref name="steamId"/>, taking the lowest free one if they have none; -1 when the lobby is full.</summary>
    public int SeatOf(ulong steamId)
    {
        if (steamId == HostId)
        {
            return 0;
        }

        if (steamId == 0)
        {
            return -1;
        }

        if (_seats.TryGetValue(steamId, out int seat))
        {
            return seat;
        }

        for (int i = 1; i < Capacity; i++)
        {
            if (!_taken[i])
            {
                _taken[i] = true;
                _seats[steamId] = i;
                return i;
            }
        }

        return -1;
    }

    /// <summary>Gives <paramref name="steamId"/>'s seat up. False when they have none.</summary>
    public bool Free(ulong steamId)
    {
        if (!_seats.Remove(steamId, out int seat))
        {
            return false;
        }

        _taken[seat] = false;
        return true;
    }

    /// <summary>Friends with a seat, in seat order: the order they become game players at Start.</summary>
    public IReadOnlyList<ulong> FriendsInSeatOrder()
    {
        _order.Clear();
        for (int i = 1; i < Capacity; i++)
        {
            if (!_taken[i])
            {
                continue;
            }

            foreach (KeyValuePair<ulong, int> seat in _seats)
            {
                if (seat.Value == i)
                {
                    _order.Add(seat.Key);
                    break;
                }
            }
        }

        return _order.ToArray();
    }

    /// <summary>The board for <paramref name="inputs"/>: one row per person who has or can take a seat. Nobody who fits is dropped; a lobby that's full leaves out whoever came last.</summary>
    public LobbyBoardMessage Build(LobbyBoardInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        _build.Clear();
        Span<bool> used = stackalloc bool[LobbyState.MaxSeats];
        used[0] = true;
        _build.Add(Row(0, inputs.Host with { SteamId = HostId, Stage = inputs.Host.Stage == SeatStage.Open ? SeatStage.Here : inputs.Host.Stage }, isHost: true));
        foreach (LobbyPerson person in inputs.People)
        {
            if (person.Stage is SeatStage.Open or SeatStage.Invited || person.SteamId == HostId)
            {
                continue;
            }

            int seat = SeatOf(person.SteamId);
            if (seat > 0 && !used[seat])
            {
                used[seat] = true;
                _build.Add(Row(seat, person, isHost: false));
            }
        }

        foreach (LobbyPerson person in inputs.People)
        {
            if (person.Stage == SeatStage.Invited && person.SteamId != 0 && person.SteamId != HostId && !Contains(person.SteamId))
            {
                int ghost = LowestFree(used);
                if (ghost < 0)
                {
                    break;
                }

                used[ghost] = true;
                _build.Add(Row(ghost, person, isHost: false));
            }
        }

        _build.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return new LobbyBoardMessage
        {
            Phase = inputs.Phase,
            Epoch = inputs.Epoch,
            Mode = inputs.Mode,
            Activity = inputs.Activity,
            CountdownMs = inputs.Phase == LobbyPhase.Counting ? (ushort)Math.Clamp(inputs.CountdownMs, 0, LobbyBoardMessage.MaxCountdownMs) : (ushort)0,
            Capacity = (byte)Capacity,
            Save = inputs.Mode == LobbyMode.Campaign ? inputs.Save : LobbySaveRow.None,
            Result = inputs.Result,
            Seats = _build.ToArray(),
        };
    }

    private int LowestFree(Span<bool> used)
    {
        for (int i = 1; i < Capacity; i++)
        {
            if (!used[i] && !_taken[i])
            {
                return i;
            }
        }

        return -1;
    }

    private bool Contains(ulong steamId)
    {
        foreach (LobbyBoardSeat seat in _build)
        {
            if (seat.SteamId == steamId)
            {
                return true;
            }
        }

        return false;
    }

    private static LobbyBoardSeat Row(int seat, in LobbyPerson person, bool isHost)
    {
        int ping = isHost ? -1 : person.PingMs < 0 ? -1 : Math.Min(person.PingMs, short.MaxValue);
        return new LobbyBoardSeat((byte)seat, person.SteamId, SafeText.Clean(person.Name, LobbyBoardMessage.MaxNameBytes), isHost, person.Stage, person.Ready, person.DeviceKind, person.DeviceFamily, (byte)seat,
            (short)ping, (byte)(isHost ? 0 : SignalBars.Of(ping, person.LossPercent)), (ushort)Math.Clamp(person.ReconnectSeconds, 0, ushort.MaxValue));
    }
}
