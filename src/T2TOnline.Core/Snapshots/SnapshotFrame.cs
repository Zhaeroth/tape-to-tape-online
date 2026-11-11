using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Snapshots;

/// <summary>
/// One snapshot of the host's match, as the friend reconstructs it: every replay object's recorder bytes by network id,
/// plus what the recorders don't carry and the picture needs (time scales, camera pose).
/// </summary>
public sealed class SnapshotFrame
{
    /// <summary>Most objects one snapshot may hold.</summary>
    public const int MaxObjects = 1024;

    /// <summary>Longest single object state, in bytes (the game's recorder buffers are 512 bytes).</summary>
    public const int MaxStateBytes = 4096;

    /// <summary>Most per-group time scales one snapshot may carry.</summary>
    public const int MaxGroupScales = 32;

    /// <summary>An empty snapshot.</summary>
    public SnapshotFrame() => Objects = new SortedDictionary<ushort, byte[]>();

    /// <summary>A snapshot starting with <paramref name="baseFrame"/>'s objects (the state arrays shared), for decoding a delta.</summary>
    internal SnapshotFrame(SnapshotFrame baseFrame) => Objects = new SortedDictionary<ushort, byte[]>(baseFrame.Objects);

    /// <summary>The host's counter, one per snapshot.</summary>
    public uint Sequence { get; set; }

    /// <summary>The host's clock when the snapshot was taken, in microseconds.</summary>
    public long HostTimeMicros { get; set; }

    /// <summary>The game's global time scale.</summary>
    public float TimeScale { get; set; } = 1f;

    /// <summary>
    /// Per-update-group time scales that differ from 1 (group id, scale). Part of the snapshot format, but nothing fills it
    /// yet: the host records none, so every group stays at 1 on a friend's PC.
    /// </summary>
    public List<(byte Group, float Scale)> GroupScales { get; } = new();

    /// <summary>The rendered camera's pose, if known.</summary>
    public CameraPose? Camera { get; set; }

    /// <summary>Object states by network id. Values are owned by the frame: don't change them after adding.</summary>
    public SortedDictionary<ushort, byte[]> Objects { get; }
}

/// <summary>A camera's pose: world position, rotation, and lens.</summary>
/// <param name="X">Position X.</param>
/// <param name="Y">Position Y.</param>
/// <param name="Z">Position Z.</param>
/// <param name="QX">Rotation quaternion X.</param>
/// <param name="QY">Rotation quaternion Y.</param>
/// <param name="QZ">Rotation quaternion Z.</param>
/// <param name="QW">Rotation quaternion W.</param>
/// <param name="Lens">Vertical field of view in degrees, or the orthographic size when <paramref name="Orthographic"/>.</param>
/// <param name="Orthographic">True for an orthographic camera.</param>
public readonly record struct CameraPose(float X, float Y, float Z, float QX, float QY, float QZ, float QW, float Lens, bool Orthographic)
{
    /// <summary>A position jump per 1/60 s beyond which <see cref="IsCut"/> calls it a cut.</summary>
    public const float CutMetres = 6f;

    /// <summary>A turn beyond which <see cref="IsCut"/> calls it a cut.</summary>
    public const float CutDegrees = 45f;

    private static readonly float CutCosineHalfAngle = MathF.Cos(CutDegrees * MathF.PI / 360f);

    /// <summary>Blends two poses: positions and lens linearly, rotations by normalized lerp along the shorter arc.</summary>
    public static CameraPose Lerp(in CameraPose a, in CameraPose b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float dot = (a.QX * b.QX) + (a.QY * b.QY) + (a.QZ * b.QZ) + (a.QW * b.QW);
        float sign = dot < 0 ? -1f : 1f;
        float qx = a.QX + (((sign * b.QX) - a.QX) * t);
        float qy = a.QY + (((sign * b.QY) - a.QY) * t);
        float qz = a.QZ + (((sign * b.QZ) - a.QZ) * t);
        float qw = a.QW + (((sign * b.QW) - a.QW) * t);
        float length = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
        if (length > 1e-6f)
        {
            qx /= length;
            qy /= length;
            qz /= length;
            qw /= length;
        }
        else
        {
            (qx, qy, qz, qw) = (b.QX, b.QY, b.QZ, b.QW);
        }

        return new CameraPose(
            a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t),
            qx, qy, qz, qw,
            a.Lens + ((b.Lens - a.Lens) * t),
            t < 0.5f ? a.Orthographic : b.Orthographic);
    }

    /// <summary>
    /// True when <paramref name="b"/>, <paramref name="intervalMicros"/> after <paramref name="a"/>, is another shot rather
    /// than the same camera moving: the position jumped further than <see cref="CutMetres"/> per 1/60 s (at least that far),
    /// the view turned more than <see cref="CutDegrees"/>, or the projection changed. Blending across a cut would sweep the
    /// view through everything in between. Measured on a 402 s host recording: moving, the camera covers under
    /// 1.5 m per snapshot 99.9% of the time and under 5 m even easing in right after a cut; its ten cuts jumped 7 to 43 m.
    /// </summary>
    public static bool IsCut(in CameraPose a, in CameraPose b, long intervalMicros)
    {
        if (a.Orthographic != b.Orthographic)
        {
            return true;
        }

        float dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        double allowed = CutMetres * Math.Max(1.0, intervalMicros / (1_000_000.0 / 60));
        if ((double)((dx * dx) + (dy * dy) + (dz * dz)) > allowed * allowed)
        {
            return true;
        }

        // The angle between two rotations is 2·acos(|dot|) for unit quaternions.
        float dot = Math.Abs((a.QX * b.QX) + (a.QY * b.QY) + (a.QZ * b.QZ) + (a.QW * b.QW));
        return dot < CutCosineHalfAngle;
    }
}
