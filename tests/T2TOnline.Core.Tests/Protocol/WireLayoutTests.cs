using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Input;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Snapshots;
using T2TOnline.Core.Stream;
using T2TOnline.Core.Tests.Campaign;

namespace T2TOnline.Core.Tests.Protocol;

/// <summary>
/// Every wire layout, pinned byte for byte: a friend on another build with the same <see cref="ProtocolInfo.Version"/>
/// reads exactly these bytes. A failure here means a layout changed: bump <see cref="ProtocolInfo.Version"/>, then update
/// the bytes (and <see cref="PinnedVersion"/>). The protocol's own pins (header, welcome, host view, follow report, a
/// neutral pad packet, acknowledgement, ready, pong) are in <c>SessionMessagesTests</c>; hello and reject are frozen there.
/// </summary>
/// <remarks>
/// Deflated payloads (run checkpoints, compressed snapshots, run history entries in progress batches) are pinned by
/// reading recorded bytes: the deflate stream a runtime writes may differ between .NET versions, but every version reads
/// the same stream the same way. Everything around them is pinned by writing.
/// </remarks>
public class WireLayoutTests
{
    /// <summary>The protocol version these bytes are.</summary>
    private const byte PinnedVersion = 9;

    private static readonly Guid SoundGuid = new("00112233-4455-6677-8899-aabbccddeeff");

    /// <summary>A keyframe as a protocol 4 host wrote it, deflated (its objects repeat): see <see cref="A_recorded_compressed_keyframe_still_reads"/>.</summary>
    private const string CompressedKeyframe =
        "03508DC6491583400044C1CF9E014E28690779212CB670832DA47069017DAA2ACFBDC0F585B956F55BFFDB7E9CA9A8F65251E3A5A2D64B459D978A7A2F15" +
        "0D5E2AFA78A9A878A968F452D1E4A5A2D94B7D01";

    /// <summary>The run checkpoint of <see cref="Checkpoint"/> as a protocol 4 host wrote it (each part deflated).</summary>
    private const string CheckpointMessage =
        "0500000002012616AB562A2ACD53B232D451CA4D2C50B2524AC402946A01020406636462660100BED77540AEC380E2";

    /// <summary>
    /// A progress batch carrying <see cref="SmallHistory"/> as a protocol 4 host wrote it (the entry's fields deflated), its first
    /// byte, the batch format, 2 since protocol 6 (the bytes after it are the same: no squad record in it).
    /// </summary>
    private const string HistoryBatchMessage =
        "0208070605040302010100000002000000050000000572756E2D3101053536E37EF2EA4E5492D29CD6AB3B2F6E9C7A5192838583639DECFF1DE765191D1A" +
        "425D1C5AB69AEF36D8B673AB890C031703101830010900";

    /// <summary>
    /// A run summary attachment for menu 28 carrying <see cref="SmallHistory"/>, as a protocol 4 host wrote it: serial, kind,
    /// format, the body's length, then the deflated body (the same deflate stream as in <see cref="HistoryBatchMessage"/>).
    /// </summary>
    private const string RunSummaryAttachment =
        "1C00000001013536E37EF2EA4E5492D29CD6AB3B2F6E9C7A5192838583639DECFF1DE765191D1A425D1C5AB69AEF36D8B673AB890C031703101830010900";

    /// <summary>What each layout's bytes are now: hex, or for long ones the length and FNV-1a hash.</summary>
    private static Dictionary<string, string> Pinned { get; } = new()
    {
        ["netvalue.null"] = "00",
        ["netvalue.flag"] = "11",
        ["netvalue.whole"] = "724D00",
        ["netvalue.single"] = "030000FC03",
        ["netvalue.text"] = "24102606",
        ["netvalue.vector2"] = "050000F8030000000C",
        ["netvalue.vector3"] = "060000F8030000000400000404",
        ["netvalue.rotation"] = "070000E0030000E80B0000F0030000F803",
        ["netvalue.reference"] = "C82A00",
        ["netvalue.blob"] = "3910203000",
        ["netvalue.colour"] = "0A0000F8030000F0030000E8030000F403",
        ["netvalue.double"] = "0B0000000000400004",
        ["netvalue.list"] = "0C10E59A600000807F48204CAC0000007F000000800D0000F00700000008000008E80000007C0000007D0100007E0000007F90552007020406140000F0070000E0070000D0070000E867010000000000088000",
        ["session.ping"] = "AC020807060504030201",
        ["session.leave"] = "02",
        ["session.pad-device"] = "02",
        ["session.pad-disconnected"] = "01",
        ["session.achievement"] = "054D455243590700000000",
        ["session.pad-frames"] = "443322113C000C010080E80318FCFF7F01800100FEFF1500000000000000C00040080000780080003000000000000200000000",
        ["session.keyboard-frames"] = "03443322119B000000001010000000000000000020000000006040000000000000000060000000000000000000000000000000",
        ["session.input-remaps-pad"] = "04000101020003022800200800A109025A1000",
        ["session.input-remaps-keyboard"] = "0101010207E28032E00E980340003974000C000C0420400043084080C0000604C4504010061804EC4000",
        ["session.prompt-names"] = "01010300050200000E52696768742053686F756C64657200000001104C65667420537469636B205269676874000000020F4C65667420537469636B204C656674003100000152020A4C6566742053686966740C4C65667420436F6E74726F6CFF03000D5269676874205472696767657200",
        ["stream.object-table"] = "02010905463A483A7806536B61746572030102AC02C801CA7300045075636B00",
        ["stream.object-table-types"] = "02010905463A483A7806536B61746572020102C801CA7300045075636B000204030201D0C0B0A000",
        ["stream.event-table"] = "02011347616D65706C61794576656E74732E476F616C020673636F726572047465616DAC02015800",
        ["stream.game-event"] = "AC020338300000403F148007",
        ["stream.sound-at"] = "05CE8844005411DD992166AAEE3277BBFF030000FE0000000001000001694CBDD5B9913585B9859D95C9B940B185E53DB9954DA1BDD105D101",
        ["stream.sound"] = "06CD8844005411DD992166AAEE3277BBFF07E001",
        ["stream.hud"] = "0205636C6F636B0300002FA43037F62657F682F6D6562604",
        ["stream.setup"] = "677ECF05DC1B86F0030D6D6F64652E67616D65547970652290405716D6E682F6D65646056D6F6F73650C63616D706169676E2E72756E8200",
        ["stream.end"] = "0807060504030201",
        ["stream.report"] = "A09C01B0EA01B817900102013C00",
        ["stream.rumble"] = "0302",
        ["stream.loop-start"] = "AC020003000010400000003F000008C1",
        ["stream.loop-move"] = "05010000003F000000C00000A040",
        ["stream.loop-stop"] = "80800402",
        ["stream.custom-content"] = "EA895B14B59D2861040C637573746F6D5F612E706E670301020203",
        ["stream.custom-ref"] = "197465616D3A393737316431366366376336643362623A742D31",
        ["snapshot.keyframe"] = "0543AC02C8AEE2046666663F01039A99993ECDCC8C3F333313C0CDCC6C40CDCCCC3DCDCC4CBE9A99993E6666663F33332542000003020691A2B3C4D5E60A0297A8800301F1",
        ["snapshot.delta"] = "0448AE0202B4B7E6046666663F01039A99993ECDCC8C3F333313C0CDCC6C40CDCCCC3DCDCC4CBE9A99993E6666663F3333254200010703030201080101E001000E03999A9BFC0202F1F2",
        ["campaign.host-view"] = "07000000036D61700100000002036163744240E0F646564603322C33",
        ["campaign.map-view"] = "08000000036D617002000000060361637442403016D6863500004841046E6F64653420C31243001746874607312C303B322C310372756EC49063063326434653D62257E7363057C64603332C32",
        ["campaign.map-view-entry"] = "0A000000036D617002000000090361637442403016D686350000484105656E7465723430C3227350E6465726E7F42608046E6F64653420C31243001746874607312C303B322C310372756EC49063063326434653D62257E7363057C64603332C320374697011",
        ["campaign.preview-view"] = "209 bytes, fnv1a dbd127865ae10176",
        ["campaign.follow-report"] = "050000000402000000026E6F",
        ["menu.opened"] = "01030000000B52656C69635265776172640F52656C69635265776172644D656E7509776F726C645F6D6170020205636F756E74424080F6D65616030AE4CAD8D2C60404D2C80841A82CCC8D28AD8CAE80ADCCAE8C4D0400",
        ["menu.updated"] = "02030000000B52656C69635265776172640F52656C69635265776172644D656E7509776F726C645F6D6170030000",
        ["menu.closed"] = "03030000000000",
        ["menu.closed-back"] = "03030000000102",
        ["menu.synced"] = "0702030000002C010000",
        ["menu.nav"] = "0300000001030100C8010443617264",
        ["menu.nav-none"] = "0300000000",
        ["menu.select"] = "03000000020572656C696301C801",
        ["menu.click"] = "03000000020572656C6963010102",
        ["menu.controller-screen"] = "410 bytes, fnv1a 165ba0ad5c1aa4e3",
        ["menu.controller-feedback"] = "03000000020563612E666201A302",
        ["menu.controller-exhibition"] = "507 bytes, fnv1a e0c63dce95332792",
        ["attachment.controller-content"] = "02247465616D3A303132333435363738396162636465663A7465616D2D6963652D6265617273266C6F676F3A666564636261393837363534333231303A637573746F6D5F62656172732E706E67",
        ["menu.controller-glyphs"] = "378 bytes, fnv1a c3c4ca47d5a39fcb",
        ["progress.batch"] = "02080706050403020103000000070000000C0000000572756E2D310701090201AC02050106010B0A0200004C41020401051084CAC2DC88D2CAE814A8E4D2C4C2D89AC2E6D6060210CEC2EADCE8D8CAE8060010B08A0000080C080400",
        ["menu.game-model"] = "652 bytes, fnv1a b167dd60e97aff71",
        ["menu.campaign-tip"] = "011F0000000B43616D706169676E5469700B43616D706169676E54697009776F726C645F6D61700104067469702E6964B43016D606179676E6D6140677409706E7325716470F5469705F43616D706169676E4D6170097469702E6C696E65738280409706E7421626B7160100",
        ["menu.glossary"] = "010600000008476C6F73736172790C476C6F73736172794D656E750D7374616E646172645F67616D65010506676C2E746162227070C6E64217263627060A676C2E656E7472696573A2BD2F8070C6E652E6462797272207676C2E726F7759323F0000",
        ["progress.run-history"] = "0BE4EADC5A62229C85D5B9D1B195D11908040808AE1DFFB8CF1D01408055444084B537BB30B6B9B5341C000A000000003002000000",
        ["progress.run-history-full"] = "426 bytes, fnv1a d012b864dfe8f7a2",
        ["progress.batch-gaps"] = "02080706050403020103000000080000000C0000000572756E2D310401130D01141001260716150B9ADEDEE6CA10020A1084CAC2DC88D2CAE80A001284CAC2DC4088D2CAE80400B00900",
        ["progress.entity-relic"] = "0105084265616E446965740500094265616E20446965740200D804",
        ["progress.entity-skater"] = "01000C6677642D6B6F76616C736B692B0A736B6174657249642D780C736B617465724E616D652D78040002050002000000000500020000000405000200000002050002000000000204000204000204000205000200000000020400020400020400020400020400020400",
        ["progress.status"] = "01080706050403020101030000002C010000",
        ["progress.resend"] = "01080706050403020105000000FFFFFFFF",
        ["progress.run-request"] = "0108070605040302010572756E2D31",
        ["progress.run-copy-not-kept"] = "0108070605040302010572756E2D3102",
        ["progress.batch-run-end"] = "02080706050403020103000000090000000C0000000572756E2D31050301086761756E746C65740300085845084B6F76616C736B690E0302086761756E746C657403000040410000090A01040A02116368616F734E6F64652F76697369745F31",
        ["attachment.event-choices"] = "508 bytes, fnv1a 99e3decfb592f69e",
        ["attachment.event-choices-gm"] = "1138 bytes, fnv1a 9fb6e6120521b263",
        ["attachment.event-choices-fan"] = "1751 bytes, fnv1a 619738e31925b1f5",
        ["attachment.event-choices-nurse"] = "2115 bytes, fnv1a ceeafe652850cbb1",
        ["campaign.text-recipes"] = "165 bytes, fnv1a c439179f20ea3600",
        ["campaign.toast-recipes"] = "177 bytes, fnv1a e09a48bbb49e3c4d",
        ["campaign.image-sources"] = "0011EAD2BEC2E8D8C2E600000000000002850000008400000084000000001A0300001A0100000000000000045498A30BA3FB4A1B7B739B030000180200000000000014020000141208784017C656E64637070000F80300000004000004040000082430F010000000F00700000008000008080000104880604298581A5B1A5D5AD91C0000E00F000000100000101000002010",
        ["campaign.free-agent-look"] = "811 bytes, fnv1a 207b5bf327d698c8",
        ["attachment.npc-line"] = "020002032386DEC2C6D088D2C2D8DECEEACAA0DEDED81AC6DEC2C6D0BEECD2E6D2E8BE680400",
        ["attachment.prompt-yes-no"] = "010135EC645EEAD25EE0E4DEDAE0E85EC6DEDCC6CAC8CA5EE8D2E8D8CA0266D8C9BCD4A5BDC0C9BDB5C1D1BD8CBDB98D959195BD9095CD8D057C382B732B930B637B197B73334B936B132861275397F6526657E646F752869746F7E2F676805200",
        ["attachment.prompt-recipe"] = "213 bytes, fnv1a fe5af587426171b1",
        ["attachment.prompt-notice"] = "0200038578C6DED8DEE47A46CCCC606060607CB2DEEAE440E4DEE6E8CAE440D2E6DC4EE840ECC2D8D2C85C785EC6DED8DEE47C14A0D2C6D640CCD2ECCA40E6D6C2E8CAE4E65C060A3C2D01",
        ["attachment.run-preview"] = "2569 bytes, fnv1a e5a09b11015587b1",
        ["attachment.run-preview-old-school"] = "2613 bytes, fnv1a 4a8bc073e6920ed8",
        ["attachment.run-preview-lineup"] = "2587 bytes, fnv1a 6cbab99d80c2c55e",
        ["attachment.unlock-states"] = "050F72656C69635F6265616E5F64696574020101010D74616C656E745F736E69706572010025320C6162696C6974795F64617368000000030D74616C656E745F736E69706572040132320DC3A971756970655FC3A974C3A90300000003096677642D6B69726279010505096677642D73616C6C790002070A6677642D6A6F686E6E79020001070F00010203040A",
        ["attachment.history-page"] = "02000C003C000BE4EADC5A62229C85D5B9D1B195D11908040808AE1DFFB8CF1D01408055444084B537BB30B6B9B5341C000A000000003002000000",
        ["campaign.title-view"] = "04000000057469746C650000000003057363656E65C43047172647F73537265756E6B6409746C75626544747F7E6460B636F6E74696E756552756E0A7469746C6547726F75704200",
        ["attachment.node-rewards"] = "0200030103010C6677642D6B6F76616C736B690405020B6677642D6167656E742D37000903096677642D6D6F6F7365",
        ["menu.speech-value"] = "030000000206737065656368010E",
        ["menu.event-cards"] = "0300000002056576656E740401",
        ["menu.canada-cards"] = "03000000020663616E6164610001",
        ["attachment.nurse-roster"] = "0812CCEEC85AC2DCCEEAE6941E10286030E796065726179000C71696D716B656262750607647D622D3F3C08002B33BB296191D1498DD91B5D0001480402042999BD99BDC58995C408434B937B7AFB630B780096677642D616E6775730400",
        ["attachment.nurse-outcome"] = "0812CCEEC85AC2DCCEEAE6941E10286030E796065726179000C71696D716B656262750607647D622D3F3C08002B33BB296191D1498DD91B5D0001480402042999BD99BDC58995C408434B937B7AFB630B780096677642D616E677573040904020E000C0600040000060E0000000000009C1F1A4CB9A5C195C9452872337B931B2B937B9024F7E6D614E616",
        ["attachment.nurse-unavailable"] = "13DCDE40E4DEE6E8CAE400",
        ["attachment.nurse-steps"] = "0402010503000302040408000C04080C1400100048A84A222AA30B4B639B1318300028009050954454461796C636273070001000B00100",
        ["stream.run-look"] = "725 bytes, fnv1a 7ce166b26bffb1a1",
        ["notification.queue-relic"] = "01C8010400000002095010627B7B23932A634B1B2330B04088EDED8D2C6041AE8C2D6DEC4B8CEDED8D8CE04BD65B9D1CC8DC5A185D99DC1CC859191DC84A0C088F980FD4DB5D991CCF8B980F4898195D991C48188859DA191A9D0B0000",
        ["notification.queue-injury"] = "169 bytes, fnv1a 9fdcb7bb81b375cf",
        ["notification.queue-recipes"] = "284 bytes, fnv1a 270c969f5d0bdfca",
        ["notification.pop"] = "022400000001",
        ["notification.clear"] = "03",
        ["lobby.board"] = "0101AC020001BE0A0402010773717561642D6102058089F8AD0D0104020400080706050403020104416C65780103000100FFFF00000118171615141312110353616D0E030100013A00000002282726252423222105C3846C6578040500020297001B000338373635343332310552696C65790001000003FFFF0000",
        ["lobby.board-empty"] = "010000000000000400000000000000000000",
        ["lobby.member"] = "0103AC02",
        ["hash.fnv"] = "25232284E49CF2CB8CEC01864CDC63AFE86739F771419485",
        ["hash.setup"] = "677ECF05DC1B86F0",
        ["hash.checkpoint"] = "BED77540AEC380E2",
    };

    /// <summary>Each layout, written from fixed values.</summary>
    private static Dictionary<string, Func<byte[]>> Writers { get; } = new()
    {
        // NetValue: 4 bits of kind, then the value (every event, HUD, setup, view and menu value is one).
        ["netvalue.null"] = () => Write(w => NetValue.Null.Write(w)),
        ["netvalue.flag"] = () => Write(w => NetValue.FromBool(true).Write(w)),
        ["netvalue.whole"] = () => Write(w => NetValue.FromInt(-300).Write(w)),
        ["netvalue.single"] = () => Write(w => NetValue.FromFloat(1.5f).Write(w)),
        ["netvalue.text"] = () => Write(w => NetValue.FromString("ab").Write(w)),
        ["netvalue.vector2"] = () => Write(w => NetValue.FromVector2(1, -2).Write(w)),
        ["netvalue.vector3"] = () => Write(w => NetValue.FromVector3(1, 2, 3).Write(w)),
        ["netvalue.rotation"] = () => Write(w => NetValue.FromQuaternion(0.125f, -0.25f, 0.5f, 1).Write(w)),
        ["netvalue.reference"] = () => Write(w => NetValue.FromRef(300).Write(w)),
        ["netvalue.blob"] = () => Write(w => NetValue.FromBytes(new byte[] { 1, 2, 3 }).Write(w)),
        ["netvalue.colour"] = () => Write(w => NetValue.FromColor(1, 0.5f, 0.25f, 0.75f).Write(w)),
        ["netvalue.double"] = () => Write(w => NetValue.FromDouble(2.5).Write(w)),
        ["netvalue.list"] = () => Write(w => NetValue.WriteList(w, EveryValue())),

        // Session and input.
        ["session.ping"] = () => Write(w => new PingMessage(300, 0x0102030405060708).Write(w)),
        ["session.leave"] = () => Write(w => new LeaveMessage(LeaveReason.TimedOut).Write(w)),
        ["session.pad-device"] = () => Write(w => new PadDeviceMessage(2).Write(w)),
        // Message 17's reason byte (optional: none, or one a reader doesn't know, reads as Unplugged).
        ["session.pad-disconnected"] = () => Write(w => new PadDisconnectedMessage(PadAwayReason.ChangingControls).Write(w)),
        ["session.achievement"] = () => Write(w => AchievementUnlockedMessage.Write(w, new AchievementMoment("MERCY", 7, false))),
        ["session.pad-frames"] = () => Write(w => PadInputCodec.Write(w, 0x11223344, new[]
        {
            new PadState(PadButtons.A | PadButtons.DPadRight, 0b10_0001, 1000, -1000, 32767, -32767, 0, 65535),
            new PadState(PadButtons.A | PadButtons.DPadRight, 0b10_0001, 1000, -1000, 32767, -32767, 0, 65535),
            new PadState(PadButtons.A | PadButtons.DPadRight, 0b10_0001, 0, 0, 0, 0, 0, 65535),
            new PadState(PadButtons.None, 0b10_0001, 0, 0, 0, 0, 7, 8),
            PadState.Neutral,
        })),
        ["session.keyboard-frames"] = () => Write(w =>
        {
            w.WriteByte(3); // the key table's generation
            KeyboardMouseCodec.Write(w, 40, 0x11223344, new[]
            {
                new KeyboardMouseState(0b1001, 0b01_0000_0001, 0),
                new KeyboardMouseState(0b1001, 0b01_0000_0001, 0),
                new KeyboardMouseState(1UL << 39, 0b01_0000_0001, 0b10UL << 14),
                KeyboardMouseState.Neutral,
            });
        }),
        ["session.input-remaps-pad"] = () => Write(w => InputRemapsMessage.Write(w, InputMessagesTests.PadSet())),
        ["session.input-remaps-keyboard"] = () => Write(w => InputRemapsMessage.Write(w, InputMessagesTests.KeyboardSet())),
        ["session.prompt-names"] = () => Write(w => PromptNamesMessage.Write(w, PromptNamesMessageTests.Sample())),

        // The match stream.
        ["stream.object-table"] = () => Write(w => ObjectTableMessage.Write(w, new[]
        {
            new ObjectEntry(1, -5, "F:H:x", "Skater", new ushort[] { 1, 2, 300 }),
            new ObjectEntry(200, 7397, string.Empty, "Puck"),
        })),
        ["stream.object-table-types"] = () => Write(w => ObjectTableMessage.Write(w, new[]
        {
            new ObjectEntry(1, -5, "F:H:x", "Skater", new ushort[] { 1, 2 }, new uint[] { 0x01020304, 0xA0B0C0D0 }),
            new ObjectEntry(200, 7397, string.Empty, "Puck"),
        })),
        ["stream.event-table"] = () => Write(w => EventTableMessage.Write(w, new[]
        {
            new EventEntry(1, "GameplayEvents.Goal", new[] { "scorer", "team" }),
            new EventEntry(300, "X", Array.Empty<string>()),
        })),
        ["stream.game-event"] = () => Write(w => GameEventMessage.Write(w, 300, new[] { NetValue.FromRef(3), NetValue.FromFloat(0.75f), NetValue.FromString("x") })),
        ["stream.sound-at"] = () => Write(w => new SoundCall(5, false, SoundGuid, true, 1, 2, 3, "SoundManager.PlayOneShotAt").Write(w)),
        ["stream.sound"] = () => Write(w => new SoundCall(6, true, SoundGuid, false, 0, 0, 0, "x").Write(w)),
        ["stream.hud"] = () => Write(w => HudStateMessage.Write(w, new[] { V("clock", NetValue.FromFloat(120)), V("score/home", NetValue.FromInt(2)) })),
        ["stream.setup"] = () => Write(w => MatchSetupMessage.Write(w, MatchSetupMessage.Hash(Setup()), Setup())),
        ["stream.end"] = () => Write(w => MatchEndMessage.Write(w, 0x0102030405060708)),
        ["stream.report"] = () => Write(w => StreamReportMessage.Write(w, new StreamReport(20_000, 30_000, 3_000, 144, 2, 1, 60, 0))),
        ["stream.rumble"] = () => Write(w => new RumbleMessage(3, 2).Write(w)),
        ["stream.loop-start"] = () => Write(w => SoundLoopMessage.Start(300, LoopKind.MagnetRebound, 2.25f, 0.5f, -8.5f).Write(w)),
        ["stream.loop-move"] = () => Write(w => SoundLoopMessage.Move(5, 0.5f, -2, 5).Write(w)),
        ["stream.loop-stop"] = () => Write(w => SoundLoopMessage.Stop(65_536).Write(w)),
        ["stream.custom-content"] = () => Write(w => CustomContentMessage.Write(w, new CustomItem(CustomContentKind.Logo, "custom_a.png", new byte[] { 1, 2, 3 }), 1)),
        ["stream.custom-ref"] = () => Write(w => w.WriteShortString(new CustomItem(CustomContentKind.Team, "t-1", new byte[] { 7 }).Ref.Format())),

        // Snapshots, uncompressed (the bodies are too short to deflate smaller): a keyframe, then a delta against it.
        ["snapshot.keyframe"] = () => Write(w => Assert.True(SnapshotCodec.Write(w, Keyframe(), null))),
        ["snapshot.delta"] = () => Write(w => Assert.True(SnapshotCodec.Write(w, Delta(), Keyframe()))),

        // The campaign: views (the map's and a preview's details included), follow reports, menus, progress.
        ["campaign.host-view"] = () => Write(w => HostViewMessage.Write(w, new HostView(7, "map", 1, new[] { V("node", NetValue.FromString("2,3")), V("act", NetValue.FromInt(2)) }))),
        ["campaign.map-view"] = () => Write(w => HostViewMessage.Write(w, new HostView(8, "map", 2, MapView().ToValues()))),
        // A map view with the host's node entry and the cursor node's tooltip (new named values: absent from the plain one above).
        ["campaign.map-view-entry"] = () => Write(w => HostViewMessage.Write(w, new HostView(10, "map", 2, (MapView() with { Entering = new NodeEntry(new MapPoint(3, 2), 4), TooltipShown = true }).ToValues()))),
        ["campaign.preview-view"] = () => Write(w => HostViewMessage.Write(w, new HostView(9, "preview", 2, PreviewView().ToValues()))),
        ["campaign.follow-report"] = () => Write(w => FollowReportMessage.Write(w, new FollowReport(5, FollowState.Failed, 2, "no"))),
        ["menu.opened"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Opened(Menu())))),
        ["menu.updated"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Updated(Menu().With(MenuData.Empty))))),
        ["menu.closed"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Closed(3)))),
        ["menu.closed-back"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Closed(3, MenuCloseReason.Back, MenuTransition.Loading)))),
        ["menu.synced"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Synced(new uint[] { 3, 300 })))),
        ["menu.nav"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Selected(3, MenuTarget.AtPath(new[] { 1, 0, 200 }, "Card"))))),
        ["menu.nav-none"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Selected(3, MenuTarget.None)))),
        ["menu.select"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.ValueSet(3, MenuTarget.AtItem("relic", 1), 200)))),
        ["menu.click"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Clicked(3, MenuTarget.AtItem("relic", 1), 1, 2)))),
        // The controller screen (row version 2, the real screen on friends): its settings and two players' rows; a feedback
        // step (a refused move's bump on player 2's icon).
        ["menu.controller-screen"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Opened(ControllerScreen())))),
        ["menu.controller-feedback"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.ValueSet(3, MenuTarget.AtItem(ControllerFeedback.Group, 1), Feedback())))),
        // Another player's own button names for their icon: a pad player's, a keyboard player's with modifier
        // keys, and one with a button nothing is bound to, as rows of the controller screen's data.
        ["menu.controller-glyphs"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Opened(ControllerGlyphsState())))),
        // The in-match menu's numbers (GameMenuModel format 3: the injury list's rows) and the glossary's values (its row's
        // place in its list, gl.rowY), as menu values of an open menu.
        ["menu.game-model"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Opened(GameMenuState())))),
        ["menu.glossary"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Opened(GlossaryState())))),
        // The host's campaign tip, a board item of its own (its id, sequence, lines and overlay).
        ["menu.campaign-tip"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Opened(
            new MenuState(31, CampaignTipData.Type, CampaignTipData.Type, "world_map", 1, CampaignTipTests.Sample().ToData()))))),
        ["progress.batch"] = () => Write(w => ProgressBatchMessage.Write(w, 0x0102030405060708, 3, ProgressBatchMessage.EncodeBody(
            new ProgressBatch(7, 12, "run-1", EveryProgressKindButHistory()))!)),
        ["progress.run-history"] = () => Write(w => SmallHistory().Write(w)),
        ["progress.run-history-full"] = () => Write(w => RunHistoryDataTests.Sample().Write(w)),
        ["progress.batch-gaps"] = () => Write(w => ProgressBatchMessage.Write(w, 0x0102030405060708, 3, ProgressBatchMessage.EncodeBody(
            new ProgressBatch(8, 12, "run-1", ProgressGapsKinds()))!)),
        ["progress.entity-relic"] = () => Write(w => RelicStat().Write(w)),
        ["progress.entity-skater"] = () => Write(w => EntityStatsTests.Skater().Write(w)),
        ["progress.status"] = () => Write(w => ProgressStatusMessage.Write(w, new ProgressStatus(0x0102030405060708, true, 3, 300))),
        ["progress.resend"] = () => Write(w => ProgressResendMessage.Write(w, new ProgressResend(0x0102030405060708, 5, ProgressResendMessage.ToNewest))),
        // A friend's request for a copy of a shared run, and the host's "not kept" (a copy's checkpoint is RunCheckpointMessage's).
        ["progress.run-request"] = () => Write(w => ProgressRunRequestMessage.Write(w, new ProgressRunRequest(0x0102030405060708, "run-1"))),
        ["progress.run-copy-not-kept"] = () => Write(w => Assert.True(ProgressRunCopyMessage.Write(w, new ProgressRunCopy(0x0102030405060708, "run-1", RunCopyStatus.NotKept, null)))),
        // Batch format 2 (protocol 6): a squad record with the run's best scorer, the run's end for the benchwarmers, the marks.
        ["progress.batch-run-end"] = () => Write(w => ProgressBatchMessage.Write(w, 0x0102030405060708, 3, ProgressBatchMessage.EncodeBody(
            new ProgressBatch(9, 12, "run-1", RunEndKinds()))!)),

        // Menu attachment bodies (uncompressed; message 87 deflates them): event choices format 3 (a Blademaster set, a
        // General Manager page with free agents and the first one's look, a Fan Number 1 set with previews, a Nurse set with
        // takeovers; texts with their recipes, images and a preview's icon with their sources), an NPC line, the two prompts
        // (format 2: each text with how it's made; a literal-key yes/no prompt with a localized button, a notice with the
        // host's texts).
        ["attachment.event-choices"] = () => Write(w => EventChoiceSetTests.Sample().Write(w)),
        ["attachment.event-choices-gm"] = () => Write(w => EventChoiceSetTests.GeneralManager().Write(w)),
        ["attachment.event-choices-fan"] = () => Write(w => EventChoiceSetTests.FanNumber1().Write(w)),
        ["attachment.event-choices-nurse"] = () => Write(w => EventChoiceSetTests.Nurse().Write(w)),
        // Event choices format 3's pieces alone: a text recipe of every op, an image source of every kind, a free agent's
        // version 2 (its look, birth date and number format after version 1's fields).
        ["campaign.text-recipes"] = () => Write(w =>
        {
            foreach (TextRecipe recipe in TextRecipeTests.EveryOp())
            {
                recipe.Write(w);
            }
        }),
        // The toast and prompt recipes' ops: a recipe of each, and the formats around them.
        ["campaign.toast-recipes"] = () => Write(w =>
        {
            foreach (TextRecipe recipe in TextRecipeTests.EveryToastOp())
            {
                recipe.Write(w);
            }
        }),
        ["campaign.image-sources"] = () => Write(w =>
        {
            foreach (ImageSource source in ImageSourceTests.EveryKind())
            {
                source.Write(w);
            }
        }),
        ["campaign.free-agent-look"] = () => Write(w => FreeAgentLookTests.WithLook().Write(w, FreeAgentData.Version2)),
        ["attachment.npc-line"] = () => Write(w => NpcLineDataTests.Sample().Write(w)),
        ["attachment.prompt-yes-no"] = () => Write(w => PromptDataTests.YesNo().Write(w)),
        ["attachment.prompt-recipe"] = () => Write(w => PromptDataTests.WithRecipe().Write(w)),
        ["attachment.prompt-notice"] = () => Write(w => PromptDataTests.Notice().Write(w)),

        // The run-start carrier (attachment kind 5): a superstar pick and the old school draft; the run team's look (a
        // custom content item's bytes).
        ["attachment.run-preview"] = () => Write(w => RunPreviewSetTests.SuperStarPick().Write(w)),
        ["attachment.run-preview-old-school"] = () => Write(w => RunPreviewSetTests.OldSchool().Write(w)),
        // The lineup with benchwarmers (format 2, added under protocol 5: format 1's fields, then the pool).
        ["attachment.run-preview-lineup"] = () => Write(w => RunPreviewSetTests.Lineup().Write(w)),

        // The title's screens on a friend: the unlockables' states (kind 6), a run history page (kind
        // 7), and the title view with the host's button group and highlighted button.
        ["attachment.unlock-states"] = () => Write(w => UnlockStatesDataTests.Sample().Write(w)),
        ["attachment.history-page"] = () => Write(w => (HistoryPageDataTests.Sample() with { Entry = SmallHistory() }).Write(w)),
        ["campaign.title-view"] = () => Write(w => HostViewMessage.Write(w, TitleViewTests.View(new TitleViewDetails((int)TitleGroup.Campaign, "continueRun")))),

        // The General Manager's rerolled node rewards (attachment kind 8), and the speech mirror's menu values: a speaker's
        // progress (group "speech", item = the speaker's slot, value = line key × 4 + stage: here Hatfreeze's line 3,
        // advanced) and an event menu's "cards in" state (group "event", item 4).
        ["attachment.node-rewards"] = () => Write(w => NodeRewardsDataTests.Sample().Write(w)),
        ["menu.speech-value"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.ValueSet(3, MenuTarget.AtItem(SpeechProgress.Group, 1), SpeechProgress.Line(3, SpeechStage.Advanced).Value)))),
        ["menu.event-cards"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.ValueSet(3, MenuTarget.AtItem("event", 4), 1)))),

        // The host's Canada curse cards are up (group "canada", item 0): a friend's dialogue runs out.
        ["menu.canada-cards"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.ValueSet(3, MenuTarget.AtItem("canada", 0), 1)))),
        ["stream.run-look"] = () => T2TOnline.Core.Tests.Session.RunLookTests.Sample().ToBytes(),

        // The host's run toasts (message 88): a relic trigger's toast, an injury's (every text and image kind), a
        // pop, a clear.
        ["notification.queue-relic"] = () => NotificationCopyTests.Encoded(NotificationEvent.Queued(NotificationCopyTests.Relic())),
        ["notification.queue-injury"] = () => NotificationCopyTests.Encoded(NotificationEvent.Queued(NotificationCopyTests.Injury())),
        ["notification.queue-recipes"] = () => NotificationCopyTests.Encoded(NotificationEvent.Queued(NotificationCopyTests.WithRecipes())),
        ["notification.pop"] = () => NotificationCopyTests.Encoded(NotificationEvent.Popped(0x24, true)),
        ["notification.clear"] = () => NotificationCopyTests.Encoded(NotificationEvent.Cleared()),

        // The Nurse's procedure screen (attachment kinds 9 and 10): the host's roster, again with its commit's
        // outcome (format 2), one that can't be sent, and a window of steps.
        ["attachment.nurse-roster"] = () => Write(w => NurseRosterDataTests.Sample().Write(w)),
        ["attachment.nurse-outcome"] = () => Write(w => NurseRosterDataTests.Committed().Write(w)),
        ["attachment.nurse-unavailable"] = () => Write(w => NurseRosterData.Unavailable("no roster").Write(w)),
        ["attachment.nurse-steps"] = () => Write(w => NurseStepsTests.Sample().Write(w)),

        // Play Now's controller screen (attachment kind 11): what its teams need of the host's custom content, and its
        // settings and teams as menu values of an open menu.
        ["attachment.controller-content"] = () => Write(w => ControllerExhibitionTests.Content().Write(w)),
        ["menu.controller-exhibition"] = () => Write(w => Assert.True(MenuMessages.Write(w, MenuEvent.Opened(new MenuState(4, "ControllerAssignment", "ControllerAssignmentMenu", "start_screen", 1,
            new MenuData(ControllerExhibitionTests.PlayNow(), new[] { ControllerScreenStateTests.Icon(0, side: 2, ready: true) })))))),

        // The native lobby (protocol 7): messages 9 and 10. The lobby data a host publishes (t2t.save and the other keys) is
        // pinned with its writer, in the Steam library's tests (LobbyDataLayoutTests).
        ["lobby.board"] = () => Write(w => LobbyMessagesTests.Sample().Write(w)),
        ["lobby.board-empty"] = () => Write(w => new LobbyBoardMessage().Write(w)),
        ["lobby.member"] = () => Write(w => new LobbyMemberMessage(true, LobbyMemberStage.Returning, 300).Write(w)),

        // Hashes both ends compute and compare: FNV-1a (with its published test vectors), a setup's, a checkpoint's.
        ["hash.fnv"] = () => Write(w =>
        {
            w.WriteUInt64(Fnv.Hash(ReadOnlySpan<byte>.Empty));
            w.WriteUInt64(Fnv.Hash("a"u8));
            w.WriteUInt64(Fnv.Hash("foobar"u8));
        }),
        ["hash.setup"] = () => Write(w => w.WriteUInt64(MatchSetupMessage.Hash(Setup()))),
        ["hash.checkpoint"] = () => Write(w => w.WriteUInt64(Checkpoint().Hash)),
    };

    [Fact]
    public void The_pins_are_for_this_protocol_version()
    {
        // Bumped the version? Then check every layout below against the new one and update the bytes and this number.
        Assert.Equal(PinnedVersion, ProtocolInfo.Version);
        Assert.Equal(Writers.Keys.OrderBy(k => k, StringComparer.Ordinal), Pinned.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_layout_is_pinned()
    {
        // Every one checked, and every one that changed named, with what it is now.
        List<string> changed = Writers.Where(p => Pinned[p.Key] != Describe(p.Value())).Select(p => $"[\"{p.Key}\"] = \"{Describe(p.Value())}\"").ToList();
        Assert.True(changed.Count == 0, "Layouts changed: bump ProtocolInfo.Version, then update their pins:\n" + string.Join("\n", changed));
    }

    [Fact]
    public void The_pinned_hashes_are_fnv1a()
    {
        // The published FNV-1a 64-bit vectors: the protocol's hash is the standard one.
        Assert.Equal(0xcbf29ce484222325UL, Fnv.Hash(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0xaf63dc4c8601ec8cUL, Fnv.Hash("a"u8));
        Assert.Equal(0x85944171f73967e8UL, Fnv.Hash("foobar"u8));
    }

    [Fact]
    public void Uncompressed_snapshots_say_so_and_read_back()
    {
        byte[] keyframe = Writers["snapshot.keyframe"]();
        byte[] delta = Writers["snapshot.delta"]();
        Assert.Equal(1 | 4, keyframe[0]); // keyframe, camera; not compressed
        Assert.Equal(4, delta[0]);
        var history = new SnapshotHistory();
        var reader = new BitReader(keyframe);
        Assert.True(SnapshotCodec.TryRead(ref reader, history, out SnapshotFrame first, out _));
        history.Add(first);
        reader = new BitReader(delta);
        Assert.True(SnapshotCodec.TryRead(ref reader, history, out SnapshotFrame second, out _));
        SnapshotFrame expected = Delta();
        Assert.Equal(expected.Objects.Keys, second.Objects.Keys);
        foreach (ushort id in expected.Objects.Keys)
        {
            Assert.Equal(expected.Objects[id], second.Objects[id]);
        }
    }

    [Fact]
    public void Every_wire_enum_keeps_its_values()
    {
        // Values are part of the protocol: an older peer refuses or misreads one it doesn't know. Adding one is a layout
        // change too, unless every older reader takes it (then say why where it's added).
        Assert.Equal("None=0,Hello=1,Welcome=2,Reject=3,Ping=4,Pong=5,Leave=6,StateHash=7,PadDevice=8,LobbyBoard=9,LobbyMember=10,PadInput=16,PadDisconnected=17,PadReconnected=18," +
            "KeyboardMouseInput=19,InputRemaps=20,OwnerState=21,PromptNames=22,Snapshot=32,Keyframe=33,HudState=34,GameEvent=35,SoundCall=36,SoundLoop=37," +
            "Spawn=38,Despawn=39,AuthorityToHost=40,AuthorityToOwner=41,ActionClaim=42,PickupClaim=43,ClaimResult=44,ObjectTable=45," +
            "EventTable=46,SnapshotAck=47,StreamReport=48,Rumble=49,MatchSetup=64,MatchReady=65,MatchEnd=66,CustomContent=67,MenuOpen=80,MenuNav=81,MenuSelect=82," +
            "MenuClick=83,HostView=84,RunCheckpoint=85,FollowReport=86,MenuAttachment=87,Notification=88,AchievementUnlocked=112,ProgressEvent=113,ProgressResend=114," +
            "ProgressStatus=115,ProgressRunRequest=116,ProgressRunCopy=117", Values<MessageType>());
        Assert.Equal("Realtime=0,Events=1,Bulk=2", Values<Channel>());
        Assert.Equal("Magnet=0,GravityWell=1,Magneto=2,MagnetRebound=3,MagnetSafeBounce=4,RetroRocket=5,ExplodingPuck=6,FusRoDahCharge=7", Values<LoopKind>());
        Assert.Equal("Start=0,Move=1,Stop=2", Values<SoundLoopOp>());
        Assert.Equal("Gamepad=0,KeyboardMouse=1", Values<InputKind>());
        Assert.Equal("Unknown=0,Menus=1,Match=2,Lobby=3,Playing=4", Values<SessionPhase>());
        Assert.Equal("Open=0,Counting=1,Starting=2,Playing=3,Returning=4", Values<LobbyPhase>());
        Assert.Equal("Campaign=0,QuickGame=1", Values<LobbyMode>());
        Assert.Equal("Open=0,Invited=1,Joining=2,Here=3,Loading=4,Reconnecting=5", Values<SeatStage>());
        Assert.Equal("None=0,PickingSave=1,PickingMode=2,Inviting=3,WaitingForEveryone=4", Values<HostActivity>());
        Assert.Equal("InLobby=0,Preparing=1,Playing=2,Returning=3", Values<LobbyMemberStage>());
        Assert.Equal("None=0,MatchWon=1,MatchLost=2,MatchTied=3,RunWon=4,RunOver=5,RunSaved=6,MatchScore=7", Values<LobbyResultKind>());
        Assert.Equal(1, LobbyBoardMessage.Format);
        Assert.Equal("None=0,ProtocolMismatch=1,GameBuildMismatch=2,SessionFull=3,NotInLobby=4,NoControllerSlot=5,UnsupportedInput=6," +
            "HostUnavailable=7,NotAFriend=8,Removed=9,FriendshipUnconfirmed=10", Values<RejectReason>());
        Assert.Equal("None=0,Quit=1,TimedOut=2,HostClosed=3,Removed=4,LeftLobby=5", Values<LeaveReason>());
        Assert.Equal("Unplugged=0,ChangingControls=1,InSettings=2", Values<PadAwayReason>());
        Assert.Equal("Null=0,Flag=1,Whole=2,Single32=3,Text=4,Vector2=5,Vector3=6,Rotation=7,Reference=8,Blob=9,Colour=10,Double64=11", Values<NetValueKind>());
        Assert.Equal("None=0,A=1,B=2,X=4,Y=8,LeftBumper=16,RightBumper=32,Back=64,Start=128,LeftStick=256,RightStick=512,DPadUp=1024," +
            "DPadDown=2048,DPadLeft=4096,DPadRight=8192,All=16383", Values<PadButtons>());
        Assert.Equal("GameDefaults=0,OwnMaps=1", Values<BindingSource>());
        Assert.Equal("PadButton=0,PadAxis=1,Key=2,MouseButton=3", Values<BindingElementKind>());
        Assert.Equal("None=0,Control=1,Alt=2,Shift=4,Command=8,All=15", Values<KeyModifiers>());
        Assert.Equal("Full=0,Positive=1,Negative=2", Values<BindingRange>());
        Assert.Equal("LeftStickX=0,LeftStickY=1,RightStickX=2,RightStickY=3,LeftTrigger=4,RightTrigger=5", Values<PadAxis>());
        Assert.Equal("None=0,WaitingForRun=1,Moving=2,Following=3,Failed=4", Values<FollowState>());
        Assert.Equal("None=0,Path=1,Item=2", Values<MenuTargetKind>());
        Assert.Equal("None=0,Opened=1,Updated=2,Closed=3,Selected=4,ValueSet=5,Clicked=6,Synced=7", Values<MenuEventKind>());
        Assert.Equal("Other=0,Back=1", Values<MenuCloseReason>());
        Assert.Equal("None=0,Progress=1,Loading=2,Regress=3", Values<MenuTransition>());
        Assert.Equal("None=0,RunSummary=1,EventChoices=2,NpcLine=3,Prompt=4,RunPreview=5,UnlockStates=6,HistoryPage=7,NodeRewards=8,NurseRoster=9,NurseSteps=10,ControllerContent=11", Values<MenuAttachmentKind>());
        Assert.Equal((1, 1), (MenuAttachment.UnlockStatesFormat, MenuAttachment.HistoryPageFormat));
        Assert.Equal("PressToStart=0,Main=1,Campaign=2,Play=3,Practice=4,Customize=5", Values<TitleGroup>());
        Assert.Equal("None=0,Fan=1,Chaos=2,SkaterTraining=3", Values<NodeRewardClass>());
        Assert.Equal("Started=0,Shown=1,Advanced=2", Values<SpeechStage>());
        Assert.Equal((1, "speech", 8, 255), (MenuAttachment.NodeRewardsFormat, SpeechProgress.Group, SpeechProgress.MaxSpeakers, SpeechProgress.MaxKey));
        Assert.Equal("None=0,OldSquad=1,Benchwarmer=2", Values<RunStartMenu>());
        Assert.Equal("None=0,Talent=1,Ability=2,Relic=3", Values<RunStartRewardKind>());
        Assert.Equal((3, 4, 1), (MenuAttachment.RunPreviewFormat, MenuAttachment.RunPreviewLineupFormat, RunLookData.Version));
        Assert.Equal("None=0,Open=1,Page=2,Reroll=3", Values<EventSetStep>());
        Assert.Equal("None=0,Relic=1,Talent=2,Ability=3,Preview=4", Values<EventObjectKind>());
        Assert.Equal("None=0,Team=1,Skater=2,FreeAgent=3", Values<EventObjectOwner>());
        Assert.Equal("None=0,MaxGames=1,MaxGoals=2,Injury=3,Random=4", Values<FreeAgentConditionKind>());
        Assert.Equal("None=0,FirstVisit=1,Visit=2,NoChoices=3,PoolEntry=4,Other=5", Values<NpcLineSource>());
        Assert.Equal("None=0,YesNo=1,Dismissable=2", Values<PromptKind>());
        Assert.Equal("Empty=0,Term=1,LocalizedTerm=2,Literal=3,Recipe=4", Values<PromptTextKind>());
        Assert.Equal((3, 1, 2), (MenuAttachment.EventChoicesFormat, MenuAttachment.NpcLineFormat, MenuAttachment.PromptFormat));
        Assert.Equal("None=0,Literal=1,Term=2,Format=3,Concat=4,ObjectName=5,ObjectDescription=6,StatName=7,NurseProcedureName=8,NurseCardDescription=9,TeamName=10,AssetName=11,InjuryName=12,LocalizedStatName=13", Values<TextOp>());
        Assert.Equal("None=0,Object1=1,Object2=2,AgentTalent=3,AgentAbility=4", Values<ChoiceObjectSlot>());
        Assert.Equal("Named=0,Event=1,ObjectIcon=2", Values<ImageSourceKind>());
        Assert.Equal((1, 2), (FreeAgentData.Version1, FreeAgentData.Version2));
        Assert.Equal(1, PromptNamesMessage.Format);
        Assert.Equal((1, 2, 1), (MenuAttachment.NurseRosterFormat, MenuAttachment.NurseOutcomeFormat, MenuAttachment.NurseStepsFormat));
        Assert.Equal(1, MenuAttachment.ControllerContentFormat);
        Assert.Equal("None=0,Confirm=1,Back=2,Direction=3,Press=4,Release=5,Focus=6,Auto=7,SkipReveal=8,Lineup=9", Values<NurseStepKind>());
        Assert.Equal("None=0,Run=1,RunStats=2", Values<RunCheckpointPart>());
        Assert.Equal("None=0,Team=1,Skater=2,Goalie=3,Logo=4,RunLogo=5,RunLook=6", Values<CustomContentKind>());
        Assert.Equal("None=0,Increment=1,IncrementCampaign=2,IncrementPlayNow=3,IncrementBy=4,IncrementCampaignBy=5,IncrementPlayNowBy=6," +
            "TrySetLarger=7,TrySetLargerCampaign=8,TrySetLargerPlayNow=9,TrySetSmaller=10,TrySetSmallerCampaign=11,TrySetSmallerPlayNow=12," +
            "ResetCampaign=13,ResetPlayNow=14,TrySetLargerFromStat=15,TrySetLargerCampaignFromStat=16,TrySetLargerPlayNowFromStat=17", Values<StatOp>());
        Assert.Equal("Skater=0,Goalie=1,Team=2,Talent=3,Ability=4,Relic=5", Values<EntityStatGroup>());
        Assert.Equal("None=0,Whole=1,Real=2", Values<StatValueKind>());
        Assert.Equal("None=0,Won=1,Lost=2", Values<SquadOutcome>());
        Assert.Equal("None=0,MatchEnded=1,RunWon=2,RewardAssigned=3,NewAct=4,RunFailed=5", Values<QuestStep>());
        Assert.Equal("None=0,Queue=1,Pop=2,Clear=3", Values<NotificationOp>());
        Assert.Equal("None=0,Run=1,Home=2,Visitor=3", Values<ToastTeam>());
        Assert.Equal("None=0,Relic=1,Talent=2,Ability=3", Values<ToastObjectKind>());
        Assert.Equal("None=0,Literal=1,Term=2,ObjectName=3,ObjectDescription=4,Recipe=5", Values<ToastTextKind>());
        Assert.Equal("None=0,ObjectIcon=1,Named=2", Values<ToastImageKind>());
        Assert.Equal("None=0,NurseOffered=1,DialogueSeen=2", Values<ProfileMark>());
        Assert.Equal("None=0,Copy=1,NotKept=2", Values<RunCopyStatus>());
        Assert.Equal((1, 1), (ProgressRunRequestMessage.Format, ProgressRunCopyMessage.Format));
        Assert.Equal(2, ProgressBatchMessage.Format);
    }

    [Fact]
    public void A_recorded_compressed_keyframe_still_reads()
    {
        var reader = new BitReader(Convert.FromHexString(CompressedKeyframe));
        Assert.True(SnapshotCodec.TryRead(ref reader, new SnapshotHistory(), out SnapshotFrame frame, out _));
        Assert.Equal((9u, 150_000L, 1f, 12), (frame.Sequence, frame.HostTimeMicros, frame.TimeScale, frame.Objects.Count));
        for (ushort id = 1; id <= 12; id++)
        {
            Assert.Equal(Enumerable.Range(0, 48).Select(i => (byte)(i % 8 == 0 ? id : 0x40 + (i % 8))), frame.Objects[id]);
        }
    }

    [Fact]
    public void A_recorded_run_checkpoint_still_reads()
    {
        var reader = new BitReader(Convert.FromHexString(CheckpointMessage));
        Assert.True(RunCheckpointMessage.TryRead(ref reader, out RunCheckpoint? read));
        RunCheckpoint expected = Checkpoint();
        Assert.Equal((expected.Sequence, expected.Hash), (read!.Sequence, read.Hash));
        Assert.Equal(expected.Get(RunCheckpointPart.Run), read.Get(RunCheckpointPart.Run));
        Assert.Equal(expected.Get(RunCheckpointPart.RunStats), read.Get(RunCheckpointPart.RunStats));
    }

    [Fact]
    public void A_recorded_progress_batch_with_a_run_history_entry_still_reads()
    {
        var reader = new BitReader(Convert.FromHexString(HistoryBatchMessage));
        Assert.True(ProgressBatchMessage.TryRead(ref reader, out ProgressMessage? read));
        Assert.Equal((0x0102030405060708UL, 1u, 2u, 5u, "run-1"), (read!.Session, read.EntitledFrom, read.Batch.Sequence, read.Batch.Checkpoint, read.Batch.Run));
        RunHistoryRecord history = Assert.IsType<RunHistoryRecord>(Assert.Single(read.Batch.Entries));
        Assert.Equal(Write(w => SmallHistory().Write(w)), Write(w => history.Entry.Write(w)));
    }

    [Fact]
    public void A_recorded_menu_attachment_still_reads()
    {
        var reader = new BitReader(Convert.FromHexString(RunSummaryAttachment));
        Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
        Assert.Equal((28u, MenuAttachmentKind.RunSummary, (byte)1), (read!.Serial, read.Kind, read.Format));
        Assert.True(read.TryGetRunSummary(out RunHistoryData? entry));
        Assert.Equal(Write(w => SmallHistory().Write(w)), Write(w => entry!.Write(w)));

        // What this runtime writes around the deflated body is the same, byte for byte: serial, kind, format, body length.
        byte[] written = Write(w => Assert.True(MenuAttachmentMessage.Write(w, MenuAttachment.RunSummary(28, SmallHistory()))));
        Assert.Equal(RunSummaryAttachment[..14], Convert.ToHexString(written[..7]));
    }

    private static string Values<T>()
        where T : struct, Enum =>
        string.Join(",", Enum.GetValues<T>().Select(v => $"{v}={Convert.ToInt64(v, CultureInfo.InvariantCulture)}"));

    private static byte[] Write(Action<BitWriter> write)
    {
        var writer = new BitWriter();
        write(writer);
        return writer.ToArray();
    }

    /// <summary>Hex up to 160 bytes; past that, the length and FNV-1a hash (a mismatch still names the layout).</summary>
    private static string Describe(byte[] bytes) =>
        bytes.Length <= 160 ? Convert.ToHexString(bytes) : string.Create(CultureInfo.InvariantCulture, $"{bytes.Length} bytes, fnv1a {Fnv.Hash(bytes):x16}");

    private static KeyValuePair<string, NetValue> V(string key, NetValue value) => new(key, value);

    private static List<NetValue> EveryValue() => new()
    {
        NetValue.Null, NetValue.FromBool(true), NetValue.FromInt(-300), NetValue.FromFloat(1.5f), NetValue.FromString("ab"),
        NetValue.FromVector2(1, -2), NetValue.FromVector3(1, 2, 3), NetValue.FromQuaternion(0.125f, -0.25f, 0.5f, 1), NetValue.FromRef(300),
        NetValue.FromBytes(new byte[] { 1, 2, 3 }), NetValue.FromColor(1, 0.5f, 0.25f, 0.75f), NetValue.FromDouble(2.5),
    };

    private static List<KeyValuePair<string, NetValue>> Setup() => new()
    {
        V("mode.gameType", NetValue.FromInt(1)),
        V("team.home", NetValue.FromString("moose")),
        V("campaign.run", NetValue.FromInt(4)),
    };

    private static SnapshotFrame Keyframe()
    {
        var frame = new SnapshotFrame
        {
            Sequence = 300,
            HostTimeMicros = 5_000_100,
            TimeScale = 0.9f,
            Camera = new CameraPose(1.1f, -2.3f, 3.7f, 0.1f, -0.2f, 0.3f, 0.9f, 41.3f, false),
        };
        frame.GroupScales.Add((3, 0.3f));
        frame.Objects[1] = new byte[] { 0x91, 0xA2, 0xB3, 0xC4, 0xD5, 0xE6 };
        frame.Objects[7] = new byte[] { 0x97, 0xA8 };
        frame.Objects[200] = new byte[] { 0xF1 };
        return frame;
    }

    private static SnapshotFrame Delta()
    {
        SnapshotFrame frame = Keyframe();
        frame.Sequence = 302;
        frame.HostTimeMicros = 5_033_434;
        frame.Objects[1] = new byte[] { 0x91, 0xA2, 0xBB, 0xC4, 0x35, 0xE6 }; // same length: XOR runs
        frame.Objects.Remove(7); // gone
        frame.Objects[9] = new byte[] { 0x99, 0x9A, 0x9B }; // new: in full
        frame.Objects[200] = new byte[] { 0xF1, 0xF2 }; // another length: in full
        return frame;
    }

    private static MapViewDetails MapView() =>
        new("9f0c2d4e-run", 2, new[] { new MapPoint(1, 0), new MapPoint(2, 1) }, new MapPoint(3, 2), 12.5f);

    private static PreviewViewDetails PreviewView() =>
        new(MapView() with { Selected = null, CameraX = null }, new MapPoint(3, 2), 1, new[] { "fwd-1", "fwd-2", string.Empty, "fwd-4", "def-5" }, "goalie-1", "BeanDiet", string.Empty, "YoyoShot");

    private static MenuState Menu() => new(3, "RelicReward", "RelicRewardMenu", "world_map", 2, new MenuData(
        new[] { V("count", NetValue.FromInt(2)), V("home", NetValue.FromBool(true)) },
        new[] { new MenuItem("relic", new[] { V("id", NetValue.FromString("BeanDiet")), V("level", NetValue.FromInt(1)) }) }));

    private static MenuState ControllerScreen() => new(4, "ControllerAssignmentCampaign", "ControllerAssignmentMenu", "standard_game", 1, new MenuData(
        ControllerScreenStateTests.Settings(ready: false),
        new[]
        {
            ControllerScreenStateTests.Icon(0, side: 2, position: 2, ready: true, keyboard: true),
            ControllerScreenStateTests.Icon(1, side: 0, position: 6, device: 3, steam: 76561199000000001UL),
        }));

    private static MenuState ControllerGlyphsState() => new(4, "ControllerAssignmentCampaign", "ControllerAssignmentMenu", "standard_game", 1, new MenuData(
        ControllerScreenStateTests.Settings(ready: false),
        new[]
        {
            ControllerScreenStateTests.Icon(1, side: 0, position: 6, device: 2, steam: 76561199000000001UL),
            ControllerGlyphs.ToItem(new ControllerGlyphRow(1, 2, new GlyphName?[] { new("Button B", Array.Empty<string>()), new("Button X", Array.Empty<string>()), GlyphName.Blank })),
            ControllerGlyphs.ToItem(new ControllerGlyphRow(2, 0, new GlyphName?[] { new("E", new[] { "Left Shift" }), null, new("Mouse Button 1", Array.Empty<string>()) })),
        }));

    private static int Feedback()
    {
        Assert.True(ControllerFeedback.TryEncode(3, true, 0, 1, out int value));
        return value;
    }

    private static MenuState GameMenuState()
    {
        var values = new List<KeyValuePair<string, NetValue>> { V("context", NetValue.FromString("Pause")) };
        Assert.True(GameMenuModelTests.WithInjuries().TryWrite(values, out string problem), problem);
        return new MenuState(4, "Game", "GameMenu", "standard_game", 1, new MenuData(values, Array.Empty<MenuItem>()));
    }

    private static MenuState GlossaryState() => new(6, "Glossary", "GlossaryMenu", "standard_game", 1, new MenuData(
        new[] { V("gl.tab", NetValue.FromInt(1)), V("gl.tabs", NetValue.FromInt(3)), V("gl.entries", NetValue.FromInt(0x5EED)), V("gl.entry", NetValue.FromInt(17)), V("gl.rowY", NetValue.FromInt(-250)) },
        Array.Empty<MenuItem>()));

    private static List<ProgressEntry> EveryProgressKindButHistory() => new()
    {
        new StatEntry(9, StatOp.IncrementCampaign, StatValue.None),
        new StatEntry(300, StatOp.IncrementCampaignBy, StatValue.Whole(3)),
        new StatEntry(11, StatOp.TrySetSmaller, StatValue.Real(12.75f)),
        new UnlockPassEntry(4, -1, true, new[] { "BeanDiet", "TribalMask" }),
        new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 3456.5f),
        new ComicsEntry(),
        new QuestEntry(QuestStep.NewAct, 2),
    };

    private static List<ProgressEntry> ProgressGapsKinds() => new()
    {
        new StatEntry(ProgressGameStats.CurrentWinStreak, StatOp.ResetCampaign, StatValue.None),
        new StatEntry(ProgressGameStats.LargestWinStreak, StatOp.TrySetLargerCampaignFromStat, StatValue.Whole(ProgressGameStats.CurrentWinStreak)),
        new StatTextEntry(ProgressGameStats.TeamWithMostGoalsInOneGame, ProgressGameStats.MostGoalsInOneGame, "Moose", true),
        RelicStat(),
    };

    private static List<ProgressEntry> RunEndKinds() => new()
    {
        new SquadEntry(SquadOutcome.Won, "gauntlet", 3, 3456.5f, "Kovalski", 14),
        new SquadEntry(SquadOutcome.Lost, "gauntlet", 3, 12f),
        new RunEndingEntry(),
        ProfileMarkEntry.NurseOffered(4),
        ProfileMarkEntry.DialogueSeen("chaosNode/visit_1"),
    };

    private static EntityStatEntry RelicStat() => new(true, false, EntityStatGroup.Relic, "BeanDiet", new[]
    {
        EntityStatValue.OfText(null), EntityStatValue.OfText("Bean Diet"), EntityStatValue.OfWhole(1), EntityStatValue.OfWhole(0), EntityStatValue.OfWhole(300),
    });

    private static RunHistoryData SmallHistory() => new()
    {
        RunId = "run-1",
        SquadId = "gauntlet",
        DifficultyLevel = 3,
        GameRule = 1,
        IsWin = true,
        TimestampUtc = 638_656_000_000_000_000,
        RunTime = 3456.25f,
        Scorers = new[] { new ScorerLine { SkaterId = "kovalski", Goals = 14, Assists = 5 } },
        SticksBroken = 12,
        InjuriesInflicted = -1,
    };

    private static RunCheckpoint Checkpoint() => new(5, new Dictionary<RunCheckpointPart, byte[]>
    {
        [RunCheckpointPart.Run] = "{\"run\":1,\"map\":\"aaaaaaaaaaaaaaaaaaaa\"}"u8.ToArray(),
        [RunCheckpointPart.RunStats] = new byte[] { 1, 2, 3, 4 },
    });
}
