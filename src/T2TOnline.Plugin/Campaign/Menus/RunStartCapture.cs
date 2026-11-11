#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppData;
using Il2CppRogue;
using Il2CppRogue.Rewards;
using Il2CppRogue.Stickers;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The host's side of the run-start menus:
/// <list type="bullet">
/// <item>The old squad menu (the superstar pick, or the old school squad's draft): what each of its own rolls returned
/// (<see cref="Offered"/>, from the four generators' postfixes); once the mode's last roll is in, its run preview
/// (<see cref="RunPreview"/>) and those offers go to friends as its <see cref="MenuAttachmentKind.RunPreview"/> attachment,
/// so their games open the real menu with the host's run and the host's offers. Each pick the host makes (its own click
/// handlers' postfixes) goes as a step value (<see cref="Picked"/>). One that can't be sent leaves the menu as text on
/// friends, with why (its data's <see cref="ProblemKey"/>).</item>
/// <item>The lineup with benchwarmers: once the menu has loaded its team and benchwarmers (its <c>LoadCurrentTeam</c>), its
/// run preview and its benchwarmers as the host's game built them (<see cref="BenchLoaded"/>), so friends' games open the
/// real menu with the host's run and pool (its cards follow in its data, <see cref="BenchViews"/>); and, for a friend that
/// shows it as text (the fallback), the lineup, the bench and the benchwarmers as lines (<see cref="Bench"/>).</item>
/// </list>
/// Only reads the game: nothing here changes the host's run or its menus. Never throws into a patch: the patches guard it.
/// </summary>
internal static class RunStartCapture
{
    /// <summary>The old squad menu's class (the superstar pick and the old school draft).</summary>
    public const string OldSquad = "OldSquadMenu";

    /// <summary>The lineup-with-benchwarmers menu's class.</summary>
    public const string Benchwarmer = "BenchwarmerMenu";

    /// <summary>Value: the old squad menu's mode (<see cref="RunPreviewSet.FullSequence"/> or <see cref="RunPreviewSet.SuperStarOnly"/>).</summary>
    public const string ModeKey = "runstart.mode";

    /// <summary>Value: why the old squad menu shows as text on friends (absent when its run preview goes).</summary>
    public const string ProblemKey = "runstart.problem";

    /// <summary>Value group: the host picked a power-up (the value is its index).</summary>
    public const string PowerUpStep = "oldsquad.powerUp";

    /// <summary>Value group: the host picked a superstar (the value is its index).</summary>
    public const string SuperStarStep = "oldsquad.superStar";

    /// <summary>Value group: the host picked a relic (the value is its index).</summary>
    public const string RelicStep = "oldsquad.relic";

    /// <summary>Value group: the host's menu applied its picks and is leaving (the value is its skater choice, or 0).</summary>
    public const string ExitStep = "oldsquad.exit";

    private const int MaxMenus = 4;
    private const int RowChars = 150;
    private const int MaxPoolLines = 4;

    private static readonly List<Pending> Menus = new();

    /// <summary>The old squad menu's rolls, in the order its <c>Show</c> makes them.</summary>
    public enum Offer
    {
        /// <summary><c>GenerateSuperStarSkaters</c> (both modes).</summary>
        SuperStars,

        /// <summary><c>GeneratePowerUpRewards</c> (the old school draft).</summary>
        PowerUps,

        /// <summary><c>GenerateRandomSkaters</c> (the old school draft).</summary>
        RandomSkaters,

        /// <summary><c>GenerateRelics</c> (the old school draft, its last roll).</summary>
        Relics,
    }

    /// <summary>Forgets every menu (hosting stopped) and deletes the preview file.</summary>
    public static void Clear()
    {
        Menus.Clear();
        RunPreview.CleanUp("hosting stopped");
    }

    /// <summary>
    /// Host: one of the old squad menu's rolls returned <paramref name="list"/>. Once the last roll of the menu's mode is in,
    /// its run preview and offers are made and attached for friends (or why not is noted for its data). Only while this PC
    /// hosts a shared campaign (a solo run's menus aren't shared).
    /// </summary>
    public static void Offered(OldSquadMenu menu, Offer offer, Il2CppSystem.Object? list)
    {
        if (MenuViews.Instance is not { Hosting: true } views || RunSync.Instance?.HostOnCoopSlot != true)
        {
            return;
        }

        Pending pending = Track(menu);
        if (offer == Offer.SuperStars)
        {
            // Its first roll in both modes: a new opening (a menu shown again starts over).
            pending.Lists.Clear();
            pending.Done = false;
            pending.Problem = null;
        }

        pending.Lists[offer] = list;
        bool superStarOnly = (int)menu.m_Mode == RunPreviewSet.SuperStarOnly;
        bool complete = superStarOnly ? offer == Offer.SuperStars : offer == Offer.Relics && pending.Lists.Count == 4;
        if (!complete || pending.Done)
        {
            return;
        }

        pending.Done = true;
        RunPreviewSet? set;
        string problem, made;
        try
        {
            set = Make(menu, pending, out problem, out made);
        }
        catch (Exception ex)
        {
            // The game's own code threw (its save's serializer, a skater's model): friends show the menu as text, with why, at
            // once (not after waiting for data that won't come), and it isn't counted as a patch failure.
            set = null;
            made = string.Empty;
            problem = $"an error while making it ({ModLog.SafeErrorText(ex)})";
        }

        if (set != null)
        {
            string about = $"{set}; {made}";
            if (views.Host.Attach(menu, MenuAttachmentKind.RunPreview, serial => MenuAttachment.RunPreview(serial, set), about, views.HasFriends, out problem))
            {
                ModLog.Info($"Menus: the host's {OldSquad} is {Words(set.Mode)}: its run ({made}) and its offers go to friends for the real menu ({set})" +
                    (views.HasFriends ? "." : "; no friend is here yet, so they wait for one who joins while it's up."));
                return;
            }
        }

        pending.Problem = $"the host's run start data can't be sent ({problem})";
        ModLog.Warn($"Menus: the host's {OldSquad} goes to friends as text: {pending.Problem}.");
        views.Host.Reread(menu, views.HasFriends);
    }

    /// <summary>
    /// Host: the lineup menu loaded its team and benchwarmers (its <c>LoadCurrentTeam</c>, once per opening, inside its
    /// <c>Show</c>; its random fill and its reveals come after). Its run preview and its benchwarmers as this game built them
    /// (this profile's snapshots, quest talents, unlocks and hot or cold status) go to friends as its
    /// <see cref="MenuAttachmentKind.RunPreview"/> attachment, and its data is read again now (its cards are its lineup's from
    /// here on). One that can't be sent leaves the menu as text on friends, with why. Only while this PC hosts a shared campaign.
    /// </summary>
    public static void BenchLoaded(BenchwarmerMenu menu)
    {
        if (MenuViews.Instance is not { Hosting: true } views || RunSync.Instance?.HostOnCoopSlot != true)
        {
            return;
        }

        Pending pending = Track(menu);
        pending.Lists.Clear();
        pending.Done = true;
        pending.Problem = null;
        pending.Loaded = true;
        RunPreviewSet? set;
        string problem, made;
        try
        {
            set = MakeBench(menu, out problem, out made);
        }
        catch (Exception ex)
        {
            // The game's own code threw (its save's serializer, a skater's model): friends show the menu as text, with why.
            set = null;
            made = string.Empty;
            problem = $"an error while making it ({ModLog.SafeErrorText(ex)})";
        }

        if (set != null)
        {
            string about = $"{set}; {made}";
            if (views.Host.Attach(menu, MenuAttachmentKind.RunPreview, serial => MenuAttachment.RunPreview(serial, set), about, views.HasFriends, out problem))
            {
                ModLog.Info($"Menus: the host's {Benchwarmer} is the lineup: its run ({made}) and its {set.Pool.Count} benchwarmer(s) go to friends for the real menu ({set})" +
                    (views.HasFriends ? "." : "; no friend is here yet, so they wait for one who joins while it's up."));
                views.Host.Changed(menu, views.HasFriends); // its cards, now that they're its lineup's
                return;
            }
        }

        pending.Problem = $"the host's run start data can't be sent ({problem})";
        ModLog.Warn($"Menus: the host's {Benchwarmer} goes to friends as text: {pending.Problem}.");
        views.Host.Reread(menu, views.HasFriends);
    }

    /// <summary>True once the host's lineup menu <paramref name="menu"/> has loaded its team and benchwarmers.</summary>
    public static bool Loaded(AbstractMenu menu) => Find(menu)?.Loaded == true;

    /// <summary>Host: the old squad menu took a pick (its own click handler ran); it goes to friends as a step value.</summary>
    public static void Picked(OldSquadMenu menu, string step, int index)
    {
        if (MenuViews.Instance is { Hosting: true, HasFriends: true } views && index >= 0)
        {
            views.Host.Step(menu, step, 0, index);
        }
    }

    /// <summary>
    /// A run-start menu's values for its data: the old squad menu's mode, and why the menu is text on friends when it is (its
    /// run start data can't be sent).
    /// </summary>
    public static void Values(AbstractMenu menu, List<KeyValuePair<string, NetValue>> values)
    {
        if (menu.TryCast<OldSquadMenu>() is { } oldSquad)
        {
            values.Add(new KeyValuePair<string, NetValue>(ModeKey, NetValue.FromInt((int)oldSquad.m_Mode)));
        }

        string? problem = Find(menu)?.Problem;
        if (!string.IsNullOrEmpty(problem))
        {
            values.Add(new KeyValuePair<string, NetValue>(ProblemKey, NetValue.FromString(MenuGame.Plain(problem, RowChars))));
        }
    }

    /// <summary>
    /// The lineup-with-benchwarmers menu as text (v1's panel): the lineup by position, the bench, and the benchwarmers
    /// (unlocked first, the locked marked), each with its overall rating, read from the menu's own roster as it is now (it
    /// changes with each of the host's swaps; the panel is read again as it changes).
    /// </summary>
    public static void Bench(BenchwarmerMenu menu, List<MenuItem> items)
    {
        TeamRosterData? roster = menu.Team;
        if (roster == null)
        {
            return;
        }

        var lineup = new List<string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string position, ForwardData? forward) in new[] { ("C", roster.m_C), ("LW", roster.m_LW), ("RW", roster.m_RW), ("LD", roster.m_LD), ("RD", roster.m_RD) })
        {
            lineup.Add(forward == null ? $"{position} (empty)" : $"{position} {Skater(forward)}");
            Take(forward);
        }

        Line(items, $"Lineup: {string.Join(" · ", lineup)}");
        var bench = new List<string>();
        Il2CppList.List<ForwardData>? benched = roster.m_BenchForwards;
        for (int i = 0; benched != null && i < benched.Count; i++)
        {
            if (benched[i] != null)
            {
                bench.Add(Skater(benched[i]));
                Take(benched[i]);
            }
        }

        Line(items, bench.Count > 0 ? $"Bench: {string.Join(" · ", bench)}" : "Bench: nobody");
        var unlocked = new List<string>();
        var locked = new List<string>();
        Il2CppList.List<SkaterDataForUI>? pool = menu.m_BenchwarmersSkaters;
        for (int i = 0; pool != null && i < pool.Count; i++)
        {
            ForwardData? forward = pool[i]?.Skater?.TryCast<ForwardData>();
            if (forward == null || taken.Contains(forward.id ?? string.Empty))
            {
                continue;
            }

            if (menu.IsBenchwarmerUnlocked(forward.id))
            {
                unlocked.Add(Skater(forward));
            }
            else
            {
                locked.Add($"{Skater(forward)} (locked)");
            }
        }

        if (unlocked.Count + locked.Count == 0)
        {
            return;
        }

        // Several lines when the pool is long (a row of the panel holds about a line's worth), at most a few.
        var lines = new List<string>();
        var line = new List<string>();
        int length = 0;
        foreach (string skater in unlocked.Concat(locked))
        {
            if (line.Count > 0 && length + skater.Length + 3 > RowChars - 40)
            {
                lines.Add(string.Join(" · ", line));
                line.Clear();
                length = 0;
            }

            line.Add(skater);
            length += skater.Length + 3;
        }

        lines.Add(string.Join(" · ", line));
        for (int i = 0; i < lines.Count && i < MaxPoolLines; i++)
        {
            string more = i == MaxPoolLines - 1 && lines.Count > MaxPoolLines ? " · …" : string.Empty;
            Line(items, i == 0 ? $"Benchwarmers ({unlocked.Count} unlocked, {locked.Count} locked): {lines[i]}{more}" : $"  {lines[i]}{more}");
        }

        void Take(ForwardData? forward)
        {
            if (forward?.id is { Length: > 0 } id)
            {
                taken.Add(id);
            }
        }
    }

    /// <summary>"B. Smith #17 (74)": a skater with the overall rating its card shows.</summary>
    private static string Skater(ForwardData forward)
    {
        int overall = T2TUtils.GetOverall(forward.DisplaySpeed, forward.DisplayShotAccuracy, forward.DisplayShotPower, forward.DisplayChecking);
        return $"{MenuGame.Plain(forward.FullName, 40)} #{forward.number} ({overall})";
    }

    private static void Line(List<MenuItem> items, string text)
    {
        if (items.Count < MenuData.MaxItems)
        {
            items.Add(new MenuItem(MenuPayloads.Line, new[] { new KeyValuePair<string, NetValue>("text", NetValue.FromString(MenuGame.Plain(text, RowChars))) }));
        }
    }

    /// <summary>The menu's run preview and offers, or null with why. <paramref name="made"/>: the preview's size and summary, for the log.</summary>
    private static RunPreviewSet? Make(OldSquadMenu menu, Pending pending, out string problem, out string made)
    {
        made = string.Empty;
        Il2CppState.CampaignState? state = CampaignGame.State();
        if (state == null)
        {
            problem = "the game's campaign isn't there";
            return null;
        }

        byte[]? run = RunPreview.Make(state, RunPreview.PreviewPath, out problem, out double milliseconds);
        if (run == null)
        {
            problem = $"its run preview couldn't be made: {problem}";
            return null;
        }

        made = string.Create(CultureInfo.InvariantCulture, $"{run.Length / 1024.0:0.0} KB, made in {milliseconds:0.0} ms; {RunPreview.Summary(run)}");
        int mode = (int)menu.m_Mode;
        List<RunStartSkater>? superStars = Skaters(pending.Lists.GetValueOrDefault(Offer.SuperStars), "superstar", out problem);
        if (superStars == null)
        {
            return null;
        }

        var set = new RunPreviewSet
        {
            Menu = RunStartMenu.OldSquad,
            Mode = mode,
            IsTeamHome = menu.m_IsTeamHome,
            RunFile = run,
            SuperStars = superStars,
        };
        if (mode == RunPreviewSet.FullSequence)
        {
            List<RunStartSkater>? random = Skaters(pending.Lists.GetValueOrDefault(Offer.RandomSkaters), "random skater", out problem);
            List<RunStartReward>? powerUps = random == null ? null : PowerUps(pending.Lists.GetValueOrDefault(Offer.PowerUps), out problem);
            List<RunStartReward>? relics = powerUps == null ? null : Relics(pending.Lists.GetValueOrDefault(Offer.Relics), out problem);
            if (relics == null)
            {
                return null;
            }

            set = new RunPreviewSet
            {
                Menu = RunStartMenu.OldSquad,
                Mode = mode,
                IsTeamHome = menu.m_IsTeamHome,
                RunFile = run,
                SuperStars = superStars,
                RandomSkaters = random!,
                PowerUps = powerUps!,
                Relics = relics,
            };
        }

        problem = set.Problem() ?? string.Empty;
        return problem.Length == 0 ? set : null;
    }

    /// <summary>
    /// The lineup menu's run preview and benchwarmers (its benchwarmer cards in order, as they are once it has loaded them),
    /// or null with why. <paramref name="made"/>: the preview's size and summary, for the log.
    /// </summary>
    private static RunPreviewSet? MakeBench(BenchwarmerMenu menu, out string problem, out string made)
    {
        made = string.Empty;
        Il2CppState.CampaignState? state = CampaignGame.State();
        if (state == null)
        {
            problem = "the game's campaign isn't there";
            return null;
        }

        List<SkaterCard> cards = BenchViews.PoolCards(menu);
        if (cards.Count > RunPreviewSet.MaxPool)
        {
            problem = $"its {cards.Count} benchwarmers are more than {RunPreviewSet.MaxPool}";
            return null;
        }

        var pool = new List<RunStartBenchwarmer>(cards.Count);
        Il2CppRogue.BenchSnapshots.BenchSnapshotService? snapshots = menu.m_BenchSnapshotService;
        for (int i = 0; i < cards.Count; i++)
        {
            ForwardData? forward = cards[i].m_CurrentSkater?.Skater?.TryCast<ForwardData>();
            string why = "its card shows nobody";
            RunStartSkater? skater = forward == null ? null : SkaterModels.Read(forward, out why);
            if (skater == null)
            {
                problem = $"its benchwarmer {i + 1} can't be read ({why})";
                return null;
            }

            string? power = forward!.benchPower?.ID;
            pool.Add(new RunStartBenchwarmer(skater, Unlocked: !cards[i].m_IsLocked, Hot: snapshots?.IsHotBenchwarmer(forward) == true,
                Cold: snapshots?.IsColdBenchwarmer(forward) == true, BenchPowerId: string.IsNullOrEmpty(power) ? null : power));
        }

        byte[]? run = RunPreview.Make(state, RunPreview.PreviewPath, out problem, out double milliseconds);
        if (run == null)
        {
            problem = $"its run preview couldn't be made: {problem}";
            return null;
        }

        made = string.Create(CultureInfo.InvariantCulture, $"{run.Length / 1024.0:0.0} KB, made in {milliseconds:0.0} ms; {RunPreview.Summary(run)}");
        var set = new RunPreviewSet
        {
            Menu = RunStartMenu.Benchwarmer,
            Mode = 0,
            IsTeamHome = menu.m_IsHomeTeam,
            RunFile = run,
            Pool = pool,
        };
        problem = set.Problem() ?? string.Empty;
        return problem.Length == 0 ? set : null;
    }

    private static List<RunStartSkater>? Skaters(Il2CppSystem.Object? list, string what, out string problem)
    {
        Il2CppList.List<ForwardData>? forwards = list?.TryCast<Il2CppList.List<ForwardData>>();
        var skaters = new List<RunStartSkater>();
        for (int i = 0; forwards != null && i < forwards.Count; i++)
        {
            string why = "it's missing";
            RunStartSkater? skater = forwards[i] == null ? null : SkaterModels.Read(forwards[i], out why);
            if (skater == null)
            {
                problem = $"its {what} {i + 1} can't be read ({why})";
                return null;
            }

            skaters.Add(skater);
        }

        problem = string.Empty;
        return skaters;
    }

    private static List<RunStartReward>? PowerUps(Il2CppSystem.Object? list, out string problem)
    {
        Il2CppList.List<PowerUpReward>? rewards = list?.TryCast<Il2CppList.List<PowerUpReward>>();
        var read = new List<RunStartReward>();
        for (int i = 0; rewards != null && i < rewards.Count; i++)
        {
            PowerUp? power = rewards[i]?.powerUp;
            RunStartRewardKind kind = power?.TryCast<Talent>() != null ? RunStartRewardKind.Talent : power?.TryCast<Ability>() != null ? RunStartRewardKind.Ability : RunStartRewardKind.None;
            if (power == null || kind == RunStartRewardKind.None)
            {
                problem = $"its power-up {i + 1} is {(power == null ? "missing" : $"a {MenuGame.TypeName(power)}")}";
                return null;
            }

            read.Add(new RunStartReward { Kind = kind, Id = power.id ?? string.Empty, Level = power.level, Stickers = Stickers(rewards[i]) });
        }

        problem = string.Empty;
        return read;
    }

    private static List<RunStartReward>? Relics(Il2CppSystem.Object? list, out string problem)
    {
        Il2CppList.List<RelicReward>? rewards = list?.TryCast<Il2CppList.List<RelicReward>>();
        var read = new List<RunStartReward>();
        for (int i = 0; rewards != null && i < rewards.Count; i++)
        {
            Relic? relic = rewards[i]?.relic;
            if (relic == null)
            {
                problem = $"its relic {i + 1} is missing";
                return null;
            }

            read.Add(new RunStartReward { Kind = RunStartRewardKind.Relic, Id = relic.id ?? string.Empty, Level = relic.level, Stickers = Stickers(rewards[i]) });
        }

        problem = string.Empty;
        return read;
    }

    private static List<RunStartSticker> Stickers(Reward? reward)
    {
        var read = new List<RunStartSticker>();
        Il2CppList.List<Sticker>? stickers = reward?.stickers;
        for (int i = 0; stickers != null && i < stickers.Count && read.Count < RunStartReward.MaxStickers; i++)
        {
            if (stickers[i] != null)
            {
                read.Add(new RunStartSticker((int)stickers[i].Stat, stickers[i].Value));
            }
        }

        return read;
    }

    private static string Words(int mode) => mode == RunPreviewSet.SuperStarOnly ? "the superstar pick" : "the old school draft";

    private static Pending Track(AbstractMenu menu)
    {
        Pending? known = Find(menu);
        if (known != null)
        {
            return known;
        }

        if (Menus.Count >= MaxMenus)
        {
            Menus.RemoveAt(0);
        }

        var pending = new Pending(menu.GetInstanceID());
        Menus.Add(pending);
        return pending;
    }

    /// <summary>
    /// The state kept for <paramref name="menu"/>, or null. Kept by the menu's Unity instance id, which no later object gets while the
    /// game runs: these menus aren't kept between openings, and a native address is handed out again once a closed menu's object is
    /// collected, so a new menu at that address would read the old one's problem (a friend's copy then showing as text).
    /// </summary>
    private static Pending? Find(AbstractMenu menu)
    {
        int id = menu.GetInstanceID();
        foreach (Pending pending in Menus)
        {
            if (pending.MenuId == id)
            {
                return pending;
            }
        }

        return null;
    }

    /// <summary>One run-start menu's rolls so far, whether its set was made (once per opening), and why it can't go when it can't.</summary>
    private sealed class Pending
    {
        public Pending(int menuId) => MenuId = menuId;

        /// <summary>The menu's Unity instance id.</summary>
        public int MenuId { get; }

        public Dictionary<Offer, Il2CppSystem.Object?> Lists { get; } = new();

        public bool Done { get; set; }

        public string? Problem { get; set; }

        /// <summary>The lineup menu has loaded its team and benchwarmers (its cards are its lineup's from then on).</summary>
        public bool Loaded { get; set; }
    }
}
#endif
