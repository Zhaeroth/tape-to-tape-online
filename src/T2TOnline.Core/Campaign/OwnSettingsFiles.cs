using System;
using System.Collections.Generic;
using System.IO;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The save files a friend's own Settings menu may write while they follow a host: <c>config.es3</c> (the game's <c>SystemSettings</c>: graphics, window, volumes,
/// language, controller icons; never cloud-saved) and <c>GameplaySettings.es3</c> (the <c>GameplaySettings</c> options: sticky shooting, simple swap, rumble,
/// camera, HUD, tooltips...; the exhibition setup menus keep their match settings in it under other keys). Neither holds run data, profile progress or stats, and
/// the host's session reads neither from a friend: the host's own settings rule its game, a friend's apply to their own games. The save guard lets these through
/// (with Easy Save's copies of them) only while that menu is open and a moment after; everything else stays refused.
/// </summary>
public static class OwnSettingsFiles
{
    /// <summary>The files, by name.</summary>
    public static IReadOnlyList<string> Names { get; } = new[] { "config.es3", "GameplaySettings.es3" };

    /// <summary>True when a write naming <paramref name="path"/> (a name or a path) is of one of <see cref="Names"/> or one of Easy Save's copies of it.</summary>
    public static bool Allows(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string file;
        try
        {
            file = Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (string name in Names)
        {
            if (EasySaveCopies.IsWriteOf(file, name))
            {
                return true;
            }
        }

        return false;
    }
}
