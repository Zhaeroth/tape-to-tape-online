using System;
using System.Linq;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

public class LobbyMessagesTests
{
    /// <summary>A board with every field set and every kind of seat: pinned in <see cref="WireLayoutTests"/>.</summary>
    internal static LobbyBoardMessage Sample() => new()
    {
        Phase = LobbyPhase.Counting,
        Epoch = 300,
        Mode = LobbyMode.Campaign,
        Activity = HostActivity.PickingSave,
        CountdownMs = 2750,
        Capacity = 4,
        Save = new LobbySaveRow(2, true, "squad-a", 2, 5, 1_793_000_000),
        Result = new LobbyResult(LobbyResultKind.MatchWon, 4, 2),
        Seats = new[]
        {
            new LobbyBoardSeat(0, 0x0102030405060708, "Alex", true, SeatStage.Here, false, InputKind.Gamepad, 1, 0, -1, 0, 0),
            new LobbyBoardSeat(1, 0x1112131415161718, "Sam", false, SeatStage.Here, true, InputKind.KeyboardMouse, 0, 1, 58, 3, 0),
            new LobbyBoardSeat(2, 0x2122232425262728, "Älex", false, SeatStage.Reconnecting, false, InputKind.Gamepad, 2, 2, 151, 1, 27),
            new LobbyBoardSeat(3, 0x3132333435363738, "Riley", false, SeatStage.Invited, false, InputKind.Gamepad, 0, 3, -1, 0, 0),
        },
    };

    private static byte[] Encode(LobbyBoardMessage board)
    {
        var writer = new BitWriter();
        board.Write(writer);
        return writer.ToArray();
    }

    private static LobbyBoardMessage Decode(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        Assert.True(LobbyBoardMessage.TryRead(ref reader, out LobbyBoardMessage? board));
        Assert.False(reader.IsOverrun);
        return board!;
    }

    private static bool Reads(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        return LobbyBoardMessage.TryRead(ref reader, out _);
    }

    [Fact]
    public void A_board_round_trips()
    {
        LobbyBoardMessage sent = Sample();
        LobbyBoardMessage read = Decode(Encode(sent));

        Assert.Equal((sent.Phase, sent.Epoch, sent.Mode, sent.Activity, sent.CountdownMs, sent.Capacity), (read.Phase, read.Epoch, read.Mode, read.Activity, read.CountdownMs, read.Capacity));
        Assert.Equal(sent.Save, read.Save);
        Assert.Equal(sent.Result, read.Result);
        Assert.Equal(sent.Seats, read.Seats);
    }

    [Fact]
    public void An_empty_board_round_trips_and_has_defaults()
    {
        LobbyBoardMessage read = Decode(Encode(new LobbyBoardMessage()));

        Assert.Equal(LobbyPhase.Open, read.Phase);
        Assert.Equal(4, read.Capacity);
        Assert.Equal(LobbySaveRow.None, read.Save);
        Assert.Equal(LobbyResult.None, read.Result);
        Assert.Empty(read.Seats);
    }

    [Fact]
    public void Every_phase_mode_stage_and_result_round_trips()
    {
        foreach (LobbyPhase phase in Enum.GetValues<LobbyPhase>())
        {
            Assert.Equal(phase, Decode(Encode(new LobbyBoardMessage { Phase = phase })).Phase);
        }

        foreach (LobbyMode mode in Enum.GetValues<LobbyMode>())
        {
            Assert.Equal(mode, Decode(Encode(new LobbyBoardMessage { Mode = mode })).Mode);
        }

        foreach (HostActivity activity in Enum.GetValues<HostActivity>())
        {
            Assert.Equal(activity, Decode(Encode(new LobbyBoardMessage { Activity = activity })).Activity);
        }

        foreach (LobbyResultKind kind in Enum.GetValues<LobbyResultKind>())
        {
            Assert.Equal(kind, Decode(Encode(new LobbyBoardMessage { Result = new LobbyResult(kind, 1, 2) })).Result.Kind);
        }

        foreach (SeatStage stage in Enum.GetValues<SeatStage>().Where(s => s != SeatStage.Open))
        {
            var seat = new LobbyBoardSeat(1, 5, "a", false, stage, false, InputKind.Gamepad, 0, 0, -1, 0, 0);
            Assert.Equal(stage, Decode(Encode(new LobbyBoardMessage { Seats = new[] { seat } })).Seats[0].Stage);
        }
    }

    [Fact]
    public void Ten_full_seats_fit_under_the_cap_and_round_trip()
    {
        LobbyBoardSeat[] seats = Enumerable.Range(0, 10).Select(i =>
            new LobbyBoardSeat((byte)i, (ulong)(i + 1), new string('é', 16), i == 0, SeatStage.Reconnecting, true, InputKind.KeyboardMouse, 255, 255, short.MaxValue, 3, ushort.MaxValue)).ToArray();
        var board = new LobbyBoardMessage
        {
            Capacity = 10,
            Epoch = uint.MaxValue,
            Save = new LobbySaveRow(LobbyBoardMessage.MaxSlot, true, new string('x', 48), 255, 255, LobbyBoardMessage.MaxSavedAtUnixSeconds),
            Seats = seats,
        };

        byte[] bytes = Encode(board);

        Assert.True(bytes.Length <= LobbyBoardMessage.MaxPayloadBytes, $"{bytes.Length} bytes");
        Assert.Equal(seats, Decode(bytes).Seats);
    }

    [Fact]
    public void Truncated_boards_are_refused_at_every_length()
    {
        byte[] full = Encode(Sample());
        for (int length = 0; length < full.Length; length++)
        {
            Assert.False(Reads(full.AsSpan(0, length).ToArray()), $"{length} of {full.Length} bytes");
        }
    }

    [Fact]
    public void Bytes_left_over_are_refused()
    {
        byte[] full = Encode(Sample());
        Assert.False(Reads(full.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(1234);
        foreach (int length in Enumerable.Range(0, 600))
        {
            byte[] bytes = new byte[random.Next(0, 800)];
            random.NextBytes(bytes);
            _ = Reads(bytes);
            var reader = new BitReader(bytes);
            _ = LobbyMemberMessage.TryRead(ref reader, out _);
        }

        // And single-byte damage to a good board, which gets much further in.
        byte[] good = Encode(Sample());
        for (int i = 0; i < good.Length; i++)
        {
            foreach (byte value in new byte[] { 0, 1, 2, 5, 9, 10, 11, 127, 128, 200, 255 })
            {
                byte[] damaged = (byte[])good.Clone();
                damaged[i] = value;
                if (Reads(damaged))
                {
                    LobbyBoardMessage board = Decode(damaged);
                    Assert.Equal(board.Seats.Count, board.Seats.Select(s => s.Index).Distinct().Count());
                    Assert.Equal(board.Seats.Count, board.Seats.Select(s => s.SteamId).Distinct().Count());
                    Assert.True(board.Seats.Count(s => s.IsHost) <= 1);
                    Assert.All(board.Seats, s =>
                    {
                        Assert.True(s.Index < board.Capacity);
                        Assert.NotEqual(0UL, s.SteamId);
                        Assert.NotEqual(SeatStage.Open, s.Stage);
                        Assert.True(s.PingMs >= -1);
                        Assert.True(s.Bars <= SignalBars.Most);
                    });
                }
            }
        }
    }

    /// <summary>The payload's offset of each fixed field in <see cref="Sample"/>'s bytes.</summary>
    private const int FormatAt = 0;
    private const int PhaseAt = 1;

    private static byte[] Corrupt(LobbyBoardMessage board, int at, byte value)
    {
        byte[] bytes = Encode(board);
        bytes[at] = value;
        return bytes;
    }

    [Fact]
    public void Another_format_or_a_phase_this_version_does_not_know_is_refused()
    {
        Assert.False(Reads(Corrupt(new LobbyBoardMessage(), FormatAt, 2)));
        Assert.False(Reads(Corrupt(new LobbyBoardMessage(), PhaseAt, 5)));
        Assert.False(Reads(Corrupt(new LobbyBoardMessage(), PhaseAt, 255)));
    }

    /// <summary>A board's bytes hand-written, for what a writer never produces.</summary>
    private static byte[] Raw(byte phase = 0, ulong epoch = 1, byte mode = 0, byte activity = 0, ushort countdown = 0, byte capacity = 4, byte slot = 0, byte hasRun = 0, long savedAt = 0,
        byte resultKind = 0, byte count = 0, params (byte Index, ulong Id, byte Flags, byte Stage, short Ping)[] seats)
    {
        var w = new BitWriter();
        w.WriteByte(LobbyBoardMessage.Format);
        w.WriteByte(phase);
        w.WriteVarUInt(epoch);
        w.WriteByte(mode);
        w.WriteByte(activity);
        w.WriteUInt16(countdown);
        w.WriteByte(capacity);
        w.WriteByte(slot);
        w.WriteByte(hasRun);
        w.WriteShortString(string.Empty);
        w.WriteByte(0);
        w.WriteByte(0);
        w.WriteVarInt(savedAt);
        w.WriteByte(resultKind);
        w.WriteByte(1);
        w.WriteByte(2);
        w.WriteByte(count);
        foreach ((byte index, ulong id, byte flags, byte stage, short ping) in seats)
        {
            w.WriteByte(index);
            w.WriteUInt64(id);
            w.WriteShortString("x");
            w.WriteByte(flags);
            w.WriteByte(stage);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteByte(0);
            w.WriteInt16(ping);
            w.WriteUInt16(0);
        }

        return w.ToArray();
    }

    [Fact]
    public void Numbers_out_of_their_bounds_are_refused()
    {
        Assert.True(Reads(Raw()));
        Assert.False(Reads(Raw(mode: 2)));
        Assert.False(Reads(Raw(epoch: (ulong)uint.MaxValue + 1)));
        Assert.False(Reads(Raw(countdown: LobbyBoardMessage.MaxCountdownMs + 1)));
        Assert.False(Reads(Raw(capacity: 0)));
        Assert.False(Reads(Raw(capacity: 11)));
        Assert.False(Reads(Raw(slot: LobbyBoardMessage.MaxSlot + 1)));
        Assert.False(Reads(Raw(hasRun: 2)));
        Assert.False(Reads(Raw(savedAt: -1)));
        Assert.False(Reads(Raw(savedAt: LobbyBoardMessage.MaxSavedAtUnixSeconds + 1)));
        Assert.False(Reads(Raw(capacity: 4, count: 5)));
    }

    [Fact]
    public void Seats_that_make_no_sense_are_refused()
    {
        Assert.True(Reads(Raw(count: 1, seats: (0, 5, 0, (byte)SeatStage.Here, -1))));
        Assert.False(Reads(Raw(count: 1, seats: (4, 5, 0, (byte)SeatStage.Here, -1)))); // past the lobby's seats
        Assert.False(Reads(Raw(count: 1, seats: (0, 0, 0, (byte)SeatStage.Here, -1)))); // nobody
        Assert.False(Reads(Raw(count: 1, seats: (0, 5, 0, (byte)SeatStage.Open, -1)))); // an empty seat isn't listed
        Assert.False(Reads(Raw(count: 1, seats: (0, 5, 0, (byte)SeatStage.Reconnecting + 1, -1)))); // a stage it doesn't know
        Assert.False(Reads(Raw(count: 1, seats: (0, 5, 0, (byte)SeatStage.Here, -2)))); // a ping below unknown
        Assert.False(Reads(Raw(count: 2, seats: new (byte, ulong, byte, byte, short)[] { (0, 5, 0, 3, -1), (0, 6, 0, 3, -1) }))); // a seat twice
        Assert.False(Reads(Raw(count: 2, seats: new (byte, ulong, byte, byte, short)[] { (0, 5, 0, 3, -1), (1, 5, 0, 3, -1) }))); // a person twice
        Assert.False(Reads(Raw(count: 2, seats: new (byte, ulong, byte, byte, short)[] { (0, 5, 1, 3, -1), (1, 6, 1, 3, -1) }))); // two hosts
    }

    [Fact]
    public void An_activity_or_result_this_version_does_not_know_reads_as_none()
    {
        LobbyBoardMessage read = Decode(Raw(activity: 200, resultKind: 200));

        Assert.Equal(HostActivity.None, read.Activity);
        Assert.Equal(LobbyResult.None, read.Result);
    }

    [Fact]
    public void A_device_kind_this_version_does_not_know_reads_as_a_gamepad()
    {
        var seat = new LobbyBoardSeat(1, 5, "a", false, SeatStage.Here, false, (InputKind)200, 0, 0, -1, 0, 0);

        Assert.Equal(InputKind.Gamepad, Decode(Encode(new LobbyBoardMessage { Seats = new[] { seat } })).Seats[0].DeviceKind);
    }

    [Fact]
    public void Writing_clamps_what_a_reader_would_refuse()
    {
        var board = new LobbyBoardMessage
        {
            Capacity = 200,
            CountdownMs = ushort.MaxValue,
            Save = new LobbySaveRow(200, true, new string('s', 100), 1, 1, long.MaxValue),
            Seats = new[]
            {
                new LobbyBoardSeat(0, 5, new string('n', 100), true, SeatStage.Here, false, InputKind.Gamepad, 0, 0, -30, 200, 0),
                new LobbyBoardSeat(1, 0, "nobody", false, SeatStage.Here, false, InputKind.Gamepad, 0, 0, 0, 0, 0),
                new LobbyBoardSeat(2, 6, "an empty seat", false, SeatStage.Open, false, InputKind.Gamepad, 0, 0, 0, 0, 0),
                new LobbyBoardSeat(100, 7, "past the end", false, SeatStage.Here, false, InputKind.Gamepad, 0, 0, 0, 0, 0),
            },
        };

        LobbyBoardMessage read = Decode(Encode(board));

        Assert.Equal(LobbyState.MaxSeats, read.Capacity);
        Assert.Equal(LobbyBoardMessage.MaxCountdownMs, read.CountdownMs);
        Assert.Equal(LobbyBoardMessage.MaxSlot, read.Save.Slot);
        Assert.Equal(LobbyBoardMessage.MaxSquadBytes, read.Save.SquadId.Length);
        Assert.Equal(LobbyBoardMessage.MaxSavedAtUnixSeconds, read.Save.SavedAtUnixSeconds);
        LobbyBoardSeat seat = Assert.Single(read.Seats);
        Assert.Equal(LobbyBoardMessage.MaxNameBytes, seat.Name.Length);
        Assert.Equal(-1, seat.PingMs);
        Assert.Equal(SignalBars.Most, seat.Bars);
    }

    [Fact]
    public void Names_and_squads_from_the_wire_are_cleaned()
    {
        var w = new LobbyBoardMessage
        {
            Save = new LobbySaveRow(1, true, "sq\nad", 1, 1, 0),
            Seats = new[] { new LobbyBoardSeat(0, 5, "a\u202Eb\nc", true, SeatStage.Here, false, InputKind.Gamepad, 0, 0, -1, 0, 0) },
        };

        LobbyBoardMessage read = Decode(Encode(w));

        Assert.Equal("a?b?c", read.Seats[0].Name);
        Assert.Equal("sq?ad", read.Save.SquadId);
    }

    [Fact]
    public void A_member_message_round_trips_in_every_state()
    {
        foreach (LobbyMemberStage stage in Enum.GetValues<LobbyMemberStage>())
        {
            foreach (bool ready in new[] { false, true })
            {
                foreach (uint epoch in new uint[] { 0, 1, 300, uint.MaxValue })
                {
                    var sent = new LobbyMemberMessage(ready, stage, epoch);
                    var writer = new BitWriter();
                    sent.Write(writer);
                    var reader = new BitReader(writer.AsSpan());

                    Assert.True(LobbyMemberMessage.TryRead(ref reader, out LobbyMemberMessage read));
                    Assert.Equal(sent, read);
                }
            }
        }
    }

    [Fact]
    public void A_bad_member_message_is_refused()
    {
        foreach (byte[] bad in new[]
        {
            Array.Empty<byte>(),
            new byte[] { 1 },
            new byte[] { 1, 0 },
            new byte[] { 2, 0, 0 }, // flags it doesn't know
            new byte[] { 0, 4, 0 }, // a stage it doesn't know
            new byte[] { 0, 0, 0, 0 }, // bytes left over
            new byte[] { 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, // an epoch past u32
        })
        {
            var reader = new BitReader(bad);
            Assert.False(LobbyMemberMessage.TryRead(ref reader, out LobbyMemberMessage read));
            Assert.Equal(default, read);
        }
    }
}
