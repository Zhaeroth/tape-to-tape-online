#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppData;
using Il2CppRogue;
using Il2CppSTS.Map;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;
using T2TOnline.Plugin.Online;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// A match node's preview (<c>MatchPreviewMenu</c>): read on the host, shown
/// with the host's data on a friend's PC.
/// <list type="bullet">
/// <item>Host: <c>ShowMenu</c> and <c>BackToMap</c> say when the host's preview opens and closes without a match; the
/// open preview is read the next frame: the node, the opponent's five playing skaters and goalie after the game's
/// substitutes, and the possible rewards it rolled (<c>RefreshUnknownRewards</c>), by asset id. This build never calls
/// <c>RefreshUnknownRewards</c> (no call to it in GameAssembly), so the three reward slots keep their own
/// placeholder art on every PC and the rewards go out empty; the mirror stays for a build that calls it again.</item>
/// <item>Friend: the same preview opens as the game's own <c>MatchBlueprint.ShowPreviewMenu</c> opens it (<c>ShowMenu</c>,
/// then the panel's intro, <c>ShowPreviewMenu</c>), while <c>AssignSubstitute</c> and
/// <c>RefreshUnknownRewards</c>, which roll, are replaced: the substitutes come from the game's own
/// <c>ReplaceForwardWithSubstitute</c> with the host's picks, and the reward icons from the host's assets. A Challenge
/// node's opponent, rolled per map load (<c>ChallengeMapNode.Awake</c>), takes the host's picks first. Outside that,
/// both rolls are refused on a friend's PC.</item>
/// </list>
/// </summary>
internal static class PreviewMirror
{
    private static readonly LineBudget Refusals = new("Scenes: preview rolls refused", 10);
    private static PreviewViewDetails? _applying;
    private static IntPtr _applyingNode;

    /// <summary>Host: the game opened a preview (read and published on the next frame).</summary>
    public static event Action<MatchPreviewMenu>? Opened;

    /// <summary>Host: the game closed its preview back to the map (no match).</summary>
    public static event Action? Closed;

    /// <summary>Host: the game started the match from its preview (its zoom into the node starts now).</summary>
    public static event Action<MatchPreviewMenu>? Started;

    /// <summary>Host: the game started the match from <paramref name="menu"/>; tells <see cref="Started"/>'s handlers. Never throws.</summary>
    public static void HostStarted(MatchPreviewMenu menu)
    {
        try
        {
            Started?.Invoke(menu);
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Scenes: telling friends the host started its match failed ({ModLog.SafeErrorText(ex)}); their zoom plays as their match loads.");
        }
    }

    /// <summary>Patches the preview. Adds a line to <paramref name="failed"/> per patch that couldn't go in.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ScenePatches.Postfix(harmony, failed, typeof(MatchPreviewMenu), nameof(MatchPreviewMenu.ShowMenu), typeof(PreviewMirror), nameof(ShowMenuPostfix));
        ScenePatches.Postfix(harmony, failed, typeof(MatchPreviewMenu), nameof(MatchPreviewMenu.BackToMap), typeof(PreviewMirror), nameof(BackToMapPostfix));
        ScenePatches.Prefix(harmony, failed, typeof(MatchMapNode), nameof(MatchMapNode.AssignSubstitute), typeof(PreviewMirror), nameof(AssignSubstitutePrefix));
        ScenePatches.Prefix(harmony, failed, typeof(MatchPreviewMenu), nameof(MatchPreviewMenu.RefreshUnknownRewards), typeof(PreviewMirror), nameof(RefreshUnknownRewardsPrefix));
    }

    /// <summary>The game's preview menu in the world map, or null.</summary>
    public static MatchPreviewMenu? Menu => UnityEngine.Object.FindObjectOfType<MatchPreviewMenu>();

    /// <summary>Host: the open preview as the host's game shows it, on the map <paramref name="place"/>. False, with why, when it can't be read.</summary>
    public static bool TryCapture(MatchPreviewMenu menu, MapPlace place, out PreviewViewDetails? preview, out string problem)
    {
        preview = null;
        MatchMapNode? node = menu.currentMapNode?.TryCast<MatchMapNode>();
        Il2CppSTS.Map.Point? at = node?.Node?.point;
        TeamData? team = node?.opponent;
        if (node == null || at == null || team == null)
        {
            problem = "the preview has no match node or opponent";
            return false;
        }

        var opponent = new List<string>(PreviewViewDetails.Slots);
        for (int i = 0; i < PreviewViewDetails.Slots; i++)
        {
            opponent.Add(IdOf(team.GetForward(i)));
        }

        preview = new PreviewViewDetails(
            new MapViewDetails(place.RunId, place.Act, place.Path, null, null),
            new MapPoint(at.x, at.y),
            (int)node.NodeType,
            opponent,
            team.goalie != null ? team.goalie.OriginalId ?? string.Empty : string.Empty,
            RewardShown(menu.m_UnknownReward1, Il2CppLists.Items(menu._relicRepository?.GetAllRelics()), r => r.sprite, r => r.id),
            RewardShown(menu.m_UnknownReward2, Il2CppLists.Items(menu._talentRepository?.GetAllTalents()), t => t.icon, t => t.id),
            RewardShown(menu.m_UnknownReward3, Il2CppLists.Items(menu._abilityRepository?.GetAllAbilities()), a => a.icon, a => a.id));
        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// Friend: opens the host's <paramref name="host"/> preview of <paramref name="node"/> through the game's own
    /// <c>ShowMenu</c> and <c>ShowPreviewMenu</c>, with the host's opponent and rewards instead of rolls. Returns what came
    /// out differently from the host's (empty: the same preview).
    /// </summary>
    public static List<string> Open(MatchPreviewMenu menu, MatchMapNode node, PreviewViewDetails host)
    {
        var differences = new List<string>();
        ChallengeMapNode? challenge = node.TryCast<ChallengeMapNode>();
        if (challenge != null)
        {
            TakeChallengePicks(challenge, host, differences);
        }

        _applying = host;
        _applyingNode = node.Pointer;
        try
        {
            FriendUi.Apply(() =>
            {
                // As the game's MatchBlueprint.ShowPreviewMenu opens every preview: ShowMenu (the data and the camera's move
                // to the node), then the panel itself, whose animator's intro only ShowPreviewMenu starts (inlined there,
                // verified in the game's code). ShowMenu alone leaves the panel hidden.
                menu.ShowMenu(node);
                menu.ShowPreviewMenu();
            });
        }
        finally
        {
            _applying = null;
            _applyingNode = IntPtr.Zero;
        }

        TeamData? team = node.opponent;
        for (int i = 0; i < PreviewViewDetails.Slots && team != null; i++)
        {
            string have = IdOf(team.GetForward(i));
            if (have != host.Opponent[i])
            {
                differences.Add($"slot {i}: {Show(have)} instead of {ShowHost(host.Opponent[i])}");
            }
        }

        string goalie = team?.goalie != null ? team.goalie.OriginalId ?? string.Empty : string.Empty;
        if (goalie != host.Goalie)
        {
            differences.Add($"goalie {Show(goalie)} instead of {ShowHost(host.Goalie)}");
        }

        return differences;
    }

    /// <summary>A Challenge node's opponent as the host's game rolled it on its map load: the same skaters and goalie, copied from the node's pools as the game does.</summary>
    private static void TakeChallengePicks(ChallengeMapNode node, PreviewViewDetails host, List<string> differences)
    {
        TeamData? team = node.opponent;
        if (team == null)
        {
            differences.Add("the Challenge node has no opponent yet");
            return;
        }

        for (int i = 0; i < PreviewViewDetails.Slots; i++)
        {
            string want = host.Opponent[i];
            if (want.Length == 0 || IdOf(team.GetForward(i)) == want)
            {
                continue;
            }

            ForwardData? source = FindForward(node.challengeElligiblePlayers, want);
            if (source == null)
            {
                differences.Add($"the Challenge pool has no skater {ShowHost(want)}");
                continue;
            }

            // As ChallengeMapNode.Awake: a copy of the pool's skater, in slots 0-3 by SetForward and the right defence.
            ForwardData copy = UnityEngine.Object.Instantiate(source).Cast<ForwardData>();
            if (i == PreviewViewDetails.Slots - 1)
            {
                team.rightDefensemen = copy;
            }
            else
            {
                team.SetForward(i, copy);
            }
        }

        if (host.Goalie.Length > 0 && (team.goalie == null || team.goalie.OriginalId != host.Goalie))
        {
            Il2CppSystem.Collections.Generic.List<GoaltenderData>? goalies = node.challengeElligibleGoalers;
            GoaltenderData? source = null;
            for (int i = 0; goalies != null && i < goalies.Count && source == null; i++)
            {
                GoaltenderData? goalie = goalies[i];
                if (goalie != null && (goalie.OriginalId == host.Goalie || goalie.id == host.Goalie))
                {
                    source = goalie;
                }
            }

            if (source == null)
            {
                differences.Add($"the Challenge pool has no goalie {ShowHost(host.Goalie)}");
            }
            else
            {
                team.goalie = UnityEngine.Object.Instantiate(source).Cast<GoaltenderData>();
            }
        }
    }

    /// <summary>Host: a preview opened.</summary>
    private static void ShowMenuPostfix(MatchPreviewMenu __instance)
    {
        try
        {
            if (CampaignRelay.Current?.Hosting == true)
            {
                Opened?.Invoke(__instance);
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: noting the host's preview failed", ex);
        }
    }

    /// <summary>Host: the preview closed back to the map.</summary>
    private static void BackToMapPostfix()
    {
        try
        {
            if (CampaignRelay.Current?.Hosting == true)
            {
                Closed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: noting the host's preview close failed", ex);
        }
    }

    /// <summary>Friend: the opponent's substitutes are the host's, through the game's own replacement; never rolled here.</summary>
    private static bool AssignSubstitutePrefix(MatchMapNode __instance)
    {
        if (CampaignRelay.Current?.Following != true)
        {
            return true;
        }

        try
        {
            if (_applying != null && __instance.Pointer == _applyingNode)
            {
                Substitute(__instance, _applying);
            }
            else
            {
                Refused("MatchMapNode.AssignSubstitute");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: putting the host's substitutes in failed (the preview may show other skaters)", ex);
        }

        return false;
    }

    /// <summary>Friend: the preview's possible rewards are the host's; never rolled here.</summary>
    private static bool RefreshUnknownRewardsPrefix(MatchPreviewMenu __instance)
    {
        if (CampaignRelay.Current?.Following != true)
        {
            return true;
        }

        try
        {
            if (_applying != null)
            {
                ShowRewards(__instance, _applying);
            }
            else
            {
                Refused("MatchPreviewMenu.RefreshUnknownRewards");
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Scenes: showing the host's possible rewards failed", ex);
        }

        return false;
    }

    /// <summary>Each playing slot that isn't the host's skater gets the host's, from the node's substitutes, the way the game substitutes.</summary>
    private static void Substitute(MatchMapNode node, PreviewViewDetails host)
    {
        TeamData? team = node.opponent;
        if (team == null)
        {
            return;
        }

        Il2CppSystem.Collections.Generic.List<ForwardData>? substitutes = null;
        for (int i = 0; i < PreviewViewDetails.Slots; i++)
        {
            string want = host.Opponent[i];
            ForwardData? have = team.GetForward(i);
            if (want.Length == 0 || have == null || IdOf(have) == want)
            {
                continue;
            }

            substitutes ??= node.GetSubstitutes();
            ForwardData? template = FindForward(substitutes, want);
            if (template != null)
            {
                node.ReplaceForwardWithSubstitute(have, template);
            }
        }
    }

    /// <summary>The three "?" rewards as <c>RefreshUnknownRewards</c> lays them out, with the host's assets.</summary>
    private static void ShowRewards(MatchPreviewMenu menu, PreviewViewDetails host)
    {
        // The game titles the relic slot by the node (boss or not) and both the talent and the ability slot with the
        // talent text (it reads m_Talent for both).
        string relicTitle = (host.NodeType == 2 ? menu.m_BossRelic : menu.m_Relic).ToString();
        string talentTitle = menu.m_Talent.ToString();
        Relic? relic = host.RewardRelic.Length > 0 ? menu._relicRepository?.GetRelic(host.RewardRelic, false) : null;
        Talent? talent = host.RewardTalent.Length > 0 ? menu._talentRepository?.GetTalent(host.RewardTalent, false) : null;
        Ability? ability = host.RewardAbility.Length > 0 ? menu._abilityRepository?.GetAbility(host.RewardAbility, false) : null;
        bool relicShown = ShowReward(menu.m_UnknownReward1, relicTitle, relic?.sprite, host.RewardRelic);
        bool talentShown = ShowReward(menu.m_UnknownReward2, talentTitle, talent?.icon, host.RewardTalent);
        bool abilityShown = ShowReward(menu.m_UnknownReward3, talentTitle, ability?.icon, host.RewardAbility);
        menu.m_UnknownRewardSeparator1?.SetActive(relicShown && (talentShown || abilityShown));
        menu.m_UnknownRewardSeparator2?.SetActive(talentShown && abilityShown);
    }

    private static bool ShowReward(UnknownMatchRewardUI? slot, string title, Sprite? icon, string id)
    {
        if (slot == null)
        {
            return false;
        }

        if (id.Length > 0 && icon == null)
        {
            ModLog.Warn($"Scenes: the host's possible reward {HostText.KeyForLog(id)} isn't in this game; its slot stays empty.");
        }

        if (icon != null)
        {
            slot.Refresh(title, icon);
        }

        slot.gameObject.SetActive(icon != null);
        return icon != null;
    }

    /// <summary>Host: the asset behind a shown reward icon (the reward's own sprite), by id; "" when none is shown.</summary>
    private static string RewardShown<T>(UnknownMatchRewardUI? slot, IEnumerable<T> assets, Func<T, Sprite?> spriteOf, Func<T, string?> idOf)
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
    {
        Sprite? shown = slot != null && slot.gameObject.activeSelf ? slot.m_Icon?.overrideSprite : null;
        if (shown == null)
        {
            return string.Empty;
        }

        foreach (T asset in assets)
        {
            if (asset != null && spriteOf(asset) is { } sprite && sprite.Pointer == shown.Pointer)
            {
                return idOf(asset) ?? string.Empty;
            }
        }

        // The slot's own placeholder art: the game never calls RefreshUnknownRewards (no call to it anywhere in
        // GameAssembly, Nov 6 build), so the slots show the same art on every PC, and a friend's are left as they are.
        ModLog.Trace($"Scenes: the preview's reward slot shows its own art ({shown.name}), not a reward.");
        return string.Empty;
    }

    private static ForwardData? FindForward(Il2CppSystem.Collections.Generic.List<ForwardData>? pool, string id)
    {
        for (int i = 0; pool != null && i < pool.Count; i++)
        {
            ForwardData? forward = pool[i];
            if (forward != null && (forward.OriginalId == id || forward.id == id))
            {
                return forward;
            }
        }

        return null;
    }

    private static string IdOf(ForwardData? forward) => forward != null ? forward.OriginalId ?? string.Empty : string.Empty;

    private static string Show(string id) => id.Length == 0 ? "nobody" : id;

    /// <summary>
    /// An id from the host's view for this PC's log: the id when it's shaped like one (every asset id of the game's is), else
    /// its length: a modified host chooses these, so to this PC's log they are player-written text (<see cref="HostText"/>).
    /// </summary>
    internal static string ShowHost(string id) => id.Length == 0 ? "nobody" : HostText.KeyForLog(id);

    private static void Refused(string what)
    {
        if (Refusals.TrySpend())
        {
            ModLog.Info($"Scenes: refused {what} on this friend's game (it rolls; the host's preview is shown instead).");
        }
    }
}
#endif
