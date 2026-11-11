#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>One choice on the spectator panel: its text, its kind (event choices) and whether the host's game greys it out.</summary>
/// <param name="Text">What the choice says.</param>
/// <param name="Kind">What kind of choice it is (an event choice's type, in words), or "".</param>
/// <param name="Off">The host's game shows it but it can't be picked.</param>
internal sealed record PanelRow(string Text, string Kind, bool Off);

/// <summary>What the spectator panel shows: the host's menu as text.</summary>
/// <param name="Title">The menu's name ("Event", "Relic reward"...).</param>
/// <param name="Host">The host's name.</param>
/// <param name="Note">A short note under the title (why it's shown as text), or "".</param>
/// <param name="Choices">The menu's choices, in order.</param>
/// <param name="Lines">The menu's other text, a line per row.</param>
/// <param name="Selected">The choice the host's cursor is on, or -1.</param>
/// <param name="Pressed">The choice just clicked on the host's screen, or -1.</param>
internal sealed record PanelContent(string Title, string Host, string Note, IReadOnlyList<PanelRow> Choices, IReadOnlyList<string> Lines, int Selected, int Pressed)
{
    /// <summary>A colour for each of <see cref="Lines"/> (a swatch drawn before it: a controller screen's player colours), or null for none.</summary>
    public IReadOnlyList<Color?>? LineColors { get; init; }
}

/// <summary>
/// The spectator panel (D43, v1): a host's menu a friend's game can't show for real (almost every menu is real since v1:
/// this is the fallback, with why, and a note's fallback when the game's own toast can't show it) drawn as text in the
/// middle of the friend's screen, clear of the status box on its left, with the host's cursor marked as it moves. Unity's
/// immediate-mode GUI from the plugin's OnGUI, like the status box (<see cref="Online.StatusOverlay"/>): drawn on repaint
/// events only, laid out again only when its text or the screen size changes (the host's cursor and clicks only move the
/// highlight; the styles are made once per font size; each text's <c>GUIContent</c> is made with the layout, so a repaint
/// hands the engine no new strings), sized from the screen height so it reads the same at 1080p and 4K. Host text is drawn as plain text (no markup). If the game's build can't
/// draw it, it turns itself off until the game restarts.
/// </summary>
internal static class MenuPanel
{
    // Space kept between the status box at its widest (StatusOverlay.MaxWidthShare) and the panel's left edge, as a share
    // of the screen's width: the panel starts at 28% or more.
    private const float StatusGapShare = 0.02f;

    private static readonly Color Background = new(0.05f, 0.08f, 0.12f, 0.9f);
    private static readonly Color Accent = new(0.98f, 0.74f, 0.25f, 1f);
    private static readonly Color SelectedRow = new(0.98f, 0.74f, 0.25f, 0.28f);
    private static readonly Color PressedRow = new(0.98f, 0.74f, 0.25f, 0.6f);
    private static readonly Color Dim = new(0.72f, 0.76f, 0.82f, 1f);
    private static readonly Color Greyed = new(0.55f, 0.57f, 0.6f, 1f);

    private static PanelContent? _content;
    private static GUIContent? _marker;
    private static Texture2D? _white;
    private static Layout? _layout;
    private static Styles? _styles;
    private static bool _failed;

    /// <summary>Shows <paramref name="content"/> from the next frame (a new record each change: the layout is kept per record).</summary>
    public static void Show(PanelContent content) => _content = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>Hides the panel.</summary>
    public static void Clear() => _content = null;

    /// <summary>Draws the panel when there's something to show. Call from OnGUI only.</summary>
    public static void Draw()
    {
        PanelContent? content = _content;
        if (_failed || content == null)
        {
            return;
        }

        try
        {
            if (Event.current is { } current && current.type != EventType.Repaint)
            {
                return;
            }

            if (_layout == null || !_layout.Fits(content) || _layout.Width != Screen.width || _layout.Height != Screen.height)
            {
                int size = Math.Clamp(Screen.height / 40, 14, 60);
                if (_styles == null || _styles.Size != size)
                {
                    _styles = new Styles(size);
                }

                _layout = new Layout(content, _styles);
            }

            _white ??= Texture2D.whiteTexture;

            // The host's cursor and clicks only move the highlight: drawn here, not laid out.
            Color previous = GUI.color;
            GUI.color = Background;
            GUI.DrawTexture(_layout.Panel, _white);
            Highlight(content.Selected, SelectedRow, content.Pressed);
            Highlight(content.Pressed, PressedRow, -1);
            GUI.color = previous;
            foreach (Layout.Swatch swatch in _layout.Swatches)
            {
                GUI.color = swatch.Colour;
                GUI.DrawTexture(swatch.Area, _white);
            }

            GUI.color = previous;
            foreach (Layout.Text text in _layout.Texts)
            {
                GUI.Label(text.Area, text.Content, text.Style);
            }

            Marker(content.Selected);
            if (content.Pressed != content.Selected)
            {
                Marker(content.Pressed);
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            ModLog.Error("Menu panel failed; it's off until the game restarts (the log still names each host menu)", ex);
        }
    }

    /// <summary>Fills choice <paramref name="row"/>'s band (unless it's <paramref name="unless"/>, drawn in its own colour), when it's on the panel.</summary>
    private static void Highlight(int row, Color colour, int unless)
    {
        if (row >= 0 && row != unless && row < _layout!.Rows.Count)
        {
            GUI.color = colour;
            GUI.DrawTexture(_layout.Rows[row].Band, _white);
        }
    }

    /// <summary>The cursor's marker beside choice <paramref name="row"/>, when it's on the panel.</summary>
    private static void Marker(int row)
    {
        if (row >= 0 && row < _layout!.Rows.Count)
        {
            _marker ??= new GUIContent("►");
            GUI.Label(_layout.Rows[row].Marker, _marker, _layout.Styles.Marker);
        }
    }

    /// <summary>The panel's text styles at one font size (made inside OnGUI: they copy the skin's label).</summary>
    private sealed class Styles
    {
        public Styles(int size)
        {
            Size = size;
            Title = Style(size * 1.15f, Accent, FontStyle.Bold);
            Note = Style(size * 0.75f, Dim, FontStyle.Italic);
            Row = Style(size, Color.white, FontStyle.Normal);
            Greyed = Style(size, MenuPanel.Greyed, FontStyle.Normal);
            Kind = Style(size * 0.72f, Dim, FontStyle.Normal);
            Line = Style(size * 0.82f, Dim, FontStyle.Normal);
            Marker = Style(size, Accent, FontStyle.Bold);
        }

        public int Size { get; }

        public GUIStyle Title { get; }

        public GUIStyle Note { get; }

        public GUIStyle Row { get; }

        public GUIStyle Greyed { get; }

        public GUIStyle Kind { get; }

        public GUIStyle Line { get; }

        public GUIStyle Marker { get; }

        private static GUIStyle Style(float size, Color colour, FontStyle fontStyle)
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = (int)size, wordWrap = true, richText = false, fontStyle = fontStyle };
            style.normal.textColor = colour;
            return style;
        }
    }

    /// <summary>
    /// The panel laid out for one content's text at one screen size: the panel's rectangle, each choice's row and the texts
    /// to draw. The selection and the click flash aren't part of it (the host's cursor moves often).
    /// </summary>
    private sealed class Layout
    {
        public Layout(PanelContent content, Styles styles)
        {
            Content = content;
            Styles = styles;
            Width = Screen.width;
            Height = Screen.height;
            int size = styles.Size;
            GUIStyle title = styles.Title;
            GUIStyle note = styles.Note;
            GUIStyle row = styles.Row;
            GUIStyle greyed = styles.Greyed;
            GUIStyle kind = styles.Kind;
            GUIStyle line = styles.Line;

            float pad = size * 0.8f;
            float panelWidth = Math.Clamp(Screen.width * 0.44f, size * 22f, Math.Max(size * 10f, Screen.width - (2f * size)));

            // Centred, but never over the status box on the left (it's at most StatusOverlay.MaxWidthShare of the width):
            // a panel wider than the space between the two moves right and narrows instead.
            float x = Math.Max((Screen.width - panelWidth) / 2f, Screen.width * (Online.StatusOverlay.MaxWidthShare + StatusGapShare));
            panelWidth = Math.Min(panelWidth, Math.Max(size * 10f, Screen.width - x - size));
            float inner = panelWidth - (2f * pad);
            float markerWidth = size * 1.3f;
            float top = Screen.height * 0.12f;
            float bottomLimit = Screen.height * 0.92f;
            float y = top + pad;

            y = Add(title, $"{content.Title} · on {(content.Host.Length > 0 ? content.Host + "'s" : "the host's")} screen", x + pad, y, inner) + (size * 0.2f);
            if (content.Note.Length > 0)
            {
                y = Add(note, content.Note, x + pad, y, inner) + (size * 0.2f);
            }

            y += size * 0.3f;
            for (int i = 0; i < content.Choices.Count; i++)
            {
                PanelRow choice = content.Choices[i];
                GUIStyle style = choice.Off ? greyed : row;
                var textContent = new GUIContent(choice.Text);
                GUIContent? kindContent = choice.Kind.Length > 0 ? new GUIContent(choice.Kind) : null;
                float textHeight = style.CalcHeight(textContent, inner - markerWidth);
                float kindHeight = kindContent != null ? kind.CalcHeight(kindContent, inner - markerWidth) : 0f;
                float rowHeight = textHeight + kindHeight + (size * 0.3f);
                if (y + rowHeight > bottomLimit)
                {
                    y = Add(line, $"… {content.Choices.Count - i} more", x + pad, y, inner);
                    break;
                }

                Rows.Add(new Row(new Rect(x + (pad * 0.5f), y, panelWidth - pad, rowHeight), new Rect(x + pad, y + (size * 0.15f), markerWidth, textHeight)));
                Texts.Add(new Text(new Rect(x + pad + markerWidth, y + (size * 0.15f), inner - markerWidth, textHeight), textContent, style));
                if (kindContent != null && kindHeight > 0)
                {
                    Texts.Add(new Text(new Rect(x + pad + markerWidth, y + (size * 0.15f) + textHeight, inner - markerWidth, kindHeight), kindContent, kind));
                }

                y += rowHeight;
            }

            if (content.Lines.Count > 0)
            {
                y += size * 0.4f;
            }

            float swatchSize = size * 0.7f;
            for (int i = 0; i < content.Lines.Count; i++)
            {
                var lineContent = new GUIContent(content.Lines[i]);
                Color? colour = content.LineColors != null && i < content.LineColors.Count ? content.LineColors[i] : null;
                float indent = colour != null ? swatchSize + (size * 0.4f) : 0f;
                float lineHeight = line.CalcHeight(lineContent, inner - indent);
                if (y + lineHeight > bottomLimit)
                {
                    y = Add(line, $"… {content.Lines.Count - i} more lines", x + pad, y, inner);
                    break;
                }

                if (colour is { } swatch)
                {
                    Swatches.Add(new Swatch(new Rect(x + pad, y + ((Math.Min(lineHeight, size * 1.2f) - swatchSize) / 2f), swatchSize, swatchSize), new Color(swatch.r, swatch.g, swatch.b, 1f)));
                }

                Texts.Add(new Text(new Rect(x + pad + indent, y, inner - indent, lineHeight), lineContent, line));
                y += lineHeight;
            }

            Panel = new Rect(x, top, panelWidth, y - top + pad);
        }

        public PanelContent Content { get; }

        public Styles Styles { get; }

        public int Width { get; }

        public int Height { get; }

        public Rect Panel { get; }

        public List<Row> Rows { get; } = new();

        public List<Text> Texts { get; } = new();

        public List<Swatch> Swatches { get; } = new();

        /// <summary>True when <paramref name="content"/> shows the same text (only its selection or click flash may differ).</summary>
        public bool Fits(PanelContent content) =>
            ReferenceEquals(content, Content) ||
            (content.Title == Content.Title && content.Host == Content.Host && content.Note == Content.Note &&
             ReferenceEquals(content.Choices, Content.Choices) && ReferenceEquals(content.Lines, Content.Lines) && ReferenceEquals(content.LineColors, Content.LineColors));

        private float Add(GUIStyle style, string value, float x, float y, float width)
        {
            var content = new GUIContent(value);
            float height = style.CalcHeight(content, width);
            Texts.Add(new Text(new Rect(x, y, width, height), content, style));
            return y + height;
        }

        /// <summary>A choice's row: the band filled when it's selected or clicked, and where the cursor's marker goes.</summary>
        public readonly record struct Row(Rect Band, Rect Marker);

        /// <summary>A text drawn in a rectangle (its engine-side content made once, with the layout).</summary>
        public readonly record struct Text(Rect Area, GUIContent Content, GUIStyle Style);

        /// <summary>A colour swatch drawn before a line (a player's colour).</summary>
        public readonly record struct Swatch(Rect Area, Color Colour);
    }
}
#endif
