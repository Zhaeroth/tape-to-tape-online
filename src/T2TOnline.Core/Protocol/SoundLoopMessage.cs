using System;
using System.Globalization;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>
/// The game's looping gameplay sounds a friend's game doesn't start itself (their owners are gameplay code client mode
/// holds), by the SoundManager event each one plays. The first five are the game's own <c>MagnetType</c> values, the
/// argument of <c>SoundManager.StartMagnetSound</c>. Values are part of the protocol.
/// </summary>
public enum LoopKind : byte
{
    /// <summary><c>magnetActivationEvent</c>: a puck attractor of type Default (the Lightning Rod talent).</summary>
    Magnet = 0,

    /// <summary><c>gravityWellEvent</c>: the gravity well.</summary>
    GravityWell = 1,

    /// <summary><c>magnetAbilityEvent</c>: the Magneto ability's magnet.</summary>
    Magneto = 2,

    /// <summary><c>magnetReboundEvent</c>: the Attract Rebound talent's puck attractor.</summary>
    MagnetRebound = 3,

    /// <summary><c>magnetSafeBounceEvent</c>: the Magnet Interception talent's puck attractor.</summary>
    MagnetSafeBounce = 4,

    /// <summary><c>retroRocketEvent</c>: the retro rocket status effect.</summary>
    RetroRocket = 5,

    /// <summary><c>explodingPuckEvent</c>: the exploding puck ability.</summary>
    ExplodingPuck = 6,

    /// <summary><c>fusrodahChargingEvent</c>: the fus-ro-dah ability's charge.</summary>
    FusRoDahCharge = 7,
}

/// <summary>What a <see cref="SoundLoopMessage"/> does to its loop. Values are part of the protocol.</summary>
public enum SoundLoopOp : byte
{
    /// <summary>The loop plays (again, every few seconds while it does, so a friend who wasn't there yet picks it up).</summary>
    Start = 0,

    /// <summary>The loop moved.</summary>
    Move = 1,

    /// <summary>The loop stopped.</summary>
    Stop = 2,
}

/// <summary>Names of <see cref="LoopKind"/>, for the log.</summary>
public static class LoopKinds
{
    /// <summary>How many kinds there are (values 0 to Count - 1).</summary>
    public const int Count = 8;

    /// <summary>The kind's name as the log says it ("magnet rebound").</summary>
    public static string Name(LoopKind kind) => kind switch
    {
        LoopKind.Magnet => "magnet",
        LoopKind.GravityWell => "gravity well",
        LoopKind.Magneto => "magneto",
        LoopKind.MagnetRebound => "magnet rebound",
        LoopKind.MagnetSafeBounce => "magnet safe bounce",
        LoopKind.RetroRocket => "retro rocket",
        LoopKind.ExplodingPuck => "exploding puck",
        LoopKind.FusRoDahCharge => "fus-ro-dah charge",
        _ => $"kind {(byte)kind}",
    };
}

/// <summary>
/// Host to friend (<see cref="MessageType.SoundLoop"/>): one of the host's looping gameplay sounds started (or still
/// plays), moved or stopped. Starts and stops go on the reliable events channel (a lost stop would leave a loop playing);
/// moves on the realtime channel (a lost one is superseded by the next, and never holds up anything). Layout: the loop's
/// handle (a varint, never 0), the op, then for a start the kind, and for a start or a move the position (three floats,
/// finite, within <see cref="MaxCoordinate"/>). Anything else is refused, never thrown on.
/// </summary>
/// <param name="Handle">The host's id for this loop in this match, never 0.</param>
/// <param name="Op">What happened.</param>
/// <param name="Kind">Which sound (a start only).</param>
/// <param name="X">Position X (a start or a move).</param>
/// <param name="Y">Position Y.</param>
/// <param name="Z">Position Z.</param>
public readonly record struct SoundLoopMessage(uint Handle, SoundLoopOp Op, LoopKind Kind, float X, float Y, float Z)
{
    /// <summary>The largest coordinate taken (a rink is about 60 m by 26 m; anything far past it is broken).</summary>
    public const float MaxCoordinate = 10_000f;

    /// <summary>The longest payload: a start with a 5-byte handle.</summary>
    public const int MaxPayloadBytes = 5 + 1 + 1 + 12;

    /// <summary>A start (or a start again) of <paramref name="kind"/> at a position.</summary>
    public static SoundLoopMessage Start(uint handle, LoopKind kind, float x, float y, float z) => new(handle, SoundLoopOp.Start, kind, x, y, z);

    /// <summary>A move to a position.</summary>
    public static SoundLoopMessage Move(uint handle, float x, float y, float z) => new(handle, SoundLoopOp.Move, default, x, y, z);

    /// <summary>A stop.</summary>
    public static SoundLoopMessage Stop(uint handle) => new(handle, SoundLoopOp.Stop, default, 0, 0, 0);

    /// <summary>True when every value is one <see cref="TryRead"/> takes (what <see cref="Write"/> writes).</summary>
    public bool IsValid =>
        Handle != 0 && Op <= SoundLoopOp.Stop && (Op != SoundLoopOp.Start || (byte)Kind < LoopKinds.Count) &&
        (Op == SoundLoopOp.Stop || (Coordinate(X) && Coordinate(Y) && Coordinate(Z)));

    /// <summary>True for a coordinate a loop can be at: finite and within <see cref="MaxCoordinate"/>.</summary>
    public static bool Coordinate(float value) => float.IsFinite(value) && Math.Abs(value) <= MaxCoordinate;

    /// <summary>Writes the payload (after the header). Values a friend would refuse throw: the host never sends them.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!IsValid)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Sound loop {Handle} {Op} of kind {(byte)Kind} at ({X}, {Y}, {Z}) is out of range."));
        }

        writer.WriteVarUInt(Handle);
        writer.WriteByte((byte)Op);
        if (Op == SoundLoopOp.Start)
        {
            writer.WriteByte((byte)Kind);
        }

        if (Op != SoundLoopOp.Stop)
        {
            writer.WriteSingle(X);
            writer.WriteSingle(Y);
            writer.WriteSingle(Z);
        }
    }

    /// <summary>Reads the payload. False for truncated data or any value out of range.</summary>
    public static bool TryRead(ref BitReader reader, out SoundLoopMessage message)
    {
        message = default;
        ulong handle = reader.ReadVarUInt();
        byte op = reader.ReadByte();
        if (reader.IsOverrun || handle is 0 or > uint.MaxValue || op > (byte)SoundLoopOp.Stop)
        {
            return false;
        }

        byte kind = op == (byte)SoundLoopOp.Start ? reader.ReadByte() : (byte)0;
        float x = 0, y = 0, z = 0;
        if (op != (byte)SoundLoopOp.Stop)
        {
            x = reader.ReadSingle();
            y = reader.ReadSingle();
            z = reader.ReadSingle();
        }

        message = new SoundLoopMessage((uint)handle, (SoundLoopOp)op, (LoopKind)kind, x, y, z);
        return !reader.IsOverrun && message.IsValid;
    }
}
