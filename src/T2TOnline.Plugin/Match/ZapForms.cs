#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppData;
using Il2CppRogue.Powerups.Components;
using Il2CppSkaters;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// Zapped skaters on a friend's PC as on the host's. The zap ability (<c>ZapController.Use</c>) puts a
/// <c>ZapComponent</c> on the skater it hits, which turns it into a random other skater (<c>Transform</c>, a
/// <c>RandomRangeInt</c> pick: <c>_Transform</c> runs <c>Forward.Initialize</c> with that skater's data and its talents
/// again) and back after a while (<c>Revert</c>, to its <c>_initialPlayer</c>). That's the host's gameplay only, and the
/// stream carries transforms and animations, not skater data, so a friend's copy kept its old self.
/// <list type="bullet">
/// <item>Host: the HUD carries each zapped skater and the asset id of the skater it is now
/// (<see cref="ForwardSlots.Zaps"/>), read twice a second.</item>
/// <item>Friend: on a change, a skater the host shows as another gets this PC's own <c>ZapComponent</c> (made once, its
/// <c>_initialPlayer</c> the skater's own data) and the game's own <c>_Transform</c> with a fresh copy of that skater's data
/// (the game's skater repository); one the host shows as itself again gets the game's own <c>Revert</c>. Its replay
/// components are taken over again. The component's own timer never runs here (it times only its own random zaps).</item>
/// </list>
/// Logged once per change; a zap into a skater this PC doesn't have only for the match's first few (<see cref="ZapTally"/>).
/// Never throws.
/// </summary>
internal static class ZapForms
{
    private const double ReadEverySeconds = 0.5;

    private static readonly ErrorLogLimit Errors = new(3);
    private static readonly List<KeyValuePair<string, NetValue>> HostValues = new();
    private static readonly ForwardSlots Keys = ForwardSlots.Zaps();
    private static readonly ZapTally Tally = new();
    private static double _nextRead = double.MinValue;
    private static int _hostSlots;
    private static IForwardRepository? _skaters;

    /// <summary>Host: a new stream starts.</summary>
    public static void HostStart()
    {
        HostValues.Clear();
        _nextRead = double.MinValue;
        _hostSlots = 0;
    }

    /// <summary>Host, at each regular HUD sample: the zapped skaters as last read. Never throws.</summary>
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
            Failed("reading the host's zaps", ex);
        }
    }

    /// <summary>Friend: playback starts.</summary>
    public static void Begin()
    {
        Tally.Begin();
        _skaters = null;
    }

    /// <summary>Friend: playback ended.</summary>
    public static void End()
    {
        if (Tally.EndLine() is { } line)
        {
            ModLog.Info(line);
        }

        _skaters = null;
    }

    /// <summary>Friend: on a change of the host's zaps, this PC's skaters take the same forms. Never throws.</summary>
    public static void Follow(PresentationValues values, Func<NetValue, Type, object?> resolve)
    {
        if (!values.Zaps.Changed)
        {
            return;
        }

        try
        {
            var host = new Dictionary<IntPtr, string>();
            for (int i = 0; i < ForwardSlots.MaxSlots; i++)
            {
                if (values.Zaps.TryGet(i, out NetValue who, out NetValue form) && resolve(who, typeof(Forward)) is Forward forward && forward != null)
                {
                    host[forward.Pointer] = form.Text ?? string.Empty;
                }
            }

            foreach (Forward forward in AllForwards())
            {
                FollowOne(forward, host.TryGetValue(forward.Pointer, out string? form) ? form : null);
            }
        }
        catch (Exception ex)
        {
            Failed("following the host's zaps", ex);
        }
    }

    private static void FollowOne(Forward forward, string? hostForm)
    {
        ZapComponent? zap = forward.GetComponent<ZapComponent>();
        string now = GameMenuCapture.AssetId(forward);
        if (hostForm == null)
        {
            // The host's isn't zapped (any more): this PC's comes back only if it was zapped here.
            if (zap == null || zap._initialPlayer == null || now == (zap._initialPlayer.OriginalId ?? string.Empty))
            {
                return;
            }

            zap.Revert();
            Tally.AddReverted();
            Said(forward, "itself again (the game's own Revert)");
            return;
        }

        if (hostForm == now)
        {
            return;
        }

        _skaters ??= MenuGame.Repository<IForwardRepository>(p => new IForwardRepository(p));
        ForwardData? data = _skaters?.GetForwardData(hostForm, true);
        if (data == null)
        {
            // An id this PC can't find is only the host's text: written when it has an id's shape, else as "a skater". Nothing
            // changes on the skater, so past the match's first few it is only counted (no line, no realign); the match's
            // closing line says how many.
            if (Tally.AddNotFollowed())
            {
                Said(forward, $"zapped into {ObjectEntryLogText.Skater(hostForm)} on the host, a skater this PC doesn't have; not followed");
            }

            return;
        }

        if (zap == null)
        {
            zap = forward.gameObject.AddComponent<ZapComponent>();
            zap._initialPlayer = forward._forwardData;
        }

        zap._Transform(data);
        Tally.AddZapped();
        Said(forward, $"zapped into {ObjectEntryLogText.Skater(hostForm)} (the game's own _Transform)");
    }

    private static void Said(Forward forward, string what)
    {
        string? realigned = StreamPlayer.Realign?.Invoke(forward.gameObject);
        ModLog.Info($"Zaps: the host's {SkaterLogText.Of(forward)} is {what} here too{(realigned != null ? $" (replay components: {realigned})" : string.Empty)}.");
    }

    private static void Read(Func<UnityEngine.Object, NetValue> refOf)
    {
        HostValues.Clear();
        int slot = 0;
        foreach (Forward forward in AllForwards())
        {
            ZapComponent? zap = slot < ForwardSlots.MaxSlots ? forward.GetComponent<ZapComponent>() : null;
            if (zap == null || !zap.isTransformed || GameMenuCapture.AssetId(forward) is not { Length: > 0 and <= ForwardSlots.MaxIdLength } form)
            {
                continue;
            }

            HostValues.Add(new(Keys.WhoKey(slot), refOf(forward)));
            HostValues.Add(new(Keys.ValueKey(slot), NetValue.FromString(form)));
            slot++;
        }

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

    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Zaps: {what} failed", ex);
        }
    }
}
#endif
