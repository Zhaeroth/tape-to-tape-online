using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>A talent the host's Nurse roster drew (a fresh copy from the game's talent list): its id and level.</summary>
/// <param name="Id">The talent's id.</param>
/// <param name="Level">Its level.</param>
public sealed record NurseTalent(string Id, int Level)
{
    /// <summary>For logs: "sniper (level 2)".</summary>
    public override string ToString() => $"{HostText.KeyForLog(Id)} (level {Level})";
}

/// <summary>An injury the host's Butcher offers a skater (the game's <c>Injury</c>: its type and its games out).</summary>
/// <param name="Type">The game's injury type number.</param>
/// <param name="Games">The games it lasts (its healing time).</param>
public sealed record NurseInjuryOffer(int Type, int Games);

/// <summary>
/// One skater of the host's Nurse roster, in the roster's order (the game's <c>NurseProcedureRoster._forwards</c>): its id,
/// the overall and eligibility the procedure screen shows, the injury the Butcher offers it (null: none), and the talents
/// drawn to replace one of its own (null: none were drawn).
/// </summary>
public sealed class NurseRosterPlayer
{
    /// <summary>The skater's id in the run.</summary>
    public string ForwardId { get; init; } = string.Empty;

    /// <summary>The overall the screen shows (the game's <c>NurseProcedureState.Player.Overall</c>).</summary>
    public int Overall { get; init; }

    /// <summary>The skater can take part in a procedure (<c>Player.Eligible</c>).</summary>
    public bool Eligible { get; init; }

    /// <summary>The injury the Butcher offers it, or null.</summary>
    public NurseInjuryOffer? Offer { get; init; }

    /// <summary>The talents drawn to replace one of its own (the surgery's), or null when none were drawn.</summary>
    public IReadOnlyList<NurseTalent>? Replacements { get; init; }
}

/// <summary>One skater's change from a sacrifice on the host (the game's <c>SacrificeResultChip</c>): its index and stat deltas.</summary>
public sealed class NurseSacrificeChip
{
    /// <summary>The skater's index in the roster.</summary>
    public int Player { get; init; }

    /// <summary>Its stat deltas, as the host's commit measured them (the game's <c>AttributeDeltas</c>).</summary>
    public IReadOnlyList<int> Deltas { get; init; } = Array.Empty<int>();
}

/// <summary>
/// What the host's commit of a procedure did (read once its <c>NurseProcedureScreen._committed</c> is set): the sacrifice's
/// measured stat deltas per skater, and the talents and room it settled on, by the game's talent identity (the class name),
/// for checking a friend's screen against.
/// </summary>
public sealed class NurseOutcome
{
    /// <summary>The procedure committed (the game's procedure number).</summary>
    public int Procedure { get; init; }

    /// <summary>A sacrifice's skaters and their deltas (empty for other procedures).</summary>
    public IReadOnlyList<NurseSacrificeChip> Chips { get; init; } = Array.Empty<NurseSacrificeChip>();

    /// <summary>The replacement talent's identity (a surgery), or null.</summary>
    public string? Replacement { get; init; }

    /// <summary>The injury reward talent's identity, or null.</summary>
    public string? InjuryReward { get; init; }

    /// <summary>The immunity reward talent's identity, or null.</summary>
    public string? ImmunityReward { get; init; }

    /// <summary>The bench had room for a clone (<c>CloneBenchRoom</c>).</summary>
    public bool CloneBenchRoom { get; init; }

    /// <summary>For logs: "procedure 4, 2 sacrifice chip(s)", "procedure 1, replacement Sniper".</summary>
    public override string ToString()
    {
        var parts = new List<string> { $"procedure {Procedure}" };
        if (Chips.Count > 0)
        {
            parts.Add($"{Chips.Count} sacrifice chip(s)");
        }

        if (Replacement != null)
        {
            parts.Add($"replacement {HostText.KeyForLog(Replacement)}");
        }

        if (InjuryReward != null || ImmunityReward != null)
        {
            parts.Add($"rewards {(InjuryReward == null ? "none" : HostText.KeyForLog(InjuryReward))}/{(ImmunityReward == null ? "none" : HostText.KeyForLog(ImmunityReward))}");
        }

        parts.Add(CloneBenchRoom ? "bench room" : "no bench room");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// The host's Nurse roster, sent as a
/// <see cref="MenuAttachmentKind.NurseRoster"/> attachment of the event menu: everything random the game's own roster
/// factory (<c>NurseProcedureRoster.Create</c>) drew on the host (the Butcher's injury offers, their type and games, the
/// surgery's replacement talents, the injury and immunity talents), so a friend's game rebuilds the same roster with that
/// factory, and what the procedure screen shows of each skater, to check the rebuilt one against
/// (<see cref="FirstDifference"/>). A roster the host can't send says why instead (<see cref="Problem"/>, no skaters); a friend
/// then shows the procedure screen as text. Format <see cref="MenuAttachment.NurseRosterFormat"/>; once the host's procedure
/// is committed, the same roster again with its <see cref="Outcome"/> (format <see cref="MenuAttachment.NurseOutcomeFormat"/>).
/// Checked when it's written and when it's read; immutable.
/// <code>
/// bool problem (· name problem · bool outcome, always false: nothing else) ·
/// varuint skaters · each: name id · varuint overall · bool eligible · bool offer (· u8 type · varuint games) ·
///   bool replacements (· varuint count · each: name id · varuint level) ·
/// u8 offer type + 1 (0: none) · varuint offer games · optional talent injury · optional talent immunity ·
/// optional name required skater · varuint options ·
/// bool outcome (· u8 procedure · varuint chips · each: varuint skater · varuint deltas · each: varint ·
///   optional name replacement · optional name injury reward · optional name immunity reward · bool bench room)
/// </code>
/// </summary>
public sealed class NurseRosterData
{
    /// <summary>Most skaters in a roster (the game's roster is the team's forwards).</summary>
    public const int MaxPlayers = 16;

    /// <summary>Most replacement talents drawn for one skater (the game draws <c>SurgeryTalentCount</c>).</summary>
    public const int MaxReplacements = 8;

    /// <summary>Highest injury type number kept (the game's types and room for more).</summary>
    public const int MaxInjuryType = 63;

    /// <summary>Highest games out, overall and option count kept.</summary>
    public const int MaxNumber = 999;

    /// <summary>Highest talent level kept.</summary>
    public const int MaxLevel = 99;

    /// <summary>Highest procedure number kept (the game's procedures and room for more).</summary>
    public const int MaxProcedure = 63;

    /// <summary>Most sacrifice chips in an outcome, and most deltas in one.</summary>
    public const int MaxChips = 16;

    /// <summary>Largest stat delta kept, either way.</summary>
    public const int MaxDelta = 999;

    /// <summary>Why a host with the real procedure screen switched off (its NurseRealScreen setting) sends no roster.</summary>
    public const string SwitchedOff = "the host has the real procedure screen switched off (its NurseRealScreen setting)";

    /// <summary>Why a host whose game couldn't be patched to follow its procedure screen sends no roster.</summary>
    public const string NotPatched = "the host's game couldn't be patched to follow its procedure screen";

    /// <summary>Why a host whose Nurse made no roster sends none.</summary>
    public const string NoRoster = "the host's Nurse made no roster (its run has no team)";

    /// <summary>What <see cref="Unavailable(string)"/> gives for an empty reason.</summary>
    public const string UnknownProblem = "unknown";

    /// <summary>The opening words of a host's roster that couldn't be sent (then why, then a closing parenthesis).</summary>
    public const string NotSentLeadIn = "its roster couldn't be sent (";

    /// <summary>The opening words of a host's roster outside the carrier's limits (then which, then a closing parenthesis).</summary>
    public const string OutOfLimitsLeadIn = "its roster is out of the carrier's limits (";

    /// <summary>The opening words of a host's roster its game couldn't read (then the error, then a closing parenthesis).</summary>
    public const string NotReadLeadIn = "its roster couldn't be read (";

    /// <summary>This build's problems that are always the same words (a friend's log writes them as they are).</summary>
    public static IReadOnlyList<string> FixedProblems { get; } = new[] { SwitchedOff, NotPatched, NoRoster, UnknownProblem };

    /// <summary>This build's problems' opening words, each followed by a part that varies (a friend's log writes the words and the rest's length).</summary>
    public static IReadOnlyList<string> ProblemLeadIns { get; } = new[] { NotSentLeadIn, OutOfLimitsLeadIn, NotReadLeadIn };

    /// <summary>Why the host couldn't send its roster (then nothing else), or null.</summary>
    public string? Problem { get; init; }

    /// <summary>The roster's skaters, in its order.</summary>
    public IReadOnlyList<NurseRosterPlayer> Players { get; init; } = Array.Empty<NurseRosterPlayer>();

    /// <summary>The injury type every offer has (the game's <c>InjuryOfferType</c>), or -1 for none.</summary>
    public int OfferType { get; init; } = -1;

    /// <summary>The games the offers were drawn with (<c>InjuryOfferGames</c>).</summary>
    public int OfferGames { get; init; }

    /// <summary>The talent the Butcher's injury procedure grants (<c>InjuryTalent</c>), or null.</summary>
    public NurseTalent? InjuryTalent { get; init; }

    /// <summary>The immunity talent (<c>ImmunityTalent</c>), or null.</summary>
    public NurseTalent? ImmunityTalent { get; init; }

    /// <summary>The skater the squad requires (<c>NurseProcedureRoster.GetRequiredForwardId</c>), or null.</summary>
    public string? RequiredForwardId { get; init; }

    /// <summary>How many options the procedure screen's first stage has (<c>NurseProcedureState.Options.Count</c>).</summary>
    public int Options { get; init; }

    /// <summary>What the host's commit did, once it's done (format 2), or null.</summary>
    public NurseOutcome? Outcome { get; init; }

    /// <summary>The layout this one is written in: with an outcome, <see cref="MenuAttachment.NurseOutcomeFormat"/>.</summary>
    public byte Format => Outcome == null ? MenuAttachment.NurseRosterFormat : MenuAttachment.NurseOutcomeFormat;

    /// <summary>A roster the host can't send, with why, cut to fit (a friend shows the procedure screen as text).</summary>
    public static NurseRosterData Unavailable(string why)
    {
        // Cut a character at a time from the end, never between a pair's two halves (a lone half isn't text, and a reason
        // that isn't would be refused whole: the friend would get no reason at all).
        string text = SafeText.Clean(why, 120);
        while (text.Length > 0 && (Encoding.UTF8.GetByteCount(text) > EventChoiceSet.MaxNameBytes || char.IsHighSurrogate(text[^1])))
        {
            text = text[..^1];
        }

        return new NurseRosterData { Problem = text.Length > 0 ? text : UnknownProblem };
    }

    /// <summary>
    /// A roster the host can't send, why being one of this build's <see cref="ProblemLeadIns"/> with <paramref name="detail"/>
    /// after it: "its roster couldn't be read (NullReferenceException: ...)". A host's reasons are made only from this list and
    /// <see cref="FixedProblems"/>, so a friend's log (<see cref="ProblemForLog"/>) keeps their words.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="leadIn"/> isn't one of <see cref="ProblemLeadIns"/>.</exception>
    public static NurseRosterData Unavailable(string leadIn, string? detail)
    {
        if (!ProblemLeadIns.Contains(leadIn))
        {
            throw new ArgumentException("Not one of this build's opening words for a roster that can't be sent.", nameof(leadIn));
        }

        return Unavailable($"{leadIn}{detail})");
    }

    /// <summary>
    /// A host's <see cref="Problem"/> for a log line on a friend's PC (D146): one of <see cref="FixedProblems"/> as it is, one
    /// that begins with one of <see cref="ProblemLeadIns"/> as those words and the length of the rest, any other text as its
    /// length (<see cref="SentText.Reason(string?, IReadOnlyCollection{string}, IReadOnlyList{string})"/>). The host's own log
    /// has its reason in full.
    /// </summary>
    public static string ProblemForLog(string? problem) => SentText.Reason(problem, FixedProblems, ProblemLeadIns);

    /// <summary>This roster with the host's commit's outcome (format 2).</summary>
    public NurseRosterData WithOutcome(NurseOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new NurseRosterData
        {
            Problem = Problem,
            Players = Players,
            OfferType = OfferType,
            OfferGames = OfferGames,
            InjuryTalent = InjuryTalent,
            ImmunityTalent = ImmunityTalent,
            RequiredForwardId = RequiredForwardId,
            Options = Options,
            Outcome = outcome,
        };
    }

    /// <summary>The host's replacement talents for skater <paramref name="forwardId"/>, or null when none were drawn for it.</summary>
    public IReadOnlyList<NurseTalent>? ReplacementsOf(string forwardId) => Players.FirstOrDefault(p => p.ForwardId == forwardId)?.Replacements;

    /// <summary>True when the host offered skater <paramref name="forwardId"/> an injury.</summary>
    public bool OffersInjury(string forwardId) => Players.FirstOrDefault(p => p.ForwardId == forwardId)?.Offer != null;

    /// <summary>
    /// The first way <paramref name="here"/> (a roster rebuilt on a friend's PC, read the same way) differs from
    /// <paramref name="host"/>, in words, or null when they're the same: the skaters (ids, order, overall, eligibility, offers,
    /// replacements), the offers' type and games, the talents, the required skater and the first stage's options. Problems
    /// and outcomes aren't compared.
    /// </summary>
    public static string? FirstDifference(NurseRosterData host, NurseRosterData here)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(here);
        if (host.Problem != null || here.Problem != null)
        {
            return host.Problem != null ? $"the host's roster isn't here ({ProblemForLog(host.Problem)})" : $"this PC's roster couldn't be read ({here.Problem})";
        }

        if (host.Players.Count != here.Players.Count)
        {
            return $"{here.Players.Count} skater(s) here, {host.Players.Count} on the host's";
        }

        for (int i = 0; i < host.Players.Count; i++)
        {
            NurseRosterPlayer want = host.Players[i];
            NurseRosterPlayer got = here.Players[i];
            string who = $"skater {i + 1} (\"{HostText.KeyForLog(want.ForwardId)}\")";
            string? field =
                got.ForwardId != want.ForwardId ? $"is \"{HostText.KeyForLog(got.ForwardId)}\" here"
                : got.Overall != want.Overall ? $"has overall {got.Overall} here, {want.Overall} on the host's"
                : got.Eligible != want.Eligible ? $"is {(got.Eligible ? string.Empty : "not ")}eligible here"
                : got.Offer != want.Offer ? $"is offered {Describe(got.Offer)} here, {Describe(want.Offer)} on the host's"
                : !SameTalents(got.Replacements, want.Replacements) ? $"has replacements {Describe(got.Replacements)} here, {Describe(want.Replacements)} on the host's"
                : null;
            if (field != null)
            {
                return $"{who} {field}";
            }
        }

        return here.OfferType != host.OfferType ? $"the offers' injury type is {here.OfferType} here, {host.OfferType} on the host's"
            : here.OfferGames != host.OfferGames ? $"the offers' games are {here.OfferGames} here, {host.OfferGames} on the host's"
            : here.InjuryTalent != host.InjuryTalent ? $"the injury talent is {Describe(here.InjuryTalent)} here, {Describe(host.InjuryTalent)} on the host's"
            : here.ImmunityTalent != host.ImmunityTalent ? $"the immunity talent is {Describe(here.ImmunityTalent)} here, {Describe(host.ImmunityTalent)} on the host's"
            : here.RequiredForwardId != host.RequiredForwardId ? "the required skater differs"
            : here.Options != host.Options ? $"the first stage has {here.Options} option(s) here, {host.Options} on the host's"
            : null;
    }

    /// <summary>
    /// For logs: "6 skaters, offers type 3 x2 for 2 games, 2 with replacements, injury talent ..." or "unavailable (why)", why by
    /// <see cref="ProblemForLog"/> (a roster can be a host's: the host's own lines write its reason in full).
    /// </summary>
    public override string ToString()
    {
        if (Problem != null)
        {
            return $"unavailable ({ProblemForLog(Problem)})";
        }

        int offers = Players.Count(p => p.Offer != null);
        int replaced = Players.Count(p => p.Replacements != null);
        string offered = OfferType < 0 ? "no injury offers" : $"injury type {OfferType} offered to {offers} for {OfferGames} game(s)";
        string outcome = Outcome == null ? string.Empty : $"; committed: {Outcome}";
        return $"{Players.Count} skater(s), {offered}, {replaced} with replacement talents, injury talent {Describe(InjuryTalent)}, immunity talent {Describe(ImmunityTalent)}, " +
            $"{Options} first option(s){outcome}";
    }

    /// <summary>Why the roster can't be sent, or null when it can.</summary>
    public string? Check()
    {
        if (Problem != null)
        {
            return Players.Count > 0 || Outcome != null ? "a roster that can't be sent carries nothing else" : EventWire.CheckName(Problem, "the problem", required: true);
        }

        if (Players.Count > MaxPlayers)
        {
            return $"{Players.Count} skaters";
        }

        foreach (NurseRosterPlayer player in Players)
        {
            string? problem = player == null ? "a skater is missing" : CheckPlayer(player);
            if (problem != null)
            {
                return problem;
            }
        }

        if (OfferType is < -1 or > MaxInjuryType || OfferGames is < 0 or > MaxNumber || Options is < 0 or > MaxNumber)
        {
            return "the offers' type or games, or the option count, are out of range";
        }

        return CheckTalent(InjuryTalent, "the injury talent") ?? CheckTalent(ImmunityTalent, "the immunity talent") ??
            EventWire.CheckName(RequiredForwardId, "the required skater", required: false) ?? (Outcome == null ? null : CheckOutcome(Outcome));
    }

    /// <summary>Writes the roster. Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Check();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteBool(Problem != null);
        if (Problem != null)
        {
            EventWire.Name(writer, Problem);
            writer.WriteBool(false);
            return;
        }

        writer.WriteVarUInt((ulong)Players.Count);
        foreach (NurseRosterPlayer player in Players)
        {
            EventWire.Name(writer, player.ForwardId);
            writer.WriteVarUInt((ulong)player.Overall);
            writer.WriteBool(player.Eligible);
            writer.WriteBool(player.Offer != null);
            if (player.Offer != null)
            {
                writer.WriteByte((byte)player.Offer.Type);
                writer.WriteVarUInt((ulong)player.Offer.Games);
            }

            writer.WriteBool(player.Replacements != null);
            if (player.Replacements != null)
            {
                writer.WriteVarUInt((ulong)player.Replacements.Count);
                foreach (NurseTalent talent in player.Replacements)
                {
                    WriteTalent(writer, talent);
                }
            }
        }

        writer.WriteByte((byte)(OfferType + 1));
        writer.WriteVarUInt((ulong)OfferGames);
        WriteOptionalTalent(writer, InjuryTalent);
        WriteOptionalTalent(writer, ImmunityTalent);
        EventWire.OptionalName(writer, RequiredForwardId);
        writer.WriteVarUInt((ulong)Options);
        writer.WriteBool(Outcome != null);
        if (Outcome != null)
        {
            writer.WriteByte((byte)Outcome.Procedure);
            writer.WriteVarUInt((ulong)Outcome.Chips.Count);
            foreach (NurseSacrificeChip chip in Outcome.Chips)
            {
                writer.WriteVarUInt((ulong)chip.Player);
                writer.WriteVarUInt((ulong)chip.Deltas.Count);
                foreach (int delta in chip.Deltas)
                {
                    writer.WriteVarInt(delta);
                }
            }

            EventWire.OptionalName(writer, Outcome.Replacement);
            EventWire.OptionalName(writer, Outcome.InjuryReward);
            EventWire.OptionalName(writer, Outcome.ImmunityReward);
            writer.WriteBool(Outcome.CloneBenchRoom);
        }
    }

    /// <summary>Reads a roster. False for anything malformed or out of range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out NurseRosterData? roster)
    {
        roster = null;
        bool ok = true;
        NurseRosterData read;
        if (reader.ReadBool())
        {
            string? problem = EventWire.ReadName(ref reader);
            if (problem == null || reader.ReadBool() || reader.IsOverrun)
            {
                return false;
            }

            read = new NurseRosterData { Problem = problem };
        }
        else
        {
            int count = EventWire.Count(ref reader, MaxPlayers, ref ok);
            if (!ok)
            {
                return false;
            }

            var players = new List<NurseRosterPlayer>(count);
            for (int i = 0; i < count; i++)
            {
                NurseRosterPlayer? player = ReadPlayer(ref reader);
                if (player == null)
                {
                    return false;
                }

                players.Add(player);
            }

            int offerType = reader.ReadByte() - 1;
            int offerGames = EventWire.Count(ref reader, MaxNumber, ref ok);
            if (!ok || !TryReadOptionalTalent(ref reader, out NurseTalent? injury) || !TryReadOptionalTalent(ref reader, out NurseTalent? immunity) ||
                !EventWire.TryReadOptionalName(ref reader, out string? required))
            {
                return false;
            }

            int options = EventWire.Count(ref reader, MaxNumber, ref ok);
            NurseOutcome? outcome = null;
            if (!ok || (reader.ReadBool() && (outcome = ReadOutcome(ref reader)) == null) || reader.IsOverrun)
            {
                return false;
            }

            read = new NurseRosterData
            {
                Players = players,
                OfferType = offerType,
                OfferGames = offerGames,
                InjuryTalent = injury,
                ImmunityTalent = immunity,
                RequiredForwardId = required,
                Options = options,
                Outcome = outcome,
            };
        }

        if (read.Check() != null)
        {
            return false;
        }

        roster = read;
        return true;
    }

    private static NurseRosterPlayer? ReadPlayer(ref BitReader reader)
    {
        bool ok = true;
        string? id = EventWire.ReadName(ref reader);
        int overall = EventWire.Count(ref reader, MaxNumber, ref ok);
        bool eligible = reader.ReadBool();
        NurseInjuryOffer? offer = null;
        if (reader.ReadBool())
        {
            int type = reader.ReadByte();
            int games = EventWire.Count(ref reader, MaxNumber, ref ok);
            offer = new NurseInjuryOffer(type, games);
        }

        List<NurseTalent>? replacements = null;
        if (reader.ReadBool())
        {
            int count = EventWire.Count(ref reader, MaxReplacements, ref ok);
            replacements = new List<NurseTalent>(ok ? count : 0);
            for (int i = 0; ok && i < count; i++)
            {
                NurseTalent? talent = ReadTalent(ref reader);
                if (talent == null)
                {
                    return null;
                }

                replacements.Add(talent);
            }
        }

        return !ok || id == null || reader.IsOverrun ? null : new NurseRosterPlayer { ForwardId = id, Overall = overall, Eligible = eligible, Offer = offer, Replacements = replacements };
    }

    private static NurseOutcome? ReadOutcome(ref BitReader reader)
    {
        bool ok = true;
        int procedure = reader.ReadByte();
        int count = EventWire.Count(ref reader, MaxChips, ref ok);
        var chips = new List<NurseSacrificeChip>(ok ? count : 0);
        for (int i = 0; ok && i < count; i++)
        {
            int player = EventWire.Count(ref reader, MaxPlayers - 1, ref ok);
            int deltas = EventWire.Count(ref reader, MaxChips, ref ok);
            var values = new int[ok ? deltas : 0];
            for (int d = 0; ok && d < deltas; d++)
            {
                long value = reader.ReadVarInt();
                if (reader.IsOverrun || value is < -MaxDelta or > MaxDelta)
                {
                    return null;
                }

                values[d] = (int)value;
            }

            chips.Add(new NurseSacrificeChip { Player = player, Deltas = values });
        }

        if (!ok || !EventWire.TryReadOptionalName(ref reader, out string? replacement) || !EventWire.TryReadOptionalName(ref reader, out string? injury) ||
            !EventWire.TryReadOptionalName(ref reader, out string? immunity))
        {
            return null;
        }

        bool room = reader.ReadBool();
        return reader.IsOverrun ? null : new NurseOutcome
        {
            Procedure = procedure,
            Chips = chips,
            Replacement = replacement,
            InjuryReward = injury,
            ImmunityReward = immunity,
            CloneBenchRoom = room,
        };
    }

    private static void WriteTalent(BitWriter writer, NurseTalent talent)
    {
        EventWire.Name(writer, talent.Id);
        writer.WriteVarUInt((ulong)talent.Level);
    }

    private static void WriteOptionalTalent(BitWriter writer, NurseTalent? talent)
    {
        writer.WriteBool(talent != null);
        if (talent != null)
        {
            WriteTalent(writer, talent);
        }
    }

    private static NurseTalent? ReadTalent(ref BitReader reader)
    {
        bool ok = true;
        string? id = EventWire.ReadName(ref reader);
        int level = EventWire.Count(ref reader, MaxLevel, ref ok);
        return ok && id != null ? new NurseTalent(id, level) : null;
    }

    private static bool TryReadOptionalTalent(ref BitReader reader, out NurseTalent? talent)
    {
        talent = null;
        if (!reader.ReadBool())
        {
            return !reader.IsOverrun;
        }

        talent = ReadTalent(ref reader);
        return talent != null;
    }

    private static string? CheckPlayer(NurseRosterPlayer player)
    {
        string who = $"skater \"{HostText.KeyForLog(player.ForwardId)}\"";
        if (player.Overall is < 0 or > MaxNumber)
        {
            return $"{who}'s overall {player.Overall} is out of range";
        }

        if (player.Offer is { } offer && (offer.Type is < 0 or > MaxInjuryType || offer.Games is < 0 or > MaxNumber))
        {
            return $"{who}'s injury offer is out of range";
        }

        if (player.Replacements is { } replacements)
        {
            if (replacements.Count > MaxReplacements)
            {
                return $"{who} has {replacements.Count} replacement talents";
            }

            foreach (NurseTalent talent in replacements)
            {
                string? problem = talent == null ? $"{who}: a replacement talent is missing" : CheckTalent(talent, $"{who}'s replacement talent");
                if (problem != null)
                {
                    return problem;
                }
            }
        }

        return EventWire.CheckName(player.ForwardId, "a skater's id", required: true);
    }

    private static string? CheckTalent(NurseTalent? talent, string what) =>
        talent == null ? null
        : talent.Level is < 0 or > MaxLevel ? $"{what}'s level {talent.Level} is out of range"
        : EventWire.CheckName(talent.Id, $"{what}'s id", required: true);

    private static string? CheckOutcome(NurseOutcome outcome)
    {
        if (outcome.Procedure is < 0 or > MaxProcedure)
        {
            return $"the outcome's procedure {outcome.Procedure} is out of range";
        }

        if (outcome.Chips.Count > MaxChips)
        {
            return $"{outcome.Chips.Count} sacrifice chips";
        }

        foreach (NurseSacrificeChip chip in outcome.Chips)
        {
            if (chip == null || chip.Player is < 0 or >= MaxPlayers || chip.Deltas.Count > MaxChips || chip.Deltas.Any(d => d is < -MaxDelta or > MaxDelta))
            {
                return "a sacrifice chip is out of range";
            }
        }

        return EventWire.CheckName(outcome.Replacement, "the replacement talent", required: false) ??
            EventWire.CheckName(outcome.InjuryReward, "the injury reward", required: false) ??
            EventWire.CheckName(outcome.ImmunityReward, "the immunity reward", required: false);
    }

    private static bool SameTalents(IReadOnlyList<NurseTalent>? a, IReadOnlyList<NurseTalent>? b) =>
        a == null || b == null ? a == null && b == null : a.SequenceEqual(b);

    private static string Describe(NurseInjuryOffer? offer) => offer == null ? "no injury" : $"injury type {offer.Type} for {offer.Games} game(s)";

    private static string Describe(NurseTalent? talent) => talent?.ToString() ?? "none";

    private static string Describe(IReadOnlyList<NurseTalent>? talents) => talents == null ? "none" : $"[{string.Join(", ", talents)}]";
}
