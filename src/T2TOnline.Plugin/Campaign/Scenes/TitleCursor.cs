#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The title screen's button groups and cursor on every screen. The title (<c>TitleScreen</c>, not a menu) shows one group of
/// buttons at a time (press start, main, campaign, play, practice, customize) and moves between them with a short
/// transition; a friend's game showed its own title, wherever its own last press left it.
/// <list type="bullet">
/// <item>Host: the title view carries the group shown (or the one it's moving to) and the title button the cursor is on, by
/// the title's own name for it (<see cref="TitleViewDetails"/>); none while the cursor is in a menu over the title.</item>
/// <item>Friend: its own title follows (<see cref="TitleFollow"/>): the game's own press-start (<c>PressedToStart</c>, no
/// player) while it still waits at "press start", the game's own group change (<c>SetActiveGroup</c>, what the group buttons
/// do), then the host's button selected (<c>SetCurrentNavigable</c>). A button this title doesn't show (Continue and Abandon
/// are there only with a run of this PC's own) is skipped and logged. A friend's title never shows the game's onboarding
/// prompt meanwhile (<c>ShouldShowOnboardingPrompt</c> is false while following): it would save the profile and wait for an
/// answer nobody here can give.</item>
/// </list>
/// </summary>
internal static class TitleCursor
{
    private const double FindSeconds = 0.5;
    private const double StepSeconds = 0.1;
    private const int LinesPerMinute = 20;
    private const int MaxErrors = 3;

    // The title's buttons, by the names the title view carries (the TitleScreen fields that hold them).
    private static readonly (string Name, Func<TitleScreen, AbstractButton?> Get)[] Buttons =
    {
        ("campaign", t => t.m_CampaignButton), ("play", t => t.m_PlayButton), ("customize", t => t.m_CustomizeButton),
        ("compendium", t => t.m_CompendiumButton), ("settings", t => t.m_SettingsButton), ("glossary", t => t.m_GlossaryButton),
        ("exit", t => t.m_ExitButton), ("newRun", t => t.m_NewRunButton), ("continueRun", t => t.m_ContinueRunButton),
        ("abandonRun", t => t.m_AbandonRunButton), ("campaignBack", t => t.m_CampaignBackButton), ("playNow", t => t.m_PlayNowButton),
        ("practice", t => t.m_PracticeButton), ("tournament", t => t.m_TournamentButton), ("unlockables", t => t.m_UnlockablesButton),
        ("runHistory", t => t.m_RunHistoryButton), ("playBack", t => t.m_PlayBackButton), ("tutorial", t => t.m_TutorialButton),
        ("skirmish", t => t.m_SkirmishButton), ("shootout", t => t.m_ShootoutButton), ("practiceBack", t => t.m_PracticeBackButton),
        ("customizeTeams", t => t.m_CustomizeTeamsButton), ("customizePlayers", t => t.m_CustomizePlayersButton),
        ("customizeBack", t => t.m_CustomizeBackButton), ("switchBranch", t => t.m_SwitchBranchButton),
        ("online", t => NativeLobby.OnlineGroup.Button(t, "online")),
    };

    private static readonly LineBudget Lines = new("Scenes (title)", LinesPerMinute);
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);
    private static readonly TitleViewDetails Unknown = new(null, null);
    private static TitleScreen? _title;
    private static NavigationController? _navigation;
    private static TitleViewDetails _hostDetails = Unknown;
    private static double _findAt;
    private static IntPtr _lastNavigable;
    private static string? _lastButton;
    private static TitleViewDetails? _wanted;
    private static string? _skipped;
    private static double _stepAt;
    private static bool _pressed;
    private static bool _offLogged;
    private static int _errors;
    private static int _heldPrompts;

    /// <summary>True when the onboarding prompt's block went in (without it, a friend's title isn't moved).</summary>
    public static bool Installed { get; private set; }

    /// <summary>Patches the onboarding prompt's check (held off on a following friend). Adds a line to <paramref name="failed"/> if it couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        int before = failed.Count;
        ScenePatches.Prefix(harmony, failed, typeof(TitleScreen), nameof(TitleScreen.ShouldShowOnboardingPrompt), typeof(TitleCursor), nameof(OnboardingPrefix));
        Installed = failed.Count == before;
    }

    // ---- Host ----

    /// <summary>
    /// Host: the title's group (the one shown, or the one it's moving to) and the title button the cursor is on; both null
    /// when the title isn't up, the button null while the cursor is elsewhere (a menu over the title). The same object while
    /// nothing changed (it's read many times a second). Never throws.
    /// </summary>
    public static TitleViewDetails HostDetails()
    {
        try
        {
            TitleScreen? title = Find();
            if (title == null)
            {
                return _hostDetails = Unknown;
            }

            int group = (int)(title._isTransitioningGroup ? title._transitionTarget : title.m_CurrentButtonGroup);
            string? button = ButtonOf(title, _navigation?.CurrentNavigable);
            if (group == NativeLobby.OnlineGroup.GroupValue)
            {
                // The Online group is a private step before a session exists: friends stay on their main group, on the row it opens from
                // (a friend whose array didn't grow would go deaf on SetActiveGroup(6)).
                group = (int)TitleScreen.EButtonGroup.Main;
                button = "online";
            }

            if (_hostDetails.Group != group || !ReferenceEquals(_hostDetails.Button, button))
            {
                _hostDetails = new TitleViewDetails(group, button);
            }

            return _hostDetails;
        }
        catch (Exception ex)
        {
            Error("reading the host's title", ex);
            return _hostDetails = Unknown;
        }
    }

    // ---- Friend ----

    /// <summary>Friend: the host's title as its newest view has it (followed from the next frame this title is up).</summary>
    public static void Want(TitleViewDetails host)
    {
        if (_wanted?.Group != host.Group && host.Group is int group)
        {
            Log($"Scenes: the host's title shows its {(TitleGroup)group} buttons{(host.Button != null ? $" (on {host.Button})" : string.Empty)}; this PC's title follows.");
        }

        if (_wanted?.Button != host.Button)
        {
            _skipped = null;
        }

        _wanted = host;
        _stepAt = 0;
    }

    /// <summary>Friend: following stopped, or another screen took over: nothing more is moved (the title stays where it is).</summary>
    public static void Leave()
    {
        _wanted = null;
        _skipped = null;
        _pressed = false;
    }

    /// <summary>
    /// Friend: every frame while the host is on its title and this game is on its own: the next step toward the host's group
    /// and button, at most every <see cref="StepSeconds"/>. Never throws (a failing step is logged; after a few, it stops).
    /// </summary>
    public static void Tick()
    {
        double now = FrameClock.Seconds;
        if (_wanted == null || now < _stepAt || _errors >= MaxErrors)
        {
            return;
        }

        _stepAt = now + StepSeconds;
        if (!Installed)
        {
            if (!_offLogged)
            {
                _offLogged = true;
                ModLog.Warn("Scenes: this PC's title doesn't follow the host's (the game's onboarding prompt couldn't be held off: it would show here and save the profile).");
            }

            return;
        }

        try
        {
            TitleScreen? title = Find();
            if (title == null)
            {
                return;
            }

            NavigationController? navigation = _navigation;
            AbstractNavigable? current = navigation?.CurrentNavigable;
            Il2CppSystem.Collections.Generic.List<string>? over = title.m_MenusOverSelf;
            var here = new TitleState(
                (int)title.m_CurrentButtonGroup,
                title._isTransitioningGroup,
                (int)title._transitionTarget,
                title._groupInputReady,
                ButtonOf(title, current),
                title.m_IsStartingRun || title.m_IsExiting || title.m_IsRefreshingCampaignState || (over != null && over.Count > 0));
            TitleStep step = TitleFollow.Next(_wanted, here);

            // Only a step to take asks whether the title is on its way out: that looks through every loader in the scene
            // (FindObjectsOfType), too slow for ten times a second on a title that already shows the host's.
            if (step != TitleStep.None && !(step == TitleStep.Button && _wanted.Button == _skipped) && CampaignGame.AtSettledTitle())
            {
                Step(title, navigation, step);
            }
        }
        catch (Exception ex)
        {
            Error("following the host's title", ex);
        }
    }

    private static void Step(TitleScreen title, NavigationController? navigation, TitleStep step)
    {
        TitleViewDetails host = _wanted!;
        switch (step)
        {
            case TitleStep.PressStart:
                FriendUi.Apply(() => title.PressedToStart(null!));
                if (!_pressed)
                {
                    _pressed = true;
                    Log("Scenes: this PC's title was still at \"press start\": pressed it with the game's own press-start, as the host's title has its buttons up.");
                }

                break;
            case TitleStep.Group:
                var group = (TitleScreen.EButtonGroup)host.Group!.Value;
                if (host.Group.Value >= title._groupVisuals.Length)
                {
                    return; // past this title's array: BeginGroupTransition would index out of it with the input already off, and leave it deaf
                }

                FriendUi.Apply(() => title.SetActiveGroup(group));
                Log($"Scenes: this PC's title moves to its {(TitleGroup)host.Group.Value} buttons, as the host's (the game's own group change).");
                break;
            case TitleStep.Button when host.Button != null && host.Button != _skipped && navigation != null:
                AbstractButton? button = Button(title, host.Button);
                if (button == null || !button.gameObject.activeInHierarchy)
                {
                    // This title doesn't show it (Continue and Abandon need a run of this PC's own): left on its own. Only the
                    // title's own names are remembered and named: a host's view may carry any name of letters, one per view.
                    _skipped = host.Button;
                    if (!Known(host.Button))
                    {
                        Log("Scenes: the host's title highlights a button this version doesn't know; this PC's own highlight stays.");
                    }
                    else if (Missing.Add(host.Button))
                    {
                        ModLog.Info($"Scenes: the host's title highlights {host.Button}, which this PC's title doesn't show ({(button == null ? "it has no such button" : "this profile's title hides it")}); its own highlight stays.");
                    }

                    return;
                }

                FriendUi.Select(navigation, button);
                break;
        }
    }

    /// <summary>The title button <paramref name="navigable"/> is, by name, or null (none, or not one of the title's).</summary>
    private static string? ButtonOf(TitleScreen title, AbstractNavigable? navigable)
    {
        if (navigable == null)
        {
            _lastNavigable = IntPtr.Zero;
            return null;
        }

        if (navigable.Pointer == _lastNavigable)
        {
            return _lastButton;
        }

        _lastNavigable = navigable.Pointer;
        _lastButton = null;
        foreach ((string name, Func<TitleScreen, AbstractButton?> get) in Buttons)
        {
            if (get(title)?.Pointer == navigable.Pointer)
            {
                _lastButton = name;
                break;
            }
        }

        return _lastButton;
    }

    /// <summary>True when <paramref name="name"/> is one of the title's buttons, by the names the title view carries.</summary>
    private static bool Known(string name)
    {
        foreach ((string known, Func<TitleScreen, AbstractButton?> _) in Buttons)
        {
            if (known == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>This title's button called <paramref name="name"/>, or null.</summary>
    private static AbstractButton? Button(TitleScreen title, string name)
    {
        foreach ((string known, Func<TitleScreen, AbstractButton?> get) in Buttons)
        {
            if (known == name)
            {
                return get(title);
            }
        }

        return null;
    }

    /// <summary>The title screen, looked for at most every <see cref="FindSeconds"/> (it's a scene object: found once per load).</summary>
    private static TitleScreen? Find()
    {
        if (_title != null && _title.isActiveAndEnabled)
        {
            return _title;
        }

        double now = FrameClock.Seconds;
        if (now < _findAt)
        {
            return null;
        }

        _findAt = now + FindSeconds;
        _title = CampaignGame.Title();
        _navigation = _title == null ? null : MenuGame.Module()?.NavigationController;
        _lastNavigable = IntPtr.Zero;
        return _title;
    }

    /// <summary>
    /// The game's onboarding prompt check on a following friend's title: false (the prompt would save the profile and wait
    /// for an answer nobody here can give; the friend gets it at their own next visit). The host's game is never touched.
    /// </summary>
    private static bool OnboardingPrefix(ref bool __result)
    {
        try
        {
            // A lobby open holds it off too: it would pop over the lobby and start a tutorial on a Yes. One already up can be answered until Start (OnlineSession.Lobby.cs closes it then).
            if (CampaignRelay.Current?.Following != true && !SessionExit.Leaving && !NativeLobby.TourHolds.On && !Online.OnlineSession.LobbyUp)
            {
                return true;
            }

            __result = false;
            if (_heldPrompts++ == 0)
            {
                ModLog.Info("Scenes: the game's onboarding prompt was held off on this title (a lobby is open or this PC follows a host: it would save the profile and wait for an answer here); further ones are only counted.");
            }

            return false;
        }
        catch (Exception)
        {
            return true; // not knowing, the game's own check runs
        }
    }

    private static void Log(string line)
    {
        if (Lines.TrySpend())
        {
            ModLog.Info(line);
        }
    }

    private static void Error(string what, Exception ex)
    {
        if (_errors++ < MaxErrors)
        {
            ModLog.Error($"Scenes: {what} failed{(_errors == MaxErrors ? "; the title isn't followed any more this game" : string.Empty)}", ex);
        }
    }
}
#endif
