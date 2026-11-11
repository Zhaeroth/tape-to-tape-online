using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace T2TOnline.Core.Campaign;

/// <summary>A history logo file in the mod's store: its name, size and when it was last written or used.</summary>
/// <param name="FileName">The file's name (<see cref="HistoryLogoNames.FileName"/>).</param>
/// <param name="Bytes">Its size.</param>
/// <param name="WrittenUtc">When it was last stored or stored again.</param>
public readonly record struct HistoryLogoFile(string FileName, long Bytes, DateTime WrittenUtc);

/// <summary>
/// A host's custom logo in a friend's run history (D55 as amended): the game's history entry names
/// its team's logo, and the only lookup (<c>RunHistoryDeserializer.ResolveLogo</c>) finds a host's <c>custom_*.png</c> only
/// while the session lasts. So a friend keeps the PNG in the mod's own data folder, under a content name made from its
/// bytes, and the entry names that: <c>t2t_hostlogo_&lt;16 hex&gt;.png</c>, a name nothing of the game's or the player's
/// can have, never a path. The store is capped in count and bytes, the least recently stored going first. Names only;
/// game-independent.
/// </summary>
public static class HistoryLogoNames
{
    /// <summary>The start of a content name.</summary>
    public const string Prefix = "t2t_hostlogo_";

    /// <summary>The store's folder name, in the mod's data folder.</summary>
    public const string FolderName = "history-logos";

    /// <summary>Most logos kept.</summary>
    public const int MaxFiles = 32;

    /// <summary>Most bytes kept, all logos together.</summary>
    public const long MaxTotalBytes = 48L * 1024 * 1024;

    private const int KeyLength = 16;
    private const string Extension = ".png";

    /// <summary>A logo's key: the first 16 hex digits (lowercase) of its bytes' SHA-256.</summary>
    public static string Key(ReadOnlySpan<byte> png) => Convert.ToHexString(SHA256.HashData(png), 0, KeyLength / 2).ToLowerInvariant();

    /// <summary>The name a history entry carries for the logo with <paramref name="key"/>.</summary>
    public static string ContentName(string key) => IsKey(key) ? Prefix + key + Extension : throw new ArgumentException("A logo's key.", nameof(key));

    /// <summary>The store's file for the logo with <paramref name="key"/>.</summary>
    public static string FileName(string key) => IsKey(key) ? key + Extension : throw new ArgumentException("A logo's key.", nameof(key));

    /// <summary>The key a history entry's logo name stands for. False for any other name (the game's, a player's, anything odd).</summary>
    public static bool TryParseContentName(string? name, out string key)
    {
        key = string.Empty;
        if (name == null || name.Length != Prefix.Length + KeyLength + Extension.Length ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = name.Substring(Prefix.Length, KeyLength);
        if (!IsKey(candidate))
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>The key of a store file's name. False for any other file.</summary>
    public static bool TryParseFileName(string? name, out string key)
    {
        key = string.Empty;
        if (name == null || name.Length != KeyLength + Extension.Length || !name.EndsWith(Extension, StringComparison.Ordinal) || !IsKey(name[..KeyLength]))
        {
            return false;
        }

        key = name[..KeyLength];
        return true;
    }

    /// <summary>
    /// The store's files to remove so that at most <see cref="MaxFiles"/> and <see cref="MaxTotalBytes"/> stay: the least
    /// recently written first, never <paramref name="keepKey"/>'s (the one just stored). Files that aren't the store's are
    /// never in it.
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<HistoryLogoFile> files, string? keepKey)
    {
        ArgumentNullException.ThrowIfNull(files);
        List<HistoryLogoFile> logos = files.Where(f => TryParseFileName(f.FileName, out _)).OrderByDescending(f => f.WrittenUtc).ThenBy(f => f.FileName, StringComparer.Ordinal).ToList();
        string? keep = keepKey != null && IsKey(keepKey) ? FileName(keepKey) : null;
        var expired = new List<string>();
        int count = logos.Count;
        long total = logos.Sum(f => Math.Max(0, f.Bytes));
        for (int i = logos.Count - 1; i >= 0 && (count > MaxFiles || total > MaxTotalBytes); i--)
        {
            if (string.Equals(logos[i].FileName, keep, StringComparison.Ordinal))
            {
                continue;
            }

            expired.Add(logos[i].FileName);
            count--;
            total -= Math.Max(0, logos[i].Bytes);
        }

        return expired;
    }

    private static bool IsKey(string key) => key.Length == KeyLength && key.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
