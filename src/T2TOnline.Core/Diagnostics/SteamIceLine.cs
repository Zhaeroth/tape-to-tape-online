using System;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// A developer's proof of relay only per connection (D133): Steam's own debug line that says how one P2P connection uses
/// ICE, read from Steam's networking debug output. Steam prints one of two lines as a connection starts its P2P
/// rendezvous, each after a description of the connection (<c>[#n P2P steamid:...]</c>, which names the peer):
/// <list type="bullet">
/// <item><c>ICE failed 3998 ICE not enabled by local user options</c>: the connection's own <c>P2P_Transport_ICE_Enable</c>
/// is 0 (or the player's Steam setting is off), so it shares no address: relay only.</item>
/// <item><c>P2P_Transport_ICE_Enable=0x..., AllowedCandidateTypes=0x...</c>: ICE started, with the candidate types it may
/// share (none: no ICE after all).</item>
/// </list>
/// Only those two shapes are read, and the line is rebuilt from the numbers parsed and the one known reason: nothing else of
/// Steam's text (the description, an address, a name) can reach the log.
/// </summary>
public static class SteamIceLine
{
    /// <summary>The reason Steam gives when the connection's ICE setting is off.</summary>
    public const string NotEnabledReason = "ICE not enabled by local user options";

    /// <summary>The longest debug line read; Steam's are far shorter.</summary>
    public const int MaxLength = 512;

    private const string EnableMark = "P2P_Transport_ICE_Enable=0x";
    private const string AllowedMark = ", AllowedCandidateTypes=0x";
    private const string FailedMark = "ICE failed ";

    /// <summary>
    /// The reading in <paramref name="message"/> (one line of Steam's debug output), or null when it isn't one of the two
    /// lines. Never throws.
    /// </summary>
    public static SteamIceReading? Read(string? message)
    {
        if (string.IsNullOrEmpty(message) || message.Length > MaxLength)
        {
            return null;
        }

        // The text after the connection's description ("[...] ", and whatever Steam may put before it, such as a time): from
        // the mark to the line's end, which must be the whole of the rest.
        ReadOnlySpan<char> line = message.AsSpan().TrimEnd();
        int enableAt = line.IndexOf(EnableMark, StringComparison.Ordinal);
        if (enableAt >= 0)
        {
            ReadOnlySpan<char> values = line[(enableAt + EnableMark.Length)..];
            int comma = values.IndexOf(AllowedMark, StringComparison.Ordinal);
            if (comma < 0 ||
                !TryHex(values[..comma], out uint enable) ||
                !TryHex(values[(comma + AllowedMark.Length)..], out uint allowed))
            {
                return null;
            }

            return new SteamIceReading(
                string.Create(CultureInfo.InvariantCulture, $"P2P_Transport_ICE_Enable=0x{enable:x}, AllowedCandidateTypes=0x{allowed:x}"),
                allowed != 0);
        }

        int failedAt = line.IndexOf(FailedMark, StringComparison.Ordinal);
        if (failedAt >= 0)
        {
            ReadOnlySpan<char> after = line[(failedAt + FailedMark.Length)..];
            int space = after.IndexOf(' ');
            if (space < 1 || space > 11 ||
                !int.TryParse(after[..space], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code) ||
                !after[(space + 1)..].SequenceEqual(NotEnabledReason))
            {
                return null; // another failure: ICE had started, and its own line came first
            }

            return new SteamIceReading(string.Create(CultureInfo.InvariantCulture, $"ICE failed {code} {NotEnabledReason}"), false);
        }

        return null;
    }

    /// <summary>
    /// The log line for <paramref name="reading"/>, with <paramref name="relayOnly"/> the setting in effect; <paramref name="warn"/>
    /// is true when the connection uses ICE although relay only is set (Steam didn't keep it for this connection).
    /// </summary>
    public static string LogLine(SteamIceReading reading, bool relayOnly, out bool warn)
    {
        ArgumentNullException.ThrowIfNull(reading);
        warn = relayOnly && reading.IceOn;
        string meaning = !reading.IceOn
            ? relayOnly ? "no ICE on it, so it is relay only, as set" : "no ICE on it, although direct connections are allowed"
            : relayOnly ? "ICE is on for it although relay only is set: Steam didn't keep the setting for this connection"
            : "ICE is on for it (direct connections allowed, as set)";
        return $"ICE: Steam's own line for this connection: \"{reading.Text}\": {meaning}.";
    }

    private static bool TryHex(ReadOnlySpan<char> text, out uint value)
    {
        value = 0;
        return text.Length is > 0 and <= 8 && uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>What one of Steam's ICE lines says (<see cref="SteamIceLine.Read"/>).</summary>
/// <param name="Text">The line as rebuilt for the log: numbers and fixed words only.</param>
/// <param name="IceOn">The connection may share ICE candidates (addresses) with its peer.</param>
public sealed record SteamIceReading(string Text, bool IceOn);
