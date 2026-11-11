#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppRogue.NPC.GeneralManagerNPC;
using Il2CppState;
using Il2CppSTS.Map;
using Il2CppSTS.NodeRewards;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Scenes;

/// <summary>
/// The General Manager's rerolled map node rewards on a friend's map at once (v1). In a General Manager squad's run, each
/// page of the General Manager's event rerolls the rewards of the map's Fan, Chaos and skater-training nodes in place, at
/// random (<c>GeneralManagerEvent.RerollSkaterNodeRewards</c>, from <c>NextChoices</c>: <c>GenerateReward</c>, the node's
/// <c>nodeRewardID</c>, <c>SetupTooltip</c>).
/// <list type="bullet">
/// <item>Host: the rewards read before and after the game's reroll (a prefix and a postfix); when any changed, every one of
/// those nodes' (node, class, id) goes as the General Manager event menu's <see cref="MenuAttachmentKind.NodeRewards"/>
/// attachment (<see cref="NodeRewardsData"/>).</item>
/// <item>Friend: each reward that differs is built with the game's own <c>SetRewardFromID</c> (the map load's own path),
/// read back, written to the node's <c>nodeRewardID</c>, and the node's tooltip made again (<c>SetupTooltip</c>). One this
/// PC's run can't build yet (a skater the General Manager just signed: it's in the host's run from the event's exit) waits
/// for the host's next run here (<see cref="RunSync.HostRunLoaded"/>); one that still can't, or any that fails, leaves the
/// map marked stale (<see cref="WorldMap.MarkRewardsStale"/>: it loads again at the host's next move, as before). When none
/// failed, the mark the General Manager's open set is taken off: the map moves in place.</item>
/// </list>
/// Game thread only; the callers guard every entry.
/// </summary>
internal static class NodeRewardsMirror
{
    private static readonly Dictionary<MapPoint, string> Before = new();
    private static readonly List<NodeRewardEntry> Pending = new();
    private static int _reroll;
    private static bool _listening;

    /// <summary>Host: the game is about to reroll the map's node rewards: what they are now.</summary>
    public static void HostBefore()
    {
        Before.Clear();
        foreach (NodeRewardEntry entry in Read())
        {
            Before[entry.Point] = entry.Id;
        }
    }

    /// <summary>
    /// Host: the game rerolled the map's node rewards for <paramref name="gm"/>: when any changed, they go to friends as its
    /// event menu's attachment (made once the menu is on the board).
    /// </summary>
    public static void HostAfter(GeneralManagerEvent gm, MenuViews views)
    {
        List<NodeRewardEntry> now = Read();
        int changed = now.Count(e => !Before.TryGetValue(e.Point, out string? was) || was != e.Id);
        Before.Clear();
        if (changed == 0)
        {
            return; // not a General Manager squad's run, or no node to reroll: the game changed nothing
        }

        if (now.Count > NodeRewardsData.MaxEntries)
        {
            ModLog.Warn($"Menus: the host's General Manager rerolled {now.Count} node rewards, over the {NodeRewardsData.MaxEntries} that go to friends; the rest reach them with the host's next run.");
            now = now.Take(NodeRewardsData.MaxEntries).ToList();
        }

        var data = new NodeRewardsData { Reroll = Math.Clamp(++_reroll, 1, ushort.MaxValue), Entries = now };
        string? problem = data.Problem();
        EventChoiceMenu? menu = MenuOf(gm, views);
        if (problem == null && menu == null)
        {
            problem = "its event menu wasn't found";
        }

        if (problem != null || !views.Host.Attach(menu!, MenuAttachmentKind.NodeRewards, s => MenuAttachment.NodeRewards(s, data), data.ToString(), views.HasFriends, out problem))
        {
            ModLog.Warn($"Menus: the host's General Manager rerolled {changed} node reward(s), which can't go to friends ({problem}); their maps load again at the host's next move.");
            return;
        }

        ModLog.Info($"Menus: the host's General Manager rerolled {changed} node reward(s) ({data}); they go to friends.");
    }

    /// <summary>Hosting stopped.</summary>
    public static void ClearHost()
    {
        Before.Clear();
        _reroll = 0;
    }

    /// <summary>
    /// Friend: the host's rerolled rewards came: each that differs here is built
    /// at once; one this PC's run can't build yet waits for the host's next run.
    /// </summary>
    public static void Arrived(NodeRewardsData rewards)
    {
        Pending.Clear();
        if (!WorldMap.MapLoaded)
        {
            WorldMap.MarkRewardsStale("the host's General Manager rerolled the map's node rewards while this PC's map wasn't up");
            ModLog.Info($"Scenes: {LogNames.HostTag()}'s General Manager rerolled {rewards.Entries.Count} node reward(s) ({rewards}); this PC's map isn't up, so it loads them with {LogNames.HostTag()}'s next run.");
            return;
        }

        int applied = 0;
        int same = 0;
        var failed = new List<string>();
        foreach (NodeRewardEntry entry in rewards.Entries)
        {
            switch (Apply(entry, out string why))
            {
                case Outcome.Applied:
                    applied++;
                    break;
                case Outcome.Same:
                    same++;
                    break;
                case Outcome.Pending:
                    Pending.Add(entry);
                    break;
                default:
                    failed.Add(why);
                    break;
            }
        }

        if (failed.Count == 0)
        {
            // The mark the General Manager's open set (EventViews.HostEventOpened): this map has the host's rewards (or will,
            // with the host's next run, before it moves), so it moves in place.
            _ = WorldMap.TakeStaleRewards(out _);
        }
        else
        {
            WorldMap.MarkRewardsStale("the host's General Manager rerolled node rewards this PC's map couldn't take in place");
        }

        if (Pending.Count > 0 && !_listening)
        {
            _listening = true;
            RunSync.HostRunLoaded += HostRunLoaded;
        }

        string failures = failed.Count == 0 ? string.Empty : $"; {failed.Count} couldn't be ({SafeText.Clean(string.Join("; ", failed.Take(3)), 240)}): the map loads again at {LogNames.HostTag()}'s next move";
        ModLog.Info($"Scenes: {LogNames.HostTag()}'s General Manager rerolled node rewards ({rewards}); this map shows {applied} new one(s) now, {same} already the same, " +
            $"{Pending.Count} waiting for {LogNames.HostTag()}'s next run{failures}.");
    }

    /// <summary>Friend: following started or stopped; nothing waits.</summary>
    public static void ClearFriend()
    {
        Pending.Clear();
        if (_listening)
        {
            _listening = false;
            RunSync.HostRunLoaded -= HostRunLoaded;
        }
    }

    /// <summary>Friend: the host's next run is in: the rewards that waited are built now; any that still can't be leave the map marked stale.</summary>
    private static void HostRunLoaded(uint checkpoint)
    {
        if (Pending.Count == 0)
        {
            ClearFriend();
            return;
        }

        var left = new List<string>();
        int applied = 0;
        foreach (NodeRewardEntry entry in Pending)
        {
            if (Apply(entry, out string why) is Outcome.Applied or Outcome.Same)
            {
                applied++;
            }
            else
            {
                left.Add($"{entry.Point}: {why}");
            }
        }

        ClearFriend();
        if (left.Count > 0)
        {
            WorldMap.MarkRewardsStale("node rewards the host's General Manager rerolled couldn't be built from the host's run in place");
            ModLog.Warn($"Scenes: {applied} of {LogNames.HostTag()}'s rerolled node reward(s) went in with {LogNames.HostTag()}'s run {checkpoint}; {left.Count} still couldn't " +
                $"({SafeText.Clean(string.Join("; ", left.Take(3)), 240)}); the map loads again at {LogNames.HostTag()}'s next move.");
        }
        else
        {
            ModLog.Info($"Scenes: the {applied} rerolled node reward(s) that waited went in with {LogNames.HostTag()}'s run {checkpoint}.");
        }
    }

    private enum Outcome
    {
        Applied,
        Same,
        Pending,
        Failed,
    }

    /// <summary>Builds one host reward on this PC's map node, the way a map load does. Never throws (a failure is the outcome, with why).</summary>
    private static Outcome Apply(NodeRewardEntry entry, out string why)
    {
        why = string.Empty;
        try
        {
            MapNode? node = WorldMap.NodeAt(entry.Point);
            INodeReward? reward = node?.nodeReward;
            if (node == null || reward == null)
            {
                why = $"this map has no node reward at {entry.Point}";
                return Outcome.Failed;
            }

            NodeRewardClass here = Classify(reward);
            if (here != entry.Class)
            {
                why = $"the node at {entry.Point} has a {here} reward here, a {entry.Class} one on the host's map";
                return Outcome.Failed;
            }

            if (reward.RewardID == entry.Id)
            {
                return Outcome.Same;
            }

            CampaignState? state = WorldMap.State ?? MenuGame.Campaign();
            if (state == null)
            {
                why = "this PC has no campaign state";
                return Outcome.Failed;
            }

            reward.SetRewardFromID(state, entry.Id);
            if (reward.RewardID != entry.Id)
            {
                why = $"{HostText.KeyForLog(entry.Id)} isn't in this PC's run yet";
                return Outcome.Pending;
            }

            if (node.Node != null)
            {
                node.Node.nodeRewardID = entry.Id;
            }

            node.SetupTooltip();
            return Outcome.Applied;
        }
        catch (Exception ex)
        {
            why = $"{ModLog.SafeErrorText(ex)}";
            return Outcome.Failed;
        }
    }

    /// <summary>The reward classes the reroll changes (a skater training's only), or none.</summary>
    private static NodeRewardClass Classify(INodeReward reward) =>
        reward.TryCast<FanNodeReward>() != null ? NodeRewardClass.Fan
        : reward.TryCast<ChaosNodeReward>() != null ? NodeRewardClass.Chaos
        : reward.TryCast<TrainingReward>() is { RewardType: TrainingRewardType.Skater } ? NodeRewardClass.SkaterTraining
        : NodeRewardClass.None;

    /// <summary>Host: every node of the loaded map whose reward the reroll changes, with its reward's id (nodes off the network's map range left out).</summary>
    private static List<NodeRewardEntry> Read()
    {
        var entries = new List<NodeRewardEntry>();
        Il2CppSystem.Collections.Generic.List<MapNode>? nodes = MapObject.Current?.MapNodes;
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            MapNode? node = nodes[i];
            Point? at = node?.Node?.point;
            INodeReward? reward = node?.nodeReward;
            if (at == null || reward == null || at.x is < 0 or > MapPoint.MaxCoordinate || at.y is < 0 or > MapPoint.MaxCoordinate)
            {
                continue;
            }

            NodeRewardClass kind = Classify(reward);
            string? id = kind == NodeRewardClass.None ? null : reward.RewardID;
            if (!string.IsNullOrEmpty(id))
            {
                entries.Add(new NodeRewardEntry(new MapPoint(at.x, at.y), kind, id));
            }
        }

        return entries;
    }

    /// <summary>
    /// Host: the event menu showing <paramref name="gm"/>: among the host's tracked event menus, else among every loaded one
    /// (the first reroll runs inside the menu's own <c>Show</c>, before the menu is tracked). Null when none.
    /// </summary>
    private static EventChoiceMenu? MenuOf(GeneralManagerEvent gm, MenuViews views)
    {
        foreach ((EventChoiceMenu menu, uint _) in views.Host.EventMenus())
        {
            if (menu.SourceNode != null && menu.SourceNode.Pointer == gm.Pointer)
            {
                return menu;
            }
        }

        foreach (UnityEngine.Object any in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<EventChoiceMenu>()))
        {
            EventChoiceMenu? menu = any?.TryCast<EventChoiceMenu>();
            if (menu != null && menu.SourceNode != null && menu.SourceNode.Pointer == gm.Pointer)
            {
                return menu;
            }
        }

        return null;
    }
}
#endif
