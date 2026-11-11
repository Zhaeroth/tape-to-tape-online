using System;
using T2TOnline.Core.Snapshots;

namespace T2TOnline.Core.Tests.Snapshots;

public class CameraCutTests
{
    private const long Interval = 16_667;

    private static CameraPose At(float x, float z = 0, float yawDegrees = 0, bool orthographic = false)
    {
        float half = yawDegrees * MathF.PI / 360f;
        return new CameraPose(x, 10, z, 0, MathF.Sin(half), 0, MathF.Cos(half), 40, orthographic);
    }

    [Fact]
    public void Ordinary_and_fast_moves_are_not_cuts()
    {
        // The recording's moving camera: under 1.5 m a snapshot almost always, under 5 m easing in after a cut.
        Assert.False(CameraPose.IsCut(At(0), At(0.06f), Interval));
        Assert.False(CameraPose.IsCut(At(0), At(1.43f), Interval));
        Assert.False(CameraPose.IsCut(At(0), At(4.65f), Interval));
        Assert.False(CameraPose.IsCut(At(0, yawDegrees: 0), At(0, yawDegrees: 20), Interval));
    }

    [Fact]
    public void Jumps_turns_and_projection_changes_are_cuts()
    {
        // The recording's cuts jumped 7 to 43 m in one snapshot interval.
        Assert.True(CameraPose.IsCut(At(0), At(7.22f), Interval));
        Assert.True(CameraPose.IsCut(At(0), At(0, 43.28f), Interval));
        Assert.True(CameraPose.IsCut(At(0, yawDegrees: 0), At(0, yawDegrees: 90), Interval));
        Assert.True(CameraPose.IsCut(At(0), At(0, orthographic: true), Interval));
    }

    [Fact]
    public void A_longer_interval_allows_a_longer_move()
    {
        // Two snapshots lost: three intervals of fast easing is still one move, not a cut.
        Assert.True(CameraPose.IsCut(At(0), At(9), Interval));
        Assert.False(CameraPose.IsCut(At(0), At(9), 3 * Interval));
        // A shorter interval never tightens the limit below its floor.
        Assert.False(CameraPose.IsCut(At(0), At(5.9f), Interval / 4));
    }

    [Fact]
    public void Opposite_quaternion_signs_are_the_same_rotation()
    {
        CameraPose a = At(0, yawDegrees: 10);
        var b = new CameraPose(a.X, a.Y, a.Z, -a.QX, -a.QY, -a.QZ, -a.QW, a.Lens, a.Orthographic);
        Assert.False(CameraPose.IsCut(a, b, Interval));
    }
}
