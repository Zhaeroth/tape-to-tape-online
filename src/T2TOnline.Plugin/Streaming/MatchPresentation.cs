#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Match;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// A friend's own match presentations in step with the host's: the match intro
/// (<see cref="MatchIntro"/>), the end-of-match win banner (here), the golfer outro (<see cref="GolferOutroFollow"/>) and
/// the golfer boss's forms (<see cref="GolferTiers"/>), the host's random talent picks (<see cref="MatchPicks"/>) and the cup
/// cultists' evil twins (<see cref="CultistForms"/>). Each friend's game runs the game's own code for them; the host's
/// HUD values (<see cref="PresentationKeys"/>) say when.
/// <list type="bullet">
/// <item>Host: <see cref="ReadHost"/> adds the values to each regular HUD sample (the HUD recorder).</item>
/// <item>Friend: the HUD channel hands every presentation value to <see cref="Take"/> and calls <see cref="AfterMessage"/>
/// after each message (at playout time, in step with the snapshots); <see cref="Tick"/> runs every frame.</item>
/// </list>
/// Nothing here throws into the game: each part's failures are logged (the first few) and the part goes on.
/// </summary>
internal static class MatchPresentation
{
    private static readonly PresentationValues Values = new();
    private static readonly ErrorLogLimit Errors = new(3);
    private static Func<NetValue, Type, object?>? _resolve;
    private static bool _playing;

    // Host: the win banner, looked up twice a second while there's none.
    private static EndGameAnimatedScoreUI? _hostBanner;
    private static double _lastBannerSearch = double.MinValue;
    private static bool _hostBannerLogged;

    // Host: the reward fade said once a stream.
    private static bool _hostRewardFadeLogged;

    // Friend: the host's banner as last heard, and whether this PC's played this match; the host's reward fade as last heard.
    private static bool _bannerHeard;
    private static bool _bannerPlayed;
    private static bool? _rewardFadeHeard;

    /// <summary>Host: a new stream starts (the HUD recorder's start).</summary>
    public static void HostStart()
    {
        MatchIntro.HostStart();
        Campaign.Menus.GameMenuCapture.RememberForwards();
        MatchPicks.HostStart();
        CultistForms.HostStart();
        ZapForms.HostStart();
        FrogStatuses.HostStart();
        GolferOutroFollow.HostStart();
        BannerBackdrop.HostStart();
        _hostRewardFadeLogged = false;
        _hostBanner = null;
        _lastBannerSearch = double.MinValue;
        _hostBannerLogged = false;
    }

    /// <summary>Host, at each regular HUD sample: every presentation value. Never throws.</summary>
    public static void ReadHost(Dictionary<string, NetValue> values, Func<UnityEngine.Object, NetValue> refOf)
    {
        MatchIntro.ReadHost(values);
        GolferOutroFollow.ReadHost(values);
        GolferTiers.ReadHost(values, refOf);
        MatchPicks.ReadHost(values, refOf);
        CultistForms.ReadHost(values, refOf);
        ZapForms.ReadHost(values, refOf);
        FrogStatuses.ReadHost(values, refOf);
        BannerBackdrop.ReadHost(values);
        ReadRewardFade(values);
        try
        {
            EndGameAnimatedScoreUI? banner = HostBanner();
            bool up = banner != null && banner._canvasGroup is UnityEngine.CanvasGroup group && group != null && group.alpha > 0.01f;
            values[PresentationKeys.Banner] = NetValue.FromBool(up);
            if (up && !_hostBannerLogged)
            {
                _hostBannerLogged = true;
                ModLog.Info("End of match: the win banner plays here; friends' games play theirs with it (HUD endgame.banner).");
            }
        }
        catch (Exception ex)
        {
            Failed("reading the host's win banner", ex);
        }
    }

    /// <summary>Friend: playback of a host's match starts (the HUD channel's start).</summary>
    public static void Begin(Func<NetValue, Type, object?> resolve)
    {
        _resolve = resolve;
        _playing = true;
        Values.Reset();
        _bannerHeard = _bannerPlayed = false;
        _rewardFadeHeard = null;

        // Before the golfer boss's forms follow the host's (GolferTiers): the in-match menu names forwards as they began.
        Campaign.Menus.GameMenuCapture.RememberForwards();
        GolferOutroFollow.Begin();
        GolferTiers.Begin();
        MatchPicks.Begin();
        CultistForms.Begin();
        ZapForms.Begin();
        FrogStatuses.Begin();
        BannerBackdrop.Begin();
        MatchIntro.Showing();
    }

    /// <summary>Friend: one HUD value. True when it's a presentation value (taken here; the HUD channel writes it nowhere else).</summary>
    public static bool Take(string key, NetValue value) => Values.Take(key, value);

    /// <summary>Friend: a HUD message has been read: each part follows the host's values. Never throws.</summary>
    public static void AfterMessage()
    {
        try
        {
            MatchIntro.HostValues(Values);
            GolferOutroFollow.HostValues(Values);
            if (Values.GolfersChanged && _resolve != null)
            {
                GolferTiers.Follow(Values, _resolve);
            }

            if (_resolve != null)
            {
                // The host's random talent picks and the cultists' forms: each acts only on a change (or a retry).
                MatchPicks.Follow(Values, _resolve);
                CultistForms.Follow(Values, _resolve);
                ZapForms.Follow(Values, _resolve);
                FrogStatuses.Follow(Values, _resolve);
            }

            BannerBackdrop.Message(Values.BannerDim);
            FollowBanner();
            FollowRewardFade();
        }
        catch (Exception ex)
        {
            Failed("following the host's presentations", ex);
        }
        finally
        {
            Values.EndMessage();
        }
    }

    /// <summary>Friend: playback ended (the match is still loaded, or gone). Never throws.</summary>
    public static void End(bool matchLoaded)
    {
        if (!_playing)
        {
            return;
        }

        _playing = false;
        GolferOutroFollow.End(matchLoaded);
        GolferTiers.End(matchLoaded);
        MatchPicks.End();
        CultistForms.End();
        ZapForms.End();
        FrogStatuses.End();
        BannerBackdrop.End(matchLoaded);
        if (Values.Refused > 0)
        {
            ModLog.Warn($"Match presentations: {Values.Refused} presentation value(s) from the host were refused (wrong kind or out of range).");
        }

        _resolve = null;
    }

    /// <summary>Every frame (both sides; each part does nothing when it has nothing to do). Never throws.</summary>
    public static void Tick()
    {
        MatchIntro.Tick();
        BannerBackdrop.Tick();
        if (_playing)
        {
            GolferOutroFollow.Tick();
        }

        try
        {
            // The act intro on the world map (not a match presentation, but the same kind of wait, and this tick always runs).
            Campaign.Scenes.ActIntro.Tick();
            Campaign.Scenes.MapReveal.Tick();
        }
        catch (Exception ex)
        {
            Failed("the act intro's tick", ex);
        }
    }

    /// <summary>
    /// Friend: the host's win banner went up: this PC's own plays (the game's <c>EndGameAnimatedScoreUI.StartAnimation</c>,
    /// with this PC's own teams, whose score the stream keeps the host's), once a match. The end-of-match pipeline that
    /// starts it is the host's (a friend's is held or detached), so it never played here before.
    /// </summary>
    private static void FollowBanner()
    {
        if (Values.Banner is not bool up || _bannerPlayed)
        {
            return;
        }

        if (!up)
        {
            _bannerHeard = true; // the host's first values say it's down: a later "up" is the banner starting
            return;
        }

        _bannerPlayed = true;
        EndGameAnimatedScoreUI? banner = UnityEngine.Object.FindObjectOfType<EndGameAnimatedScoreUI>();
        if (banner == null)
        {
            ModLog.Warn("End of match: the host's win banner plays, but this PC's match has no active win banner to play.");
            return;
        }

        if (banner._canvasGroup is UnityEngine.CanvasGroup group && group != null && group.alpha > 0.01f)
        {
            ModLog.Info("End of match: this PC's win banner is already up; not started again.");
            return;
        }

        try
        {
            banner.StartAnimation();
            ModLog.Info($"End of match: the host's win banner plays here too (the game's own, with this PC's teams{(_bannerHeard ? string.Empty : "; it was already up on the host when this PC's stream started")}).");
        }
        catch (Exception ex)
        {
            Failed("playing the win banner", ex);
        }
    }

    /// <summary>
    /// Host, at each regular HUD sample: whether its cover is going black or black before a post-game reward menu (the game's
    /// <c>m_FadeInPending</c>: <c>FadeToBlackBeforeRewardMenu</c> sets it, the menu's show clears it).
    /// </summary>
    private static void ReadRewardFade(Dictionary<string, NetValue> values)
    {
        try
        {
            RogueRewardsHandler? handler = RogueRewardsHandler.s_Instance;
            bool fading = handler != null && handler.m_FadeInPending;
            values[PresentationKeys.RewardFade] = NetValue.FromBool(fading);
            if (fading && !_hostRewardFadeLogged)
            {
                _hostRewardFadeLogged = true;
                ModLog.Info("Menus: the host's game fades to black before its reward menu; friends' games fade with it (HUD endgame.fade).");
            }
        }
        catch (Exception ex)
        {
            Failed("reading the host's reward fade", ex);
        }
    }

    /// <summary>
    /// Friend: the host's cover went up or came off (<see cref="PresentationKeys.RewardFade"/>). Up: this PC's game fades too
    /// unless a joiner heard it up from its first value (the host's menu then isn't long off). Off: the host's menu is up.
    /// </summary>
    private static void FollowRewardFade()
    {
        if (Values.RewardFade is not bool up || up == _rewardFadeHeard)
        {
            return;
        }

        bool? was = _rewardFadeHeard;
        _rewardFadeHeard = up;
        if (Campaign.Menus.MenuViews.Instance is not { Off: false, Following: true } views)
        {
            return;
        }

        if (!up)
        {
            Campaign.Menus.RewardFade.HostMenuUp();
        }
        else if (was == false)
        {
            views.Friend.HostRewardFade();
        }
    }

    /// <summary>The host's win banner: looked up twice a second while there's none, kept while it lives.</summary>
    private static EndGameAnimatedScoreUI? HostBanner()
    {
        if (_hostBanner != null)
        {
            return _hostBanner;
        }

        double now = FrameClock.Seconds;
        if (now - _lastBannerSearch < 0.5)
        {
            return null;
        }

        _lastBannerSearch = now;
        EndGameAnimatedScoreUI? found = UnityEngine.Object.FindObjectOfType<EndGameAnimatedScoreUI>();
        _hostBanner = found != null ? found : null;
        return _hostBanner;
    }

    /// <summary>Logs the first few failures of each kind.</summary>
    private static void Failed(string what, Exception ex)
    {
        if (Errors.Allow(what))
        {
            ModLog.Error($"Match presentations: {what} failed", ex);
        }
    }
}

/// <summary>Installs the presentation patches (the anthem's start; nothing else is patched: the rest is read or called).</summary>
internal static class MatchPresentationPatches
{
    /// <summary>Adds a line to <paramref name="failed"/> for each patch that couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed) => MatchIntro.Install(harmony, failed);
}
#endif
