using System;
using System.Collections.Generic;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Protocol;

public class SessionMessagesTests
{
    [Fact]
    public void Every_session_message_round_trips()
    {
        var hello = new HelloMessage(1, "0.2.0-m1", 0x0E1B540314D591A1, InputKind.Gamepad, 3);
        var welcome = new WelcomeMessage(2, SessionPhase.Menus, 0xFEDC_BA98_7654_3210);
        var reject = new RejectMessage(RejectReason.GameBuildMismatch);
        var ping = new PingMessage(uint.MaxValue, long.MaxValue);
        var pong = new PongMessage(77, long.MinValue, 42);
        var leave = new LeaveMessage(LeaveReason.TimedOut);

        Assert.Equal(hello, RoundTrip(hello.Write, (ref BitReader r, out HelloMessage m) => HelloMessage.TryRead(ref r, out m)));
        Assert.Equal(welcome, RoundTrip(welcome.Write, (ref BitReader r, out WelcomeMessage m) => WelcomeMessage.TryRead(ref r, out m)));
        Assert.Equal(reject, RoundTrip(reject.Write, (ref BitReader r, out RejectMessage m) => RejectMessage.TryRead(ref r, out m)));
        Assert.Equal(ping, RoundTrip(ping.Write, (ref BitReader r, out PingMessage m) => PingMessage.TryRead(ref r, out m)));
        Assert.Equal(pong, RoundTrip(pong.Write, (ref BitReader r, out PongMessage m) => PongMessage.TryRead(ref r, out m)));
        Assert.Equal(leave, RoundTrip(leave.Write, (ref BitReader r, out LeaveMessage m) => LeaveMessage.TryRead(ref r, out m)));
    }

    [Fact]
    public void Truncated_messages_are_refused()
    {
        var writer = new BitWriter();
        new HelloMessage(1, "0.2.0-m1", 1, InputKind.Gamepad, 0).Write(writer);
        byte[] full = writer.ToArray();
        for (int length = 0; length < full.Length; length++)
        {
            var reader = new BitReader(full.AsSpan(0, length));
            Assert.False(HelloMessage.TryRead(ref reader, out _), $"{length} of {full.Length} bytes");
        }

        writer.Reset();
        new PongMessage(1, 2, 3).Write(writer);
        byte[] pong = writer.ToArray();
        var cut = new BitReader(pong.AsSpan(0, pong.Length - 1));
        Assert.False(PongMessage.TryRead(ref cut, out _));
    }

    [Fact]
    public void Hello_and_reject_keep_their_frozen_layout()
    {
        // Every protocol version must read these two, so a host can always answer "wrong version". Never change them.
        var writer = new BitWriter();
        new HelloMessage(9, "v1", 0x0102030405060708, InputKind.KeyboardMouse, 5).Write(writer);
        Assert.Equal(new byte[] { 9, 2, (byte)'v', (byte)'1', 8, 7, 6, 5, 4, 3, 2, 1, 1, 5 }, writer.ToArray());

        writer.Reset();
        new RejectMessage(RejectReason.ProtocolMismatch).Write(writer);
        Assert.Equal(new byte[] { 1 }, writer.ToArray());
    }

    [Fact]
    public void Other_layouts_are_pinned_so_a_change_needs_a_version_bump()
    {
        // These bytes are protocol 9 (unchanged since protocol 4). A failure here means a layout changed: bump
        // ProtocolInfo.Version, then update them.
        Assert.Equal(9, ProtocolInfo.Version);

        var writer = new BitWriter();
        new MessageHeader(MessageType.PadInput, 300, 0x0A0B0C0D, 0x01020304).Write(writer);
        Assert.Equal(new byte[] { 16, 0xAC, 0x02, 0x0D, 0x0C, 0x0B, 0x0A, 0x04, 0x03, 0x02, 0x01 }, writer.ToArray());

        writer.Reset();
        new WelcomeMessage(2, SessionPhase.Menus, 0x0102030405060708).Write(writer);
        Assert.Equal(new byte[] { 2, 1, 8, 7, 6, 5, 4, 3, 2, 1 }, writer.ToArray());

        // The campaign's view and follow report (protocol 4).
        writer.Reset();
        T2TOnline.Core.Campaign.HostViewMessage.Write(writer, new T2TOnline.Core.Campaign.HostView(7, "map", 1, new List<KeyValuePair<string, NetValue>>()));
        Assert.Equal(new byte[] { 7, 0, 0, 0, 3, (byte)'m', (byte)'a', (byte)'p', 1, 0, 0, 0, 0 }, writer.ToArray());
        writer.Reset();
        T2TOnline.Core.Campaign.FollowReportMessage.Write(writer, new T2TOnline.Core.Campaign.FollowReport(5, T2TOnline.Core.Campaign.FollowState.Following, 2, ""));
        Assert.Equal(new byte[] { 5, 0, 0, 0, 3, 2, 0, 0, 0, 0 }, writer.ToArray());

        // A pad packet: newest frame number, frame count - 1 in 3 bits, then one "unchanged" bit per neutral frame.
        writer.Reset();
        PadInputCodec.Write(writer, 0x11223344, new[] { PadState.Neutral, PadState.Neutral });
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11, 0b0000_0001 }, writer.ToArray());

        writer.Reset();
        SnapshotAckMessage.Write(writer, 300, needKeyframe: true);
        Assert.Equal(new byte[] { 0xAC, 0x02, 0x01 }, writer.ToArray());

        // The ready flag is one bit, so the reason's length and bytes that follow sit one bit along.
        writer.Reset();
        MatchReadyMessage.Write(writer, 0x0102030405060708, ready: true, "ab");
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1, 0x05, 0xC2, 0xC4, 0x00 }, writer.ToArray());

        writer.Reset();
        new PongMessage(5, 0x0102030405060708, 0x1112131415161718).Write(writer);
        Assert.Equal(new byte[] { 5, 8, 7, 6, 5, 4, 3, 2, 1, 0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11 }, writer.ToArray());
    }

    [Fact]
    public void Unknown_reasons_from_newer_versions_still_decode()
    {
        var reader = new BitReader(new byte[] { 200 });
        Assert.True(RejectMessage.TryRead(ref reader, out RejectMessage reject));
        Assert.Equal((RejectReason)200, reject.Reason);
    }

    [Fact]
    public void A_long_mod_version_is_cut_at_a_character_boundary()
    {
        string version = new string('é', 40); // 80 bytes of UTF-8
        var writer = new BitWriter();
        new HelloMessage(1, version, 0, InputKind.Gamepad, 0).Write(writer);
        var reader = new BitReader(writer.AsSpan());

        Assert.True(HelloMessage.TryRead(ref reader, out HelloMessage hello));
        Assert.Equal(new string('é', HelloMessage.MaxModVersionBytes / 2), hello.ModVersion);
    }

    [Fact]
    public void A_string_longer_than_its_limit_is_refused_on_read()
    {
        var writer = new BitWriter();
        writer.WriteShortString(new string('a', 100));
        var reader = new BitReader(writer.AsSpan());

        Assert.Equal(string.Empty, reader.ReadShortString(maxBytes: 99));
        Assert.True(reader.IsOverrun);
    }

    [Fact]
    public void Sixty_four_bit_values_round_trip()
    {
        var writer = new BitWriter();
        writer.WriteBool(true); // unaligned on purpose
        writer.WriteInt64(long.MinValue);
        writer.WriteUInt64(ulong.MaxValue);
        var reader = new BitReader(writer.AsSpan());

        Assert.True(reader.ReadBool());
        Assert.Equal(long.MinValue, reader.ReadInt64());
        Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
        Assert.False(reader.IsOverrun);
    }

    private delegate bool Reader<T>(ref BitReader reader, out T message);

    private static T RoundTrip<T>(Action<BitWriter> write, Reader<T> read)
    {
        var writer = new BitWriter();
        write(writer);
        var reader = new BitReader(writer.AsSpan());
        Assert.True(read(ref reader, out T message));
        Assert.False(reader.IsOverrun);
        return message;
    }
}

public class StreamMessagesTests
{
    [Fact]
    public void Object_and_event_tables_round_trip()
    {
        var objects = new List<ObjectEntry> { new(1, 7397, "skater/home/3", "Gedeon Lamoustache"), new(2, -5, string.Empty, "PuckBody") };
        var events = new List<EventEntry> { new(0, "GameplayEvents.AllSkatersInitialized", Array.Empty<string>()), new(1, "GameplayEvents.Goal", new[] { "scorer", "team" }) };
        // Each its own message, as on the wire: the object table's reader takes what follows its entries as its types section.
        var objectWriter = new BitWriter();
        ObjectTableMessage.Write(objectWriter, objects);
        var eventWriter = new BitWriter();
        EventTableMessage.Write(eventWriter, events);
        var objectReader = new BitReader(objectWriter.AsSpan());
        var eventReader = new BitReader(eventWriter.AsSpan());
        var readObjects = new List<ObjectEntry>();
        var readEvents = new List<EventEntry>();

        Assert.True(ObjectTableMessage.TryRead(ref objectReader, readObjects));
        Assert.True(EventTableMessage.TryRead(ref eventReader, readEvents));
        Assert.Equal(objects, readObjects);
        Assert.Equal(events[1].Name, readEvents[1].Name);
        Assert.Equal(events[1].ArgFields, readEvents[1].ArgFields);
    }

    [Fact]
    public void An_object_tables_name_cant_forge_a_log_line_and_its_key_stays_as_sent()
    {
        var writer = new BitWriter();
        ObjectTableMessage.Write(writer, new[] { new ObjectEntry(1, 2, "go:a\nb", "Puck\nINFO forged") });
        var read = new List<ObjectEntry>();
        var reader = new BitReader(writer.AsSpan());
        Assert.True(ObjectTableMessage.TryRead(ref reader, read));
        Assert.Equal("Puck?INFO forged", read[0].Name);
        Assert.Equal("go:a\nb", read[0].Key); // what it's looked up by: cleaned only where it's logged
    }

    [Fact]
    public void Events_sounds_and_hud_round_trip()
    {
        var writer = new BitWriter();
        GameEventMessage.Write(writer, 42, new[] { NetValue.FromRef(3), NetValue.FromFloat(0.75f) });
        var sound = new SoundCall(7, true, Guid.NewGuid(), true, 1, 2, 3, "SoundManager.StartMagnetSound");
        sound.Write(writer);
        HudStateMessage.Write(writer, new[] { new KeyValuePair<string, NetValue>("clock", NetValue.FromFloat(120)), new KeyValuePair<string, NetValue>("score/home", NetValue.FromInt(2)) });
        var reader = new BitReader(writer.AsSpan());

        var args = new List<NetValue>();
        Assert.True(GameEventMessage.TryRead(ref reader, out ushort eventId, args));
        Assert.Equal(42, eventId);
        Assert.Equal(NetValue.FromRef(3), args[0]);
        Assert.True(SoundCall.TryRead(ref reader, out SoundCall readSound));
        Assert.Equal(sound, readSound);
        var hud = new List<KeyValuePair<string, NetValue>>();
        Assert.True(HudStateMessage.TryRead(ref reader, hud));
        Assert.Equal(NetValue.FromInt(2), hud[1].Value);
    }

    [Fact]
    public void Truncated_stream_messages_are_refused()
    {
        var writer = new BitWriter();
        ObjectTableMessage.Write(writer, new[] { new ObjectEntry(1, 2, "puck", "PuckBody") });
        byte[] full = writer.ToArray();
        for (int length = 0; length < full.Length; length++)
        {
            var reader = new BitReader(full.AsSpan(0, length));
            Assert.False(ObjectTableMessage.TryRead(ref reader, new List<ObjectEntry>()), $"{length} bytes");
        }
    }
}
