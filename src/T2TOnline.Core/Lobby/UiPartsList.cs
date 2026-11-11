using System;
using System.Collections.Generic;

namespace T2TOnline.Core.Lobby;

/// <summary>A prefab the native lobby clones from: a menu definition's name, its bundle and its root's name.</summary>
/// <param name="Definition">The menu definition's name in the game's catalogue.</param>
/// <param name="Bundle">The start of its bundle's file name (the offline checker's).</param>
/// <param name="Root">The name of its prefab's root object.</param>
public sealed record UiPartSource(string Definition, string Bundle, string Root);

/// <summary>A part the native lobby needs from a prefab.</summary>
/// <param name="Definition">The menu definition it comes from.</param>
/// <param name="Path">Its path under the prefab's root ("" for the root itself).</param>
/// <param name="Components">Script classes it must carry.</param>
public sealed record UiPart(string Definition, string Path, IReadOnlyList<string> Components);

/// <summary>
/// <c>Hooks/ui_parts.txt</c>: every prefab part the native lobby clones or finds by path. Read both offline
/// (<c>tools/checks/uipartscheck.py</c>, against the game's bundles) and in the game, when a source prefab has loaded. A part
/// that's gone turns off only the native lobby, never online: that's why this isn't one of the hook lists, whose rows turn
/// online off. Format, one row a line, '#' comments:
/// <code>
/// @Glossary | ui-menu_glossary | GlossaryMenu
/// Glossary | ScaledContainer/Header/Title | TextMeshProUGUI, Localize
/// </code>
/// </summary>
public sealed class UiPartsList
{
    private UiPartsList(IReadOnlyList<UiPartSource> sources, IReadOnlyList<UiPart> parts)
    {
        Sources = sources;
        Parts = parts;
    }

    /// <summary>The prefabs.</summary>
    public IReadOnlyList<UiPartSource> Sources { get; }

    /// <summary>The parts.</summary>
    public IReadOnlyList<UiPart> Parts { get; }

    /// <summary>The list in <paramref name="text"/>, or null with why (the first bad line, by number).</summary>
    public static UiPartsList? Parse(string text, out string problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sources = new List<UiPartSource>();
        var parts = new List<UiPart>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        int number = 0;
        foreach (string raw in text.Split('\n'))
        {
            number++;
            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            string line = (hash < 0 ? raw : raw[..hash]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] cells = line.Split('|');
            if (cells.Length != 3)
            {
                problem = $"line {number}: expected three cells, got {cells.Length}";
                return null;
            }

            string first = cells[0].Trim();
            if (first.StartsWith('@'))
            {
                string name = first[1..];
                if (name.Length == 0 || cells[1].Trim().Length == 0 || cells[2].Trim().Length == 0 || !known.Add(name))
                {
                    problem = $"line {number}: a source needs a new name, a bundle and a root";
                    return null;
                }

                sources.Add(new UiPartSource(name, cells[1].Trim(), cells[2].Trim()));
                continue;
            }

            if (!known.Contains(first))
            {
                problem = $"line {number}: the source \"{first}\" isn't declared above it";
                return null;
            }

            var components = new List<string>();
            foreach (string cell in cells[2].Split(','))
            {
                if (cell.Trim().Length > 0)
                {
                    components.Add(cell.Trim());
                }
            }

            parts.Add(new UiPart(first, cells[1].Trim(), components));
        }

        problem = string.Empty;
        return new UiPartsList(sources, parts);
    }

    /// <summary>The parts of one source.</summary>
    public IEnumerable<UiPart> PartsOf(string definition)
    {
        foreach (UiPart part in Parts)
        {
            if (part.Definition == definition)
            {
                yield return part;
            }
        }
    }
}
