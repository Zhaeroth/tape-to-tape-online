#if HAS_INTEROP
using System;
using MelonLoader;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// A friend whose session ended while its game was in one of the host's scenes (the world map) is on its way back to its
/// own title screen. Until it gets there, the friend's UI lock (<see cref="FriendUi"/>) and the world-map guards
/// (<see cref="MapGuards"/>) stay on: the map on screen is still the host's, and a node entered or a menu used now would act
/// on this game's own run. The title screen loading ends it, or <see cref="TimeoutSeconds"/> at the latest, so the lock can
/// never outlast a failed load. Never throws into the game.
/// </summary>
internal static class SessionExit
{
    /// <summary>The longest the guards stay on waiting for the title screen.</summary>
    public const double TimeoutSeconds = 120;

    private static bool _subscribed;
    private static double _since;

    /// <summary>True while the friend's game is leaving the host's scenes for its own title screen.</summary>
    public static bool Leaving { get; private set; }

    /// <summary>The friend's game is leaving a host scene for its title screen: keep the lock and guards on until it's there.</summary>
    public static void Begin(string why)
    {
        try
        {
            if (!_subscribed)
            {
                _subscribed = true;
                MelonEvents.OnSceneWasLoaded.Subscribe(OnSceneLoaded);
                MelonEvents.OnUpdate.Subscribe(Tick);
            }

            Leaving = true;
            _since = FrameClock.Seconds;
            ModLog.Info($"Scenes: leaving the host's scenes ({why}); this game's UI stays locked and the map guarded until its title screen loads.");
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: keeping the guards on until the title screen failed; unlocking now", ex);
            End("couldn't wait for the title screen");
        }
    }

    private static void OnSceneLoaded(int buildIndex, string name)
    {
        try
        {
            if (Leaving && name == WorldMap.TitleScene)
            {
                if (CampaignRelay.Current is { Following: true })
                {
                    // A new session took over before the title loaded: the lock is its own now (its Lock found this one still on).
                    Leaving = false;
                    return;
                }

                End("this game's title screen loaded");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: ending the session's exit failed", ex);
        }
    }

    private static void Tick()
    {
        if (!Leaving)
        {
            return;
        }

        try
        {
            if (CampaignRelay.Current is { Following: true })
            {
                // A new session took over before the title loaded: its own lock is in charge now.
                Leaving = false;
                return;
            }

            FriendUi.Update();
            if (FrameClock.Seconds - _since > TimeoutSeconds)
            {
                ModLog.Warn($"Scenes: this game's title screen didn't load within {TimeoutSeconds:0} s of the session ending; unlocking its UI anyway (quit to the title from the map's menu).");
                End("the title screen didn't load in time");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: watching the session's exit failed; unlocking now", ex);
            End("an error");
        }
    }

    private static void End(string why)
    {
        Leaving = false;
        try
        {
            FriendUi.Unlock(why);
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: unlocking this game's UI failed", ex);
        }
    }
}
#endif
