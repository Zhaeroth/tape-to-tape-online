using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MelonLoader;
using MelonLoader.Utils;
#if HAS_INTEROP
using UnityEngine;
using UnityEngine.SceneManagement;
#endif

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// A mixed install's whole life (D125: T2TOnline.Core or T2TOnline.Steam isn't the plugin's build, see <see cref="InstallCheck"/>):
/// the plugin stays inert. Nothing here may touch T2TOnline.Core (it's the part that may not load) or, before MelonLoader's
/// generator, an interop assembly (D108): it logs through MelonLoader, takes the plugin out of MelonLoader's events, and puts an
/// English notice on the title with Unity's own GUI (the game's words need the library that's wrong: a failure's fallback).
/// </summary>
internal static class InertPlugin
{
    private const string UnregisterReason = "part of T2T Online is from another version; see the install check's error above";

    // Kept alive for as long as MelonLoader holds the subscription.
    private static LemonAction? _onGui;
    private static IReadOnlyList<string> _lines = Array.Empty<string>();
    private static string? _staleInteropFailure;
    private static bool _drawFailed;

    /// <summary>
    /// From the constructor: the old interop assemblies are still cleared when MelonLoader's generator will remake them (D108),
    /// or a game update would also stop the game from starting. That uses T2TOnline.Core, which may not load here: tried, and a
    /// failure kept for the log. Never throws.
    /// </summary>
    public static void ClearStaleInterop()
    {
        try
        {
            ClearNow();
        }
        catch (Exception ex)
        {
            _staleInteropFailure = ex.GetType().Name;
        }
    }

    /// <summary>
    /// From <c>OnPreInitialization</c>, MelonLoader's first call to the plugin after its constructor: the error line, crash
    /// reporting and analytics kept off as on a whole install (<see cref="InertHousekeeping"/>: the game still runs modded), then
    /// the plugin out of MelonLoader's events (no online patch, no session, no log file), and the title's notice. The
    /// housekeeping's checks and the notice take their events only after that. Never throws.
    /// </summary>
    public static void Stop(MelonPlugin plugin)
    {
        // Each step on its own: the error line said, the plugin out of MelonLoader's calls, the notice up, whatever failed before.
        Step("error line", () =>
        {
            MelonLogger.Instance log = plugin.LoggerInstance;
            log.Error(InstallCheck.LogLine ?? "Install check: mixed install; T2T Online is off this session.");
            if (_staleInteropFailure != null)
            {
                log.Warning($"Install check: the check of MelonLoader's interop assemblies against the game couldn't run ({_staleInteropFailure}): " +
                    "if the game was just updated, MelonLoader's generator may stop it from starting until T2T Online is installed again.");
            }
            else if (StaleInterop.Report != null)
            {
                log.Msg(WithoutGameFolder(StaleInterop.Report));
            }

            log.Msg("Install check: the title shows an English notice in its corner saying what to do; the game's own prompt can't be used, as its words are in T2T Online's library, the part that's another build.");
        });
        Step("housekeeping", () => InertHousekeeping.Start(plugin));
        Step("unregistering", () => plugin.Unregister(UnregisterReason));

        // Every event subscription after unregistering: MelonLoader drops this assembly's subscriptions as it unregisters.
        Step("housekeeping's checks", InertHousekeeping.Subscribe);
        Step("notice", () =>
        {
            _lines = InstallCheck.Verdict is { } verdict ? InstallLayout.MixedNotice(verdict) : Array.Empty<string>();
            _onGui = OnGui;
            MelonEvents.OnGUI.Subscribe(_onGui);
        });
    }

    private static void Step(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Say($"T2T Online: the mixed install's {what} failed ({ex.GetType().Name}).");
        }
    }

    // MelonLoader's own log, which can't take the game down from here.
    private static void Say(string line)
    {
        try
        {
            MelonLogger.Error(line);
        }
        catch (Exception)
        {
            // Nothing else to tell it to.
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearNow() => StaleInterop.ClearIfGameChanged();

    // The interop check's report can quote a file error, whose message holds the file's full path. The session log's scrubber
    // labels the game folder; it needs T2T Online's library, so this line, written straight to MelonLoader's log, does the same
    // by hand (every file the check touches is under MelonLoader's folder, in the game folder).
    private static string WithoutGameFolder(string line) =>
        InstallLayout.LabelGameFolder(line, MelonEnvironment.MelonBaseDirectory, MelonEnvironment.GameRootDirectory);

    // Every OnGUI pass: the notice, drawn only once Unity's GUI is up (long after the generator), so this method names nothing of
    // the interop and calls the drawer, compiled only then.
    private static void OnGui()
    {
        if (_drawFailed || _lines.Count == 0)
        {
            return;
        }

        try
        {
#if HAS_INTEROP
            InertNotice.Draw(_lines);
#endif
        }
        catch (Exception ex)
        {
            _drawFailed = true;
            Say($"T2T Online: the mixed install's notice couldn't be drawn ({ex.GetType().Name}); it's off for this session.");
        }
    }
}

#if HAS_INTEROP
/// <summary>
/// The mixed install's notice: a box in the top-left corner while the title screen is up, drawn as the status box is (Unity's
/// immediate-mode GUI, sized from the screen's height), but on its own, without the mod's log or library.
/// </summary>
internal static class InertNotice
{
    private const string TitleScene = "start_screen";
    private const long SceneLookMs = 500;

    private static GUIContent[] _shown = Array.Empty<GUIContent>();
    private static float[] _heights = Array.Empty<float>();
    private static GUIStyle? _style;
    private static int _size;
    private static int _screenWidth;
    private static float _textWidth;
    private static float _boxHeight;
    private static long _nextSceneLook;
    private static bool _onTitle;
    private static Texture2D? _white;

    /// <summary>Draws <paramref name="lines"/> while the title screen is the active scene. Call from OnGUI only.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Draw(IReadOnlyList<string> lines)
    {
        if (Event.current is { } current && current.type != EventType.Repaint)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now >= _nextSceneLook)
        {
            _nextSceneLook = now + SceneLookMs;
            _onTitle = SceneManager.GetActiveScene().name == TitleScene;
        }

        if (!_onTitle)
        {
            return;
        }

        int size = Math.Clamp(Screen.height / 50, 12, 48);
        if (_style == null || _size != size || _screenWidth != Screen.width || _shown.Length != lines.Count)
        {
            Layout(lines, size);
        }

        float pad = size * 0.6f;
        var box = new Rect(size * 0.75f, size * 0.75f, _textWidth + (2 * pad), _boxHeight + pad);
        _white ??= Texture2D.whiteTexture;
        Color previous = GUI.color;
        GUI.color = new Color(0.05f, 0.08f, 0.12f, 0.85f);
        GUI.DrawTexture(box, _white);
        GUI.color = previous;
        float y = box.y + (pad / 2);
        for (int i = 0; i < _shown.Length; i++)
        {
            GUI.Label(new Rect(box.x + pad, y, _textWidth, _heights[i]), _shown[i], _style);
            y += _heights[i];
        }
    }

    private static void Layout(IReadOnlyList<string> lines, int size)
    {
        _style = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true, richText = false };
        _style.normal.textColor = Color.white;
        _size = size;
        _screenWidth = Screen.width;
        float pad = size * 0.6f;
        float maxText = Math.Max(size * 8f, (Screen.width * 0.4f) - (2 * pad));
        _shown = new GUIContent[lines.Count];
        float width = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            _shown[i] = new GUIContent(lines[i]);
            width = Math.Max(width, _style.CalcSize(_shown[i]).x);
        }

        _textWidth = Math.Min(width + 1, maxText);
        _heights = new float[lines.Count];
        _boxHeight = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            _heights[i] = Math.Max(_style.CalcHeight(_shown[i], _textWidth), size * 1.4f);
            _boxHeight += _heights[i];
        }
    }
}
#endif
