#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of the host's menu sounds and small looks, each through the game's
/// own code (<see cref="MenuAudio"/>): a click's sound with the host's result and effect, a shortcut's press, the back sound
/// (a close the host's back input made, or one inside a menu), the host's navigation mode (select sounds and highlight),
/// and the reward menus' fade to black at their close. Sounds play only for the host's live events on a real view, never
/// again when the view applies the host's values anew. Each kind is logged the first time; a failure is logged once and
/// never stops the view.
/// </summary>
internal sealed partial class FriendMenus
{
    // The relic reward menu's data value: a match's forced pick, whose close doesn't fade (MenuPayloads).
    private const string ForcedMatchPickKey = "forcedMatchPick";

    // After a join's sync the host's open menus and their values are replayed: no sound plays from them for this long.
    private const double QuietAfterSyncSeconds = 1;

    // How often the fade's cover is looked at (it finds the loading screen: not every frame).
    private const double FadeLookSeconds = 0.25;

    private static readonly MenuTarget NavModeTarget = MenuTarget.AtItem(MenuSounds.NavModeGroup, 0);

    private readonly HashSet<string> _sounds = new(StringComparer.Ordinal);
    private double _syncedAt = double.NegativeInfinity;
    private double _fadeLookAt;

    /// <summary>
    /// True for a reward menu whose own close fades to black first on the host: a post-game reward (its data says so), in
    /// <c>RelicRewardMenu.&lt;Hide&gt;d__61</c> unless its <c>m_ForcedMatchPick</c> (its data carries it), in
    /// <c>SkaterPowerupRewardMenu.&lt;Hide&gt;d__92</c> always.
    /// </summary>
    private static bool FadesOnClose(string type, MenuData data) =>
        RewardFadeRules.FadesOnClose(type == MenuPayloads.RelicReward, type == MenuPayloads.PowerUpReward, data.Flag(ForcedMatchPickKey), data.Flag(RewardFadeRules.PostGameKey));

    /// <summary>True for a value that is one of the host's sounds (a shortcut's press, a back sound): never applied as a state.</summary>
    private static bool IsSound(MenuTarget target, int index) =>
        (target.Kind == MenuTargetKind.Path && MenuPress.IsPress(index)) || MenuSounds.IsBack(target, index);

    /// <summary>
    /// A live value of the host's that is a sound: played here (the back sound, whatever the view shows; a shortcut's own
    /// trigger or failure sound, on a real view). True when it was one (whether it played or not). Nothing plays in the
    /// moment after a join's sync (<see cref="QuietAfterSyncSeconds"/>): those values are the host's menus replayed.
    /// </summary>
    private bool Sounded(View view, MenuTarget element, int index)
    {
        bool live = FrameClock.Seconds - _syncedAt > QuietAfterSyncSeconds;
        if (MenuSounds.IsBack(element, index))
        {
            if (live)
            {
                PlaySound("back", $"Menus: {LogNames.HostTag()}'s back sound inside {view.LogId} plays here too.", MenuAudio.Back);
            }

            return true;
        }

        if (element.Kind != MenuTargetKind.Path || !MenuPress.TryRead(index, out bool success))
        {
            return false;
        }

        if (!live || view.Mode != Mode.Real)
        {
            return true;
        }

        if (view.Menu is { } open && DetailsHolds.Find(open.transform, element) is { } shortcut)
        {
            string missing = string.Empty;
            PlaySound($"press:{view.LogId}", $"Menus: {view.LogId}: {LogNames.HostTag()} pressed {element}; its {(success ? "trigger" : "failure")} sound plays here too.",
                () => missing = MenuAudio.Press(open, shortcut, success));
            if (missing.Length > 0 && _sounds.Add($"nopress:{view.LogId}:{element}"))
            {
                Log($"Menus: {view.LogId}: {LogNames.HostTag()}'s press of {element} has no sound here ({missing}).");
            }
        }
        else
        {
            Missed(view, $"the host's pressed shortcut {element}");
        }

        return true;
    }

    /// <summary>The host's click on a real view: the clicked element's own click sound, with the host's result and effect.</summary>
    private void ClickSound(View view, MenuTarget target, MenuEvent click)
    {
        if (view.Mode != Mode.Real || view.Menu == null || MenuTargets.Find(view.Menu.transform, target) is not { } navigable)
        {
            return;
        }

        bool played = false;
        PlaySound($"click:{view.LogId}", $"Menus: {view.LogId}: {LogNames.HostTag()}'s click on {target} plays its sound here too (result {click.Result}, effect {click.Effect}).",
            () => played = MenuAudio.Click(navigable, click.Result, click.Effect));
        if (!played && _sounds.Add($"noclick:{view.LogId}"))
        {
            Log($"Menus: {view.LogId}: the element {LogNames.HostTag()} clicked has no click sound here.");
        }
    }

    /// <summary>The host's back input closed the menu: its back sound, as the host's game played it.</summary>
    private void BackSound(View view) =>
        PlaySound("back-close", $"Menus: {LogNames.HostTag()} backed out of {view.LogId}; the back sound plays here too.", MenuAudio.Back);

    /// <summary>
    /// The host's navigation mode (a value of the menu): this PC's is made the same through the game's own change, so its
    /// highlight and select sounds follow the host's (the game plays neither in mouse mode). Said when it changes.
    /// </summary>
    private void FollowNavMode(View view, int mode)
    {
        try
        {
            // Only the game's own modes (a host's value is untrusted; 0 is "unknown"): the game stores any other as it is.
            int here = MenuAudio.NavMode();
            if (here < 0 || here == mode || mode is <= 0 or > MenuSounds.MaxNavMode)
            {
                return;
            }

            if (MenuAudio.SetNavMode(mode, out string problem))
            {
                // Said once per mode: this PC's own mouse can move it back any number of times.
                if (_sounds.Add($"navmode:is:{mode}"))
                {
                    Log($"Menus: {view.LogId}: this PC's navigation mode is now {LogNames.HostTag()}'s ({MenuSounds.NavModeName(mode)}; it was {MenuSounds.NavModeName(here)}): " +
                        "its highlight and select sounds follow the host's.");
                }
            }
            else if (_sounds.Add($"navmode:{problem}"))
            {
                Log($"Menus: this PC's navigation mode can't follow {LogNames.HostTag()}'s ({problem}).");
            }
        }
        catch (Exception ex)
        {
            if (_sounds.Add("navmode:failed"))
            {
                ModLog.Warn($"Menus: this PC's navigation mode couldn't follow {LogNames.HostTag()}'s ({ModLog.SafeErrorText(ex)}).");
            }
        }
    }

    /// <summary>The host's newest navigation mode (a value of the menu) again: this PC's own input may have moved its own since.</summary>
    private void FollowNavModeOf(View view, MirroredMenu menu)
    {
        if (menu.Values.TryGetValue(NavModeTarget, out int value) && MenuSounds.TryReadNavMode(NavModeTarget, value, out int mode))
        {
            FollowNavMode(view, mode);
        }
    }

    /// <summary>
    /// The host's squad screen plays its exit animation (<c>ChooseMetaMenu.SetState(ExitAnimation)</c>: the zamboni
    /// leaving, its sound an animation event): this PC's copy runs the same step. Its commit is refused here
    /// (<c>ConfirmSelections</c>'s prefix: nothing written, it reports done), and its timer to the next step (the menu's own
    /// hide, <c>Perform</c>, which would show a transition of its own) is cancelled at once: the host's close closes
    /// the copy, with the host's transition. Its selection goes too, as on the host.
    /// </summary>
    private void SquadExitAnimation(View view, Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu squads)
    {
        if (Try(view, "playing the squad screen's exit animation", () =>
        {
            squads.SetState(Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu.EState.ExitAnimation, true);
            squads.m_CancelExit?.Cancel();
        }) && _sounds.Add("squad exit"))
        {
            Log($"Menus: {view.LogId}: {LogNames.HostTag()} confirmed; its exit animation plays here too (nothing is committed on this PC; it closes with the host's).");
        }
    }

    /// <summary>
    /// The host's squad screen reached its last exit step (the same frame as its hide): this PC's copy takes the step's
    /// animator state (<c>SetState</c>) without the step's hide, which the host's close does here.
    /// </summary>
    private void SquadExiting(View view, Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu squads) =>
        Try(view, "playing the squad screen's last exit step", () =>
        {
            squads.m_CurrentState = Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu.EState.Exiting;
            if (squads.Animator is { } animator && animator != null && animator.isInitialized)
            {
                animator.SetInteger(Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu.STATE, (int)Il2CppTape2Tape.Hockey.UI.ChooseMetaMenu.EState.Exiting);
            }
        });

    /// <summary>
    /// Before a reward menu's copy closes: the game's fade to black, when the host's own close faded first
    /// (<see cref="FadesOnClose"/>). True when it started: the copy closes behind it (<see cref="RewardFadeRules.FadeSeconds"/>).
    /// </summary>
    private bool FadeBeforeClose(View view)
    {
        if (!view.FadeOnClose)
        {
            return false;
        }

        try
        {
            bool faded = RewardFade.Begin(view.Serial);
            if (_sounds.Add($"fade:{faded}"))
            {
                Log(faded
                    ? $"Menus: {view.LogId} closes here behind the game's fade to black, as on {LogNames.HostTag()}'s screen."
                    : $"Menus: {view.LogId}: {LogNames.HostTag()}'s close faded to black; this PC's scene has no reward handler, so it closes without the fade.");
            }

            return faded;
        }
        catch (Exception ex)
        {
            if (_sounds.Add("fade:failed"))
            {
                ModLog.Warn($"Menus: {view.LogId}: the game's fade to black couldn't run here ({ModLog.SafeErrorText(ex)}).");
            }

            return false;
        }
    }

    /// <summary>
    /// The host's game started its fade to black before a reward menu (the host's HUD, <c>endgame.fade</c>): the same fade plays
    /// here, unless a cover is up already (the last reward's close faded) or the host's menu has reached this PC already
    /// (nothing may fade over a menu that's up). The host's menu opens here when it opens, under the cover, whichever comes
    /// first. Never throws: a failure is the old look, a menu opening with no fade before it.
    /// </summary>
    public void HostRewardFade()
    {
        try
        {
            uint newest = 0;
            foreach (View view in _views.Values)
            {
                if (view.Type is MenuPayloads.RelicReward or MenuPayloads.PowerUpReward)
                {
                    return;
                }

                newest = Math.Max(newest, view.Serial);
            }

            if (RewardFade.Up)
            {
                return;
            }

            bool faded = RewardFade.BeginBeforeMenu(newest);
            if (_sounds.Add($"fade before:{faded}"))
            {
                Log(faded
                    ? $"Menus: the fade to black before {LogNames.HostTag()}'s reward menu plays here too (its menu opens under it; at most {RewardFadeRules.MaxBeforeMenuSeconds:0} s)."
                    : $"Menus: {LogNames.HostTag()}'s game faded to black before its reward menu; this PC's scene has no reward handler, so its menu opens without the fade.");
            }
        }
        catch (Exception ex)
        {
            if (_sounds.Add("fade before:failed"))
            {
                ModLog.Warn($"Menus: the fade to black before {LogNames.HostTag()}'s reward menu couldn't run here ({ModLog.SafeErrorText(ex)}); its menu opens without it.");
            }
        }
    }

    /// <summary>
    /// While the reward fade's cover is up: it goes back as soon as the host's next screen shows here, for real, as a note or
    /// as text (nothing may sit under a black screen), or by <see cref="RewardFade.Tick"/>'s other rules.
    /// </summary>
    private void RewardFadeTick(double now)
    {
        if (!RewardFade.Up || now < _fadeLookAt)
        {
            return;
        }

        _fadeLookAt = RewardFade.BeforeMenu ? now : now + FadeLookSeconds;
        bool next = false;
        foreach (View view in _views.Values)
        {
            next |= view.Serial > RewardFade.Serial && view.Type != BrawlViews.Type && (view.Mode is Mode.Real or Mode.Note || (view.Mode == Mode.Panel && !Quiet(view)));
        }

        try
        {
            string why = RewardFade.Tick(next);
            if (why.Length > 0 && _sounds.Add($"fade back:{why}"))
            {
                Log($"Menus: the fade to black goes back here: {why}.");
            }
        }
        catch (Exception ex)
        {
            RewardFade.Forget();
            ModLog.Warn($"Menus: the fade to black couldn't go back here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Following stopped: a cover still up goes back (<paramref name="gameClosing"/>: no game calls).</summary>
    private static void RewardFadeStopped(bool gameClosing)
    {
        try
        {
            if (gameClosing)
            {
                RewardFade.Forget();
            }
            else
            {
                RewardFade.End();
            }
        }
        catch (Exception ex)
        {
            RewardFade.Forget();
            ModLog.Warn($"Menus: the fade to black couldn't go back here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Plays one of the host's sounds; the first of each <paramref name="kind"/> is logged, a failure once.</summary>
    private void PlaySound(string kind, string first, Action play)
    {
        try
        {
            play();
            if (_sounds.Add(kind))
            {
                Log(first);
            }
        }
        catch (Exception ex)
        {
            if (_sounds.Add($"{kind}:failed"))
            {
                ModLog.Warn($"Menus: a sound of {LogNames.HostTag()}'s couldn't play here ({kind}: {ModLog.SafeErrorText(ex)}).");
            }
        }
    }

    /// <summary>Following started: the first of each sound is said again.</summary>
    private void SoundsStarted() => _sounds.Clear();
}
#endif
