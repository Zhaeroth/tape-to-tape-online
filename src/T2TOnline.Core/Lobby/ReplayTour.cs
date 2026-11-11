using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Lobby;

/// <summary>A recording file the replay tour could try F2 on: where it is (a label in the log), and when it was written.</summary>
/// <param name="Path">Its path (only a label of it is ever logged).</param>
/// <param name="WrittenUtc">Its last write, UTC.</param>
public readonly record struct RecordingFile(string Path, DateTime WrittenUtc);

/// <summary>
/// The replay tour (<c>run.txt</c>: <c>replay</c>): a friend's whole match drawing on one PC, unattended, through the
/// developer keys' own handlers. A Play Now match started by the tour's pad through the game's controller screen; F3
/// records it for <see cref="RecordSeconds"/> with a real goal at <see cref="GoalAtSeconds"/> (the game's own scoring of a
/// puck in a net: the match goes on); F4 plays the recording back in place for <see cref="PlaybackSeconds"/>; the game's
/// quit; F2 loads the recording's match fresh, as a friend's game would, and plays it to its end; then F2 once on a
/// campaign recording, if one is there. Its steps, timings, lines and verdicts; the plugin's <c>LobbyUiCheck.Replay.cs</c>
/// runs them.
/// </summary>
public static class ReplayTour
{
    /// <summary>Its name in <c>run.txt</c>.</summary>
    public const string Name = "replay";

    /// <summary>How long F3 records.</summary>
    public const double RecordSeconds = 40;

    /// <summary>When, into the recording, the goal is forced.</summary>
    public const double GoalAtSeconds = 20;

    /// <summary>How long the tour waits, from <see cref="GoalAtSeconds"/>, for a loose puck to put in a net.</summary>
    public const double GoalWindowSeconds = 15;

    /// <summary>How long the game has to score a puck lying in a net.</summary>
    public const double GoalScoredSeconds = 3;

    /// <summary>How long F4 plays the recording back in place.</summary>
    public const double PlaybackSeconds = 20;

    /// <summary>The shortest recording that passes: the recorder's clock runs from its first snapshot, a little after F3.</summary>
    public const double MinRecordedSeconds = RecordSeconds - 5;

    /// <summary>The game's <c>State.GameState</c> for a shot on its way: the puck is loose.</summary>
    public const int DuringShot = 2;

    /// <summary>The game's <c>State.GameState</c> for a pass on its way: the puck is loose.</summary>
    public const int DuringPass = 3;

    /// <summary>The game's <c>State.GameState</c> when nobody has the puck.</summary>
    public const int PuckForGrabs = 4;

    /// <summary>How far inside a net's box the puck is held (metres), so it lies in the box and not on its edge.</summary>
    public const float NetMargin = 0.05f;

    /// <summary>The tour's time limit.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(8);

    /// <summary>Its steps, in order. All run in the player's own language and window (real menus that write: the controller screen).</summary>
    public static IReadOnlyList<UiCheckStep> Steps { get; } = new[]
    {
        new UiCheckStep("rp_playnow", "Play Now by the tour's pad through the game's controller screen: the match loads and plays", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("rp_record", "F3 records the match 40 s, a real goal at 20 s by the game's own scoring; the file read back", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("rp_playback", "F4 plays the recording back in place 20 s, client mode: no failed states", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("rp_quit", "The game's own quit; the title; Play Now's writes refused by the save guard", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("rp_rehearsal", "F2 loads the recording's match fresh as a friend's game would: exactly its setup, played to its end; the game's quit", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("rp_campaign", "F2 once on a campaign recording, if one is there: what happens", Needs: UiCheckNeeds.PlayersSettings),
        new UiCheckStep("finish", "Everything back; the files compared", UiCheckStepKind.Finish),
    };

    /// <summary>True when the puck is loose in <paramref name="gameState"/>: a shot or a pass on its way, or nobody's (the game's own goal check wants it unpossessed).</summary>
    public static bool IsLoose(int gameState) => gameState is DuringShot or DuringPass or PuckForGrabs;

    /// <summary>
    /// Where the puck goes to lie in a net whose trigger box spans <paramref name="min"/> to <paramref name="max"/> with
    /// <paramref name="center"/>: the box's centre across the ice, at the puck's own height (<paramref name="puckY"/>, on the
    /// ice) held inside the box by <see cref="NetMargin"/> (the box's middle when it is thinner than that).
    /// </summary>
    public static (float X, float Y, float Z) PuckTarget((float X, float Y, float Z) center, (float X, float Y, float Z) min, (float X, float Y, float Z) max, float puckY)
    {
        float low = min.Y + NetMargin;
        float high = max.Y - NetMargin;
        float y = !float.IsFinite(puckY) || low > high ? center.Y : Math.Clamp(puckY, low, high);
        return (center.X, y, center.Z);
    }

    /// <summary>A file's size in words: "640 KB", "3.2 MB".</summary>
    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(bytes, 0) / 1024} KB");

    /// <summary>The recording's line: <c>replay: recorded 2400 frames in 40.0 s (3.2 MB): dev/ui-check/.../f.t2tstream</c>.</summary>
    public static string RecordedLine(int frames, double seconds, long bytes, string label) =>
        string.Create(CultureInfo.InvariantCulture, $"replay: recorded {frames} frames in {seconds:0.0} s ({Size(bytes)}): {label}");

    /// <summary>What's wrong with the recording read back, or null: none, too short, or no frames.</summary>
    public static string? RecordingProblem(bool read, int frames, double seconds)
    {
        if (!read)
        {
            return "the recording doesn't read back";
        }

        if (frames <= 0)
        {
            return "the recording has no frames";
        }

        return seconds < MinRecordedSeconds
            ? string.Create(CultureInfo.InvariantCulture, $"the recording is {seconds:0.0} s, under {MinRecordedSeconds:0} s")
            : null;
    }

    /// <summary>F4's line: <c>replay: played back 1200 frames, 0 failed (F4 pressed)</c>.</summary>
    public static string PlayedLine(long frames, long failed, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"replay: played back {frames} frames, {failed} failed ({reason})");

    /// <summary>F2's line: <c>replay: rehearsal loaded &lt;setup&gt; and played 2400 frames, 0 failed</c>.</summary>
    public static string RehearsalLine(string setup, long frames, long failed) =>
        string.Create(CultureInfo.InvariantCulture, $"replay: rehearsal loaded {setup} and played {frames} frames, {failed} failed");

    /// <summary>What's wrong with a playback, or null: nothing shown, or object states that failed to apply.</summary>
    public static string? PlaybackProblem(long frames, long failed)
    {
        if (frames <= 0)
        {
            return "no frame was shown";
        }

        return failed > 0 ? string.Create(CultureInfo.InvariantCulture, $"{failed} object states failed to apply") : null;
    }

    /// <summary>
    /// The writes the save guard refused between two of its counts (each by a "Method(files)" key): the keys that grew, by how
    /// much, sorted by key; and their total.
    /// </summary>
    public static (IReadOnlyList<KeyValuePair<string, long>> Grown, long Total) RefusedSince(IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var grown = new List<KeyValuePair<string, long>>();
        long total = 0;
        foreach (KeyValuePair<string, long> now in after.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            long more = now.Value - (before.TryGetValue(now.Key, out long was) ? was : 0);
            if (more > 0)
            {
                grown.Add(new KeyValuePair<string, long>(now.Key, more));
                total += more;
            }
        }

        return (grown, total);
    }

    /// <summary>The save guard's line for <see cref="RefusedSince"/>'s result.</summary>
    public static string RefusedLine(IReadOnlyList<KeyValuePair<string, long>> grown, long total) => total == 0
        ? "replay: the save guard refused no write during the Play Now match"
        : string.Create(CultureInfo.InvariantCulture, $"replay: the save guard refused {total} writes during the Play Now match: {string.Join(", ", grown.Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Value}x")))}");

    /// <summary>
    /// The newest of <paramref name="files"/> that <paramref name="isCampaign"/> says is a campaign match's recording, looking
    /// at no more than <paramref name="most"/> of them, newest first; null when none is. <paramref name="looked"/> counts
    /// those read.
    /// </summary>
    public static RecordingFile? NewestCampaign(IEnumerable<RecordingFile> files, Func<string, bool> isCampaign, int most, out int looked)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(isCampaign);
        looked = 0;
        foreach (RecordingFile file in files.OrderByDescending(f => f.WrittenUtc).ThenBy(f => f.Path, StringComparer.Ordinal).Take(Math.Max(most, 0)))
        {
            looked++;
            if (isCampaign(file.Path))
            {
                return file;
            }
        }

        return null;
    }
}
