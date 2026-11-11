#if HAS_INTEROP
using System;
using System.Linq;
using Il2CppData;
using Il2CppRogue;
using Il2CppRogue.BenchPowers;
using Il2CppRogue.BenchPowers.Repository;
using Il2CppRogue.Powerups.Repository;
using Il2CppRogue.Relics.Repository;
using Il2CppRogue.Rewards;
using Il2CppRogue.Stickers;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A friend's side of a menu's data: the game's own menu data rebuilt from the host's (<see cref="MenuPayloads"/>)
/// against this PC's copy of the host's run and its own repositories, with the menu's actions off
/// (<c>applyChoice = false</c>, <c>DoNotSetChoice = true</c>; D43). Nothing here rolls: every offer is looked up by the
/// id the host's roll picked. Each piece of the host's data is checked against this PC's own: an offer this game doesn't
/// have, or a skater who isn't in the run, fails the rebuild (with why) rather than showing something else.
/// </summary>
internal static class MenuRebuild
{
    private const int MaxRerolls = 99;
    private const int MaxIdChars = 96;

    /// <summary>
    /// The data to open a menu of class <paramref name="type"/> with, or null with <paramref name="problem"/> (for the log and
    /// the panel). <paramref name="attachment"/>: what the host sent with the menu, if anything (a run-end menu's run summary).
    /// </summary>
    public static Il2CppSystem.Object? Metadata(string type, MenuData data, MenuAttachment? attachment, out string problem)
    {
        problem = string.Empty;
        return type switch
        {
            MenuPayloads.RelicReward => Relics(data, out problem),
            MenuPayloads.PowerUpReward => PowerUps(data, out problem),
            MenuPayloads.ChooseMeta => new ChooseMetaMenu.Metadata { DoNotSetChoice = true },
            MenuPayloads.RunEnd => RunEndView.Metadata(data, attachment, out problem),
            _ => Fail<Il2CppSystem.Object>($"this version doesn't rebuild a {type}", out problem),
        };
    }

    /// <summary>The relic reward menu's data, with the host's offers and <c>applyChoice = false</c>.</summary>
    public static RelicRewardMenu.Metadata? Relics(MenuData data, out string problem)
    {
        TeamData? team = Team(out problem);
        Il2CppList.List<RelicReward>? rewards = team == null ? null : RelicRewards(data, out problem);
        if (team == null || rewards == null || !TryCategory(data, out RelicCategory category, out problem))
        {
            return null;
        }

        var metadata = new RelicRewardMenu.Metadata(
            team,
            data.Flag("isTeamHome", true),
            new Il2CppList.IReadOnlyList<RelicReward>(rewards.Pointer),
            Rerolls(data),
            false,
            category);
        string term = data.Text("titleTerm");
        if (term.Length > 0)
        {
            metadata.TitleTerm = term;
        }

        metadata.ForcedMatchPick = data.Flag("forcedMatchPick");
        return metadata;
    }

    /// <summary>The power-up reward menu's data, with the host's offers, skater choices and forced skater, and <c>applyChoice = false</c>.</summary>
    public static SkaterPowerupRewardMenu.Metadata? PowerUps(MenuData data, out string problem)
    {
        TeamData? team = Team(out problem);
        if (team == null || !TryPowerUpLists(data, team, out Offers offers, out problem) || !TryCategory(data, out TalentCategory category, out problem))
        {
            return null;
        }

        Il2CppList.IReadOnlyList<SkaterData>? skaters = offers.Skaters == null ? null : new Il2CppList.IReadOnlyList<SkaterData>(offers.Skaters.Pointer);
        SkaterPowerupRewardMenu.Metadata metadata = offers.PowerUps == null && offers.Bench != null
            ? new SkaterPowerupRewardMenu.Metadata(team, data.Flag("isHomeTeam", true), new Il2CppList.IReadOnlyList<BenchPowerReward>(offers.Bench.Pointer), skaters!, Rerolls(data), false, category)
            : new SkaterPowerupRewardMenu.Metadata(team, data.Flag("isHomeTeam", true), offers.PowerUps == null ? null! : new Il2CppList.IReadOnlyList<PowerUpReward>(offers.PowerUps.Pointer), skaters!, Rerolls(data), false, category, offers.Forced!);

        // The host's menu may have had both lists, or neither: set them as it had them.
        metadata.PowerUpRewards = offers.PowerUps == null ? null! : new Il2CppList.IReadOnlyList<PowerUpReward>(offers.PowerUps.Pointer);
        metadata.BenchRewards = offers.Bench == null ? null! : new Il2CppList.IReadOnlyList<BenchPowerReward>(offers.Bench.Pointer);
        return metadata;
    }

    /// <summary>The host's relic offers, rebuilt from this PC's relic list (clones, as the game's own roll makes them).</summary>
    public static Il2CppList.List<RelicReward>? RelicRewards(MenuData data, out string problem)
    {
        problem = string.Empty;
        IRelicRepository? repository = MenuGame.Repository<IRelicRepository>(p => new IRelicRepository(p));
        if (repository == null)
        {
            return Fail<Il2CppList.List<RelicReward>>("the game's relic list isn't loaded", out problem);
        }

        var rewards = new Il2CppList.List<RelicReward>();
        foreach (MenuItem row in data.Group(MenuPayloads.Relic))
        {
            string id = row.Text("id");
            int level = (int)row.Whole("level");
            Relic? relic = IsId(id) ? repository.GetRelic(id, true) : null;
            if (relic != null && relic.level != level)
            {
                relic = repository.GetRelic(relic.GetIl2CppType(), level, true);
            }

            if (relic == null || relic.level != level)
            {
                return Fail<Il2CppList.List<RelicReward>>($"the relic \"{Shown(id)}\" (level {level}) isn't in this game", out problem);
            }

            var reward = new RelicReward(relic);
            AddStickers(reward, row.Text("stickers"));
            rewards.Add(reward);
        }

        return rewards.Count == 0 ? Fail<Il2CppList.List<RelicReward>>("the host's menu offered no relics", out problem) : rewards;
    }

    /// <summary>
    /// The host's power-up offers (talents, abilities, bench powers), skater choices (by run skater id, from the run's
    /// team) and forced skater. A list the host's menu didn't have stays null.
    /// </summary>
    public static bool TryPowerUpLists(MenuData data, TeamData team, out Offers offers, out string problem)
    {
        offers = default;
        problem = string.Empty;
        Il2CppList.List<PowerUpReward>? powerUps = null;
        Il2CppList.List<BenchPowerReward>? bench = null;
        Il2CppList.List<SkaterData>? skaters = null;
        if (data.Flag("hasPowerUps"))
        {
            ITalentRepository? talents = MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p));
            IAbilityRepository? abilities = MenuGame.Repository<IAbilityRepository>(p => new IAbilityRepository(p));
            powerUps = new Il2CppList.List<PowerUpReward>();
            foreach (MenuItem row in data.Group(MenuPayloads.PowerUp))
            {
                PowerUp? power = PowerUpOf(row, talents, abilities, out problem);
                if (power == null)
                {
                    return false;
                }

                var reward = new PowerUpReward(power);
                AddStickers(reward, row.Text("stickers"));
                powerUps.Add(reward);
            }
        }

        if (data.Flag("hasBench"))
        {
            IBenchPowerRepository? repository = MenuGame.Repository<IBenchPowerRepository>(p => new IBenchPowerRepository(p));
            bench = new Il2CppList.List<BenchPowerReward>();
            foreach (MenuItem row in data.Group(MenuPayloads.Bench))
            {
                string id = row.Text("id");
                BenchPower? power = repository != null && IsId(id) ? repository.GetBenchPower(id, true) : null;
                if (power == null)
                {
                    problem = $"the bench power \"{Shown(id)}\" isn't in this game";
                    return false;
                }

                var reward = new BenchPowerReward(power);
                AddStickers(reward, row.Text("stickers"));
                bench.Add(reward);
            }
        }

        if (data.Flag("hasSkaters"))
        {
            skaters = new Il2CppList.List<SkaterData>();
            foreach (MenuItem row in data.Group(MenuPayloads.Skater))
            {
                SkaterData? skater = SkaterOf(team, row.Text("id"));
                if (skater == null)
                {
                    problem = $"the skater \"{Shown(row.Text("id"))}\" isn't in the host's run here";
                    return false;
                }

                skaters.Add(skater);
            }
        }

        if ((powerUps?.Count ?? 0) + (bench?.Count ?? 0) == 0)
        {
            problem = "the host's menu offered nothing";
            return false;
        }

        string forcedId = data.Text("forcedSkater");
        ForwardData? forced = forcedId.Length == 0 ? null : team.GetForwardById(forcedId);
        if (forcedId.Length > 0 && forced == null)
        {
            problem = $"the forced skater \"{Shown(forcedId)}\" isn't in the host's run here";
            return false;
        }

        offers = new Offers(powerUps, bench, skaters, forced);
        return true;
    }

    /// <summary>The run's team on this PC (the host's run, when it follows), or null with why.</summary>
    public static TeamData? Team(out string problem)
    {
        problem = string.Empty;
        TeamData? team = MenuGame.Campaign()?.runData?.teamData;
        return team ?? Fail<TeamData>("the host's run has no team here", out problem);
    }

    /// <summary>The host's reroll count, within reason.</summary>
    public static int Rerolls(MenuData data) => (int)Math.Clamp(data.Whole("rerollCount"), 0, MaxRerolls);

    /// <summary>
    /// The host's offer category as this game's <typeparamref name="T"/>: one of its values (or, for a flags enum, a mix of
    /// them), else false with why. Never a number this game has no name for.
    /// </summary>
    private static bool TryCategory<T>(MenuData data, out T category, out string problem)
        where T : struct, Enum
    {
        category = default;
        problem = string.Empty;
        long raw = data.Whole("category");
        bool known = raw is >= int.MinValue and <= int.MaxValue && (typeof(T).IsDefined(typeof(FlagsAttribute), false)
            ? ((int)raw & ~Enum.GetValues<T>().Aggregate(0, (all, value) => all | Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture))) == 0
            : Enum.IsDefined(typeof(T), (int)raw));
        if (!known)
        {
            problem = $"the host's menu has a {typeof(T).Name} ({raw}) this game doesn't know";
            return false;
        }

        category = (T)Enum.ToObject(typeof(T), (int)raw);
        return true;
    }

    private static PowerUp? PowerUpOf(MenuItem row, ITalentRepository? talents, IAbilityRepository? abilities, out string problem)
    {
        problem = string.Empty;
        string id = row.Text("id");
        int level = (int)row.Whole("level");
        string kind = row.Text("kind");
        PowerUp? power = null;
        if (IsId(id) && kind == "talent" && talents != null)
        {
            Talent? talent = talents.GetTalent(id, true);
            if (talent != null && talent.level != level)
            {
                talent = talents.GetTalent(talent.GetIl2CppType(), level, true);
            }

            power = talent;
        }
        else if (IsId(id) && kind == "ability" && abilities != null)
        {
            Ability? ability = abilities.GetAbility(id, true);
            if (ability != null && ability.level != level)
            {
                ability = abilities.GetAbility(ability.GetIl2CppType(), level, true);
            }

            power = ability;
        }

        return power != null && power.level == level ? power : Fail<PowerUp>($"the {Shown(kind)} \"{Shown(id)}\" (level {level}) isn't in this game", out problem);
    }

    private static SkaterData? SkaterOf(TeamData team, string id)
    {
        if (!IsId(id))
        {
            return null;
        }

        Il2CppList.List<SkaterData>? all = team.GetAllSkaters();
        for (int i = 0; all != null && i < all.Count; i++)
        {
            if (all[i] != null && all[i].id == id)
            {
                return all[i];
            }
        }

        return null;
    }

    /// <summary>Adds the host's stickers ("Stat:value", comma-separated) to a rebuilt reward; unknown stats are left out.</summary>
    private static void AddStickers(Reward reward, string stickers)
    {
        if (stickers.Length == 0)
        {
            return;
        }

        reward.stickers ??= new Il2CppList.List<Sticker>();
        int added = 0;
        foreach (string part in stickers.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split(':');
            if (added < 8 && pair.Length == 2 && Enum.TryParse(pair[0], out Sticker.StatType stat) && Enum.IsDefined(stat) &&
                int.TryParse(pair[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) &&
                value is >= -999 and <= 999)
            {
                reward.stickers.Add(new Sticker(stat, value));
                added++;
            }
        }
    }

    /// <summary>An asset id as the host sent it: non-empty, short, no control characters (looked up, never shown raw).</summary>
    private static bool IsId(string id) => id.Length is > 0 and <= MaxIdChars;

    // A host's id in a reason (the log's, and the text panel's): as it is when shaped like one, else its length.
    private static string Shown(string id) => Core.Diagnostics.HostText.KeyForLog(id);

    private static T? Fail<T>(string why, out string problem)
        where T : class
    {
        problem = why;
        return null;
    }

    /// <summary>The power-up reward menu's lists, rebuilt (a list the host's menu didn't have is null).</summary>
    public readonly record struct Offers(
        Il2CppList.List<PowerUpReward>? PowerUps,
        Il2CppList.List<BenchPowerReward>? Bench,
        Il2CppList.List<SkaterData>? Skaters,
        ForwardData? Forced);
}
#endif
