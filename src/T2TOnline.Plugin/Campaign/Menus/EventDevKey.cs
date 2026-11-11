#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppData;
using Il2CppInterop.Runtime;
using Il2CppRogue.NPC.BlademasterNPC;
using Il2CppRogue.NPC.ChaosEvents;
using Il2CppRogue.NPC.ConditioningNPC;
using Il2CppRogue.NPC.FanNumber1NPC;
using Il2CppSTS.Map;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The developer's event key (Ctrl+F10, only with the DevTools setting; D39): opens the next kind of event in turn on this
/// PC's world map (the Coach, the three trainings, Chaos, the Blademaster, the General Manager, Fan Number 1, then the
/// Nurse, whose procedure screen friends see for real when they can), so each kind can be tried on demand: events come up at random. Never while this PC
/// follows a host (the events are the host's). Only on the world map with the cursor on the map (nothing open over it).
/// <para>
/// The menu opens the game's own way (<c>NodeBlueprint.ShowEventChoiceMenu</c>) for the event asset this game has, with no
/// map node: the event rolls and its pick applies to the run as a real visit's would, but its exit doesn't move the team on
/// the map or save the run as entering a node does (the run's next save carries the pick; friends see it with the next
/// checkpoint). Before it opens, fields
/// the map node would set on a real visit are set for a test: a skater training's skater (the team's first forward when it
/// has none in this run), and a Chaos or Fan Number 1 skater, or a Blademaster's locked ability, left from an earlier visit are
/// cleared so the event picks its own. A General Manager event on the General Manager's squad rerolls the map's skater
/// rewards, as a real visit does.
/// </para>
/// </summary>
internal static class EventDevKey
{
    private static readonly (string Class, NodeType Type)[] Kinds =
    {
        ("CoachEvent", NodeType.Coach),
        ("RegularPlayerTraining", NodeType.PlayerTraining),
        ("StatsTraining", NodeType.StatTraining),
        ("TeamTraining", NodeType.TeamTraining),
        ("ChaosEvent3Choice", NodeType.Chaos),
        ("BlademasterEvent", NodeType.Blademaster),
        ("GeneralManagerEvent", NodeType.GeneralManager),
        ("FanNumber1Event", NodeType.FanNumber1),
        ("NurseEvent", NodeType.Nurse),
    };

    private static int _next;

    /// <summary>Ctrl+F10: opens the next kind of event on this world map, or says why not. Never throws for a state it can't use.</summary>
    public static void Open()
    {
        string blocker = Blocker(out TeamData? team);
        if (blocker.Length > 0 || team == null)
        {
            ModLog.Warn($"Event test (Ctrl+F10): {blocker}.");
            return;
        }

        (string eventClass, NodeType type) = Kinds[_next % Kinds.Length];
        string next = EventCapture.Words(Kinds[(_next + 1) % Kinds.Length].Class);
        _next++;
        EventNode? node = Find(eventClass);
        if (node == null)
        {
            ModLog.Warn($"Event test (Ctrl+F10): this game has no {EventCapture.Words(eventClass)} event loaded; press again for the next kind ({next}).");
            return;
        }

        string prepared = Prepare(node, team);
        NodeBlueprint.ShowEventChoiceMenu(null!, node, type);
        string friends = !EventCapture.RealClasses.ContainsKey(eventClass) ? "friends see it as text in this version"
            : EventCapture.TextClasses.ContainsKey(eventClass) ? "friends see the real event screen (its procedure screen too, else as text)"
            : "friends see the real event screen";
        ModLog.Info($"Event test (Ctrl+F10): opening a {EventCapture.Words(eventClass)} event (\"{SafeText.Clean(node.name, 48)}\") on this map{prepared}; {friends}. " +
            $"Its pick applies to this run as a real visit's would; the team doesn't move on the map. Next: {next}.");
    }

    /// <summary>Why the key can't open an event now ("" when it can), and the run's team.</summary>
    private static string Blocker(out TeamData? team)
    {
        team = null;
        if (CampaignRelay.Current?.Following == true)
        {
            return "not while following a host (the events are the host's)";
        }

        if (!WorldMap.MapLoaded)
        {
            return "only on the campaign's world map, once it has loaded";
        }

        if (WorldMap.PointOf(WorldMap.Navigation?.CurrentNavigable) == null)
        {
            return "only with the cursor on the map (close what's open over it first)";
        }

        team = WorldMap.State?.runData?.teamData;
        return team == null ? "the game has no run team" : string.Empty;
    }

    /// <summary>
    /// The event asset of class <paramref name="eventClass"/> to open: one a node on this map launches, else the first loaded
    /// one by name (the same one each time).
    /// </summary>
    private static EventNode? Find(string eventClass)
    {
        Il2CppList.List<MapNode>? nodes = MapObject.Current?.MapNodes;
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            foreach (EventNode node in Launched(nodes[i]))
            {
                if (MenuGame.TypeName(node) == eventClass)
                {
                    return node;
                }
            }
        }

        var loaded = new List<EventNode>();
        foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<EventNode>()))
        {
            if (found?.TryCast<EventNode>() is { } node && MenuGame.TypeName(node) == eventClass)
            {
                loaded.Add(node);
            }
        }

        return loaded.OrderBy(n => n.name, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>The events a map node launches.</summary>
    private static IEnumerable<EventNode> Launched(MapNode? node)
    {
        if (node == null)
        {
            yield break;
        }

        EventNode? single = node.TryCast<CoachMapNode>()?.coachEvent
            ?? (EventNode?)node.TryCast<ChaosMapNode>()?.chaos3ChoiceNode
            ?? (EventNode?)node.TryCast<BlademasterMapNode>()?.blademasterEvent
            ?? (EventNode?)node.TryCast<GeneralManagerMapNode>()?.gmEvent
            ?? (EventNode?)node.TryCast<FanNumber1MapNode>()?.fanEvent
            ?? node.TryCast<NurseMapNode>()?.nurseEvent;
        if (single != null)
        {
            yield return single;
        }

        Il2CppList.List<TrainingEvent>? trainings = node.TryCast<Il2CppSTS.Scripts.Components.MapNodes.ConditioningMapNode>()?.trainingEvents;
        for (int i = 0; trainings != null && i < trainings.Count; i++)
        {
            if (trainings[i] != null)
            {
                yield return trainings[i];
            }
        }
    }

    /// <summary>What a real visit's map node would have set, for a test (said in the log line: "" when nothing changed).</summary>
    private static string Prepare(EventNode node, TeamData team)
    {
        var skaters = new HashSet<string>(StringComparer.Ordinal);
        Il2CppList.List<SkaterData>? all = team.GetAllSkaters();
        for (int i = 0; all != null && i < all.Count; i++)
        {
            if (all[i]?.id is { Length: > 0 } id)
            {
                skaters.Add(id);
            }
        }

        if (node.TryCast<RegularPlayerTraining>() is { } training)
        {
            ForwardData? forward = training.ForwardTraining;
            if (forward == null || !skaters.Contains(forward.id ?? string.Empty))
            {
                ForwardData? first = Enumerable.Range(0, team.forwards?.Count ?? 0).Select(i => team.forwards![i]).FirstOrDefault(f => f != null);
                training.ForwardTraining = first!;
                return first == null ? string.Empty : $" (training skater {SafeText.Clean(first.id, 48)})";
            }

            return $" (training skater {SafeText.Clean(forward.id, 48)})";
        }

        if (node.TryCast<ChaosEvent3Choice>() is { } chaos && chaos.SkaterId != null)
        {
            chaos.SkaterId = null!;
            return " (its skater picked by the event)";
        }

        if (node.TryCast<BlademasterEvent>() is { } blademaster && blademaster.m_LockedAbilityReward != null)
        {
            blademaster.m_LockedAbilityReward = null!;
            return " (upgrades, not a locked ability)";
        }

        if (node.TryCast<FanNumber1Event>() is { } fan && fan.playerId != null && !skaters.Contains(fan.playerId))
        {
            fan.playerId = null!;
            return " (its skater picked by the event)";
        }

        return string.Empty;
    }
}
#endif
