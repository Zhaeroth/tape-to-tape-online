using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace T2TOnline.Core.Campaign;

/// <summary>What <see cref="ComicMarkWatch.Judge"/> decided about the golfer comic's marks at a session's end.</summary>
/// <param name="Owed">The marks in memory include strips the profile on disk doesn't have: they are written now.</param>
/// <param name="Why">When they aren't owed or can't be written: why (empty when <paramref name="Owed"/>).</param>
/// <param name="NewStrips">The strips (1 to 31) marked seen since the profile was last on disk, in order.</param>
public readonly record struct ComicMarkVerdict(bool Owed, string Why, IReadOnlyList<int> NewStrips);

/// <summary>
/// The golfer comic's marks on a friend's profile (D42's amendment). The game marks the strips a
/// player has seen in memory (<c>ProfileData.golferComicStripsSeen</c>, one bit a strip) and saves the profile inline, which
/// the save guard refuses while the friend follows a host's campaign. They reach the disk with shared progress's next
/// allowed save; with none (ShareProgress off, or a session that ended mid-outro with nothing left to apply) they stayed in
/// memory until the game's next own save, and a hard crash before that showed the reveal again. This watches what the profile
/// on disk has (the marks when following began, then each allowed save's) against memory, so the session's end can write them
/// once, through the same allowance, and only them being new. Marks are a player's own viewing, not the host's credit: the
/// setting that stops shared progress doesn't stop them. Game-independent.
/// </summary>
public sealed class ComicMarkWatch
{
    /// <summary>Most strips a profile records (one bit each of a 32-bit mask, the sign bit left).</summary>
    public const int MaxStrips = 31;

    private int _onDisk = -1;

    /// <summary>True once the marks on disk are known (<see cref="OnDisk"/>).</summary>
    public bool Known => _onDisk >= 0;

    /// <summary>The marks the profile on disk has, as far as this knows (-1: not known yet).</summary>
    public int Marks => _onDisk;

    /// <summary>The profile on disk has <paramref name="marks"/>: the profile as following began (it was loaded, and nothing was refused yet), or as an allowed save just wrote it.</summary>
    public void OnDisk(int marks) => _onDisk = marks < 0 ? 0 : marks;

    /// <summary>
    /// Whether the marks in memory (<paramref name="inMemory"/>) are owed to the disk. Not owed when the disk's marks aren't
    /// known, when memory has no strip the disk lacks (nothing new, or memory has fewer: a profile reloaded meanwhile), or
    /// when the profile can't be written now (<paramref name="profileLoaded"/>: its load finished; <paramref name="savable"/>:
    /// the game saves it at all, not a failed load or cheats, and no restore waits for a restart).
    /// </summary>
    public ComicMarkVerdict Judge(int inMemory, bool profileLoaded, bool savable)
    {
        if (!Known)
        {
            return new ComicMarkVerdict(false, "this profile's marks on disk weren't read when following began", Array.Empty<int>());
        }

        IReadOnlyList<int> added = Strips(inMemory & ~_onDisk);
        if (added.Count == 0)
        {
            return new ComicMarkVerdict(false, string.Empty, added);
        }

        if (!profileLoaded)
        {
            return new ComicMarkVerdict(false, "this game's profile hasn't loaded", added);
        }

        return savable ? new ComicMarkVerdict(true, string.Empty, added) : new ComicMarkVerdict(false, "this game won't save the profile now", added);
    }

    /// <summary>The strips (1 to <see cref="MaxStrips"/>) whose bit is set in <paramref name="marks"/>, in order.</summary>
    public static IReadOnlyList<int> Strips(int marks)
    {
        var list = new List<int>();
        for (int i = 0; i < MaxStrips; i++)
        {
            if ((marks & (1 << i)) != 0)
            {
                list.Add(i + 1);
            }
        }

        return list;
    }

    /// <summary>The strips in <paramref name="marks"/> in words for a log line ("1, 2, 5", or "none").</summary>
    public static string Describe(int marks)
    {
        IReadOnlyList<int> strips = Strips(marks);
        return strips.Count == 0 ? "none" : string.Join(", ", strips.Select(s => s.ToString(CultureInfo.InvariantCulture)));
    }
}
