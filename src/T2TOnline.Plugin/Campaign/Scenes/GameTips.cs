#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppRogue.Tutorial;
using Il2CppTape2Tape.Dialogue;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The game's first-time campaign tips and its tutorial, kept off a following friend's screen. Screens a friend's game builds
/// for the host (the world map, the squad menu, the power-up reward menu) start the game's tips (<c>CampaignTips.ShowAsync</c>): the
/// blacksmith's speech over a dark overlay, checked against this player's own seen tips. Its lines wait for a skip press that nothing local can
/// give there (the friend's pad and UI input are off), so the tip would cover the host's screens for the session, and start again at every map
/// load (it's marked seen only once it ends). The tutorial's signals (<c>CampaignTutorial.Fire</c>) would move this player's own tutorial on
/// with the host's screens. On a friend that follows a host (or leaves the host's scenes, <see cref="SessionExit"/>):
/// <list type="bullet">
/// <item><c>CampaignTipsConfig.GetTip</c> (called only by <c>ShowAsync</c>'s body) hands back a disabled stand-in, which
/// <c>ShowAsync</c> takes as a switched-off tip: its task completes at once, nothing is shown and nothing is marked seen
/// (the player still gets each tip in their own campaign). <c>ShowAsync</c> itself returns a <c>UniTask</c> struct and isn't
/// patched;</item>
/// <item><c>CampaignTutorial.Fire</c> is skipped (only the tutorial's own steps wait for its signals).</item>
/// </list>
/// The host's game is never touched: the host sees and dismisses its tips as on the couch. Since v1 its tips reach
/// friends as the couch shows them to everyone (<see cref="TipViews"/>): the same lookup's postfix notes the host's tip, and a
/// friend plays it with the presenter's own speech call, never through <c>ShowAsync</c>, so this player's own tips keep their
/// stand-in and nothing is marked seen on this profile.
/// </summary>
internal static class GameTips
{
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static TipDefinition? _standIn;
    private static int _errors;

    /// <summary>Tips skipped on this PC since the game started.</summary>
    public static int Skipped { get; private set; }

    /// <summary>Tutorial signals skipped on this PC since the game started.</summary>
    public static int SkippedSignals { get; private set; }

    /// <summary>True while this PC follows a host, or leaves the host's scenes for its own title screen.</summary>
    private static bool Following => CampaignRelay.Current?.Following == true || SessionExit.Leaving;

    /// <summary>Patches the tip lookup and the tutorial's signals. Adds a line to <paramref name="failed"/> per patch that couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ScenePatches.Postfix(harmony, failed, typeof(CampaignTipsConfig), nameof(CampaignTipsConfig.GetTip), typeof(GameTips), nameof(GetTipPostfix));
        ScenePatches.Prefix(harmony, failed, typeof(CampaignTutorial), nameof(CampaignTutorial.Fire), typeof(GameTips), nameof(FirePrefix));
    }

    /// <summary>
    /// A tip about to show on a following friend's game: a disabled stand-in instead, so it ends at once. On a host, the tip
    /// is noted for friends (<see cref="TipViews"/>) and left as it is.
    /// </summary>
    private static void GetTipPostfix(CampaignTipsConfig __instance, ref TipDefinition __result)
    {
        if (__result == null)
        {
            return;
        }

        if (!Following)
        {
            HostTip(__instance, __result);
            return;
        }

        try
        {
            if (__result.disabled)
            {
                return; // off in the game's own data: it shows nothing anyway
            }

            string id = __result.id ?? string.Empty;
            _standIn ??= new TipDefinition { id = "t2t-online-skipped", disabled = true };
            __result = _standIn;
            Skipped++;
            if (Logged.Add(id))
            {
                ModLog.Info($"Scenes: skipped the game's campaign tip \"{SafeText.Clean(id, 48)}\" on this friend's game (nobody here could dismiss it; the host's game shows its own).");
            }
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: skipping a campaign tip on this friend's game failed (it may show)", ex);
            }
        }
    }

    /// <summary>Host: the game looked up a tip to show; it goes to friends once it's up (<see cref="TipViews.HostAsked"/>). Reads only.</summary>
    private static void HostTip(CampaignTipsConfig config, TipDefinition tip)
    {
        try
        {
            if (MenuViews.Instance is { Hosting: true, Off: false })
            {
                TipViews.HostAsked(config, tip);
            }
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                ModLog.Error("Scenes: noting the host's campaign tip for friends failed (they go without it)", ex);
            }
        }
    }

    /// <summary>A tutorial signal on a following friend's game: skipped (the host's screens never move this player's own tutorial on).</summary>
    private static bool FirePrefix(TutorialSignal signal)
    {
        try
        {
            if (!Following)
            {
                return true;
            }

            if (SkippedSignals++ == 0)
            {
                ModLog.Info($"Scenes: skipped the game's tutorial signal {signal} on this friend's game (its own tutorial doesn't follow the host's screens); further ones are only counted.");
            }

            return false;
        }
        catch (Exception)
        {
            return true; // not knowing, the game's own call goes through
        }
    }
}
#endif
