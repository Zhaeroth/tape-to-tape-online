using System;
using System.Collections.Generic;
using System.Text.Json;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// Compares two run files (Easy Save 3 JSON, as the game's run repository writes them) value by value: every object's
/// members by name, every array's items in order, every number, string and flag. The run-start carrier's self-test uses it
/// to check that a run written, loaded the way a friend's game loads it and written again is the same run (DevTools'
/// Ctrl+F11). Reads tolerantly (a byte order mark, comments and trailing commas, as the game's own files may have, and the
/// bare <c>NaN</c> and <c>Infinity</c> Easy Save writes for a float that isn't finite, compared as those words:
/// <see cref="EasySaveNumbers.WithNonFiniteAsText"/>); never throws. A run's team can be a custom one, whose names are typed by
/// a player: a text under a member that holds a name (<c>firstName</c>, <c>lastName</c>, <c>name</c>, <c>displayName</c>,
/// <c>teamName</c>, <c>city</c>, <c>nickname</c>, <c>logoName</c>, ...) is written only as its shape (D127).
/// </summary>
public static class RunFileDiff
{
    /// <summary>Deepest nesting followed (a run file is about a dozen deep).</summary>
    public const int MaxDepth = 64;

    // The members of a run file whose text is a name a player can type (a skater's first, last and full name, a scorer's, a
    // team's name, city and nickname, a logo's file), or a squad's name: their values are written by their shape only.
    private static readonly HashSet<string> NameMembers = new(StringComparer.Ordinal)
    {
        "firstName", "lastName", "name", "displayName", "teamName", "city", "nickname", "logoName", "squadName", "playerName",
    };

    /// <summary>
    /// Compares <paramref name="first"/> with <paramref name="second"/>. False when either isn't JSON. Otherwise true, with
    /// <paramref name="differences"/> holding up to <paramref name="max"/> of the differing values by path
    /// ("runData.value.team.forwards[3].speed: 68, then 70"; a name's text by <paramref name="shape"/>:
    /// "runData.value.team.skaters[0].firstName: (5 characters, #4be1), then (6 characters, #0c2d)"; empty when the two are the
    /// same run), <paramref name="total"/> how many there were, and <paramref name="values"/> how many values were compared.
    /// </summary>
    /// <param name="first">The first run file's bytes.</param>
    /// <param name="second">The second run file's bytes.</param>
    /// <param name="max">Most differences kept.</param>
    /// <param name="shape">Player-written text as its shape (<see cref="Diagnostics.PlayerTextShape.Describe"/>, which never throws).</param>
    /// <param name="differences">The differences kept, by path.</param>
    /// <param name="total">How many differences there were.</param>
    /// <param name="values">How many values were compared.</param>
    public static bool TryCompare(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, int max, Func<string?, string> shape, out List<string> differences, out int total, out int values)
    {
        ArgumentNullException.ThrowIfNull(shape);
        differences = new List<string>();
        total = 0;
        values = 0;
        try
        {
            using JsonDocument a = Parse(first);
            using JsonDocument b = Parse(second);
            var walk = new Walk(Math.Max(0, max), differences, shape);
            walk.Compare(a.RootElement, b.RootElement, string.Empty, 0);
            total = walk.Total;
            values = walk.Values;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            differences.Clear();
            return false;
        }
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> json = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
        try
        {
            return ParseJson(json);
        }
        catch (JsonException) when (EasySaveNumbers.WithNonFiniteAsText(json) is { } words)
        {
            // Easy Save's non-finite floats: read again with each word as a text, so "NaN" and "Infinity" stay apart.
            return ParseJson(words);
        }
    }

    private static JsonDocument ParseJson(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = MaxDepth + 8 });
        JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (reader.Read())
        {
            // A second value after the file's one (the reader throws for most such text first).
            document.Dispose();
            throw new JsonException("Something follows the run.");
        }

        return document;
    }

    private sealed class Walk
    {
        private readonly int _max;
        private readonly List<string> _differences;
        private readonly Func<string?, string> _shape;

        public Walk(int max, List<string> differences, Func<string?, string> shape)
        {
            _max = max;
            _differences = differences;
            _shape = shape;
        }

        public int Total { get; private set; }

        public int Values { get; private set; }

        public void Compare(JsonElement a, JsonElement b, string path, int depth)
        {
            if (depth > MaxDepth)
            {
                Differ(path, "nested too deep to compare", string.Empty);
                return;
            }

            if (a.ValueKind != b.ValueKind && !(IsFlag(a) && IsFlag(b)))
            {
                Differ(path, Shown(a, path), Shown(b, path));
                return;
            }

            switch (a.ValueKind)
            {
                case JsonValueKind.Object:
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonProperty member in a.EnumerateObject())
                    {
                        seen.Add(member.Name);
                        string at = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
                        if (b.TryGetProperty(member.Name, out JsonElement other))
                        {
                            Compare(member.Value, other, at, depth + 1);
                        }
                        else
                        {
                            Differ(at, Shown(member.Value, at), "missing");
                        }
                    }

                    foreach (JsonProperty member in b.EnumerateObject())
                    {
                        if (!seen.Contains(member.Name))
                        {
                            string at = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
                            Differ(at, "missing", Shown(member.Value, at));
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    int count = a.GetArrayLength(), otherCount = b.GetArrayLength();
                    if (count != otherCount)
                    {
                        Differ($"{path}.length", count.ToString(System.Globalization.CultureInfo.InvariantCulture), otherCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    var others = new List<JsonElement>(b.EnumerateArray());
                    int index = 0;
                    foreach (JsonElement item in a.EnumerateArray())
                    {
                        if (index >= others.Count)
                        {
                            break;
                        }

                        Compare(item, others[index], $"{path}[{index}]", depth + 1);
                        index++;
                    }

                    break;
                default:
                    Values++;
                    string left = a.GetRawText(), rightText = b.GetRawText();
                    if (!string.Equals(left, rightText, StringComparison.Ordinal) && !(a.ValueKind == JsonValueKind.Number && SameNumber(a, b)))
                    {
                        Differ(path, Shown(a, path), Shown(b, path));
                    }

                    break;
            }
        }

        private static bool IsFlag(JsonElement element) => element.ValueKind is JsonValueKind.True or JsonValueKind.False;

        private static bool SameNumber(JsonElement a, JsonElement b) => a.TryGetDouble(out double x) && b.TryGetDouble(out double y) && x.Equals(y);

        /// <summary>The last member a path names: "firstName" for "team.skaters[2].firstName", "names" for "team.names[0]".</summary>
        private static string MemberOf(string path)
        {
            int end = path.Length;
            while (end > 0 && path[end - 1] == ']')
            {
                int open = path.LastIndexOf('[', end - 1);
                if (open < 0)
                {
                    break;
                }

                end = open;
            }

            int dot = end > 0 ? path.LastIndexOf('.', end - 1) : -1;
            return path[(dot + 1)..end];
        }

        private static string? Text(JsonElement element)
        {
            try
            {
                return element.GetString();
            }
            catch (InvalidOperationException)
            {
                // JSON parsing doesn't check a string's bytes; reading it does (invalid UTF-8, an escaped lone surrogate).
                return null;
            }
        }

        /// <summary>A value for the line: an object's or a list's kind, a name's text by its shape, any other value as written.</summary>
        private string Shown(JsonElement element, string path) => element.ValueKind switch
        {
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "a list",
            JsonValueKind.Undefined => "nothing",
            JsonValueKind.String when NameMembers.Contains(MemberOf(path)) => $"({_shape(Text(element))})",
            _ => element.GetRawText(),
        };

        private void Differ(string path, string first, string second)
        {
            Total++;
            if (_differences.Count < _max)
            {
                _differences.Add($"{(path.Length == 0 ? "(the file)" : Diagnostics.SafeText.Clean(path, 120))}: {Diagnostics.SafeText.Clean(first, 60)}, then {Diagnostics.SafeText.Clean(second, 60)}");
            }
        }
    }
}
