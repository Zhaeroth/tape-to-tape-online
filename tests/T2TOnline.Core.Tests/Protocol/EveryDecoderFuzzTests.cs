using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Input;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>
/// Every decoder a peer's bytes reach, each fed noise biased to what gets past a first check: small numbers
/// where a count may be, the extremes, and plain random. None may throw, and none may take long: every count and size is capped
/// before anything is allocated or looped over. The grammar-aware fuzz of the campaign's messages is <c>CampaignFuzzTests</c>.
/// </summary>
public class EveryDecoderFuzzTests
{
    private const int Rounds = 6000;

    private delegate void Decoder(ref BitReader reader);

    public static IEnumerable<object[]> Decoders() => Table.Keys.Select(name => new object[] { name });

    private static readonly Dictionary<string, Decoder> Table = new()
    {
        ["ColorWire.Schemes"] = static (ref BitReader r) => ColorWire.TryReadOptionalSchemes(ref r, out _),
        ["ControllerContent"] = static (ref BitReader r) => ControllerContentData.TryRead(ref r, out _),
        ["EntityStat"] = static (ref BitReader r) => EntityStatEntry.TryRead(ref r, out _),
        ["EventChoiceSet"] = static (ref BitReader r) => EventChoiceSet.TryRead(ref r, out _),
        ["EventWire.Bounded"] = static (ref BitReader r) => EventWire.TryReadBounded(ref r, 256, out _),
        ["EventWire.Name"] = static (ref BitReader r) => EventWire.TryReadOptionalName(ref r, out _),
        ["EventWire.Recipe"] = static (ref BitReader r) => EventWire.TryReadOptionalRecipe(ref r, out _),
        ["EventWire.Source"] = static (ref BitReader r) => EventWire.TryReadOptionalSource(ref r, out _),
        ["EventWire.Text"] = static (ref BitReader r) => EventWire.TryReadOptionalText(ref r, out _),
        ["EventWire.Terms"] = static (ref BitReader r) => EventWire.TryReadTerms(ref r, out _),
        ["FollowReport"] = static (ref BitReader r) => FollowReportMessage.TryRead(ref r, out _),
        ["ForwardSnapshot"] = static (ref BitReader r) => ForwardSnapshot.TryRead(ref r, out _),
        ["GoalieLine"] = static (ref BitReader r) => GoalieLine.TryRead(ref r, out _),
        ["GoalieSnapshot"] = static (ref BitReader r) => GoalieSnapshot.TryRead(ref r, out _),
        ["HistoryPage"] = static (ref BitReader r) => HistoryPageData.TryRead(ref r, out _),
        ["HostView"] = static (ref BitReader r) => HostViewMessage.TryRead(ref r, out _),
        ["MatchLine"] = static (ref BitReader r) => MatchLine.TryRead(ref r, out _),
        ["MenuAttachment"] = static (ref BitReader r) => MenuAttachmentMessage.TryRead(ref r, out _),
        ["MenuOpen"] = static (ref BitReader r) => MenuMessages.TryRead(MessageType.MenuOpen, ref r, out _),
        ["MenuNav"] = static (ref BitReader r) => MenuMessages.TryRead(MessageType.MenuNav, ref r, out _),
        ["MenuSelect"] = static (ref BitReader r) => MenuMessages.TryRead(MessageType.MenuSelect, ref r, out _),
        ["MenuClick"] = static (ref BitReader r) => MenuMessages.TryRead(MessageType.MenuClick, ref r, out _),
        ["NodeRewards"] = static (ref BitReader r) => NodeRewardsData.TryRead(ref r, out _),
        ["NotificationCopy"] = static (ref BitReader r) => NotificationCopy.TryRead(ref r, out _),
        ["Notification"] = static (ref BitReader r) => NotificationMessage.TryRead(ref r, out _),
        ["NpcLine"] = static (ref BitReader r) => NpcLineData.TryRead(ref r, out _),
        ["NurseRoster"] = static (ref BitReader r) => NurseRosterData.TryRead(ref r, out _),
        ["NurseSteps"] = static (ref BitReader r) => NurseStepsData.TryRead(ref r, out _),
        ["ProgressBatch"] = static (ref BitReader r) => ProgressBatchMessage.TryRead(ref r, out _),
        ["ProgressBatch.Body"] = static (ref BitReader r) => ProgressBatchMessage.TryReadBody(ref r, out _),
        ["ProgressResend"] = static (ref BitReader r) => ProgressResendMessage.TryRead(ref r, out _),
        ["ProgressRunCopy"] = static (ref BitReader r) => ProgressRunCopyMessage.TryRead(ref r, out _),
        ["ProgressRunRequest"] = static (ref BitReader r) => ProgressRunRequestMessage.TryRead(ref r, out _),
        ["ProgressStatus"] = static (ref BitReader r) => ProgressStatusMessage.TryRead(ref r, out _),
        ["Prompt"] = static (ref BitReader r) => PromptData.TryRead(ref r, out _),
        ["RunCheckpoint"] = static (ref BitReader r) => RunCheckpointMessage.TryRead(ref r, out _),
        ["RunHistory"] = static (ref BitReader r) => RunHistoryData.TryRead(ref r, out _),
        ["RunPreviewSet"] = static (ref BitReader r) => RunPreviewSet.TryRead(ref r, out _),
        ["ScorerLine"] = static (ref BitReader r) => ScorerLine.TryRead(ref r, out _),
        ["TeamSnapshot"] = static (ref BitReader r) => TeamSnapshot.TryRead(ref r, out _),
        ["UnlockStates"] = static (ref BitReader r) => UnlockStatesData.TryRead(ref r, out _),
        ["Keyboard"] = static (ref BitReader r) => KeyboardMouseCodec.TryRead(ref r, 64, new KeyboardMouseState[64], out _, out _),
        ["Pad"] = static (ref BitReader r) => PadInputCodec.TryRead(ref r, new PadState[16], out _, out _),
        ["Achievement"] = static (ref BitReader r) => AchievementUnlockedMessage.TryRead(ref r, out _),
        ["CustomContent"] = static (ref BitReader r) => CustomContentMessage.TryRead(ref r, out _),
        ["EventTable"] = static (ref BitReader r) => EventTableMessage.TryRead(ref r, new List<EventEntry>()),
        ["GameEvent"] = static (ref BitReader r) => GameEventMessage.TryRead(ref r, out _, new List<NetValue>()),
        ["Hello"] = static (ref BitReader r) => HelloMessage.TryRead(ref r, out _),
        ["HudState"] = static (ref BitReader r) => HudStateMessage.TryRead(ref r, new List<KeyValuePair<string, NetValue>>()),
        ["InputRemaps"] = static (ref BitReader r) => InputRemapsMessage.TryRead(ref r, out _),
        ["Leave"] = static (ref BitReader r) => LeaveMessage.TryRead(ref r, out _),
        ["LobbyBoard"] = static (ref BitReader r) => LobbyBoardMessage.TryRead(ref r, out _),
        ["LobbyMember"] = static (ref BitReader r) => LobbyMemberMessage.TryRead(ref r, out _),
        ["MatchEnd"] = static (ref BitReader r) => MatchEndMessage.TryRead(ref r, out _),
        ["MatchReady"] = static (ref BitReader r) => MatchReadyMessage.TryRead(ref r, out _, out _, out _),
        ["MatchSetup"] = static (ref BitReader r) => MatchSetupMessage.TryRead(ref r, out _, new List<KeyValuePair<string, NetValue>>()),
        ["Header"] = static (ref BitReader r) => MessageHeader.TryRead(ref r, out _),
        ["ObjectTable"] = static (ref BitReader r) => ObjectTableMessage.TryRead(ref r, new List<ObjectEntry>()),
        ["PadDevice"] = static (ref BitReader r) => PadDeviceMessage.TryRead(ref r, out _),
        ["PadDisconnected"] = static (ref BitReader r) => PadDisconnectedMessage.TryRead(ref r, out _),
        ["Ping"] = static (ref BitReader r) => PingMessage.TryRead(ref r, out _),
        ["Pong"] = static (ref BitReader r) => PongMessage.TryRead(ref r, out _),
        ["PromptNames"] = static (ref BitReader r) => PromptNamesMessage.TryRead(ref r, out _),
        ["Reject"] = static (ref BitReader r) => RejectMessage.TryRead(ref r, out _),
        ["Rumble"] = static (ref BitReader r) => RumbleMessage.TryRead(ref r, out _),
        ["SnapshotAck"] = static (ref BitReader r) => SnapshotAckMessage.TryRead(ref r, out _, out _),
        ["SoundCall"] = static (ref BitReader r) => SoundCall.TryRead(ref r, out _),
        ["SoundLoop"] = static (ref BitReader r) => SoundLoopMessage.TryRead(ref r, out _),
        ["StreamReport"] = static (ref BitReader r) => StreamReportMessage.TryRead(ref r, out _),
        ["Welcome"] = static (ref BitReader r) => WelcomeMessage.TryRead(ref r, out _),
        ["NetValue"] = static (ref BitReader r) => NetValue.TryRead(ref r, out _),
        ["NetValue.List"] = static (ref BitReader r) => NetValue.TryReadList(ref r, new List<NetValue>()),
        ["Snapshot"] = static (ref BitReader r) => SnapshotCodec.TryRead(ref r, new SnapshotHistory(), out _, out _),
        ["XorRuns"] = static (ref BitReader r) => XorRuns.TryRead(ref r, new byte[64], out _),
    };

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Noise_never_throws_and_never_takes_long(string name)
    {
        Decoder decoder = Table[name];
        var random = new Random(name.Aggregate(11, (hash, c) => (hash * 31) + c));
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < Rounds; i++)
        {
            byte[] bytes = Noise(random, i);
            var reader = new BitReader(bytes);
            decoder(ref reader);
        }

        // A second for thousands of rounds of a few hundred bytes: a loop or an allocation a crafted count could drive shows here.
        Assert.InRange(clock.ElapsedMilliseconds, 0, 20_000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"runData\":{\"value\":null}}")]
    [InlineData("{\"runData\":{\"value\":{\"team\":[],\"currentAct\":\"x\",\"squadId\":7,\"runID\":{}}}}")]
    public void A_run_files_readers_never_throw(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _ = RunFileSummary.TryRead(bytes, out _);
        _ = RunTeamRoster.TryRead(bytes, out _);
    }

    [Fact]
    public void A_run_files_readers_refuse_deep_nesting_without_throwing()
    {
        byte[] deep = Encoding.UTF8.GetBytes(new string('[', 100_000));
        Assert.False(RunFileSummary.TryRead(deep, out _));
        Assert.False(RunTeamRoster.TryRead(deep, out _));
    }

    [Fact]
    public void Text_parsers_never_throw()
    {
        var random = new Random(1101);
        for (int i = 0; i < Rounds; i++)
        {
            string text = NoiseText(random);
            _ = CustomRef.TryParse(text, out _);
            _ = MapPoint.TryParse(text, out _);
            _ = MapPath.TryParse(text, out _);
            _ = CampaignMatchValues.TryParsePoint(text, 255, out _, out _);
            _ = CampaignMatchValues.TryParseList(text, 8, 0, 9, out _);
            _ = CampaignMatchValues.TryParseForward(text, 99, out _, out _);
            _ = HistoryLogoNames.TryParseContentName(text, out _);
            _ = HistoryLogoNames.TryParseFileName(text, out _);
            _ = KeyboardOwners.TryParse(text, out _);
            _ = RunLookData.TryRead(Encoding.UTF8.GetBytes(text), out _);
            _ = PngHeader.TryRead(Encoding.UTF8.GetBytes(text), out _, out _);
        }
    }

    private static byte[] Noise(Random random, int round)
    {
        byte[] bytes = new byte[random.Next(0, 320)];
        switch (round % 4)
        {
            case 0:
                random.NextBytes(bytes);
                break;
            case 1:
                // Small numbers everywhere: counts and lengths get past their caps, kinds are known ones.
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)random.Next(0, 8);
                }

                break;
            case 2:
                // Mostly small, some extremes.
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = random.Next(8) == 0 ? new byte[] { 0x7F, 0x80, 0xFE, 0xFF }[random.Next(4)] : (byte)random.Next(0, 32);
                }

                break;
            default:
                // Text-like: lengths followed by printable bytes.
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = random.Next(6) == 0 ? (byte)random.Next(0, 16) : (byte)random.Next(32, 127);
                }

                break;
        }

        return bytes;
    }

    private static string NoiseText(Random random)
    {
        string alphabet = "0123456789abcdefxyz:,;.-+/\\ \t\n\r\"'{}[]_$%#<>|custom_team.png" + (char)0 + (char)0x2028 + (char)0xFFFF + (char)0xD800;
        var text = new StringBuilder();
        int length = random.Next(0, 48);
        for (int i = 0; i < length; i++)
        {
            text.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return text.ToString();
    }
}
