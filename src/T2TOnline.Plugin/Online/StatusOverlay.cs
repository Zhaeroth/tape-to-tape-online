#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// A small box in the top-left corner of the game window saying what the online session is doing: who's connected and
/// how, and short notices ("joined", "you're hosting your own lobby"). Without it, a friend's only feedback is the
/// MelonLoader console behind the game window. Drawn with Unity's immediate-mode GUI from the plugin's OnGUI; if the
/// game's build can't draw it, it turns itself off for the session.
/// <para>
/// Sized from the screen height like the menu panel (<c>Campaign/Menus/MenuPanel</c>), so it reads the same at 1080p
/// and 4K, and never wider than <see cref="MaxWidthShare"/> of the screen, long lines wrapping: the menu panel starts at
/// 28% of the width, and the net overlay (the same box, top-right) ends where this one would, so none of the three
/// covers another.
/// </para>
/// </summary>
internal static class StatusOverlay
{
    /// <summary>The widest a box gets, as a share of the screen's width (the menu panel starts at 28%).</summary>
    public const float MaxWidthShare = 0.26f;

    // A coloured span in a line: start, the colour as RRGGBB, text start, the text, end. Private-use characters, so
    // nothing else in a line (a Steam name) can make one, and any '<' in a line is drawn as is, never as markup.
    private const char SpanStart = '';
    private const char SpanText = '';
    private const char SpanEnd = '';

    private static readonly Box Left = new();
    private static readonly Box Right = new();
    private static GUIStyle? _style;
    private static int _styleSize;
    private static Texture2D? _white;
    private static bool _failed;

    /// <summary>
    /// Draws <paramref name="lines"/> in the top-left corner (top-right with <paramref name="right"/>); nothing when empty.
    /// Call from OnGUI only. The box is drawn on repaint events only (labels at fixed places need no layout pass), and the
    /// lines' markup, wrapping and size are worked out again only when a different list comes in or the screen changes:
    /// give it a new list when the lines change, never the same one changed. Each line's text goes to the game once, as
    /// a kept <see cref="GUIContent"/>, not as a new string every frame.
    /// </summary>
    public static void Draw(IReadOnlyList<string> lines, bool right = false)
    {
        if (_failed || lines.Count == 0)
        {
            return;
        }

        try
        {
            // Without an event to look at (never seen inside OnGUI), it draws as it always did rather than not at all.
            if (Event.current is { } current && current.type != EventType.Repaint)
            {
                return;
            }

            int size = Math.Clamp(Screen.height / 50, 12, 48);
            if (_style == null || _styleSize != size)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true, richText = true };
                _style.normal.textColor = Color.white;
                _styleSize = size;
            }

            Box cache = right ? Right : Left;
            if (!ReferenceEquals(cache.Lines, lines) || cache.Size != size || cache.ScreenWidth != Screen.width)
            {
                Layout(cache, lines, size, _style);
            }

            float pad = size * 0.6f;
            var box = new Rect(right ? Screen.width - cache.BoxWidth - (size * 0.75f) : size * 0.75f, size * 0.75f, cache.BoxWidth, cache.BoxHeight);
            _white ??= Texture2D.whiteTexture;
            Color previous = GUI.color;
            GUI.color = new Color(0.05f, 0.08f, 0.12f, 0.72f);
            GUI.DrawTexture(box, _white);
            GUI.color = previous;
            float y = box.y + (pad / 2);
            for (int i = 0; i < cache.Shown.Length; i++)
            {
                GUI.Label(new Rect(box.x + pad, y, cache.TextWidth, cache.Heights[i]), cache.Shown[i], _style);
                y += cache.Heights[i];
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            ModLog.Error("Status overlay failed; it's off for this session (the console still shows everything)", ex);
        }
    }

    /// <summary><paramref name="text"/>, drawn bold in <paramref name="colour"/> when it's part of a status line.</summary>
    public static string Coloured(string text, Color colour) =>
        string.Create(CultureInfo.InvariantCulture, $"{SpanStart}{Channel(colour.r):X2}{Channel(colour.g):X2}{Channel(colour.b):X2}{SpanText}{text}{SpanEnd}");

    private static int Channel(float value) => (int)Math.Round(Math.Clamp(value, 0f, 1f) * 255);

    /// <summary>
    /// Works out a box for <paramref name="lines"/>: each line as markup in a kept <see cref="GUIContent"/>, the text width
    /// (the widest line, up to the cap) and each line's height once wrapped to it.
    /// </summary>
    private static void Layout(Box cache, IReadOnlyList<string> lines, int size, GUIStyle style)
    {
        float pad = size * 0.6f;
        float maxText = Math.Max(size * 8f, (Screen.width * MaxWidthShare) - (2 * pad));
        var shown = new GUIContent[lines.Count];
        float width = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            shown[i] = new GUIContent(Markup(lines[i]));
            width = Math.Max(width, style.CalcSize(shown[i]).x);
        }

        // A little over the widest line, or a line that just fits would wrap on rounding.
        float textWidth = Math.Min(width + 1, maxText);
        var heights = new float[lines.Count];
        float height = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            heights[i] = Math.Max(style.CalcHeight(shown[i], textWidth), size * 1.4f);
            height += heights[i];
        }

        cache.Lines = lines;
        cache.Size = size;
        cache.ScreenWidth = Screen.width;
        cache.Shown = shown;
        cache.Heights = heights;
        cache.TextWidth = textWidth;
        cache.BoxWidth = textWidth + (2 * pad);
        cache.BoxHeight = height + pad;
    }

    /// <summary>A line as rich text: '&lt;' shown as a look-alike, so only <see cref="Coloured"/> spans become markup.</summary>
    private static string Markup(string line) =>
        line.Replace('<', '‹')
            .Replace(SpanStart.ToString(), "<color=#", StringComparison.Ordinal)
            .Replace(SpanText.ToString(), "><b>", StringComparison.Ordinal)
            .Replace(SpanEnd.ToString(), "</b></color>", StringComparison.Ordinal);

    /// <summary>One corner's box as last worked out: which lines, at which font size and screen width, as content, and its size.</summary>
    private sealed class Box
    {
        public IReadOnlyList<string>? Lines { get; set; }

        public int Size { get; set; }

        public int ScreenWidth { get; set; }

        public GUIContent[] Shown { get; set; } = Array.Empty<GUIContent>();

        public float[] Heights { get; set; } = Array.Empty<float>();

        public float TextWidth { get; set; }

        public float BoxWidth { get; set; }

        public float BoxHeight { get; set; }
    }
}
#endif
