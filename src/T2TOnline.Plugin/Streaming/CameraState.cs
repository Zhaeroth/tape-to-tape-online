#if HAS_INTEROP
using System;
using System.Globalization;
using T2TOnline.Core.Snapshots;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>Reading and imposing the rendered camera's pose.</summary>
internal static class CameraState
{
    /// <summary>The camera the match renders through: the game's own (<c>CameraController._camera</c>), else the main camera.</summary>
    public static Camera? GameCamera()
    {
        Camera? camera = Il2Cpp.CameraController._camera;
        return camera != null ? camera : Camera.main;
    }

    /// <summary>The game camera's current pose, or null when there's none.</summary>
    public static CameraPose? Read()
    {
        Camera? camera = GameCamera();
        if (camera == null)
        {
            return null;
        }

        Transform transform = camera.transform;
        Vector3 p = transform.position;
        Quaternion q = transform.rotation;
        return new CameraPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w, camera.orthographic ? camera.orthographicSize : camera.fieldOfView, camera.orthographic);
    }

    /// <summary>Narrowest and widest field of view a stream may set, in degrees.</summary>
    private const float MinFieldOfView = 1f;
    private const float MaxFieldOfView = 179f;

    /// <summary>Smallest and largest orthographic size a stream may set.</summary>
    private const float MinOrthographicSize = 0.01f;
    private const float MaxOrthographicSize = 10_000f;

    private static bool _lensClamped;

    /// <summary>
    /// Puts the main camera at <paramref name="pose"/>. Call after anything else that moves it this frame. The pose comes
    /// from the network (finite, the decoder checks): the lens is held to a range Unity accepts (logged once when a value
    /// is outside it), and a rotation that isn't one (zero length) leaves the camera's own.
    /// </summary>
    public static bool Apply(in CameraPose pose)
    {
        Camera? camera = GameCamera();
        if (camera == null)
        {
            return false;
        }

        var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
        float length = (rotation.x * rotation.x) + (rotation.y * rotation.y) + (rotation.z * rotation.z) + (rotation.w * rotation.w);
        if (length > 1e-6f)
        {
            camera.transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), rotation);
        }
        else
        {
            camera.transform.position = new Vector3(pose.X, pose.Y, pose.Z);
        }

        float lens = pose.Orthographic ? Math.Clamp(pose.Lens, MinOrthographicSize, MaxOrthographicSize) : Math.Clamp(pose.Lens, MinFieldOfView, MaxFieldOfView);
        if (lens != pose.Lens && !_lensClamped)
        {
            _lensClamped = true;
            ModLog.Warn(string.Create(CultureInfo.InvariantCulture, $"Stream player: the stream asked for a camera {(pose.Orthographic ? "orthographic size" : "field of view")} of {pose.Lens}; held to {lens} (logged once)."));
        }

        if (pose.Orthographic)
        {
            camera.orthographicSize = lens;
        }
        else
        {
            camera.fieldOfView = lens;
        }

        return true;
    }
}
#endif
