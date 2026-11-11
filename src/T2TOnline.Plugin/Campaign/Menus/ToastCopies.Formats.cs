#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppData;
using Il2CppRogue;
using Il2CppSkaters.Injuries;
using Il2CppTape2Tape;
using Il2CppTape2Tape.Customization.UI;
using Il2CppTape2Tape.Hockey.UI;
using T2TOnline.Core.Campaign;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

// A toast's texts the game formats from a term and arguments, in each friend's language (v1): the host finds the recipe
// that makes the shown text from the game's own templates and names (Core FormatRecipes), verifies it with the functions below, and a friend
// makes it again with the same functions on its PC. Apart from the toasts' copies (ToastCopies.cs).
internal static partial class ToastCopies
{
    private const int MaxKept = 8;

    // The stats a toast writes and the injuries it names (the game's EForwardStat 0-4 and InjuryType 0-3).
    private const int ForwardStats = 5;
    private const int InjuryTypes = 4;

    /// <summary>
    /// The game's functions for a toast's recipes, on this PC and in its language: the events' text functions over the toast's
    /// own object, and the four names a toast writes (a team's, an asset's, an injury's, a stat's). The host verifies a recipe with
    /// these; a friend makes it with them.
    /// </summary>
    private sealed class ToastFunctions : IToastTextFunctions
    {
        private readonly GameTextFunctions _inner;
        private readonly TeamData? _team;

        public ToastFunctions(ScriptableObject? own, TeamData? team)
        {
            _inner = new GameTextFunctions(own, null, null);
            _team = team;
        }

        public string? Term(string key) => _inner.Term(key);

        public string? Format(string format, IReadOnlyList<string> args) => _inner.Format(format, args);

        public string? ObjectName(ChoiceObjectRef target) => _inner.ObjectName(target);

        public string? ObjectDescription(ChoiceObjectRef target, bool includeUpgrade, bool includeStatBonus, string colorHex) =>
            _inner.ObjectDescription(target, includeUpgrade, includeStatBonus, colorHex);

        public string? StatName(int stat, bool description) => _inner.StatName(stat, description);

        public string? NurseProcedureName(int procedure) => _inner.NurseProcedureName(procedure);

        public string? NurseCardDescription(int? procedure, ChoiceObjectRef talent, int? injury, int injuryGames) =>
            _inner.NurseCardDescription(procedure, talent, injury, injuryGames);

        /// <summary>The game's <c>CampaignUtil.GetTeamDisplayName</c> of this PC's run, home or visitor team (a squad's translated name for the run's).</summary>
        public string? TeamName(ToastTeam team) => TeamFor(team) is { } found ? CampaignUtil.GetTeamDisplayName(found) : null;

        /// <summary>The relic, talent or ability of that id and level, from the toast's team (else the game's lists), by its name in this language.</summary>
        public string? AssetName(ToastObjectKind kind, string id, int level) =>
            Thing.Find(kind, id, level, _team ?? MenuGame.Campaign()?.runData?.teamData)?.Name is { Length: > 0 } name ? name : null;

        /// <summary>The game's <c>UIInjuryUtils.GetInjuryName</c>.</summary>
        public string? InjuryName(int injury) => Enum.IsDefined(typeof(InjuryType), injury) ? UIInjuryUtils.GetInjuryName((InjuryType)injury) : null;

        /// <summary>The game's <c>EForwardStatExtensions.GetLocalizedName</c> (the stat's name inside a keyword tag).</summary>
        public string? LocalizedStatName(int stat) => Enum.IsDefined(typeof(EForwardStat), stat) ? ((EForwardStat)stat).GetLocalizedName() : null;
    }

    /// <summary>
    /// What the host's game knows by name when it looks for a toast's recipe (<see cref="ITextNames"/>): the stats' keyword names,
    /// the injuries' and teams' names, the relics, talents and abilities of the toast's team and the match's teams (read when
    /// first needed), and the game's one term with that text. Each name is the node a friend's game makes in its language; a
    /// skater's name stays a name. One per toast read; game thread only; nothing here throws.
    /// </summary>
    private sealed class ToastNames : ITextNames
    {
        private readonly NotificationData _data;
        private readonly ToastFunctions _functions;
        private Dictionary<string, TextRecipe>? _words;
        private Dictionary<string, TextRecipe>? _assets;
        private HashSet<string>? _skaters;

        public ToastNames(NotificationData data, Thing? thing)
        {
            _data = data;
            _functions = new ToastFunctions(thing?.Asset, data.SourceTeam);
        }

        /// <summary>The texts left as the host's with words in them, each as "title (its shape; why)" (at most <see cref="MaxKept"/>).</summary>
        public List<string> Kept { get; } = new();

        /// <inheritdoc />
        public TextRecipe? Translated(string text)
        {
            if (string.IsNullOrEmpty(text) || IsName(text))
            {
                return null;
            }

            return Name(text) ?? (PromptViews.TryUniqueTerm(text, out string term) && Id(term) == term ? TextRecipe.Term(term) : null);
        }

        /// <inheritdoc />
        public bool IsName(string text) => Skaters().Contains(text);

        /// <summary>
        /// The text as one name the game writes in the player's language (a stat's, an injury's, a team's, a relic's), as a recipe the
        /// host's own functions make exactly; null when it isn't one.
        /// </summary>
        public ToastText? TryName(string shown, string original)
        {
            try
            {
                if (shown != original || shown.Length == 0 || IsName(shown) || Name(shown) is not { } node)
                {
                    return null;
                }

                string? made = node.Evaluate(_functions);
                return made == shown && node.Check() == null ? new ToastText(ToastTextKind.Recipe, shown, null, node) : null;
            }
            catch (Exception)
            {
                return null; // the game's lookups threw: the text goes the way it did before recipes
            }
        }

        /// <summary>
        /// The text as a recipe where the game formatted it from a term and arguments (<see cref="FormatRecipes"/>), or null (a number
        /// or a skater's name needs none; any other text is noted in <see cref="Kept"/> with why).
        /// </summary>
        public ToastText? TryFormat(string shown, string original, string slot)
        {
            string why = "the game's terms couldn't be read";
            try
            {
                if (shown != original || !shown.Any(char.IsLetter) || IsName(shown))
                {
                    return null;
                }

                TextTemplates? templates = PromptViews.Templates();
                FormatRecipe? built = templates == null ? null : FormatRecipes.TryBuild(shown, templates, this, _functions, out why);
                if (built != null)
                {
                    return new ToastText(ToastTextKind.Recipe, shown, null, built.Recipe);
                }
            }
            catch (Exception ex)
            {
                why = $"{ex.GetType().Name} while looking for one";
            }

            if (Kept.Count < MaxKept)
            {
                // As its shape: a toast's text can hold a custom team's or skater's typed name (D127).
                Kept.Add($"{slot} ({LogNames.TextShape(shown)}; {ShownTextLog.Reason(why, LogNames.TextShape)})");
            }

            return null;
        }

        private TextRecipe? Name(string text)
        {
            if (Words().TryGetValue(text, out TextRecipe? word))
            {
                return word;
            }

            return Assets().TryGetValue(text, out TextRecipe? asset) ? asset : null;
        }

        /// <summary>The stats', injuries' and teams' names in this language.</summary>
        private Dictionary<string, TextRecipe> Words()
        {
            if (_words != null)
            {
                return _words;
            }

            var words = new Dictionary<string, TextRecipe>(StringComparer.Ordinal);
            _words = words;
            for (int stat = 0; stat < ForwardStats; stat++)
            {
                int which = stat;
                Add(words, () => ((EForwardStat)which).GetLocalizedName(), TextRecipe.LocalizedStatName(which));
            }

            for (int injury = 0; injury < InjuryTypes; injury++)
            {
                int which = injury;
                Add(words, () => UIInjuryUtils.GetInjuryName((InjuryType)which), TextRecipe.InjuryName(which));
            }

            foreach (ToastTeam team in new[] { ToastTeam.Run, ToastTeam.Home, ToastTeam.Visitor })
            {
                ToastTeam which = team;
                if (TeamFor(which) is { } found)
                {
                    Add(words, () => CampaignUtil.GetTeamDisplayName(found), TextRecipe.TeamName(which));
                }
            }

            return words;
        }

        /// <summary>The relics, talents and abilities of the toast's team and the match's teams, by their names in this language.</summary>
        private Dictionary<string, TextRecipe> Assets()
        {
            if (_assets != null)
            {
                return _assets;
            }

            var assets = new Dictionary<string, TextRecipe>(StringComparer.Ordinal);
            _assets = assets;
            foreach (TeamData team in Teams())
            {
                Il2CppList.List<Relic>? relics = Safely(() => team.relics);
                for (int i = 0; relics != null && i < relics.Count; i++)
                {
                    Relic relic = relics[i];
                    Add(assets, relic);
                }

                Il2CppList.List<SkaterData>? skaters = Safely(() => team.GetAllSkaters());
                for (int i = 0; skaters != null && i < skaters.Count; i++)
                {
                    SkaterData? skater = skaters[i];
                    if (skater == null)
                    {
                        continue;
                    }

                    Add(assets, skater.ability);
                    Il2CppList.List<Talent>? talents = skater.powerups;
                    for (int j = 0; talents != null && j < talents.Count; j++)
                    {
                        Add(assets, talents[j]);
                    }
                }
            }

            return assets;
        }

        /// <summary>The skaters' full names of the toast's team and the match's teams (names aren't translated).</summary>
        private HashSet<string> Skaters()
        {
            if (_skaters != null)
            {
                return _skaters;
            }

            var skaters = new HashSet<string>(StringComparer.Ordinal);
            _skaters = skaters;
            AddName(skaters, _data.SourceSkater);
            foreach (TeamData team in Teams())
            {
                Il2CppList.List<SkaterData>? all = Safely(() => team.GetAllSkaters());
                for (int i = 0; all != null && i < all.Count; i++)
                {
                    AddName(skaters, all[i]);
                }
            }

            return skaters;
        }

        /// <summary>The toast's team, then the run's, the home and the visitor team, each once.</summary>
        private IEnumerable<TeamData> Teams()
        {
            var seen = new HashSet<IntPtr>();
            foreach (TeamData? team in new[] { _data.SourceTeam, TeamFor(ToastTeam.Run), TeamFor(ToastTeam.Home), TeamFor(ToastTeam.Visitor) })
            {
                if (team != null && seen.Add(team.Pointer))
                {
                    yield return team;
                }
            }
        }

        private static void Add(Dictionary<string, TextRecipe> into, Func<string?> name, TextRecipe node)
        {
            string? text = Safely(name);
            if (!string.IsNullOrEmpty(text))
            {
                _ = into.TryAdd(text, node);
            }
        }

        private static void Add(Dictionary<string, TextRecipe> into, ScriptableObject? asset)
        {
            if (asset == null)
            {
                return;
            }

            string? name = null;
            string? id = null;
            int level = 0;
            ToastObjectKind kind = ToastObjectKind.None;
            try
            {
                if (asset.TryCast<Relic>() is { } relic)
                {
                    (kind, id, level, name) = (ToastObjectKind.Relic, relic.id, relic.level, relic.localizedRelicName);
                }
                else if (asset.TryCast<PowerUp>() is { } power)
                {
                    kind = power.TryCast<Talent>() != null ? ToastObjectKind.Talent : power.TryCast<Ability>() != null ? ToastObjectKind.Ability : ToastObjectKind.None;
                    (id, level, name) = (power.id, power.level, power.localizedPowerupName);
                }
            }
            catch (Exception)
            {
                return; // an asset whose name can't be read stays out
            }

            if (kind != ToastObjectKind.None && !string.IsNullOrEmpty(name) && ToastCopies.Id(id) is { Length: > 0 } key && level is >= 0 and <= NotificationCopy.MaxLevel)
            {
                _ = into.TryAdd(name, TextRecipe.AssetName(kind, key, level));
            }
        }

        private static void AddName(HashSet<string> into, SkaterData? skater)
        {
            string? name = skater == null ? null : Safely(() => string.Concat(skater.firstName, " ", skater.lastName));
            if (!string.IsNullOrWhiteSpace(name))
            {
                _ = into.Add(name.Trim());
            }
        }

        private static T? Safely<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}
#endif
