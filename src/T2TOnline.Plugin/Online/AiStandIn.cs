#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppSkaters;
using T2TOnline.Core.Hosting;
using T2TOnline.Plugin.Diagnostics;
using Player = Il2CppTape2Tape.Player;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// The AI plays a dropped friend's skater. After <see cref="StandInTimer.AwayMicros"/> without the friend (silent, dropped, or
/// their pad unplugged) their skater gets the game's own <c>Skater.forceAIControlled</c> (the AI then drives it:
/// <c>isControlled</c> turns false) and <c>canBeSelected</c> off (so nobody else switches onto it), as the game's own no-goal
/// talent does. The friend's game player stays joined and their virtual pad neutral, so the game never sees a disconnect (which
/// would pause the match for the controller screen).
/// <para>
/// The game keeps moving a player between skaters (passes, pickups, line changes, faceoffs, brawls), so the flag follows
/// whatever skater the player holds, checked every frame; the skater left behind gets its flags back. When the friend
/// is back and steady for <see cref="StandInTimer.BackMicros"/>, their skater is theirs again (the timing is
/// <see cref="StandInTimer"/>, Core, tested). A skater whose flags the game itself owns at that moment (the anthem
/// skate-in, a talent's or a boss's sequence: <c>Forward.overrideAI</c>) is left to the game, and the hand-back waits a
/// frame. A skater left behind while the game owns it (the friend gone for good, hosting stopped, the player moved on) gets
/// its flags back once the game's sequence is over (<see cref="RestorePending"/>). A sequence that makes the skater
/// selectable again while the AI plays it (the no-goal talent's end) is remembered, so the hand-back makes it selectable.
/// </para>
/// Field writes only, no patches. Nothing here throws into the game.
/// </summary>
internal sealed class AiStandIn
{
    /// <summary>The anthem skate-in's component type, looked up once (asked every frame for a skater waiting to be put back).</summary>
    private static readonly Lazy<Il2CppSystem.Type> AnthemType = new(Il2CppType.Of<AnthemMovementSkaters>);

    private readonly Dictionary<int, Standing> _standings = new();
    private readonly List<int> _gone = new();
    private readonly List<PendingRestore> _pending = new();
    private int _pendingFailures;

    /// <summary>
    /// Call every frame for each friend's pad: <paramref name="away"/> while they're silent, dropped or unplugged.
    /// <paramref name="player"/> is their game player (null outside a match or before they joined). <paramref name="blind"/>:
    /// their game said it can't play this match (its load failed), so the AI takes their skater at once (their pad would drive
    /// it unseen), until their game plays it or they leave. <paramref name="tag"/> names them in the log (their tag).
    /// </summary>
    public void Track(int rewiredId, string tag, bool away, Player? player, long nowMicros, bool blind = false)
    {
        if (!_standings.TryGetValue(rewiredId, out Standing? standing))
        {
            standing = new Standing(tag);
            _standings[rewiredId] = standing;
        }

        standing.Seen = true;
        try
        {
            StandInTimer timer = standing.Timer;
            bool hasSkater = away && !timer.Active && player?.controlledSkater is { } skater && skater != null;
            switch (timer.Step(away, hasSkater, nowMicros, atOnce: away && blind))
            {
                case StandInStep.Start when away && blind:
                    ModLog.Info($"AI stand-in: {tag}'s game couldn't load this match; the AI plays their skater at once, until their game plays the match or they leave.");
                    break;
                case StandInStep.Start:
                    ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"AI stand-in: {tag} has been gone {(nowMicros - timer.AwaySince) / 1_000_000.0:0.0} s; the AI plays their skater until they're back."));
                    break;
                case StandInStep.HandBack when TryHandBack(standing):
                    timer.HandedBack();
                    ModLog.Info($"AI stand-in: {tag} is back; their skater is theirs again.");
                    break;
            }

            if (timer.Active)
            {
                Follow(standing, player);
            }
        }
        catch (Exception ex)
        {
            if (standing.Failures++ < 3)
            {
                ModLog.Warn($"AI stand-in: {tag}: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <summary>How many friends' skaters the AI plays now (read by the check tour; no interop).</summary>
    public int ActiveCount
    {
        get
        {
            int active = 0;
            foreach (Standing standing in _standings.Values)
            {
                active += standing.Timer.Active ? 1 : 0;
            }

            return active;
        }
    }

    /// <summary>Call every frame after <see cref="Track"/>: a pad no longer tracked (removed) gets its skater back.</summary>
    public void Sweep()
    {
        _gone.Clear();
        foreach ((int id, Standing standing) in _standings)
        {
            if (!standing.Seen)
            {
                _gone.Add(id);
            }

            standing.Seen = false;
        }

        foreach (int id in _gone)
        {
            Forget(id);
        }
    }

    /// <summary>The friend on this Rewired player is gone for good (or hosting stopped): their skater's flags go back.</summary>
    public void Forget(int rewiredId)
    {
        if (_standings.Remove(rewiredId, out Standing? standing) && standing.Timer.Active)
        {
            try
            {
                Restore(standing);
            }
            catch (Exception)
            {
                // Gone with its match.
            }
        }
    }

    /// <summary>Every friend's skater back (hosting stopped).</summary>
    public void ForgetAll()
    {
        foreach (int id in new List<int>(_standings.Keys))
        {
            Forget(id);
        }
    }

    /// <summary>True while the AI plays this friend's skater.</summary>
    public bool IsStandingIn(int rewiredId) => _standings.TryGetValue(rewiredId, out Standing? standing) && standing.Timer.Active;

    /// <summary>
    /// Call every frame, hosting or not: a skater left while one of the game's own sequences held it gets its flags
    /// back once that's over (the anthem skate-in clears <c>forceAIControlled</c> but never touches <c>canBeSelected</c>),
    /// or is forgotten once it's gone.
    /// </summary>
    public void RestorePending()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            PendingRestore pending = _pending[i];
            try
            {
                Skater skater = pending.Skater;
                if (skater.WasCollected || skater == null || IsFlagged(skater))
                {
                    _pending.RemoveAt(i); // gone with its match, or the AI plays it again (that stand-in puts it back)
                    continue;
                }

                if (OwnedByGame(skater))
                {
                    continue;
                }

                _pending.RemoveAt(i);
                Unflag(skater, pending.ChangedSelectable);
                ModLog.Info($"AI stand-in: the game's own sequence let go of {pending.Tag}'s last skater; its flags are back.");
            }
            catch (Exception ex)
            {
                _pending.RemoveAt(i);
                if (_pendingFailures++ < 3)
                {
                    ModLog.Warn($"AI stand-in: couldn't put {pending.Tag}'s last skater back: {ModLog.SafeErrorText(ex)}");
                }
            }
        }
    }

    /// <summary>The flag follows the player: on the skater they hold now, off the one they left.</summary>
    private void Follow(Standing standing, Player? player)
    {
        Skater? current = player?.controlledSkater;
        if (current == null)
        {
            return; // between matches, or between skaters
        }

        if (standing.Flagged != null && standing.Flagged.Pointer != current.Pointer)
        {
            Restore(standing);
        }

        if (standing.Flagged == null)
        {
            // A skater still waiting to be put back from an earlier stand-in was selectable before that one.
            standing.Flagged = current;
            standing.ChangedSelectable = TakePending(current);
        }

        // Every frame: the anthem skate-in or a talent may have cleared it meanwhile. Selectable now means it was before
        // the stand-in, or a game sequence made it so since (the no-goal talent's end): either way it is again at the
        // hand-back.
        standing.ChangedSelectable |= current.canBeSelected;
        current.forceAIControlled = true;
        current.canBeSelected = false;
    }

    /// <summary>The friend is back: their skater's flags go back unless the game owns them right now (then next frame).</summary>
    private bool TryHandBack(Standing standing)
    {
        if (standing.Flagged is { } skater && skater != null && OwnedByGame(skater))
        {
            return false;
        }

        Restore(standing);
        return true;
    }

    /// <summary>The standing's skater gets its flags back, now or (while the game's own sequence holds it) once that's over.</summary>
    private void Restore(Standing standing)
    {
        Skater? skater = standing.Flagged;
        bool changedSelectable = standing.ChangedSelectable;
        standing.Flagged = null;
        standing.ChangedSelectable = false;
        if (skater is null || skater.WasCollected || skater == null)
        {
            return; // gone with its match
        }

        if (OwnedByGame(skater))
        {
            _pending.Add(new PendingRestore(skater, changedSelectable | TakePending(skater), standing.Tag));
            ModLog.Info($"AI stand-in: {standing.Tag}'s skater is in one of the game's own sequences; its flags go back once that's over.");
            return;
        }

        Unflag(skater, changedSelectable);
    }

    private static void Unflag(Skater skater, bool changedSelectable)
    {
        skater.forceAIControlled = false;
        if (changedSelectable)
        {
            skater.canBeSelected = true;
        }
    }

    /// <summary>Takes <paramref name="skater"/> off the pending list; true if it was selectable before we flagged it.</summary>
    private bool TakePending(Skater skater)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            if (Same(_pending[i].Skater, skater))
            {
                bool changedSelectable = _pending[i].ChangedSelectable;
                _pending.RemoveAt(i);
                return changedSelectable;
            }
        }

        return false;
    }

    private bool IsFlagged(Skater skater)
    {
        foreach (Standing standing in _standings.Values)
        {
            if (Same(standing.Flagged, skater))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The same native skater (a collected wrapper has no pointer to compare).</summary>
    private static bool Same(Skater? a, Skater b) => a is { WasCollected: false } && !b.WasCollected && a.Pointer == b.Pointer;

    /// <summary>The game's own sequences set the same flags: a talent's or a boss's (<c>overrideAI</c>), the anthem skate-in.</summary>
    private static bool OwnedByGame(Skater skater) =>
        skater.TryCast<Forward>() is { overrideAI: true } ||
        skater.gameObject.GetComponent(AnthemType.Value) != null;

    private sealed class Standing
    {
        public Standing(string tag) => Tag = tag;

        /// <summary>How the log names the friend: their tag.</summary>
        public string Tag { get; }

        public bool Seen { get; set; }

        /// <summary>Away and back, and whether the AI plays for them now.</summary>
        public StandInTimer Timer { get; } = new();

        public Skater? Flagged { get; set; }

        public bool ChangedSelectable { get; set; }

        public int Failures { get; set; }
    }

    /// <summary>A skater whose flags go back once the game's own sequence lets go of it.</summary>
    private sealed record PendingRestore(Skater Skater, bool ChangedSelectable, string Tag);
}
#endif
