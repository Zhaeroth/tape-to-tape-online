#if HAS_INTEROP
using System;
using System.Reflection;
using HarmonyLib;
using Il2CppRewired;
using Il2CppUI;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online.Panel;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// Online play must not stop when a game window loses focus: a friend clicks over to Discord to watch the host's stream,
/// the host to manage it. Offline the game would pause or go deaf: <c>InGameMenu.PauseOnNextOpportunity</c> (reached from
/// its focus-loss, app-pause and refresh-focus handlers) pauses the match, Unity may stop running a player whose window
/// isn't focused, and Rewired can ignore controllers while unfocused. While a session is active this keeps the game
/// running (<c>Application.runInBackground</c>), keeps Rewired reading pads, and skips the focus pause; leaving the
/// session restores the game's own settings.
/// <para>
/// Each stretch without focus is measured (<see cref="Spells"/>: its length, frames and longest frame, and the focus pauses
/// skipped): the host's Alt-Tab can cost friends' input, and the session's log ties
/// the two.
/// </para>
/// <para>
/// Rewired's "ignore input when not in focus" is one switch for every device, so with it off the keyboard and mouse
/// could reach the game while the player types in another window (whether they do depends on Rewired's input source).
/// Only pads should play in the background (D23), so while the window is unfocused Rewired's keyboard and mouse
/// controllers are switched off, and back on with focus. The mod's panel (F1) switches them off too; each gives back only
/// what it switched off itself, and never while the panel holds them.
/// </para>
/// </summary>
internal static class FocusGuard
{
    private static bool _savedRunInBackground;
    private static bool _savedIgnoreUnfocused;
    private static bool _keyboardOff;
    private static bool _mouseOff;
    private static double _lastFrameAt = double.NaN;

    /// <summary>True while the guard is on.</summary>
    public static bool Active { get; private set; }

    /// <summary>True when <see cref="Update"/> has nothing to do: the guard is off and gives nothing back (no keyboard or mouse of its switching is still off).</summary>
    public static bool Idle => !Active && !_keyboardOff && !_mouseOff;

    /// <summary>This session's stretches without focus and the focus pauses skipped.</summary>
    public static FocusSpells Spells { get; } = new();

    /// <summary>The stretch without focus that ended last (null before any this session).</summary>
    public static FocusSpell? LastSpell { get; private set; }

    /// <summary>Patches the game. Returns false, logging why, if the target is missing.</summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        MethodInfo? target = AccessTools.Method(typeof(InGameMenu), "PauseOnNextOpportunity");
        if (target == null)
        {
            ModLog.Warn("Focus guard: InGameMenu.PauseOnNextOpportunity not found; losing focus may pause online matches.");
            return false;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(typeof(FocusGuard).GetMethod(nameof(PausePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        return true;
    }

    /// <summary>Turns the guard on: the game keeps running and reading pads without focus, and focus loss doesn't pause.</summary>
    public static void Enter()
    {
        if (Active)
        {
            return;
        }

        Spells.Clear();
        LastSpell = null;
        _lastFrameAt = double.NaN;
        _savedRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        ReInput.ConfigHelper? config = ReInput.configuration;
        if (config != null)
        {
            _savedIgnoreUnfocused = config.ignoreInputWhenAppNotInFocus;
            config.ignoreInputWhenAppNotInFocus = false;
        }

        Active = true;
        ModLog.Info($"Online: while you're online, the game keeps running and reading pads when its window isn't focused (keyboard and mouse only with focus), " +
            $"and losing focus doesn't pause the match (the game had run-in-background {_savedRunInBackground}, Rewired ignoring unfocused input {_savedIgnoreUnfocused}).");
    }

    /// <summary>Turns the guard off and restores the game's own settings (the keyboard and mouse too, if it switched them off).</summary>
    public static void Exit()
    {
        if (!Active)
        {
            return;
        }

        Active = false;
        Application.runInBackground = _savedRunInBackground;
        ReInput.ConfigHelper? config = ReInput.configuration;
        if (config != null)
        {
            config.ignoreInputWhenAppNotInFocus = _savedIgnoreUnfocused;
        }

        GiveKeyboardBack();
        LastSpell = Spells.Regained(Time.realtimeSinceStartupAsDouble) ?? LastSpell;
        ModLog.Info(Spells.Count == 0 && Spells.PausesSkipped == 0
            ? "Online: focus settings are back to the game's own (the window kept focus all session)."
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Online: focus settings are back to the game's own (unfocused {Spells.Count} time(s), {Spells.TotalSeconds:0} s in all, longest frame meanwhile {Spells.LongestFrameMs:0} ms; ") +
              $"{Spells.PausesSkipped} focus pause(s) the game asked for skipped).");
    }

    /// <summary>
    /// Call every frame: while the guard is on and the window unfocused, Rewired's keyboard and mouse are off (pads keep
    /// playing); with focus back, or the guard off, they're back on. A keyboard or mouse already off (the panel's) is left
    /// alone, and nothing is given back while the panel holds them (it gives them back itself).
    /// </summary>
    public static void Update(bool focused)
    {
        MeasureFocus(focused);
        if (Active && !focused)
        {
            if (!ReInput.isReady)
            {
                return;
            }

            if (ReInput.controllers.Keyboard is { enabled: true } keyboard)
            {
                keyboard.enabled = false;
                _keyboardOff = true;
            }

            if (ReInput.controllers.Mouse is { enabled: true } mouse)
            {
                mouse.enabled = false;
                _mouseOff = true;
            }

            return;
        }

        GiveKeyboardBack();
    }

    /// <summary>Gives back the keyboard and mouse this guard switched off, unless the panel holds them now.</summary>
    private static void GiveKeyboardBack()
    {
        if ((!_keyboardOff && !_mouseOff) || PanelInput.Held || !ReInput.isReady)
        {
            return;
        }

        if (_keyboardOff)
        {
            _keyboardOff = false;
            if (ReInput.controllers.Keyboard is { } keyboard)
            {
                keyboard.enabled = true;
            }
        }

        if (_mouseOff)
        {
            _mouseOff = false;
            if (ReInput.controllers.Mouse is { } mouse)
            {
                mouse.enabled = true;
            }
        }
    }

    /// <summary>Each frame while the guard is on: a stretch without focus starts, runs (its frames) or ends.</summary>
    private static void MeasureFocus(bool focused)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        double frame = double.IsNaN(_lastFrameAt) ? 0 : now - _lastFrameAt;
        _lastFrameAt = now;
        if (!Active)
        {
            return;
        }

        if (!focused)
        {
            Spells.Lost(now);
            Spells.Frame(frame);
        }
        else if (Spells.Unfocused)
        {
            LastSpell = Spells.Regained(now);
        }
    }

    /// <summary>
    /// Skips the game's focus pause while online; the first skip a session is logged, the rest counted (it was
    /// silent before). Never throws into the game.
    /// </summary>
    private static bool PausePrefix()
    {
        if (!Active)
        {
            return true;
        }

        try
        {
            if (Spells.PauseSkipped())
            {
                ModLog.Info("Focus guard: the game asked to pause on a focus change (InGameMenu.PauseOnNextOpportunity); skipped while online. Further ones are counted.");
            }
        }
        catch (Exception)
        {
            // Only the count is lost.
        }

        return false;
    }
}
#endif
