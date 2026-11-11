#if HAS_INTEROP
using System;
using Il2Cpp;
using Il2CppEvents;
using Il2CppInterop.Runtime;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// SCORING_SQUAD (every skater of the human team scored in one match) for a host who owns it (D34). The game's check,
/// <c>AchievementManager.OnGoalScored</c>, is subscribed to <c>GameplayEvents.GoalScored</c> once, in
/// <c>AchievementManager.Awake</c> at start-up, and only if the player doesn't own the achievement; it's an ordinary
/// listener (wiped at a match's teardown unless the event is kept, and removed by the check itself once it unlocks). So a
/// host who owns it never runs the check, and friends who earned it alongside never get it. While hosting with sharing on,
/// this listens to the goal event itself (a persistent listener, checked once a second) and, for each goal the game's own
/// check isn't listening to, runs that check: the game's own conditions, its own <c>PlatformAchievements.Unlock</c> call
/// (nothing new on Steam for an owner), which the achievement mirror passes on to the friends who shared the match.
/// Nothing here throws into the game.
/// </summary>
internal static class ScoringSquad
{
    private const int MaxLoggedFailures = 3;

    // Made once and kept for the game's life, as MatchSetup's listener is: its address is in EventChannel.ModListeners, and
    // a delegate let go could have its address taken by a game listener, which a friend's event channel would then never
    // detach or deliver to.
    private static Il2CppSystem.Action<GoalScoredArg>? _listener;
    private static bool _listening;
    private static AchievementManager? _manager;
    private static double _nextCheck;
    private static int _failures;
    private static bool _loggedStand;
    private static int _runs;

    /// <summary>
    /// Call once per frame while hosting: listens to goals while <paramref name="sharing"/> (and stops when not). Checked
    /// once a second; never throws.
    /// </summary>
    public static void Update(bool sharing)
    {
        double now = FrameClock.Seconds;
        if (now < _nextCheck)
        {
            return;
        }

        _nextCheck = now + 1;
        try
        {
            if (!sharing)
            {
                Stop();
                return;
            }

            Listen();
        }
        catch (Exception ex)
        {
            _nextCheck = now + 30;
            Warn("couldn't listen for goals", ex);
        }
    }

    /// <summary>Hosting ended (or sharing went off): no more listening.</summary>
    public static void Stop()
    {
        if (!_listening || _listener == null)
        {
            return;
        }

        _listening = false;
        try
        {
            GameplayEvents.GoalScored?.UnregisterDelegate(_listener);
        }
        catch (Exception ex)
        {
            Warn("couldn't stop listening for goals", ex);
        }

        if (_runs > 0)
        {
            ModLog.Info($"Achievements: the game's SCORING_SQUAD check ran {_runs} time(s) for goals it wasn't listening to this session.");
        }

        _runs = 0;
        _loggedStand = false;
    }

    private static void Listen()
    {
        var goalScored = GameplayEvents.GoalScored;
        if (goalScored == null)
        {
            return;
        }

        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? listeners = goalScored.m_Listeners;
        if (_listening && _listener != null && listeners != null)
        {
            for (int i = 0; i < listeners.Count; i++)
            {
                if (listeners[i]?.Pointer == _listener.Pointer)
                {
                    return;
                }
            }
        }

        if (_listener == null)
        {
            _listener = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GoalScoredArg>>(new Action<GoalScoredArg>(OnGoalScored))
                ?? throw new InvalidOperationException("the listener couldn't be made");
            EventChannel.ModListeners.Add(_listener.Pointer);
        }

        goalScored.RegisterPersistentDelegate(_listener, "T2TOnline");
        _listening = true;
    }

    private static void OnGoalScored(GoalScoredArg arg)
    {
        try
        {
            if (arg == null || !_listening || AchievementMirror.Sharing?.Invoke() != true)
            {
                return;
            }

            if (_manager == null)
            {
                // Unity's null: also found again if the one held was destroyed.
                _manager = UnityEngine.Object.FindObjectOfType<AchievementManager>();
            }

            if (_manager == null || GameListens(_manager))
            {
                return;
            }

            if (!_loggedStand)
            {
                _loggedStand = true;
                bool owned = PlatformAchievements.IsUnlocked(AchievementManager.Achievements.SCORING_SQUAD);
                ModLog.Info($"Achievements: the game's SCORING_SQUAD check isn't listening here ({(owned ? "this PC owns it, so the game never subscribed it" : "the game's listener is gone")}); " +
                    "the mod runs the game's own check at each goal while hosting, so friends who share the match get it.");
            }

            _runs++;
            _manager.OnGoalScored(arg);
        }
        catch (Exception ex)
        {
            Warn("running the game's SCORING_SQUAD check failed", ex);
        }
    }

    /// <summary>True when the game's own check is on the goal event (its target is the achievement manager): it runs itself.</summary>
    private static bool GameListens(AchievementManager manager)
    {
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Delegate>? listeners = GameplayEvents.GoalScored?.m_Listeners;
        for (int i = 0; listeners != null && i < listeners.Count; i++)
        {
            Il2CppSystem.Object? target = listeners[i]?.m_target;
            if (target != null && target.Pointer == manager.Pointer)
            {
                return true;
            }
        }

        return false;
    }

    private static void Warn(string what, Exception ex)
    {
        if (_failures++ < MaxLoggedFailures)
        {
            ModLog.Warn($"Achievements: {what} ({ModLog.SafeErrorText(ex)}).");
        }
    }
}
#endif
