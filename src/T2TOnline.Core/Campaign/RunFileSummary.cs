using System;
using System.Text.Json;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// What a run file says about its run, for the co-op slots in the mod's panel (D46): the squad, act and floor, the run's
/// id, and whether it failed. A run file (<c>RunDataV2.es3</c>, or a co-op slot) is Easy Save 3 JSON: each key holds
/// <c>{"__type": ..., "value": ...}</c>, and the run is under <c>runData</c> (<c>RunDataModelV2</c>: <c>squadId</c>,
/// <c>currentAct</c>, <c>currentFloor</c>, <c>runID</c>, <c>isRunFailed</c>). Read tolerantly: a field that's missing or of
/// another kind is left at its default. Never throws.
/// </summary>
/// <param name="SquadId">The squad's id (<c>vanilla</c>, <c>oldschool</c>, ...), or empty.</param>
/// <param name="Act">The act (1-3), or 0.</param>
/// <param name="Floor">The floor within the act, or 0.</param>
/// <param name="RunId">The run's id, or empty.</param>
/// <param name="Failed">The run is over (lost); the game won't continue it.</param>
public sealed record RunFileSummary(string SquadId, int Act, int Floor, string RunId, bool Failed)
{
    /// <summary>
    /// Reads the run in a run file's bytes. False when they aren't JSON or hold no <c>runData</c> object. A file that is
    /// JSON but for the numbers Easy Save writes as bare words (<see cref="EasySaveNumbers"/>: a float that isn't finite)
    /// is read with those taken as nothing: the game loads such a run, so it is one here too.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out RunFileSummary? summary)
    {
        ReadOnlySpan<byte> json = WithoutBom(bytes);
        if (json.IsEmpty)
        {
            summary = null;
            return false;
        }

        if (TryParse(json, out summary, out bool notJson))
        {
            return true;
        }

        // Only a file plain JSON parsing refused is looked at again (never one that parsed and held no run).
        return notJson && EasySaveNumbers.WithoutNonFinite(json) is { } finite && TryParse(finite, out summary, out _);
    }

    /// <summary>Reads the run in <paramref name="json"/>; <paramref name="notJson"/>: the bytes didn't parse as JSON at all.</summary>
    private static bool TryParse(ReadOnlySpan<byte> json, out RunFileSummary? summary, out bool notJson)
    {
        summary = null;
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

            summary = new RunFileSummary(Text(run, "squadId"), Whole(run, "currentAct"), Whole(run, "currentFloor"), Text(run, "runID"), Flag(run, "isRunFailed"));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            notJson = ex is JsonException;
            return false;
        }
    }

    /// <summary>True when <paramref name="bytes"/> start like JSON (an object, after any byte order mark and white space).</summary>
    public static bool LooksLikeJson(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in WithoutBom(bytes))
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return b == (byte)'{';
        }

        return false;
    }

    private static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;

    private static string Text(JsonElement run, string name)
    {
        if (!run.TryGetProperty(name, out JsonElement found) || found.ValueKind != JsonValueKind.String)
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

    private static int Whole(JsonElement run, string name)
    {
        if (!run.TryGetProperty(name, out JsonElement found) || found.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (found.TryGetInt32(out int whole))
        {
            return whole;
        }

        return found.TryGetDouble(out double number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : 0;
    }

    private static bool Flag(JsonElement run, string name) =>
        run.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.True;
}
