using System;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Protocol;

/// <summary>
/// One shared moment as the host sends it (<see cref="AchievementUnlockedMessage"/>).
/// </summary>
/// <param name="Name">The game's name for the achievement (<c>EPlatformAchievement.mName</c>).</param>
/// <param name="Match">
/// The number the host's game loaded the match it came from as (0 when it came from no match): a friend unlocks a match
/// moment only for a match its own game played under that number.
/// </param>
/// <param name="FromRun">
/// True for a moment of the run (its win, a boss win): a friend unlocks it only while it holds the host's co-op run.
/// </param>
public readonly record struct AchievementMoment(string Name, int Match, bool FromRun);

/// <summary>
/// Host to friend (channel 1): the host earned an achievement, by the game's own name for
/// it (<c>EPlatformAchievement.mName</c>, which is a class, not an enum), with the play it came from (protocol 9). The
/// friend's game unlocks the same one on the friend's own Steam account if its own game shared that play.
/// </summary>
/// <remarks>
/// Layout: the name (a short string of at most <see cref="MaxNameBytes"/> UTF-8 bytes), the match number (32 bits, at most
/// <see cref="int.MaxValue"/>), a flags byte (bit 0: from the run; the other bits zero).
/// </remarks>
public static class AchievementUnlockedMessage
{
    /// <summary>Longest name, in UTF-8 bytes (the game's are short identifiers).</summary>
    public const int MaxNameBytes = 64;

    private const byte FromRunFlag = 0x01;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, in AchievementMoment moment)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(moment.Name);
        if (moment.Match < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moment), moment.Match, "A match number is never negative.");
        }

        writer.WriteShortString(moment.Name, MaxNameBytes);
        writer.WriteUInt32((uint)moment.Match);
        writer.WriteByte(moment.FromRun ? FromRunFlag : (byte)0);
    }

    /// <summary>
    /// Reads the payload. False for malformed data: a name that can't be an achievement's, a match number past
    /// <see cref="int.MaxValue"/>, or a flag this version doesn't know.
    /// </summary>
    public static bool TryRead(ref BitReader reader, out AchievementMoment moment)
    {
        string name = reader.ReadShortString(MaxNameBytes);
        uint match = reader.ReadUInt32();
        byte flags = reader.ReadByte();
        moment = default;
        if (reader.IsOverrun || !IsPlausibleName(name) || match > int.MaxValue || (flags & ~FromRunFlag) != 0)
        {
            return false;
        }

        moment = new AchievementMoment(name, (int)match, (flags & FromRunFlag) != 0);
        return true;
    }

    /// <summary>
    /// Non-empty printable ASCII with no leading or trailing space. Anything else is a corrupt or hostile message; the
    /// friend still only unlocks a name the game's own list has.
    /// </summary>
    public static bool IsPlausibleName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name[0] == ' ' || name[^1] == ' ')
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
