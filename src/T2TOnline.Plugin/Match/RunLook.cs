#if HAS_INTEROP
using System;
using Il2CppData;
using Il2CppState;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// The look of the host's campaign run team that its run file doesn't keep: the
/// away kit, the away goalie's kit, the three transition colours and the goalie's own colours. The game makes them at
/// each run load from the loading PC's squad team or custom campaign team (<c>CampaignState.InstantiateStartingTeam</c>),
/// and the run file then sets only the name, logo, player colours and home kits: so a friend's copy
/// of a host's run with a custom campaign team (or a friend with one of their own) wore the wrong away kit and transitions.
/// <list type="bullet">
/// <item>Host, a shared campaign: the run team's look as a custom content item (<see cref="RunLookData"/>), sent with the run
/// logo (once a second it's looked at again; a changed look goes again).</item>
/// <item>Friend: the newest look is put on its copy of the host's run when it arrives and after every load of a run of the
/// host's (<see cref="RunSync.HostRunLoaded"/>), with new colour objects, and read back. A campaign match plays with the
/// run's team itself, so it has the look too. Never saved (a friend saves no run).</item>
/// </list>
/// Nothing here throws into the game: failures are logged, and the friend's copy keeps the look its own game made.
/// </summary>
internal static class RunLook
{
    private static RunLookData? _sent;
    private static CustomItem? _item;
    private static uint _serial;
    private static RunLookData? _held;
    private static bool _worn;
    private static int _failures;

    /// <summary>
    /// Host: the run team's look as an item for friends (null without a run team). Made again only when the look changes, each
    /// time with a new number: a look that comes back to an earlier one (a run start left for another squad, then the first
    /// squad again) is a new item, so it reaches friends (custom content goes once per item).
    /// </summary>
    public static CustomItem? Item(CampaignState? campaign)
    {
        TeamData? team = campaign?.runData?.teamData;
        if (team == null)
        {
            _sent = null;
            _item = null;
            return null;
        }

        RunLookData look = Read(team);
        if (_sent != null && _item != null && _sent.FirstDifference(look) == null)
        {
            return _item;
        }

        try
        {
            RunLookData numbered = look.WithSerial(++_serial);
            _item = numbered.ToItem();
            _sent = numbered;
            ModLog.Info($"Run look: the run team's look beyond its run file ({look}) goes to friends.");
        }
        catch (ArgumentException ex)
        {
            _item = null;
            _sent = null;
            ModLog.Warn($"Run look: the run team's look can't be sent ({ModLog.SafeErrorText(ex)}); friends' copies wear their own game's.");
        }

        return _item;
    }

    /// <summary>Friend: a session started; each later load of the host's run gets the host's look.</summary>
    public static void StartFriend()
    {
        StopFriend();
        RunSync.HostRunLoaded += Loaded;
    }

    /// <summary>Friend: the session ended; the look is forgotten (the friend's own run comes back with its own).</summary>
    public static void StopFriend()
    {
        RunSync.HostRunLoaded -= Loaded;
        _held = null;
        _worn = false;
        _failures = 0;
    }

    /// <summary>Friend: the host's run team's look arrived: kept, and put on the run held here now.</summary>
    public static void Received(CustomItem item)
    {
        if (!RunLookData.TryRead(item.Bytes, out RunLookData? look) || look == null)
        {
            ModLog.Warn($"Run look: {LogNames.HostTag()}'s run team's look can't be read here; this PC's copy of the run wears its own game's.");
            return;
        }

        _held = look;
        _worn = false;
        Wear("it arrived");
    }

    /// <summary>The look of <paramref name="team"/> as plain data.</summary>
    public static RunLookData Read(TeamData team) => new()
    {
        Away = SkaterModels.Schemes(team.awayColors),
        AwayGoalie = SkaterModels.Schemes(team.awayGoalieColors),
        PrimaryTransition = SkaterModels.Colour(team.primaryColorTransition),
        SecondaryTransition = SkaterModels.Colour(team.secondaryColorTransition),
        TertiaryTransition = SkaterModels.Colour(team.tertiaryColorTransition),
        Goalie = team.goalie == null ? null : SkaterModels.Schemes(team.goalie.colorSchemes),
    };

    private static void Loaded(uint checkpoint) => Wear(checkpoint == 0 ? "after the run-start preview's load" : $"after run {checkpoint}'s load");

    /// <summary>Puts the held look on the host's run held here (a kit the host's team has none of is left as it is), and reads it back.</summary>
    private static void Wear(string when)
    {
        RunLookData? look = _held;
        TeamData? team = RunSync.Instance?.HoldingHostRun == true ? CampaignGame.State()?.runData?.teamData : null;
        if (look == null || team == null)
        {
            return;
        }

        try
        {
            if (look.Away != null)
            {
                team.awayColors = SkaterModels.Schemes(look.Away)!;
            }

            if (look.AwayGoalie != null)
            {
                team.awayGoalieColors = SkaterModels.Schemes(look.AwayGoalie)!;
            }

            team.primaryColorTransition = SkaterModels.Colour(look.PrimaryTransition);
            team.secondaryColorTransition = SkaterModels.Colour(look.SecondaryTransition);
            team.tertiaryColorTransition = SkaterModels.Colour(look.TertiaryTransition);
            if (look.Goalie != null && team.goalie != null)
            {
                team.goalie.colorSchemes = SkaterModels.Schemes(look.Goalie)!;
            }

            RunLookData now = Read(team);
            var expected = new RunLookData
            {
                Away = look.Away ?? now.Away,
                AwayGoalie = look.AwayGoalie ?? now.AwayGoalie,
                PrimaryTransition = look.PrimaryTransition,
                SecondaryTransition = look.SecondaryTransition,
                TertiaryTransition = look.TertiaryTransition,
                Goalie = team.goalie == null ? null : look.Goalie ?? now.Goalie,
            };
            string? differs = expected.FirstDifference(now);
            if (differs != null)
            {
                Fail($"its {differs} didn't read back as written ({when})");
                return;
            }

            if (!_worn)
            {
                _worn = true;
                ModLog.Info($"Run look: {LogNames.HostTag()}'s run team's look beyond its run file ({look}) is on this PC's copy of the host's run ({when}); each later load of the host's run gets it too.");
            }
        }
        catch (Exception ex)
        {
            Fail($"{ModLog.SafeErrorText(ex)} ({when})");
        }
    }

    private static void Fail(string why)
    {
        if (++_failures <= 3)
        {
            ModLog.Warn($"Run look: {LogNames.HostTag()}'s run team's look couldn't be put on this PC's copy of the run: {why}; it wears its own game's.");
        }
    }
}
#endif
