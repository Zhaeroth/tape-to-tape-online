using System;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Steam;
using T2TOnline.Steam.Native;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// Session messages over <c>ISteamNetworkingMessages</c>. The realtime channel is unreliable and never queued (a newer
/// packet repeats what an old one carried), session control goes reliable without Nagle, bulk goes reliable.
/// </summary>
/// <remarks>
/// Every send goes through the session's <see cref="SteamSessionWatch"/>: a Steam session that broke is noted before it's
/// restarted, since reliable messages queued on it may be lost. The session glue then has the friend say hello again, and
/// the host resends everything a rejoining friend gets.
/// <para>
/// For the one-PC test, both ends of a session run in this process with the same Steam ID, and Steam delivers messages
/// sent to yourself. The host listens on the normal channels; the friend in the same process listens on the same
/// channels shifted by <see cref="LoopbackChannelOffset"/>, and the host's transport shifts what it sends to itself.
/// The protocol never sees the shift.
/// </para>
/// </remarks>
internal sealed class SteamSessionTransport : ISessionTransport
{
    /// <summary>How far the one-PC test's friend channels are moved up.</summary>
    public const int LoopbackChannelOffset = 16;

    private readonly ulong _self;
    private readonly SteamSessionWatch _watch;

    /// <summary>Creates a transport for this PC's Steam account.</summary>
    /// <param name="self">Our Steam ID.</param>
    /// <param name="shiftMessagesToSelf">True for the host's transport: messages to ourselves go to the one-PC test friend's channels.</param>
    /// <param name="watch">Sends go through it, so a broken Steam session is noticed before it's restarted.</param>
    public SteamSessionTransport(ulong self, bool shiftMessagesToSelf, SteamSessionWatch watch)
    {
        _self = self;
        ShiftsMessagesToSelf = shiftMessagesToSelf;
        _watch = watch ?? throw new ArgumentNullException(nameof(watch));
    }

    /// <summary>True when messages to ourselves go to the shifted channels.</summary>
    public bool ShiftsMessagesToSelf { get; }

    /// <summary>While true, nothing is sent (a simulated network drop): every send is refused.</summary>
    public bool Cut { get; set; }

    /// <summary>The Steam channel a session channel uses to reach whoever listens with <paramref name="shifted"/>.</summary>
    public static int SteamChannel(Channel channel, bool shifted) => (int)channel + (shifted ? LoopbackChannelOffset : 0);

    /// <summary>Steam's send flags for a session channel (a broken session is restarted by the watch, after it's noted).</summary>
    public static SteamSendType Flags(Channel channel) => channel switch
    {
        Channel.Realtime => SteamSendType.UnreliableNoDelay,
        Channel.Events => SteamSendType.ReliableNoNagle,
        _ => SteamSendType.Reliable,
    };

    /// <inheritdoc />
    public bool Send(ulong peer, Channel channel, ReadOnlySpan<byte> message)
    {
        if (Cut)
        {
            return false;
        }

        int steamChannel = SteamChannel(channel, ShiftsMessagesToSelf && peer == _self);
        return _watch.Send(peer, steamChannel, message, Flags(channel)) == SteamResult.OK;
    }
}
