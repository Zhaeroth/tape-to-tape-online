#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
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
/// Frogs on a friend's PC as on the host's. A frog is a status (<c>SkaterStatusEffectsManager.SetFrogged</c>, the
/// "FrogEffect" status): its controller gives the skater the <c>Froggified</c> talent, a frog costume
/// (<c>Forward.Equip</c>), a jump for its ability and a swapped ability icon. Gameplay frogs skaters on the host only (the
/// frogging cultist's spell, a critical shot, frog relics on a faceoff, goal or knockout), and the Canada curse "frog two"
/// frogs a random skater at the match's start on each PC (<c>CanadaCurseFrogTwoController.AddEffect</c>,
/// <c>RandomRangeInt</c>), so a friend's game showed its own pick's frog beside the host's. The stream's skater states carry
/// the costume's skins (<c>ReplayForward.OnReplayDeserialize</c> applies them), not the status, its talent or its icon.
/// <list type="bullet">
/// <item>Host: the HUD carries each frogged skater and its frog time left (<see cref="ForwardSlots.Frogs"/>), read twice a
/// second.</item>
/// <item>Friend: at its first message and on each change, a skater frogged on the host and not here is frogged with the
/// game's own <c>SetFrogged</c> for the host's time left; one frogged here and not on the host is unfrogged with
/// <c>RemoveFrogged</c> (its frog then can't end here on its own: status updates are gameplay, held). Each changed skater's
/// replay components are taken over again.</item>
/// </list>
/// Logged once per change. Never throws.
/// </summary>
internal static class FrogStatuses
{
    private const double ReadEverySeconds = 0.5;

    private static readonly ErrorLogLimit Errors = new(3);
    private static readonly List<KeyValuePair<string, NetValue>> HostValues = new();
    private static readonly ForwardSlots Keys = ForwardSlots.Frogs();
    private static double _nextRead = double.MinValue;
    private static int _hostSlots;
    private static bool _first;
    private static int _frogged;
    private static int _unfrogged;

    /// <summary>Host: a new stream starts.</summary>
    public static void HostStart()
    {
        HostValues.Clear();
        _nextRead = double.MinValue;
        _hostSlots = 0;
    }

    /// <summary>Host, at each regular HUD sample: the frogged skaters as last read. Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        try
        {
            double now = FrameClock.Seconds;
            if (now >= _nextRead)
            {
                _nextRead = now + ReadEverySeconds;
                Read(refOf);
            }

            foreach (KeyValuePair<string, NetValue> pair in HostValues)
            {
                values[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            Failed("reading the host's frogs", ex);
        }
    }

    /// <summary>Friend: playback starts.</summary>
    public static void Begin()
    {
        _first = true;
        _frogged = _unfrogged = 0;
    }

    /// <summary>Friend: playback ended.</summary>
    public static void End()
    {
        if (_frogged + _unfrogged > 0)
        {
            ModLog.Info($"Frogs: {_frogged} of the host's frog(s) put on this PC's skaters, {_unfrogged} of this PC's own taken off.");
        }
    }

    /// <summary>Friend: after each HUD message; at the first and on a change, this PC's frogs become the host's. Never throws.</summary>
    public static void Follow(PresentationValues values, Func<NetValue, Type, object?> resolve)
    {
        if (!_first && !values.Frogs.Changed)
        {
            return;
        }

        _first = false;
        try
        {
            var host = new Dictionary<IntPtr, int>();
            for (int i = 0; i < ForwardSlots.MaxSlots; i++)
            {
                if (values.Frogs.TryGet(i, out NetValue who, out NetValue seconds) && resolve(who, typeof(Forward)) is Forward forward && forward != null)
                {
                    host[forward.Pointer] = (int)seconds.WholeValue;
                }
            }

            var said = new List<string>();
            foreach (Forward forward in AllForwards())
            {
                SkaterStatusEffectsManager? statuses = forward.statusEffectsManager;
                if (statuses == null)
                {
                    continue;
                }

                bool here = statuses.IsFrogged();
                bool there = host.TryGetValue(forward.Pointer, out int seconds);
                if (there == here)
                {
                    continue;
                }

                if (there)
                {
                    statuses.SetFrogged(Math.Max(1, seconds));
                    _frogged++;
                }
                else
                {
                    statuses.RemoveFrogged();
                    _unfrogged++;
                }

                string? realigned = StreamPlayer.Realign?.Invoke(forward.gameObject);
                said.Add($"{SafeName(forward)} {(there ? $"frogged ({seconds} s left on the host)" : "unfrogged")}{(realigned != null ? $" (replay components: {realigned})" : string.Empty)}");
            }

            if (said.Count > 0)
            {
                ModLog.Info($"Frogs: this PC's skaters follow the host's frogs with the game's own SetFrogged and RemoveFrogged: {string.Join("; ", said)}.");
            }
        }
        catch (Exception ex)
        {
            Failed("following the host's frogs", ex);
        }
    }

    private static void Read(Func<UnityEngine.Object, NetValue> refOf)
    {
        HostValues.Clear();
        int slot = 0;
        foreach (Forward forward in AllForwards())
        {
            SkaterStatusEffectsManager? statuses = forward.statusEffectsManager;
            if (slot >= ForwardSlots.MaxSlots || statuses == null || !statuses.IsFrogged())
            {
                continue;
            }

            float left = statuses.GetFroggedTimeRemaining();
            int seconds = float.IsFinite(left) ? Math.Clamp((int)Math.Ceiling(left), 0, ForwardSlots.MaxFrogSeconds) : ForwardSlots.MaxFrogSeconds;
            HostValues.Add(new(Keys.WhoKey(slot), refOf(forward)));
            HostValues.Add(new(Keys.ValueKey(slot), NetValue.FromInt(seconds)));
            slot++;
        }

        // Slots no longer used are emptied, so a friend's frog comes off with the host's.
        for (int i = slot; i < Math.Max(slot, _hostSlots); i++)
        {
            HostValues.Add(new(Keys.WhoKey(i), NetValue.Null));
            HostValues.Add(new(Keys.ValueKey(i), NetValue.Null));
        }

        _hostSlots = slot;
    }

    private static IEnumerable<Forward> AllForwards()
    {
        GameManager? game = GameMenuCapture.Game();
        foreach (Team? team in new[] { game?._homeTeam, game?._visitorTeam })
        {
            if (team != null)
            {
                foreach (Forward forward in GameMenuCapture.Forwards(team))
                {
                    yield return forward;
                }
            }
        }
    }

    private static string SafeName(Forward forward) => SkaterLogText.Of(forward);

    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Frogs: {what} failed", ex);
        }
    }
}
#endif
