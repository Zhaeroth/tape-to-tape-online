using System;

namespace T2TOnline.Core.Campaign;

/// <summary>A change of the game's language the mod saw.</summary>
/// <param name="From">The language before ("English").</param>
/// <param name="To">The language now ("German").</param>
/// <param name="Number">Which change it is since the watch began (1, 2...).</param>
public sealed record LanguageChange(string From, string To, int Number);

/// <summary>
/// Sees when the game's language changes while a session is up: a friend's own
/// Settings menu changes it (its dropdown calls <c>SystemSettings.SetLanguage</c>, which loads the fonts and then sets
/// <c>LocalizationManager.CurrentLanguage</c>), and the mod's caches of words in that language are dropped and the change logged.
/// The language is read a couple of times a second (an interop call with a string), not each frame. The first read is the
/// language the watch starts with, not a change. Pure logic; the reader is the caller's.
/// </summary>
public sealed class LanguageWatch
{
    /// <summary>How often the language is read: twice a second.</summary>
    public const long DefaultIntervalMicros = 500_000;

    private readonly long _interval;
    private long _next;
    private string? _current;

    /// <summary>A watch that reads the language at most every <paramref name="intervalMicros"/>.</summary>
    public LanguageWatch(long intervalMicros = DefaultIntervalMicros)
    {
        _interval = Math.Max(1, intervalMicros);
    }

    /// <summary>The language the watch last saw, or null before its first read.</summary>
    public string? Current => _current;

    /// <summary>How many changes it has seen.</summary>
    public int Changes { get; private set; }

    /// <summary>How many times the reader has been asked.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// Reads the language when it is due (<paramref name="read"/> is called then and no other time) and returns the change if it
    /// isn't what the watch saw last. A read of nothing (the game's localization isn't up) changes nothing. A clock that went
    /// back reads at once.
    /// </summary>
    public LanguageChange? Poll(long nowMicros, Func<string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (nowMicros < _next && nowMicros >= _next - _interval)
        {
            return null;
        }

        _next = nowMicros + _interval;
        Reads++;
        string? language = read();
        if (string.IsNullOrEmpty(language))
        {
            return null;
        }

        if (_current == null)
        {
            _current = language;
            return null;
        }

        if (string.Equals(language, _current, StringComparison.Ordinal))
        {
            return null;
        }

        var change = new LanguageChange(_current, language, ++Changes);
        _current = language;
        return change;
    }

    /// <summary>Forgets the language and the count: the next read is the starting language again (a new session).</summary>
    public void Reset()
    {
        _current = null;
        _next = 0;
        Changes = 0;
        Reads = 0;
    }
}
