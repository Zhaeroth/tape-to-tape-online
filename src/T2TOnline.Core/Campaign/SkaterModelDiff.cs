using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Compares two skaters field by field (the host's, as sent, and the one a friend's game built and read back with the game's
/// own serializer), so a friend can tell its copy is exactly the host's and say where it isn't: "speed (68 here, 70 on the
/// host's)". Colours and floats compare bit for bit (they travel as the same floats). Never throws.
/// </summary>
public static class SkaterModelDiff
{
    /// <summary>
    /// The first field of <paramref name="here"/> that isn't <paramref name="host"/>'s, in words, or null when every field is
    /// the same. <paramref name="ignoreFlavor"/>: leave the three flavour-text keys out (a model read back from the game's
    /// serializer has none: the run file doesn't keep them). The look a <see cref="FreeAgentData.Version2"/> agent carries
    /// (its <see cref="FreeAgentData.Look"/>, birth date and number format) is compared when either has one.
    /// </summary>
    public static string? FirstDifference(FreeAgentData host, FreeAgentData here, bool ignoreFlavor = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(here);
        string? differs =
            Text("id", host.Id, here.Id) ?? Text("template", host.OriginalId, here.OriginalId) ?? Name("asset name", host.Name, here.Name) ??
            Name("first name", host.FirstName, here.FirstName) ?? Name("last name", host.LastName, here.LastName) ??
            Whole("number", host.Number, here.Number) ?? Whole("checking", host.Checking, here.Checking) ?? Whole("speed", host.Speed, here.Speed) ??
            Whole("shot accuracy", host.ShotAccuracy, here.ShotAccuracy) ?? Whole("shot power", host.ShotPower, here.ShotPower) ??
            Number("injury counter", host.InjuryCounter, here.InjuryCounter) ?? Flag("lefty", host.IsLefty, here.IsLefty) ?? Flag("isBlack", host.IsBlack, here.IsBlack) ??
            Flag("oversized helmet", host.HasOversizedHelmet, here.HasOversizedHelmet) ?? Whole("rarity", host.ForwardRarity, here.ForwardRarity) ??
            Whole("size", host.SkaterSize, here.SkaterSize) ?? Whole("type", host.DefaultSkaterType, here.DefaultSkaterType) ??
            Whole("category", host.SkaterCategory, here.SkaterCategory) ?? Skins(host.Skins, here.Skins) ?? Schemes("colour schemes", host.Colors, here.Colors) ??
            Text("ability", host.AbilityId, here.AbilityId) ?? Whole("ability level", host.AbilityLevel, here.AbilityLevel) ?? Talents(host.Talents, here.Talents) ??
            AgentLook(host, here);
        if (differs != null || ignoreFlavor)
        {
            return differs;
        }

        return Text("flavour text key", host.FlavorKey, here.FlavorKey) ?? Text("perk text key", host.PerkFlavorKey, here.PerkFlavorKey) ??
            Text("power-up text key", host.PowerUpTextKey, here.PowerUpTextKey);
    }

    /// <summary>
    /// The first field of a run-start skater <paramref name="here"/> that isn't <paramref name="host"/>'s, in words, or null when
    /// every field is the same: its model, its look, then its back number's format (the run file's model has none, so a skater
    /// built from it is regular until it's given the host's).
    /// </summary>
    public static string? FirstDifference(RunStartSkater host, RunStartSkater here)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(here);
        return FirstDifference(host.Model, here.Model) ?? FirstDifference(host.Look, here.Look) ?? Whole("number format", host.NumberFormat, here.NumberFormat);
    }

    private static string? AgentLook(FreeAgentData host, FreeAgentData here)
    {
        if (host.Look == null || here.Look == null)
        {
            return (host.Look == null) == (here.Look == null) ? null : $"look ({(here.Look == null ? "none" : "some")} here, {(host.Look == null ? "none" : "some")} on the host's)";
        }

        return FirstDifference(host.Look, here.Look) ?? (host.BirthDate == here.BirthDate ? null : $"birth date ({here.BirthDate} here, {host.BirthDate} on the host's)") ??
            Whole("number format", host.NumberFormat, here.NumberFormat);
    }

    /// <summary>The first field of <paramref name="here"/>'s look that isn't <paramref name="host"/>'s, in words, or null when they're the same.</summary>
    public static string? FirstDifference(SkaterLook host, SkaterLook here)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(here);
        return Whole("hidden shot power", host.HiddenShotPower, here.HiddenShotPower) ?? Whole("hidden speed", host.HiddenSpeed, here.HiddenSpeed) ??
            Whole("hidden checking", host.HiddenChecking, here.HiddenChecking) ?? Whole("hidden shot accuracy", host.HiddenShotAccuracy, here.HiddenShotAccuracy) ??
            Flag("isFemale", host.IsFemale, here.IsFemale) ?? Flag("golden helmet", host.HasGoldenHelmet, here.HasGoldenHelmet) ??
            Text("glasses", host.GlassesSkin, here.GlassesSkin) ?? Text("away helmet", host.HelmetAwaySkin, here.HelmetAwaySkin) ??
            Number("size offset", host.SizeOffset, here.SizeOffset) ?? Colour("number colour", host.NumberColor, here.NumberColor) ??
            Scheme("stick colour", host.StickColor, here.StickColor) ?? Scheme("skate colour", host.SkatesColor, here.SkatesColor) ??
            Whole("height", host.HeightInches, here.HeightInches) ?? Whole("weight", host.WeightKg, here.WeightKg);
    }

    /// <summary>The first colour scheme that differs between two teams' colours (null: none on either side), in words, or null.</summary>
    public static string? SchemesDiffer(string what, IReadOnlyList<ColorSchemeData?>? host, IReadOnlyList<ColorSchemeData?>? here) => Schemes(what, host, here);

    private static string? Skins(IReadOnlyList<string?> host, IReadOnlyList<string?> here)
    {
        if (host.Count != here.Count)
        {
            return $"skins ({here.Count} here, {host.Count} on the host's)";
        }

        for (int i = 0; i < host.Count; i++)
        {
            string name = i < FreeAgentData.SkinNames.Count ? FreeAgentData.SkinNames[i] : $"skin {i + 1}";
            string? differs = Text(name, Empty(host[i]), Empty(here[i]));
            if (differs != null)
            {
                return differs;
            }
        }

        return null;
    }

    private static string? Talents(IReadOnlyList<FreeAgentTalent> host, IReadOnlyList<FreeAgentTalent> here)
    {
        if (host.Count != here.Count)
        {
            return $"talents ({here.Count} here, {host.Count} on the host's)";
        }

        for (int i = 0; i < host.Count; i++)
        {
            string? differs = Text($"talent {i + 1}", host[i].Id, here[i].Id) ?? Whole($"talent {i + 1}'s level", host[i].Level, here[i].Level) ??
                Whole($"talent {i + 1}'s contract games", host[i].ContractGames, here[i].ContractGames);
            if (differs != null)
            {
                return differs;
            }
        }

        return null;
    }

    private static string? Schemes(string what, IReadOnlyList<ColorSchemeData?>? host, IReadOnlyList<ColorSchemeData?>? here)
    {
        if (host == null || here == null)
        {
            return (host == null) == (here == null) ? null : $"{what} ({(here == null ? "none" : "some")} here, {(host == null ? "none" : "some")} on the host's)";
        }

        if (host.Count != here.Count)
        {
            return $"{what} ({here.Count} here, {host.Count} on the host's)";
        }

        for (int i = 0; i < host.Count; i++)
        {
            string name = i < FreeAgentData.SchemeNames.Count ? FreeAgentData.SchemeNames[i] : $"scheme {i + 1}";
            string? differs = Scheme($"{what}' {name}", host[i], here[i]);
            if (differs != null)
            {
                return differs;
            }
        }

        return null;
    }

    private static string? Scheme(string what, ColorSchemeData? host, ColorSchemeData? here)
    {
        if (host == null || here == null)
        {
            return (host == null) == (here == null) ? null : $"{what} ({(here == null ? "none" : "set")} here, {(host == null ? "none" : "set")} on the host's)";
        }

        return Colour($"{what} (primary)", host.Primary, here.Primary) ?? Colour($"{what} (secondary)", host.Secondary, here.Secondary) ??
            Colour($"{what} (tertiary)", host.Tertiary, here.Tertiary);
    }

    private static string? Colour(string what, ColorValue host, ColorValue here) =>
        Same(host.R, here.R) && Same(host.G, here.G) && Same(host.B, here.B) && Same(host.A, here.A) ? null : $"{what} ({Show(here)} here, {Show(host)} on the host's)";

    private static string Show(ColorValue color) => string.Create(CultureInfo.InvariantCulture, $"{color.R:0.###}/{color.G:0.###}/{color.B:0.###}/{color.A:0.###}");

    private static string? Text(string what, string? host, string? here) =>
        string.Equals(host, here, StringComparison.Ordinal) ? null : $"{what} ({Quote(here)} here, {Quote(host)} on the host's)";

    private static string? Whole(string what, long host, long here) =>
        host == here ? null : string.Create(CultureInfo.InvariantCulture, $"{what} ({here} here, {host} on the host's)");

    private static string? Number(string what, float host, float here) =>
        Same(host, here) ? null : string.Create(CultureInfo.InvariantCulture, $"{what} ({here:R} here, {host:R} on the host's)");

    private static string? Flag(string what, bool host, bool here) => host == here ? null : $"{what} ({(here ? "yes" : "no")} here, {(host ? "yes" : "no")} on the host's)";

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b) || a == b;

    private static string? Empty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // A skater's name: a host sends it, and a custom skater's is typed by its player: its length only.
    private static string? Name(string what, string? host, string? here) =>
        string.Equals(host, here, StringComparison.Ordinal) ? null
            : $"{what} ({(here == null ? "none" : Diagnostics.HostText.TextShape(here))} here, {(host == null ? "none" : Diagnostics.HostText.TextShape(host))} on the host's)";

    // An id, a key or a skin's name, as a host sends it: by Diagnostics.HostText.KeyForLog.
    private static string Quote(string? text) => text == null ? "none" : $"\"{Diagnostics.HostText.KeyForLog(text)}\"";
}
