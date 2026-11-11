#if HAS_INTEROP
using System;
using Il2CppInterop.Runtime;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// A callback once per frame just before rendering, after every LateUpdate, Cinemachine's included
/// (<c>RenderPipelineManager.beginContextRendering</c>; this build strips <c>Application.onBeforeRender</c>'s add method).
/// The host reads the camera pose that actually renders there (blends, shake and FOV tweens included); a friend's PC
/// imposes the stream's pose there, before the game's own <c>CameraController.OnBeginCameraRendering</c> reads it.
/// </summary>
internal static class RenderHook
{
    private static Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>? _listener;
    private static int _failures;

    /// <summary>Raised before each frame renders. Handlers must be quick and must not throw.</summary>
    public static event Action? BeforeRender;

    /// <summary>True once the callback is in.</summary>
    public static bool Installed => _listener != null;

    /// <summary>Adds the callback (once). False if this build can't take it; callers then fall back to LateUpdate.</summary>
    public static bool Install()
    {
        if (_listener != null)
        {
            return true;
        }

        try
        {
            _listener = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>>(
                new Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>((_, _) => Raise()));
            RenderPipelineManager.add_beginContextRendering(_listener);
            ModLog.Info("Render hook: camera pose read and imposed just before each frame renders.");
            return true;
        }
        catch (Exception ex)
        {
            _listener = null;
            ModLog.Warn($"Render hook: unavailable ({ModLog.SafeErrorText(ex)}); the camera pose is read and imposed in LateUpdate instead.");
            return false;
        }
    }

    private static void Raise()
    {
        // Inside the engine's render loop: never throw.
        try
        {
            BeforeRender?.Invoke();
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Error("Render hook: a handler failed", ex);
            }
        }
    }
}
#endif
