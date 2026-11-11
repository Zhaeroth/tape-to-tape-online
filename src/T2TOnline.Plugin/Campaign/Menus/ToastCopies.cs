#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppData;
using Il2CppRogue;
using Il2CppRogue.Powerups.Repository;
using Il2CppRogue.Relics.Repository;
using Il2CppSkaters.Injuries;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// A run toast (the game's <c>NotificationData</c>) read on the host as a <see cref="NotificationCopy"/>, and made again from
/// one on a friend's PC with its own objects (<see cref="Toasts"/>).
/// <list type="bullet">
/// <item>Host: the team by which of the game's teams it is (the run's, the match's home or visitor), the skater by run
/// skater id, the injury by its type and games, the object (<c>PowerUpOrRelic</c>: a relic, talent or ability) by id and
/// level; with no object, the relic, talent or ability whose icon is the title's (the team's relics, then its skaters'
/// talents and abilities), for its texts only. Each text is its object's name or description when it says the same as the
/// object does here, else a translation term when it's one term's text in this language, else a name the game writes in the
/// player's language (a stat's, an injury's, a team's, a relic's) or a text the game formats from a term and arguments, as a recipe
/// (ToastCopies.Formats.cs), else the host's text; each image its object's icon, else its sprite's name.</item>
/// <item>Friend: the same team and skater from this PC's copy of the run or match, the injury made new, the object from the
/// team (else the game's repositories), the texts in this PC's language where they were made from a term or the object
/// (the host's text otherwise, or when this game lacks it), the images from the object or by sprite name among the sprites
/// this game has loaded (looked up once, the first time one's needed); a recipe made with this PC's terms and names. A toast whose
/// team, skater or object this PC lacks can't be made (null, with why); a missing image only goes without.</item>
/// </list>
/// Game thread only; the callers guard against exceptions.
/// </summary>
internal static partial class ToastCopies
{
    // The longest id or sprite name a copy takes (the wire's name limit).
    private const int MaxNameBytes = 128;

    // Most missing sprite names kept (a host's words, as many as it likes): only their count is ever read.
    private const int MaxMissingSprites = 256;

    private static readonly HashSet<string> MissingSprites = new(StringComparer.Ordinal);
    private static Dictionary<string, Sprite>? _sprites;
    private static int _spritesScene;

    /// <summary>Friend: how long the sprite index took to make (0 before it's needed), for the log.</summary>
    public static double SpriteIndexMs { get; private set; }

    /// <summary>Friend: sprites a toast named that this game hasn't loaded (each logged once).</summary>
    public static int MissingSpriteCount => MissingSprites.Count;

    /// <summary>Forgets the sprites looked up (following stopped: the next session's scenes load others).</summary>
    public static void Forget()
    {
        _sprites = null;
        MissingSprites.Clear();
        SpriteIndexMs = 0;
    }

    /// <summary>
    /// Host: <paramref name="data"/> as a copy for friends, or null with why (an object of a kind friends can't make).
    /// <paramref name="kept"/>: the texts left as the host's (words no recipe makes), each as "description (its shape; why)", for the log.
    /// </summary>
    public static NotificationCopy? Read(NotificationData data, out string problem, out IReadOnlyList<string> kept)
    {
        problem = string.Empty;
        kept = Array.Empty<string>();
        TeamData? team = data.SourceTeam;
        ToastTeam which = TeamOf(team);
        SkaterData? skater = data.SourceSkater;
        Injury? injury = data.NewInjury;
        ScriptableObject? own = data.PowerUpOrRelic;
        Thing? thing = null;
        if (own != null)
        {
            thing = Thing.Of(own);
            if (thing == null)
            {
                problem = $"its object ({SafeText.Clean(own.GetIl2CppType()?.Name, 48)}) isn't a relic, talent or ability";
                return null;
            }
        }

        Sprite? icon = data.TitleIcon;
        thing ??= icon == null ? null : Thing.WithIcon(icon, team ?? MenuGame.Campaign()?.runData?.teamData, skater);
        var names = new ToastNames(data, thing);
        var texts = new List<ToastText>();
        int slot = 0;
        foreach (string text in Strings(data.CustomTexts, NotificationCopy.MaxTexts))
        {
            texts.Add(Text(text, thing, names, $"text {++slot}"));
        }

        var images = new List<ToastImage>();
        foreach (Sprite sprite in Sprites(data.CustomImages, NotificationCopy.MaxImages))
        {
            if (Image(sprite, thing) is { } image)
            {
                images.Add(image);
            }
        }

        var copy = new NotificationCopy
        {
            Type = (int)data.Type,
            Timing = (int)data.Timing,
            Team = which,
            IsTeamHome = data.IsTeamHome,
            SkaterId = skater == null ? null : Id(skater.id),
            InjuryType = injury == null ? null : (int)injury.InjuryType,
            InjuryGames = injury == null ? 0 : Math.Clamp(injury.HealingTime, -1, NotificationCopy.MaxInjuryGames),
            ObjectKind = thing?.Kind ?? ToastObjectKind.None,
            ObjectId = thing?.Id ?? string.Empty,
            ObjectLevel = thing == null ? 0 : Math.Clamp(thing.Level, 0, NotificationCopy.MaxLevel),
            ObjectFromData = own != null,
            Title = data.Title is { Length: > 0 } title ? Text(title, thing, names, "title") : null,
            TitleIcon = icon == null ? null : Image(icon, thing),
            Description = data.Description is { Length: > 0 } description ? Text(description, thing, names, "description") : null,
            CustomTexts = texts,
            CustomImages = images,
        };
        string? check = copy.Problem();
        if (check != null)
        {
            problem = check;
            return null;
        }

        kept = names.Kept;
        return copy;
    }

    /// <summary>
    /// Friend: the game's toast data made from <paramref name="copy"/> with this PC's objects, or null with why. <paramref name="how"/>
    /// says how its title was made, for the log; <paramref name="fellBack"/> the recipes that made nothing here (their texts are the
    /// host's), each with its slot. <paramref name="later"/>: it can't be made only because of something this PC's game may have
    /// soon (the match's team or skater before its copy of the host's match has loaded), so it's worth another try.
    /// </summary>
    public static NotificationData? Build(NotificationCopy copy, out string problem, out string how, out IReadOnlyList<string> fellBack, out bool later)
    {
        problem = string.Empty;
        how = copy.Title?.How ?? "no title";
        fellBack = Array.Empty<string>();
        later = false;
        TeamData? team = null;
        if (copy.Team != ToastTeam.None)
        {
            team = TeamFor(copy.Team);
            if (team == null)
            {
                problem = $"this PC holds no {copy.Team.ToString().ToLowerInvariant()} team now";
                later = true;
                return null;
            }
        }

        SkaterData? skater = null;
        if (copy.SkaterId != null)
        {
            skater = SkaterIn(team, copy.SkaterId) ?? SkaterIn(MenuGame.Campaign()?.runData?.teamData, copy.SkaterId) ??
                SkaterIn(GameMenuCapture.Game()?._homeTeam?._teamData, copy.SkaterId) ?? SkaterIn(GameMenuCapture.Game()?._visitorTeam?._teamData, copy.SkaterId);
            if (skater == null)
            {
                problem = $"its skater ({SafeText.Clean(copy.SkaterId, 48)}) isn't on this PC's teams";
                later = true;
                return null;
            }
        }

        Thing? thing = null;
        if (copy.ObjectKind != ToastObjectKind.None)
        {
            thing = Thing.Find(copy.ObjectKind, copy.ObjectId, copy.ObjectLevel, team ?? MenuGame.Campaign()?.runData?.teamData);
            if (thing == null && copy.ObjectFromData)
            {
                problem = $"its {copy.ObjectKind.ToString().ToLowerInvariant()} ({SafeText.Clean(copy.ObjectId, 48)}, level {copy.ObjectLevel}) isn't in this game";
                return null;
            }
        }

        var failed = new List<string>();
        var functions = new ToastFunctions(thing?.Asset, team ?? MenuGame.Campaign()?.runData?.teamData);
        var texts = new Il2CppList.List<string>();
        int slot = 0;
        foreach (ToastText text in copy.CustomTexts)
        {
            texts.Add(Shown(text, thing, functions, failed, $"text {++slot}"));
        }

        var images = new Il2CppList.List<Sprite>();
        foreach (ToastImage image in copy.CustomImages)
        {
            if (Sprite(image, thing) is { } sprite)
            {
                images.Add(sprite);
            }
        }

        var data = new NotificationData
        {
            Type = (ENotificationType)copy.Type,
            Timing = (ENotificationTiming)copy.Timing,
            Title = copy.Title == null ? null! : Shown(copy.Title, thing, functions, failed, "title"),
            TitleIcon = copy.TitleIcon == null ? null! : Sprite(copy.TitleIcon, thing)!,
            Description = copy.Description == null ? null! : Shown(copy.Description, thing, functions, failed, "description"),
            SourceTeam = team!,
            IsTeamHome = copy.IsTeamHome,
            SourceSkater = skater!,
            NewInjury = copy.InjuryType is int injury ? new Injury((InjuryType)injury, copy.InjuryGames) : null!,
            PowerUpOrRelic = copy.ObjectFromData ? thing?.Asset! : null!,
            CustomTexts = copy.CustomTexts.Count == 0 ? null! : new Il2CppList.IReadOnlyList<string>(texts.Pointer),
            CustomImages = copy.CustomImages.Count == 0 ? null! : new Il2CppList.IReadOnlyList<Sprite>(images.Pointer),
        };
        fellBack = failed;
        return data;
    }

    /// <summary>Which of the game's teams <paramref name="team"/> is (none for null or another).</summary>
    private static ToastTeam TeamOf(TeamData? team)
    {
        if (team == null)
        {
            return ToastTeam.None;
        }

        if (MenuGame.Campaign()?.runData?.teamData is { } run && run.Pointer == team.Pointer)
        {
            return ToastTeam.Run;
        }

        GameManager? game = GameMenuCapture.Game();
        return game?._homeTeam?._teamData?.Pointer == team.Pointer ? ToastTeam.Home
            : game?._visitorTeam?._teamData?.Pointer == team.Pointer ? ToastTeam.Visitor
            : ToastTeam.None;
    }

    /// <summary>This PC's copy of the team <paramref name="which"/>, or null.</summary>
    private static TeamData? TeamFor(ToastTeam which) => which switch
    {
        ToastTeam.Run => MenuGame.Campaign()?.runData?.teamData,
        ToastTeam.Home => GameMenuCapture.Game()?._homeTeam?._teamData,
        ToastTeam.Visitor => GameMenuCapture.Game()?._visitorTeam?._teamData,
        _ => null,
    };

    private static SkaterData? SkaterIn(TeamData? team, string id)
    {
        Il2CppList.List<SkaterData>? all = team?.GetAllSkaters();
        for (int i = 0; all != null && i < all.Count; i++)
        {
            if (all[i] != null && all[i].id == id)
            {
                return all[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Host: a text as its object's; a term's (the one term with that text, else a name the game writes in the player's language by
    /// its object, else the first of several terms); a recipe where the game formatted it from a term and arguments; or the host's
    /// own (the host's goes with each, cleaned, as the fallback). A text left as the host's with words in it is noted in
    /// <paramref name="names"/> for the log.
    /// </summary>
    private static ToastText Text(string text, Thing? thing, ToastNames names, string slot)
    {
        string shown = Clean(text);
        if (thing != null && text == thing.Name)
        {
            return new ToastText(ToastTextKind.ObjectName, shown);
        }

        if (thing != null && text == thing.Description)
        {
            return new ToastText(ToastTextKind.ObjectDescription, shown);
        }

        bool term = PromptViews.TryTerm(text, out string found) && Id(found) is { Length: > 0 } key && key == found;
        if (term && PromptViews.TryUniqueTerm(text, out _))
        {
            return new ToastText(ToastTextKind.Term, shown, found);
        }

        if (names.TryName(shown, text) is { } named)
        {
            return named;
        }

        if (term)
        {
            return new ToastText(ToastTextKind.Term, shown, found);
        }

        return names.TryFormat(shown, text, slot) ?? new ToastText(ToastTextKind.Literal, shown);
    }

    /// <summary>
    /// Friend: a text in this PC's language where it was made from a term, the object or a recipe, else the host's (a recipe that
    /// makes nothing here is noted in <paramref name="fellBack"/>).
    /// </summary>
    private static string Shown(ToastText text, Thing? thing, ToastFunctions functions, List<string> fellBack, string slot)
    {
        string local = text.Kind switch
        {
            ToastTextKind.Term => MenuGame.Localized(text.Term ?? string.Empty),
            ToastTextKind.ObjectName => thing?.Name ?? string.Empty,
            ToastTextKind.ObjectDescription => thing?.Description ?? string.Empty,
            ToastTextKind.Recipe => text.Recipe?.Evaluate(functions) ?? string.Empty,
            _ => string.Empty,
        };
        if (local.Length == 0 && text.Kind == ToastTextKind.Recipe)
        {
            fellBack.Add($"{slot} {ShownTextLog.Recipe(text.Recipe, LogNames.TextShape)}");
        }

        return local.Length > 0 ? local : text.Value;
    }

    /// <summary>Host: an image as its object's icon, else by its sprite's name (null for a sprite with no usable name).</summary>
    private static ToastImage? Image(Sprite sprite, Thing? thing)
    {
        string name = Id(sprite.name);
        if (thing?.Icon is { } icon && icon.Pointer == sprite.Pointer)
        {
            return new ToastImage(ToastImageKind.ObjectIcon, name.Length > 0 ? name : thing.Id);
        }

        return name.Length > 0 ? new ToastImage(ToastImageKind.Named, name) : null;
    }

    /// <summary>Friend: the object's icon, or the sprite of that name this game has loaded; null when there's none.</summary>
    private static Sprite? Sprite(ToastImage image, Thing? thing)
    {
        if (image.Kind == ToastImageKind.ObjectIcon && thing?.Icon is { } icon)
        {
            return icon;
        }

        if (_sprites == null)
        {
            IndexSprites();
        }

        if (_sprites!.TryGetValue(image.Name, out Sprite? named) && named != null)
        {
            return named;
        }

        // Not among the sprites loaded when the index was made: the scene has changed since, so looked for once more (one
        // index per scene at most: it's a hitch, logged at the session's end).
        int scene = Scenes.WorldMap.SceneHandle;
        if (scene != _spritesScene)
        {
            IndexSprites();
            if (_sprites!.TryGetValue(image.Name, out named) && named != null)
            {
                return named;
            }
        }

        if (MissingSprites.Count < MaxMissingSprites)
        {
            _ = MissingSprites.Add(image.Name);
        }

        return null;
    }

    /// <summary>The sprites this game has loaded now, by name.</summary>
    private static void IndexSprites()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            Sprite? sprite = found?.TryCast<Sprite>();
            if (sprite != null && sprite.name is { Length: > 0 } name)
            {
                _ = sprites.TryAdd(name, sprite);
            }
        }

        _sprites = sprites;
        _spritesScene = Scenes.WorldMap.SceneHandle;
        SpriteIndexMs += (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>An id or name as it may travel: "" when it's empty, over 128 bytes or has a control character.</summary>
    private static string Id(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxNameBytes || Encoding.UTF8.GetByteCount(text) > MaxNameBytes)
        {
            return string.Empty;
        }

        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                return string.Empty;
            }
        }

        return text;
    }

    /// <summary>
    /// A text as it may travel: at most <see cref="NotificationCopy.MaxTextChars"/> characters, control characters other than
    /// line breaks and tabs made spaces.
    /// </summary>
    private static string Clean(string text)
    {
        string cut = text.Length > NotificationCopy.MaxTextChars ? text[..NotificationCopy.MaxTextChars] : text;
        char[] chars = cut.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) && chars[i] is not ('\n' or '\r' or '\t'))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    /// <summary>The strings of an IL2CPP read-only list, at most <paramref name="max"/>.</summary>
    private static IEnumerable<string> Strings(Il2CppList.IReadOnlyList<string>? list, int max)
    {
        int count = list?.TryCast<Il2CppList.IReadOnlyCollection<string>>()?.Count ?? 0;
        for (int i = 0; list != null && i < count && i < max; i++)
        {
            string? text = list[i];
            yield return text ?? string.Empty;
        }
    }

    /// <summary>The sprites of an IL2CPP read-only list, at most <paramref name="max"/> (empty places skipped).</summary>
    private static IEnumerable<Sprite> Sprites(Il2CppList.IReadOnlyList<Sprite>? list, int max)
    {
        int taken = 0;
        foreach (Sprite sprite in Native.Il2CppLists.Items(list))
        {
            if (taken >= max)
            {
                yield break;
            }

            if (sprite != null)
            {
                taken++;
                yield return sprite;
            }
        }
    }

    /// <summary>A toast's relic, talent or ability, with what a toast shows of it in this PC's language.</summary>
    private sealed class Thing
    {
        private Thing(ScriptableObject asset, ToastObjectKind kind, string id, int level, string name, string description, Sprite? icon)
        {
            Asset = asset;
            Kind = kind;
            Id = id;
            Level = level;
            Name = name;
            Description = description;
            Icon = icon;
        }

        public ScriptableObject Asset { get; }

        public ToastObjectKind Kind { get; }

        public string Id { get; }

        public int Level { get; }

        public string Name { get; }

        public string Description { get; }

        public Sprite? Icon { get; }

        /// <summary>The thing <paramref name="asset"/> is, or null when it isn't a relic, talent or ability (or has no usable id).</summary>
        public static Thing? Of(ScriptableObject asset)
        {
            if (asset.TryCast<Relic>() is { } relic)
            {
                return Make(relic, ToastObjectKind.Relic, relic.id, relic.level, relic.localizedRelicName, relic.localizedDescription, relic.sprite);
            }

            PowerUp? power = asset.TryCast<PowerUp>();
            ToastObjectKind kind = power == null ? ToastObjectKind.None
                : power.TryCast<Talent>() != null ? ToastObjectKind.Talent
                : power.TryCast<Ability>() != null ? ToastObjectKind.Ability
                : ToastObjectKind.None;
            return power == null || kind == ToastObjectKind.None ? null
                : Make(power, kind, power.id, power.level, power.localizedPowerupName, power.localizedDescription, power.icon);
        }

        /// <summary>Host: the relic, talent or ability of <paramref name="team"/> (or <paramref name="skater"/>) whose icon is <paramref name="icon"/>, or null.</summary>
        public static Thing? WithIcon(Sprite icon, TeamData? team, SkaterData? skater)
        {
            Il2CppList.List<Relic>? relics = team?.relics;
            for (int i = 0; relics != null && i < relics.Count; i++)
            {
                if (relics[i]?.sprite is { } sprite && sprite.Pointer == icon.Pointer)
                {
                    return Of(relics[i]);
                }
            }

            if (skater != null && PowerUpWithIcon(skater, icon) is { } own)
            {
                return own;
            }

            Il2CppList.List<SkaterData>? skaters = team?.GetAllSkaters();
            for (int i = 0; skaters != null && i < skaters.Count; i++)
            {
                if (skaters[i] != null && PowerUpWithIcon(skaters[i], icon) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Friend: this PC's relic, talent or ability of that id and level: the team's own, else the game's repositories'.</summary>
        public static Thing? Find(ToastObjectKind kind, string id, int level, TeamData? team)
        {
            if (kind == ToastObjectKind.Relic)
            {
                Il2CppList.List<Relic>? relics = team?.relics;
                for (int i = 0; relics != null && i < relics.Count; i++)
                {
                    if (relics[i] is { } own && own.id == id && own.level == level)
                    {
                        return Of(own);
                    }
                }

                IRelicRepository? repository = MenuGame.Repository<IRelicRepository>(p => new IRelicRepository(p));
                Relic? relic = repository?.GetRelic(id, true);
                if (relic != null && relic.level != level)
                {
                    relic = repository!.GetRelic(relic.GetIl2CppType(), level, true);
                }

                return relic != null && relic.level == level ? Of(relic) : null;
            }

            Il2CppList.List<SkaterData>? skaters = team?.GetAllSkaters();
            for (int i = 0; skaters != null && i < skaters.Count; i++)
            {
                if (skaters[i] is { } skater && PowerUpOf(skater, id, level) is { } own)
                {
                    return Of(own);
                }
            }

            PowerUp? power = null;
            if (kind == ToastObjectKind.Talent && MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p)) is { } talents)
            {
                Talent? talent = talents.GetTalent(id, true);
                power = talent != null && talent.level != level ? talents.GetTalent(talent.GetIl2CppType(), level, true) : talent;
            }
            else if (kind == ToastObjectKind.Ability && MenuGame.Repository<IAbilityRepository>(p => new IAbilityRepository(p)) is { } abilities)
            {
                Ability? ability = abilities.GetAbility(id, true);
                power = ability != null && ability.level != level ? abilities.GetAbility(ability.GetIl2CppType(), level, true) : ability;
            }

            return power != null && power.level == level ? Of(power) : null;
        }

        private static Thing? Make(ScriptableObject asset, ToastObjectKind kind, string? id, int level, string? name, string? description, Sprite? icon)
        {
            string key = ToastCopies.Id(id);
            return key.Length == 0 ? null : new Thing(asset, kind, key, level, name ?? string.Empty, description ?? string.Empty, icon);
        }

        private static Thing? PowerUpWithIcon(SkaterData skater, Sprite icon)
        {
            if (skater.ability?.icon is { } abilityIcon && abilityIcon.Pointer == icon.Pointer)
            {
                return Of(skater.ability);
            }

            Il2CppList.List<Talent>? talents = skater.powerups;
            for (int i = 0; talents != null && i < talents.Count; i++)
            {
                if (talents[i]?.icon is { } talentIcon && talentIcon.Pointer == icon.Pointer)
                {
                    return Of(talents[i]);
                }
            }

            return null;
        }

        private static PowerUp? PowerUpOf(SkaterData skater, string id, int level)
        {
            if (skater.ability is { } ability && ability.id == id && ability.level == level)
            {
                return ability;
            }

            Il2CppList.List<Talent>? talents = skater.powerups;
            for (int i = 0; talents != null && i < talents.Count; i++)
            {
                if (talents[i] is { } talent && talent.id == id && talent.level == level)
                {
                    return talent;
                }
            }

            return null;
        }
    }
}
#endif
