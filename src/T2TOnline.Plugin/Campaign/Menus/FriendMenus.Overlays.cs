#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppBishopGames.UI;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// What a friend's screen shows around the host's overlays and waits (v1):
/// <list type="bullet">
/// <item>The host's settings show as a note ("Alex is in the settings."): each PC's settings are its own, so nothing of
/// the host's is shown or applied here.</item>
/// <item>The host's in-match menu hides itself once an overlay it opens is up (<c>GameMenu.ShowOverlayMenu</c>: the
/// controller screen, the glossary, the settings, the unlockables, the lineup): a real in-match menu here is hidden too
/// while any of those is up over it, real, a note or text, and shown again when it goes. The map's menu stays drawn under
/// the host's overlays (nothing in the game hides it): a real one here stays under a real overlay, which this PC's game
/// draws over it as the host's does, and is hidden under a note or text, which this PC draws in place of the host's
/// overlay.</item>
/// <item>A real view waiting for data that follows the host menu's open (its run, event choices, prompt, run summary,
/// run-start data or preview, the unlockables' states or a run history page) shows nothing for its first second (this PC's
/// screen as it was), then the panel: most waits end well within it, and the text flashed for a moment before the real
/// menu.</item>
/// </list>
/// </summary>
internal sealed partial class FriendMenus
{
    // The overlays the host's in-match menu opens over itself (GameMenu.ShowOverlayMenu's callers), which hide its canvas
    // once shown.
    private static readonly HashSet<string> OverlayTypes = new(StringComparer.Ordinal)
    {
        ControllerViews.Type, MenuPayloads.Glossary, MenuPayloads.Settings, UnlockablesView.Type, LineupViews.Type,
    };

    // The real map menus whose canvas this PC hid, by native object (put back as soon as they shouldn't be hidden).
    private readonly Dictionary<IntPtr, AbstractMenu> _hiddenMaps = new();

    // The real views covered now (by serial), for the log's one line per cover.
    private readonly HashSet<uint> _covered = new();

    /// <summary>The host's settings: a note (its values are each PC's own). True when <paramref name="view"/> was one.</summary>
    private bool OpenSettingsNote(View view)
    {
        if (view.Type != MenuPayloads.Settings)
        {
            return false;
        }

        view.Mode = Mode.Note;
        view.Note = "is in the settings.";
        view.NoteWord = LobbyWords.NoteHostInSettings;
        _tally.Note(view.Serial);
        Log($"Menus: {LogNames.HostTag()} is in the settings ({view.LogId}); not shown here (each PC's settings are its own).");
        return true;
    }

    /// <summary>
    /// Every frame: a real in-match menu is hidden while an overlay it opened is up over it (real, a note or text), as the
    /// host's game hides its own; a real map menu is hidden while a note or text overlay is up over it. Each comes back once
    /// that's gone.
    /// </summary>
    private void CoverUnderOverlays()
    {
        foreach (View under in _views.Values)
        {
            bool game = under.Type == MenuPayloads.Game;
            bool map = under.Type == WorldMapType;
            if ((!game && !map) || under.Mode != Mode.Real || under.Menu is not { } open)
            {
                continue;
            }

            View? over = Overlay(under, realCounts: game);
            bool covered = over != null;
            if (game)
            {
                // (Nothing to do, and no call a frame, while it's neither covered nor was: a pause menu with no overlay.)
                if (covered || _covered.Contains(under.Serial))
                {
                    Try(under, covered ? "hiding it under the host's overlay" : "showing it again after the host's overlay", () => GameMenuViews.Cover(open, covered));
                }
            }
            else
            {
                CoverMap(under, open, covered);
            }

            if (covered ? _covered.Add(under.Serial) : _covered.Remove(under.Serial))
            {
                Log(covered
                    ? $"Menus: {under.LogId} is hidden here under {over!.LogId} ({(over.Mode == Mode.Real ? "real" : over.Mode == Mode.Note ? "a note" : "text")}), as {LogNames.HostTag()}'s is."
                    : $"Menus: {under.LogId} shows here again, as on {LogNames.HostTag()}'s screen.");
            }
        }

        // A map menu hidden here whose view is gone, or no longer covered: its canvas back.
        if (_hiddenMaps.Count > 0)
        {
            List<IntPtr>? back = null;
            foreach (KeyValuePair<IntPtr, AbstractMenu> hidden in _hiddenMaps)
            {
                if (!CoveredMap(hidden.Key))
                {
                    (back ??= new List<IntPtr>()).Add(hidden.Key);
                }
            }

            if (back != null)
            {
                foreach (IntPtr pointer in back)
                {
                    ShowMap(pointer);
                }
            }
        }
    }

    /// <summary>True while a real map menu view shows the menu at <paramref name="pointer"/> and is covered (no allocation: run every frame one is hidden).</summary>
    private bool CoveredMap(IntPtr pointer)
    {
        foreach (View view in _views.Values)
        {
            if (view.Type == WorldMapType && view.Mode == Mode.Real && view.Menu?.Pointer == pointer && _covered.Contains(view.Serial))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The newest overlay view over <paramref name="under"/> that covers it (a real one only when <paramref name="realCounts"/>), or null.</summary>
    private View? Overlay(View under, bool realCounts)
    {
        View? found = null;
        foreach (View view in _views.Values)
        {
            if (view.Serial <= under.Serial || !OverlayTypes.Contains(view.Type))
            {
                continue;
            }

            bool shows = view.Mode switch
            {
                Mode.Real => realCounts && view.Menu != null,
                Mode.Note => true,
                Mode.Panel => !Quiet(view),
                Mode.Pending => view.PanelShown,
                _ => false,
            };
            if (shows && (found == null || view.Serial > found.Serial))
            {
                found = view;
            }
        }

        return found;
    }

    /// <summary>Hides or shows a real map menu's canvas (its own; put back by <see cref="ShowMap"/>, at the latest when following stops).</summary>
    private void CoverMap(View view, AbstractMenu open, bool covered)
    {
        if (!covered)
        {
            ShowMap(open.Pointer);
            return;
        }

        if (_hiddenMaps.ContainsKey(open.Pointer))
        {
            return;
        }

        Try(view, "hiding it under the host's overlay", () =>
        {
            UnityEngine.Canvas? canvas = open.m_Canvas;
            if (canvas != null && canvas.enabled)
            {
                canvas.enabled = false;
                _hiddenMaps[open.Pointer] = open;
            }
        });
    }

    /// <summary>A map menu this PC hid shows again. Never throws.</summary>
    private void ShowMap(IntPtr pointer)
    {
        if (!_hiddenMaps.Remove(pointer, out AbstractMenu? menu))
        {
            return;
        }

        try
        {
            if (menu != null && menu.m_Canvas is { } canvas && canvas != null)
            {
                canvas.enabled = true;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Menus: couldn't show the map menu again here ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>Following stopped: every map menu hidden here shows again (<paramref name="gameClosing"/>: no game calls).</summary>
    private void OverlaysStopped(bool gameClosing)
    {
        if (!gameClosing)
        {
            foreach (IntPtr pointer in new List<IntPtr>(_hiddenMaps.Keys))
            {
                ShowMap(pointer);
            }
        }

        _hiddenMaps.Clear();
        _covered.Clear();
    }

    /// <summary>
    /// <paramref name="view"/> waits for data that follows the host menu's open (<paramref name="why"/>): as text, but nothing
    /// shows for its first <see cref="PendingPanelSeconds"/> (this PC's screen as it was).
    /// </summary>
    private void Wait(View view, string why)
    {
        ToPanel(view, why);
        if (view.QuietUntil == 0)
        {
            view.QuietUntil = FrameClock.Seconds + PendingPanelSeconds;
        }
    }

    /// <summary>True while a waiting view shows nothing yet (<see cref="Wait"/>).</summary>
    private static bool Quiet(View view) => view.Mode == Mode.Panel && view.QuietUntil > FrameClock.Seconds;

    /// <summary>Every frame: a wait whose quiet second is over shows as text now.</summary>
    private void QuietTick()
    {
        double now = FrameClock.Seconds;
        foreach (View view in _views.Values)
        {
            if (view.QuietUntil > 0 && now >= view.QuietUntil)
            {
                view.QuietUntil = -1; // once per view: a later wait shows its text at once
                _panelDirty = true;
            }
        }
    }
}
#endif
