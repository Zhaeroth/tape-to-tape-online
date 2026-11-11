using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>
/// Host to friend (<see cref="MessageType.Rumble"/>, realtime channel): the host's game vibrated a friend's virtual
/// controller, so that friend's own pad vibrates with the game's own nudge of that type, shown when render time reaches it.
/// Two bytes: the Rewired player the game vibrated (a friend's slot, as the welcome gave it) and the game's
/// <c>ControllerVibration.NudgeType</c>. Anything outside those ranges is refused, never thrown on.
/// </summary>
/// <remarks>
/// Realtime, not the reliable events channel: every stream message is shown when render time reaches its stamp, so both are
/// in time on a healthy link; under loss a reliable rumble would wait behind the resent packet and buzz after the hit it
/// belongs to (and with the others in a burst), while an unreliable one is simply lost. A buzz after the moment is worse
/// than none.
/// </remarks>
/// <param name="RewiredPlayer">The Rewired player the host's game vibrated (0 to <see cref="MaxRewiredPlayer"/>).</param>
/// <param name="Nudge">The game's nudge type (BigHand 0, NormalHand 1, TinyHand 2, BigTrigger 3, NormalTrigger 4,
/// TinyTrigger 5).</param>
public readonly record struct RumbleMessage(byte RewiredPlayer, byte Nudge)
{
    /// <summary>The highest Rewired player id the game has (Player0 to Player10).</summary>
    public const byte MaxRewiredPlayer = 10;

    /// <summary>The highest nudge type the game has (TinyTrigger).</summary>
    public const byte MaxNudge = 5;

    /// <summary>The payload's size in bytes.</summary>
    public const int PayloadBytes = 2;

    /// <summary>True when both values are in range (what <see cref="TryRead"/> accepts and <see cref="Write"/> writes).</summary>
    public bool IsValid => RewiredPlayer <= MaxRewiredPlayer && Nudge <= MaxNudge;

    /// <summary>Writes the payload (after the header). Out-of-range values throw: the host never sends what a friend refuses.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!IsValid)
        {
            throw new InvalidOperationException($"Rewired player {RewiredPlayer} or nudge {Nudge} is out of range.");
        }

        writer.WriteByte(RewiredPlayer);
        writer.WriteByte(Nudge);
    }

    /// <summary>Reads the payload. False for truncated data or a value out of range.</summary>
    public static bool TryRead(ref BitReader reader, out RumbleMessage message)
    {
        message = new RumbleMessage(reader.ReadByte(), reader.ReadByte());
        return !reader.IsOverrun && message.IsValid;
    }
}
