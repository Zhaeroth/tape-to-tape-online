#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppBishopGames.Utils;
using Il2CppData;
using Il2CppRogue;
using Il2CppRogue.Powerups.Repository;
using Il2CppRogue.Relics.Repository;
using Il2CppSkaters;
using Il2CppState;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// Skaters that aren't in the run (an event's free agents, a run-start menu's superstars and random skaters) as plain data
/// both ways:
/// <list type="bullet">
/// <item>Host, <see cref="Read"/>: the run file's own model of the skater (<c>SerializerV2.SerializeToModel(ForwardData)</c>,
/// what the run's save makes of it; <see cref="EventCapture.FreeAgent"/>) and what its card shows besides
/// (<see cref="Look"/>: the hidden stat offsets and the look the model drops).</item>
/// <item>Friend, <see cref="Builder"/>: the skater made with the game's own deserializer
/// (<c>DeserializerV2.ConvertForwardDataModelToForwardData</c>, what this PC's checkpoint load makes of it once it's in the
/// run), given the levels, contract games and flavour keys the model leaves out, and its look; then read back with the
/// game's own serializer and compared field by field with the host's (<see cref="Verify"/>).</item>
/// </list>
/// A skater built here never joins this PC's run (picks are refused on a friend; the run changes only with the host's
/// checkpoint) and is never saved.
/// </summary>
internal static class SkaterModels
{
    /// <summary>
    /// Host: <paramref name="forward"/> as the run file's model of it, its look and its back number's format (the model has none:
    /// the game's deserializer builds a regular number); null, with why, when the model can't be made.
    /// </summary>
    public static RunStartSkater? Read(ForwardData forward, out string problem)
    {
        FreeAgentData? model = EventCapture.FreeAgent(forward, out problem);
        return model == null ? null : new RunStartSkater(model, Look(forward), (int)forward.numberFormat);
    }

    /// <summary>What <paramref name="forward"/>'s card shows that the run file's model leaves out.</summary>
    public static SkaterLook Look(ForwardData forward) => new()
    {
        HiddenShotPower = forward._hiddenShotPower,
        HiddenSpeed = forward._hiddenSpeed,
        HiddenChecking = forward._hiddenChecking,
        HiddenShotAccuracy = forward._hiddenShotAccuracy,
        IsFemale = forward.isFemale,
        HasGoldenHelmet = forward.HasGoldenHelmet,
        GlassesSkin = forward.glassesSkin,
        HelmetAwaySkin = forward.helmetAwaySkin,
        SizeOffset = forward.sizeOffsetPercentage,
        NumberColor = Colour(forward.numberColorOverride),
        StickColor = Scheme(forward.StickColor),
        SkatesColor = Scheme(forward.SkatesColor),
        HeightInches = forward.heightInches,
        WeightKg = forward.weightKg,
    };

    /// <summary>Gives <paramref name="forward"/> the host's <paramref name="look"/> (new colour schemes: a template's shared objects are never changed).</summary>
    public static void ApplyLook(ForwardData forward, SkaterLook look)
    {
        forward._hiddenShotPower = look.HiddenShotPower;
        forward._hiddenSpeed = look.HiddenSpeed;
        forward._hiddenChecking = look.HiddenChecking;
        forward._hiddenShotAccuracy = look.HiddenShotAccuracy;
        forward.isFemale = look.IsFemale;
        forward.HasGoldenHelmet = look.HasGoldenHelmet;
        forward.glassesSkin = look.GlassesSkin!;
        forward.helmetAwaySkin = look.HelmetAwaySkin!;
        forward.sizeOffsetPercentage = look.SizeOffset;
        forward.numberColorOverride = Colour(look.NumberColor);
        forward.StickColor = Scheme(look.StickColor)!;
        forward.SkatesColor = Scheme(look.SkatesColor)!;
        forward.heightInches = look.HeightInches;
        forward.weightKg = look.WeightKg;
    }

    /// <summary>
    /// Reads <paramref name="built"/> back with the game's own serializer and compares it with the host's skater field by field
    /// (the model, its levels and flavour keys, its look). Null when it's the host's; otherwise the first difference, in words.
    /// </summary>
    public static string? Verify(ForwardData built, RunStartSkater host)
    {
        RunStartSkater? read = Read(built, out string problem);
        if (read == null)
        {
            return $"it can't be read back ({problem})";
        }

        return SkaterModelDiff.FirstDifference(host, read) ?? CardStats(built, host.Model, host.Look);
    }

    /// <summary>
    /// Host: an event's free agent as the run file's model of it with what its card and preview show besides
    /// (<see cref="FreeAgentData.Version2"/>: its look, birth date and number format); null, with why, when the model can't be made.
    /// </summary>
    public static FreeAgentData? Agent(ForwardData forward, out string problem)
    {
        FreeAgentData? model = EventCapture.FreeAgent(forward, out problem);
        if (model == null)
        {
            return null;
        }

        DateDMY birth = forward.birthDate;
        return model.WithLook(Look(forward), new DateDmy(birth.Day, birth.Month, birth.Year), (int)forward.numberFormat);
    }

    /// <summary>
    /// Friend: gives an event's free agent built from the host's model (<paramref name="forward"/>) the host's look, birth date
    /// and number format (none for an agent sent without them), then reads it back whole as the host read it
    /// (<see cref="Agent"/>) and compares field by field, and the stats its card shows. False, with the first difference,
    /// when it isn't the host's.
    /// </summary>
    public static bool ApplyAgentLook(ForwardData forward, FreeAgentData host, out string problem)
    {
        problem = string.Empty;
        if (host.Look == null)
        {
            return true;
        }

        if (!Enum.IsDefined(typeof(NumberFormat), host.NumberFormat))
        {
            problem = $"its number format ({host.NumberFormat}) is one this game doesn't know";
            return false;
        }

        ApplyLook(forward, host.Look);
        forward.birthDate = new DateDMY { Day = host.BirthDate.Day, Month = host.BirthDate.Month, Year = host.BirthDate.Year };
        forward.numberFormat = (NumberFormat)host.NumberFormat;
        FreeAgentData? read = Agent(forward, out string unread);
        string? differs = read == null ? $"it can't be read back ({unread})" : SkaterModelDiff.FirstDifference(host, read) ?? CardStats(forward, host, host.Look);
        if (differs != null)
        {
            problem = $"its {differs} didn't read back as the host's";
            return false;
        }

        return true;
    }

    /// <summary>The stats <paramref name="built"/>'s card shows (each less its hidden offset, as the game works them out) against the host's, or null when they're the same.</summary>
    private static string? CardStats(ForwardData built, FreeAgentData model, SkaterLook look) =>
        built.DisplayShotPower != model.ShotPower - look.HiddenShotPower || built.DisplaySpeed != model.Speed - look.HiddenSpeed ||
        built.DisplayChecking != model.Checking - look.HiddenChecking || built.DisplayShotAccuracy != model.ShotAccuracy - look.HiddenShotAccuracy
            ? "the stats its card shows"
            : null;

    /// <summary>
    /// Destroys objects made here that nothing of the game's holds (what a <see cref="Builder"/> made for something that won't
    /// be shown); <paramref name="what"/> names them for a failure's log line. Returns how many were destroyed. Never throws.
    /// </summary>
    public static int Destroy(IReadOnlyList<UnityEngine.Object> made, string what)
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
                ModLog.Warn($"Menus: an object made for {what} couldn't be destroyed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        return destroyed;
    }

    /// <summary>A game colour as plain data.</summary>
    public static ColorValue Colour(Color color) => new(color.r, color.g, color.b, color.a);

    /// <summary>A plain colour as the game's.</summary>
    public static Color Colour(ColorValue color) => new(color.R, color.G, color.B, color.A);

    /// <summary>A game colour scheme as plain data (null for none).</summary>
    public static ColorSchemeData? Scheme(ColorScheme? scheme) =>
        scheme == null ? null : new ColorSchemeData(Colour(scheme.primaryColor), Colour(scheme.secondaryColor), Colour(scheme.tertiaryColor));

    /// <summary>A plain colour scheme as a new game one (null for none).</summary>
    public static ColorScheme? Scheme(ColorSchemeData? scheme) => scheme == null ? null : new ColorScheme
    {
        primaryColor = Colour(scheme.Primary),
        secondaryColor = Colour(scheme.Secondary),
        tertiaryColor = Colour(scheme.Tertiary),
    };

    /// <summary>A team's colours (a <c>TeamColorsData</c>) as plain data: its eight schemes in <see cref="FreeAgentData.SchemeNames"/>' order, or null for none.</summary>
    public static ColorSchemeData?[]? Schemes(TeamColorsData? colors) => colors == null ? null : new[]
    {
        Scheme(colors.jerseyScheme), Scheme(colors.numberScheme), Scheme(colors.pantsScheme), Scheme(colors.glovesScheme),
        Scheme(colors.stickScheme), Scheme(colors.helmetScheme), Scheme(colors.socksScheme), Scheme(colors.skatesScheme),
    };

    /// <summary>Plain colours as a new <c>TeamColorsData</c> with a new <c>ColorScheme</c> for each, or null for none.</summary>
    public static TeamColorsData? Schemes(IReadOnlyList<ColorSchemeData?>? colors) => colors == null ? null : new TeamColorsData
    {
        jerseyScheme = Scheme(colors[0])!,
        numberScheme = Scheme(colors[1])!,
        pantsScheme = Scheme(colors[2])!,
        glovesScheme = Scheme(colors[3])!,
        stickScheme = Scheme(colors[4])!,
        helmetScheme = Scheme(colors[5])!,
        socksScheme = Scheme(colors[6])!,
        skatesScheme = Scheme(colors[7])!,
    };

    /// <summary>
    /// Friend: builds skaters from the host's models with the game's own deserializer, against this game's talent, ability and
    /// relic lists (looked up once per builder, the deserializer made once). One per set of skaters built together.
    /// </summary>
    /// <remarks>
    /// Every object it makes is this PC's own and is noted in <see cref="Made"/>: the skater the deserializer
    /// makes (a new <c>ForwardData</c>), the talents and ability it clones from this game's lists, and any made again at the
    /// host's level; a clone another took the place of is destroyed at once (nothing holds it). Its owner destroys
    /// <see cref="Made"/> when what was built won't be shown, also after a failed build (what was made so far stays noted).
    /// </remarks>
    internal sealed class Builder
    {
        private readonly List<UnityEngine.Object> _made;
        private ITalentRepository? _talents;
        private IAbilityRepository? _abilities;
        private IRelicRepository? _relics;
        private DeserializerV2? _deserializer;

        /// <summary>A builder that notes what it makes in its own list.</summary>
        public Builder()
            : this(new List<UnityEngine.Object>())
        {
        }

        /// <summary>A builder that notes what it makes in <paramref name="made"/> (an event builder's own list, with its other objects).</summary>
        public Builder(List<UnityEngine.Object> made)
        {
            _made = made ?? throw new ArgumentNullException(nameof(made));
        }

        /// <summary>The objects made here so far (skaters, their talents and abilities, other clones noted with <see cref="Keep"/>), every one this PC's own.</summary>
        public IReadOnlyList<UnityEngine.Object> Made => _made;

        /// <summary>Notes an object made here (a clone, a new instance) with the rest. Returns it.</summary>
        public T? Keep<T>(T? value)
            where T : UnityEngine.Object
        {
            if (value != null)
            {
                _made.Add(value);
            }

            return value;
        }

        /// <summary>An object made here that another took the place of (nothing holds it): no longer noted, and destroyed now.</summary>
        public void Drop(UnityEngine.Object? replaced)
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
        /// A skater built from the run file's own model of it with the game's own deserializer, then given what the model
        /// leaves out (its talents' and ability's levels, a temporary contract's games, the flavour keys) and read back field by
        /// field. Null, with why, when a talent or ability isn't in this game (the deserializer drops one it can't find) or
        /// anything reads back otherwise.
        /// </summary>
        public ForwardData? Build(FreeAgentData data, out string problem)
        {
            if (!Enum.IsDefined(typeof(ForwardRarity), data.ForwardRarity) || !Enum.IsDefined(typeof(SkaterSize), data.SkaterSize) ||
                !Enum.IsDefined(typeof(SkaterType), data.DefaultSkaterType) || !Enum.IsDefined(typeof(SkaterCategory), data.SkaterCategory))
            {
                problem = "its rarity, size, type or category is one this game doesn't know";
                return null;
            }

            _talents ??= MenuGame.Repository<ITalentRepository>(p => new ITalentRepository(p));
            _abilities ??= MenuGame.Repository<IAbilityRepository>(p => new IAbilityRepository(p));
            _relics ??= MenuGame.Repository<IRelicRepository>(p => new IRelicRepository(p));
            if (_talents == null || _abilities == null)
            {
                problem = "this game's talent and ability lists aren't ready";
                return null;
            }

            var powerUps = new Il2CppList.List<PowerUpModelV2>();
            foreach (FreeAgentTalent talent in data.Talents)
            {
                powerUps.Add(new PowerUpModelV2 { Id = talent.Id });
            }

            TeamColorsData? colors = Schemes(data.Colors);
            IReadOnlyList<string?> skins = data.Skins;
            var model = new ForwardDataModelV2
            {
                id = data.Id,
                originalId = data.OriginalId,
                name = data.Name,
                firstName = data.FirstName!,
                lastName = data.LastName!,
                number = data.Number,
                checking = data.Checking,
                speed = data.Speed,
                shotAccuracy = data.ShotAccuracy,
                shotPower = data.ShotPower,
                injuryCounter = data.InjuryCounter,
                isLefty = data.IsLefty,
                isBlack = data.IsBlack,
                hasOversizedHelmet = data.HasOversizedHelmet,
                forwardRarity = (ForwardRarity)data.ForwardRarity,
                skaterSize = (SkaterSize)data.SkaterSize,
                defaultSkaterType = (SkaterType)data.DefaultSkaterType,
                skaterCategory = (SkaterCategory)data.SkaterCategory,
                bodySkin = skins[0]!,
                headSkin = skins[1]!,
                helmetSkin = skins[2]!,
                logoSkin = skins[3]!,
                numberSkin = skins[4]!,
                stickSkin = skins[5]!,
                bodyAwaySkin = skins[6]!,
                bicepSkin = skins[7]!,
                gloveSkin = skins[8]!,
                skateSkin = skins[9]!,
                pantsSkin = skins[10]!,
                bicepAwaySkin = skins[11]!,
                gloveAwaySkin = skins[12]!,
                skateAwaySkin = skins[13]!,
                pantsAwaySkin = skins[14]!,
                colorSchemes = colors!,
                ability = data.AbilityId == null ? null! : new AbilityDataModelV2 { Id = data.AbilityId },
                powerUps = powerUps,
            };

            // The logo list is never read for a forward (only a team's logo): none is passed.
            _deserializer ??= new DeserializerV2(_talents, _relics!, _abilities);
            ForwardData? forward = Keep(_deserializer.ConvertForwardDataModelToForwardData(model));
            if (forward == null)
            {
                problem = "the game's deserializer made no skater";
                return null;
            }

            // Its talents and ability are the deserializer's clones from this game's lists: this PC's own too.
            Il2CppList.List<Talent>? cloned = forward.powerups;
            for (int i = 0; cloned != null && i < cloned.Count; i++)
            {
                Keep(cloned[i]);
            }

            Keep(forward.ability);

            forward.flavorTextLoc = data.FlavorKey!;
            forward.perkFlavorTextLoc = data.PerkFlavorKey!;
            forward.powerupTextLoc = data.PowerUpTextKey!;
            if (!Talents(forward, data, out problem) || !Ability(forward, data, out problem))
            {
                return null;
            }

            string? differs = forward.id != data.Id ? "id"
                : forward._originalId != data.OriginalId ? "template"
                : forward.name != data.Name ? "asset name"
                : forward.firstName != data.FirstName || forward.lastName != data.LastName ? "name"
                : forward.number != data.Number ? "number"
                : forward.checking != data.Checking || forward.speed != data.Speed || forward.shotAccuracy != data.ShotAccuracy || forward.shotPower != data.ShotPower ? "stats"
                : (int)forward.skaterSize != data.SkaterSize || (int)forward.forwardRarity != data.ForwardRarity ? "size or rarity"
                : Pointer(forward.colorSchemes) != Pointer(colors) ? "colours"
                : forward.flavorTextLoc != data.FlavorKey || forward.perkFlavorTextLoc != data.PerkFlavorKey || forward.powerupTextLoc != data.PowerUpTextKey ? "flavour keys"
                : null;
            if (differs != null)
            {
                problem = $"its {differs} didn't read back as sent";
                return null;
            }

            problem = string.Empty;
            return forward;
        }

        /// <summary>
        /// A run-start skater: <see cref="Build(FreeAgentData, out string)"/>, its look and its back number's format given (a Roman
        /// numeral or Prison skater's number is the host's), then read back with the game's own serializer and compared with the host's
        /// (<see cref="Verify"/>). Null, with why, otherwise (a format this game doesn't know is one: the set shows as text).
        /// </summary>
        public ForwardData? Build(RunStartSkater skater, out string problem)
        {
            ForwardData? forward = Build(skater.Model, out problem);
            if (forward == null)
            {
                return null;
            }

            if (!Enum.IsDefined(typeof(NumberFormat), skater.NumberFormat))
            {
                problem = $"its number format ({skater.NumberFormat}) is one this game doesn't know";
                return null;
            }

            ApplyLook(forward, skater.Look);
            forward.numberFormat = (NumberFormat)skater.NumberFormat;
            string? differs = Verify(forward, skater);
            if (differs != null)
            {
                problem = $"its {differs} didn't read back as the host's";
                return null;
            }

            return forward;
        }

        /// <summary>
        /// The built skater's talents as the host's: every one there in order (the deserializer drops one this game doesn't
        /// have), each at the host's level (made again from this game's list when it isn't), a temporary contract with the
        /// host's games.
        /// </summary>
        private bool Talents(ForwardData forward, FreeAgentData data, out string problem)
        {
            Il2CppList.List<Talent>? talents = forward.powerups;
            int count = talents?.Count ?? 0;
            for (int i = 0; i < data.Talents.Count; i++)
            {
                FreeAgentTalent want = data.Talents[i];
                Talent? talent = i < count ? talents![i] : null;
                if (talent == null || talent.id != want.Id)
                {
                    problem = $"its talent {i + 1} \"{HostText.KeyForLog(want.Id)}\" isn't in this game";
                    return false;
                }

                if (talent.level != want.Level)
                {
                    Talent? leveled = Keep(_talents!.GetTalent(talent.GetIl2CppType(), want.Level, true));
                    if (leveled == null || leveled.level != want.Level || leveled.id != want.Id)
                    {
                        problem = $"its talent \"{HostText.KeyForLog(want.Id)}\" isn't in this game at level {want.Level}";
                        return false;
                    }

                    talents![i] = leveled;
                    Drop(talent); // the deserializer's clone at the list's level: nothing holds it now
                    talent = leveled;
                }

                TemporaryContract? contract = talent.TryCast<TemporaryContract>();
                if ((contract != null) != (want.ContractGames >= 0))
                {
                    problem = $"its talent \"{HostText.KeyForLog(want.Id)}\" is{(contract == null ? " not" : string.Empty)} a temporary contract here";
                    return false;
                }

                if (contract != null)
                {
                    contract.numOfGames = want.ContractGames;
                    if (contract.numOfGames != want.ContractGames)
                    {
                        problem = "its contract's games didn't read back as written";
                        return false;
                    }
                }
            }

            problem = count == data.Talents.Count ? string.Empty : $"it has {count} talents here, {data.Talents.Count} on the host's";
            return problem.Length == 0;
        }

        /// <summary>The built skater's ability as the host's (none, or the same at the host's level).</summary>
        private bool Ability(ForwardData forward, FreeAgentData data, out string problem)
        {
            problem = string.Empty;
            Ability? ability = forward.ability;
            if (data.AbilityId == null)
            {
                problem = ability == null ? string.Empty : "it has an ability here, none on the host's";
                return ability == null;
            }

            if (ability == null || ability.id != data.AbilityId)
            {
                problem = $"its ability \"{HostText.KeyForLog(data.AbilityId)}\" isn't in this game";
                return false;
            }

            if (ability.level != data.AbilityLevel)
            {
                Ability? leveled = Keep(_abilities!.GetAbility(ability.GetIl2CppType(), data.AbilityLevel, true));
                if (leveled == null || leveled.level != data.AbilityLevel || leveled.id != data.AbilityId)
                {
                    problem = $"its ability \"{HostText.KeyForLog(data.AbilityId)}\" isn't in this game at level {data.AbilityLevel}";
                    return false;
                }

                forward.ability = leveled;
                Drop(ability); // the deserializer's clone at the list's level
            }

            return true;
        }

        private static IntPtr Pointer(Il2CppSystem.Object? value) => value?.Pointer ?? IntPtr.Zero;
    }
}
#endif
