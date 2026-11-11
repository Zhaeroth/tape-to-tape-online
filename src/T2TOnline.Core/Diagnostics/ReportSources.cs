namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Where a problem report was asked from (D140): the log's <c>Bug report: asked from ...</c> line and the report's own manifest
/// (<c>Made at ... UTC (Settings > Online).</c>) name it in these words. Plain English, the same in every language: they're read in a log.
/// </summary>
public static class ReportSources
{
    /// <summary>The Settings menu's Online tab, its Problem Report row.</summary>
    public const string SettingsOnline = "Settings > Online";

    /// <summary>The title's Online menu, its Report a Problem row.</summary>
    public const string OnlineMenu = "the Online menu";

    /// <summary>The offer at the title after something went wrong, its Make Report.</summary>
    public const string Offer = "the offer";

    /// <summary>Insert held for 2 s where the native screens aren't used (an untested game build).</summary>
    public const string InsertHeld = "Insert held";

    /// <summary>Every source a player can ask from, for the tests.</summary>
    public static readonly string[] All = { SettingsOnline, OnlineMenu, Offer, InsertHeld };
}
