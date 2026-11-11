using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Who skates for a run's team, read from the run file's bytes, so both PCs can say the same thing about a checkpoint: the free
/// agent a host signed, the skater a Nurse took. Under the file's <c>runData</c>, <c>team.skaters</c> holds each seat's
/// <c>{"__type": "State.ForwardDataModelV2,...", "id", "firstName", "lastName", "number", ...}</c>, null for an empty seat and a
/// <c>GoaltenderDataModelV2</c> for the goalie, who is left out (checked against real co-op slot files). A skater is kept by its
/// id and number only: a custom skater's names are typed by a player, and on a friend the file is the host's bytes, so the
/// names are never read (D127, D146). Read tolerantly: what's missing or of another kind is left out. Never throws.
/// </summary>
public sealed class RunTeamRoster
{
    /// <summary>Most seats read (a run's team has eleven).</summary>
    public const int MaxSkaters = 64;

    /// <summary>Most skaters written per direction in a change's line.</summary>
    public const int MaxNamed = 8;

    private const int RunIdChars = 40;

    // One more than the longest id a line writes: a longer one keeps its cut mark, and is then written as no id at all.
    private const int IdChars = ObjectEntryLogText.MaxIdLength + 1;

    private RunTeamRoster(string runId, IReadOnlyList<RosterSkater> skaters)
    {
        RunId = runId;
        Skaters = skaters;
    }

    /// <summary>The run's id, or "".</summary>
    public string RunId { get; }

    /// <summary>The team's skaters, in the file's order.</summary>
    public IReadOnlyList<RosterSkater> Skaters { get; }

    /// <summary>
    /// Reads the team in a run file's bytes. False when they aren't JSON or hold no <c>runData</c> object with a <c>team</c>.
    /// A file that is JSON but for the numbers Easy Save writes as bare words (<see cref="EasySaveNumbers"/>: a float that isn't
    /// finite, such as a skater's) is read with those taken as nothing: the game loads such a run.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out RunTeamRoster? roster)
    {
        roster = null;
        ReadOnlySpan<byte> json = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
        if (json.IsEmpty)
        {
            return false;
        }

        if (TryParse(json, out roster, out bool notJson))
        {
            return true;
        }

        // Only a file plain JSON parsing refused is looked at again (never one that parsed and held no team).
        return notJson && EasySaveNumbers.WithoutNonFinite(json) is { } finite && TryParse(finite, out roster, out _);
    }

    /// <summary>Reads the team in <paramref name="json"/>; <paramref name="notJson"/>: the bytes didn't parse as JSON at all.</summary>
    private static bool TryParse(ReadOnlySpan<byte> json, out RunTeamRoster? roster, out bool notJson)
    {
        roster = null;
        notJson = false;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("runData", out JsonElement run) || run.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Easy Save wraps each value with its type; take the value when it's there.
            if (run.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.Object)
            {
                run = value;
            }

            if (!run.TryGetProperty("team", out JsonElement team) || team.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var skaters = new List<RosterSkater>();
            if (team.TryGetProperty("skaters", out JsonElement seats) && seats.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement seat in seats.EnumerateArray().Take(MaxSkaters))
                {
                    if (seat.ValueKind == JsonValueKind.Object && !Text(seat, "__type").Contains("Goaltender", StringComparison.Ordinal) && Text(seat, "id") is { Length: > 0 } id)
                    {
                        skaters.Add(new RosterSkater(SafeText.Clean(id, IdChars), Whole(seat, "number")));
                    }
                }
            }

            roster = new RunTeamRoster(SafeText.Clean(Text(run, "runID"), RunIdChars), skaters);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            notJson = ex is JsonException;
            return false;
        }
    }

    /// <summary>
    /// What changed since <paramref name="before"/>: "+skater 1a2b3c4d-... #50, -skater 0f2b7633-... #93 (now 6 skaters)", or null when
    /// nothing did, or when there's nothing to compare with (no earlier checkpoint, or it's another run: a new team isn't a change).
    /// </summary>
    public string? ChangesSince(RunTeamRoster? before)
    {
        if (before == null || before.RunId != RunId)
        {
            return null;
        }

        var had = new HashSet<string>(before.Skaters.Select(s => s.Id), StringComparer.Ordinal);
        var has = new HashSet<string>(Skaters.Select(s => s.Id), StringComparer.Ordinal);
        List<RosterSkater> added = Skaters.Where(s => !had.Contains(s.Id)).ToList();
        List<RosterSkater> gone = before.Skaters.Where(s => !has.Contains(s.Id)).ToList();
        if (added.Count == 0 && gone.Count == 0)
        {
            return null;
        }

        IEnumerable<string> parts = Named(added, '+').Concat(Named(gone, '-'));
        return $"{string.Join(", ", parts)} (now {Skaters.Count} skaters)";
    }

    private static IEnumerable<string> Named(List<RosterSkater> skaters, char sign)
    {
        foreach (RosterSkater skater in skaters.Take(MaxNamed))
        {
            yield return $"{sign}{skater}";
        }

        if (skaters.Count > MaxNamed)
        {
            yield return $"{sign}{skaters.Count - MaxNamed} more";
        }
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement found) || found.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        try
        {
            return found.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            // JSON parsing doesn't check a string's bytes; reading it does (invalid UTF-8, an escaped lone surrogate).
            return string.Empty;
        }
    }

    private static int Whole(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out int whole) ? whole : 0;
}

/// <summary>One skater of a <see cref="RunTeamRoster"/>: by its id and number, never its names.</summary>
/// <param name="Id">The skater's id, as the run file has it (cleaned, cut).</param>
/// <param name="Number">Jersey number.</param>
public readonly record struct RosterSkater(string Id, int Number)
{
    /// <summary>
    /// "skater 1a2b3c4d-... #50", as every log line writes a skater (<see cref="ObjectEntryLogText.Skater"/>: "a skater #50"
    /// when the id isn't shaped like one).
    /// </summary>
    public override string ToString() => $"{ObjectEntryLogText.Skater(Id)} #{Number}";
}

/// <summary>How a run in memory compares with one noted before (<see cref="OwnRunCheck"/>).</summary>
public enum OwnRunVerdict
{
    /// <summary>The same run, at the same act and floor (or no run both times).</summary>
    Same,

    /// <summary>The same run, at another act or floor.</summary>
    SameRunElsewhere,

    /// <summary>Another run, or a run where there was none (or the other way round).</summary>
    Other,
}

/// <summary>
/// A friend's own run around a session: the run its file held before following, in words, and whether the
/// run in memory once the title has reloaded it is that run. The save check proves the file; this says what the game loaded.
/// </summary>
public static class OwnRunCheck
{
    /// <summary>
    /// "run 0a1b2c3d-..., squad tchel, act 1, floor 3", or "none". The ids are read from a run file: each is written only when it
    /// has an id's shape (<see cref="HostText.KeyForLog(string?)"/>), as every id read from a file or a peer is.
    /// </summary>
    public static string Words(RunFileSummary? run) => !HoldsRun(run) ? "none"
        : $"run {HostText.KeyForLog(run!.RunId)}, squad {HostText.KeyForLog(run.SquadId)}, act {run.Act}, floor {run.Floor}";

    /// <summary>Compares the run noted before following with the one in memory now.</summary>
    public static OwnRunVerdict Compare(RunFileSummary? before, RunFileSummary? now)
    {
        if (!HoldsRun(before) || !HoldsRun(now))
        {
            return HoldsRun(before) == HoldsRun(now) ? OwnRunVerdict.Same : OwnRunVerdict.Other;
        }

        if (before!.RunId != now!.RunId || before.SquadId != now.SquadId)
        {
            return OwnRunVerdict.Other;
        }

        return before.Act == now.Act && before.Floor == now.Floor ? OwnRunVerdict.Same : OwnRunVerdict.SameRunElsewhere;
    }

    /// <summary>
    /// The line once the run is back. <paramref name="fileSame"/>: whether the solo run file is unchanged since the session's start
    /// (null: not known). <paramref name="known"/>: the run before following could be read (else nothing is compared).
    /// </summary>
    public static string Line(RunFileSummary? before, RunFileSummary? now, bool known, bool? fileSame, string fileName)
    {
        string compared = !known ? "the run before following couldn't be read, so it isn't compared" : Compare(before, now) switch
        {
            OwnRunVerdict.Same => "the same as before following",
            OwnRunVerdict.SameRunElsewhere => $"the same run as before following, which was at act {before!.Act}, floor {before.Floor}",
            _ => $"NOT the same as before following: {Words(before)}",
        };
        string file = fileSame switch
        {
            true => "unchanged",
            false => "changed",
            null => "not checked",
        };
        return $"Run sync: your own run is in memory again: {Words(now)} ({compared}; {fileName} {file} since the session's start).";
    }

    private static bool HoldsRun(RunFileSummary? run) => run != null && run.RunId.Length > 0 && run.SquadId.Length > 0; // the game's reset gives a new id before any squad: that's no run
}
