using System;
using System.Collections.Generic;
using System.Text;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// One row of the host's unlockables (the game's <c>UnlockableMenu</c>) as the host's profile has it: the unlock's id (the
/// game's <c>IUnlockData.RewardID</c>), the row's category (its <c>UnlockCategory</c>), and the three values that come from
/// the profile (<c>UnlockData.IsUnlocked</c>, <c>UnlockProgress</c>, <c>UnlockTotal</c>). Everything else on the row (texts,
/// icon) is the asset's, made by each PC's own game in its own language.
/// </summary>
/// <param name="Key">The unlock's id: 1 to <see cref="UnlockStatesData.MaxKeyBytes"/> UTF-8 bytes, no control characters.</param>
/// <param name="Category">The row's category (0 to <see cref="UnlockStatesData.MaxCategory"/>).</param>
/// <param name="Unlocked">The row shows as unlocked.</param>
/// <param name="Progress">Its bar's progress (0 to <see cref="UnlockStatesData.MaxCount"/>).</param>
/// <param name="Total">Its bar's total (0 to <see cref="UnlockStatesData.MaxCount"/>).</param>
public sealed record UnlockRowState(string Key, int Category, bool Unlocked, int Progress, int Total);

/// <summary>
/// One row of the host's quests tab (a benchwarmer's quest): the benchwarmer it's for, and the host's state of it. A secret
/// row (a benchwarmer the host hasn't unlocked) shows "???" on the host's screen; a friend shows the same.
/// </summary>
/// <param name="Benchwarmer">The benchwarmer's id (its <c>ForwardData.id</c>): 1 to <see cref="UnlockStatesData.MaxKeyBytes"/> UTF-8 bytes.</param>
/// <param name="Completed">The quest is completed (the row shows as unlocked).</param>
/// <param name="Secret">The row is the game's secret row (never with <paramref name="Completed"/>).</param>
/// <param name="Progress">Its bar's progress (0 to <see cref="UnlockStatesData.MaxCount"/>).</param>
/// <param name="Total">Its bar's total (0 to <see cref="UnlockStatesData.MaxCount"/>).</param>
public sealed record QuestRowState(string Benchwarmer, bool Completed, bool Secret, int Progress, int Total);

/// <summary>
/// The host's unlockables screen as its profile fills it, sent as a <see cref="MenuAttachmentKind.UnlockStates"/> attachment
/// of the menu (format <see cref="MenuAttachment.UnlockStatesFormat"/>): every unlock row's state, every quest row's, and the
/// host's tabs in order, each by what it lists (<see cref="TabCodes"/>). A friend's game builds the menu itself and takes
/// these states in place of its own profile's, so it shows the host's progress in its own language. Rows are named by id,
/// never by place (rows sort by their translated title, and a tab is there only when it has rows). Checked when it's written
/// and when it's read; immutable.
/// <code>
/// varuint rows · each: key · u8 category · u8 flags (bit 0 unlocked) · varuint progress · varuint total
/// varuint quests · each: benchwarmer id · u8 flags (bit 0 completed, bit 1 secret) · varuint progress · varuint total
/// varuint tabs · each: u8 tab code
/// </code>
/// An id is a varuint byte length (1 to <see cref="MaxKeyBytes"/>) and that many bytes of strict UTF-8.
/// </summary>
public sealed class UnlockStatesData
{
    /// <summary>Most unlock rows (the game has a few hundred).</summary>
    public const int MaxRows = 960;

    /// <summary>Most quest rows (the game has nine benchwarmers).</summary>
    public const int MaxQuests = 63;

    /// <summary>Longest id, in UTF-8 bytes.</summary>
    public const int MaxKeyBytes = 64;

    /// <summary>Highest category (the game's ten, the quests' 10, and room for more below <see cref="AllTab"/>).</summary>
    public const int MaxCategory = 14;

    /// <summary>The category of a quest row (the game's quests tab sets it; the enum stops at 9).</summary>
    public const int QuestCategory = 10;

    /// <summary>Highest progress or total.</summary>
    public const int MaxCount = 1_000_000;

    /// <summary>Most tabs.</summary>
    public const int MaxTabs = 16;

    /// <summary>The tab code of the "all" tab (its rows have more than one category).</summary>
    public const int AllTab = 15;

    /// <summary>Highest tab code: a category (0 to <see cref="MaxCategory"/>), or <see cref="AllTab"/>.</summary>
    public const int MaxTabCode = AllTab;

    /// <summary>The cursor key of quest row 0 (a row's key is its place in <see cref="Rows"/>, a quest row's this plus its place in <see cref="Quests"/>).</summary>
    public const int QuestKeyBase = MaxRows;

    /// <summary>The highest cursor key (<see cref="MenuTarget.MaxIndex"/>).</summary>
    public const int MaxKey = QuestKeyBase + MaxQuests;

    private const byte UnlockedFlag = 1;
    private const byte CompletedFlag = 1;
    private const byte SecretFlag = 2;

    // Strict UTF-8: an id that isn't is refused, never patched up.
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Every unlock row the host's menu made, in the order it made them.</summary>
    public IReadOnlyList<UnlockRowState> Rows { get; init; } = Array.Empty<UnlockRowState>();

    /// <summary>Every quest row, in the quests tab's order.</summary>
    public IReadOnlyList<QuestRowState> Quests { get; init; } = Array.Empty<QuestRowState>();

    /// <summary>
    /// The host's tabs in order, each by what it lists: the category its rows share (0 to <see cref="MaxCategory"/>), or
    /// <see cref="AllTab"/> for the tab with every category (<see cref="TabCode"/>).
    /// </summary>
    public IReadOnlyList<int> TabCodes { get; init; } = Array.Empty<int>();

    /// <summary>The cursor key of unlock row <paramref name="row"/> (its place in <see cref="Rows"/>).</summary>
    public static int RowKey(int row) => row;

    /// <summary>The cursor key of quest row <paramref name="quest"/> (its place in <see cref="Quests"/>).</summary>
    public static int QuestKey(int quest) => QuestKeyBase + quest;

    /// <summary>True for an id that can be sent: 1 to <see cref="MaxKeyBytes"/> bytes of UTF-8, no control characters.</summary>
    public static bool IsKey(string? key) => CheckKey(key, "id") == null;

    /// <summary>What a cursor key names: an unlock row (<paramref name="quest"/> false) or a quest row, and its place. False for a key out of range. Never throws.</summary>
    public static bool TryKey(int key, out bool quest, out int index)
    {
        quest = key >= QuestKeyBase;
        index = quest ? key - QuestKeyBase : key;
        return key is >= 0 and <= MaxKey;
    }

    /// <summary>
    /// A tab's code from its rows' categories: the one category they all share, <see cref="AllTab"/> when they differ, or -1
    /// for an empty tab or a category out of range (never followed).
    /// </summary>
    public static int TabCode(IEnumerable<int> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        int code = -1;
        foreach (int category in categories)
        {
            if (code == -1)
            {
                code = category;
            }
            else if (category != code)
            {
                return AllTab;
            }
        }

        return code is >= -1 and <= MaxCategory ? code : -1;
    }

    /// <summary>For logs: "412 unlock row(s) (237 unlocked), 9 quest row(s) (2 completed, 3 secret), 11 tab(s)".</summary>
    public override string ToString()
    {
        int unlocked = 0, completed = 0, secret = 0;
        foreach (UnlockRowState row in Rows)
        {
            unlocked += row.Unlocked ? 1 : 0;
        }

        foreach (QuestRowState quest in Quests)
        {
            completed += quest.Completed ? 1 : 0;
            secret += quest.Secret ? 1 : 0;
        }

        return $"{Rows.Count} unlock row(s) ({unlocked} unlocked), {Quests.Count} quest row(s) ({completed} completed, {secret} secret), {TabCodes.Count} tab(s)";
    }

    /// <summary>Why it can't be sent, or null when it can.</summary>
    public string? Problem()
    {
        if (Rows == null || Quests == null || TabCodes == null)
        {
            return "a list is missing";
        }

        if (Rows.Count > MaxRows || Quests.Count > MaxQuests || TabCodes.Count > MaxTabs)
        {
            return $"{Rows.Count} rows, {Quests.Count} quest rows and {TabCodes.Count} tabs (at most {MaxRows}, {MaxQuests} and {MaxTabs})";
        }

        var rows = new HashSet<(string, int)>();
        for (int i = 0; i < Rows.Count; i++)
        {
            UnlockRowState? row = Rows[i];
            if (row == null)
            {
                return $"row {i} is missing";
            }

            string? problem = CheckKey(row.Key, $"row {i}'s id") ?? CheckCounts(row.Progress, row.Total, $"row {i}");
            if (problem != null)
            {
                return problem;
            }

            if (row.Category is < 0 or > MaxCategory)
            {
                return $"row {i}'s category {row.Category} is out of range";
            }

            if (!rows.Add((row.Key, row.Category)))
            {
                return $"row {i} repeats an id in its category";
            }
        }

        var quests = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Quests.Count; i++)
        {
            QuestRowState? quest = Quests[i];
            if (quest == null)
            {
                return $"quest row {i} is missing";
            }

            string? problem = CheckKey(quest.Benchwarmer, $"quest row {i}'s benchwarmer") ?? CheckCounts(quest.Progress, quest.Total, $"quest row {i}");
            if (problem != null)
            {
                return problem;
            }

            if (quest.Secret && quest.Completed)
            {
                return $"quest row {i} is both secret and completed";
            }

            if (!quests.Add(quest.Benchwarmer))
            {
                return $"quest row {i} repeats a benchwarmer";
            }
        }

        foreach (int code in TabCodes)
        {
            if (code is < 0 or > MaxTabCode)
            {
                return $"tab code {code} is out of range";
            }
        }

        return null;
    }

    /// <summary>Writes the states. Throws <see cref="ArgumentException"/> (saying what) for anything out of range.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string? problem = Problem();
        if (problem != null)
        {
            throw new ArgumentException(problem, nameof(writer));
        }

        writer.WriteVarUInt((ulong)Rows.Count);
        foreach (UnlockRowState row in Rows)
        {
            Key(writer, row.Key);
            writer.WriteByte((byte)row.Category);
            writer.WriteByte(row.Unlocked ? UnlockedFlag : (byte)0);
            writer.WriteVarUInt((ulong)row.Progress);
            writer.WriteVarUInt((ulong)row.Total);
        }

        writer.WriteVarUInt((ulong)Quests.Count);
        foreach (QuestRowState quest in Quests)
        {
            Key(writer, quest.Benchwarmer);
            writer.WriteByte((byte)((quest.Completed ? CompletedFlag : 0) | (quest.Secret ? SecretFlag : 0)));
            writer.WriteVarUInt((ulong)quest.Progress);
            writer.WriteVarUInt((ulong)quest.Total);
        }

        writer.WriteVarUInt((ulong)TabCodes.Count);
        foreach (int code in TabCodes)
        {
            writer.WriteByte((byte)code);
        }
    }

    /// <summary>Reads the states. False for anything malformed or out of range (unknown flag bits included). Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out UnlockStatesData? states)
    {
        states = null;
        bool ok = true;
        int count = EventWire.Count(ref reader, MaxRows, ref ok);
        if (!ok)
        {
            return false;
        }

        var rows = new List<UnlockRowState>(count);
        for (int i = 0; i < count; i++)
        {
            string? key = ReadKey(ref reader);
            int category = reader.ReadByte();
            byte flags = reader.ReadByte();
            int progress = EventWire.Count(ref reader, MaxCount, ref ok);
            int total = EventWire.Count(ref reader, MaxCount, ref ok);
            if (key == null || !ok || reader.IsOverrun || (flags & ~UnlockedFlag) != 0)
            {
                return false;
            }

            rows.Add(new UnlockRowState(key, category, (flags & UnlockedFlag) != 0, progress, total));
        }

        count = EventWire.Count(ref reader, MaxQuests, ref ok);
        if (!ok)
        {
            return false;
        }

        var quests = new List<QuestRowState>(count);
        for (int i = 0; i < count; i++)
        {
            string? id = ReadKey(ref reader);
            byte flags = reader.ReadByte();
            int progress = EventWire.Count(ref reader, MaxCount, ref ok);
            int total = EventWire.Count(ref reader, MaxCount, ref ok);
            if (id == null || !ok || reader.IsOverrun || (flags & ~(CompletedFlag | SecretFlag)) != 0)
            {
                return false;
            }

            quests.Add(new QuestRowState(id, (flags & CompletedFlag) != 0, (flags & SecretFlag) != 0, progress, total));
        }

        count = EventWire.Count(ref reader, MaxTabs, ref ok);
        if (!ok)
        {
            return false;
        }

        var tabs = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            tabs.Add(reader.ReadByte());
        }

        var read = new UnlockStatesData { Rows = rows, Quests = quests, TabCodes = tabs };
        if (reader.IsOverrun || read.Problem() != null)
        {
            return false;
        }

        states = read;
        return true;
    }

    private static string? CheckKey(string? key, string what)
    {
        if (string.IsNullOrEmpty(key))
        {
            return $"{what} is missing";
        }

        foreach (char c in key)
        {
            if (char.IsControl(c))
            {
                return $"{what} has a control character";
            }
        }

        int bytes;
        try
        {
            bytes = Strict.GetByteCount(key);
        }
        catch (ArgumentException)
        {
            return $"{what} isn't valid text";
        }

        return bytes > MaxKeyBytes ? $"{what} is over {MaxKeyBytes} bytes" : null;
    }

    private static string? CheckCounts(int progress, int total, string what) =>
        progress is < 0 or > MaxCount || total is < 0 or > MaxCount ? $"{what}'s progress {progress} of {total} is out of range" : null;

    private static void Key(BitWriter writer, string key)
    {
        byte[] bytes = Strict.GetBytes(key);
        writer.WriteVarUInt((ulong)bytes.Length);
        writer.WriteBytes(bytes);
    }

    /// <summary>An id (1 to <see cref="MaxKeyBytes"/> bytes of strict UTF-8, no control characters), or null.</summary>
    private static string? ReadKey(ref BitReader reader)
    {
        ulong length = reader.ReadVarUInt();
        if (reader.IsOverrun || length is 0 or > MaxKeyBytes || (long)length * 8 > reader.RemainingBits)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[(int)length];
        reader.ReadBytes(bytes);
        if (reader.IsOverrun)
        {
            return null;
        }

        string key;
        try
        {
            key = Strict.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return CheckKey(key, "id") == null ? key : null;
    }
}
