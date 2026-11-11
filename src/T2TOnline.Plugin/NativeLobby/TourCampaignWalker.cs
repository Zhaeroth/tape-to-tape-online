#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppT2T.Debugging;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The game's own campaign walker (<c>T2T.Debugging.CampaignSoakWalker</c>) as the campaign check tour drives it, outside
/// the game's command-line soak runner: its three questions and two numbers set before it runs, the callbacks it reports
/// to, its reward coroutine armed on the game's persistent <c>PlayersManager</c>. Every delegate handed to the game is made
/// once (<c>DelegateSupport.ConvertDelegate</c>) and held in a static field for the life of the game: the game keeps them
/// in its statics, and a collected one would crash inside its coroutine. Each callback catches everything (nothing throws
/// into the game) and only notes what it was told; the tour reads the notes on its own frames. Developer only.
/// </summary>
/// <remarks>
/// Verified in the game's code (Nov 6 build): <c>ShouldStop</c> and <c>BeforeSelectNode</c> are asked only by
/// <c>WalkWorldMapToMatch</c>, before each node it selects; <c>GlobalMatchCount</c> only by <c>FailPhase</c>, for its line
/// (null-checked); <c>SquadSeed</c> seeds the random squad; <c>RunIndex</c> goes into the route's seed and every line.
/// </remarks>
internal static class TourCampaignWalker
{
    /// <summary>The most walker lines kept for the tour between two of its reads; more are counted.</summary>
    private const int MaxPending = 64;

    private static readonly List<string> Pending = new();

    // The managed delegates and the game's delegates made from them, made once and never let go.
    private static readonly Func<bool> AskStop = StopAsked;
    private static readonly Func<int> AskMatches = MatchesSoFar;
    private static readonly Func<Il2CppSystem.Collections.IEnumerator?> AskBeforeNode = BeforeNode;
    private static readonly Action<string> OnFailedManaged = Failed;
    private static readonly Action<string> OnLogManaged = Logged;
    private static readonly Action<string> OnRewardFailedManaged = RewardFailed;
    private static Il2CppSystem.Func<bool>? _stop;
    private static Il2CppSystem.Func<int>? _matches;
    private static Il2CppSystem.Func<Il2CppSystem.Collections.IEnumerator>? _beforeNode;
    private static Il2CppSystem.Action<string>? _onFailed;
    private static Il2CppSystem.Action<string>? _onLog;
    private static Il2CppSystem.Action<string>? _onRewardFailed;
    private static Func<bool> _stopping = () => true;

    /// <summary>The pump running a walker coroutine now: the walker's failure callback fails it.</summary>
    public static CoroutinePump? CurrentPump { get; set; }

    /// <summary>The walker's last failure (its own line), or null.</summary>
    public static string? LastFailure { get; private set; }

    /// <summary>The reward coroutine's last failure, or null.</summary>
    public static string? RewardFailure { get; private set; }

    /// <summary>Matches the tour has ended so far (the walker's <c>GlobalMatchCount</c>).</summary>
    public static int Matches { get; set; }

    /// <summary>True while the walker's rewards are armed by the tour.</summary>
    public static bool Armed { get; private set; }

    /// <summary>Walker lines dropped because the tour didn't read them in time.</summary>
    public static int Dropped { get; private set; }

    /// <summary>
    /// The walker set up for a tour: the delegates made (once), its three questions and <c>RunIndex</c> 1 and
    /// <c>SquadSeed</c> <paramref name="seed"/> set; <paramref name="stopping"/> answers its <c>ShouldStop</c>. Null when done,
    /// else why not (the tour doesn't run the walker then).
    /// </summary>
    public static string? Configure(int seed, Func<bool> stopping)
    {
        try
        {
            _stopping = stopping ?? (() => true);
            _stop ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Func<bool>>(AskStop);
            _matches ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Func<int>>(AskMatches);
            _beforeNode ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Func<Il2CppSystem.Collections.IEnumerator>>(AskBeforeNode);
            _onFailed ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(OnFailedManaged);
            _onLog ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(OnLogManaged);
            _onRewardFailed ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(OnRewardFailedManaged);
            if (_stop == null || _matches == null || _beforeNode == null || _onFailed == null || _onLog == null || _onRewardFailed == null)
            {
                return "a delegate for the walker couldn't be made";
            }

            // All three questions before the first call: the walker asks them as it walks.
            CampaignSoakWalker.ShouldStop = _stop;
            CampaignSoakWalker.GlobalMatchCount = _matches;
            CampaignSoakWalker.BeforeSelectNode = _beforeNode;
            CampaignSoakWalker.RunIndex = 1;
            CampaignSoakWalker.SquadSeed = seed;
            LastFailure = null;
            RewardFailure = null;
            Matches = 0;
            lock (Pending)
            {
                Pending.Clear();
                Dropped = 0;
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"the walker couldn't be set up ({ModLog.SafeErrorText(ex)})";
        }
    }

    /// <summary>The failure callback for a walker coroutine (made by <see cref="Configure"/>).</summary>
    public static Il2CppSystem.Action<string> OnFailed => _onFailed ?? throw new InvalidOperationException("the walker isn't set up");

    /// <summary>The line callback for a walker coroutine (made by <see cref="Configure"/>).</summary>
    public static Il2CppSystem.Action<string> OnLog => _onLog ?? throw new InvalidOperationException("the walker isn't set up");

    /// <summary>
    /// The walker's rewards armed on <paramref name="host"/>: the game's reward events start its reward coroutine there. Null
    /// when armed, else why not. Its callbacks are the tour's.
    /// </summary>
    public static string? Arm(MonoBehaviour host)
    {
        try
        {
            if (_onRewardFailed == null || _onLog == null)
            {
                return "the walker isn't set up";
            }

            CampaignSoakWalker.ArmRewards(host, _onRewardFailed, _onLog);
            Armed = true;
            return null;
        }
        catch (Exception ex)
        {
            return $"ArmRewards failed ({ModLog.SafeErrorText(ex)})";
        }
    }

    /// <summary>The walker's rewards disarmed (its events unregistered, its queue emptied). Safe at any time; never throws.</summary>
    public static void Disarm()
    {
        if (!Armed)
        {
            return;
        }

        Armed = false;
        try
        {
            CampaignSoakWalker.DisarmRewards();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Lobby check: campaign: the walker's rewards couldn't be disarmed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>True while the armed reward coroutine is driving a reward (the walker's own flag); false when unread.</summary>
    public static bool RewardInFlight()
    {
        try
        {
            return Armed && CampaignSoakWalker.s_rewardInFlight;
        }
        catch (Exception)
        {
            return false; // read for a fallback's timing only
        }
    }

    /// <summary>The node type the walker selected last ("" when none or unread).</summary>
    public static string LastNodeType()
    {
        try
        {
            return CampaignSoakWalker.LastNodeType ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The walker's lines since the last call, in order.</summary>
    public static List<string> TakeLines()
    {
        lock (Pending)
        {
            var lines = new List<string>(Pending);
            Pending.Clear();
            return lines;
        }
    }

    // ---- What the game calls ----

    private static bool StopAsked()
    {
        try
        {
            return _stopping();
        }
        catch (Exception)
        {
            return true; // in doubt the walk stops
        }
    }

    private static int MatchesSoFar() => Matches;

    // One frame before each node (null: the walker goes on at once).
    private static Il2CppSystem.Collections.IEnumerator? BeforeNode() => null;

    private static void Failed(string reason)
    {
        try
        {
            string line = SafeText.Clean(reason, 300);
            LastFailure = line;
            Note(line);
            CurrentPump?.Fail(line);
        }
        catch (Exception)
        {
            // Nothing throws into the game's coroutine.
        }
    }

    private static void RewardFailed(string reason)
    {
        try
        {
            string line = SafeText.Clean(reason, 300);
            RewardFailure = line;
            Note(line);
        }
        catch (Exception)
        {
            // Nothing throws into the game's coroutine.
        }
    }

    private static void Logged(string line)
    {
        try
        {
            Note(SafeText.Clean(line, 300));
        }
        catch (Exception)
        {
            // Nothing throws into the game's coroutine.
        }
    }

    private static void Note(string line)
    {
        lock (Pending)
        {
            if (Pending.Count >= MaxPending)
            {
                Dropped++;
                return;
            }

            Pending.Add(line);
        }
    }
}
#endif
