#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppSkaters;
using Il2CppState;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// The golfer boss's forms on a friend's PC. In the Golfers' boss match the
/// game's <c>GolferBossTierSystem</c> (static) turns golfers into mutants and giant mutants as the player's lead grows:
/// its listeners (the end of a goal replay or a period, the next faceoff) pick the transformations and play them at the
/// faceoff (camera, the pre-transform speech, a Spine transformation), swapping each golfer's data at the animation's
/// swap event (<c>ApplyFormOnce</c> → <c>ApplyData</c>: <c>Forward.Initialize</c> with the mutant's data, its size and
/// mass, and <c>SpawnExtraSwapVfx</c>). Those listeners are logic that client mode detaches on a friend, and the stream
/// carries the skaters' transforms and animations but not their data or skins, so a friend's golfers kept their first
/// form while the host's turned.
/// <list type="bullet">
/// <item>Host: the HUD carries each transformer's forward (a reference) and form
/// (<see cref="PresentationKeys.GolferWho"/>, <see cref="PresentationKeys.GolferForm"/>), while the system has any.</item>
/// <item>Friend: when the host's form for a forward differs from this PC's, the game's own
/// <c>GolferBossTierSystem.SetFormImmediate</c> gives this PC's golfer that form (its data, skin and size), and a
/// transformation (a higher form) gets the game's own swap effect (<c>SpawnExtraSwapVfx</c>). This PC's transformers are
/// built by the game's own <c>OnGameInitializationDone</c> if its listener never ran here (it's raised once the match
/// starts, after client mode is on).</item>
/// <item>The look during a transformation (<c>ShowAsDrawn</c>: its Spine animation is drawn for an unmirrored skeleton
/// and forces a glove, the settings' <c>gloveAttachment</c>): the host's HUD says which forward has it
/// (<see cref="PresentationKeys.GolferDrawn"/>, <c>_drawnForward</c>), and a friend's golfer gets it and loses it with the
/// game's own call, in the same order as the form. Playback's end takes it off.</item>
/// </list>
/// Logged once per change. Never throws.
/// </summary>
internal static class GolferTiers
{
    private static readonly ErrorLogLimit Errors = new(3);
    private static bool _built;
    private static int _applied;
    private static int _hostCount;
    private static IntPtr _hostFirst;
    private static int _drawnShown;
    private static Forward? _drawn;
    private static bool _hostDrawnSaid;

    /// <summary>Host, at each regular HUD sample: each transformer's forward and form. Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<GolferBossTierSystem.Transformer>? transformers = GolferBossTierSystem._transformers;
            int count = transformers == null ? 0 : Math.Min(transformers.Count, PresentationKeys.MaxGolfers);
            IntPtr first = IntPtr.Zero;
            for (int i = 0; i < count; i++)
            {
                GolferBossTierSystem.Transformer t = transformers![i];
                Forward? forward = t?.forward;
                if (t == null || forward == null)
                {
                    continue;
                }

                first = first == IntPtr.Zero ? forward.Pointer : first;
                values[PresentationKeys.GolferWho(i)] = refOf(forward);
                values[PresentationKeys.GolferForm(i)] = NetValue.FromInt((int)t.applied);
            }

            // A new set of transformers (another match's forwards, the same count or not): its lines are said again. The
            // stream starts after the game has built them, so the count alone stays the same from one golfer match to the next.
            if (count != _hostCount || first != _hostFirst)
            {
                _hostCount = count;
                _hostFirst = first;
                _hostDrawnSaid = false;
                if (count > 0)
                {
                    ModLog.Info($"Golfer boss: {count} golfer(s) can transform in this match; friends' games follow their forms (HUD golfer.*).");
                }
            }

            if (count > 0)
            {
                Forward? drawn = GolferBossTierSystem._drawnForward;
                values[PresentationKeys.GolferDrawn] = drawn != null ? refOf(drawn) : NetValue.Null;
                if (drawn != null && !_hostDrawnSaid)
                {
                    _hostDrawnSaid = true;
                    ModLog.Info($"Golfer boss: {SafeName(drawn)} transforms here; friends' golfers take the same look with it (HUD golfer.drawn; further ones aren't logged).");
                }
            }
        }
        catch (Exception ex)
        {
            Failed("reading the golfer boss's forms", ex);
        }
    }

    /// <summary>Friend: playback starts.</summary>
    public static void Begin()
    {
        _built = false;
        _applied = 0;
        _drawnShown = 0;
        _drawn = null;
    }

    /// <summary>Friend: playback ended (<paramref name="matchLoaded"/>: its golfers are still here): a look still on comes off.</summary>
    public static void End(bool matchLoaded)
    {
        if (_applied > 0)
        {
            ModLog.Info($"Golfer boss: {_applied} form change(s) of the host's golfers were applied here" +
                $"{(_drawnShown > 0 ? $", and the transformation's look {_drawnShown} time(s)" : string.Empty)}.");
        }

        if (matchLoaded)
        {
            try
            {
                TakeDrawnOff();
            }
            catch (Exception ex)
            {
                Failed("taking the transformation's look off", ex);
            }
        }

        _drawn = null;
    }

    /// <summary>Friend: the host's forms changed (a HUD message): this PC's golfers take them. Never throws.</summary>
    public static void Follow(PresentationValues values, Func<NetValue, Type, object?> resolve)
    {
        try
        {
            FollowDrawn(values, resolve);
        }
        catch (Exception ex)
        {
            Failed("following the transformation's look", ex);
        }

        try
        {
            for (int i = 0; i < PresentationKeys.MaxGolfers; i++)
            {
                if (!values.TryGolfer(i, out NetValue who, out int form) || resolve(who, typeof(Forward)) is not Forward forward || forward == null)
                {
                    continue;
                }

                GolferBossTierSystem.Transformer? local = Find(forward);
                if (local == null && !_built)
                {
                    // This PC's tier system never ran its match start (a listener client mode detached): the game's own.
                    _built = true;
                    GolferBossTierSystem.OnGameInitializationDone();
                    ModLog.Info($"Golfer boss: this PC's golfer transformers were built with the game's own OnGameInitializationDone " +
                        $"({GolferBossTierSystem._transformers?.Count ?? 0} found), to follow the host's forms.");
                    local = Find(forward);
                }

                int here = local == null ? -1 : (int)local.applied;
                if (local == null || here == form)
                {
                    continue;
                }

                GolferBossTierSystem.SetFormImmediate(local, (GolferBossTierSystem.Form)form);
                if (form > here)
                {
                    GolferBossTierSystem.SpawnExtraSwapVfx(forward, GolferBossTierSystem.LoadSettings());
                }

                _applied++;
                ModLog.Info($"Golfer boss: the host's {SafeName(forward)} is now {GolferForms.Name(form)} (was {GolferForms.Name(here)} here); " +
                    "this PC's golfer took the same form with the game's own SetFormImmediate.");
            }
        }
        catch (Exception ex)
        {
            Failed("following the golfer boss's forms", ex);
        }
    }

    /// <summary>
    /// The host's transformation shows a golfer as drawn (or no longer): this PC's takes the same look with the game's own
    /// <c>ShowAsDrawn</c> (a look on another golfer comes off first, as the host's would have before this one's began).
    /// </summary>
    private static void FollowDrawn(PresentationValues values, Func<NetValue, Type, object?> resolve)
    {
        Forward? now = values.GolferDrawn.Kind == NetValueKind.Reference ? resolve(values.GolferDrawn, typeof(Forward)) as Forward : null;
        if (now == null ? _drawn == null : _drawn != null && _drawn.Pointer == now.Pointer)
        {
            return;
        }

        TakeDrawnOff();
        if (now == null)
        {
            return;
        }

        GolferBossTierSystem.ShowAsDrawn(now, true, GolferBossTierSystem.LoadSettings()?.gloveAttachment);
        _drawn = now;
        if (_drawnShown++ == 0)
        {
            ModLog.Info($"Golfer boss: the host's {SafeName(now)} transforms; this PC's golfer takes the same look with the game's own ShowAsDrawn (further ones are counted).");
        }
    }

    /// <summary>The look on this PC's golfer comes off, when it's still here.</summary>
    private static void TakeDrawnOff()
    {
        Forward? drawn = _drawn;
        _drawn = null;
        if (drawn != null && !drawn.WasCollected)
        {
            GolferBossTierSystem.ShowAsDrawn(drawn, false);
        }
    }

    /// <summary>This PC's transformer for <paramref name="forward"/>, or null.</summary>
    private static GolferBossTierSystem.Transformer? Find(Forward forward)
    {
        Il2CppSystem.Collections.Generic.List<GolferBossTierSystem.Transformer>? transformers = GolferBossTierSystem._transformers;
        for (int i = 0; transformers != null && i < transformers.Count; i++)
        {
            GolferBossTierSystem.Transformer t = transformers[i];
            if (t?.forward != null && t.forward.Pointer == forward.Pointer)
            {
                return t;
            }
        }

        return null;
    }

    private static string SafeName(Forward forward) => SkaterLogText.Of(forward);

    /// <summary>Logs the first few failures of each kind.</summary>
    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Golfer boss: {what} failed", ex);
        }
    }
}
#endif
