#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's side of the menu exits' transitions and close reasons: what a tracked
/// menu's close tells friends besides the close itself.
/// <list type="bullet">
/// <item>The transition its own exit shows (<see cref="MenuExits"/>): for a menu that shows it before it hides (the
/// new-unlocks and run-end menus), the one the host's game showed since the menu opened (every transition passes
/// <c>T2TMenu.Show</c>: <see cref="Shown"/>); for one that hides first (the squad screen, the lineup, the old squad menus),
/// the one its <c>Hide</c> will show, read from the same fields it reads (its result, its mode), and checked when the
/// host's game then shows it (logged as sent, or warned when it differs).</item>
/// <item>The reason: <see cref="MenuCloseReason.Back"/> when it closed inside the host's back input
/// (<c>NavigationController.OnBackInput</c>, whose consumed input plays the game's back sound).</item>
/// </list>
/// Game thread only; the callers guard against exceptions.
/// </summary>
internal static class HostTransitions
{
    /// <summary>How long after its show a transition counts as a closing menu's own (it's shown just before the menu hides).</summary>
    private const double ObservedSeconds = 5;

    /// <summary>How long a predicted transition is waited for on the host's screen.</summary>
    private const double CheckSeconds = 5;

    private const string GeneralManagerSquad = "generalmanager";
    private const int MaxChecks = 8;

    private static readonly List<Check> Checks = new();
    private static readonly HashSet<string> Confirmed = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);
    private static MenuTransition _lastKind;
    private static double _lastAt = double.NegativeInfinity;
    private static int _backDepth;
    private static int _backFrame = -1;
    private static int _backPlayedFrame = -1;
    private static bool _closedInBack;
    private static Il2CppSystem.Action? _backListener;
    private static IntPtr _listenedNavigation;
    private static bool _backListenFailed;
    private static bool _unknownLogged;

    /// <summary>
    /// True while the host's back input is being handled (a menu closing now closes because of it). Only within the frame it
    /// started: a back input whose handling threw (no postfix) doesn't make later closes look like backs.
    /// </summary>
    public static bool InBackInput => _backDepth > 0 && _backFrame == UnityEngine.Time.frameCount;

    /// <summary>True while a predicted transition waits to be checked (<see cref="Tick"/> has work).</summary>
    public static bool Checking => Checks.Count > 0;

    /// <summary>
    /// The host's back input starts being handled (<c>NavigationController.OnBackInput</c>'s prefix, on
    /// <paramref name="navigation"/>): its back stack's "processed" event is listened to (once per controller), the event
    /// the game's back sound plays on (<c>T2TUIModule.OnBackFromBackStack</c>).
    /// </summary>
    public static void BackStarted(NavigationController navigation)
    {
        int frame = UnityEngine.Time.frameCount;
        _backDepth = _backFrame == frame ? _backDepth + 1 : 1;
        _backFrame = frame;
        _closedInBack = false;
        ListenForBack(navigation);
    }

    /// <summary>
    /// The host's back input was handled (its postfix). True when the host's game played its back sound for it and no
    /// shared menu closed in it (a step back inside a menu, a prompt opening): the caller sends the sound on the newest
    /// menu. A close in it already carries the reason <see cref="MenuCloseReason.Back"/>.
    /// </summary>
    public static bool BackEnded()
    {
        _backDepth = Math.Max(0, _backDepth - 1);
        bool played = _backPlayedFrame == UnityEngine.Time.frameCount;
        _backPlayedFrame = -1;
        return played && !_closedInBack && _backDepth == 0;
    }

    /// <summary>Hosting started or stopped: nothing is carried over.</summary>
    public static void Clear()
    {
        Checks.Clear();
        _lastKind = MenuTransition.None;
        _lastAt = double.NegativeInfinity;
        _backDepth = 0;
        _backPlayedFrame = -1;
        _closedInBack = false;
    }

    /// <summary>A menu shows on the host's screen (<c>T2TMenu.Show</c>): a transition is noted, and checked against a prediction.</summary>
    public static void Shown(AbstractMenu menu)
    {
        if (!WooshGame.IsWoosh(menu))
        {
            return;
        }

        MenuTransition kind = WooshGame.Kind(menu);
        if (kind == MenuTransition.None)
        {
            if (!_unknownLogged)
            {
                _unknownLogged = true;
                ModLog.Warn($"Menus: the host's game showed a transition this build can't name ({SafeName(menu)}); its menu exits go to friends without it.");
            }

            return;
        }

        double now = FrameClock.Seconds;
        _lastKind = kind;
        _lastAt = now;
        for (int i = 0; i < Checks.Count; i++)
        {
            if (now - Checks[i].At <= CheckSeconds)
            {
                Verify(Checks[i], kind);
                Checks.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>
    /// The transition a tracked menu's close tells friends, as the host's game shows it for this close (see the class
    /// summary); <see cref="MenuTransition.None"/> for a menu whose exit shows none, a close during a scene load, or one
    /// that didn't come from the menu's own exit. Never throws.
    /// </summary>
    public static MenuTransition AtClose(AbstractMenu menu, string id, string type, double openedAt)
    {
        try
        {
            if (!MenuExits.ShowsTransition(type) || WorldMap.Loading)
            {
                return MenuTransition.None;
            }

            double now = FrameClock.Seconds;
            if (MenuExits.TransitionFirst(type))
            {
                // Shown and waited for before the menu hides: the host's game showed it since the menu opened.
                return _lastAt >= openedAt && now - _lastAt <= ObservedSeconds ? _lastKind : MenuTransition.None;
            }

            MenuTransition predicted = Predict(menu, type, out string from);
            if (predicted != MenuTransition.None)
            {
                if (Checks.Count >= MaxChecks)
                {
                    Checks.RemoveAt(0);
                }

                Checks.Add(new Check(id, type, predicted, from, now));
            }

            return predicted;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: couldn't tell which transition the host's {id} exit shows ({ModLog.SafeErrorText(ex)}); friends get its close without one.");
            return MenuTransition.None;
        }
    }

    /// <summary>The reason a tracked menu's close tells friends (a close inside the back input is noted: its sound goes with it).</summary>
    public static MenuCloseReason ReasonNow()
    {
        if (!InBackInput)
        {
            return MenuCloseReason.Other;
        }

        _closedInBack = true;
        return MenuCloseReason.Back;
    }

    /// <summary>
    /// Listens to <paramref name="navigation"/>'s back stack "processed" event (raised at the end of
    /// <c>OnBackInput</c> when a back handler took the input): once per controller, a persistent delegate.
    /// </summary>
    private static void ListenForBack(NavigationController navigation)
    {
        if (navigation.Pointer == _listenedNavigation || _backListenFailed)
        {
            return;
        }

        try
        {
            if (_backListener == null)
            {
                _backListener = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnBackProcessed))
                    ?? throw new InvalidOperationException("the listener couldn't be made");
                EventChannel.ModListeners.Add(_backListener.Pointer);
            }

            navigation.OnBackStackProcessed.RegisterPersistentDelegate(_backListener, "T2TOnline");
            _listenedNavigation = navigation.Pointer;
        }
        catch (Exception ex)
        {
            _backListenFailed = true;
            ModLog.Warn($"Menus: can't listen for the host's back sound ({ModLog.SafeErrorText(ex)}); friends hear it only when the host backs out of a menu.");
        }
    }

    private static void OnBackProcessed()
    {
        try
        {
            if (_backDepth > 0)
            {
                _backPlayedFrame = UnityEngine.Time.frameCount;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Menus: noting the host's back sound failed", ex);
        }
    }

    /// <summary>Every frame while a prediction waits (<see cref="Checking"/>): one the host's game never showed is warned about.</summary>
    public static void Tick()
    {
        double now = FrameClock.Seconds;
        for (int i = Checks.Count - 1; i >= 0; i--)
        {
            Check check = Checks[i];
            if (now - check.At <= CheckSeconds)
            {
                continue;
            }

            Checks.RemoveAt(i);
            if (Warned.Add($"{check.Type}:none"))
            {
                ModLog.Warn($"Menus: the host's {check.Id} exit was sent to friends with its {MenuExits.Name(check.Kind)} transition ({check.From}), " +
                    $"but the host's game showed none within {CheckSeconds:0} s; friends showed one the host didn't.");
            }
        }
    }

    /// <summary>
    /// The transition a menu that hides first will show, from what its <c>Hide</c> reads: the squad screen's result, tutorial and squad;
    /// the lineup's result; the old squad menu's mode. None when the close isn't its own exit's (the squad screen not exiting, the old
    /// squad menu not exiting).
    /// </summary>
    private static MenuTransition Predict(AbstractMenu menu, string type, out string from)
    {
        from = string.Empty;
        switch (type)
        {
            case MenuExits.ChooseMeta when menu.TryCast<ChooseMetaMenu>() is { } squads:
                if (squads.m_CurrentState is not (ChooseMetaMenu.EState.ExitAnimation or ChooseMetaMenu.EState.Exiting))
                {
                    return MenuTransition.None;
                }

                bool confirmed = ChooseMetaMenu.LastResult.Confirmed;
                bool tutorial = squads.m_CampaignTutorial?.IsActive == true;
                bool manager = !tutorial && confirmed && CampaignUtil.Instance?.IsPlayingSquad(GeneralManagerSquad, false) == true;
                from = !confirmed ? "it was backed out of" : tutorial ? "confirmed, in the campaign tutorial" : manager ? "confirmed, the General Manager's squad" : "confirmed";
                return MenuExits.ForChooseMeta(confirmed, tutorial || manager);
            case MenuExits.Benchwarmer:
                bool lineup = BenchwarmerMenu.LastResult.Confirmed;
                from = lineup ? "confirmed" : "backed out of";
                return MenuExits.ForBenchwarmer(lineup);
            case MenuExits.OldSquad when menu.TryCast<OldSquadMenu>() is { m_IsExiting: true } oldSquad:
                int mode = (int)oldSquad.m_Mode;
                from = mode == MenuExits.SuperStarOnlyMode ? "the superstar pick" : "the old school draft";
                return MenuExits.ForOldSquad(mode);
            default:
                return MenuTransition.None;
        }
    }

    /// <summary>A prediction met the host's next transition: said once per menu class when it matches, warned when it doesn't.</summary>
    private static void Verify(Check check, MenuTransition shown)
    {
        if (shown == check.Kind)
        {
            if (Confirmed.Add(check.Type))
            {
                ModLog.Info($"Menus: the host's {check.Id} exit showed {MenuExits.Name(shown)} ({check.From}), as friends were told.");
            }

            return;
        }

        if (Warned.Add($"{check.Type}:{shown}"))
        {
            ModLog.Warn($"Menus: the host's {check.Id} exit showed {MenuExits.Name(shown)}, but friends were told {MenuExits.Name(check.Kind)} ({check.From}); " +
                "their screens show the other transition.");
        }
    }

    private static string SafeName(AbstractMenu menu) => Core.Diagnostics.SafeText.Clean($"{MenuGame.TypeName(menu)} \"{MenuGame.MenuId(menu)}\"", 80);

    /// <summary>A predicted transition, waiting for the host's game to show it.</summary>
    private sealed record Check(string Id, string Type, MenuTransition Kind, string From, double At);
}
#endif
