#if HAS_INTEROP
using System;
using System.Collections.Generic;
using UnityEngine;

namespace T2TOnline.Plugin.Online.Panel;

/// <summary>
/// The mod panel's look and its drawing primitives, on Unity's immediate-mode GUI with plain rectangles (no layout pass,
/// no window callbacks): flat dark fills, a cool ice-blue accent, white and grey text. Everything is measured in
/// <see cref="Unit"/>, the body text size, which follows the screen height (21 px at 1080p, 28 at 1440p, 42 at 4K), so the
/// panel reads the same at every resolution. Fills and text are drawn on repaint events only; buttons run on every event
/// (they need the mouse ones), and every button takes its control id whether it can be clicked or not, so a button that
/// turns on or off between a press and its release doesn't move the ids of the ones after it. Nothing is allocated per
/// frame: the white texture is fetched once, and each text's <c>GUIContent</c> is made once and kept (a label given a
/// <see cref="string"/> would copy it into a new IL2CPP string at every call).
/// </summary>
internal sealed class PanelDraw
{
    /// <summary>Texts whose content is kept; past this many the cache starts again (texts that change, like a ping, come and go).</summary>
    private const int MaxContents = 512;

    /// <summary>The panel's background.</summary>
    public static readonly Color Background = new(0.055f, 0.075f, 0.105f, 0.95f);

    /// <summary>Thin lines between parts.</summary>
    public static readonly Color Divider = new(1f, 1f, 1f, 0.08f);

    /// <summary>Every other row of a list.</summary>
    public static readonly Color RowShade = new(1f, 1f, 1f, 0.035f);

    /// <summary>The accent: selected things, switches that are on.</summary>
    public static readonly Color Accent = new(0.36f, 0.67f, 0.93f, 1f);

    /// <summary>A selected row's fill.</summary>
    public static readonly Color Selected = new(0.12f, 0.24f, 0.36f, 1f);

    /// <summary>Main text.</summary>
    public static readonly Color Text = new(0.93f, 0.95f, 0.97f, 1f);

    /// <summary>Explanations and secondary text.</summary>
    public static readonly Color Muted = new(0.62f, 0.69f, 0.77f, 1f);

    /// <summary>Text of things that can't be used now.</summary>
    public static readonly Color Dim = new(0.42f, 0.47f, 0.53f, 1f);

    /// <summary>Good news.</summary>
    public static readonly Color Good = new(0.52f, 0.82f, 0.58f, 1f);

    /// <summary>Something to look at.</summary>
    public static readonly Color Warn = new(0.96f, 0.76f, 0.38f, 1f);

    /// <summary>Something went wrong.</summary>
    public static readonly Color Bad = new(0.94f, 0.47f, 0.42f, 1f);

    private static readonly Color Dimmed = new(0f, 0f, 0f, 0.35f);
    private static readonly Color ButtonFill = new(0.17f, 0.22f, 0.29f, 1f);
    private static readonly Color ButtonHover = new(0.23f, 0.30f, 0.39f, 1f);
    private static readonly Color ButtonOff = new(0.11f, 0.14f, 0.18f, 1f);
    private static readonly Color PrimaryFill = new(0.16f, 0.40f, 0.64f, 1f);
    private static readonly Color PrimaryHover = new(0.20f, 0.47f, 0.74f, 1f);
    private static readonly Color RowHover = new(1f, 1f, 1f, 0.06f);
    private static readonly Color SwitchOff = new(0.25f, 0.29f, 0.34f, 1f);

    private readonly Dictionary<(string Text, int Width, int Style), float> _heights = new();
    private readonly Dictionary<string, GUIContent> _contents = new(StringComparer.Ordinal);
    private Texture2D? _white;
    private GUIContent? _empty;
    private GUIStyle? _body;
    private GUIStyle? _bold;
    private GUIStyle? _small;
    private GUIStyle? _wrap;
    private GUIStyle? _title;
    private GUIStyle? _center;
    private GUIStyle? _right;
    private GUIStyle? _smallRight;
    private GUIStyle? _heading;
    private GUIStyle? _blank;
    private Event? _event;
    private bool _repaint;

    /// <summary>The body text size in pixels: the unit everything is measured in.</summary>
    public float Unit { get; private set; }

    /// <summary>Body text, one line.</summary>
    public GUIStyle Body => _body!;

    /// <summary>Bold body text, one line.</summary>
    public GUIStyle Bold => _bold!;

    /// <summary>Smaller text, one line.</summary>
    public GUIStyle Small => _small!;

    /// <summary>Smaller text, wrapped.</summary>
    public GUIStyle Wrap => _wrap!;

    /// <summary>The panel's title.</summary>
    public GUIStyle Title => _title!;

    /// <summary>Bold text, centred (buttons, tabs).</summary>
    public GUIStyle Center => _center!;

    /// <summary>Body text, right-aligned.</summary>
    public GUIStyle Right => _right!;

    /// <summary>Smaller text, right-aligned.</summary>
    public GUIStyle SmallRight => _smallRight!;

    /// <summary>A section's heading.</summary>
    public GUIStyle Heading => _heading!;

    /// <summary>Starts drawing <paramref name="current"/>: works out the unit from the screen height and (re)makes the styles when it changed.</summary>
    public void Begin(Event current)
    {
        _event = current;
        _repaint = current.type == EventType.Repaint;
        _white ??= Texture2D.whiteTexture;
        _empty ??= new GUIContent();
        float unit = Math.Clamp(MathF.Round(Screen.height / 52f), 12f, 48f);
        if (_body != null && unit == Unit)
        {
            return;
        }

        Unit = unit;
        _heights.Clear();
        int size = (int)unit;
        _body = Style(size, FontStyle.Normal, TextAnchor.MiddleLeft, wrap: false);
        _bold = Style(size, FontStyle.Bold, TextAnchor.MiddleLeft, wrap: false);
        _small = Style((int)MathF.Round(unit * 0.86f), FontStyle.Normal, TextAnchor.MiddleLeft, wrap: false);
        _wrap = Style((int)MathF.Round(unit * 0.86f), FontStyle.Normal, TextAnchor.UpperLeft, wrap: true);
        _title = Style((int)MathF.Round(unit * 1.4f), FontStyle.Bold, TextAnchor.MiddleLeft, wrap: false);
        _center = Style(size, FontStyle.Bold, TextAnchor.MiddleCenter, wrap: false);
        _right = Style(size, FontStyle.Normal, TextAnchor.MiddleRight, wrap: false);
        _smallRight = Style((int)MathF.Round(unit * 0.86f), FontStyle.Normal, TextAnchor.MiddleRight, wrap: false);
        _heading = Style((int)MathF.Round(unit * 1.1f), FontStyle.Bold, TextAnchor.MiddleLeft, wrap: false);
        _blank = new GUIStyle();
    }

    /// <summary>The dimmed screen behind the panel: the game is there, but not listening.</summary>
    public void Backdrop() => Fill(new Rect(0, 0, Screen.width, Screen.height), Dimmed);

    /// <summary>A flat fill.</summary>
    public void Fill(Rect area, Color colour)
    {
        if (!_repaint)
        {
            return;
        }

        Color previous = GUI.color;
        GUI.color = colour;
        GUI.DrawTexture(area, _white!);
        GUI.color = previous;
    }

    /// <summary><paramref name="text"/> in <paramref name="style"/> and <paramref name="colour"/>, clipped to <paramref name="area"/>.</summary>
    public void Label(Rect area, string text, GUIStyle style, Color colour)
    {
        if (!_repaint || text.Length == 0)
        {
            return;
        }

        Color previous = GUI.contentColor;
        GUI.contentColor = colour;
        GUI.Label(area, Content(text), style);
        GUI.contentColor = previous;
    }

    /// <summary>The height <paramref name="text"/> takes wrapped in <paramref name="style"/> at <paramref name="width"/> (remembered per text).</summary>
    public float Height(string text, GUIStyle style, float width)
    {
        var key = (text, (int)width, style.fontSize);
        if (!_heights.TryGetValue(key, out float height))
        {
            height = style.CalcHeight(Content(text), width);
            if (_heights.Count > 256)
            {
                _heights.Clear();
            }

            _heights[key] = height;
        }

        return height;
    }

    /// <summary>A button; true when it was clicked. A button that's off is drawn dimmed and can't be clicked.</summary>
    public bool Button(Rect area, string text, bool enabled = true, bool primary = false)
    {
        bool hover = enabled && Hovered(area);
        Fill(area, !enabled ? ButtonOff : primary ? (hover ? PrimaryHover : PrimaryFill) : hover ? ButtonHover : ButtonFill);
        Label(area, text, Center, enabled ? Text : Dim);
        bool clicked = GUI.Button(area, _empty!, _blank);
        return enabled && clicked;
    }

    /// <summary>A clickable area (a row, a tab) that lightens under the mouse; true when it was clicked. One that's off (the open tab, the selected row) does neither.</summary>
    public bool Clickable(Rect area, bool enabled = true)
    {
        if (enabled && Hovered(area))
        {
            Fill(area, RowHover);
        }

        bool clicked = GUI.Button(area, _empty!, _blank);
        return enabled && clicked;
    }

    /// <summary>An on/off switch drawn in <paramref name="area"/> (not clickable itself: the row around it is).</summary>
    public void Switch(Rect area, bool on)
    {
        Fill(area, on ? Accent : SwitchOff);
        float inset = Math.Max(2f, area.height * 0.16f);
        float knob = area.height - (2 * inset);
        Fill(new Rect(on ? area.x + area.width - inset - knob : area.x + inset, area.y + inset, knob, knob), on ? Color.white : Muted);
    }

    /// <summary>A small square marker (selected things).</summary>
    public void Marker(Rect area, bool on) => Fill(area, on ? Accent : SwitchOff);

    /// <summary>A horizontal line across <paramref name="width"/> at <paramref name="y"/>.</summary>
    public void Line(float x, float y, float width) => Fill(new Rect(x, y, width, Math.Max(1f, MathF.Round(Unit * 0.06f))), Divider);

    /// <summary>The mouse wheel over <paramref name="area"/> in this event: 1 for down (towards older rows), -1 for up, else 0.</summary>
    public int Scrolled(Rect area)
    {
        if (_event!.type != EventType.ScrollWheel || !Hovered(area))
        {
            return 0;
        }

        float delta = _event.delta.y;
        return delta > 0 ? 1 : delta < 0 ? -1 : 0;
    }

    /// <summary>The key pressed in this event when it's a key-down, else <see cref="KeyCode.None"/> (the panel holds the keyboard from the game while it's open).</summary>
    public KeyCode KeyDown => _event!.type == EventType.KeyDown ? _event.keyCode : KeyCode.None;

    /// <summary>True when the mouse is over <paramref name="area"/>.</summary>
    public bool Hovered(Rect area)
    {
        Vector2 mouse = _event!.mousePosition;
        return mouse.x >= area.x && mouse.x < area.x + area.width && mouse.y >= area.y && mouse.y < area.y + area.height;
    }

    /// <summary><paramref name="text"/>'s content, made once and kept (see <see cref="MaxContents"/>).</summary>
    private GUIContent Content(string text)
    {
        if (!_contents.TryGetValue(text, out GUIContent? content))
        {
            if (_contents.Count >= MaxContents)
            {
                _contents.Clear();
            }

            content = new GUIContent(text);
            _contents[text] = content;
        }

        return content;
    }

    /// <summary>A text style from the skin's label: white (the colour comes from <see cref="Label"/>), no markup, clipped.</summary>
    private static GUIStyle Style(int size, FontStyle fontStyle, TextAnchor anchor, bool wrap)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = size,
            fontStyle = fontStyle,
            alignment = anchor,
            wordWrap = wrap,
            richText = false,
            clipping = TextClipping.Clip,
        };
        style.normal.textColor = Color.white;
        return style;
    }
}
#endif
