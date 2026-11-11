#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppAI;
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
/// The cup cultists' evil twins on a friend's PC. In the Cup Cultists' boss match a cultist's AI
/// (<c>CultistAI</c>, its powers built by the <c>Cultist</c> talent) can cast <c>TransformationCultistAI</c>: at the
/// incantation's success <c>TransformInElite</c> initializes the cultist with its evil twin's skater data
/// (<c>Forward.Initialize</c>, then its talents and ability), for 30 seconds of play, and <c>TransformBack</c> puts its own
/// data back. That AI is gameplay, held on a friend (<c>CultistAI.FixedUpdate</c>), and the stream carries transforms and
/// animations, not skater data, so a friend's cultist stayed itself while the host's was its twin.
/// <list type="bullet">
/// <item>Host: the HUD carries each cultist that can turn and whether it is turned now
/// (<see cref="PresentationKeys.CultistWho"/>, <see cref="PresentationKeys.CultistTurned"/>; the AI's own
/// <c>_isTransformed</c>), the cultists looked for again every 2 seconds.</item>
/// <item>Friend: when the host's form for a cultist differs from this PC's, this PC's own <c>TransformationCultistAI</c> of
/// that cultist runs the game's own <c>TransformInElite</c> or <c>TransformBack</c>, and its replay components are taken
/// over again (<see cref="StreamPlayer.Realign"/>).</item>
/// </list>
/// Logged once per change. Never throws.
/// </summary>
internal static class CultistForms
{
    private const double FindEverySeconds = 2;

    private static readonly ErrorLogLimit Errors = new(3);
    private static readonly List<(Forward Forward, TransformationCultistAI Ai)> HostCultists = new();
    private static double _nextFind = double.MinValue;
    private static int _hostCount = -1;
    private static int _applied;
    private static int _missing;

    /// <summary>Host: a new stream starts.</summary>
    public static void HostStart()
    {
        HostCultists.Clear();
        _nextFind = double.MinValue;
        _hostCount = -1;
    }

    /// <summary>Host, at each regular HUD sample: each cultist that can turn, and whether it is turned. Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        try
        {
            double now = FrameClock.Seconds;
            if (now >= _nextFind)
            {
                _nextFind = now + FindEverySeconds;
                Find(HostCultists);
                if (HostCultists.Count != _hostCount)
                {
                    _hostCount = HostCultists.Count;
                    if (_hostCount > 0)
                    {
                        ModLog.Info($"Cultist boss: {_hostCount} cultist(s) can turn into their evil twin in this match; friends' games follow their forms (HUD cultist.*).");
                    }
                }
            }

            for (int i = 0; i < HostCultists.Count; i++)
            {
                (Forward forward, TransformationCultistAI ai) = HostCultists[i];
                if (forward == null)
                {
                    continue;
                }

                values[PresentationKeys.CultistWho(i)] = refOf(forward);
                values[PresentationKeys.CultistTurned(i)] = NetValue.FromBool(ai._isTransformed);
            }
        }
        catch (Exception ex)
        {
            Failed("reading the cultists' forms", ex);
        }
    }

    /// <summary>Friend: playback starts.</summary>
    public static void Begin()
    {
        _applied = 0;
        _missing = 0;
    }

    /// <summary>Friend: playback ended.</summary>
    public static void End()
    {
        if (_applied + _missing > 0)
        {
            ModLog.Info($"Cultist boss: {_applied} form change(s) of the host's cultists were applied here" +
                $"{(_missing > 0 ? $"; {_missing} couldn't be (no such cultist power here)" : string.Empty)}.");
        }
    }

    /// <summary>Friend: the host's cultist forms changed (a HUD message): this PC's cultists take them. Never throws.</summary>
    public static void Follow(PresentationValues values, Func<NetValue, Type, object?> resolve)
    {
        if (!values.CultistsChanged)
        {
            return;
        }

        for (int i = 0; i < PresentationKeys.MaxCultists; i++)
        {
            try
            {
                if (values.TryCultist(i, out NetValue who, out bool turned) && resolve(who, typeof(Forward)) is Forward forward && forward != null)
                {
                    FollowOne(forward, turned);
                }
            }
            catch (Exception ex)
            {
                Failed("following a cultist's form", ex);
            }
        }
    }

    private static void FollowOne(Forward forward, bool turned)
    {
        TransformationCultistAI? ai = AiOf(forward);
        if (ai == null)
        {
            if (_missing++ == 0)
            {
                ModLog.Warn($"Cultist boss: the host's {SafeName(forward)} is {(turned ? "its evil twin" : "itself")}, but this PC's has no evil-twin power to follow it with (further ones are counted).");
            }

            return;
        }

        if (ai._isTransformed == turned)
        {
            return;
        }

        if (turned)
        {
            ai.TransformInElite();
        }
        else
        {
            ai.TransformBack();
            ai._isTransformed = false;
        }

        _applied++;
        string? realigned = StreamPlayer.Realign?.Invoke(forward.gameObject);
        ModLog.Info($"Cultist boss: the host's {SafeName(forward)} is {(turned ? "now its evil twin" : "itself again")}; this PC's took the same form with the game's own " +
            $"{(turned ? "TransformInElite" : "TransformBack")}{(realigned != null ? $" (replay components: {realigned})" : string.Empty)}.");
    }

    /// <summary>Every forward of both teams with an evil-twin power (its <c>CultistAI</c>'s available powers).</summary>
    private static void Find(List<(Forward Forward, TransformationCultistAI Ai)> found)
    {
        found.Clear();
        GameManager? game = GameMenuCapture.Game();
        foreach (Team? team in new[] { game?._homeTeam, game?._visitorTeam })
        {
            if (team == null)
            {
                continue;
            }

            foreach (Forward forward in GameMenuCapture.Forwards(team))
            {
                if (found.Count < PresentationKeys.MaxCultists && AiOf(forward) is TransformationCultistAI ai)
                {
                    found.Add((forward, ai));
                }
            }
        }
    }

    private static TransformationCultistAI? AiOf(Forward forward)
    {
        CultistAI? cultist = forward.GetComponent<CultistAI>();
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BossAI>? powers = cultist != null ? cultist._availableAIs : null;
        int count = cultist != null ? Math.Min(cultist._availableAIsCount, powers?.Length ?? 0) : 0;
        for (int i = 0; i < count; i++)
        {
            if (powers![i]?.TryCast<TransformationCultistAI>() is TransformationCultistAI ai)
            {
                return ai;
            }
        }

        return null;
    }

    private static string SafeName(Forward forward) => SkaterLogText.Of(forward);

    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Cultist boss: {what} failed", ex);
        }
    }
}
#endif
