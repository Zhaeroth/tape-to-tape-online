#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppData;
using Il2CppInjection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSkaters;
using Il2CppState;
using Il2CppTape2Tape.Customization;
using Il2CppTape2Tape.Customization.UI;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using UnityEngine;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// Custom teams, skaters, goalies and logos, which exist only on the PC that made them. The game keeps each custom team, skater
/// and goalie as its own JSON (<c>TeamDataModels/*.json</c>, <c>JsonUtility</c>) and each custom logo as a PNG in
/// <c>CustomLogos</c>, named <c>custom_&lt;file&gt;</c> as a sprite; a team names its logo, skaters and goalie by those keys.
/// <list type="bullet">
/// <item>Host: an exhibition's setup names the custom content its two teams need (<see cref="Collect"/>; the setup's
/// <c>custom.*</c> values), and the relay sends each item to each friend once a session. A campaign's run team wears a
/// custom logo when the host's custom campaign team has one: that logo goes too (<see cref="RunLogoItem"/>).</item>
/// <item>Friend: logos are loaded as they arrive with the game's own loader (<c>CustomLogoLoader.LoadSprites</c>, from a
/// file in the mod's folder that's deleted at once) and put first in the game's logo list for the session, so the game's
/// own lookups by name find them (a custom team's build, a run load). A custom team, skater or goalie is built when a match
/// needs it with the game's own code (<c>TeamRepository.DeserializeTeam</c>, the adapters and the repositories'
/// <c>DeserializeAssetReferences</c>), never saved: nothing goes into the friend's custom lists or files.</item>
/// <item>When the session ends the logo list is put back and the mod's references dropped: the game frees what's no
/// longer used at its next scene load, as it does its own random teams.</item>
/// </list>
/// Nothing here throws into the game: failures are logged and answered as "not here".
/// </summary>
internal static class CustomContent
{
    private const int MaxLoggedFailures = 5;

    private static readonly Dictionary<ulong, UnityEngine.Object> Built = new();
    private static readonly Dictionary<string, CustomRef> BuiltIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<ulong, CustomRef> TeamLogos = new();
    private static readonly Dictionary<string, Sprite> Logos = new(StringComparer.Ordinal);
    private static readonly List<CustomRef> Refs = new();
    private static readonly CustomLogoBudget LogoBudget = new();
    private static CustomContentReceiver? _source;
    private static int _failures;
    private static string? _runLogoName;
    private static CustomItem? _runLogo;

    /// <summary>Friend: true while the host's custom content is being received (a session with a host).</summary>
    public static bool Receiving => _source != null;

    // ------------------------------------------------------------------ host

    /// <summary>
    /// The custom content a match's two teams need, as items (the bytes the host's game keeps) and the references a setup
    /// carries (<see cref="CustomContentValues"/>). On a friend's PC, reading back the match it loaded, content built from the
    /// host's items is named by the host's references, so the two setups compare equal. False, with why, when there's more
    /// than a setup can name or content can't be read.
    /// </summary>
    public static bool Collect(TeamData home, TeamData visitor, List<CustomItem> items, List<CustomRef> refs, out string problem)
    {
        problem = string.Empty;
        HostCustoms? customs = null;
        foreach (TeamData team in new[] { home, visitor })
        {
            if (team == null)
            {
                continue;
            }

            customs ??= HostCustoms.Read();
            if (!CollectTeam(team, customs, items, refs, out problem))
            {
                return false;
            }
        }

        if (refs.Count > CustomContentValues.MaxRefs)
        {
            problem = $"it uses {refs.Count} custom teams, skaters, goalies and logos (at most {CustomContentValues.MaxRefs} can be sent)";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Host, shared campaign: the custom logo the run team wears, as an item for friends (null when it wears one of the
    /// game's own, or there's no run). Read again only when the logo's name changes.
    /// </summary>
    public static CustomItem? RunLogoItem(CampaignState? campaign)
    {
        string? name = campaign?.runData?.teamData?.logo?.name;
        if (name == null || !name.StartsWith(CustomItem.LogoPrefix, StringComparison.Ordinal))
        {
            _runLogoName = null;
            _runLogo = null;
            return null;
        }

        if (name != _runLogoName)
        {
            LogNames.NoteLogo(name);
            _runLogoName = name;
            _runLogo = ReadLogo(CustomContentKind.RunLogo, name, out string problem);
            ModLog.Info(_runLogo != null
                ? $"Custom content: the run team wears this PC's custom logo ({LogNames.TextShape(name)}, {_runLogo.Bytes.Length / 1024} KB); it goes to friends."
                : $"Custom content: the run team wears this PC's custom logo ({LogNames.TextShape(name)}), which can't be sent ({problem}); friends see the game's default logo.");
        }

        return _runLogo;
    }

    /// <summary>
    /// Host: the custom logo <paramref name="name"/> (<c>custom_&lt;file&gt;.png</c>) a history page wears, as an item for friends,
    /// read from the game's custom logo folder (checked as a match's logos are: a PNG, 4 MB at most, 4096 pixels a side). Null,
    /// with why, when it can't be read or sent.
    /// </summary>
    public static CustomItem? HistoryLogoItem(string name, out string problem)
    {
        LogNames.NoteLogo(name);
        return ReadLogo(CustomContentKind.Logo, name, out problem);
    }

    /// <summary>
    /// Friend: true when the host's logo <paramref name="key"/> (the sprite name a page names) is loaded here and in the game's
    /// logo list for this session, so the game's own lookup by that name finds the host's logo.
    /// </summary>
    public static bool HasLogo(string key) => Logos.TryGetValue(key, out Sprite? sprite) && sprite != null;

    /// <summary>
    /// A campaign match's custom content: the host's run team's custom logo and its look beyond the run
    /// file, named in the setup so a friend's load waits for them as an exhibition's does (and the first faceoff shows the
    /// logo). On the host, from its shared run; on a friend reading back the match it loaded, the references the host's
    /// setup named, so the two setups compare equal.
    /// </summary>
    public static void CollectRun(List<CustomItem> items, List<CustomRef> refs)
    {
        LogNames.NoteRunNames(CampaignGame.State());
        if (CampaignRelay.Current is { Hosting: true } && RunSync.Instance?.HostOnCoopSlot == true)
        {
            CampaignState? campaign = CampaignGame.State();
            foreach (CustomItem? item in new[] { RunLogoItem(campaign), RunLook.Item(campaign) })
            {
                if (item != null)
                {
                    AddItem(item, items, refs);
                }
            }
        }
        else if (_source != null)
        {
            refs.AddRange(Refs.Where(r => r.Kind is CustomContentKind.RunLogo or CustomContentKind.RunLook && !refs.Contains(r)));
        }
    }

    private static bool CollectTeam(TeamData team, HostCustoms customs, List<CustomItem> items, List<CustomRef> refs, out string problem)
    {
        problem = string.Empty;
        string teamId = team.id ?? string.Empty;
        if (customs.Teams.TryGetValue(teamId, out TeamDataModel? model))
        {
            if (!AddModel(CustomContentKind.Team, teamId, model, items, refs, out problem))
            {
                return false;
            }

            string logo = model.logoId ?? string.Empty;
            if (logo.StartsWith(CustomItem.LogoPrefix, StringComparison.Ordinal))
            {
                LogNames.NoteLogo(logo);
                CustomItem? item = ReadLogo(CustomContentKind.Logo, logo, out string logoProblem);
                if (item == null)
                {
                    // The team still goes: a friend's game then shows its default logo, as it would for a missing file.
                    ModLog.Warn($"Custom content: a custom team's logo ({LogNames.TextShape(logo)}) can't be sent ({logoProblem}; the team: {LogNames.TextShape(team.teamName)}); friends see the game's default logo.");
                }
                else
                {
                    AddItem(item, items, refs);
                }
            }
        }
        else
        {
            AddBuilt(CustomContentKind.Team, teamId, refs);
        }

        var ids = new List<string>();
        Il2CppSystem.Collections.Generic.List<ForwardData> forwards = team.GetForwards(true);
        for (int i = 0; i < forwards.Count; i++)
        {
            if (forwards[i] is ForwardData forward)
            {
                ids.Add(forward.OriginalId ?? string.Empty);
                ids.Add(forward.id ?? string.Empty);
            }
        }

        foreach (string id in ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (customs.Skaters.TryGetValue(id, out ForwardDataModel? skater))
            {
                if (!AddModel(CustomContentKind.Skater, skater.id ?? id, skater, items, refs, out problem))
                {
                    return false;
                }
            }
            else
            {
                AddBuilt(CustomContentKind.Skater, id, refs);
            }
        }

        GoaltenderData? goalie = team.goalie;
        foreach (string id in new[] { goalie?.OriginalId, goalie?.id }.OfType<string>().Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (customs.Goalies.TryGetValue(id, out GoalieDataModel? keeper))
            {
                return AddModel(CustomContentKind.Goalie, keeper.id ?? id, keeper, items, refs, out problem);
            }

            AddBuilt(CustomContentKind.Goalie, id, refs);
        }

        return true;
    }

    private static bool AddModel(CustomContentKind kind, string key, Il2CppSystem.Object model, List<CustomItem> items, List<CustomRef> refs, out string problem)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonUtility.ToJson(model, false) ?? string.Empty);
        if (CustomItem.Check(kind, key, json.Length) is string bad)
        {
            problem = $"its custom {CustomRef.Name(kind)} ({LogKey(kind, key)}) can't be sent ({bad})";
            return false;
        }

        AddItem(new CustomItem(kind, key, json), items, refs);
        problem = string.Empty;
        return true;
    }

    private static void AddItem(CustomItem item, List<CustomItem> items, List<CustomRef> refs)
    {
        if (items.All(i => i.Hash != item.Hash))
        {
            items.Add(item);
        }

        if (!refs.Contains(item.Ref))
        {
            refs.Add(item.Ref);
        }
    }

    /// <summary>
    /// A friend reading back its match: content it built from the host's item is named by that item (a team with its logo,
    /// in the host's order).
    /// </summary>
    private static void AddBuilt(CustomContentKind kind, string id, List<CustomRef> refs)
    {
        if (!BuiltIds.TryGetValue(IdKey(kind, id), out CustomRef found))
        {
            return;
        }

        if (!refs.Contains(found))
        {
            refs.Add(found);
        }

        if (TeamLogos.TryGetValue(found.Hash, out CustomRef logo) && !refs.Contains(logo))
        {
            refs.Add(logo);
        }
    }

    /// <summary>A custom logo's PNG from the game's custom logo folder, as an item; null, with why, when it can't be read or sent.</summary>
    private static CustomItem? ReadLogo(CustomContentKind kind, string name, out string problem)
    {
        try
        {
            if (CustomItem.Check(kind, name, 1) is string bad)
            {
                problem = bad;
                return null;
            }

            string path = Path.Combine(Application.persistentDataPath, "CustomLogos", CustomItem.LogoFileName(name));
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                problem = "its file isn't in the game's CustomLogos folder";
                return null;
            }

            if (file.Length > CustomItem.MaxLogoBytes)
            {
                problem = $"its file is {file.Length / 1024} KB (at most {CustomItem.MaxLogoBytes / 1024 / 1024} MB)";
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (PngHeader.CheckLogo(bytes) is string notLogo)
            {
                problem = notLogo;
                return null;
            }

            problem = string.Empty;
            return new CustomItem(kind, name, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = $"the file couldn't be read ({ex.GetType().Name})"; // its path holds the logo's name: player-written (D127)
            return null;
        }
    }

    // ------------------------------------------------------------------ friend

    /// <summary>A friend session started: the host's custom content arrives in <paramref name="source"/>.</summary>
    public static void StartFriend(CustomContentReceiver source, string hostName)
    {
        StopFriend("a new session");
        _source = source;
        _failures = 0;
        LogoBudget.Reset();
        RunLook.StartFriend();
        Campaign.Progress.HistoryLogos.StartFriend(hostName);
    }

    /// <summary>
    /// The friend session ended: the game's logo list is as it was, and the content built from the host's is let go (the
    /// game frees it at its next scene load once nothing uses it; a host match still held here keeps what it uses).
    /// </summary>
    public static void StopFriend(string reason)
    {
        RunLook.StopFriend();
        if (_source == null && Logos.Count == 0 && Built.Count == 0)
        {
            return;
        }

        int logos = Logos.Count;
        int built = Built.Count;
        int overLimit = LogoBudget.Refused;
        RemoveLogos();
        Built.Clear();
        BuiltIds.Clear();
        TeamLogos.Clear();
        Refs.Clear();
        _source?.Clear();
        _source = null;
        LogoBudget.Reset();
        if (logos + built + overLimit > 0)
        {
            ModLog.Info($"Custom content: the session ended ({reason}); {logos} logo(s) of the host's taken out of this game's logo list, {built} custom team(s), skater(s) and goalie(s) let go" +
                $"{(overLimit > 0 ? $"; {overLimit} logo load(s) refused over the session's limit" : string.Empty)}.");
        }
    }

    /// <summary>
    /// Call once per frame (friend): content that arrived is logged, and each logo is loaded and put in the game's logo
    /// list at once (a run load or a custom team's build looks its logo up by name). Never throws.
    /// </summary>
    public static void Update()
    {
        LogNames.Update(); // the log's team names and custom names: two field reads once the game's teams are read
        if (_source == null)
        {
            return;
        }

        try
        {
            while (_source.TryTakeCompleted(out CustomItem? item) && item != null)
            {
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Custom content: {LogNames.HostTag()}'s {item.Ref} arrived ({item.Bytes.Length / 1024.0:0.#} KB)."));
                if (item.Kind == CustomContentKind.RunLook)
                {
                    RunLook.Received(item);
                }
                else if (item.IsLogo)
                {
                    LogNames.NoteLogo(item.Key);
                    Campaign.Progress.HistoryLogos.Offer(item); // a shared run's history entry may name it (D55 as amended)
                    Sprite? sprite = RegisterLogo(item);
                    if (sprite != null && item.Kind == CustomContentKind.RunLogo)
                    {
                        WearOnRun(sprite);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fail("taking the host's custom content", ex);
        }
    }

    /// <summary>
    /// Friend: the custom content the setup being loaded names (none: the match uses only the game's own), or the host's
    /// Play Now screen names for its teams. Lookups below answer only for these.
    /// </summary>
    public static void BeginLoad(IReadOnlyList<CustomRef> refs)
    {
        Refs.Clear();
        Refs.AddRange(refs);
    }

    /// <summary>
    /// Friend: true while some of the content the setup names hasn't arrived. Never true without a host (F2 rehearses on
    /// the PC that made the content, which finds it in its own lists). Allocates nothing: a load waiting for content asks
    /// every frame.
    /// </summary>
    public static bool Waiting()
    {
        if (_source == null)
        {
            return false;
        }

        foreach (CustomRef found in Refs)
        {
            if (!_source.TryGet(found.Hash, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Friend: how much of the content the setup names is here (<paramref name="received"/> of <paramref name="total"/>
    /// bytes; an item's size is known from its first piece). Allocates nothing: a load waiting for content asks every frame.
    /// </summary>
    public static void AwaitedBytes(out long received, out long total)
    {
        received = total = 0;
        if (_source == null)
        {
            return;
        }

        foreach (CustomRef found in Refs)
        {
            _source.ProgressOf(found.Hash, out long here, out long size);
            received += here;
            total += size;
        }
    }

    /// <summary>Friend: what of the content the setup names hasn't arrived yet, for the log (empty when it all has).</summary>
    public static string WaitingFor()
    {
        if (_source == null)
        {
            return string.Empty;
        }

        var missing = Refs.Where(r => !_source.TryGet(r.Hash, out _)).Select(r => $"{r} ({_source.Progress(r.Hash)})").ToList();
        return string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? $" and {missing.Count - 4} more" : string.Empty);
    }

    /// <summary>
    /// Friend: the host's custom team <paramref name="id"/>, built with the game's own code (cached for the session), or null:
    /// with an empty <paramref name="problem"/> when the setup names no such custom team (the game's own lists have it),
    /// else why it can't be built here.
    /// </summary>
    public static TeamData? Team(string id, out string problem)
    {
        problem = string.Empty;
        if (!TryItem(CustomContentKind.Team, id, out CustomRef found, out CustomItem? item, ref problem))
        {
            return null;
        }

        if (Built.TryGetValue(found.Hash, out UnityEngine.Object? cached) && cached != null)
        {
            return cached.TryCast<TeamData>();
        }

        try
        {
            TeamDataModel? model = FromJson<TeamDataModel>(item!);
            LogNames.NoteCustom(model);
            TeamRepository? repository = Injected<ITeamRepository>()?.TryCast<TeamRepository>();
            if (model == null || repository == null)
            {
                problem = model == null ? $"the host's custom team {Quote(id)} didn't read" : "the game's team list isn't available";
                return null;
            }

            if (model.Id != id)
            {
                problem = $"the host's custom team {Quote(id)} says it's {Quote(model.Id)}";
                return null;
            }

            // Its logo goes into the game's list first (it may have arrived this very frame): the build looks it up by name.
            CustomRef logoRef = Refs.FirstOrDefault(r => r.Kind == CustomContentKind.Logo && r.Key == model.logoId);
            if (logoRef.Kind != CustomContentKind.None && _source!.TryGet(logoRef.Hash, out CustomItem? logoItem) && logoItem != null)
            {
                RegisterLogo(logoItem);
            }

            // The game's own build: colours, logo (by name: a logo of the host's is in the list by now), relics, the skaters
            // and goalie it has; the host's own custom skaters and goalie are put in their places after, as the game puts
            // its own (an instance with the custom team's uniform).
            TeamData team = repository.DeserializeTeam(model);
            string[] slots = { model.leftWingerId, model.rightWingerId, model.centerId, model.leftDefensemenId, model.rightDefensemenId };
            var filled = new List<string>();
            for (int i = 0; i < slots.Length; i++)
            {
                string slot = slots[i] ?? string.Empty;
                if (slot.Length == 0 || team.GetForward(i) != null)
                {
                    continue;
                }

                ForwardData? source = Skater(slot, out string skaterProblem);
                if (source == null)
                {
                    problem = skaterProblem.Length > 0 ? skaterProblem : $"the host's custom team ({LogNames.TextShape(model.TeamName)}) has a skater {Quote(slot)} this PC doesn't";
                    return null;
                }

                ForwardData forward = UnityEngine.Object.Instantiate(source).Cast<ForwardData>();
                SkinHelper.OverrideSkinsWithCustomTeamUniform(forward, false, false);
                if (i == 4)
                {
                    team.rightDefensemen = forward;
                }
                else
                {
                    team.SetForward(i, forward);
                }

                filled.Add(slot);
            }

            string goalieId = model.goalieId ?? string.Empty;
            if (team.goalie == null && goalieId.Length > 0)
            {
                GoaltenderData? source = Goalie(goalieId, out string goalieProblem);
                if (source == null)
                {
                    problem = goalieProblem.Length > 0 ? goalieProblem : $"the host's custom team ({LogNames.TextShape(model.TeamName)}) has a goalie {Quote(goalieId)} this PC doesn't";
                    return null;
                }

                GoaltenderData goalie = UnityEngine.Object.Instantiate(source).Cast<GoaltenderData>();
                SkinHelper.OverrideSkinsWithCustomTeamUniform(goalie, false, false);
                team.goalie = goalie;
                filled.Add(goalieId);
            }

            Remember(found, team);
            if (logoRef.Kind != CustomContentKind.None)
            {
                TeamLogos[found.Hash] = logoRef;
            }

            string logo = team.logo != null ? team.logo.name ?? string.Empty : string.Empty;
            ModLog.Info($"Custom content: built {LogNames.HostTag()}'s custom team ({LogNames.TextShape(model.TeamName)}; {HostText.KeyForLog(id)}) with the game's own code; logo ({LogNames.TextShape(logo)})" +
                $"{(model.logoId != null && logo != model.logoId ? $" (the host's is {LogNames.TextShape(model.logoId)}: not here)" : string.Empty)}; " +
                $"{filled.Count} of its skaters and goalie are the host's own custom ones.");
            return team;
        }
        catch (Exception ex)
        {
            problem = $"building the host's custom team {Quote(id)} failed ({ex.GetType().Name})";
            Fail($"building the custom team {Quote(id)}", ex);
            return null;
        }
    }

    /// <summary>
    /// Friend: a copy of the host's custom skater <paramref name="id"/> for a roster, or null when the setup names no such
    /// skater (or it can't be built: logged).
    /// </summary>
    public static ForwardData? NewForward(string id)
    {
        ForwardData? source = Skater(id, out string problem);
        if (source == null)
        {
            if (problem.Length > 0)
            {
                ModLog.Warn($"Custom content: {problem}.");
            }

            return null;
        }

        return UnityEngine.Object.Instantiate(source).Cast<ForwardData>();
    }

    /// <summary>Friend: the host's custom goalie <paramref name="id"/>, or null when the setup names no such goalie (or it can't be built: logged).</summary>
    public static GoaltenderData? GoalieFor(string id)
    {
        GoaltenderData? goalie = Goalie(id, out string problem);
        if (goalie == null && problem.Length > 0)
        {
            ModLog.Warn($"Custom content: {problem}.");
        }

        return goalie;
    }

    private static ForwardData? Skater(string id, out string problem)
    {
        problem = string.Empty;
        if (!TryItem(CustomContentKind.Skater, id, out CustomRef found, out CustomItem? item, ref problem))
        {
            return null;
        }

        if (Built.TryGetValue(found.Hash, out UnityEngine.Object? cached) && cached != null)
        {
            return cached.TryCast<ForwardData>();
        }

        try
        {
            ForwardDataModel? model = FromJson<ForwardDataModel>(item!);
            LogNames.NoteCustom(model);
            ForwardRepository? repository = Injected<IForwardRepository>()?.TryCast<ForwardRepository>();
            if (model == null || repository == null || model.id != id)
            {
                problem = repository == null ? "the game's skater list isn't available" : $"the host's custom skater {Quote(id)} didn't read";
                return null;
            }

            // As the game makes its own custom skaters (ForwardRepository.LoadAllCustomForwardsInternal).
            ForwardData forward = ForwardDataAdapter.ConvertToForwardData(model);
            repository.DeserializeAssetReferences(model, forward);
            Remember(found, forward);
            ModLog.Info($"Custom content: built {LogNames.HostTag()}'s custom skater ({LogNames.TextShape($"{model.firstName} {model.lastName}")}; {HostText.KeyForLog(id)}) with the game's own code.");
            return forward;
        }
        catch (Exception ex)
        {
            problem = $"building the host's custom skater {Quote(id)} failed ({ex.GetType().Name})";
            Fail($"building the custom skater {Quote(id)}", ex);
            return null;
        }
    }

    private static GoaltenderData? Goalie(string id, out string problem)
    {
        problem = string.Empty;
        if (!TryItem(CustomContentKind.Goalie, id, out CustomRef found, out CustomItem? item, ref problem))
        {
            return null;
        }

        if (Built.TryGetValue(found.Hash, out UnityEngine.Object? cached) && cached != null)
        {
            return cached.TryCast<GoaltenderData>();
        }

        try
        {
            GoalieDataModel? model = FromJson<GoalieDataModel>(item!);
            LogNames.NoteCustom(model);
            GoalieRepository? repository = Injected<IGoalieRepository>()?.TryCast<GoalieRepository>();
            if (model == null || repository == null || model.id != id)
            {
                problem = repository == null ? "the game's goalie list isn't available" : $"the host's custom goalie {Quote(id)} didn't read";
                return null;
            }

            // As the game makes its own custom goalies (GoalieRepository.LoadAllCustomGoaliesInternal).
            GoaltenderData goalie = GoalieDataAdapter.ConvertToGoaltenderData(model);
            repository.DeserializeAssetReferences(model, goalie);
            Remember(found, goalie);
            ModLog.Info($"Custom content: built {LogNames.HostTag()}'s custom goalie ({LogNames.TextShape($"{model.firstName} {model.lastName}")}; {HostText.KeyForLog(id)}) with the game's own code.");
            return goalie;
        }
        catch (Exception ex)
        {
            problem = $"building the host's custom goalie {Quote(id)} failed ({ex.GetType().Name})";
            Fail($"building the custom goalie {Quote(id)}", ex);
            return null;
        }
    }

    /// <summary>
    /// The setup's reference of <paramref name="kind"/> for <paramref name="id"/> and its item. False with an empty problem
    /// when the setup names none (or there's no host: F2 finds the content in this PC's own lists), false with why when
    /// it's named but not here.
    /// </summary>
    private static bool TryItem(CustomContentKind kind, string id, out CustomRef found, out CustomItem? item, ref string problem)
    {
        item = null;
        found = Refs.FirstOrDefault(r => r.Kind == kind && r.Key == id);
        if (found.Kind == CustomContentKind.None || _source == null)
        {
            return false;
        }

        if (!_source.TryGet(found.Hash, out item) || item == null)
        {
            problem = $"the host's custom {CustomRef.Name(kind)} ({LogKey(kind, id)}) hasn't arrived ({_source.Progress(found.Hash)})";
            return false;
        }

        return true;
    }

    private static void Remember(CustomRef found, UnityEngine.Object built)
    {
        Built[found.Hash] = built;
        BuiltIds[IdKey(found.Kind, found.Key)] = found;
    }

    private static string IdKey(CustomContentKind kind, string id) => $"{(int)kind}:{id}";

    private static T? FromJson<T>(CustomItem item)
        where T : Il2CppSystem.Object
    {
        if (!CustomItem.IsShallowJson(item.Bytes))
        {
            ModLog.Warn($"Custom content: {LogNames.HostTag()}'s {item.Ref} nests deeper than the game's JSON ever does, so it never reaches Unity's reader; it counts as not read.");
            return null;
        }

        Il2CppSystem.Object? read = JsonUtility.FromJson(Encoding.UTF8.GetString(item.Bytes), Il2CppType.Of<T>());
        return read?.TryCast<T>();
    }

    /// <summary>
    /// A logo of the host's, loaded with the game's own loader (it names the sprite <c>custom_</c> and the file's name, as
    /// the host's) from a file in the mod's folder, deleted at once, and put first in the game's logo list for the session.
    /// Null, logged, when it isn't a logo this PC decodes.
    /// </summary>
    private static Sprite? RegisterLogo(CustomItem item)
    {
        if (Logos.TryGetValue(item.Key, out Sprite? known) && known != null)
        {
            return known;
        }

        if (PngHeader.CheckLogo(item.Bytes) is string bad)
        {
            ModLog.Warn($"Custom content: {LogNames.HostTag()}'s logo ({LogNames.TextShape(item.Key)}) isn't loaded ({bad}); this game shows its default logo instead.");
            return null;
        }

        // Each logo decoded stays a texture for the session: how many and how large is capped.
        PngHeader.TryRead(item.Bytes, out int width, out int height);
        if (!LogoBudget.TryTake(width, height, out string overLimit))
        {
            if (LogoBudget.Refused <= MaxLoggedFailures)
            {
                ModLog.Warn($"Custom content: {LogNames.HostTag()}'s logo ({LogNames.TextShape(item.Key)}, {width}x{height}) isn't loaded: {overLimit}; this game shows its default logo instead.");
            }

            return null;
        }

        TeamAssetsRepositoryScriptableObject? repository = LogoRepository();
        if (repository == null)
        {
            ModLog.Warn($"Custom content: the game's logo list isn't available; {LogNames.HostTag()}'s logo ({LogNames.TextShape(item.Key)}) isn't loaded.");
            return null;
        }

        string folder = Path.Combine(CampaignFiles.ModFolder, "custom");
        string path = Path.Combine(folder, CustomItem.LogoFileName(item.Key));
        Sprite? sprite = null;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, item.Bytes);

            // The path's string is fresh and held only by this fresh array until the loader reads it, after an allocation of
            // its own: stored through the write barrier (GameArrays), so the incremental collector can't miss it meanwhile.
            Il2CppStringArray paths = Campaign.Menus.GameArrays.Strings(new[] { path });
            Il2CppSystem.Collections.Generic.List<Sprite>? loaded = CustomLogoLoader.LoadSprites(paths);
            GC.KeepAlive(paths);
            sprite = loaded != null && loaded.Count > 0 ? loaded[0] : null;
        }
        finally
        {
            TryDelete(path);
        }

        if (sprite == null || sprite.name != item.Key)
        {
            ModLog.Warn($"Custom content: the game couldn't load {LogNames.HostTag()}'s logo ({LogNames.TextShape(item.Key)}){(sprite != null ? $" (it came out as " + LogNames.TextShape(sprite.name) + ")" : string.Empty)}; this game shows its default logo instead.");
            return null;
        }

        Il2CppReferenceArray<Sprite>? current = repository._allLogos;
        int count = current?.Length ?? 0;
        var logos = new List<Sprite> { sprite };
        bool clash = false;
        for (int i = 0; i < count; i++)
        {
            Sprite? other = current![i];
            if (other != null && other.name == item.Key)
            {
                clash |= !Logos.ContainsKey(item.Key);
            }

            logos.Add(other!);
        }

        // The old list is dropped as the new one is set, and this game's own custom logos are held by nothing else: each is
        // stored through the write barrier (GameArrays), so a collection under way can't miss one.
        var array = new Il2CppReferenceArray<Sprite>(logos.Count);
        for (int i = 0; i < logos.Count; i++)
        {
            Campaign.Menus.GameArrays.Store(array, i, logos[i]?.Pointer ?? IntPtr.Zero);
        }

        repository._allLogos = array;
        Logos[item.Key] = sprite;
        Texture2D texture = sprite.texture;
        ModLog.Info($"Custom content: loaded {LogNames.HostTag()}'s logo ({LogNames.TextShape(item.Key)}; {(texture != null ? $"{texture.width}x{texture.height}" : "no texture")}) with the game's own loader; " +
            $"it's first in this game's logo list for the session{(clash ? " (this PC has a custom logo of the same name: the host's is shown meanwhile)" : string.Empty)}.");
        return sprite;
    }

    /// <summary>The host's run team wears <paramref name="sprite"/>: put it on the copy of the host's run held here, if that loaded before the logo arrived.</summary>
    private static void WearOnRun(Sprite sprite)
    {
        if (RunSync.Instance is not { HoldingHostRun: true })
        {
            return;
        }

        TeamData? team = CampaignGame.State()?.runData?.teamData;
        if (team == null || (team.logo != null && team.logo.name == sprite.name))
        {
            return;
        }

        string before = team.logo != null ? team.logo.name ?? string.Empty : string.Empty;
        team.logo = sprite;
        ModLog.Info($"Custom content: the host's run team here now wears {LogNames.HostTag()}'s logo ({LogNames.TextShape(sprite.name)}; the run had loaded with {LogNames.TextShape(before)}).");
    }

    /// <summary>Takes this session's logos out of the game's logo list (whatever else is in it now stays).</summary>
    private static void RemoveLogos()
    {
        if (Logos.Count == 0)
        {
            return;
        }

        try
        {
            TeamAssetsRepositoryScriptableObject? repository = LogoRepository();
            Il2CppReferenceArray<Sprite>? current = repository?._allLogos;
            if (repository != null && current != null)
            {
                var ours = new HashSet<IntPtr>(Logos.Values.Where(s => s != null).Select(s => s.Pointer));
                var kept = new List<Sprite>();
                for (int i = 0; i < current.Length; i++)
                {
                    Sprite? sprite = current[i];
                    if (sprite == null || !ours.Contains(sprite.Pointer))
                    {
                        kept.Add(sprite!);
                    }
                }

                var array = new Il2CppReferenceArray<Sprite>(kept.Count);
                for (int i = 0; i < kept.Count; i++)
                {
                    Campaign.Menus.GameArrays.Store(array, i, kept[i]?.Pointer ?? IntPtr.Zero); // as in RegisterLogo
                }

                repository._allLogos = array;
            }
        }
        catch (Exception ex)
        {
            Fail("taking the host's logos out of the game's logo list", ex);
        }

        Logos.Clear();
        TryDelete(Path.Combine(CampaignFiles.ModFolder, "custom"), folder: true);
    }

    private static TeamAssetsRepositoryScriptableObject? LogoRepository() =>
        Injected<ILogoRepository>()?.TryCast<TeamAssetsRepositoryScriptableObject>()
        ?? UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<TeamAssetsRepositoryScriptableObject>()).Select(o => o?.TryCast<TeamAssetsRepositoryScriptableObject>()).FirstOrDefault(r => r != null);

    /// <summary>The game's team list (its own and this PC's custom teams), or null.</summary>
    public static ITeamRepository? TeamRepository() => Injected<ITeamRepository>()?.TryCast<ITeamRepository>();

    /// <summary>A service the game binds in its project injector, by type.</summary>
    private static Il2CppSystem.Object? Injected<T>()
        where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        => ProjectContext.Instance?.Injector?.Get(Il2CppType.Of<T>());

    private static void TryDelete(string path, bool folder = false)
    {
        try
        {
            if (folder && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (!folder && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Custom content: couldn't delete a received {(folder ? "folder" : "file")} ({ex.GetType().Name})."); // its name is a logo's: player-written (D127)
        }
    }

    private static void Fail(string what, Exception ex)
    {
        if (_failures++ < MaxLoggedFailures)
        {
            ModLog.Error($"Custom content: {what} failed", ex);
        }
    }

    /// <summary>
    /// An id from the host, short and safe for a log line or the screen: as it is (cut) when it has an identifier's shape (every
    /// honest host's does: the game gives custom content GUIDs), else its length only (D146).
    /// </summary>
    private static string Quote(string? text) => $"\"{(string.IsNullOrEmpty(text) || HostText.IsKey(text) ? SafeText.Clean(text, 48) : HostText.KeyForLog(text))}\"";

    /// <summary>
    /// A custom item's key for a log line: a logo's key is its file name (player-written), so only its shape; a team's,
    /// skater's or goalie's is the game's id, quoted.
    /// </summary>
    private static string LogKey(CustomContentKind kind, string key) =>
        kind is CustomContentKind.Logo or CustomContentKind.RunLogo ? LogNames.TextShape(key) : Quote(key);

    /// <summary>The host's own custom teams, skaters and goalies by id, as the game keeps them (read once per match load).</summary>
    private sealed class HostCustoms
    {
        public Dictionary<string, TeamDataModel> Teams { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ForwardDataModel> Skaters { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, GoalieDataModel> Goalies { get; } = new(StringComparer.Ordinal);

        public static HostCustoms Read()
        {
            var customs = new HostCustoms();
            try
            {
                Il2CppSystem.Collections.Generic.List<TeamDataModel>? teams = Injected<ITeamRepository>()?.TryCast<TeamRepository>()?.m_customTeamRepository?.LoadCustomTeams();
                for (int i = 0; teams != null && i < teams.Count; i++)
                {
                    if (teams[i] is TeamDataModel team && !string.IsNullOrEmpty(team.Id))
                    {
                        LogNames.NoteCustom(team);
                        customs.Teams[team.Id] = team;
                    }
                }
            }
            catch (Exception ex)
            {
                // The game's list has a throwing path of its own (ICustomTeamsRepository.CustomTeamNotFoundException).
                ModLog.Warn($"Custom content: this PC's custom teams can't be listed ({ModLog.SafeErrorText(ex)}); they aren't sent.");
            }

            try
            {
                Il2CppSystem.Collections.Generic.List<ForwardDataModel>? skaters = Injected<IForwardRepository>()?.TryCast<ForwardRepository>()?.m_customForwardRepository?.LoadCustomForwards();
                for (int i = 0; skaters != null && i < skaters.Count; i++)
                {
                    if (skaters[i] is ForwardDataModel skater && !string.IsNullOrEmpty(skater.id))
                    {
                        LogNames.NoteCustom(skater);
                        customs.Skaters[skater.id] = skater;
                    }
                }

                Il2CppSystem.Collections.Generic.List<GoalieDataModel>? goalies = Injected<IGoalieRepository>()?.TryCast<GoalieRepository>()?._customGoalieRepository?.LoadCustomGoalies();
                for (int i = 0; goalies != null && i < goalies.Count; i++)
                {
                    if (goalies[i] is GoalieDataModel goalie && !string.IsNullOrEmpty(goalie.id))
                    {
                        LogNames.NoteCustom(goalie);
                        customs.Goalies[goalie.id] = goalie;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Custom content: this PC's custom skaters or goalies can't be listed ({ModLog.SafeErrorText(ex)}); they aren't sent.");
            }

            return customs;
        }
    }
}
#endif
