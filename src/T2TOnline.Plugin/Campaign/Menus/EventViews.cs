#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppData;
using Il2CppI2.Loc;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppRogue;
using Il2CppRogue.FreeAgents;
using Il2CppRogue.NPC.ConditioningNPC;
using Il2CppRogue.NPC.FanNumber1NPC;
using Il2CppRogue.NPC.FanNumber1NPC.Events;
using Il2CppRogue.Powerups.Repository;
using Il2CppRogue.Relics.Repository;
using Il2CppRogue.Tutorial;
using Il2CppSkaters.Injuries;
using Il2CppSTS.Map;
using Il2CppSTS.NodeRewards;
using Il2CppSTS.Scripts.Components.MapNodes;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign.Scenes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of an event menu as a real view (D43): the game's own
/// <c>EventChoice</c> values built from the host's <see cref="EventChoiceSet"/> before the menu opens, then put into the
/// event in place of its own roll.
/// <list type="number">
/// <item>Build (<see cref="Build"/>): the event asset this game has (by class and name: the map node at the host's point,
/// else every loaded event), the run's skaters by id, relics, talents and abilities (the run's own, or fresh copies from
/// this game's lists at the host's level), Fan Number 1's preview talents (made as the event makes them, a size change
/// named in this PC's language), a General Manager's free agents (built from the run file's own model with the game's own
/// deserializer) and their leaving conditions, a Nurse's takeover (an action that does nothing: a pick never runs here, and
/// the host's procedure screen opens here for real from the host's roster rebuilt here, <see cref="NurseRosters"/>, or shows as
/// text, <see cref="NurseScreen"/>), the images by their sources (<see cref="EventImages"/>), the texts (made in this PC's
/// language where the host's recipe makes them, <see cref="EventTexts"/>), stat bonuses and
/// injury; each choice a new boxed
/// <c>EventChoice</c> put into a new IL2CPP <c>List&lt;EventChoice&gt;</c> with the interop's <c>List.Add</c> (which unboxes
/// it into the list), then read back field by field. If anything differs, the list is made again with the runtime's own
/// <c>IList.Add(object)</c> (the game's runtime unboxes and stores it) and read back again; if that differs too, or anything
/// can't be resolved, there's no real menu and the event shows as text with why.</item>
/// <item>Arm and open: the plan is armed for its event asset, and the menu opened with the game's own
/// <c>UIModule.ShowMenuAsync&lt;EventChoiceMenu&gt;</c>, with no map node (so even its own exit, refused here, could never
/// move the map).</item>
/// <item>Install (<see cref="TryInstall"/>, from the event's <c>Initialize</c> prefix): the armed event gets the host's
/// choices, description parameters and (a training) its stat, rarity, reward type and skater, and its own roll is
/// skipped. <see cref="AfterShow"/> (the menu's <c>Show</c> postfix, before its cards are made) checks they're in place,
/// puts them in place if they aren't, and sets the menu's count of choices made.</item>
/// </list>
/// Pages and rerolls are built the same way and written into the open menu's event before its own page or reroll step
/// runs (<see cref="Write"/>). Nothing here acts: the friend's choices have actions that do nothing, and the menu's own
/// picks, skips, rerolls and exits are refused (<see cref="MenuPatches"/>).
/// </summary>
internal static class EventViews
{
    // An armed plan older than this is stale (the menu never opened): it's dropped at the next look.
    private const double ArmedSeconds = 20;

    // The General Manager squad's id (the game's own string: GeneralManagerEvent.RerollSkaterNodeRewards checks it).
    private const string GeneralManagerSquad = "generalmanager";

    // Fan Number 1's size changes: their preview ids, and the terms the event makes their name and description from
    // (PrototypeRewardPoolSubEvent.TryCreateIncreaseSizeChoice / TryCreateReduceSizeChoice), and which way the size goes.
    private static readonly Dictionary<string, (string Name, string Description, bool Up)> SizeTerms = new(StringComparer.Ordinal)
    {
        ["fan_number_1_increase_size"] = ("v2ui/event_choice/fan_no_1/increase_size/name", "v2ui/event_choice/fan_no_1/increase_size/desc", true),
        ["fan_number_1_reduce_size"] = ("v2ui/event_choice/fan_no_1/reduce_size/name", "v2ui/event_choice/fan_no_1/reduce_size/desc", false),
    };

    private static EventPlan? _armed;
    private static double _armedAt;

    /// <summary>The event classes this version shows for real (the host's list; a friend checks its own).</summary>
    public static bool IsReal(string eventClass) => EventCapture.RealClasses.ContainsKey(eventClass);

    /// <summary>
    /// The host opened an event menu of <paramref name="eventClass"/> (shown here for real or as text). A General Manager in a
    /// General Manager squad's run rerolls the rewards of the map's Fan, Chaos and training nodes on the host's map (its
    /// <c>NextChoices</c> calls <c>RerollSkaterNodeRewards</c> when <c>CampaignUtil.IsPlayingSquad("generalmanager")</c>):
    /// this PC's map is marked stale, so the host's next move loads the map here (with the rerolled rewards from the host's
    /// run) instead of moving in place over the old ones.
    /// </summary>
    public static void HostEventOpened(string eventClass)
    {
        if (eventClass != "GeneralManagerEvent")
        {
            return;
        }

        string? squad = MenuGame.Campaign()?.runData?.squadId;
        if (squad == null)
        {
            // Its squad can't be told here. A reroll's rewards still reach the map (NodeRewardsMirror).
            ModLog.Info("Menus: the host opened a General Manager event, but this PC holds no copy of the host's run to tell its squad; in a General Manager squad's run this map takes its rerolled rewards only as they come.");
            return;
        }

        if (squad != GeneralManagerSquad)
        {
            return;
        }

        WorldMap.MarkRewardsStale("the host's General Manager rerolled the map's node rewards");
        ModLog.Info("Menus: the host's General Manager rerolls the map's node rewards (a General Manager squad's run); this PC's map takes them as they come (NodeRewardsMirror), else it loads again at the host's next move.");
    }

    /// <summary>
    /// The game's own choices for <paramref name="set"/>, ready to put into its event, or null with why. <paramref name="open"/>:
    /// for opening the menu (the request is made too); otherwise for a page or reroll of an open one.
    /// </summary>
    public static EventPlan? Build(EventChoiceSet set, bool open, out string problem)
    {
        if (!IsReal(set.EventClass))
        {
            problem = EventCapture.TextClasses.TryGetValue(set.EventClass, out string? why) ? why : $"a {HostText.KeyForLog(set.EventClass)} event is one this version doesn't show for real";
            return null;
        }

        if (!Enum.IsDefined(typeof(NodeType), set.NodeType))
        {
            problem = $"the host's event has a node type ({set.NodeType}) this game doesn't know";
            return null;
        }

        TeamData? team = MenuRebuild.Team(out problem);
        EventNode? node = team == null ? null : FindNode(set, out problem);
        if (team == null || node == null)
        {
            return null;
        }

        var builder = new Builder(team, node);
        var plan = new EventPlan(set, node);
        if (!builder.Training(set, plan, out problem) || !Builder.Terms(set.DescTerms, out Il2CppList.List<EventTermParam>? descTerms, out problem))
        {
            return null;
        }

        plan.DescTerms = descTerms;
        var built = new List<EventChoice>(set.Choices.Count);
        Il2CppList.List<EventChoice>? list;
        string how;
        try
        {
            for (int i = 0; i < set.Choices.Count; i++)
            {
                EventChoice? choice = builder.Choice(set.Choices[i], out problem);
                if (choice == null)
                {
                    problem = $"choice {i + 1}: {problem}";
                    Destroy(builder.Made, "they can't be shown here");
                    return null;
                }

                built.Add(choice);
            }

            list = Fill(built, builder.Expected, out how, out problem);
        }
        catch (Exception ex)
        {
            // The game's own code threw (its deserializer, a preview's Initialize, one of its lists): the event shows as text.
            problem = $"choice {built.Count + 1}: an error ({ModLog.SafeErrorText(ex)})";
            Destroy(builder.Made, "they can't be shown here");
            return null;
        }

        if (list == null)
        {
            Destroy(builder.Made, "they can't be shown here");
            return null;
        }

        plan.Made = builder.Made;
        plan.Choices = list;
        plan.How = how;
        if (open)
        {
            plan.Request = new EventChoiceMenu.Metadata
            {
                Node = node,
                MapNode = null!,
                NodeType = (NodeType)set.NodeType,
                RerollCount = set.RerollCount,
            };
        }

        string sprites = builder.Images.ToString() is { Length: > 0 } images ? $"; {images}" : string.Empty;
        string texts = builder.Texts.Made + builder.Texts.Failed.Count == 0 ? string.Empty
            : $"; {builder.Texts.Made} text(s) made here in this PC's language from the host's recipes ({builder.Texts.Failed.Count} kept as the host's)";
        string previews = builder.Previews == 0 ? string.Empty
            : $"; {builder.Previews} preview(s) made here{(builder.LocalPreviews > 0 ? $", {builder.LocalPreviews} named in this PC's language" : string.Empty)}";
        string agents = builder.Agents.Count == 0 ? string.Empty
            : $"; {builder.Agents.Count} free agent(s) built from the host's with the game's deserializer, read back field by field ({string.Join(", ", builder.Agents)})";
        string takeovers = builder.Takeovers == 0 ? string.Empty
            : $"; {builder.Takeovers} take the screen over on the host's (its procedure screen opens over this one for real when it can, else as text)";
        ModLog.Info($"Menus: EventChoice: built {set} for this PC's {EventCapture.Words(set.EventClass)} event \"{SafeText.Clean(node.name, 48)}\" ({builder.Found}): " +
            $"{set.Choices.Count} choice(s) written with {how} and read back field by field, every one as sent ({builder.Fresh} fresh object(s), {builder.Owned} of the run's own{previews}{agents}{takeovers}{texts}{sprites}).");
        problem = string.Empty;
        return plan;
    }

    /// <summary>Arms <paramref name="plan"/>: its event's next <c>Initialize</c> on this PC gets the host's choices instead of its roll.</summary>
    public static void Arm(EventPlan plan)
    {
        EventPlan? before = _armed;
        _armed = plan;
        _armedAt = FrameClock.Seconds;
        if (before != null && !ReferenceEquals(before, plan))
        {
            Release(before);
        }
    }

    /// <summary>
    /// Disarms <paramref name="plan"/> (or whatever is armed, for null): the view closed or gave up opening. A plan whose
    /// choices never went into an event can't go in any more: what it made is destroyed (<see cref="Release"/>).
    /// </summary>
    public static void Disarm(EventPlan? plan = null)
    {
        if (plan == null || ReferenceEquals(_armed, plan))
        {
            plan ??= _armed;
            _armed = null;
        }

        Release(plan);
    }

    /// <summary>
    /// A plan that won't be shown: the objects it made here (previews, free agents and their power-ups, fresh copies) are
    /// destroyed when its choices never went into an event (nothing of the game's holds them then). Once they went in, the
    /// event's own list holds them, as the host's game keeps its own (a closing card may still read one). Never throws.
    /// </summary>
    public static void Release(EventPlan? plan)
    {
        if (plan == null || plan.Written || ReferenceEquals(_armed, plan))
        {
            return;
        }

        // A Nurse roster rebuilt for it (v1) never went on the event: its talents go too.
        NurseRosters.Release(plan.Nurse, null);
        if (plan.Made.Count == 0)
        {
            return;
        }

        Destroy(plan.Made, "they weren't shown");
        plan.Made = Array.Empty<UnityEngine.Object>();
    }

    /// <summary>Destroys objects made here for choices that won't be shown (nothing of the game's holds them), and says so. Never throws.</summary>
    private static void Destroy(IReadOnlyList<UnityEngine.Object> made, string why)
    {
        int destroyed = 0;
        foreach (UnityEngine.Object value in made.ToList())
        {
            try
            {
                if (value != null)
                {
                    UnityEngine.Object.Destroy(value);
                    destroyed++;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Menus: EventChoice: an object made for the host's choices couldn't be destroyed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        if (destroyed > 0)
        {
            ModLog.Info($"Menus: EventChoice: destroyed the {destroyed} object(s) made here for the host's choices ({why}).");
        }
    }

    /// <summary>
    /// The event's <c>Initialize</c> on a following friend's PC: true when <paramref name="node"/> is the armed event and the
    /// host's choices went in (its own roll is skipped). False when it isn't armed (the caller still skips the roll while
    /// following: only the host's game rolls).
    /// </summary>
    public static bool TryInstall(EventNode node)
    {
        EventPlan? plan = Current(node);
        if (plan == null)
        {
            return false;
        }

        if (!Write(node, plan, out string problem))
        {
            plan.Failure = $"the host's choices couldn't be put into the event ({problem})";
            ModLog.Warn($"Menus: EventChoice: {plan.Failure}; it shows as text once it's up.");
            return true;
        }

        plan.Installed = true;
        return true;
    }

    /// <summary>
    /// The menu's <c>Show</c> has run its first part (the event's <c>Initialize</c>, before any card is made): for the armed
    /// event, checks the host's choices are the event's (and puts them in if its own roll ran after all), sets the menu's
    /// count of choices made, and disarms. A Coach while this PC's campaign tutorial runs fails here (its speech would wait
    /// for a press nothing local can give).
    /// </summary>
    public static void AfterShow(EventChoiceMenu menu)
    {
        EventNode? node = menu.SourceNode;
        EventPlan? plan = node == null ? null : Current(node);
        if (plan == null || node == null)
        {
            return;
        }

        _armed = null;
        plan.Menu = menu;
        if (plan.Failure.Length > 0)
        {
            return;
        }

        if (node.eventChoices?.Pointer != plan.Choices.Pointer)
        {
            // The event's own Initialize ran (a patch that didn't apply, or a class added by a game patch): its roll's
            // choices are replaced before any card is made.
            ModLog.Warn($"Menus: EventChoice: the {EventCapture.Words(plan.Set.EventClass)} event's own roll wasn't skipped on this PC " +
                $"({(plan.Installed ? "its choices changed after they went in" : "the host's choices never went in")}); putting them in now, before its cards are made.");
            if (!Write(node, plan, out string problem))
            {
                plan.Failure = $"the host's choices couldn't be put into the event ({problem})";
                return;
            }
        }

        menu.m_ChoicesMade = plan.Set.ChoicesMade;
        if (plan.Set.NodeType == (int)NodeType.Coach && menu.m_CampaignTutorial is { IsActive: true })
        {
            plan.Failure = "the campaign tutorial is running on this PC (its Coach speech would wait for a press)";
            return;
        }

        ModLog.Info($"Menus: EventChoice: the host's {plan.Set.Choices.Count} choice(s) are in this PC's event menu " +
            $"({(plan.Installed ? "the event's own roll skipped" : "put in after its roll")}; written with {plan.How}).");
    }

    /// <summary>
    /// Writes <paramref name="plan"/>'s choices, description parameters and training fields into <paramref name="node"/>,
    /// then reads them back. False, with why, when any reads back otherwise.
    /// </summary>
    public static bool Write(EventNode node, EventPlan plan, out string problem)
    {
        plan.Written = true; // from here the event holds what the plan made (Release leaves it)
        node.eventChoices = plan.Choices;
        node.descTermParams = plan.DescTerms!;

        // A Nurse's roster rebuilt from the host's (v1) goes on the event too, before its cards are made.
        NurseRosters.Install(node, plan.Nurse);
        if (plan.Training != null)
        {
            TrainingEvent training = plan.Training;
            training.StatsImproving = plan.Stat;
            training.rarity = plan.Rarity;
            training.RewardType = plan.Reward;
            training.ForwardTraining = plan.Forward!;
            if (training.StatsImproving != plan.Stat || training.rarity != plan.Rarity || training.RewardType != plan.Reward ||
                Pointer(training.ForwardTraining) != Pointer(plan.Forward))
            {
                problem = "the training's fields didn't read back as written";
                return false;
            }
        }

        problem = node.eventChoices?.Pointer != plan.Choices.Pointer ? "the event's choices didn't read back as written"
            : Pointer(node.descTermParams) != Pointer(plan.DescTerms) ? "the event's description parameters didn't read back as written"
            : string.Empty;
        return problem.Length == 0;
    }

    /// <summary>
    /// True while this PC's campaign tutorial runs (the game's injected <c>CampaignTutorial</c>): a Coach event then plays the
    /// tutorial's speech, which waits for a press. False when there's none.
    /// </summary>
    public static bool TutorialActive()
    {
        Il2CppSystem.Object? found = ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<CampaignTutorial>());
        return found?.TryCast<CampaignTutorial>() is { IsActive: true };
    }

    private static IntPtr Pointer(Il2CppObjectBase? value) => value?.Pointer ?? IntPtr.Zero;

    /// <summary>The armed plan when it's for <paramref name="node"/> and not stale, else null.</summary>
    private static EventPlan? Current(EventNode node)
    {
        if (_armed != null && FrameClock.Seconds - _armedAt > ArmedSeconds)
        {
            EventPlan stale = _armed;
            _armed = null;
            Release(stale);
        }

        return _armed != null && _armed.Node.Pointer == node.Pointer ? _armed : null;
    }

    /// <summary>
    /// The event asset the host's set is for, on this PC: the map node at the host's point (its event, or its training
    /// events), else every loaded event; by class and asset name, then id. Null, with why, when there's none or more than
    /// one.
    /// </summary>
    private static EventNode? FindNode(EventChoiceSet set, out string problem)
    {
        problem = string.Empty;
        var candidates = new List<EventNode>();
        if (set.Point is (int x, int y) && WorldMap.MapLoaded && WorldMap.NodeAt(new MapPoint(x, y)) is { } mapNode)
        {
            foreach (EventNode node in NodeEvents(mapNode))
            {
                if (Matches(node, set))
                {
                    candidates.Add(node);
                }
            }
        }

        if (candidates.Count == 0)
        {
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<EventNode>()))
            {
                EventNode? node = found?.TryCast<EventNode>();
                if (node != null && Matches(node, set) && !candidates.Any(c => c.Pointer == node.Pointer))
                {
                    candidates.Add(node);
                }
            }
        }

        if (candidates.Count > 1 && set.EventId != null)
        {
            candidates = candidates.Where(c => c.id == set.EventId).ToList();
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        problem = candidates.Count == 0
            ? $"this game has no {HostText.KeyForLog(set.EventClass)} event \"{HostText.KeyForLog(set.EventName)}\""
            : $"this game has {candidates.Count} {HostText.KeyForLog(set.EventClass)} events \"{HostText.KeyForLog(set.EventName)}\" and can't tell which";
        return null;
    }

    private static bool Matches(EventNode node, EventChoiceSet set) => MenuGame.TypeName(node) == set.EventClass && node.name == set.EventName;

    /// <summary>The events a map node launches (its event asset, or a conditioning node's training events).</summary>
    private static IEnumerable<EventNode> NodeEvents(MapNode mapNode)
    {
        EventNode? single = mapNode.TryCast<CoachMapNode>()?.coachEvent
            ?? (EventNode?)mapNode.TryCast<ChaosMapNode>()?.chaos3ChoiceNode
            ?? mapNode.TryCast<BlademasterMapNode>()?.blademasterEvent
            ?? mapNode.TryCast<FanNumber1MapNode>()?.fanEvent
            ?? mapNode.TryCast<GeneralManagerMapNode>()?.gmEvent
            ?? (EventNode?)mapNode.TryCast<NurseMapNode>()?.nurseEvent;
        if (single != null)
        {
            yield return single;
        }

        Il2CppList.List<TrainingEvent>? trainings = mapNode.TryCast<ConditioningMapNode>()?.trainingEvents;
        for (int i = 0; trainings != null && i < trainings.Count; i++)
        {
            if (trainings[i] != null)
            {
                yield return trainings[i];
            }
        }
    }

    /// <summary>
    /// The list the menu reads, filled with <paramref name="built"/>: with the interop's <c>List.Add</c> first, then the
    /// runtime's <c>IList.Add(object)</c>; each read back field by field. Null, with both reasons, when neither reads back.
    /// </summary>
    private static Il2CppList.List<EventChoice>? Fill(List<EventChoice> built, IReadOnlyList<EventChoiceData> expected, out string how, out string problem)
    {
        how = "List.Add";
        string first;
        try
        {
            var list = new Il2CppList.List<EventChoice>();
            foreach (EventChoice choice in built)
            {
                list.Add(choice);
            }

            first = Verify(list, built, expected);
            if (first.Length == 0)
            {
                problem = string.Empty;
                return list;
            }
        }
        catch (Exception ex)
        {
            first = $"{ModLog.SafeErrorText(ex)}";
        }

        ModLog.Warn($"Menus: EventChoice: the choices written with List.Add didn't read back as written ({first}); writing them with IList.Add instead.");
        how = "IList.Add";
        string second;
        try
        {
            var list = new Il2CppList.List<EventChoice>();
            Il2CppSystem.Collections.IList untyped = list.Cast<Il2CppSystem.Collections.IList>();
            foreach (EventChoice choice in built)
            {
                _ = untyped.Add(choice);
            }

            second = Verify(list, built, expected);
            if (second.Length == 0)
            {
                problem = string.Empty;
                return list;
            }
        }
        catch (Exception ex)
        {
            second = $"{ModLog.SafeErrorText(ex)}";
        }

        problem = $"the choices couldn't be written into the game's list (List.Add: {first}; IList.Add: {second})";
        return null;
    }

    /// <summary>
    /// Reads <paramref name="list"/> back: every field of every choice against <paramref name="expected"/> (the host's data,
    /// with any text made again in this PC's language) and the objects built for it (references by object, so a copy that
    /// lost one shows), and every term and stat bonus inside. "" when all match, else the first difference.
    /// </summary>
    private static string Verify(Il2CppList.List<EventChoice> list, List<EventChoice> built, IReadOnlyList<EventChoiceData> expected)
    {
        if (list.Count != built.Count)
        {
            return $"{list.Count} choice(s) in the list, {built.Count} written";
        }

        for (int i = 0; i < built.Count; i++)
        {
            EventChoice got = list[i];
            EventChoice want = built[i];
            EventChoiceData data = expected[i];
            string? field =
                got == null ? "the choice"
                : got.buttonName != data.ButtonName ? "buttonName"
                : got.translatedButtonName != data.TranslatedButtonName ? "translatedButtonName"
                : (got.onSelection != null) != data.HasOnSelection || Pointer(got.onSelection) != Pointer(want.onSelection) ? "onSelection"
                : (got.onTakeover != null) != data.HasOnTakeover || Pointer(got.onTakeover) != Pointer(want.onTakeover) ? "onTakeover"
                : got.interactable != data.Interactable ? "interactable"
                : (int)got.nodeType != data.NodeType ? "nodeType"
                : Pointer(got.choicesTermParams) != Pointer(want.choicesTermParams) || !SameTerms(got.choicesTermParams, data.Terms) ? "choicesTermParams"
                : (int)got.ChoiceType != data.ChoiceType ? "ChoiceType"
                : Pointer(got.Skater) != Pointer(want.Skater) || got.Skater?.id != (data.SkaterId ?? data.FreeAgent?.Id) ? "Skater"
                : Pointer(got.PowerUpOrRelic) != Pointer(want.PowerUpOrRelic) || (got.PowerUpOrRelic == null) != (data.Object1 == null) ? "PowerUpOrRelic"
                : Pointer(got.PowerUpOrRelic2) != Pointer(want.PowerUpOrRelic2) || (got.PowerUpOrRelic2 == null) != (data.Object2 == null) ? "PowerUpOrRelic2"
                : Pointer(got.CustomImages) != Pointer(want.CustomImages) || (got.CustomImages?.Length ?? -1) != (data.Images?.Count ?? -1) ? "CustomImages"
                : Pointer(got.CustomLocalizedTexts) != Pointer(want.CustomLocalizedTexts) || !SameTexts(got.CustomLocalizedTexts, data.Texts) ? "CustomLocalizedTexts"
                : Pointer(got.CustomStatBonuses) != Pointer(want.CustomStatBonuses) || !SameBonuses(got.CustomStatBonuses, data.StatBonuses) ? "CustomStatBonuses"
                : Pointer(got.CustomInjury) != Pointer(want.CustomInjury) || !SameInjury(got.CustomInjury, data.Injury) ? "CustomInjury"
                : Pointer(got.FreeAgentLeavingConditions) != Pointer(want.FreeAgentLeavingConditions) || !SameConditions(got.FreeAgentLeavingConditions, data.Conditions) ? "FreeAgentLeavingConditions"
                : got.SuppressRewardTracking != data.SuppressRewardTracking ? "SuppressRewardTracking"
                : null;
            if (field != null)
            {
                return $"choice {i + 1}'s {field} differs";
            }
        }

        return string.Empty;
    }

    private static bool SameTerms(Il2CppList.List<EventTermParam>? got, IReadOnlyList<EventTerm>? want)
    {
        if (got == null || want == null)
        {
            return got == null && want == null;
        }

        if (got.Count != want.Count)
        {
            return false;
        }

        for (int i = 0; i < want.Count; i++)
        {
            EventTermParam? term = got[i];
            if (term == null || term.name != want[i].Name || term.value != want[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameTexts(Il2CppStringArray? got, IReadOnlyList<string?>? want)
    {
        if (got == null || want == null)
        {
            return got == null && want == null;
        }

        if (got.Length != want.Count)
        {
            return false;
        }

        for (int i = 0; i < want.Count; i++)
        {
            if (got[i] != want[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameBonuses(CustomStatBonusesData? got, StatBonusSet? want)
    {
        if (got == null || want == null)
        {
            return got == null && want == null;
        }

        Il2CppList.List<StatBonus>? list = got.m_StatBonuses;
        if (got.AreAlreadyCounted != want.AreAlreadyCounted || list == null || list.Count != want.Bonuses.Count)
        {
            return false;
        }

        for (int i = 0; i < want.Bonuses.Count; i++)
        {
            StatBonus bonus = list[i];
            StatBonusData expected = want.Bonuses[i];
            if ((int)bonus.SkaterType != expected.SkaterType || (int)bonus.StatForward != expected.StatForward || (int)bonus.StatGoalie != expected.StatGoalie ||
                bonus.Bonus != expected.Bonus || bonus.IsConditional != expected.IsConditional || bonus.IsBlockableStatGain != expected.IsBlockableStatGain)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameInjury(Injury? got, InjuryData? want) =>
        got == null || want == null
            ? got == null && want == null
            : (int)got.InjuryType == want.Type && got.HealingTime == want.HealingTime && got.OriginalTime == want.OriginalTime && got.LastDisplayTime == want.LastDisplayTime;

    /// <summary>True when the game's leaving conditions read as <paramref name="want"/> (class by class, field by field, in order).</summary>
    private static bool SameConditions(Il2CppObjectBase? got, IReadOnlyList<FreeAgentCondition>? want)
    {
        if (got == null || want == null)
        {
            return got == null && want == null;
        }

        List<FreeAgentCondition>? read = EventCapture.Conditions(got, out _);
        return read != null && read.SequenceEqual(want);
    }

    /// <summary>Builds one set's game objects against this PC's run and lists.</summary>
    private sealed class Builder
    {
        private readonly TeamData _team;
        private readonly EventNode _node;
        private readonly Dictionary<string, SkaterData> _skaters = new(StringComparer.Ordinal);
        private readonly List<UnityEngine.Object> _made = new();
        private IRelicRepository? _relics;
        private ITalentRepository? _talents;
        private IAbilityRepository? _abilities;
        private Il2CppSystem.Action? _noOp;
        private Il2CppSystem.Action<EventChoiceMenu, EventChoiceItem>? _noOpTakeover;
        private SkaterModels.Builder? _agents;

        public Builder(TeamData team, EventNode node)
        {
            _team = team;
            _node = node;
            Il2CppList.List<SkaterData>? all = team.GetAllSkaters();
            for (int i = 0; all != null && i < all.Count; i++)
            {
                if (all[i]?.id is { Length: > 0 } id)
                {
                    _skaters.TryAdd(id, all[i]);
                }
            }

            // The images by their sources: an object's icon, the event's own sprites (a training's stat icons, Fan Number 1's
            // stat and size icons), else by name, texture and rect (EventImages).
            Images = new EventImages.Resolver(node);
            Found = WorldMap.MapLoaded ? "on this map" : "among this game's events";
        }

        /// <summary>Where the event asset was found, for the log.</summary>
        public string Found { get; }

        /// <summary>The choices' images found here, by their sources.</summary>
        public EventImages.Resolver Images { get; }

        /// <summary>The choices' texts made here from the host's recipes.</summary>
        public EventTextTally Texts { get; } = new();

        /// <summary>Objects made fresh from this game's lists.</summary>
        public int Fresh { get; private set; }

        /// <summary>Objects that are the run's own (this PC's copy of the host's run).</summary>
        public int Owned { get; private set; }

        /// <summary>Preview talents made here (Fan Number 1's size changes).</summary>
        public int Previews { get; private set; }

        /// <summary>Of those, the ones whose name and description were made again in this PC's language.</summary>
        public int LocalPreviews { get; private set; }

        /// <summary>The choices as they're expected to read back: the host's, with its texts made again in this PC's language.</summary>
        public List<EventChoiceData> Expected { get; } = new();

        /// <summary>The free agents built here (asset id and number, never a name), for the log.</summary>
        public List<string> Agents { get; } = new();

        /// <summary>
        /// The objects made here so far (previews, free agents and their power-ups, fresh copies), every one this PC's own:
        /// destroyed when the plan is dropped before its choices go into an event.
        /// </summary>
        public IReadOnlyList<UnityEngine.Object> Made => _made;

        /// <summary>Choices that take the screen over on the host's (a Nurse's procedures).</summary>
        public int Takeovers { get; private set; }

        /// <summary>A training's fields, checked against this game's values (none for another event).</summary>
        public bool Training(EventChoiceSet set, EventPlan plan, out string problem)
        {
            problem = string.Empty;
            TrainingEvent? training = _node.TryCast<TrainingEvent>();
            if ((training == null) != (set.Training == null))
            {
                problem = training == null ? "the host sent a training's fields for an event that isn't one" : "the host's training came without its fields";
                return false;
            }

            if (training == null || set.Training == null)
            {
                return true;
            }

            TrainingFields fields = set.Training;
            if (!Enum.IsDefined(typeof(BasicStats), fields.Stat) || !Enum.IsDefined(typeof(Rarity), fields.Rarity) || !Enum.IsDefined(typeof(TrainingRewardType), fields.RewardType))
            {
                problem = "the host's training has a stat, rarity or reward this game doesn't know";
                return false;
            }

            ForwardData? forward = null;
            if (fields.ForwardId != null && (forward = _team.GetForwardById(fields.ForwardId)) == null)
            {
                problem = $"the training's skater \"{HostText.KeyForLog(fields.ForwardId)}\" isn't in the host's run here";
                return false;
            }

            plan.Training = training;
            plan.Stat = (BasicStats)fields.Stat;
            plan.Rarity = (Rarity)fields.Rarity;
            plan.Reward = (TrainingRewardType)fields.RewardType;
            plan.Forward = forward;
            return true;
        }

        /// <summary>A term list as the game's (null for none).</summary>
        public static bool Terms(IReadOnlyList<EventTerm>? terms, out Il2CppList.List<EventTermParam>? list, out string problem)
        {
            problem = string.Empty;
            list = null;
            if (terms == null)
            {
                return true;
            }

            list = new Il2CppList.List<EventTermParam>();
            foreach (EventTerm term in terms)
            {
                list.Add(new EventTermParam { name = term.Name!, value = term.Value! });
            }

            if (!SameTerms(list, terms))
            {
                problem = "its term parameters didn't read back as written";
                list = null;
                return false;
            }

            return true;
        }

        /// <summary>One choice, as a new boxed <c>EventChoice</c> with every field set; or null with why.</summary>
        public EventChoice? Choice(EventChoiceData data, out string problem)
        {
            if (!Enum.IsDefined(typeof(EEventChoiceType), data.ChoiceType) || !Enum.IsDefined(typeof(NodeType), data.NodeType))
            {
                problem = $"its type ({data.ChoiceType}) or node type ({data.NodeType}) is one this game doesn't know";
                return null;
            }

            if (data.HasOnTakeover && MenuGame.TypeName(_node) != EventChoiceSet.TakeoverClass)
            {
                // The set's own check refuses it already; this event here isn't one that takes the screen over.
                problem = $"it takes the screen over, which only a {EventChoiceSet.TakeoverClass}'s choices do";
                return null;
            }

            // A free agent (a skater the host's event made) is built here with the game's own deserializer; a run skater is
            // this PC's copy of the host's.
            SkaterData? skater = null;
            ForwardData? agent = null;
            if (data.FreeAgent != null)
            {
                if ((agent = FreeAgent(data.FreeAgent, out problem)) == null)
                {
                    problem = $"its free agent (skater {HostText.KeyForLog(data.FreeAgent.Id)}): {problem}";
                    return null;
                }

                skater = agent;
            }
            else if (data.SkaterId != null && !_skaters.TryGetValue(data.SkaterId, out skater))
            {
                problem = $"its skater \"{HostText.KeyForLog(data.SkaterId)}\" isn't in the host's run here";
                return null;
            }

            Il2CppList.List<LeavingCondition>? conditions = null;
            if (data.Conditions != null && (conditions = Conditions(data.Conditions, out problem)) == null)
            {
                return null;
            }

            ScriptableObject? object1 = null, object2 = null;
            (string Host, string Here)? renamed = null;
            if ((data.Object1 != null && (object1 = Object(data.Object1, skater, agent, ref renamed, out problem)) == null) ||
                (data.Object2 != null && (object2 = Object(data.Object2, skater, agent, ref renamed, out problem)) == null))
            {
                return null;
            }

            if (!Terms(data.Terms, out Il2CppList.List<EventTermParam>? terms, out problem))
            {
                return null;
            }

            Il2CppReferenceArray<Sprite>? images = Images.Images(data, object1, object2, agent);

            // A preview named again in this PC's language: the choice's own copies of its name (its button and first text, as
            // the event's choice has them) are too. Then every text the host's recipe makes here, in this PC's language.
            EventChoiceData expected = renamed is { } names ? Renamed(data, names.Host, names.Here) : data;
            expected = EventTexts.Localize(expected, object1, object2, agent, Texts);
            // Fresh strings held only by this array: stored through the write barrier (GameArrays).
            Il2CppStringArray? texts = expected.Texts == null ? null : GameArrays.Strings(expected.Texts);

            CustomStatBonusesData? bonuses = null;
            if (data.StatBonuses != null && (bonuses = Bonuses(data.StatBonuses, out problem)) == null)
            {
                return null;
            }

            Injury? injury = null;
            if (data.Injury != null)
            {
                if (!Enum.IsDefined(typeof(InjuryType), data.Injury.Type))
                {
                    problem = $"its injury ({data.Injury.Type}) is one this game doesn't know";
                    return null;
                }

                injury = new Injury((InjuryType)data.Injury.Type, data.Injury.HealingTime)
                {
                    HealingTime = data.Injury.HealingTime,
                    OriginalTime = data.Injury.OriginalTime,
                    LastDisplayTime = data.Injury.LastDisplayTime,
                };
            }

            // A pick here never runs (OnItemClicked is refused): an action that does nothing keeps "has an action" as the
            // host's choice had it.
            Il2CppSystem.Action? onSelection = data.HasOnSelection ? _noOp ??= EventValues.NoOpAction() : null;

            // A Nurse's procedure takes the screen over on the host's: the same here would open its procedure screen with
            // this PC's own roster and state. Its pick never runs here (OnItemClicked is refused); an action that does
            // nothing keeps "takes the screen over" as the host's choice had it.
            Il2CppSystem.Action<EventChoiceMenu, EventChoiceItem>? onTakeover = data.HasOnTakeover ? _noOpTakeover ??= NoOpTakeover() : null;
            Takeovers += data.HasOnTakeover ? 1 : 0;
            problem = string.Empty;
            Expected.Add(expected);
            return new EventChoice
            {
                buttonName = expected.ButtonName!,
                translatedButtonName = expected.TranslatedButtonName!,
                onSelection = onSelection!,
                onTakeover = onTakeover!,
                interactable = data.Interactable,
                nodeType = (NodeType)data.NodeType,
                choicesTermParams = terms!,
                ChoiceType = (EEventChoiceType)data.ChoiceType,
                Skater = skater!,
                PowerUpOrRelic = object1!,
                PowerUpOrRelic2 = object2!,
                CustomImages = images!,
                CustomLocalizedTexts = texts!,
                CustomStatBonuses = bonuses!,
                CustomInjury = injury!,
                FreeAgentLeavingConditions = conditions == null ? null! : new Il2CppList.IReadOnlyList<LeavingCondition>(conditions.Pointer),
                SuppressRewardTracking = data.SuppressRewardTracking,
            };
        }

        /// <summary>An IL2CPP <c>Action&lt;EventChoiceMenu, EventChoiceItem&gt;</c> that does nothing (a takeover that never runs here).</summary>
        private static Il2CppSystem.Action<EventChoiceMenu, EventChoiceItem> NoOpTakeover() =>
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action<EventChoiceMenu, EventChoiceItem>>(new Action<EventChoiceMenu, EventChoiceItem>((_, _) => { }))!;

        /// <summary>
        /// The host's choice with its preview's name as this PC's: the button's translated name and any custom text that was
        /// the host's name (the event's own choice repeats the name there).
        /// </summary>
        private static EventChoiceData Renamed(EventChoiceData data, string host, string here) =>
            data.WithTexts(data.TranslatedButtonName == host ? here : data.TranslatedButtonName, data.Texts?.Select(text => text == host ? here : text).ToList());

        /// <summary>Notes an object made here (a clone, a new instance): it's destroyed if the plan is dropped unshown. Returns it.</summary>
        private T? Keep<T>(T? value)
            where T : UnityEngine.Object
        {
            if (value != null)
            {
                _made.Add(value);
            }

            return value;
        }

        /// <summary>An object made here that another took the place of (nothing holds it): destroyed now.</summary>
        private void Drop(UnityEngine.Object? replaced)
        {
            if (replaced == null)
            {
                return;
            }

            IntPtr pointer = replaced.Pointer;
            _made.RemoveAll(m => m.Pointer == pointer);
            UnityEngine.Object.Destroy(replaced);
        }

        /// <summary>
        /// A preview talent made as the event makes one (<c>FanNumber1SubEvent.CreatePreviewTalentChoice</c>): a new
        /// <c>FanNumber1PreviewTalent</c>, the host's hide flags, its <c>Initialize</c> with the id, name, description and icon,
        /// and its training flag; then every one read back. A size change's name and description are made again in this
        /// PC's language when they can be (<see cref="Localize"/>); its icon is the host's by its source (left out, as
        /// <see cref="EventImages"/> says, when it isn't here). Null, with why, when anything reads back otherwise.
        /// </summary>
        private FanNumber1PreviewTalent? Preview(EventObject value, SkaterData? skater, ref (string Host, string Here)? renamed, out string problem)
        {
            problem = string.Empty;
            Sprite? icon = value.PreviewIcon == null ? null : Images.Icon(value.PreviewIcon, value.PreviewIconSource);
            string? name = value.PreviewName, description = value.PreviewDescription;
            bool local = Localize(value.Id, skater, ref name, ref description);
            SaySizeChange(value, skater, name, local);
            FanNumber1PreviewTalent? preview = Keep(ScriptableObject.CreateInstance(Il2CppType.Of<FanNumber1PreviewTalent>()))?.TryCast<FanNumber1PreviewTalent>();
            if (preview == null)
            {
                problem = "this game couldn't make a preview talent";
                return null;
            }

            // DontSave, as the host's (CreatePreviewTalentChoice sets 0x34): never saved, kept until the game quits.
            preview.hideFlags = HideFlags.DontSave;
            preview.Initialize(value.Id, name!, description!, icon!);
            preview.IsTraining = value.PreviewTraining;
            if (preview.level != value.Level)
            {
                preview.level = value.Level;
            }

            string? differs = preview.id != value.Id ? "id"
                : preview.m_LocalizedName != name ? "name"
                : preview.m_LocalizedDescription != description ? "description"
                : Pointer(preview.icon) != Pointer(icon) ? "icon"
                : preview.IsTraining != value.PreviewTraining ? "training flag"
                : preview.level != value.Level ? "level"
                : null;
            if (differs != null)
            {
                problem = $"its {value}'s {differs} didn't read back as written";
                return null;
            }

            Previews++;
            if (local)
            {
                LocalPreviews++;
                if (value.PreviewName != null)
                {
                    renamed = (value.PreviewName, name!);
                }
            }

            return preview;
        }

        /// <summary>
        /// What a Fan Number 1 size change shows here, against the host's text: at most one line per preview of
        /// a set. A log line can't stop the event: anything that fails here is only left out. Never throws.
        /// </summary>
        private static void SaySizeChange(EventObject value, SkaterData? skater, string? here, bool local)
        {
            if (!SizeTerms.TryGetValue(value.Id, out (string Name, string Description, bool Up) terms))
            {
                return;
            }

            try
            {
                ForwardData? forward = skater?.TryCast<ForwardData>();
                string what = $"Menus: EventChoice: Fan Number 1's size change {SafeText.Clean(value.Id, 48)} for {SafeText.Clean(skater?.id, 40)}";
                string hostText = LogNames.TextShape(value.PreviewName); // the host's text: by its shape, like its other texts
                if (local && forward != null)
                {
                    SkaterSize next = terms.Up ? Il2CppTape2Tape.SkaterSizeUtil.GetNextSizeUp(forward.skaterSize) : Il2CppTape2Tape.SkaterSizeUtil.GetNextSizeDown(forward.skaterSize);
                    ModLog.Info($"{what} ({forward.skaterSize} to {next}) shows here as \"{SafeText.Clean(here, 48)}\" in {SafeText.Clean(LocalizationManager.CurrentLanguage, 32)} (the host's: {hostText}).");
                }
                else
                {
                    ModLog.Info($"{what} keeps the host's text ({hostText}; {(forward == null ? "the skater isn't a forward here" : $"no translation of {terms.Name} here")}).");
                }
            }
            catch (Exception ex)
            {
                ModLog.Trace($"Menus: EventChoice: the size change's log line was left out ({ex.GetType().Name}).");
            }
        }

        /// <summary>
        /// A Fan Number 1 size change's name and description in this PC's language, made as the event makes them
        /// (<c>PrototypeRewardPoolSubEvent.TryCreateIncreaseSizeChoice</c> / <c>TryCreateReduceSizeChoice</c>): the name's term
        /// translated, the description from <c>BuildSizeChangeDescription</c> with this PC's copy of the run's skater and its
        /// next size up or down. False (the host's texts stay) for another preview, a skater that isn't a forward here, or
        /// anything that fails.
        /// </summary>
        private static bool Localize(string id, SkaterData? skater, ref string? name, ref string? description)
        {
            if (!SizeTerms.TryGetValue(id, out (string Name, string Description, bool Up) terms) || skater?.TryCast<ForwardData>() is not { } forward)
            {
                return false;
            }

            try
            {
                string? localName = LocalizationManager.GetTranslation(terms.Name, true, 0, true, false, null, null, true);
                SkaterSize next = terms.Up ? Il2CppTape2Tape.SkaterSizeUtil.GetNextSizeUp(forward.skaterSize) : Il2CppTape2Tape.SkaterSizeUtil.GetNextSizeDown(forward.skaterSize);
                string? localDescription = PrototypeRewardPoolSubEvent.BuildSizeChangeDescription(terms.Description, forward, next);
                if (string.IsNullOrEmpty(localName) || string.IsNullOrEmpty(localDescription))
                {
                    return false;
                }

                name = localName;
                description = localDescription;
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Menus: EventChoice: couldn't make the size change's texts in this PC's language ({ModLog.SafeErrorText(ex)}); the host's are shown.");
                return false;
            }
        }

        private static CustomStatBonusesData? Bonuses(StatBonusSet set, out string problem)
        {
            problem = string.Empty;
            var list = new Il2CppList.List<StatBonus>();
            foreach (StatBonusData bonus in set.Bonuses)
            {
                if (!Enum.IsDefined(typeof(ESkateType), bonus.SkaterType) || !Enum.IsDefined(typeof(EForwardStat), bonus.StatForward) || !Enum.IsDefined(typeof(EGoalieStat), bonus.StatGoalie))
                {
                    problem = "a stat bonus is for a stat this game doesn't know";
                    return null;
                }

                list.Add(new StatBonus
                {
                    SkaterType = (ESkateType)bonus.SkaterType,
                    StatForward = (EForwardStat)bonus.StatForward,
                    StatGoalie = (EGoalieStat)bonus.StatGoalie,
                    Bonus = bonus.Bonus,
                    IsConditional = bonus.IsConditional,
                    IsBlockableStatGain = bonus.IsBlockableStatGain,
                });
            }

            var data = new CustomStatBonusesData { AreAlreadyCounted = set.AreAlreadyCounted };
            data.m_StatBonuses = list;
            return data;
        }

        /// <summary>
        /// A choice's relic, talent or ability: the run's own, the choice's free agent's (<paramref name="agent"/>, built here),
        /// or a fresh copy at the host's level; or a preview made here as the event makes one (<paramref name="renamed"/>: its
        /// name, the host's and this PC's, when it was named again in this PC's language); or null with why.
        /// <paramref name="skater"/> is the choice's skater, or null.
        /// </summary>
        private ScriptableObject? Object(EventObject value, SkaterData? skater, ForwardData? agent, ref (string Host, string Here)? renamed, out string problem)
        {
            problem = string.Empty;
            if (value.Kind == EventObjectKind.Preview)
            {
                return Preview(value, skater, ref renamed, out problem);
            }

            ScriptableObject? found = value.Owner switch
            {
                EventObjectOwner.Team => TeamRelic(value),
                EventObjectOwner.Skater => SkaterPowerUp(value),
                EventObjectOwner.FreeAgent => AgentPowerUp(value, agent),
                _ => FreshCopy(value),
            };
            if (found == null)
            {
                problem = value.Owner switch
                {
                    EventObjectOwner.None => $"its {value} isn't in this game",
                    EventObjectOwner.FreeAgent => $"its {value} isn't on the free agent built here",
                    _ => $"its {value} isn't in the host's run here",
                };
                return null;
            }

            if (MenuGame.TypeName(found) != value.ClassName)
            {
                problem = $"its {value} is a {SafeText.Clean(MenuGame.TypeName(found), 48)} here";
                return null;
            }

            if ((found.TryCast<ChaosRelic>() != null) != value.Chaos)
            {
                problem = $"its {value} is{(value.Chaos ? " not" : string.Empty)} a chaos relic here";
                return null;
            }

            if (value.Owner == EventObjectOwner.None)
            {
                // A fresh copy is this PC's own object: it takes the host's numbers.
                Fresh++;
                if (found.TryCast<Relic>() is { } relic)
                {
                    relic.intValue = value.IntValue;
                }

                if (found.TryCast<ChaosRelic>() is { } fresh)
                {
                    fresh.totalGames = value.TotalGames;
                    fresh._remainingGames = value.RemainingGames;
                }
            }
            else
            {
                // The run's own is as the host's run has it: one that isn't means this PC's copy of the run differs.
                Owned++;
                if (found.TryCast<ChaosRelic>() is { } owned && (owned.totalGames != value.TotalGames || owned._remainingGames != value.RemainingGames))
                {
                    problem = $"its {value} has {owned._remainingGames} of {owned.totalGames} games left here, {value.RemainingGames} of {value.TotalGames} on the host's";
                    return null;
                }
            }

            return found;
        }

        /// <summary>The free agent's own talent (by its number) or ability (-1), when it's the one the host's choice showed.</summary>
        private static PowerUp? AgentPowerUp(EventObject value, ForwardData? agent)
        {
            PowerUp? found = agent == null ? null
                : value.OwnerIndex < 0 ? agent.ability
                : agent.powerups is { } talents && value.OwnerIndex < talents.Count ? talents[value.OwnerIndex]
                : null;
            return found != null && found.id == value.Id && found.level == value.Level ? found : null;
        }

        /// <summary>
        /// A free agent built from the run file's own model of it with the game's own deserializer (<see cref="SkaterModels.Builder"/>:
        /// what this PC's checkpoint load will make of the skater once the host's pick is in the run), given what the model
        /// leaves out (its levels and flavour keys, then its look, birth date and number format:
        /// <see cref="SkaterModels.ApplyAgentLook"/>) and read back field by field. Null, with why, when it can't be built as
        /// the host's. It never joins this PC's run: picks are refused here, and the run changes only with the host's checkpoint.
        /// </summary>
        private ForwardData? FreeAgent(FreeAgentData data, out string problem)
        {
            // Everything it makes (the agent, its talents and ability, re-levelled ones) is noted with this event's own objects
            // (Made: destroyed if the plan is dropped unshown); a clone another took the place of is destroyed at once.
            _agents ??= new SkaterModels.Builder(_made);
            ForwardData? forward = _agents.Build(data, out problem);
            if (forward == null || !SkaterModels.ApplyAgentLook(forward, data, out problem))
            {
                return null;
            }

            Agents.Add($"skater {HostText.KeyForLog(data.Id)} #{data.Number}");
            return forward;
        }

        /// <summary>A free agent's leaving conditions as the game's (new conditions of each class in a new list), read back; or null with why.</summary>
        private static Il2CppList.List<LeavingCondition>? Conditions(IReadOnlyList<FreeAgentCondition> conditions, out string problem)
        {
            var list = new Il2CppList.List<LeavingCondition>();
            foreach (FreeAgentCondition condition in conditions)
            {
                LeavingCondition? made = condition.Kind switch
                {
                    FreeAgentConditionKind.MaxGames => new MaxGamesCondition { maxMatches = condition.Max, matches = condition.Current },
                    FreeAgentConditionKind.MaxGoals => new MaxGoalsCondition { maxGoals = condition.Max, goals = condition.Current },
                    FreeAgentConditionKind.Injury => new InjuryCondition { isInjured = condition.IsInjured },
                    FreeAgentConditionKind.Random => new RandomCondition { chance = condition.Chance },
                    _ => null,
                };
                if (made == null)
                {
                    problem = $"its free agent's {condition} is a condition this game doesn't have";
                    return null;
                }

                list.Add(made);
            }

            if (!SameConditions(list, conditions))
            {
                problem = "its free agent's leaving conditions didn't read back as written";
                return null;
            }

            problem = string.Empty;
            return list;
        }

        private Relic? TeamRelic(EventObject value)
        {
            Il2CppList.List<Relic>? relics = _team.relics;
            for (int i = 0; relics != null && i < relics.Count; i++)
            {
                Relic? relic = relics[i];
                if (relic != null && relic.id == value.Id && relic.level == value.Level && MenuGame.TypeName(relic) == value.ClassName)
                {
                    return relic;
                }
            }

            return null;
        }

        private PowerUp? SkaterPowerUp(EventObject value)
        {
            if (value.OwnerId == null || !_skaters.TryGetValue(value.OwnerId, out SkaterData? skater))
            {
                return null;
            }

            if (value.Kind == EventObjectKind.Ability)
            {
                Ability? ability = skater.ability;
                return ability != null && ability.id == value.Id && ability.level == value.Level ? ability : null;
            }

            Il2CppList.List<Talent>? talents = skater.powerups;
            for (int i = 0; talents != null && i < talents.Count; i++)
            {
                Talent? talent = talents[i];
                if (talent != null && talent.id == value.Id && talent.level == value.Level)
                {
                    return talent;
                }
            }

            return null;
        }

        /// <summary>
        /// A fresh copy from this game's lists at the host's level (as the game's own roll makes them), or null. The lists
        /// clone what they give (<c>isClone</c>): every copy is this PC's own, and one at the wrong level is destroyed.
        /// </summary>
        private ScriptableObject? FreshCopy(EventObject value)
        {
            switch (value.Kind)
            {
                case EventObjectKind.Relic:
                    _relics ??= MenuGame.Repository<IRelicRepository>(p => new IRelicRepository(p));
                    Relic? relic = Keep(_relics?.GetRelic(value.Id, true));
                    if (relic != null && relic.level != value.Level)
                    {
                        Relic first = relic;
                        relic = Keep(_relics!.GetRelic(relic.GetIl2CppType(), value.Level, true));
                        Drop(first);
                    }

                    return relic != null && relic.level == value.Level ? relic : null;
                case EventObjectKind.Talent:
                    _talents ??= MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p));
                    Talent? talent = Keep(_talents?.GetTalent(value.Id, true));
                    if (talent != null && talent.level != value.Level)
                    {
                        Talent first = talent;
                        talent = Keep(_talents!.GetTalent(talent.GetIl2CppType(), value.Level, true));
                        Drop(first);
                    }

                    return talent != null && talent.level == value.Level ? talent : null;
                case EventObjectKind.Ability:
                    _abilities ??= MenuGame.Repository<IAbilityRepository>(p => new IAbilityRepository(p));
                    Ability? ability = Keep(_abilities?.GetAbility(value.Id, true));
                    if (ability != null && ability.level != value.Level)
                    {
                        Ability first = ability;
                        ability = Keep(_abilities!.GetAbility(ability.GetIl2CppType(), value.Level, true));
                        Drop(first);
                    }

                    return ability != null && ability.level == value.Level ? ability : null;
                default:
                    return null;
            }
        }
    }
}

/// <summary>The game's own choices for one of the host's event sets, built on this PC and ready to put into its event.</summary>
internal sealed class EventPlan
{
    public EventPlan(EventChoiceSet set, EventNode node)
    {
        Set = set;
        Node = node;
    }

    /// <summary>The host's set it was built from.</summary>
    public EventChoiceSet Set { get; }

    /// <summary>This PC's event asset.</summary>
    public EventNode Node { get; }

    /// <summary>The menu's data, for opening it (null for a page or reroll).</summary>
    public EventChoiceMenu.Metadata? Request { get; set; }

    /// <summary>The choices, read back as written.</summary>
    public Il2CppList.List<EventChoice> Choices { get; set; } = null!;

    /// <summary>The node's description parameters (null when the host's had none).</summary>
    public Il2CppList.List<EventTermParam>? DescTerms { get; set; }

    /// <summary>How the choices went into their list ("List.Add" or "IList.Add").</summary>
    public string How { get; set; } = string.Empty;

    /// <summary>The training event, for one (its fields below are written too).</summary>
    public TrainingEvent? Training { get; set; }

    public BasicStats Stat { get; set; }

    public Rarity Rarity { get; set; }

    public TrainingRewardType Reward { get; set; }

    public ForwardData? Forward { get; set; }

    /// <summary>True once the event's <c>Initialize</c> took the host's choices instead of its roll.</summary>
    public bool Installed { get; set; }

    /// <summary>
    /// The objects this plan made on this PC (previews, free agents and their power-ups, fresh copies): destroyed when the
    /// plan is dropped before its choices went into an event (<see cref="EventViews.Release"/>).
    /// </summary>
    public IReadOnlyList<UnityEngine.Object> Made { get; set; } = Array.Empty<UnityEngine.Object>();

    /// <summary>True once its choices were written into an event (<see cref="EventViews.Write"/>): the event holds what it made.</summary>
    public bool Written { get; set; }

    /// <summary>The menu that showed it (after its <c>Show</c>), or null.</summary>
    public EventChoiceMenu? Menu { get; set; }

    /// <summary>A Nurse's roster rebuilt here from the host's (v1, <see cref="NurseRosters"/>), or null.</summary>
    public NurseBuilt? Nurse { get; set; }

    /// <summary>Why it can't be shown after all (set while the menu opens; the view then shows text), or "".</summary>
    public string Failure { get; set; } = string.Empty;
}
#endif
