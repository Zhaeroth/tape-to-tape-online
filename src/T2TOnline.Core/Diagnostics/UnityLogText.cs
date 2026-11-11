using System;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Unity's own log (<c>Player.log</c>, <c>Player-prev.log</c>) for a bug report: the graphics card is reduced to its maker
/// and driver (a rare model narrows a person down; the maker and driver are what a rendering problem needs). Unity
/// writes it in a block at start-up (<c>Renderer: NVIDIA GeForce RTX 4070 (ID=0x2786)</c>, <c>Vendor:</c>,
/// <c>VRAM: 12011 MB</c>, <c>Driver:</c>): the renderer keeps its maker's word, the memory size goes, and a model name
/// anywhere else (a Vulkan or crash line) is replaced. Folders are the scrubber's (<see cref="LogScrub"/>).
/// </summary>
public static class UnityLogText
{
    /// <summary>What a graphics card's model becomes.</summary>
    public const string ModelLabel = "<graphics card model>";

    // The makers' words a renderer's name starts with, as Unity writes them.
    private static readonly string[] Makers = { "NVIDIA", "AMD", "ATI", "Intel", "Microsoft", "Qualcomm", "VMware", "Parallels", "Apple", "ARM", "Mali", "Adreno" };

    // Words that begin a graphics card's model name: the model runs from here to the end of the line or a '('.
    private static readonly string[] Families =
    {
        "GeForce", "Quadro", "TITAN", "Tesla", "RTX", "GTX", "Radeon", "FirePro", "Arc(TM)", "Arc A", "Arc B", "Iris", "UHD Graphics", "HD Graphics", "Xe Graphics",
    };

    /// <summary>
    /// <paramref name="line"/> with the graphics card reduced (see the class); <paramref name="reduced"/> says whether it
    /// changed. The same instance when nothing changes. Never throws.
    /// </summary>
    public static string Reduce(string line, out bool reduced)
    {
        reduced = false;
        if (string.IsNullOrEmpty(line))
        {
            return line ?? string.Empty;
        }

        string trimmed = line.TrimStart();
        string indent = line[..(line.Length - trimmed.Length)];
        if (trimmed.StartsWith("Renderer:", StringComparison.Ordinal))
        {
            string value = trimmed["Renderer:".Length..].Trim();
            string maker = MakerOf(value);
            reduced = true;
            return $"{indent}Renderer: {(maker.Length > 0 ? maker + " " : string.Empty)}{ModelLabel}";
        }

        if (trimmed.StartsWith("VRAM:", StringComparison.Ordinal))
        {
            reduced = true;
            return $"{indent}VRAM:     (left out of the bug report)";
        }

        // Elsewhere only on a line that names a maker (a game line that happens to hold a family's word stays as it is).
        if (!NamesMaker(line))
        {
            return line;
        }

        foreach (string family in Families)
        {
            int at = line.IndexOf(family, StringComparison.Ordinal);
            if (at < 0 || (at > 0 && char.IsLetterOrDigit(line[at - 1])))
            {
                continue;
            }

            int end = line.IndexOf('(', at);
            if (family == "Arc(TM)")
            {
                end = line.IndexOf('(', at + family.Length);
            }

            end = end < 0 ? line.Length : end;
            while (end > at && line[end - 1] == ' ')
            {
                end--;
            }

            reduced = true;
            return string.Concat(line.AsSpan(0, at), ModelLabel, line.AsSpan(end));
        }

        return line;
    }

    /// <summary>True when <paramref name="line"/> holds one of the makers' words as a word of its own.</summary>
    private static bool NamesMaker(string line)
    {
        foreach (string maker in Makers)
        {
            int at = line.IndexOf(maker, StringComparison.Ordinal);
            while (at >= 0)
            {
                int end = at + maker.Length;
                if ((at == 0 || !char.IsLetterOrDigit(line[at - 1])) && (end == line.Length || !char.IsLetter(line[end])))
                {
                    return true;
                }

                at = line.IndexOf(maker, end, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>The maker's word a renderer's name starts with, or "" when it's none of the known ones.</summary>
    private static string MakerOf(string renderer)
    {
        foreach (string maker in Makers)
        {
            if (renderer.StartsWith(maker, StringComparison.OrdinalIgnoreCase) &&
                (renderer.Length == maker.Length || !char.IsLetterOrDigit(renderer[maker.Length])))
            {
                return maker;
            }
        }

        return string.Empty;
    }
}
