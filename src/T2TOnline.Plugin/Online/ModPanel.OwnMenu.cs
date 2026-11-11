#if HAS_INTEROP
using T2TOnline.Plugin.Online.Panel;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

// The panel's way into a friend's own game menus while joined (OnlineSession.Remap.cs): "Change my controls" (this game's
// own remapper) and "Your game's settings" (its whole Settings menu), on the Controller and Settings tabs; "Close it" while
// one is open (the way out should the pad not reach it). Opening one closes the panel, so the menu gets the keyboard and
// mouse. Apart from the rest of the panel (ModPanel.cs).
internal sealed partial class ModPanel
{
    /// <summary>How often the friend's own menus are read while the panel is open.</summary>
    private const long OwnMenuEveryMs = 250;

    private OwnMenuView _ownMenuView = OwnMenuView.None;
    private long _ownMenuAt = long.MinValue;

    /// <summary>Each frame while the panel is open: what the friend's own menus show, read four times a second (never while drawing).</summary>
    private void RefreshOwnMenu(long now)
    {
        if (_ownMenuAt != long.MinValue && now - _ownMenuAt < OwnMenuEveryMs)
        {
            return;
        }

        _ownMenuAt = now;
        _ownMenuView = Read("your own menus", () => _online.OwnMenu, OwnMenuView.None);
    }

    /// <summary>
    /// The row of the friend's own menus under a tab's heading, while joined as a friend: "Change my controls" and "Your game's
    /// settings" (or "Close it" while one is open) with the line about them. Nothing outside a friend's game.
    /// </summary>
    private void DrawOwnMenuRow(float x, ref float y, float w)
    {
        OwnMenuView view = _ownMenuView;
        if (!view.Available && !view.Open && view.Line.Length == 0)
        {
            return;
        }

        PanelDraw d = _draw;
        float u = d.Unit;
        if (view.Open)
        {
            if (d.Button(new Rect(x, y, 9 * u, 1.8f * u), "Close it", true, primary: true))
            {
                Queue("Closing your own menu", () => Show(_online.CloseOwnMenu(), PanelDraw.Muted));
            }
        }
        else
        {
            if (d.Button(new Rect(x, y, 11 * u, 1.8f * u), "Change my controls", view.Available, primary: true))
            {
                Queue("Change my controls", () => OpenOwnMenu(remapper: true));
            }

            if (d.Button(new Rect(x + (11.6f * u), y, 11 * u, 1.8f * u), "Your game's settings", view.Available))
            {
                Queue("Your game's settings", () => OpenOwnMenu(remapper: false));
            }
        }

        y += 2.2f * u;
        if (view.Line.Length > 0)
        {
            Paragraph(view.Line, x, ref y, w, PanelDraw.Muted);
        }

        y += 0.4f * u;
    }

    /// <summary>Opens the friend's own remapper or settings; once it's on its way the panel closes, so the menu gets the keyboard and mouse.</summary>
    private void OpenOwnMenu(bool remapper)
    {
        string line = _online.OpenOwnMenu(remapper);
        _ownMenuAt = long.MinValue;
        _ownMenuView = Read("your own menus", () => _online.OwnMenu, OwnMenuView.None);
        Show(line, _ownMenuView.Open ? PanelDraw.Muted : PanelDraw.Warn);
        if (_ownMenuView.Open)
        {
            Close(remapper ? "Change my controls" : "Your game's settings");
        }
    }
}
#endif
