using System;
using System.Text;
using System.Text.RegularExpressions;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The game's own log lines that name a skater, a team or a logo (D127): in a custom team those names are
/// player-written, so each is reduced to its shape (<see cref="PlayerTextShape"/>: <c>&lt;a name: 11 characters, #4be1&gt;</c>).
/// Applied to Unity's <c>Player.log</c> in a bug report and to the game's warnings and errors the mod's log tap copies
/// (<c>[game Error] ...</c>). The game names a skater's object <c>"&lt;first name&gt; &lt;last name&gt;"</c> and its data by
/// its saved name, loads a custom team's data with its city as typed, and calls a custom logo <c>custom_&lt;file name&gt;</c>
/// (Nov 1 build: <c>GameControllerBase::InitializeForward</c>, <c>DeserializerV2::ConvertForwardDataModelToForwardData</c>,
/// <c>TeamDataAdapter::ConvertToTeamData</c>, <c>CustomLogoLoader::LoadSprites</c>). The lines below are the ones of the
/// game's code that write such a name (the same build: <c>CampaignState::TryLoadCustomTeam</c>,
/// <c>TeamAssetsRepositoryScriptableObject::GetLogo</c>, <c>Forward::InitializeSkin</c>, <c>CustomForward::InitializeSkin</c>,
/// <c>TeamData::SetDefensePair</c>, <c>Fighter::OnHit</c>, <c>Fighter::UpdateState</c>, <c>PunchState::OnEnter</c>, the
/// post-shot lines of <c>Forward</c>, <c>Forward::Equip</c>, <c>FaceIconSpriteProviderComponent::CanBake</c>,
/// <c>BenchPowerTest::Initialize</c>, and the exception lines of <c>Forward::OnDestroy</c> and <c>Goaltender::OnDestroy</c>),
/// and Unity's own lines that print a scene path through a skater's object (<c>Hockey/Teams/&lt;team&gt;/&lt;skater&gt;/...</c>).
/// Never throws: a line that can't be checked is replaced whole.
/// </summary>
public static class GameLogText
{
    /// <summary>What a line becomes when it couldn't be checked.</summary>
    public const string Unchecked = "(a game line couldn't be checked for player-written names, so it was left out)";

    private const string TeamsPath = "Hockey/Teams/";

    // A line's start, or the mod's log tap's prefix ("[game Error] ") in a session log's copy of it.
    private const string Start = @"(?:^|\[game [A-Za-z]+\] )";
    private const string Name = @"[^\r\n]";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    // Each: a word the line must hold (a cheap first check), and the pattern whose "n" groups are names.
    private static readonly (string Key, Regex Pattern)[] Lines =
    {
        ("Loaded custom team", Line($"{Start}Loaded custom team from team id: (?<n>{Name}+?)\\s*$")),
        ("Could not find logo", Line($"{Start}Could not find logo: (?<n>{Name}+?), returning default logo")),
        ("TEAMDATA is NULL", Line($"{Start}TEAMDATA is NULL: (?<n>{Name}+?)\\s*$")),
        ("SetDefensePair", Line($"{Start}\\[TeamData\\] SetDefensePair on '(?<n>{Name}*?)': '(?<n>{Name}*?)' DROPPED")),
        (" was hit", Line($"{Start}Player (?<n>{Name}+?) was hit\\s*$")),
        ("jabbed state", Line($"{Start}Player was hit, going to jabbed state (?<n>{Name}+?)\\s*$")),
        ("cannot punch", Line($"{Start}(?<n>{Name}+?) cannot punch without a Fighter and FighterConfig")),
        ("[PostShotBrake]", Line($"{Start}\\[PostShotBrake\\] (?<n>{Name}+?) (?:GOALIE CONTACT|result braked=)")),
        ("Error while equipping", Line($"{Start}Error while equipping (?<n>{Name}+?) to (?<n>{Name}+?): ")),
        ("TeamColorsData is null for", Line($"{Start}TeamColorsData is null for (?<n>{Name}+?)\\s*$")),
        (" initialized for team ", Line($"{Start}(?<n>{Name}+?) initialized for team (?<n>{Name}+?)\\s*$")),
        ("Exception", Line($"{Start}(?<n>[^:\\r\\n\\[]{{1,80}}): (?:[A-Za-z_][A-Za-z0-9_]*\\.)*[A-Za-z_][A-Za-z0-9_]*Exception\\b")),
    };

    /// <summary>
    /// <paramref name="text"/> (one line, or a message of a few) with the names of the lines above reduced to their shape
    /// by <paramref name="shape"/> (<see cref="PlayerTextShape.Describe"/>); <paramref name="reduced"/> counts them. The same
    /// instance when nothing is reduced. Never throws.
    /// </summary>
    public static string Reduce(string? text, Func<string?, string> shape, out int reduced)
    {
        ArgumentNullException.ThrowIfNull(shape);
        reduced = 0;
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            foreach ((string key, Regex pattern) in Lines)
            {
                if (text.Contains(key, StringComparison.Ordinal))
                {
                    text = ReplaceNames(text, pattern, shape, ref reduced);
                }
            }

            return text.Contains(TeamsPath, StringComparison.Ordinal) ? ReduceTeamPaths(text, shape, ref reduced) : text;
        }
        catch (Exception)
        {
            // A timeout or anything else: the line isn't kept as it was.
            reduced++;
            return Unchecked;
        }
    }

    private static Regex Line(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, Timeout);

    private static string Shaped(string name, Func<string?, string> shape) => $"<a name: {shape(name)}>";

    private static string ReplaceNames(string text, Regex pattern, Func<string?, string> shape, ref int reduced)
    {
        MatchCollection matches = pattern.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        // Every name of every match, replaced from the end so the earlier indexes stay right.
        var builder = new StringBuilder(text);
        for (int m = matches.Count - 1; m >= 0; m--)
        {
            CaptureCollection names = matches[m].Groups["n"].Captures;
            for (int c = names.Count - 1; c >= 0; c--)
            {
                Capture name = names[c];
                if (name.Length == 0)
                {
                    continue;
                }

                builder.Remove(name.Index, name.Length).Insert(name.Index, Shaped(name.Value, shape));
                reduced++;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>Hockey/Teams/&lt;team&gt;/&lt;object&gt;</c>: an object under a team named with a space is a skater's
    /// (<c>"&lt;first name&gt; &lt;last name&gt;"</c>); the game's own objects there have none (<c>GoaltenderNorth</c>).
    /// </summary>
    private static string ReduceTeamPaths(string text, Func<string?, string> shape, ref int reduced)
    {
        int at = text.IndexOf(TeamsPath, StringComparison.Ordinal);
        while (at >= 0)
        {
            int team = at + TeamsPath.Length;
            int slash = SegmentEnd(text, team);
            int next = slash + 1;
            if (slash < text.Length && text[slash] == '/')
            {
                int end = SegmentEnd(text, next);
                string segment = text[next..end];
                if (segment.Contains(' ', StringComparison.Ordinal))
                {
                    string replacement = Shaped(segment, shape);
                    text = string.Concat(text.AsSpan(0, next), replacement, text.AsSpan(end));
                    next += replacement.Length;
                    reduced++;
                }
                else
                {
                    next = end;
                }
            }

            at = next < text.Length ? text.IndexOf(TeamsPath, next, StringComparison.Ordinal) : -1;
        }

        return text;
    }

    /// <summary>Where a path segment starting at <paramref name="from"/> ends: its '/', a quote, a line end, or the text's end.</summary>
    private static int SegmentEnd(string text, int from)
    {
        int i = from;
        while (i < text.Length && text[i] is not ('/' or '"' or '\'' or '\r' or '\n'))
        {
            i++;
        }

        return i;
    }
}
