using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The host's progress status for one friend: whether it shares progress at all (its ShareProgress setting), the first
/// batch of the hosting session it keeps for this friend, and its newest batch. A friend learns from it that the host has
/// sharing off (else it would just never get batches), and that batches are missing that no later batch would show (the
/// last ones of a stretch, lost on the wire).
/// </summary>
/// <param name="Session">The host's hosting session.</param>
/// <param name="Sharing">The host shares progress (ShareProgress on).</param>
/// <param name="EntitledFrom">The first batch the host keeps for this friend (earlier ones aren't coming).</param>
/// <param name="Newest">The host's newest batch (0: none yet).</param>
public readonly record struct ProgressStatus(ulong Session, bool Sharing, uint EntitledFrom, uint Newest);

/// <summary>
/// Host to friend (channel 1, <c>MessageType.ProgressStatus</c>): a <see cref="ProgressStatus"/>, sent when a friend joins,
/// when the host's sharing setting changes, after it answers a resend request, and a while after its newest batch changed.
/// <code>u8 format (1) · u64 session · u8 flags (bit 0 sharing) · u32 entitled-from · u32 newest</code>
/// A bad message reads as false, never throws.
/// </summary>
public static class ProgressStatusMessage
{
    /// <summary>The layout's version (the first byte).</summary>
    public const byte Format = 1;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, ProgressStatus status)
    {
        System.ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Format);
        writer.WriteUInt64(status.Session);
        writer.WriteByte((byte)(status.Sharing ? 1 : 0));
        writer.WriteUInt32(status.EntitledFrom);
        writer.WriteUInt32(status.Newest);
    }

    /// <summary>Reads the payload. False for anything malformed or of another format. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ProgressStatus status)
    {
        status = default;
        byte format = reader.ReadByte();
        ulong session = reader.ReadUInt64();
        byte flags = reader.ReadByte();
        uint entitledFrom = reader.ReadUInt32();
        uint newest = reader.ReadUInt32();
        if (reader.IsOverrun || format != Format || flags > 1 || entitledFrom == 0)
        {
            return false;
        }

        status = new ProgressStatus(session, flags == 1, entitledFrom, newest);
        return true;
    }
}

/// <summary>A friend's request to have batches <paramref name="From"/> to <paramref name="To"/> of a hosting session sent again.</summary>
/// <param name="Session">The host's hosting session the batches are from.</param>
/// <param name="From">The first batch missing.</param>
/// <param name="To">The last (<see cref="ProgressResendMessage.ToNewest"/>: through the host's newest).</param>
public readonly record struct ProgressResend(ulong Session, uint From, uint To);

/// <summary>
/// Friend to host (channel 1, <c>MessageType.ProgressResend</c>): "send batches from-to of your hosting session again":
/// a friend that sees batches missing (a gap in the numbers, batches it refused while too many waited, the newest ones the
/// host's status names, or batches it gave up on after waiting) asks for them instead of waiting for a rejoin. The host sends
/// what it still keeps, at most once every few seconds per friend; the friend's ledger still keeps anything from applying twice.
/// <code>u8 format (1) · u64 session · u32 from · u32 to</code>
/// A bad message reads as false, never throws.
/// </summary>
public static class ProgressResendMessage
{
    /// <summary>The layout's version (the first byte).</summary>
    public const byte Format = 1;

    /// <summary>A <see cref="ProgressResend.To"/> that means "through the host's newest batch".</summary>
    public const uint ToNewest = uint.MaxValue;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, ProgressResend request)
    {
        System.ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(Format);
        writer.WriteUInt64(request.Session);
        writer.WriteUInt32(request.From);
        writer.WriteUInt32(request.To);
    }

    /// <summary>Reads the payload. False for anything malformed, of another format, or an empty range. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out ProgressResend request)
    {
        request = default;
        byte format = reader.ReadByte();
        ulong session = reader.ReadUInt64();
        uint from = reader.ReadUInt32();
        uint to = reader.ReadUInt32();
        if (reader.IsOverrun || format != Format || from == 0 || to < from)
        {
            return false;
        }

        request = new ProgressResend(session, from, to);
        return true;
    }
}
