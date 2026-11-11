using System;
using System.Collections.Generic;
using System.Linq;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Tests.Campaign;

public class NurseRosterDataTests
{
    /// <summary>A roster of four skaters: two offered an injury, one with replacement talents, both talents drawn.</summary>
    public static NurseRosterData Sample() => new()
    {
        Players = new[]
        {
            new NurseRosterPlayer { ForwardId = "fwd-angus", Overall = 74, Eligible = true, Offer = new NurseInjuryOffer(3, 2), Replacements = new[] { new NurseTalent("sniper", 1), new NurseTalent("playmaker", 2) } },
            new NurseRosterPlayer { ForwardId = "fwd-2", Overall = 61, Eligible = true, Offer = new NurseInjuryOffer(3, 3) },
            new NurseRosterPlayer { ForwardId = "fwd-3", Overall = 58, Eligible = false },
            new NurseRosterPlayer { ForwardId = "fwd-4", Overall = 0, Eligible = true, Replacements = Array.Empty<NurseTalent>() },
        },
        OfferType = 3,
        OfferGames = 2,
        InjuryTalent = new NurseTalent("enforcer", 1),
        ImmunityTalent = new NurseTalent("iron_man", 1),
        RequiredForwardId = "fwd-angus",
        Options = 4,
    };

    /// <summary>The sample with a sacrifice's outcome.</summary>
    public static NurseRosterData Committed() => Sample().WithOutcome(new NurseOutcome
    {
        Procedure = 4,
        Chips = new[] { new NurseSacrificeChip { Player = 1, Deltas = new[] { 0, 3, -2, 0, 1, 0, 0 } }, new NurseSacrificeChip { Player = 3, Deltas = new[] { 0, 0, 0, 0, 0, 0, 999 } } },
        Replacement = "Sniper",
        InjuryReward = "Enforcer",
        ImmunityReward = "IronMan",
        CloneBenchRoom = true,
    });

    public static byte[] Encoded(NurseRosterData roster)
    {
        var writer = new BitWriter();
        roster.Write(writer);
        return writer.ToArray();
    }

    private static NurseRosterData? Read(byte[] bytes)
    {
        var reader = new BitReader(bytes);
        bool ok = NurseRosterData.TryRead(ref reader, out NurseRosterData? roster);
        Assert.Equal(ok, roster != null);
        return ok && reader.RemainingBits < 8 ? roster : null;
    }

    [Fact]
    public void A_roster_round_trips_field_by_field()
    {
        NurseRosterData read = Read(Encoded(Sample()))!;
        Assert.NotNull(read);
        Assert.Null(NurseRosterData.FirstDifference(Sample(), read));
        Assert.Equal((MenuAttachment.NurseRosterFormat, 4, 3, 2, 4), (read.Format, read.Players.Count, read.OfferType, read.OfferGames, read.Options));
        Assert.Equal(new NurseTalent("enforcer", 1), read.InjuryTalent);
        Assert.Equal(new NurseTalent("iron_man", 1), read.ImmunityTalent);
        Assert.Equal("fwd-angus", read.RequiredForwardId);
        Assert.Equal(new[] { new NurseTalent("sniper", 1), new NurseTalent("playmaker", 2) }, read.ReplacementsOf("fwd-angus"));
        Assert.Null(read.ReplacementsOf("fwd-2"));
        Assert.Empty(read.ReplacementsOf("fwd-4")!);
        Assert.True(read.OffersInjury("fwd-2"));
        Assert.False(read.OffersInjury("fwd-3"));
        Assert.False(read.OffersInjury("nobody"));
        Assert.Null(read.Outcome);
        Assert.Contains("4 skater(s), injury type 3 offered to 2 for 2 game(s), 2 with replacement talents", read.ToString(), StringComparison.Ordinal);
        Assert.Equal(Encoded(Sample()), Encoded(read));
    }

    [Fact]
    public void An_outcome_round_trips_after_the_same_roster()
    {
        NurseRosterData read = Read(Encoded(Committed()))!;
        Assert.Equal(MenuAttachment.NurseOutcomeFormat, read.Format);
        Assert.Null(NurseRosterData.FirstDifference(Sample(), read)); // the roster is the same; the outcome isn't compared
        NurseOutcome outcome = read.Outcome!;
        Assert.Equal((4, 2, "Sniper", "Enforcer", "IronMan", true), (outcome.Procedure, outcome.Chips.Count, outcome.Replacement, outcome.InjuryReward, outcome.ImmunityReward, outcome.CloneBenchRoom));
        Assert.Equal(new[] { 0, 3, -2, 0, 1, 0, 0 }, outcome.Chips[0].Deltas);
        Assert.Equal((1, 3), (outcome.Chips[0].Player, outcome.Chips[1].Player));
        Assert.Contains("committed: procedure 4, 2 sacrifice chip(s), replacement Sniper", read.ToString(), StringComparison.Ordinal);

        // The roster's bytes come first, unchanged: format 2 is format 1's with the outcome after its flag.
        byte[] roster = Encoded(Sample());
        byte[] committed = Encoded(Committed());
        Assert.True(committed.Length > roster.Length);
        Assert.Equal(roster[..^1], committed[..(roster.Length - 1)]);
    }

    [Fact]
    public void A_roster_that_cant_be_sent_says_why_and_nothing_else()
    {
        NurseRosterData unavailable = NurseRosterData.Unavailable("the host's game couldn't read its roster (NullReferenceException)");
        NurseRosterData read = Read(Encoded(unavailable))!;
        Assert.Equal("the host's game couldn't read its roster (NullReferenceException)", read.Problem);
        Assert.Empty(read.Players);
        Assert.StartsWith("unavailable (", read.ToString(), StringComparison.Ordinal);
        Assert.Contains("isn't here", NurseRosterData.FirstDifference(read, Sample()), StringComparison.Ordinal);
        Assert.Contains("this PC's roster couldn't be read (x)", NurseRosterData.FirstDifference(Sample(), NurseRosterData.Unavailable("x")), StringComparison.Ordinal);

        // Long and empty reasons are cut to fit, or say so.
        Assert.Null(NurseRosterData.Unavailable(new string('é', 300)).Check());
        Assert.Equal("unknown", NurseRosterData.Unavailable(string.Empty).Problem);
        Assert.NotNull(new NurseRosterData { Problem = "x", Players = Sample().Players }.Check());
        Assert.NotNull(new NurseRosterData { Problem = "x", Outcome = Committed().Outcome }.Check());
    }

    [Fact]
    public void A_host_with_the_real_screen_switched_off_says_so_whole()
    {
        NurseRosterData read = Read(Encoded(NurseRosterData.Unavailable(NurseRosterData.SwitchedOff)))!;
        Assert.Equal(NurseRosterData.SwitchedOff, read.Problem);
        Assert.Empty(read.Players);
        Assert.Contains("NurseRealScreen", read.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hosts_fixed_reasons_read_on_a_friend_as_they_did()
    {
        // Each reason an honest host sends that is always the same words, after the wire: written whole, as before.
        foreach (string why in NurseRosterData.FixedProblems)
        {
            NurseRosterData read = Read(Encoded(NurseRosterData.Unavailable(why)))!;
            Assert.Equal(why, read.Problem);
            Assert.Equal($"unavailable ({why})", read.ToString());
            Assert.Equal($"the host's roster isn't here ({why})", NurseRosterData.FirstDifference(read, Sample()));
        }

        Assert.Equal($"unavailable ({NurseRosterData.UnknownProblem})", Read(Encoded(NurseRosterData.Unavailable(string.Empty)))!.ToString());
    }

    [Fact]
    public void A_hosts_reason_with_a_part_that_varies_keeps_its_opening_words_and_the_rests_length()
    {
        // Every opening words the host's reasons are made from: the friend's line says which kind of failure, nothing of the rest.
        foreach (string leadIn in NurseRosterData.ProblemLeadIns)
        {
            NurseRosterData unavailable = NurseRosterData.Unavailable(leadIn, "NullReferenceException: Object reference not set to an instance of an object.");
            NurseRosterData read = Read(Encoded(unavailable))!;
            Assert.StartsWith(leadIn, read.Problem, StringComparison.Ordinal);
            int rest = read.Problem!.Length - leadIn.Length;
            Assert.Equal($"unavailable ({leadIn}[{rest} more character(s)])", read.ToString());
            Assert.DoesNotContain("NullReference", read.ToString(), StringComparison.Ordinal);
        }

        // A reason is made only from this build's opening words.
        Assert.Throws<ArgumentException>(() => NurseRosterData.Unavailable("Riley Casey's roster (", "x"));
    }

    [Fact]
    public void A_crafted_hosts_reason_is_written_on_a_friend_by_its_length_only()
    {
        // A modified host chooses the text: a name in it never reaches the friend's log.
        NurseRosterData read = Read(Encoded(NurseRosterData.Unavailable("Riley Casey says hello")))!;
        Assert.Equal("unavailable (a reason of 22 character(s))", read.ToString());
        Assert.Equal("the host's roster isn't here (a reason of 22 character(s))", NurseRosterData.FirstDifference(read, Sample()));
        Assert.Equal("a reason of 22 character(s)", NurseRosterData.ProblemForLog(read.Problem));

        // One that only looks like a fixed reason, or adds to one, is no fixed reason.
        Assert.Equal("a reason of 70 character(s)", NurseRosterData.ProblemForLog(NurseRosterData.NotPatched[..^2] + " Riley"));
        Assert.Equal("(none)", NurseRosterData.ProblemForLog(null));
    }

    public static IEnumerable<object[]> Differences()
    {
        NurseRosterData host = Sample();
        NurseRosterData Change(Func<NurseRosterPlayer, int, NurseRosterPlayer> change) => new()
        {
            Players = host.Players.Select(change).ToList(),
            OfferType = host.OfferType,
            OfferGames = host.OfferGames,
            InjuryTalent = host.InjuryTalent,
            ImmunityTalent = host.ImmunityTalent,
            RequiredForwardId = host.RequiredForwardId,
            Options = host.Options,
        };
        NurseRosterPlayer Copy(NurseRosterPlayer p) => new() { ForwardId = p.ForwardId, Overall = p.Overall, Eligible = p.Eligible, Offer = p.Offer, Replacements = p.Replacements };

        yield return new object[] { "skater 1 (\"fwd-angus\") is \"other\" here", Change((p, i) => i == 0 ? new NurseRosterPlayer { ForwardId = "other", Overall = p.Overall, Eligible = p.Eligible, Offer = p.Offer, Replacements = p.Replacements } : Copy(p)) };
        yield return new object[] { "has overall 75 here, 74", Change((p, i) => i == 0 ? new NurseRosterPlayer { ForwardId = p.ForwardId, Overall = 75, Eligible = p.Eligible, Offer = p.Offer, Replacements = p.Replacements } : Copy(p)) };
        yield return new object[] { "skater 3 (\"fwd-3\") is eligible here", Change((p, i) => i == 2 ? new NurseRosterPlayer { ForwardId = p.ForwardId, Overall = p.Overall, Eligible = true } : Copy(p)) };
        yield return new object[] { "is offered no injury here, injury type 3 for 3 game(s)", Change((p, i) => i == 1 ? new NurseRosterPlayer { ForwardId = p.ForwardId, Overall = p.Overall, Eligible = p.Eligible } : Copy(p)) };
        yield return new object[] { "is offered injury type 3 for 4 game(s) here", Change((p, i) => i == 1 ? new NurseRosterPlayer { ForwardId = p.ForwardId, Overall = p.Overall, Eligible = p.Eligible, Offer = new NurseInjuryOffer(3, 4) } : Copy(p)) };
        yield return new object[] { "has replacements [sniper (level 1)] here", Change((p, i) => i == 0 ? new NurseRosterPlayer { ForwardId = p.ForwardId, Overall = p.Overall, Eligible = p.Eligible, Offer = p.Offer, Replacements = new[] { new NurseTalent("sniper", 1) } } : Copy(p)) };
        yield return new object[] { "has replacements none here", Change((p, i) => i == 3 ? new NurseRosterPlayer { ForwardId = p.ForwardId, Overall = p.Overall, Eligible = p.Eligible } : Copy(p)) };
        yield return new object[] { "3 skater(s) here, 4", new NurseRosterData { Players = host.Players.Take(3).ToList(), OfferType = 3, OfferGames = 2, Options = 4 } };
        yield return new object[] { "injury type is -1 here", new NurseRosterData { Players = host.Players, OfferType = -1, OfferGames = 2, InjuryTalent = host.InjuryTalent, ImmunityTalent = host.ImmunityTalent, RequiredForwardId = host.RequiredForwardId, Options = 4 } };
        yield return new object[] { "games are 3 here", new NurseRosterData { Players = host.Players, OfferType = 3, OfferGames = 3, InjuryTalent = host.InjuryTalent, ImmunityTalent = host.ImmunityTalent, RequiredForwardId = host.RequiredForwardId, Options = 4 } };
        yield return new object[] { "injury talent is none here", new NurseRosterData { Players = host.Players, OfferType = 3, OfferGames = 2, ImmunityTalent = host.ImmunityTalent, RequiredForwardId = host.RequiredForwardId, Options = 4 } };
        yield return new object[] { "immunity talent is iron_man (level 2) here", new NurseRosterData { Players = host.Players, OfferType = 3, OfferGames = 2, InjuryTalent = host.InjuryTalent, ImmunityTalent = new NurseTalent("iron_man", 2), RequiredForwardId = host.RequiredForwardId, Options = 4 } };
        yield return new object[] { "required skater differs", new NurseRosterData { Players = host.Players, OfferType = 3, OfferGames = 2, InjuryTalent = host.InjuryTalent, ImmunityTalent = host.ImmunityTalent, Options = 4 } };
        yield return new object[] { "first stage has 5 option(s) here, 4", new NurseRosterData { Players = host.Players, OfferType = 3, OfferGames = 2, InjuryTalent = host.InjuryTalent, ImmunityTalent = host.ImmunityTalent, RequiredForwardId = host.RequiredForwardId, Options = 5 } };
    }

    [Theory]
    [MemberData(nameof(Differences))]
    public void A_rebuilt_roster_that_differs_says_its_first_difference(string expected, NurseRosterData here)
    {
        string? difference = NurseRosterData.FirstDifference(Sample(), here);
        Assert.NotNull(difference);
        Assert.Contains(expected, difference, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> OutOfLimits()
    {
        NurseRosterPlayer Skater(int overall = 1, NurseInjuryOffer? offer = null, IReadOnlyList<NurseTalent>? replacements = null, string id = "f") =>
            new() { ForwardId = id, Overall = overall, Offer = offer, Replacements = replacements };

        yield return new object[] { "too many skaters", new NurseRosterData { Players = Enumerable.Range(0, NurseRosterData.MaxPlayers + 1).Select(i => Skater(id: $"f{i}")).ToList() } };
        yield return new object[] { "a missing skater", new NurseRosterData { Players = new NurseRosterPlayer[] { null! } } };
        yield return new object[] { "an empty id", new NurseRosterData { Players = new[] { Skater(id: string.Empty) } } };
        yield return new object[] { "a negative overall", new NurseRosterData { Players = new[] { Skater(overall: -1) } } };
        yield return new object[] { "an overall past the cap", new NurseRosterData { Players = new[] { Skater(overall: 1000) } } };
        yield return new object[] { "an injury type past the cap", new NurseRosterData { Players = new[] { Skater(offer: new NurseInjuryOffer(64, 1)) } } };
        yield return new object[] { "a negative injury's games", new NurseRosterData { Players = new[] { Skater(offer: new NurseInjuryOffer(1, -1)) } } };
        yield return new object[] { "too many replacements", new NurseRosterData { Players = new[] { Skater(replacements: Enumerable.Repeat(new NurseTalent("t", 1), 9).ToList()) } } };
        yield return new object[] { "a replacement past the level cap", new NurseRosterData { Players = new[] { Skater(replacements: new[] { new NurseTalent("t", 100) }) } } };
        yield return new object[] { "a replacement with no id", new NurseRosterData { Players = new[] { Skater(replacements: new[] { new NurseTalent(string.Empty, 1) }) } } };
        yield return new object[] { "an offer type below none", new NurseRosterData { OfferType = -2 } };
        yield return new object[] { "games past the cap", new NurseRosterData { OfferGames = 1000 } };
        yield return new object[] { "negative options", new NurseRosterData { Options = -1 } };
        yield return new object[] { "a talent with a line break", new NurseRosterData { InjuryTalent = new NurseTalent("a\nb", 1) } };
        yield return new object[] { "an empty required skater", new NurseRosterData { RequiredForwardId = string.Empty } };
        yield return new object[] { "an outcome's procedure past the cap", new NurseRosterData { Outcome = new NurseOutcome { Procedure = 64 } } };
        yield return new object[] { "a chip for a skater past the cap", new NurseRosterData { Outcome = new NurseOutcome { Chips = new[] { new NurseSacrificeChip { Player = 16 } } } } };
        yield return new object[] { "a delta past the cap", new NurseRosterData { Outcome = new NurseOutcome { Chips = new[] { new NurseSacrificeChip { Deltas = new[] { -1000 } } } } } };
        yield return new object[] { "too many deltas", new NurseRosterData { Outcome = new NurseOutcome { Chips = new[] { new NurseSacrificeChip { Deltas = new int[17] } } } } };
        yield return new object[] { "too many chips", new NurseRosterData { Outcome = new NurseOutcome { Chips = Enumerable.Range(0, 17).Select(_ => new NurseSacrificeChip()).ToList() } } };
        yield return new object[] { "an empty replacement identity", new NurseRosterData { Outcome = new NurseOutcome { Replacement = string.Empty } } };
    }

    [Theory]
    [MemberData(nameof(OutOfLimits))]
    public void Rosters_out_of_their_limits_are_refused_when_written_and_never_sent(string what, NurseRosterData roster)
    {
        Assert.NotNull(roster.Check());
        Assert.ThrowsAny<ArgumentException>(() => roster.Write(new BitWriter()));
        Assert.ThrowsAny<ArgumentException>(() => MenuAttachment.NurseRoster(3, roster));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void The_largest_roster_fits_and_reads_back()
    {
        string id = new('i', EventChoiceSet.MaxNameBytes);
        var largest = new NurseRosterData
        {
            Players = Enumerable.Range(0, NurseRosterData.MaxPlayers).Select(i => new NurseRosterPlayer
            {
                ForwardId = id[..^3] + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                Overall = NurseRosterData.MaxNumber,
                Eligible = true,
                Offer = new NurseInjuryOffer(NurseRosterData.MaxInjuryType, NurseRosterData.MaxNumber),
                Replacements = Enumerable.Repeat(new NurseTalent(id, NurseRosterData.MaxLevel), NurseRosterData.MaxReplacements).ToList(),
            }).ToList(),
            OfferType = NurseRosterData.MaxInjuryType,
            OfferGames = NurseRosterData.MaxNumber,
            InjuryTalent = new NurseTalent(id, NurseRosterData.MaxLevel),
            ImmunityTalent = new NurseTalent(id, NurseRosterData.MaxLevel),
            RequiredForwardId = id,
            Options = NurseRosterData.MaxNumber,
            Outcome = new NurseOutcome
            {
                Procedure = NurseRosterData.MaxProcedure,
                Chips = Enumerable.Range(0, NurseRosterData.MaxChips).Select(i => new NurseSacrificeChip { Player = i, Deltas = Enumerable.Repeat(-NurseRosterData.MaxDelta, NurseRosterData.MaxChips).ToList() }).ToList(),
                Replacement = id,
                InjuryReward = id,
                ImmunityReward = id,
            },
        };
        Assert.Null(largest.Check());
        byte[] bytes = Encoded(largest);
        Assert.True(bytes.Length < MenuAttachment.MaxBodyBytes);
        Assert.Equal(bytes, Encoded(Read(bytes)!));
        Assert.True(MenuAttachment.NurseRoster(9, largest).TryGetNurseRoster(out NurseRosterData? attached));
        Assert.Equal(bytes, Encoded(attached!));
    }

    [Fact]
    public void Crafted_rosters_that_break_a_rule_are_refused()
    {
        byte[] good = Encoded(Sample());
        Assert.NotNull(Read(good));

        // Cut short at every length; something after it.
        for (int length = 0; length < good.Length; length++)
        {
            Assert.Null(Read(good[..length]));
        }

        Assert.Null(Read(good.Concat(new byte[] { 0 }).ToArray()));

        // More skaters than the cap; an offer type past the cap; a problem form with an outcome after it.
        Assert.Null(Read(Crafted(w => w.WriteVarUInt(NurseRosterData.MaxPlayers + 1))));
        Assert.Null(Read(Crafted(w =>
        {
            w.WriteVarUInt(0);
            w.WriteByte(NurseRosterData.MaxInjuryType + 2);
            w.WriteVarUInt(0);
            w.WriteBool(false);
            w.WriteBool(false);
            w.WriteBool(false);
            w.WriteVarUInt(0);
            w.WriteBool(false);
        })));
        Assert.NotNull(Read(Crafted(w =>
        {
            w.WriteVarUInt(0);
            w.WriteByte(NurseRosterData.MaxInjuryType + 1);
            w.WriteVarUInt(0);
            w.WriteBool(false);
            w.WriteBool(false);
            w.WriteBool(false);
            w.WriteVarUInt(0);
            w.WriteBool(false);
        })));
        var problem = new BitWriter();
        problem.WriteBool(true);
        problem.WriteVarUInt(1);
        problem.WriteBytes("x"u8);
        problem.WriteBool(true);
        Assert.Null(Read(problem.ToArray()));

        static byte[] Crafted(Action<BitWriter> body)
        {
            var writer = new BitWriter();
            writer.WriteBool(false);
            body(writer);
            return writer.ToArray();
        }
    }

    [Fact]
    public void An_attachment_of_a_roster_round_trips_and_is_only_a_roster()
    {
        MenuAttachment sent = MenuAttachment.NurseRoster(13, Sample());
        Assert.Equal((MenuAttachmentKind.NurseRoster, MenuAttachment.NurseRosterFormat), (sent.Kind, sent.Format));
        Assert.Contains("Nurse roster for #13", sent.ToString(), StringComparison.Ordinal);
        MenuAttachment committed = MenuAttachment.NurseRoster(13, Committed());
        Assert.Equal(MenuAttachment.NurseOutcomeFormat, committed.Format);
        Assert.Contains("Nurse roster and outcome for #13", committed.ToString(), StringComparison.Ordinal);
        Assert.False(sent.TryGetNurseSteps(out _));
        Assert.False(sent.TryGetEventChoices(out _));
        Assert.False(MenuAttachment.EventChoices(13, EventChoiceSetTests.Nurse()).TryGetNurseRoster(out _));

        foreach (MenuAttachment attachment in new[] { sent, committed })
        {
            var writer = new BitWriter();
            Assert.True(MenuAttachmentMessage.Write(writer, attachment));
            var reader = new BitReader(writer.AsSpan());
            Assert.True(MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read));
            Assert.Equal(attachment.Format, read!.Format);
            Assert.True(read.TryGetNurseRoster(out NurseRosterData? roster));
            Assert.Equal(Encoded(attachment == sent ? Sample() : Committed()), Encoded(roster!));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => MenuAttachment.NurseRoster(0, Sample()));
        Assert.Throws<ArgumentNullException>(() => MenuAttachment.NurseRoster(1, null!));
    }

    [Fact]
    public void A_body_in_the_wrong_format_is_refused()
    {
        // A roster with no outcome sent as format 2, and one with an outcome sent as format 1, are refused on the wire.
        Assert.Null(Wire(Encoded(Sample()), MenuAttachment.NurseOutcomeFormat));
        Assert.Null(Wire(Encoded(Committed()), MenuAttachment.NurseRosterFormat));
        Assert.NotNull(Wire(Encoded(Committed()), MenuAttachment.NurseOutcomeFormat));
        Assert.Null(Wire(Encoded(Sample()), 3));

        static MenuAttachment? Wire(byte[] body, byte format)
        {
            var writer = new BitWriter();
            writer.WriteUInt32(5);
            writer.WriteByte((byte)MenuAttachmentKind.NurseRoster);
            writer.WriteByte(format);
            byte[] deflated = Compression.Deflate(body);
            writer.WriteVarUInt((ulong)body.Length);
            writer.WriteVarUInt((ulong)deflated.Length);
            writer.WriteBytes(deflated);
            var reader = new BitReader(writer.AsSpan());
            return MenuAttachmentMessage.TryRead(ref reader, out MenuAttachment? read) ? read : null;
        }
    }

    [Fact]
    public void Changed_and_random_bytes_never_throw_and_what_reads_is_within_the_limits()
    {
        byte[][] seeds = { Encoded(Sample()), Encoded(Committed()), Encoded(NurseRosterData.Unavailable("why")) };
        var random = new Random(9);
        int read = 0;
        for (int round = 0; round < 8000; round++)
        {
            byte[] bytes = (byte[])seeds[round % seeds.Length].Clone();
            if (round % 5 == 4)
            {
                bytes = new byte[random.Next(0, 96)];
                random.NextBytes(bytes);
            }
            else
            {
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            var reader = new BitReader(bytes);
            if (!NurseRosterData.TryRead(ref reader, out NurseRosterData? roster))
            {
                Assert.Null(roster);
                continue;
            }

            read++;
            Assert.Null(roster!.Check());
            Assert.Equal(Encoded(roster), Encoded(Read(Encoded(roster))!));
            _ = roster.ToString();
            _ = NurseRosterData.FirstDifference(Sample(), roster);
        }

        Assert.True(read > 0, "some changed rosters should still read");
    }
}
