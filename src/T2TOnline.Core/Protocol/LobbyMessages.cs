using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>One person on the board: the host's seat, a friend's, or an invited ghost. Empty seats aren't listed.</summary>
/// <param name="Index">The seat's place, from 0, under the board's <see cref="LobbyBoardMessage.Capacity"/>.</param>
/// <param name="SteamId">Their Steam ID, never 0.</param>
/// <param name="Name">The host's cleaned name for them (at most <see cref="LobbyBoardMessage.MaxNameBytes"/>): a fallback for a friend of a friend.</param>
/// <param name="IsHost">The host's seat. At most one.</param>
/// <param name="Stage">Where they are; never <see cref="SeatStage.Open"/>.</param>
/// <param name="Ready">Their optional ready mark.</param>
/// <param name="DeviceKind">What they play with.</param>
/// <param name="DeviceFamily">Their pad's family, the game's own device type.</param>
/// <param name="PlayerColor">Their player colour's index.</param>
/// <param name="PingMs">Their round trip to the host in ms, -1 when unknown, at most <see cref="short.MaxValue"/>.</param>
/// <param name="Bars">Signal bars, 0 to 3 (<see cref="SignalBars"/>).</param>
/// <param name="ReconnectSeconds">Seconds their seat is still kept while reconnecting.</param>
public sealed record LobbyBoardSeat(byte Index, ulong SteamId, string Name, bool IsHost, SeatStage Stage, bool Ready, InputKind DeviceKind, byte DeviceFamily, byte PlayerColor, short PingMs, byte Bars, ushort ReconnectSeconds);

/// <summary>
/// Host to friend (channel 1, <see cref="MessageType.LobbyBoard"/>, protocol 7): the lobby's whole shared state, the one
/// source of truth. Sent in full on change, at most four times a second per friend, and at once to a friend who just joined.
/// Layout: format (u8, <see cref="Format"/>), phase (u8), epoch (varuint, at most u32), mode (u8), activity (u8), countdown ms
/// (u16, at most <see cref="MaxCountdownMs"/>), capacity (u8, 1 to 10), the save row (slot u8, at most <see cref="MaxSlot"/>; has-run
/// u8; squad id short string, at most <see cref="MaxSquadBytes"/>; act u8; floor u8; saved-at varint, UTC seconds, 0 unknown), the
/// result (kind u8, first u8, second u8), the seat count (u8, at most capacity), then per seat its index (u8), Steam ID (u64),
/// name (short string, at most <see cref="MaxNameBytes"/>), flags (u8: bit 0 host, bit 1 ready, bits 2-3 bars), stage (u8), device
/// kind (u8), device family (u8), colour (u8), ping (i16) and seconds to reconnect (u16).
/// </summary>
/// <remarks>
/// A phase, mode or stage this version doesn't know refuses the board: the flow can't act on it. An activity or a result
/// kind it doesn't know reads as none: they only colour the screen. Device kinds it doesn't know read as a gamepad. Text
/// from the wire is cleaned (<see cref="SafeText"/>).
/// </remarks>
public sealed record LobbyBoardMessage
{
    /// <summary>The layout's format.</summary>
    public const byte Format = 1;

    /// <summary>Longest name kept, in bytes of UTF-8.</summary>
    public const int MaxNameBytes = 32;

    /// <summary>Longest squad id kept, in bytes of UTF-8.</summary>
    public const int MaxSquadBytes = 48;

    /// <summary>Highest slot number on the board (the mod has 3).</summary>
    public const byte MaxSlot = 9;

    /// <summary>Longest countdown on the board, in ms (the countdown is 3 s).</summary>
    public const ushort MaxCountdownMs = 10_000;

    /// <summary>Latest saved-at on the board: the year 3000. Later is nonsense, and refused.</summary>
    public const long MaxSavedAtUnixSeconds = 32_503_680_000;

    /// <summary>The largest payload a friend takes, in bytes: a board with 10 full seats takes under 600.</summary>
    public const int MaxPayloadBytes = 1024;

    /// <summary>Where the lobby is.</summary>
    public LobbyPhase Phase { get; init; }

    /// <summary>The current Start's number, 0 before the first.</summary>
    public uint Epoch { get; init; }

    /// <summary>What Start will start.</summary>
    public LobbyMode Mode { get; init; }

    /// <summary>What the host is busy with.</summary>
    public HostActivity Activity { get; init; }

    /// <summary>The countdown left when this was made, in ms; 0 outside <see cref="LobbyPhase.Counting"/>.</summary>
    public ushort CountdownMs { get; init; }

    /// <summary>The lobby's seats, 1 to <see cref="LobbyState.MaxSeats"/>.</summary>
    public byte Capacity { get; init; } = LobbyState.TunedSeats;

    /// <summary>The save row.</summary>
    public LobbySaveRow Save { get; init; } = LobbySaveRow.None;

    /// <summary>The ribbon after a game.</summary>
    public LobbyResult Result { get; init; } = LobbyResult.None;

    /// <summary>Who's there. Written at most <see cref="Capacity"/> of them.</summary>
    public IReadOnlyList<LobbyBoardSeat> Seats { get; init; } = Array.Empty<LobbyBoardSeat>();

    /// <summary>Writes the payload (after the header). Values outside the layout's bounds are clamped, so a reader always takes it.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(Seats);

        int capacity = Math.Clamp((int)Capacity, 1, LobbyState.MaxSeats);
        writer.WriteByte(Format);
        writer.WriteByte((byte)Phase);
        writer.WriteVarUInt(Epoch);
        writer.WriteByte((byte)Mode);
        writer.WriteByte((byte)Activity);
        writer.WriteUInt16(Math.Min(CountdownMs, MaxCountdownMs));
        writer.WriteByte((byte)capacity);
        writer.WriteByte(Math.Min(Save.Slot, MaxSlot));
        writer.WriteByte(Save.HasRun ? (byte)1 : (byte)0);
        writer.WriteShortString(Save.SquadId ?? string.Empty, MaxSquadBytes);
        writer.WriteByte(Save.Act);
        writer.WriteByte(Save.Floor);
        writer.WriteVarInt(Math.Clamp(Save.SavedAtUnixSeconds, 0, MaxSavedAtUnixSeconds));
        writer.WriteByte((byte)Result.Kind);
        writer.WriteByte(Result.First);
        writer.WriteByte(Result.Second);

        int count = 0;
        foreach (LobbyBoardSeat seat in Seats)
        {
            count += count < capacity && Listed(seat, capacity) ? 1 : 0;
        }

        writer.WriteByte((byte)count);
        int written = 0;
        foreach (LobbyBoardSeat seat in Seats)
        {
            if (written >= count || !Listed(seat, capacity))
            {
                continue;
            }

            written++;
            writer.WriteByte(seat.Index);
            writer.WriteUInt64(seat.SteamId);
            writer.WriteShortString(seat.Name ?? string.Empty, MaxNameBytes);
            writer.WriteByte((byte)((seat.IsHost ? 1 : 0) | (seat.Ready ? 2 : 0) | ((Math.Min((int)seat.Bars, SignalBars.Most) & 3) << 2)));
            writer.WriteByte((byte)seat.Stage);
            writer.WriteByte((byte)seat.DeviceKind);
            writer.WriteByte(seat.DeviceFamily);
            writer.WriteByte(seat.PlayerColor);
            writer.WriteInt16(Math.Max(seat.PingMs, (short)-1));
            writer.WriteUInt16(seat.ReconnectSeconds);
        }
    }

    /// <summary>A seat a reader takes: one of the lobby's, somebody's, not empty.</summary>
    private static bool Listed(LobbyBoardSeat seat, int capacity) => seat.Index < capacity && seat.SteamId != 0 && seat.Stage != SeatStage.Open;

    /// <summary>
    /// Reads a board (after the header). False for truncated or malformed data: another format, a phase, mode or stage this
    /// version doesn't know, numbers out of their bounds, more seats than the lobby has, a seat or Steam ID twice, two hosts, a
    /// Steam ID of 0, or bytes left over. Never throws.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out LobbyBoardMessage? board)
    {
        board = null;
        byte format = reader.ReadByte();
        byte phase = reader.ReadByte();
        ulong epoch = reader.ReadVarUInt();
        byte mode = reader.ReadByte();
        byte activity = reader.ReadByte();
        ushort countdown = reader.ReadUInt16();
        byte capacity = reader.ReadByte();
        byte slot = reader.ReadByte();
        byte hasRun = reader.ReadByte();
        string squad = reader.ReadShortString(MaxSquadBytes);
        byte act = reader.ReadByte();
        byte floor = reader.ReadByte();
        long savedAt = reader.ReadVarInt();
        byte resultKind = reader.ReadByte();
        byte first = reader.ReadByte();
        byte second = reader.ReadByte();
        byte count = reader.ReadByte();
        if (reader.IsOverrun || format != Format || phase > (byte)LobbyPhase.Returning || epoch > uint.MaxValue || mode > (byte)LobbyMode.QuickGame ||
            countdown > MaxCountdownMs || capacity is 0 or > LobbyState.MaxSeats || slot > MaxSlot || hasRun > 1 ||
            savedAt is < 0 or > MaxSavedAtUnixSeconds || count > capacity)
        {
            return false;
        }

        var seats = new LobbyBoardSeat[count];
        bool hostSeen = false;
        for (int i = 0; i < count; i++)
        {
            byte index = reader.ReadByte();
            ulong id = reader.ReadUInt64();
            string name = SafeText.Clean(reader.ReadShortString(MaxNameBytes), MaxNameBytes);
            byte flags = reader.ReadByte();
            byte stage = reader.ReadByte();
            byte kind = reader.ReadByte();
            byte family = reader.ReadByte();
            byte colour = reader.ReadByte();
            short ping = reader.ReadInt16();
            ushort reconnect = reader.ReadUInt16();
            bool isHost = (flags & 1) != 0;
            int bars = (flags >> 2) & 3;
            if (reader.IsOverrun || index >= capacity || id == 0 || stage is 0 or > (byte)SeatStage.Reconnecting || ping < -1 || bars > SignalBars.Most ||
                (isHost && hostSeen))
            {
                return false;
            }

            hostSeen |= isHost;
            for (int earlier = 0; earlier < i; earlier++)
            {
                if (seats[earlier].Index == index || seats[earlier].SteamId == id)
                {
                    return false;
                }
            }

            seats[i] = new LobbyBoardSeat(index, id, name, isHost, (SeatStage)stage, (flags & 2) != 0, kind == (byte)InputKind.KeyboardMouse ? InputKind.KeyboardMouse : InputKind.Gamepad,
                family, colour, ping, (byte)bars, reconnect);
        }

        // Whole bytes are written, so anything past the last one is junk.
        if (reader.IsOverrun || reader.RemainingBits >= 8)
        {
            return false;
        }

        board = new LobbyBoardMessage
        {
            Phase = (LobbyPhase)phase,
            Epoch = (uint)epoch,
            Mode = (LobbyMode)mode,
            Activity = activity <= (byte)HostActivity.WaitingForEveryone ? (HostActivity)activity : HostActivity.None,
            CountdownMs = countdown,
            Capacity = capacity,
            Save = new LobbySaveRow(slot, hasRun != 0, SafeText.Clean(squad, MaxSquadBytes), act, floor, savedAt),
            Result = resultKind <= (byte)LobbyResultKind.MatchScore ? new LobbyResult((LobbyResultKind)resultKind, first, second) : LobbyResult.None,
            Seats = seats,
        };
        return true;
    }
}

/// <summary>
/// Friend to host (channel 1, <see cref="MessageType.LobbyMember"/>, protocol 7): what a friend says about themselves, in full
/// each time. Whose it is comes from the transport, so nobody can speak for another seat. Layout: flags (u8: bit 0 ready),
/// stage (u8), acked epoch (varuint, at most u32).
/// </summary>
/// <param name="Ready">Their optional ready mark.</param>
/// <param name="Stage">Their stage.</param>
/// <param name="AckedEpoch">The newest Start they're following; 0 before the first.</param>
public readonly record struct LobbyMemberMessage(bool Ready, LobbyMemberStage Stage, uint AckedEpoch)
{
    /// <summary>Writes the payload (after the header).</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteByte(Ready ? (byte)1 : (byte)0);
        writer.WriteByte((byte)Stage);
        writer.WriteVarUInt(AckedEpoch);
    }

    /// <summary>Reads the payload. False for truncated data, flags or a stage this version doesn't know, an epoch past u32, or bytes left over.</summary>
    public static bool TryRead(ref BitReader reader, out LobbyMemberMessage message)
    {
        byte flags = reader.ReadByte();
        byte stage = reader.ReadByte();
        ulong epoch = reader.ReadVarUInt();
        message = default;
        if (reader.IsOverrun || flags > 1 || stage > (byte)LobbyMemberStage.Returning || epoch > uint.MaxValue || reader.RemainingBits >= 8)
        {
            return false;
        }

        message = new LobbyMemberMessage(flags != 0, (LobbyMemberStage)stage, (uint)epoch);
        return true;
    }
}
