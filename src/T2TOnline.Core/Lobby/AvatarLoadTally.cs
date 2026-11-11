using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace T2TOnline.Core.Lobby;

/// <summary>
/// The avatars loaded in one burst (an invite list or a Join list opening), counted for one log line instead of one line per
/// avatar: whose avatar it was is nobody's business in a log (D127), a bystander's least of all.
/// <c>loaded 5 Medium avatars (64x64) and 1 Large avatar (184x184); 8 in the cache.</c> Main thread only.
/// </summary>
public sealed class AvatarLoadTally
{
    private readonly List<(string Size, int Width, int Height, int Count)> _groups = new();

    /// <summary>Avatars counted since the last <see cref="Take"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Counts one avatar of <paramref name="size"/> (Steam's name for it: Small, Medium, Large) loaded at its pixel size.</summary>
    public void Add(string size, int width, int height)
    {
        Count++;
        for (int i = 0; i < _groups.Count; i++)
        {
            (string s, int w, int h, int n) = _groups[i];
            if (s == size && w == width && h == height)
            {
                _groups[i] = (s, w, h, n + 1);
                return;
            }
        }

        if (_groups.Count < 8)
        {
            _groups.Add((size, width, height, 1));
        }
    }

    /// <summary>
    /// The line's words for what was counted (<c>loaded 5 Medium avatars (64x64); 8 in the cache.</c>), with
    /// <paramref name="inCache"/> textures kept now, and starts counting again. Empty when nothing was counted.
    /// </summary>
    public string Take(int inCache)
    {
        if (Count == 0)
        {
            return string.Empty;
        }

        var line = new StringBuilder("loaded ");
        int listed = 0;
        for (int i = 0; i < _groups.Count; i++)
        {
            (string size, int width, int height, int n) = _groups[i];
            line.Append(i == 0 ? string.Empty : i == _groups.Count - 1 ? " and " : ", ")
                .Append(string.Create(CultureInfo.InvariantCulture, $"{n} {size} avatar{(n == 1 ? string.Empty : "s")} ({width}x{height})"));
            listed += n;
        }

        if (listed < Count)
        {
            line.Append(string.Create(CultureInfo.InvariantCulture, $" and {Count - listed} more"));
        }

        line.Append(string.Create(CultureInfo.InvariantCulture, $"; {inCache} in the cache."));
        _groups.Clear();
        Count = 0;
        return line.ToString();
    }
}
