#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using MelonLoader;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;

namespace T2TOnline.Plugin.Online;

// Every friend's own maps, kept on their virtual controller for the whole session. Nothing in
// the game or in Rewired as the game configures it reloads, clears or replaces a custom controller's maps (the game only
// switches categories on and off for a whole player; its own map loads are the keyboard's, the mouse's and joysticks'), so
// this is a guard, not a fix of a known cause: each friend's maps as the mod built them (Core's MapsGuard), a cheap look a
// few times a second at which categories are on (the game's switches, named in the log), a full check every few seconds,
// at once when the categories changed, and a second after a scene loads; drift put back from the friend's controls (logged
// once per kind, then counted); nothing else on the friend's Rewired player; a summary when they go.
internal sealed partial class OnlineSession
{
    private static readonly TimeSpan MapsCheckInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MapsLookInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MapsCheckAfterSceneLoad = TimeSpan.FromSeconds(1);
    private const int MapsGuardFailuresAllowed = 3;

    private static bool _mapAutomationLogged;
    private readonly MapsCheckSchedule _mapsSchedule = new(MapsCheckInterval);
    private readonly List<RemotePad> _mapsFriends = new();
    private TimeSpan _nextMapsLook;
    private int _mapsGuardFailures;
    private bool _mapsGuardOff;

    /// <summary>A scene loading asks for a check of every friend's maps a second later (the game sets its players up again).</summary>
    private void InstallMapsGuard() => MelonEvents.OnSceneWasLoaded.Subscribe(OnSceneLoadedForMaps);

    private void OnSceneLoadedForMaps(int buildIndex, string name)
    {
        try
        {
            if (_host != null && !_mapsGuardOff)
            {
                _mapsSchedule.Request(_clock.Elapsed + MapsCheckAfterSceneLoad, $"scene \"{SafeText.Clean(name, 32)}\" loaded");
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Online: the friends' maps guard couldn't note a scene load ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>
    /// Host: the mod just built <paramref name="remote"/>'s virtual controller maps (<paramref name="what"/>: made, their
    /// controls applied): read back, they're what the controller must keep, and the log says so with a fingerprint. Never
    /// throws: maps that can't be read aren't guarded until the next build.
    /// </summary>
    private static void NoteMapsBuilt(RemotePad remote, string what)
    {
        try
        {
            VirtualController controller = remote.Pad;
            ControllerMapsPrint built = controller.ReadMaps();
            remote.Maps.NoteApplied(built);
            ModLog.Info($"Online: {remote.Tag}'s {controller.Kind.ToLowerInvariant()} maps on Rewired player {controller.RewiredPlayerId}: {what}; " +
                string.Create(CultureInfo.InvariantCulture, $"{built.Bindings} bindings ({built.Counts(RewiredBindings.CategoryName)}), fingerprint {built.Tag}, ") +
                $"categories on {ControllerMapsPrint.NameSet(built.Enabled, RewiredBindings.CategoryName)}. Kept like this for the session (checked every " +
                string.Create(CultureInfo.InvariantCulture, $"{MapsCheckInterval.TotalSeconds:0} s).") + $" Applied {remote.Maps.Applied} time(s) for them so far.");
            if (!_mapAutomationLogged)
            {
                _mapAutomationLogged = true;
                ModLog.Info($"Online: Rewired's map automation on Rewired player {controller.RewiredPlayerId}: {VirtualController.MapAutomation(controller.Player)}. " +
                    "Without rule sets neither loads nor switches any map; the game's own map loads are the keyboard's, the mouse's and joysticks', never a virtual controller's.");
            }
        }
        catch (Exception ex)
        {
            remote.Maps.ForgetExpected();
            ModLog.Warn($"Online: {remote.Tag}'s maps couldn't be read back after they were built ({ModLog.SafeErrorText(ex)}); they aren't checked until they're built again.");
        }
    }

    /// <summary>
    /// Host, every frame: a look at each friend's maps every <see cref="MapsLookInterval"/> (a full check of a friend whose
    /// categories changed), and a full check of every friend's when one is due (<see cref="MapsCheckInterval"/>, or a scene
    /// loaded). Never throws: three failures switch the guard off for this game, said once; friends' maps are still built.
    /// </summary>
    private void GuardFriendMaps(TimeSpan elapsed)
    {
        if (_mapsGuardOff || _host == null || (_pads.Count == 0 && _parked.Count == 0))
        {
            return;
        }

        try
        {
            _mapsFriends.Clear();
            _mapsFriends.AddRange(_pads.Values);
            foreach ((RemotePad pad, TimeSpan _) in _parked.Values)
            {
                _mapsFriends.Add(pad);
            }

            if (_mapsSchedule.Due(elapsed, out string why))
            {
                _mapsSchedule.Done(elapsed);
                _nextMapsLook = elapsed + MapsLookInterval;
                long frameStarted = Stopwatch.GetTimestamp();
                foreach (RemotePad remote in _mapsFriends)
                {
                    LookAtFriendMaps(remote);
                    CheckFriendMaps(remote, why);
                }

                NoteMapsFrame(Stopwatch.GetTimestamp() - frameStarted, _mapsFriends.Count);
                return;
            }

            if (elapsed < _nextMapsLook)
            {
                return;
            }

            _nextMapsLook = elapsed + MapsLookInterval;
            foreach (RemotePad remote in _mapsFriends)
            {
                if (LookAtFriendMaps(remote))
                {
                    CheckFriendMaps(remote, "its map categories changed");
                }
            }
        }
        catch (Exception ex)
        {
            if (++_mapsGuardFailures >= MapsGuardFailuresAllowed)
            {
                _mapsGuardOff = true;
                ModLog.Error("Online: the friends' maps guard failed three times; it's off for the rest of this game (friends' maps are still built from their controls)", ex);
            }
            else
            {
                ModLog.Warn($"Online: the friends' maps guard failed ({ModLog.SafeErrorText(ex)}); it tries again.");
            }
        }
    }

    /// <summary>
    /// The cheap look: which of the friend's maps are there and on. True when that changed since the last look (the game
    /// switched categories, or a map came or went); the first changes are named in the log.
    /// </summary>
    private static bool LookAtFriendMaps(RemotePad remote)
    {
        long started = Stopwatch.GetTimestamp();
        List<(int Category, bool Enabled)> maps = remote.Pad.LookAtMaps();
        long micros = (Stopwatch.GetTimestamp() - started) * 1_000_000 / Stopwatch.Frequency;
        (long count, long total, long max) = remote.MapsLooks;
        remote.MapsLooks = (count + 1, total + micros, Math.Max(max, micros));
        NoteMapsLook(micros);
        if (!remote.Maps.Watch(maps, out bool name, out IReadOnlyList<int> was))
        {
            return false;
        }

        if (name)
        {
            IEnumerable<int> on = maps.Where(m => m.Enabled).Select(m => m.Category).Distinct().OrderBy(c => c);
            ModLog.Info($"Online: {remote.Tag}'s {remote.Pad.Kind.ToLowerInvariant()} on Rewired player {remote.Pad.RewiredPlayerId}: the game switched its map categories to " +
                $"{ControllerMapsPrint.NameSet(on, RewiredBindings.CategoryName)} (were {ControllerMapsPrint.NameSet(was, RewiredBindings.CategoryName)})" +
                (remote.Maps.CategoryChanges == MapsGuard.DefaultCategoryChangesNamed ? "; further switches are only counted." : "."));
        }

        return true;
    }

    /// <summary>
    /// A full check of one friend's maps (<paramref name="why"/> it's due): drift is put back from their controls and
    /// logged once per kind (WARN), then counted; anything else on their Rewired player is named, and a joystick moved off.
    /// </summary>
    private static void CheckFriendMaps(RemotePad remote, string why)
    {
        long started = Stopwatch.GetTimestamp();
        VirtualController controller = remote.Pad;
        ControllerMapsPrint now = controller.ReadMaps();
        MapsVerdict verdict = remote.Maps.Check(now, out MapsDrift? drift, out bool logIt);
        CheckFriendPlayerAlone(remote);
        if ((verdict is MapsVerdict.Fix or MapsVerdict.CountOnly) && drift != null && remote.Maps.Expected is { } expected)
        {
            string kind = controller.Kind.ToLowerInvariant();
            string what = drift.Describe(expected, now, RewiredBindings.CategoryName);
            if (verdict == MapsVerdict.CountOnly)
            {
                if (logIt)
                {
                    ModLog.Warn($"Online: {remote.Tag}'s {kind} maps on Rewired player {controller.RewiredPlayerId} drifted again ({why}): {what}. Not put back: that failed " +
                        $"{MapsGuard.DefaultMaxFailedFixes} times in a row; counted until their controls are applied again.");
                }
            }
            else
            {
                (int removed, int loaded) = controller.RestoreMaps(expected, remote.Maps.LastGoodEnabled);
                ControllerMapsPrint after = controller.ReadMaps();
                bool put = remote.Maps.NoteFix(after, out bool gaveUp);
                string how = (removed > 0 ? string.Create(CultureInfo.InvariantCulture, $", {removed} map(s) it never made removed") : string.Empty) +
                    (loaded > 0 ? string.Create(CultureInfo.InvariantCulture, $", {loaded} missing map(s) loaded again, on as last seen {ControllerMapsPrint.NameSet(remote.Maps.LastGoodEnabled, RewiredBindings.CategoryName)}") : string.Empty);
                if (put && logIt)
                {
                    ModLog.Warn($"Online: {remote.Tag}'s {kind} maps on Rewired player {controller.RewiredPlayerId} had drifted ({why}): {what}. Put back from their controls " +
                        $"(fingerprint {expected.Tag} again{how}); this kind of drift is only counted from now on.");
                }
                else if (!put && (logIt || ++remote.UnfixedWarned <= UnfixedWarnings))
                {
                    // Bounded: a drift that's put back and comes again would say this at every check.
                    ModLog.Warn($"Online: {remote.Tag}'s {kind} maps on Rewired player {controller.RewiredPlayerId} drifted ({why}: {what}) and couldn't be put back{how}: " +
                        $"they're {after.Counts(RewiredBindings.CategoryName)} ({after.Tag}), and should be {expected.Counts(RewiredBindings.CategoryName)} ({expected.Tag})" +
                        (gaveUp ? "; not tried again until their controls are applied again." : "."));
                }
            }
        }

        double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        remote.SlowestMapsCheckMs = Math.Max(remote.SlowestMapsCheckMs, ms);
        remote.MapsChecks.Add((long)(ms * 1000));
    }

    /// <summary>
    /// A friend's Rewired player is theirs alone: their virtual controller, nothing else. Anything else found there (a pad
    /// Rewired's saved assignments gave it, the keyboard or mouse) would drive their game player with another device's
    /// maps: named once per kind (WARN), and a joystick moved to where the game puts a pad that connects. The one-PC test's
    /// pad is the mod's own (its guard takes it back every input update).
    /// </summary>
    private static void CheckFriendPlayerAlone(RemotePad remote)
    {
        int held = LocalPad.Held?.Joystick.id ?? -1;
        List<string> foreign = remote.Pad.ForeignDevices(held);
        if (foreign.Count == 0)
        {
            return;
        }

        remote.ForeignSeen++;
        List<string> moved = remote.Pad.MoveForeignJoysticks(held);
        string kinds = string.Join(", ", foreign.Select(f => f.StartsWith("a joystick", StringComparison.Ordinal) ? "a joystick" : f));
        if (remote.ForeignLogged.Add(kinds))
        {
            ModLog.Warn($"Online: {remote.Tag}'s Rewired player {remote.Pad.RewiredPlayerId} also had {string.Join(", ", foreign)}, which would play their game player with " +
                $"another device's maps{(moved.Count > 0 ? $"; moved {string.Join("; ", moved)}" : string.Empty)}. Found again, it's only counted.");
        }
    }

    /// <summary>A friend's summary line: how often their maps were built, checked and put back, and how often the game switched categories.</summary>
    private static void LogMapsSummary(RemotePad remote, string when)
    {
        try
        {
            ModLog.Info($"Online: {remote.Tag}'s controls {when}: {remote.Maps.Summary()}" +
                string.Create(CultureInfo.InvariantCulture, $"; slowest check {remote.SlowestMapsCheckMs:0.00} ms") +
                string.Create(CultureInfo.InvariantCulture, $"; full checks mean/p95/max {remote.MapsChecks.Mean / 1000:0.00}/{remote.MapsChecks.Percentile(0.95) / 1000.0:0.00}/{remote.MapsChecks.Percentile(1) / 1000.0:0.00} ms") +
                string.Create(CultureInfo.InvariantCulture, $"; looks {remote.MapsLooks.Count} (mean/max {(remote.MapsLooks.Count == 0 ? 0 : remote.MapsLooks.Micros / remote.MapsLooks.Count)}/{remote.MapsLooks.Max} µs)") +
                (remote.ForeignSeen > 0 ? string.Create(CultureInfo.InvariantCulture, $"; another device found on their Rewired player {remote.ForeignSeen} time(s)") : string.Empty) + ".");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Online: {remote.Tag}'s controls summary failed ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
