#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppRogue;
using Il2CppRogue.Powerups.Repository;
using Il2CppRogue.Relics.Controllers;
using Il2CppSkaters;
using Il2CppTape2Tape;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// The host's match-time random talent picks on a friend's PC. Two relics give skaters a talent for the match from a
/// random pick of each PC's own game: Hot Cake (<c>HotCakeController.AddEffect</c>: a random teammate with talents, a
/// random one of its talents, given to every other teammate; <c>UnityEngine.Random.RandomRangeInt</c>) and the hot and
/// cold relic (<c>HotColdRelicController.AddEffect</c>: a random hot and a random cold skater,
/// <c>ShufflingExtension.GetRandomElements</c>). Each keeps what it gave in a dictionary (forward to talent) and takes it
/// back at the match's end. The bench borrower talent (<c>BenchBorrowerController.Borrow</c>, at the match's start and
/// each line change) gives its own skater a copy of a random bench skater's talent (<c>UnityEngine.Random.Range</c>),
/// kept in its <c>_borrowedTalent</c>: it changes that skater's talents, never the roster. A friend's game rolls its own as its match loads, so its skaters had other talents than the
/// host's, and a talent's effect component joined a skater's replay components on one PC only.
/// <list type="bullet">
/// <item>Host: the HUD carries each pick (<see cref="PresentationKeys.PickWho"/>, <see cref="PresentationKeys.PickTalent"/>,
/// <see cref="PresentationKeys.PickCount"/>), read from both teams' controllers at most twice a second.</item>
/// <item>Friend: when the host's picks differ from this PC's, every talent this PC's controllers gave is taken back with
/// the game's own <c>Skater.RemoveTalent</c>, and each of the host's is given with <c>Skater.AddTalent</c> (a fresh copy
/// of the talent at the host's level from the game's talent repository) and noted in the same controller's dictionary,
/// so its own match end takes it back. Each skater whose talents changed has its replay components taken over again
/// (<see cref="StreamPlayer.Realign"/>).</item>
/// </list>
/// Logged once per change. Never throws.
/// </summary>
internal static class MatchPicks
{
    private const double ReadEverySeconds = 0.5;
    private const double ControllersEverySeconds = 5;
    private const double RetryEverySeconds = 1;

    private static readonly ErrorLogLimit Errors = new(3);
    private static readonly List<KeyValuePair<string, NetValue>> HostValues = new();
    private static readonly List<(RelicControllerBase Controller, PickSource Source)> HostControllers = new();
    private static readonly List<(Forward Forward, BenchBorrowerController Borrower)> HostBorrowers = new();
    private static double _nextRead = double.MinValue;
    private static double _nextControllers = double.MinValue;
    private static string _hostSaid = string.Empty;
    private static bool _pending;
    private static double _nextRetry = double.MinValue;
    private static int _followed;
    private static ITalentRepository? _talents;
    private static int _borrowsFollowed;

    // How the game names a borrowed talent's copy (BenchBorrowerController.Borrow).
    private const string BorrowedSuffix = " [BorrowedFromBench]";

    /// <summary>Host: a new stream starts.</summary>
    public static void HostStart()
    {
        HostValues.Clear();
        HostControllers.Clear();
        _nextRead = _nextControllers = double.MinValue;
        _hostSaid = string.Empty;
    }

    /// <summary>Host, at each regular HUD sample: the picks as last read (read again at most twice a second). Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        try
        {
            double now = FrameClock.Seconds;
            if (now >= _nextRead)
            {
                _nextRead = now + ReadEverySeconds;
                ReadPicks(refOf, now);
            }

            foreach (KeyValuePair<string, NetValue> pair in HostValues)
            {
                values[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            Failed("reading the host's match-time picks", ex);
        }
    }

    /// <summary>Friend: playback starts.</summary>
    public static void Begin()
    {
        _pending = false;
        _nextRetry = double.MinValue;
        _followed = 0;
        _borrowsFollowed = 0;
        _talents = null;
    }

    /// <summary>Friend: playback ended.</summary>
    public static void End()
    {
        if (_followed > 0)
        {
            ModLog.Info($"Match picks: this PC's skaters took the host's match-time talent picks {_followed} time(s) (Hot Cake, hot and cold).");
        }

        if (_pending)
        {
            // Retried once a second until the end: a skater the host's picks name was never found here, so none was taken.
            ModLog.Warn("Match picks: the host's latest match-time talent picks were never taken here (a skater they name wasn't found on this PC); " +
                "this PC's skaters kept the picks they had.");
        }

        _pending = false;
        _talents = null;
    }

    /// <summary>Friend: after each HUD message; follows when the host's picks changed, or retries one that couldn't be followed yet. Never throws.</summary>
    public static void Follow(PresentationValues values, Func<NetValue, Type, object?> resolve)
    {
        double now = FrameClock.Seconds;
        if (!values.PicksChanged && !(_pending && now >= _nextRetry))
        {
            return;
        }

        _nextRetry = now + RetryEverySeconds;
        try
        {
            _pending = !Apply(values, resolve);
        }
        catch (Exception ex)
        {
            _pending = false;
            Failed("following the host's match-time picks", ex);
        }
    }

    private static void ReadPicks(Func<UnityEngine.Object, NetValue> refOf, double now)
    {
        if (now >= _nextControllers || HostControllers.Any(c => c.Controller == null))
        {
            _nextControllers = now + ControllersEverySeconds;
            HostControllers.Clear();
            HostBorrowers.Clear();
            foreach (Team team in Teams())
            {
                HostControllers.AddRange(Controllers(team));
                HostBorrowers.AddRange(Borrowers(team));
            }
        }

        var picks = new List<(Forward Forward, TalentPick Pick)>();
        foreach ((RelicControllerBase controller, PickSource source) in HostControllers)
        {
            foreach ((Forward forward, Talent talent) in Given(controller, source))
            {
                if (picks.Count < PresentationKeys.MaxPicks && talent.id is { Length: > 0 and <= TalentPick.MaxIdLength } id)
                {
                    picks.Add((forward, new TalentPick(source, id, Math.Clamp(talent.level, 0, TalentPick.MaxLevel))));
                }
            }
        }

        foreach ((Forward forward, BenchBorrowerController borrower) in HostBorrowers)
        {
            if (forward != null && borrower != null && borrower._borrowedTalent is Talent borrowed && picks.Count < PresentationKeys.MaxPicks &&
                borrowed.id is { Length: > 0 and <= TalentPick.MaxIdLength } borrowedId)
            {
                picks.Add((forward, new TalentPick(PickSource.BenchBorrower, borrowedId, Math.Clamp(borrowed.level, 0, TalentPick.MaxLevel))));
            }
        }

        HostValues.Clear();
        for (int i = 0; i < picks.Count; i++)
        {
            HostValues.Add(new(PresentationKeys.PickWho(i), refOf(picks[i].Forward)));
            HostValues.Add(new(PresentationKeys.PickTalent(i), NetValue.FromString(picks[i].Pick.Format())));
        }

        HostValues.Add(new(PresentationKeys.PickCount, NetValue.FromInt(picks.Count)));
        string said = string.Join(", ", picks.Select(p => $"{SafeName(p.Forward)}: {p.Pick.Source} {p.Pick.TalentId} level {p.Pick.Level}"));
        if (said != _hostSaid)
        {
            _hostSaid = said;
            if (picks.Count > 0)
            {
                ModLog.Info($"Match picks: the host's match-time talent picks ({picks.Count}): {said}; friends' skaters take the same ones (HUD pick.*).");
            }
        }
    }

    /// <summary>Friend: this PC's picks made the host's. False when one can't be followed yet (its skater isn't mapped here).</summary>
    private static bool Apply(PresentationValues values, Func<NetValue, Type, object?> resolve)
    {
        var host = new List<(Forward Forward, TalentPick Pick)>();
        for (int i = 0; i < values.PickCount; i++)
        {
            if (!values.TryPick(i, out NetValue who, out TalentPick pick))
            {
                continue;
            }

            if (resolve(who, typeof(Forward)) is not Forward forward || forward == null)
            {
                return false;
            }

            host.Add((forward, pick));
        }

        var controllers = new List<(RelicControllerBase Controller, PickSource Source)>();
        var borrowers = new List<(Forward Forward, BenchBorrowerController Borrower)>();
        foreach (Team team in Teams())
        {
            controllers.AddRange(Controllers(team));
            borrowers.AddRange(Borrowers(team));
        }

        var changedBorrowers = FollowBorrowers(host.Where(h => h.Pick.Source == PickSource.BenchBorrower).ToList(), borrowers);
        host = host.Where(h => h.Pick.Source != PickSource.BenchBorrower).ToList();

        var here = controllers.SelectMany(c => Given(c.Controller, c.Source).Select(g => (g.Forward, Pick: new TalentPick(c.Source, g.Talent.id ?? string.Empty, g.Talent.level)))).ToList();
        var changed = new Dictionary<IntPtr, Forward>();
        foreach (Forward borrowerChanged in changedBorrowers)
        {
            changed[borrowerChanged.Pointer] = borrowerChanged;
        }

        if (Same(host, here))
        {
            Realign(changed.Values, here.Count, 0, host, new List<string>());
            return true;
        }

        foreach ((RelicControllerBase controller, PickSource source) in controllers)
        {
            Il2CppSystem.Collections.Generic.Dictionary<Forward, Talent>? given = Dictionary(controller, source);
            if (given == null)
            {
                continue;
            }

            foreach ((Forward forward, Talent talent) in Given(controller, source))
            {
                forward.RemoveTalent(talent, false);
                changed[forward.Pointer] = forward;
            }

            given.Clear();
        }

        _talents ??= MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p));
        int givenHere = 0;
        var missing = new List<string>();
        foreach ((Forward forward, TalentPick pick) in host)
        {
            Il2CppSystem.Collections.Generic.Dictionary<Forward, Talent>? record =
                controllers.Where(c => c.Source == pick.Source).Select(c => Dictionary(c.Controller, c.Source)).FirstOrDefault(d => d != null);
            Talent? talent = NewTalent(pick);
            if (talent == null || record == null)
            {
                missing.Add($"{pick.TalentId} ({(talent == null ? "not in this PC's talents" : $"no {pick.Source} here")})");
                continue;
            }

            forward.AddTalent(talent, false);
            record[forward] = talent;
            changed[forward.Pointer] = forward;
            givenHere++;
        }

        Realign(changed.Values, here.Count, givenHere, host, missing);
        return true;
    }

    /// <summary>Takes over the replay components of every skater whose talents changed, and says what was done.</summary>
    private static void Realign(IEnumerable<Forward> changed, int takenBack, int givenHere, List<(Forward Forward, TalentPick Pick)> host, List<string> missing)
    {
        var realigned = new List<string>();
        int count = 0;
        foreach (Forward forward in changed)
        {
            count++;
            if (StreamPlayer.Realign?.Invoke(forward.gameObject) is string outcome)
            {
                realigned.Add($"{SafeName(forward)}: {outcome}");
            }
        }

        if (count == 0)
        {
            return;
        }

        _followed++;
        ModLog.Info($"Match picks: this PC's skaters took the host's match-time talent picks: {givenHere} relic pick(s) given ({string.Join(", ", host.Select(h => $"{SafeName(h.Forward)} {h.Pick.TalentId}"))}), " +
            $"{takenBack} of this PC's own taken back; {_borrowsFollowed} bench borrow(s) followed in all{(missing.Count > 0 ? $"; not given: {string.Join(", ", missing.Take(4))}" : string.Empty)}" +
            $"{(realigned.Count > 0 ? $"; replay components taken over again: {string.Join("; ", realigned.Take(6))}" : string.Empty)}.");
    }

    /// <summary>
    /// Friend: each bench borrower's talent as the host's: a skater whose borrowed talent differs gives its own back
    /// (<c>RemoveTalent</c>) and borrows a fresh copy of the host's, named as the game names one; a borrower the host has
    /// none for gives its own back. Returns the skaters whose talents changed.
    /// </summary>
    private static List<Forward> FollowBorrowers(List<(Forward Forward, TalentPick Pick)> host, List<(Forward Forward, BenchBorrowerController Borrower)> borrowers)
    {
        var changed = new List<Forward>();
        foreach ((Forward forward, BenchBorrowerController borrower) in borrowers)
        {
            Talent? own = borrower._borrowedTalent;
            TalentPick? wanted = host.Where(h => h.Forward.Pointer == forward.Pointer).Select(h => (TalentPick?)h.Pick).FirstOrDefault();
            if (wanted is TalentPick same && own != null && own.id == same.TalentId && own.level == same.Level)
            {
                continue;
            }

            if (wanted == null && own == null)
            {
                continue;
            }

            if (own != null)
            {
                forward.RemoveTalent(own, false);
                borrower._borrowedTalent = null;
            }

            if (wanted is TalentPick pick)
            {
                _talents ??= MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p));
                if (NewTalent(pick) is Talent borrowed)
                {
                    borrowed.name = $"{borrowed.name}{BorrowedSuffix}";
                    forward.AddTalent(borrowed, false);
                    borrower._borrowedTalent = borrowed;
                }
            }

            _borrowsFollowed++;
            changed.Add(forward);
        }

        return changed;
    }

    private static IEnumerable<(Forward Forward, BenchBorrowerController Borrower)> Borrowers(Team team)
    {
        var talents = new Il2CppSystem.Collections.Generic.List<Il2CppRogue.Powerups.Controllers.Talents.TalentControllerBase>();
        foreach (Forward forward in GameMenuCapture.Forwards(team))
        {
            talents.Clear();
            forward.GetAllTalentControllers(talents);
            for (int i = 0; i < talents.Count; i++)
            {
                if (talents[i]?.TryCast<BenchBorrowerController>() is BenchBorrowerController borrower)
                {
                    yield return (forward, borrower);
                }
            }
        }
    }

    /// <summary>A fresh copy of the pick's talent at its level, from the game's talent repository, or null.</summary>
    private static Talent? NewTalent(TalentPick pick)
    {
        Talent? talent = _talents?.GetTalent(pick.TalentId, true);
        if (talent != null && talent.level != pick.Level)
        {
            talent = _talents!.GetTalent(talent.GetIl2CppType(), pick.Level, true);
        }

        return talent != null && talent.level == pick.Level ? talent : null;
    }

    private static bool Same(List<(Forward Forward, TalentPick Pick)> a, List<(Forward Forward, TalentPick Pick)> b)
    {
        static IEnumerable<string> Keys(List<(Forward Forward, TalentPick Pick)> list) =>
            list.Select(p => $"{p.Forward.Pointer}:{p.Pick.Format()}").OrderBy(k => k, StringComparer.Ordinal);
        return Keys(a).SequenceEqual(Keys(b));
    }

    private static IEnumerable<Team> Teams()
    {
        GameManager? game = GameMenuCapture.Game();
        foreach (Team? team in new[] { game?._homeTeam, game?._visitorTeam })
        {
            if (team != null)
            {
                yield return team;
            }
        }
    }

    private static IEnumerable<(RelicControllerBase Controller, PickSource Source)> Controllers(Team team)
    {
        Il2CppSystem.Collections.Generic.List<RelicControllerBase>? list = team.relicControllers;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            RelicControllerBase? controller = list[i];
            if (controller == null)
            {
                continue;
            }

            if (controller.TryCast<HotCakeController>() != null)
            {
                yield return (controller, PickSource.HotCake);
            }
            else if (controller.TryCast<HotColdRelicController>() != null)
            {
                yield return (controller, PickSource.HotCold);
            }
        }
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<Forward, Talent>? Dictionary(RelicControllerBase controller, PickSource source) =>
        source == PickSource.HotCake ? controller.TryCast<HotCakeController>()?._hotCakeTalents : controller.TryCast<HotColdRelicController>()?._talents;

    /// <summary>What the controller gave, as a copy (the friend's side changes the dictionary while it goes over it).</summary>
    private static List<(Forward Forward, Talent Talent)> Given(RelicControllerBase controller, PickSource source)
    {
        var given = new List<(Forward Forward, Talent Talent)>();
        Il2CppSystem.Collections.Generic.Dictionary<Forward, Talent>? dictionary = Dictionary(controller, source);
        if (dictionary == null)
        {
            return given;
        }

        foreach (Il2CppSystem.Collections.Generic.KeyValuePair<Forward, Talent> pair in dictionary)
        {
            if (pair.Key != null && pair.Value != null)
            {
                given.Add((pair.Key, pair.Value));
            }
        }

        return given;
    }

    private static string SafeName(Forward forward) => SkaterLogText.Of(forward);

    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Match picks: {what} failed", ex);
        }
    }
}
#endif
