#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppData;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue;
using Il2CppRogue.NPC.NurseNPC;
using Il2CppRogue.NPC.NurseNPC.Procedures;
using Il2CppRogue.Powerups.Repository;
using Il2CppSkaters.Injuries;
using Il2CppSTS.Map;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The Nurse's roster (v1): the game's <c>NurseProcedureRoster</c> and the <c>NurseProcedureState</c> made from it, read field by
/// field into a <see cref="NurseRosterData"/>, and rebuilt on a friend from the host's.
/// <list type="bullet">
/// <item>Read (<see cref="Read"/>): the host reads its own to send (everything random its roster factory drew, and what the
/// procedure screen shows of each skater); a friend reads the one it rebuilt the same way, to check it.</item>
/// <item>Rebuild (<see cref="Rebuild"/>, friend): the game's own factory, <c>NurseProcedureRoster.Create</c>, on this PC's copy
/// of the host's run, with the host's draws: its talent draws are delegates (the host's injury and replacement talents,
/// fresh copies at the host's levels), and while it runs the two injury draws it makes itself answer the host's
/// (<see cref="Armed"/>: <c>InjurySystem.GetRandomInjury</c> gives an offer of the host's type to exactly the skaters the
/// host's roster offered one, so the roster's own pick of the offers' type is the host's, and
/// <c>GetRandomizedHealingTimeInEvent</c> gives the host's games; the roster then makes every offer itself, with
/// <c>GetInjury</c>). No stat manager is passed, so even a commit that ran would count nothing. Then the game's own
/// <c>CreateState</c>, a read of both, and the check against the host's; one that differs is dropped (its talents
/// destroyed) and the procedure screen shows as text.</item>
/// <item>Install (<see cref="Install"/>, from <see cref="EventViews.Write"/>): the roster and state go on this PC's Nurse event
/// before its cards are made, as its <c>Initialize</c> puts them on the host's, so its cards warm the procedure screen as the
/// host's do (<c>OnChoiceItemCreated</c>'s <c>Prewarm</c>). One that no screen takes has its talents destroyed
/// (<see cref="Release"/>).</item>
/// </list>
/// </summary>
internal static class NurseRosters
{
    // InjuryOfferType's value in its Nullable (hasValue first; verified in the game's code: the roster's ctor writes {1, type} into it).
    private const int NullableValueOffset = 4;

    // The roster's InjuryOfferType field offset (-1: not found), looked up once.
    private static int _offerTypeOffset = -1;
    private static bool _offerTypeLooked;

    // While the factory runs on a friend: the host's roster, whose draws the two injury prefixes answer.
    private static NurseRosterData? _armed;

    /// <summary>True only while <see cref="Rebuild"/> runs the game's factory: the injury draw prefixes answer the host's.</summary>
    public static bool Armed => _armed != null;

    /// <summary>Friend, inside the factory (<c>GetRandomizedHealingTimeInEvent</c>'s prefix): the host's offers' games.</summary>
    public static int ArmedGames => _armed?.OfferGames ?? 0;

    /// <summary>
    /// Friend, inside the factory (<c>InjurySystem.GetRandomInjury(ForwardData, TeamData)</c>'s prefix): an offer of the host's
    /// type for a skater the host's roster offered one, else none. Only the offer's type matters here: the roster makes every
    /// offer again with <c>GetInjury</c>.
    /// </summary>
    public static Injury? ArmedOffer(ForwardData? forward)
    {
        NurseRosterData? host = _armed;
        return host == null || host.OfferType < 0 || forward?.id is not { Length: > 0 } id || !host.OffersInjury(id)
            ? null
            : new Injury((InjuryType)host.OfferType, host.OfferGames);
    }

    /// <summary>
    /// Friend: the host's roster rebuilt here with the game's own factory and checked against it, or null with why (anything
    /// it made is destroyed). Never throws.
    /// </summary>
    public static NurseBuilt? Rebuild(NurseRosterData host, out string problem)
    {
        var made = new List<Talent>();
        try
        {
            NurseBuilt? built = Make(host, made, out problem);
            if (built != null)
            {
                return built;
            }
        }
        catch (Exception ex)
        {
            problem = $"the game's roster factory failed here ({ModLog.SafeErrorText(ex)})";
        }
        finally
        {
            _armed = null;
        }

        Destroy(made);
        return null;
    }

    /// <summary>
    /// Friend, from <see cref="EventViews.Write"/>: a plan's rebuilt roster and state onto this PC's Nurse event (an older
    /// roster there first has its unused talents destroyed, as the event's own <c>Initialize</c> does), read back. One that
    /// doesn't read back is taken off again and released, with why (<see cref="NurseBuilt.Failure"/>: its procedure screen
    /// shows as text). Never throws.
    /// </summary>
    public static void Install(EventNode node, NurseBuilt? built)
    {
        if (built == null || built.Installed || built.Released || node.TryCast<NurseEvent>() is not { } nurse)
        {
            // A released one (its view closed while its menu was still opening) never goes on the event: its talents are gone.
            return;
        }

        try
        {
            NurseProcedureRoster? old = nurse._roster;
            if (old != null && old.Pointer != built.Roster.Pointer)
            {
                old.DestroyUnusedTalents();
            }

            nurse._roster = built.Roster;
            nurse._state = built.State;
            if (nurse._roster?.Pointer == built.Roster.Pointer && nurse._state?.Pointer == built.State.Pointer)
            {
                built.Installed = true;
                return;
            }

            built.Failure = "the rebuilt roster didn't read back on this PC's Nurse event";
            nurse._state = null!;
        }
        catch (Exception ex)
        {
            built.Failure = $"the rebuilt roster couldn't be put on this PC's Nurse event ({ModLog.SafeErrorText(ex)})";
        }

        ModLog.Warn($"Menus: {built.Failure}; the host's Nurse procedure screen will show as text here.");
        Release(built, null);
    }

    /// <summary>
    /// Friend: a rebuilt roster no procedure screen took (its view closed, or it never went on the event): its talents are
    /// destroyed (the screen's own release does it otherwise; a second time is harmless, the game skips destroyed ones; verified
    /// in the game's code, <c>DestroyIfUnused</c>), and the state it left on <paramref name="node"/> is taken off.
    /// Never throws.
    /// </summary>
    public static void Release(NurseBuilt? built, EventNode? node)
    {
        if (built == null || built.Released || built.Opened)
        {
            return;
        }

        built.Released = true;
        try
        {
            built.Roster.DestroyUnusedTalents();
            if (node?.TryCast<NurseEvent>() is { } nurse && nurse._state?.Pointer == built.State.Pointer)
            {
                nurse._state = null!;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: the Nurse roster rebuilt here couldn't be cleaned up ({ModLog.SafeErrorText(ex)}).");
        }
    }

    private static NurseBuilt? Make(NurseRosterData host, List<Talent> made, out string problem)
    {
        if (host.Problem != null)
        {
            problem = $"the host's roster didn't come ({NurseRosterData.ProblemForLog(host.Problem)})";
            return null;
        }

        TeamData? team = MenuRebuild.Team(out problem);
        RunData? run = MenuGame.Campaign()?.runData;
        InjurySystem? injuries = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<InjurySystem>())?.TryCast<InjurySystem>();
        ITalentRepository? talents = MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p));
        if (team == null || run == null || injuries == null || talents == null)
        {
            problem = team == null ? problem : run == null ? "the host's run isn't loaded here" : injuries == null ? "this game's injury system isn't up" : "this game's talent list isn't up";
            return null;
        }

        // The host's talents, as fresh copies at the host's levels (the factory's draws give fresh copies on the host too).
        Talent? injury = null, immunity = null;
        if (host.InjuryTalent != null && (injury = Fresh(talents, host.InjuryTalent, made)) == null)
        {
            problem = $"this game has no talent {host.InjuryTalent}";
            return null;
        }

        if (host.ImmunityTalent != null && (immunity = Fresh(talents, host.ImmunityTalent, made)) == null)
        {
            problem = $"this game has no talent {host.ImmunityTalent}";
            return null;
        }

        var lists = new Dictionary<string, Il2CppList.List<Talent>>(StringComparer.Ordinal);
        foreach (NurseRosterPlayer player in host.Players)
        {
            if (player.Replacements == null)
            {
                continue;
            }

            var list = new Il2CppList.List<Talent>();
            foreach (NurseTalent wanted in player.Replacements)
            {
                Talent? fresh = Fresh(talents, wanted, made);
                if (fresh == null)
                {
                    problem = $"this game has no talent {wanted}";
                    return null;
                }

                list.Add(fresh);
            }

            lists[player.ForwardId] = list;
        }

        // The factory's own talent draws, answered with the host's: the butchery talent (asked once, with no skater) and each
        // skater's surgery replacements (none for a skater the host drew none for).
        var butchery = new Func<ForwardData, Talent>(_ => injury!);
        var surgery = new Func<ForwardData, Il2CppList.List<Talent>>(forward => forward?.id is { } id && lists.TryGetValue(id, out Il2CppList.List<Talent>? list) ? list : null!);
        Il2CppSystem.Func<ForwardData, Talent> drawButchery = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<ForwardData, Talent>>(butchery)!;
        Il2CppSystem.Func<ForwardData, Il2CppList.List<Talent>> drawSurgery = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<ForwardData, Il2CppList.List<Talent>>>(surgery)!;

        // The factory, its injury draws answering the host's while it runs; no stat manager (nothing is ever counted here).
        _armed = host;
        NurseProcedureRoster? roster;
        try
        {
            roster = NurseProcedureRoster.Create(team, run, injuries, null!, drawButchery, immunity!, host.RequiredForwardId!, drawSurgery);
        }
        finally
        {
            _armed = null;
        }

        if (roster == null)
        {
            problem = "the game's roster factory made nothing here";
            return null;
        }

        NurseProcedureState? state = roster.CreateState();
        string? difference = state == null ? "the game made no procedure state from it" : NurseRosterData.FirstDifference(host, Read(roster, state, host.RequiredForwardId));
        if (difference != null)
        {
            problem = $"the roster rebuilt here differs from the host's: {difference}";
            roster.DestroyUnusedTalents();
            return null;
        }

        problem = string.Empty;
        return new NurseBuilt(host, roster, state!, new object[] { butchery, surgery, drawButchery, drawSurgery });
    }

    /// <summary>A fresh copy of a talent at its level (the game's list clones what it gives), noted in <paramref name="made"/>; or null.</summary>
    private static Talent? Fresh(ITalentRepository talents, NurseTalent wanted, List<Talent> made)
    {
        Talent? talent = talents.GetTalent(wanted.Id, true);
        if (talent != null)
        {
            made.Add(talent);
        }

        if (talent != null && talent.level != wanted.Level)
        {
            // Only the copy at the host's level goes on: nothing holds the first one, so it goes now (as EventViews.FreshCopy does).
            Talent first = talent;
            talent = talents.GetTalent(first.GetIl2CppType(), wanted.Level, true);
            if (talent != null)
            {
                made.Add(talent);
            }

            made.Remove(first);
            UnityEngine.Object.Destroy(first);
        }

        return talent != null && talent.id == wanted.Id && talent.level == wanted.Level ? talent : null;
    }

    /// <summary>Destroys talents made for a roster that won't be used (nothing of the game's holds them). Never throws.</summary>
    private static void Destroy(List<Talent> made)
    {
        foreach (Talent talent in made)
        {
            try
            {
                if (talent != null)
                {
                    UnityEngine.Object.Destroy(talent);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Menus: a talent made for the Nurse's roster here couldn't be destroyed ({ModLog.SafeErrorText(ex)}).");
                return;
            }
        }
    }

    /// <summary>
    /// <paramref name="roster"/> and <paramref name="state"/> as data, with the squad's required skater
    /// <paramref name="requiredForwardId"/> (the game passes it to the factory; the roster doesn't keep it). Throws for a roster
    /// the game didn't finish (a list missing): the caller says why.
    /// </summary>
    public static NurseRosterData Read(NurseProcedureRoster roster, NurseProcedureState state, string? requiredForwardId)
    {
        Il2CppList.List<ForwardData> forwards = roster._forwards ?? throw new InvalidOperationException("the roster has no skaters list");
        Il2CppReferenceArray<Injury> offers = roster._injuryOffers ?? throw new InvalidOperationException("the roster has no injury offers");
        Il2CppReferenceArray<Il2CppList.List<Talent>> replacements = roster._replacements ?? throw new InvalidOperationException("the roster has no replacements");
        Il2CppReferenceArray<NurseProcedureState.Player> players = state.Players ?? throw new InvalidOperationException("the procedure state has no skaters");
        int count = forwards.Count;
        if (count > NurseRosterData.MaxPlayers || offers.Length != count || replacements.Length != count || players.Length != count)
        {
            throw new InvalidOperationException($"the roster has {count} skaters, {offers.Length} offers, {replacements.Length} replacement lists and {players.Length} screen rows");
        }

        var read = new List<NurseRosterPlayer>(count);
        for (int i = 0; i < count; i++)
        {
            ForwardData forward = forwards[i] ?? throw new InvalidOperationException($"skater {i + 1} is missing");
            NurseProcedureState.Player player = players[i] ?? throw new InvalidOperationException($"skater {i + 1}'s screen row is missing");
            Injury? offer = offers[i];
            Il2CppList.List<Talent>? drawn = replacements[i];
            List<NurseTalent>? talents = null;
            if (drawn != null)
            {
                talents = new List<NurseTalent>(drawn.Count);
                for (int t = 0; t < drawn.Count; t++)
                {
                    talents.Add(Of(drawn[t]) ?? throw new InvalidOperationException($"skater {i + 1}'s replacement talent {t + 1} is missing"));
                }
            }

            read.Add(new NurseRosterPlayer
            {
                ForwardId = forward.id ?? string.Empty,
                Overall = player.Overall,
                Eligible = player.Eligible,
                Offer = offer == null ? null : new NurseInjuryOffer((int)offer.InjuryType, offer.HealingTime),
                Replacements = talents,
            });
        }

        return new NurseRosterData
        {
            Players = read,
            OfferType = OfferType(roster),
            OfferGames = roster.InjuryOfferGames,
            InjuryTalent = Of(roster.InjuryTalent),
            ImmunityTalent = Of(roster.ImmunityTalent),
            RequiredForwardId = string.IsNullOrEmpty(requiredForwardId) ? null : requiredForwardId,
            Options = state._options?.Count ?? 0,
        };
    }

    /// <summary>A talent as its id and level, or null for none.</summary>
    public static NurseTalent? Of(Talent? talent) => talent == null ? null : new NurseTalent(talent.id ?? string.Empty, talent.level);

    /// <summary>A procedure state's talent snapshot's identity (the talent's class name: the same on every PC), or null.</summary>
    public static string? Identity(NurseProcedureState.Talent? snapshot) => string.IsNullOrEmpty(snapshot?.Identity) ? null : SafeText.Clean(snapshot!.Identity, 120);

    /// <summary>
    /// The injury type every offer of <paramref name="roster"/> has, or -1 for none: its <c>InjuryOfferType</c> (a
    /// <c>Nullable&lt;InjuryType&gt;</c>) read in place, as <see cref="NurseScreen"/> reads the state's procedure (the
    /// interop's field getter boxes the Nullable with <c>il2cpp_value_box</c>, which boxes a Nullable as IL2CPP does, null or
    /// the bare enum, and its wrapper then misreads it).
    /// </summary>
    public static int OfferType(NurseProcedureRoster roster)
    {
        int offset = OfferTypeOffset();
        if (offset < 0)
        {
            throw new InvalidOperationException("the roster's injury offer type field wasn't found in this game");
        }

        return Marshal.ReadByte(roster.Pointer, offset) != 0 ? Marshal.ReadInt32(roster.Pointer, offset + NullableValueOffset) : -1;
    }

    /// <summary>The roster's offer type field offset (from the game's class), or -1 (logged once).</summary>
    private static int OfferTypeOffset()
    {
        if (!_offerTypeLooked)
        {
            _offerTypeLooked = true;
            IntPtr type = Il2CppClassPointerStore<NurseProcedureRoster>.NativeClassPtr;
            IntPtr field = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(type, "InjuryOfferType");
            int offset = field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);

            // Past the object header, the Nullable's 8 bytes inside the roster (verified in the game's code).
            _offerTypeOffset = offset is >= 0x10 and <= 0x200 ? offset : -1;
            if (_offerTypeOffset < 0)
            {
                ModLog.Warn($"Menus: the Nurse roster's injury offer type field wasn't found in this game (offset {offset}); the Nurse's procedure screen shows as text on friends.");
            }
        }

        return _offerTypeOffset;
    }
}

/// <summary>A Nurse roster rebuilt on this PC from the host's, with its procedure state, for one event menu view.</summary>
internal sealed class NurseBuilt
{
    // The factory's draw delegates (managed and IL2CPP), held while the roster lives.
    private readonly object[] _keep;

    /// <summary>The roster <paramref name="roster"/> and state <paramref name="state"/> rebuilt from <paramref name="host"/>; <paramref name="keep"/>: the draw delegates.</summary>
    public NurseBuilt(NurseRosterData host, NurseProcedureRoster roster, NurseProcedureState state, object[] keep)
    {
        Host = host;
        Roster = roster;
        State = state;
        _keep = keep;
    }

    /// <summary>The host's roster it was rebuilt from (format 1: its outcome comes later, as its own attachment).</summary>
    public NurseRosterData Host { get; }

    /// <summary>The roster made here.</summary>
    public NurseProcedureRoster Roster { get; }

    /// <summary>The procedure state made from it here.</summary>
    public NurseProcedureState State { get; }

    /// <summary>True once it went on the Nurse event (its cards warm the procedure screen with it).</summary>
    public bool Installed { get; set; }

    /// <summary>True once a procedure screen took it (the screen's own release cleans it up).</summary>
    public bool Opened { get; set; }

    /// <summary>True once its talents were destroyed (<see cref="NurseRosters.Release"/>).</summary>
    public bool Released { get; set; }

    /// <summary>Why it couldn't go on the event (its procedure screen then shows as text), or "".</summary>
    public string Failure { get; set; } = string.Empty;

    /// <summary>The delegates held (two managed, two IL2CPP).</summary>
    public int Held => _keep.Length;
}
#endif
