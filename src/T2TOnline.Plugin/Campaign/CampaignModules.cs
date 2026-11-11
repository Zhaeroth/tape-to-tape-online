#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Online;

namespace T2TOnline.Plugin.Campaign;

/// <summary>Tells the player something through the notices' one router (<see cref="NoticeId"/>; <c>OnlineSession.Notify</c>). <paramref name="args"/> are its word's arguments, or a log-only notice's text.</summary>
/// <param name="id">Which notice.</param>
/// <param name="args">The arguments.</param>
internal delegate void NoticeSink(NoticeId id, params string[] args);

/// <summary>
/// Tells the player something about <paramref name="who"/> (<c>OnlineSession.NotifyAbout</c>): the word's first argument is
/// their <paramref name="name"/> on the screen, their tag in the log's copy (D127).
/// </summary>
/// <param name="who">The player's Steam ID.</param>
/// <param name="id">Which notice.</param>
/// <param name="name">The player's name, for the screen.</param>
internal delegate void NoticeAboutSink(ulong who, NoticeId id, string name);

/// <summary>What the campaign modules get from the runtime when they're made.</summary>
/// <param name="Matches">The match half of the session (campaign matches stream through it).</param>
/// <param name="UserData">The mod's folder in UserData (<c>UserData/T2TOnline</c>).</param>
/// <param name="Notify">Tells the player something: a native toast or prompt, else the status box.</param>
/// <param name="NotifyAbout">Tells the player something about a player: their name on the screen, their tag in the log.</param>
internal sealed record CampaignContext(MatchRelay Matches, string UserData, NoticeSink Notify, NoticeAboutSink NotifyAbout);

/// <summary>
/// The campaign's parts. Each part adds its module and its patches in its own file,
/// by implementing its two partial methods here; a part with no implementation simply isn't there. Order matters only for
/// <see cref="Create"/>: modules get each call in this order (run sync first, so every other part sees the run), except
/// that a friend's session end stops them in reverse (run sync last: it puts the friend's own run back after the others,
/// e.g. shared progress, have used the host's run).
/// </summary>
internal static partial class CampaignModules
{
    /// <summary>Every module, in order.</summary>
    public static List<ICampaignModule> Create(CampaignContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var modules = new List<ICampaignModule>();
        AddRunSync(modules, context);
        AddScenes(modules, context);
        AddMatches(modules, context);
        AddMenus(modules, context);
        AddProgress(modules, context);
        AddHostDecides(modules, context);
        return modules;
    }

    /// <summary>
    /// Installs every part's patches at start-up (IL2CPP detours can't be removed, D38: they stay installed and do
    /// nothing outside a campaign session). Adds a line to <paramref name="failed"/> for each patch that couldn't go in;
    /// returns true when all went in.
    /// </summary>
    public static bool Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(failed);
        int before = failed.Count;
        InstallRunSync(harmony, failed);
        InstallScenes(harmony, failed);
        InstallMatches(harmony, failed);
        InstallMenus(harmony, failed);
        InstallProgress(harmony, failed);
        InstallHostDecides(harmony, failed);
        return failed.Count == before;
    }

    // Run sync, co-op save slots, save safety, backups (Campaign/RunSync.cs, CoopSlots.cs).
    static partial void AddRunSync(List<ICampaignModule> modules, CampaignContext context);

    static partial void InstallRunSync(HarmonyLib.Harmony harmony, List<string> failed);

    // Following scenes, the world map (Campaign/Scenes/*.cs).
    static partial void AddScenes(List<ICampaignModule> modules, CampaignContext context);

    static partial void InstallScenes(HarmonyLib.Harmony harmony, List<string> failed);

    // Campaign matches (Match/CampaignMatch.cs).
    static partial void AddMatches(List<ICampaignModule> modules, CampaignContext context);

    static partial void InstallMatches(HarmonyLib.Harmony harmony, List<string> failed);

    // Menus as views (Campaign/Menus/*.cs).
    static partial void AddMenus(List<ICampaignModule> modules, CampaignContext context);

    static partial void InstallMenus(HarmonyLib.Harmony harmony, List<string> failed);

    // Profile progress for every player (Campaign/Progress/*.cs).
    static partial void AddProgress(List<ICampaignModule> modules, CampaignContext context);

    static partial void InstallProgress(HarmonyLib.Harmony harmony, List<string> failed);

    // The host is the deciding player (Campaign/HostDecides.cs).
    static partial void AddHostDecides(List<ICampaignModule> modules, CampaignContext context);

    static partial void InstallHostDecides(HarmonyLib.Harmony harmony, List<string> failed);
}
#endif
