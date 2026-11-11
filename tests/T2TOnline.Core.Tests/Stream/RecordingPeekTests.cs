using System;
using System.Collections.Generic;
using System.IO;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Stream;

/// <summary>A recording's match setup read from its first records, as the replay tour picks a campaign recording.</summary>
public class RecordingPeekTests
{
    [Fact]
    public void The_setup_is_read_from_the_first_records()
    {
        byte[] file = Recording(Setup(0), hudFirst: 0);
        var setup = new List<KeyValuePair<string, NetValue>>();
        Assert.True(RecordingPeek.TryReadSetup(new MemoryStream(file), setup, out string problem), problem);
        Assert.Equal("team_a", MatchSetupValues.Get(setup, "home.team").Text);
        Assert.False(CampaignMatchValues.IsCampaign(setup));
    }

    [Fact]
    public void A_campaign_recording_says_so()
    {
        var setup = new List<KeyValuePair<string, NetValue>>();
        Assert.True(RecordingPeek.TryReadSetup(new MemoryStream(Recording(Setup(1), hudFirst: 2)), setup, out string problem), problem);
        Assert.True(CampaignMatchValues.IsCampaign(setup));
    }

    [Fact]
    public void A_setup_past_the_bound_or_none_at_all_is_refused_with_why()
    {
        var setup = new List<KeyValuePair<string, NetValue>>();
        Assert.False(RecordingPeek.TryReadSetup(new MemoryStream(Recording(Setup(0), hudFirst: 5)), setup, out string problem, maxRecords: 3));
        Assert.Contains("first 3 records", problem, StringComparison.Ordinal);
        Assert.Empty(setup);
        Assert.False(RecordingPeek.TryReadSetup(new MemoryStream(Recording(null, hudFirst: 2)), setup, out problem));
        Assert.Equal("no match setup in the file", problem);
    }

    [Fact]
    public void Garbage_and_a_cut_off_file_are_refused_and_never_throw()
    {
        var setup = new List<KeyValuePair<string, NetValue>>();
        Assert.False(RecordingPeek.TryReadSetup(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }), setup, out string problem));
        Assert.NotEmpty(problem);
        // The header is 37 bytes ("T2TSTRM1", the protocol, "test" twice with their lengths, the build, the time): cut in the first record.
        byte[] whole = Recording(Setup(0), hudFirst: 1);
        Assert.False(RecordingPeek.TryReadSetup(new MemoryStream(whole[..42]), setup, out problem));
        Assert.Equal("the file ends before its match setup", problem);
        Assert.Empty(setup);
        Assert.False(RecordingPeek.TryReadSetup(new MemoryStream(Array.Empty<byte>()), setup, out problem));
    }

    [Fact]
    public void A_file_that_can_t_be_opened_or_read_says_why_without_its_folder()
    {
        // The reason reaches the log (the stream player, the rehearsal, the replay tour): a file error's own message holds
        // the full path, and a path under a profile holds its user's name.
        string folder = Path.Combine(Path.GetTempPath(), $"t2t-missing-{Guid.NewGuid():N}");
        Assert.False(MatchRecording.TryLoad(Path.Combine(folder, "match.t2tstream"), out MatchRecording? recording, out string problem));
        Assert.Null(recording);
        Assert.Contains("match.t2tstream", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(folder), problem, StringComparison.Ordinal);

        Assert.False(MatchStreamReader.TryOpen(new Unreadable(), out _, out problem));
        Assert.Contains("match.t2tstream", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("Alex", problem, StringComparison.Ordinal);
        Assert.False(RecordingPeek.TryReadSetup(new Unreadable(), new List<KeyValuePair<string, NetValue>>(), out problem));
        Assert.DoesNotContain("Alex", problem, StringComparison.Ordinal);
    }

    private static List<KeyValuePair<string, NetValue>> Setup(int gameType) => new()
    {
        new(CampaignMatchValues.GameTypeKey, NetValue.FromInt(gameType)),
        new("home.team", NetValue.FromString("team_a")),
    };

    /// <summary>A stream with <paramref name="hudFirst"/> HUD records, then the setup (none when null), then one more HUD record.</summary>
    private static byte[] Recording(List<KeyValuePair<string, NetValue>>? setup, int hudFirst)
    {
        var output = new MemoryStream();
        var writer = new MatchStreamWriter(output, new MatchStreamHeader(ProtocolInfo.Version, "test", 1, 0, "test"));
        var buffer = new BitWriter();
        void Write(MessageType type, Channel channel, Action<BitWriter> payload)
        {
            buffer.Reset();
            new MessageHeader(type, 0, 0, 0).Write(buffer);
            payload(buffer);
            writer.Write(0, channel, buffer.AsSpan());
        }

        void Hud() => Write(MessageType.HudState, Channel.Events, w => HudStateMessage.Write(w, new[] { new KeyValuePair<string, NetValue>("clock", NetValue.FromInt(1)) }));
        for (int i = 0; i < hudFirst; i++)
        {
            Hud();
        }

        if (setup != null)
        {
            Write(MessageType.MatchSetup, Channel.Events, w => MatchSetupMessage.Write(w, MatchSetupMessage.Hash(setup), setup));
        }

        Hud();
        writer.Dispose();
        return output.ToArray();
    }

    /// <summary>A file whose every read fails as a network share's or a locked file's can, naming the file by its full path.</summary>
    private sealed class Unreadable : MemoryStream
    {
        private const string Message = @"The file 'C:\Users\Alex\Recordings\match.t2tstream' can't be read.";

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Message);

        public override int Read(Span<byte> buffer) => throw new IOException(Message);

        public override int ReadByte() => throw new IOException(Message);
    }
}
