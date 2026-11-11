using System;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>Where the game was when a moment was marked.</summary>
public enum MarkPlace
{
    /// <summary>The title screen.</summary>
    Title,

    /// <summary>Any menu or screen that isn't a match or the title (the world map, an event, a loading screen).</summary>
    Menu,

    /// <summary>A match (an exhibition or a campaign match).</summary>
    Match,
}

/// <summary>
/// The marks a player sets for a bug report ("something just happened"): numbered from 1 in each launch, each written to
/// the log as <c>MARK 2 (in a match) at 05:21:13 UTC: ...</c>, which a bug report's manifest lists and a reader finds by
/// its number. Not thread-safe (the game's thread only).
/// </summary>
public sealed class BugMarks
{
    /// <summary>What every mark's line starts with.</summary>
    public const string Prefix = "MARK ";

    /// <summary>The marks set so far this launch.</summary>
    public int Count { get; private set; }

    /// <summary>The words for <paramref name="place"/> in a mark's line: "in a match", "in a menu", "at the title".</summary>
    public static string Words(MarkPlace place) => place switch
    {
        MarkPlace.Match => "in a match",
        MarkPlace.Title => "at the title",
        _ => "in a menu",
    };

    /// <summary>
    /// The next mark: its number and its log line, <c>MARK 2 (in a match) at 05:21:13 UTC: a moment marked for a bug report
    /// (the Insert key).</c> <paramref name="how"/> says what set it ("the Insert key").
    /// </summary>
    public string Next(MarkPlace place, DateTime utcNow, string how, out int number)
    {
        number = ++Count;
        DateTime utc = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : utcNow;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{number} ({Words(place)}) at {utc:HH:mm:ss} UTC: a moment marked for a bug report ({how}).");
    }
}
