using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Session;
using T2TOnline.Core.Stream;
using T2TOnline.Core.Tests.Session;

namespace T2TOnline.Core.Tests.Campaign;

/// <summary>
/// Every decoder the campaign messages added to the protocol (protocol 4: types 80-87, and what the friend's glue parses
/// out of a host view or a campaign match's setup), fed mutated real messages and noise: truncated, bit-flipped, with bytes
/// overwritten by extremes or huge var-ints, spliced, and plain random. None may throw, and whatever one accepts must hold
/// its limits: the counts, ranges, names and cleaned text the glue relies on.
/// </summary>
public class CampaignFuzzTests
{
    private const int Rounds = 3000;
    private const ulong HostId = 100;
    private const ulong FriendId = 200;
    private const long Ms = 1000;
    private static readonly SessionIdentity Identity = new(ProtocolInfo.Version, "0.6.0-m4", 0x1122334455667788);

    private static KeyValuePair<string, NetValue> V(string key, NetValue value) => new(key, value);

    [Fact]
    public void Run_checkpoints_never_throw_and_what_decodes_holds_its_limits()
    {
        string map = string.Concat(Enumerable.Repeat("{\"node\":{\"x\":3,\"y\":7}},", 60));
        byte[][] seeds =
        {
            Payload(w => RunCheckpointMessage.Write(w, new RunCheckpoint(7, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = Encoding.UTF8.GetBytes(map) }))),
            Payload(w => RunCheckpointMessage.Write(w, new RunCheckpoint(uint.MaxValue, new Dictionary<RunCheckpointPart, byte[]>
            {
                [RunCheckpointPart.Run] = Encoding.UTF8.GetBytes("{\"runData\":{}}"),
                [RunCheckpointPart.RunStats] = Array.Empty<byte>(),
            }))),
        };

        foreach (byte[] bytes in Fuzzed(seeds, new Random(85)))
        {
            var reader = new BitReader(bytes);
            if (!RunCheckpointMessage.TryRead(ref reader, out RunCheckpoint? checkpoint))
            {
                Assert.Null(checkpoint);
                continue;
            }

            Assert.NotEqual(0u, checkpoint!.Sequence);
            Assert.InRange(checkpoint.Parts.Count, 1, RunCheckpoint.MaxParts);
            Assert.All(checkpoint.Parts, part =>
            {
                Assert.True(RunCheckpoint.IsKnown(part.Key));
                Assert.InRange(part.Value.Length, 0, RunCheckpoint.MaxPartBytes);
            });
            Assert.Equal(RunCheckpoint.ComputeHash(checkpoint.Parts), checkpoint.Hash);
        }
    }

    [Fact]
    public void A_checkpoint_numbered_0_is_refused()
    {
        // 0 means "no checkpoint" in a view and "none applied" on a friend; the host never numbers one 0.
        byte[] valid = Payload(w => RunCheckpointMessage.Write(w, new RunCheckpoint(1, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = new byte[] { 1, 2, 3 } })));
        Assert.True(ReadCheckpoint(valid));
        byte[] zero = (byte[])valid.Clone();
        Array.Clear(zero, 0, 4);
        Assert.False(ReadCheckpoint(zero));
    }

    [Fact]
    public void Lengths_that_claim_more_than_the_message_holds_are_refused_before_anything_is_made()
    {
        // A part claiming the largest deflated length with ten bytes behind it.
        Assert.False(ReadCheckpoint(Payload(w =>
        {
            w.WriteUInt32(1);
            w.WriteByte(1);
            w.WriteByte((byte)RunCheckpointPart.Run);
            w.WriteVarUInt(100);
            w.WriteVarUInt(RunCheckpointMessage.MaxMessageBytes);
            w.WriteBytes(new byte[10]);
        })));

        // A view and a menu claiming 2^63 details or values, and a blob claiming its limit with nothing behind it.
        Assert.False(ReadView(Payload(w =>
        {
            w.WriteUInt32(1);
            w.WriteShortString("map");
            w.WriteUInt32(0);
            w.WriteVarUInt(ulong.MaxValue >> 1);
        })));
        Assert.Null(ReadMenu(MessageType.MenuOpen, Payload(w =>
        {
            w.WriteByte((byte)MenuEventKind.Opened);
            w.WriteUInt32(1);
            w.WriteShortString("Game");
            w.WriteShortString("GameMenu");
            w.WriteShortString(string.Empty);
            w.WriteVarUInt(0);
            w.WriteVarUInt(ulong.MaxValue >> 1);
        })));
        Assert.False(ReadView(Payload(w =>
        {
            w.WriteUInt32(1);
            w.WriteShortString("map");
            w.WriteUInt32(0);
            w.WriteVarUInt(1);
            w.WriteShortString("blob");
            w.WriteBits((ulong)NetValueKind.Blob, 4);
            w.WriteVarUInt(NetValue.MaxBytes);
        })));
    }

    [Fact]
    public void Host_views_never_throw_and_what_decodes_holds_its_limits()
    {
        var map = new MapViewDetails("2f1c0f9e-5b7d-4f0e-9a51-0f3b2c1d4e5f", 2, new[] { new MapPoint(3, 0), new MapPoint(2, 1) }, new MapPoint(1, 2), 412.5f);
        var preview = new PreviewViewDetails(map with { Selected = null, CameraX = null }, new MapPoint(1, 2), 1,
            new[] { "fwd_ace", string.Empty, "fwd_brute", "fwd_c", "def_d" }, "goalie_e", "relic_f", string.Empty, "ability_g");
        byte[][] seeds =
        {
            Payload(w => HostViewMessage.Write(w, new HostView(9, "map", 4, map.ToValues()))),
            Payload(w => HostViewMessage.Write(w, new HostView(10, "preview", 4, preview.ToValues()))),
            Payload(w => HostViewMessage.Write(w, new HostView(11, "menu", 0, new[]
            {
                V("title", NetValue.FromString("Relic reward — pick one")),
                V("blob", NetValue.FromBytes(new byte[] { 1, 2, 3 })),
                V("ref", NetValue.FromRef(7)),
                V("v", NetValue.FromVector3(1, 2, 3)),
            }))),
        };

        foreach (byte[] bytes in Fuzzed(seeds, new Random(84)))
        {
            var reader = new BitReader(bytes);
            if (!HostViewMessage.TryRead(ref reader, out HostView? view))
            {
                Assert.Null(view);
                continue;
            }

            Assert.True(HostViewMessage.IsName(view!.Screen));
            Assert.InRange(Encoding.UTF8.GetByteCount(view.Screen), 1, HostView.MaxScreenBytes);
            Assert.InRange(view.Values.Count, 0, HostView.MaxValues);
            Assert.Equal(view.Values.Count, view.Values.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count());
            foreach ((string key, NetValue value) in view.Values)
            {
                Assert.True(HostViewMessage.IsName(key));
                Assert.InRange(Encoding.UTF8.GetByteCount(key), 1, HostView.MaxKeyBytes);
                AssertValue(value);
            }

            // What the friend's glue reads out of a view: never throws, and what it takes is in range.
            _ = view.ToString();
            CheckScenes(view);

            // Written again (a view's hash is its written form), it still reads.
            var again = new BitReader(Payload(w => HostViewMessage.Write(w, view)));
            Assert.True(HostViewMessage.TryRead(ref again, out _));
        }
    }

    [Fact]
    public void Follow_reports_never_throw_and_what_decodes_holds_its_limits()
    {
        byte[][] seeds =
        {
            Payload(w => FollowReportMessage.Write(w, new FollowReport(5, FollowState.Failed, 3, "this map has no Elite node at 2,3"))),
            Payload(w => FollowReportMessage.Write(w, new FollowReport(uint.MaxValue, FollowState.Following, uint.MaxValue, string.Empty))),
        };

        foreach (byte[] bytes in Fuzzed(seeds, new Random(86)))
        {
            var reader = new BitReader(bytes);
            if (!FollowReportMessage.TryRead(ref reader, out FollowReport report))
            {
                continue;
            }

            Assert.InRange((byte)report.State, (byte)FollowState.None, (byte)FollowState.Failed);
            AssertClean(report.Reason);
            Assert.InRange(Encoding.UTF8.GetByteCount(report.Reason), 0, FollowReportMessage.MaxReasonBytes * 3);
        }
    }

    [Fact]
    public void Menu_messages_never_throw_and_what_decodes_holds_its_limits()
    {
        var data = new MenuData(
            new[] { V("rerollCount", NetValue.FromInt(2)), V("titleTerm", NetValue.FromString("Rewards/Relic")), V("isTeamHome", NetValue.FromBool(true)) },
            new[]
            {
                new MenuItem("relic", new[] { V("id", NetValue.FromString("goldenStick")), V("level", NetValue.FromInt(1)), V("stickers", NetValue.FromString("Speed:2,Checking:-1")) }),
                new MenuItem("choice", new[] { V("text", NetValue.FromString("Take the money é")), V("path", NetValue.FromString("2/0/1")), V("off", NetValue.FromBool(false)) }),
                new MenuItem("line", new[] { V("text", NetValue.FromString("Act 2")) }),
            });
        var state = new MenuState(3, "RelicRewardMenu", "RelicRewardMenu", "world_map", 4, data);
        MenuEvent[] events =
        {
            MenuEvent.Opened(state),
            MenuEvent.Updated(state.With(MenuData.Empty)),
            MenuEvent.Closed(3),
            MenuEvent.Selected(3, MenuTarget.AtPath(new[] { 2, 0, 31, 4095 }, "Card(Clone)")),
            MenuEvent.Selected(3, MenuTarget.None),
            MenuEvent.ValueSet(3, MenuTarget.AtItem("state", 1023), 7),
            MenuEvent.ValueSet(3, MenuTarget.AtPath(new[] { 1 }, "Difficulty"), 2),
            MenuEvent.Clicked(3, MenuTarget.AtItem("relic", 0), 255, 1),
        };
        MessageType[] types = { MessageType.MenuOpen, MessageType.MenuNav, MessageType.MenuSelect, MessageType.MenuClick };
        byte[][] seeds = events.Select(e => Payload(w => MenuMessages.Write(w, e))).ToArray();
        var mirror = new MenuMirror();
        var random = new Random(80);
        foreach (byte[] bytes in Fuzzed(seeds, new Random(81)))
        {
            MessageType type = types[random.Next(types.Length)];
            MenuEvent? read = ReadMenu(type, bytes);
            if (read == null)
            {
                continue;
            }

            Assert.Equal(type, read.Type);
            Assert.NotEqual(0u, read.Serial);
            AssertTarget(read.Target);
            if (read.Kind is MenuEventKind.Opened or MenuEventKind.Updated)
            {
                MenuState held = read.State!;
                Assert.Equal(read.Serial, held.Serial);
                Assert.True(MenuNames.IsName(held.Id, MenuState.MaxNameBytes));
                Assert.True(MenuNames.IsName(held.Type, MenuState.MaxNameBytes));
                Assert.True(held.Scene.Length == 0 || MenuNames.IsName(held.Scene, MenuState.MaxNameBytes));
                AssertValues(held.Data.Values, MenuData.MaxValues);
                Assert.InRange(held.Data.Items.Count, 0, MenuData.MaxItems);
                foreach (MenuItem item in held.Data.Items)
                {
                    Assert.True(MenuNames.IsName(item.Group, MenuData.MaxKeyBytes));
                    AssertValues(item.Values, MenuData.MaxItemValues);
                }
            }

            if (read.Kind == MenuEventKind.ValueSet)
            {
                Assert.NotEqual(MenuTargetKind.None, read.Target.Kind);
                Assert.InRange(read.Index, 0, MenuTarget.MaxIndex);
            }

            _ = read.ToString();

            // Written again it reads again, and the friend's mirror takes it and stays within its limits.
            Assert.NotNull(ReadMenu(read.Type, Payload(w => Assert.True(MenuMessages.Write(w, read)))));
            _ = mirror.Apply(read);
            Assert.InRange(mirror.Open.Count, 0, MenuBoard.MaxOpen);
            Assert.All(mirror.Open, m => Assert.InRange(m.Values.Count, 0, MenuBoard.MaxValuesPerMenu));
        }
    }

    [Fact]
    public void A_menu_payload_over_the_writers_limit_is_refused()
    {
        static MenuEvent Lines(int rows)
        {
            string text = new('x', 250);
            var items = Enumerable.Range(0, rows)
                .Select(i => new MenuItem("line", Enumerable.Range(0, MenuData.MaxItemValues).Select(k => V("t" + k, NetValue.FromString(text))).ToList()))
                .ToList();
            return MenuEvent.Opened(new MenuState(1, "RunEnd", "RunEndMenu", string.Empty, 0, new MenuData(Array.Empty<KeyValuePair<string, NetValue>>(), items)));
        }

        // Well formed but over the limit: the host never sends it, and a friend never takes it.
        var writer = new BitWriter();
        Assert.False(MenuMessages.Write(writer, Lines(MenuData.MaxItems)));
        Assert.True(writer.ByteLength > MenuMessages.MaxMessageBytes);
        Assert.Null(ReadMenu(MessageType.MenuOpen, writer.ToArray()));

        // Under it, it reads: as many full rows as surely fit (each value is at most 256 bytes written).
        int under = (MenuMessages.MaxMessageBytes / (MenuData.MaxItemValues * 256)) - 1;
        Assert.InRange(under, 1, MenuData.MaxItems - 1);
        Assert.NotNull(ReadMenu(MessageType.MenuOpen, Payload(w => MenuMessages.Write(w, Lines(under)))));
    }

    [Fact]
    public void Menu_attachments_never_throw_and_what_decodes_holds_its_limits()
    {
        RunHistoryData small = new() { RunId = "run-1", SquadId = "vanilla", IsWin = false, Scorers = new[] { new ScorerLine { SkaterId = "a", Goals = 2 } } };
        byte[][] seeds =
        {
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.RunSummary(28, RunHistoryDataTests.Sample()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.RunSummary(uint.MaxValue, small))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.EventChoices(13, EventChoiceSetTests.Sample()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.EventChoices(2, EventChoiceSetTests.Training()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.EventChoices(8, EventChoiceSetTests.GeneralManager()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.EventChoices(7, EventChoiceSetTests.FanNumber1()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.NpcLine(13, NpcLineDataTests.Sample()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.Prompt(21, PromptDataTests.YesNo()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.Prompt(22, PromptDataTests.Notice()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.RunPreview(30, RunPreviewSetTests.SuperStarPick()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.RunPreview(31, RunPreviewSetTests.OldSchool()))),
            Payload(w => MenuAttachmentMessage.Write(w, MenuAttachment.NodeRewards(32, NodeRewardsDataTests.Sample()))),
        };

        foreach (byte[] bytes in Fuzzed(seeds, new Random(870)))
        {
            var reader = new BitReader(bytes);
            if (!MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read))
            {
                Assert.Null(read);
                continue;
            }

            Assert.NotEqual(0u, read!.Serial);
            Assert.True(MenuAttachment.IsKnown(read.Kind));
            Assert.InRange(read.BodyLength, 1, MenuAttachment.MaxBodyBytes);
            switch (read.Kind)
            {
                case MenuAttachmentKind.RunSummary:
                    Assert.True(read.TryGetRunSummary(out RunHistoryData? entry));
                    Assert.InRange(entry!.Matches.Count, 0, RunHistoryData.MaxItems);
                    break;
                case MenuAttachmentKind.EventChoices:
                    Assert.True(read.TryGetEventChoices(out EventChoiceSet? set));
                    Assert.Null(set!.Problem());
                    break;
                case MenuAttachmentKind.NpcLine:
                    Assert.True(read.TryGetNpcLine(out NpcLineData? line));
                    Assert.Null(line!.Problem());
                    break;
                case MenuAttachmentKind.RunPreview:
                    Assert.True(read.TryGetRunPreview(out RunPreviewSet? preview));
                    Assert.Null(preview!.Problem());
                    break;
                case MenuAttachmentKind.NodeRewards:
                    Assert.True(read.TryGetNodeRewards(out NodeRewardsData? rewards));
                    Assert.Null(rewards!.Problem());
                    Assert.InRange(rewards.Entries.Count, 1, NodeRewardsData.MaxEntries);
                    break;
                default:
                    Assert.True(read.TryGetPrompt(out PromptData? prompt));
                    Assert.Null(prompt!.Problem());
                    break;
            }

            _ = read.ToString();

            // Written again it reads again, the same.
            var again = new BitReader(Payload(w => MenuAttachmentMessage.Write(w, read)));
            Assert.True(MenuAttachmentMessage.TryRead(ref again, out MenuAttachment? reread));
            Assert.Equal(read.ContentHash, reread!.ContentHash);
        }
    }

    [Fact]
    public void Map_points_paths_and_number_lists_from_the_host_never_throw_and_stay_in_range()
    {
        var random = new Random(87);
        const string alphabet = "0123456789,;-+ ?.x٣１";
        for (int i = 0; i < Rounds * 4; i++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, i % 7 == 0 ? 300 : 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            if (MapPoint.TryParse(text, out MapPoint point) && !point.IsNone)
            {
                Assert.InRange(point.X, 0, MapPoint.MaxCoordinate);
                Assert.InRange(point.Y, 0, MapPoint.MaxCoordinate);
            }

            if (MapPath.TryParse(text, out List<MapPoint> path))
            {
                Assert.InRange(path.Count, 0, MapPath.MaxPoints);
                Assert.All(path, p => Assert.False(p.IsNone));
            }

            int maxCount = random.Next(-2, 40);
            int min = random.Next(-5, 5);
            int max = min + random.Next(0, 300);
            if (CampaignMatchValues.TryParseList(text, maxCount, min, max, out int[] values))
            {
                Assert.InRange(values.Length, 0, Math.Max(0, Math.Min(maxCount, CampaignMatchValues.MaxListLength)));
                Assert.All(values, v => Assert.InRange(v, min, max));
            }

            if (CampaignMatchValues.TryParsePoint(text, 255, out int x, out int y))
            {
                Assert.InRange(x, 0, 255);
                Assert.InRange(y, 0, 255);
            }
        }

        Assert.False(CampaignMatchValues.TryParseList(null, 4, 0, 9, out _));
        Assert.False(MapPath.TryParse(null, out _));
    }

    [Fact]
    public void Run_files_from_anywhere_never_throw()
    {
        byte[] seed = Encoding.UTF8.GetBytes("{\"runData\":{\"__type\":\"RunDataModelV2,Assembly-CSharp\",\"value\":{\"squadId\":\"vanilla\",\"currentAct\":2,\"currentFloor\":5," +
            "\"runID\":\"abc\",\"isRunFailed\":false,\"currentMap\":\"{\\\"nodes\\\":[]}\"}},\"runStats\":{\"value\":{\"runTime\":12.5}}}");
        foreach (byte[] bytes in Fuzzed(new[] { seed }, new Random(88)))
        {
            _ = RunFileSummary.TryRead(bytes, out _);
            _ = RunFileSummary.LooksLikeJson(bytes);
        }
    }

    [Fact]
    public void Campaign_messages_through_the_sessions_never_throw_and_only_reach_the_link_from_the_right_peer()
    {
        var net = new FakeNetwork();
        var host = new HostSession(Identity, net.For(HostId));
        host.SetMembers(new[] { HostId, FriendId });
        var campaign = new CampaignHost(net.For(HostId));
        host.CampaignMessages = (sender, _, message, _) => campaign.Receive(sender, message);
        var mirror = new MenuMirror();
        var client = new CampaignClient(net.For(FriendId), HostId)
        {
            OtherMessages = (MessageType type, ref BitReader reader) => type switch
            {
                MessageType.ProgressEvent => false,
                MessageType.MenuAttachment => MenuAttachmentMessage.TryRead(ref reader, out _),
                MessageType.Notification => NotificationMessage.TryRead(ref reader, out _),
                _ => MenuMessages.TryRead(type, ref reader, out MenuEvent? read) && mirror.Apply(read!) != MenuMirrorChange.Ignored,
            },
        };
        var friend = new FriendSession(Identity, HostId, InputKind.Gamepad, 7, net.For(FriendId))
        {
            CampaignMessages = (sender, _, message, _) => client.Receive(sender, message),
        };
        Join(net, host, campaign, friend);

        // Real messages of each campaign type, from both ends, as seeds. Each fuzzed message keeps a campaign type (session
        // control types, a Leave above all, are the session tests' business and would end the session under test).
        MessageType[] types =
        {
            MessageType.MenuOpen, MessageType.MenuNav, MessageType.MenuSelect, MessageType.MenuClick, MessageType.MenuAttachment, MessageType.HostView,
            MessageType.RunCheckpoint, MessageType.FollowReport, MessageType.ProgressEvent, MessageType.AchievementUnlocked,
            MessageType.ProgressStatus, MessageType.ProgressResend, MessageType.Notification, MessageType.ProgressRunRequest, MessageType.ProgressRunCopy,
        };
        var seeds = new List<byte[]>
        {
            Message(MessageType.HostView, w => HostViewMessage.Write(w, new HostView(1, "map", 1, new[] { V("node", NetValue.FromString("1,2")) }))),
            Message(MessageType.RunCheckpoint, w => RunCheckpointMessage.Write(w, new RunCheckpoint(1, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = new byte[] { 9, 9 } }))),
            Message(MessageType.MenuOpen, w => MenuMessages.Write(w, MenuEvent.Opened(new MenuState(1, "Game", "GameMenu", string.Empty, 0, MenuData.Empty)))),
            Message(MessageType.MenuNav, w => MenuMessages.Write(w, MenuEvent.Selected(1, MenuTarget.AtPath(new[] { 0, 1 }, "Resume")))),
            Message(MessageType.MenuAttachment, w => MenuAttachmentMessage.Write(w, MenuAttachment.RunSummary(1, new RunHistoryData { RunId = "run-1" }))),
            Message(MessageType.FollowReport, w => FollowReportMessage.Write(w, new FollowReport(1, FollowState.Following, 1, string.Empty))),
            Message(MessageType.ProgressEvent, w => w.WriteVarUInt(3)),
            Message(MessageType.ProgressStatus, w => ProgressStatusMessage.Write(w, new ProgressStatus(5, true, 1, 2))),
            Message(MessageType.ProgressResend, w => ProgressResendMessage.Write(w, new ProgressResend(5, 1, 2))),
            Message(MessageType.Notification, w => NotificationMessage.Write(w, NotificationEvent.Queued(NotificationCopyTests.Relic()))),
            Message(MessageType.Notification, w => NotificationMessage.Write(w, NotificationEvent.Popped(4, true))),
            Message(MessageType.ProgressRunRequest, w => ProgressRunRequestMessage.Write(w, new ProgressRunRequest(5, "run-1"))),
            Message(MessageType.ProgressRunCopy, w => ProgressRunCopyMessage.Write(w, new ProgressRunCopy(5, "run-1", RunCopyStatus.Copy,
                new RunCheckpoint(2, new Dictionary<RunCheckpointPart, byte[]> { [RunCheckpointPart.Run] = new byte[] { 9, 9 } })))),
        };

        var random = new Random(83);
        long malformedBefore = friend.Link.Malformed;
        foreach (byte[] bytes in Fuzzed(seeds.ToArray(), new Random(82)))
        {
            if (bytes.Length > 0 && (random.Next(3) == 0 || Array.IndexOf(types, (MessageType)bytes[0]) < 0))
            {
                bytes[0] = (byte)types[random.Next(types.Length)];
            }

            friend.Receive(HostId, Channel.Events, bytes, net.Now);
            friend.Receive(FriendId + 1, Channel.Events, bytes, net.Now); // a stranger: never reaches the link
            host.Receive(FriendId, Channel.Events, bytes, net.Now);
            host.Receive(FriendId + 1, Channel.Events, bytes, net.Now);
            Assert.False(client.Receive(FriendId + 1, bytes));
            Assert.False(campaign.Receive(FriendId + 1, bytes));

            CampaignFollower follower = client.Follower;
            if (follower.Next(net.Now) != FollowStep.None)
            {
                // The glue's answers, as it would give them: arrived, or missed.
                if (random.Next(2) == 0 && follower.Target != null)
                {
                    follower.Arrived(follower.Target, follower.Checkpoints.Latest?.Sequence ?? 0);
                }
                else
                {
                    follower.Missed("couldn't", net.Now);
                    follower.RunUpdated(0, ok: false);
                }
            }

            Assert.InRange(mirror.Open.Count, 0, MenuBoard.MaxOpen);
            client.Update();
            net.Now += Ms;
        }

        Assert.Equal(FriendSessionState.Joined, friend.State);
        Assert.True(friend.Link.Malformed > malformedBefore);
        Assert.True(friend.FromStrangers >= Rounds);
        Assert.True(host.FromStrangers >= Rounds);
        Assert.Equal(new[] { FriendId }, campaign.Friends);
    }

    /// <summary>Each seed as it is, cut short at every length, and then <see cref="Rounds"/> mutations of the seeds and noise.</summary>
    private static IEnumerable<byte[]> Fuzzed(byte[][] seeds, Random random)
    {
        foreach (byte[] seed in seeds)
        {
            yield return (byte[])seed.Clone();
            for (int length = 0; length < seed.Length; length++)
            {
                yield return seed[..length];
            }
        }

        for (int i = 0; i < Rounds; i++)
        {
            byte[] bytes = (byte[])seeds[random.Next(seeds.Length)].Clone();
            switch (random.Next(8))
            {
                case 0:
                    bytes = new byte[random.Next(0, 160)];
                    random.NextBytes(bytes);
                    break;
                case 1:
                    for (int flips = random.Next(1, 5); flips > 0 && bytes.Length > 0; flips--)
                    {
                        bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                    }

                    break;
                case 2:
                    if (bytes.Length > 0)
                    {
                        bytes[random.Next(bytes.Length)] = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF }[random.Next(6)];
                    }

                    break;
                case 3:
                    // A huge var-int where a count or length may be.
                    if (bytes.Length > 0)
                    {
                        int at = random.Next(bytes.Length);
                        byte[] huge = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
                        bytes = bytes[..at].Concat(huge.Take(random.Next(1, huge.Length + 1))).Concat(bytes[at..]).ToArray();
                    }

                    break;
                case 4:
                    if (bytes.Length > 1)
                    {
                        int from = random.Next(bytes.Length);
                        bytes = bytes[..from].Concat(bytes[Math.Min(bytes.Length, from + random.Next(1, 8))..]).ToArray();
                    }

                    break;
                case 5:
                    byte[] tail = new byte[random.Next(1, 64)];
                    random.NextBytes(tail);
                    bytes = bytes.Concat(tail).ToArray();
                    break;
                case 6:
                    // Two messages spliced.
                    byte[] other = seeds[random.Next(seeds.Length)];
                    bytes = bytes[..random.Next(bytes.Length + 1)].Concat(other[random.Next(other.Length + 1)..]).ToArray();
                    break;
                default:
                    for (int flips = random.Next(1, 12); flips > 0 && bytes.Length > 0; flips--)
                    {
                        bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                    }

                    break;
            }

            yield return bytes;
        }
    }

    /// <summary>The map and preview readers the friend's followers use on a view: never throw; what they take is in range.</summary>
    private static void CheckScenes(HostView view)
    {
        if (MapViewDetails.TryRead(view, out MapViewDetails? map, out string problem))
        {
            Assert.InRange(map!.Act, 1, MapViewDetails.MaxAct);
            Assert.InRange(map.RunId.Length, 1, MapViewDetails.MaxRunIdLength);
            AssertPath(map.Path);
            Assert.Equal(map.Path.Count > 0 ? map.Path[^1] : MapPoint.None, map.Node);
            if (map.Selected is MapPoint selected)
            {
                AssertPoint(selected);
            }

            if (map.CameraX is float camera)
            {
                Assert.True(float.IsFinite(camera));
                Assert.InRange(camera, -MapViewDetails.MaxCameraX, MapViewDetails.MaxCameraX);
            }

            _ = map.ToValues();
        }
        else
        {
            Assert.NotNull(problem);
            AssertClean(problem);
        }

        if (PreviewViewDetails.TryRead(view, out PreviewViewDetails? preview, out problem))
        {
            AssertPoint(preview!.Target);
            Assert.InRange(preview.NodeType, 0, PreviewViewDetails.MaxMatchNodeType);
            Assert.Equal(PreviewViewDetails.Slots, preview.Opponent.Count);
            Assert.All(preview.Opponent.Append(preview.Goalie).Append(preview.RewardRelic).Append(preview.RewardTalent).Append(preview.RewardAbility), id =>
                Assert.True(id.Length == 0 || MapViewDetails.IsAssetId(id, PreviewViewDetails.MaxAssetIdLength), id));
            AssertPath(preview.Map.Path);
            _ = preview.ToValues();
        }
        else
        {
            AssertClean(problem);
        }
    }

    private static void AssertPath(IReadOnlyList<MapPoint> path)
    {
        Assert.InRange(path.Count, 0, MapPath.MaxPoints);
        Assert.All(path, AssertPoint);
    }

    private static void AssertPoint(MapPoint point)
    {
        Assert.InRange(point.X, 0, MapPoint.MaxCoordinate);
        Assert.InRange(point.Y, 0, MapPoint.MaxCoordinate);
    }

    private static void AssertTarget(MenuTarget target)
    {
        switch (target.Kind)
        {
            case MenuTargetKind.None:
                break;
            case MenuTargetKind.Path:
                Assert.InRange(target.Path.Count, 1, MenuTarget.MaxDepth);
                Assert.All(target.Path, step => Assert.InRange(step, 0, MenuTarget.MaxSiblingIndex));
                AssertClean(target.Leaf);
                break;
            case MenuTargetKind.Item:
                Assert.True(MenuNames.IsName(target.Group, MenuTarget.MaxGroupBytes));
                Assert.InRange(target.Index, 0, MenuTarget.MaxIndex);
                break;
            default:
                Assert.Fail($"Unknown target kind {target.Kind}.");
                break;
        }
    }

    private static void AssertValues(IReadOnlyList<KeyValuePair<string, NetValue>> values, int max)
    {
        Assert.InRange(values.Count, 0, max);
        Assert.Equal(values.Count, values.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count());
        foreach ((string key, NetValue value) in values)
        {
            Assert.True(MenuNames.IsName(key, MenuData.MaxKeyBytes));
            AssertValue(value);
        }
    }

    private static void AssertValue(NetValue value)
    {
        Assert.True(Enum.IsDefined(value.Kind));
        if (value.Kind == NetValueKind.Text)
        {
            AssertClean(value.Text!);
            Assert.InRange(value.Text!.Length, 0, NetValue.MaxStringBytes);
        }

        if (value.Kind == NetValueKind.Blob)
        {
            Assert.InRange(value.Data!.Length, 0, NetValue.MaxBytes);
        }

        if (value.Kind == NetValueKind.Reference)
        {
            Assert.InRange(value.WholeValue, 0, ushort.MaxValue);
        }
    }

    /// <summary>Text a friend may log or show: no control, bidi or private-use character and no lone surrogate.</summary>
    private static void AssertClean(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            Assert.False(char.IsSurrogate(c) || SafeText.IsUnsafe(c), $"U+{(int)c:X4} in \"{text}\"");
        }
    }

    private static void Join(FakeNetwork net, HostSession host, CampaignHost campaign, FriendSession friend)
    {
        friend.Start(net.Now);
        for (int i = 0; i < 1000 && friend.State != FriendSessionState.Joined; i++)
        {
            net.Now += Ms;
            net.Deliver((from, to, channel, bytes) =>
            {
                if (to == HostId)
                {
                    host.Receive(from, channel, bytes, net.Now);
                }
                else
                {
                    friend.Receive(from, channel, bytes, net.Now);
                }
            });
            host.Update(net.Now);
            friend.Update(net.Now);
            while (host.TryTakeEvent(out HostEvent e))
            {
                if (e.Kind == HostEventKind.JoinRequested)
                {
                    host.Accept(e.SteamId, 1, net.Now);
                }

                if (e.Kind is HostEventKind.Joined or HostEventKind.Rejoined)
                {
                    campaign.AddFriend(e.SteamId);
                }
            }
        }

        Assert.Equal(FriendSessionState.Joined, friend.State);
    }

    private static byte[] Payload(Action<BitWriter> write)
    {
        var writer = new BitWriter();
        write(writer);
        return writer.ToArray();
    }

    private static byte[] Payload(Func<BitWriter, bool> write)
    {
        var writer = new BitWriter();
        Assert.True(write(writer));
        return writer.ToArray();
    }

    private static byte[] Message(MessageType type, Action<BitWriter> write)
    {
        var writer = new BitWriter();
        new MessageHeader(type, 1, 0, 0).Write(writer);
        write(writer);
        return writer.ToArray();
    }

    private static byte[] Message(MessageType type, Func<BitWriter, bool> write) => Message(type, w => Assert.True(write(w)));

    private static bool ReadCheckpoint(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        return RunCheckpointMessage.TryRead(ref reader, out _);
    }

    private static bool ReadView(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        return HostViewMessage.TryRead(ref reader, out _);
    }

    private static MenuEvent? ReadMenu(MessageType type, byte[] bytes)
    {
        var reader = new BitReader(bytes);
        return MenuMessages.TryRead(type, ref reader, out MenuEvent? read) ? read : null;
    }
}
