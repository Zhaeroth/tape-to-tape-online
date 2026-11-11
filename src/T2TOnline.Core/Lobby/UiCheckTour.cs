using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Lobby;

/// <summary>One key a tour takes in <c>run.txt</c> (<c>seed=3</c>, <c>record=on</c>): its name and the values it allows.</summary>
public sealed record UiCheckKey
{
    private UiCheckKey(string name, string what, IReadOnlyList<string>? choices, int min, int max)
    {
        Name = name;
        What = what;
        Choices = choices;
        Min = min;
        Max = max;
    }

    /// <summary>The key's name: lower-case letters, digits and '_', at most <see cref="UiCheckRequest.MaxKeyLength"/>.</summary>
    public string Name { get; }

    /// <summary>What it does, for the summary.</summary>
    public string What { get; }

    /// <summary>The values it allows, or null for a number between <see cref="Min"/> and <see cref="Max"/>.</summary>
    public IReadOnlyList<string>? Choices { get; }

    /// <summary>A number key's smallest value.</summary>
    public int Min { get; }

    /// <summary>A number key's largest value.</summary>
    public int Max { get; }

    /// <summary>A key taking one of <paramref name="choices"/>.</summary>
    public static UiCheckKey Choice(string name, string what, params string[] choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Length == 0)
        {
            throw new ArgumentException("At least one choice.", nameof(choices));
        }

        return new UiCheckKey(name, what, choices, 0, 0);
    }

    /// <summary>A key taking a whole number from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static UiCheckKey Number(string name, string what, int min, int max)
    {
        if (min < 0 || max < min)
        {
            throw new ArgumentOutOfRangeException(nameof(max), "A range of non-negative numbers.");
        }

        return new UiCheckKey(name, what, null, min, max);
    }

    /// <summary>True when <paramref name="value"/> is one this key allows.</summary>
    public bool Allows(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Choices != null)
        {
            foreach (string choice in Choices)
            {
                if (string.Equals(choice, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= Min && number <= Max;
    }

    /// <summary>What it allows, in words ("on or off", "0 to 999").</summary>
    public string Allowed => Choices != null
        ? string.Join(" or ", Choices)
        : string.Create(CultureInfo.InvariantCulture, $"{Min} to {Max}");
}

/// <summary>A check tour as <c>run.txt</c> names it: its steps, how long it may take, and the keys it takes.</summary>
/// <param name="Name">Its name, the first words of <c>run.txt</c> (lower-case words, single spaces).</param>
/// <param name="What">What it checks, for the summary.</param>
/// <param name="Steps">Its steps, in order (<see cref="UiCheckPlan.Problem"/> says what's wrong with them).</param>
/// <param name="Limit">How long it may run before it stops, restores everything and ends.</param>
/// <param name="Keys">The keys it takes after its name; null for none.</param>
/// <param name="KeysCheck">
/// What's wrong with a request's keys taken together (each one already allowed), or null when they can run together; null for
/// a tour whose keys always can. A sentence it returns refuses the request as an undeclared key does.
/// </param>
public sealed record UiCheckTour(string Name, string What, IReadOnlyList<UiCheckStep> Steps, TimeSpan Limit, IReadOnlyList<UiCheckKey>? Keys = null,
    Func<IReadOnlyList<KeyValuePair<string, string>>, string?>? KeysCheck = null)
{
    /// <summary>The keys it takes (empty for none).</summary>
    public IReadOnlyList<UiCheckKey> TakesKeys => Keys ?? Array.Empty<UiCheckKey>();

    /// <summary>The key named <paramref name="name"/>, or null when it takes none by that name.</summary>
    public UiCheckKey? Key(string name)
    {
        foreach (UiCheckKey key in TakesKeys)
        {
            if (string.Equals(key.Name, name, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }
}

/// <summary>
/// What the first line of <c>run.txt</c> asks for: a tour by name, then its arguments as ordered <c>key=value</c> pairs
/// (<c>campaign full seed=3</c>). Only the tours and keys the plan declares are taken: anything else is refused, with a
/// sentence saying why, and nothing runs. A blank line (or Ctrl+F12, which has none) asks for <c>all</c>.
/// </summary>
public sealed class UiCheckRequest
{
    /// <summary>The longest first line taken.</summary>
    public const int MaxLineLength = 160;

    /// <summary>The most arguments taken.</summary>
    public const int MaxArguments = 8;

    /// <summary>The longest key.</summary>
    public const int MaxKeyLength = 16;

    /// <summary>The longest value.</summary>
    public const int MaxValueLength = 32;

    /// <summary>The tour asked for when the line names none.</summary>
    public const string DefaultTour = "all";

    private static readonly char[] Spaces = { ' ', '\t' };

    private UiCheckRequest(UiCheckTour tour, IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        Tour = tour;
        Arguments = arguments;
    }

    /// <summary>The tour asked for.</summary>
    public UiCheckTour Tour { get; }

    /// <summary>Its arguments, in the order the line gives them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Arguments { get; }

    /// <summary>The value given for <paramref name="key"/>, or null when the line gives none.</summary>
    public string? Value(string key)
    {
        foreach (KeyValuePair<string, string> argument in Arguments)
        {
            if (string.Equals(argument.Key, key, StringComparison.Ordinal))
            {
                return argument.Value;
            }
        }

        return null;
    }

    /// <summary>The arguments as the line gave them ("seed=3 record=on"), or "none".</summary>
    public string ArgumentsText()
    {
        if (Arguments.Count == 0)
        {
            return "none";
        }

        var text = new StringBuilder();
        foreach (KeyValuePair<string, string> argument in Arguments)
        {
            text.Append(text.Length == 0 ? string.Empty : " ").Append(argument.Key).Append('=').Append(argument.Value);
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads <paramref name="line"/> against <paramref name="tours"/>. False, with <paramref name="problem"/> saying why
    /// in one sentence, when the tour or any argument isn't one the plan declares. Never throws on what the line holds.
    /// </summary>
    public static bool TryParse(string? line, IReadOnlyList<UiCheckTour> tours, out UiCheckRequest? request, out string problem)
    {
        ArgumentNullException.ThrowIfNull(tours);
        request = null;
        problem = string.Empty;
        string text = line?.Trim() ?? string.Empty;
        if (text.Length > MaxLineLength)
        {
            problem = $"the line is over {MaxLineLength} characters";
            return false;
        }

        string[] words = text.Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
        int at = 0;
        var name = new StringBuilder();
        while (at < words.Length && words[at].IndexOf('=', StringComparison.Ordinal) < 0)
        {
            name.Append(name.Length == 0 ? string.Empty : " ").Append(words[at]);
            at++;
        }

        string asked = name.Length == 0 ? DefaultTour : name.ToString();
        UiCheckTour? tour = Find(tours, asked);
        if (tour == null)
        {
            problem = $"no tour is called \"{Shown(asked)}\" (the tours: {Names(tours)})";
            return false;
        }

        var arguments = new List<KeyValuePair<string, string>>();
        for (; at < words.Length; at++)
        {
            string word = words[at];
            int equals = word.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                problem = $"\"{Shown(word)}\" comes after the arguments began; each argument is key=value";
                return false;
            }

            if (arguments.Count == MaxArguments)
            {
                problem = $"more than {MaxArguments} arguments";
                return false;
            }

            string key = word[..equals];
            string value = word[(equals + 1)..];
            if (!IsToken(key, MaxKeyLength, allowUpper: false) || !IsToken(value, MaxValueLength, allowUpper: true))
            {
                problem = $"\"{Shown(word)}\" isn't a key=value of letters, digits, '_', '.' or '-' (a key up to {MaxKeyLength}, a value up to {MaxValueLength})";
                return false;
            }

            UiCheckKey? declared = tour.Key(key);
            if (declared == null)
            {
                problem = tour.TakesKeys.Count == 0
                    ? $"the tour \"{tour.Name}\" takes no arguments ({key})"
                    : $"the tour \"{tour.Name}\" takes no key \"{key}\" (it takes {KeyNames(tour)})";
                return false;
            }

            if (arguments.Exists(a => a.Key == key))
            {
                problem = $"the key \"{key}\" is given twice";
                return false;
            }

            if (!declared.Allows(value))
            {
                problem = $"\"{key}\" takes {declared.Allowed}, not \"{value}\"";
                return false;
            }

            arguments.Add(new KeyValuePair<string, string>(key, value));
        }

        // Keys each allowed may still not go together (a tour's own rule: the campaign tours' kbhost and late, say).
        if (tour.KeysCheck?.Invoke(arguments) is { } together)
        {
            problem = together;
            return false;
        }

        request = new UiCheckRequest(tour, arguments);
        return true;
    }

    /// <summary>The tour named <paramref name="name"/> (any case, any run of spaces), or null.</summary>
    public static UiCheckTour? Find(IReadOnlyList<UiCheckTour> tours, string? name)
    {
        ArgumentNullException.ThrowIfNull(tours);
        string wanted = string.Join(' ', (name ?? string.Empty).Split(Spaces, StringSplitOptions.RemoveEmptyEntries));
        foreach (UiCheckTour tour in tours)
        {
            if (string.Equals(tour.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return tour;
            }
        }

        return null;
    }

    private static bool IsToken(string text, int maxLength, bool allowUpper)
    {
        if (text.Length == 0 || text.Length > maxLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' || (allowUpper && (c is (>= 'A' and <= 'Z') or '.' or '-'));
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What the line said, for a refusal: at most 24 characters, anything but plain letters, digits, spaces and "=_.-" as '?'.</summary>
    private static string Shown(string text)
    {
        var shown = new StringBuilder(Math.Min(text.Length, 24));
        foreach (char c in text)
        {
            if (shown.Length == 24)
            {
                shown.Append("...");
                break;
            }

            shown.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '=' or '_' or '.' or '-' ? c : '?');
        }

        return shown.ToString();
    }

    private static string Names(IReadOnlyList<UiCheckTour> tours)
    {
        var names = new List<string>(tours.Count);
        foreach (UiCheckTour tour in tours)
        {
            names.Add(tour.Name);
        }

        return string.Join(", ", names);
    }

    private static string KeyNames(UiCheckTour tour)
    {
        var names = new List<string>(tour.TakesKeys.Count);
        foreach (UiCheckKey key in tour.TakesKeys)
        {
            names.Add(key.Name);
        }

        return string.Join(", ", names);
    }
}
