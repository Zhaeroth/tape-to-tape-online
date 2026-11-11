#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// The arena's 20% dim behind the host's end-of-match win banner, on a friend's PC. The host's
/// <c>EndGameAnimationStep</c> (<c>d__3</c>) dims with <c>SceneBackdrop.DimInternal(SceneOwner, 0.5, -20)</c> half a
/// second before its banner, and turns that dim into a blurred still when the banner is over.
/// <list type="bullet">
/// <item>Host: the HUD value <see cref="PresentationKeys.BannerDim"/> is the backdrop's <c>m_DimOnly</c>, which nothing else
/// sets (verified in the game's code: <c>Dim</c> has no other caller).</item>
/// <item>Friend: the game's own <c>SceneBackdrop.Dim</c> with the same fade and order, under an owner of the mod's, so no other
/// code of the game can take it down or keep it up. It's taken down here every frame it's due (<see cref="BannerDimFollow"/>):
/// anything else holds the backdrop (a friend's real in-match menu owns its blurred still, <c>GameMenu.Show</c>: the
/// dim turns into that still with the game's own <c>SetDimToStill</c>), a moment after the host's dim ended, no HUD values for
/// 6 s (a drop), 20 s in any case, or playback's end. A dim never touches the camera (verified in the game's code:
/// <c>DimInternal</c>: only the still does, and the still here is the menu's, under the menu's own take-down and
/// watchdog, <see cref="Campaign.Menus.GameMenuViews"/>). A scene's unload takes the backdrop with it (<c>OnDestroy</c>).</item>
/// </list>
/// Nothing here throws into the game: a failure is logged and the dim's left out or taken down.
/// </summary>
internal static class BannerBackdrop
{
    // EndGameAnimationStep's own (0.5 s, sorting order -20).
    private const float FadeIn = 0.5f;
    private const int SortingOrder = -20;
    private const float FadeOut = 0.2f;

    private static readonly BannerDimFollow Follow = new();
    private static readonly ErrorLogLimit Errors = new(3);
    private static Il2CppSystem.Object? _owner;
    private static bool _hostLogged;

    /// <summary>Host: a new stream starts.</summary>
    public static void HostStart() => _hostLogged = false;

    /// <summary>Host, at each regular HUD sample: the arena's dim. Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values)
    {
        try
        {
            SceneBackdrop? backdrop = SceneBackdrop.s_Instance;
            bool dim = backdrop != null && backdrop.m_DimOnly;
            values[PresentationKeys.BannerDim] = NetValue.FromBool(dim);
            if (dim && !_hostLogged)
            {
                _hostLogged = true;
                ModLog.Info("End of match: the arena dims here for the win banner; friends' games dim with it (HUD endgame.dim).");
            }
        }
        catch (Exception ex)
        {
            Failed("reading the host's dim", ex);
        }
    }

    /// <summary>Friend: playback of a host's match starts.</summary>
    public static void Begin() => Follow.Reset();

    /// <summary>Friend: a HUD message came, with the host's dim as last heard. Never throws.</summary>
    public static void Message(bool? hostDim)
    {
        if (!Follow.Message(hostDim, FrameClock.Seconds))
        {
            return;
        }

        try
        {
            if (SceneBackdrop.s_Instance == null)
            {
                Follow.TakenDown();
                ModLog.Info("End of match: the host's arena dims for its win banner; this PC's match has no backdrop to dim.");
                return;
            }

            _owner ??= new Il2CppSystem.Object();
            SceneBackdrop.Dim(_owner, FadeIn, SortingOrder);
            ModLog.Info("End of match: the host's arena dims for its win banner; this PC's dims with it (the game's own, under an owner of the mod's).");
        }
        catch (Exception ex)
        {
            Follow.TakenDown();
            Failed("dimming the arena", ex);
        }
    }

    /// <summary>Friend, every frame while its dim is up: taken down when it's due. Never throws.</summary>
    public static void Tick()
    {
        if (!Follow.Up)
        {
            return;
        }

        try
        {
            SceneBackdrop? backdrop = SceneBackdrop.s_Instance;
            if (backdrop == null)
            {
                Follow.TakenDown(); // gone with its scene, the dim with it
                return;
            }

            BannerDimEnd end = Follow.Watch((backdrop.m_Owners?.Count ?? 0) > 1, FrameClock.Seconds);
            if (end != BannerDimEnd.None)
            {
                TakeDown(BannerDimFollow.Words(end));
            }
        }
        catch (Exception ex)
        {
            Follow.TakenDown();
            Failed("watching the dim", ex);
            TakeDown("watching it failed");
        }
    }

    /// <summary>Friend: playback ended (<paramref name="matchLoaded"/>: its scene is still here). Never throws.</summary>
    public static void End(bool matchLoaded)
    {
        if (!Follow.Up)
        {
            return;
        }

        Follow.TakenDown();
        if (matchLoaded)
        {
            TakeDown("playback ended");
        }
    }

    private static void TakeDown(string why)
    {
        try
        {
            if (_owner != null)
            {
                _ = SceneBackdrop.Hide(_owner, FadeOut);
            }

            ModLog.Info($"End of match: this PC's dim comes off ({why}).");
        }
        catch (Exception ex)
        {
            Failed("taking the dim down", ex);
        }
    }

    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"End of match: {what} failed", ex);
        }
    }
}
#endif
