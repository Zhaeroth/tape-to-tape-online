using System;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// MelonLoader's launch logs in UTC, for a bug report: MelonLoader names each launch's log by the local time it started
/// (<c>MelonLoader\Logs\26-11-5_12-30-45.log</c>, <c>yy-M-d_H-m-s</c>) and stamps every line with the local time of day
/// (<c>[12:30:45.715]</c>), so a log as written tells the player's time zone. This converts each stamp to UTC with the
/// zone's own rules at that line's date: the day moves on when the time of day goes back by more than 12 hours (midnight);
/// in the hour a clock goes back (daylight saving time ending) a time is read with the offset that keeps UTC moving
/// forward; a time in the hour a clock skips is read with the offset before the gap. One instance per file, its lines in
/// order. Not thread-safe.
/// </summary>
public sealed class MelonLogTimes
{
    private const string NameFormat = "yy-M-d_H-m-s";
    private const int StampLength = 14; // "[HH:mm:ss.fff]"

    private readonly TimeZoneInfo _zone;
    private DateTime _day;
    private TimeSpan _lastTimeOfDay;
    private DateTime _lastUtc;

    /// <summary>A clock for a log whose launch started at <paramref name="localStart"/> in <paramref name="zone"/>.</summary>
    public MelonLogTimes(TimeZoneInfo zone, DateTime localStart)
    {
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        DateTime start = DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified);
        _day = start.Date;
        _lastTimeOfDay = start.TimeOfDay;
        _lastUtc = DateTime.MinValue;
        StartUtc = ToUtc(start);
    }

    /// <summary>When the launch started, in UTC.</summary>
    public DateTime StartUtc { get; }

    /// <summary>How many stamps were converted.</summary>
    public int Converted { get; private set; }

    /// <summary>
    /// The local start time in a MelonLoader log's file name (<c>26-11-5_12-30-45.log</c>), or false when the name isn't
    /// in that form.
    /// </summary>
    public static bool TryParseName(string? fileName, out DateTime localStart)
    {
        localStart = default;
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = fileName[..^4];
        return DateTime.TryParseExact(stem, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out localStart);
    }

    /// <summary>A MelonLoader log's name in a bug report: <c>MelonLoader-20261105T113045Z.log</c>.</summary>
    public static string UtcName(DateTime startUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"MelonLoader-{startUtc:yyyyMMdd'T'HHmmss'Z'}.log");

    /// <summary>
    /// <paramref name="line"/> with its leading <c>[HH:mm:ss.fff]</c> stamp in UTC; a line without one (a stack trace's,
    /// a blank) is returned as it is. Never throws.
    /// </summary>
    public string Convert(string line)
    {
        if (!TryReadStamp(line, out TimeSpan timeOfDay))
        {
            return line ?? string.Empty;
        }

        try
        {
            if (timeOfDay < _lastTimeOfDay - TimeSpan.FromHours(12))
            {
                _day = _day.AddDays(1); // past midnight
            }

            _lastTimeOfDay = timeOfDay;
            DateTime utc = ToUtc(_day + timeOfDay);
            Converted++;
            return string.Concat(string.Create(CultureInfo.InvariantCulture, $"[{utc:HH:mm:ss.fff}]"), line.AsSpan(StampLength));
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            // A date past what DateTime holds: the time can't be told, so it's left out rather than kept local.
            return string.Concat("[--:--:--.---]", line.AsSpan(StampLength));
        }
    }

    /// <summary>
    /// <paramref name="local"/> (a wall time in the zone) in UTC, never earlier than the last one converted where the
    /// zone allows two readings.
    /// </summary>
    public DateTime ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        DateTime utc;
        if (_zone.IsInvalidTime(local))
        {
            // The hour a clock skips: no such wall time was ever shown; read it with the offset before the gap (the
            // smaller one, since a clock skips forward).
            TimeSpan before = _zone.GetUtcOffset(local.AddHours(-3));
            TimeSpan after = _zone.GetUtcOffset(local.AddHours(3));
            utc = local - (before < after ? before : after);
        }
        else if (_zone.IsAmbiguousTime(local))
        {
            // The hour a clock goes back: the first reading is the larger offset's (earlier in UTC), the second the
            // smaller's. Take the earliest that isn't before the last line's time.
            TimeSpan[] offsets = _zone.GetAmbiguousTimeOffsets(local);
            DateTime last = DateTime.MinValue;
            DateTime best = DateTime.MaxValue;
            foreach (TimeSpan offset in offsets)
            {
                DateTime candidate = local - offset;
                last = candidate > last ? candidate : last;
                if (candidate >= _lastUtc && candidate < best)
                {
                    best = candidate;
                }
            }

            utc = best != DateTime.MaxValue ? best : last;
        }
        else
        {
            utc = local - _zone.GetUtcOffset(local);
        }

        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (utc > _lastUtc)
        {
            _lastUtc = utc;
        }

        return utc;
    }

    /// <summary>A line's leading <c>[HH:mm:ss.fff]</c>, as a time of day.</summary>
    private static bool TryReadStamp(string? line, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (line == null || line.Length < StampLength || line[0] != '[' || line[3] != ':' || line[6] != ':' || line[9] != '.' || line[13] != ']')
        {
            return false;
        }

        if (!TwoDigits(line, 1, out int hours) || !TwoDigits(line, 4, out int minutes) || !TwoDigits(line, 7, out int seconds) ||
            !Ascii.IsDigit(line[10]) || !Ascii.IsDigit(line[11]) || !Ascii.IsDigit(line[12]) || hours > 23 || minutes > 59 || seconds > 59)
        {
            return false;
        }

        int millis = ((line[10] - '0') * 100) + ((line[11] - '0') * 10) + (line[12] - '0');
        timeOfDay = new TimeSpan(0, hours, minutes, seconds, millis);
        return true;
    }

    private static bool TwoDigits(string text, int at, out int value)
    {
        value = 0;
        if (!Ascii.IsDigit(text[at]) || !Ascii.IsDigit(text[at + 1]))
        {
            return false;
        }

        value = ((text[at] - '0') * 10) + (text[at + 1] - '0');
        return true;
    }
}
