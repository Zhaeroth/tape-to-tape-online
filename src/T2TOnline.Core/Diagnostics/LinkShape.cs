using System;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What a log may say about a Steam link's route and this PC's ping location instead of the data centres in them (D127): a
/// relay's code, or the list of measured relays with their pings, places a player within a region. The route is
/// <c>relayed</c> or <c>direct</c> (with <c>two relays</c> when each side's nearest relay differs), the ping location a
/// count and the nearest ping.
/// </summary>
public static class LinkShape
{
    /// <summary>The longest ping location read (Steam's own limit is lower: <c>k_cchMaxSteamNetworkingPingLocationString</c>).</summary>
    public const int MaxLocationLength = 1024;

    /// <summary>
    /// A link's route for the log: <c>no route yet</c> until it's <paramref name="connected"/>, then <c>direct</c>, or
    /// <c>relayed</c> (<c>relayed, two relays</c> when the remote side's relay, <paramref name="remotePop"/>, is known and isn't
    /// the one in use, <paramref name="relayPop"/>).
    /// </summary>
    public static string Route(bool connected, bool relayed, uint relayPop, uint remotePop) =>
        !connected ? "no route yet"
        : !relayed ? "direct"
        : remotePop != 0 && relayPop != 0 && remotePop != relayPop ? "relayed, two relays"
        : "relayed";

    /// <summary>
    /// This PC's ping location for the log, without the data centres in it: <c>ready (12 relays measured, nearest 16 ms,
    /// age 1 s)</c>. Steam writes it as <c>code=ping+variance[/ping+variance],...</c>; <c>unreadable</c> when nothing in it
    /// parses. Never throws.
    /// </summary>
    public static string PingLocation(string? location, float ageSeconds)
    {
        if (string.IsNullOrEmpty(location))
        {
            return "unknown";
        }

        ReadOnlySpan<char> text = location.AsSpan(0, Math.Min(location.Length, MaxLocationLength));
        if (location.Length > MaxLocationLength && text.LastIndexOf(',') is var cut and >= 0)
        {
            text = text[..cut]; // a cut entry isn't one
        }
        int relays = 0;
        int nearest = int.MaxValue;
        while (text.Length > 0)
        {
            int comma = text.IndexOf(',');
            ReadOnlySpan<char> entry = comma < 0 ? text : text[..comma];
            text = comma < 0 ? ReadOnlySpan<char>.Empty : text[(comma + 1)..];
            int equals = entry.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            int best = BestPing(entry[(equals + 1)..]);
            if (best < 0)
            {
                continue;
            }

            relays++;
            nearest = Math.Min(nearest, best);
        }

        if (relays == 0)
        {
            return "unreadable";
        }

        string age = float.IsFinite(ageSeconds) && ageSeconds >= 0
            ? string.Create(CultureInfo.InvariantCulture, $", age {ageSeconds:0} s")
            : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"ready ({relays} relay{(relays == 1 ? string.Empty : "s")} measured, nearest {nearest} ms{age})");
    }

    /// <summary>The lowest ping in one entry's value (<c>47+4/39+1</c>: each part's number before its <c>+</c>), or -1.</summary>
    private static int BestPing(ReadOnlySpan<char> value)
    {
        int best = -1;
        while (value.Length > 0)
        {
            int slash = value.IndexOf('/');
            ReadOnlySpan<char> part = slash < 0 ? value : value[..slash];
            value = slash < 0 ? ReadOnlySpan<char>.Empty : value[(slash + 1)..];
            int plus = part.IndexOf('+');
            ReadOnlySpan<char> digits = plus < 0 ? part : part[..plus];
            if (digits.Length is > 0 and <= 6 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int ms))
            {
                best = best < 0 ? ms : Math.Min(best, ms);
            }
        }

        return best;
    }
}
