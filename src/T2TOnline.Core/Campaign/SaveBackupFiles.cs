using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace T2TOnline.Core.Campaign;

/// <summary>One save file at one moment: its name (no folder), its size and its SHA-256 (uppercase hex).</summary>
/// <param name="Name">The file's name, e.g. <c>profileV2.es3</c>.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Sha256">The SHA-256 of its bytes, as <c>Get-FileHash</c> prints it.</param>
public sealed record SaveFileHash(string Name, long Bytes, string Sha256)
{
    /// <summary>The SHA-256 of <paramref name="bytes"/>, as uppercase hex.</summary>
    public static string HashOf(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The entry for a file named <paramref name="name"/> holding <paramref name="bytes"/>.</summary>
    public static SaveFileHash Of(string name, ReadOnlySpan<byte> bytes) => new(name, bytes.Length, HashOf(bytes));
}

/// <summary>
/// The game's play-time counter in its profile save: <c>"TttActiveSeconds"</c> in <see cref="ProfileFile"/>, which the game
/// writes again whenever it saves the profile, with or without play since (a return to the title is enough). Two copies of
/// the profile that differ only in that number hold the same progress, so a backup of one stands for the other.
/// </summary>
public static class ProfilePlayTime
{
    /// <summary>The profile save, the one file that holds the counter.</summary>
    public const string ProfileFile = "profileV2.es3";

    private static ReadOnlySpan<byte> Key => "\"TttActiveSeconds\""u8;

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are the same bytes but for the play-time counter's value:
    /// each holds the counter's key exactly once, followed by a colon (spaces or tabs around it) and a JSON number, and
    /// every byte before that number and after it is the same in both. Anything else is false: another difference, the
    /// key missing or twice, a value that isn't a plain number (a copy the game wrote encrypted holds no key).
    /// </summary>
    public static bool SameApartFromPlayTime(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (!TryFindValue(a, out int startA, out int endA) || !TryFindValue(b, out int startB, out int endB))
        {
            return false;
        }

        return startA == startB && a[..startA].SequenceEqual(b[..startB]) && a[endA..].SequenceEqual(b[endB..]);
    }

    /// <summary>Where the counter's number starts and ends in <paramref name="bytes"/>; false when the key isn't there exactly once or no number follows it.</summary>
    private static bool TryFindValue(ReadOnlySpan<byte> bytes, out int start, out int end)
    {
        start = end = 0;
        int key = bytes.IndexOf(Key);
        if (key < 0 || bytes[(key + Key.Length)..].IndexOf(Key) >= 0)
        {
            return false;
        }

        int i = SkipBlanks(bytes, key + Key.Length);
        if (i >= bytes.Length || bytes[i] != (byte)':')
        {
            return false;
        }

        i = SkipBlanks(bytes, i + 1);
        start = i;

        // A JSON number: -? digits (. digits)? ([eE] [+-]? digits)?
        if (i < bytes.Length && bytes[i] == (byte)'-')
        {
            i++;
        }

        int digits = Digits(bytes, ref i);
        if (digits == 0)
        {
            return false;
        }

        if (i < bytes.Length && bytes[i] == (byte)'.')
        {
            i++;
            if (Digits(bytes, ref i) == 0)
            {
                return false;
            }
        }

        if (i < bytes.Length && bytes[i] is (byte)'e' or (byte)'E')
        {
            i++;
            if (i < bytes.Length && bytes[i] is (byte)'+' or (byte)'-')
            {
                i++;
            }

            if (Digits(bytes, ref i) == 0)
            {
                return false;
            }
        }

        end = i;
        return true;
    }

    private static int SkipBlanks(ReadOnlySpan<byte> bytes, int i)
    {
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t')
        {
            i++;
        }

        return i;
    }

    private static int Digits(ReadOnlySpan<byte> bytes, ref int i)
    {
        int from = i;
        while (i < bytes.Length && bytes[i] >= (byte)'0' && bytes[i] <= (byte)'9')
        {
            i++;
        }

        return i - from;
    }
}

/// <summary>How the newest backup can stand for the saves about to be backed up (<see cref="SaveBackupManifest.StandsFor"/>).</summary>
public enum BackupStandIn
{
    /// <summary>It can't: a new backup is taken.</summary>
    None,

    /// <summary>The same files, co-op slots and per-run counters: it is their backup.</summary>
    Same,

    /// <summary>
    /// The same but for the profile (<see cref="ProfilePlayTime.ProfileFile"/>): it is their backup when the two copies of it
    /// differ only in the play-time counter (<see cref="ProfilePlayTime.SameApartFromPlayTime"/>; the caller reads both).
    /// </summary>
    IfPlayTimeOnly,
}

/// <summary>
/// One co-op slot's run file in a backup (D46): its slot (1 to
/// <see cref="SaveBackupManifest.MaxCoopSlot"/>), its size and its SHA-256 (uppercase hex). The copy is
/// <c>coop\coop-&lt;slot&gt;.es3</c> in the backup's folder.
/// </summary>
/// <param name="Slot">The co-op slot.</param>
/// <param name="Bytes">The slot file's size.</param>
/// <param name="Sha256">The SHA-256 of its bytes.</param>
public sealed record CoopSlotHash(int Slot, long Bytes, string Sha256)
{
    /// <summary>The slot file's name, <c>coop-&lt;slot&gt;.es3</c>, in the mod's co-op folder and in a backup's.</summary>
    public string FileName => SaveBackupManifest.CoopSlotFileName(Slot);

    /// <summary>The entry for co-op slot <paramref name="slot"/> holding <paramref name="bytes"/>.</summary>
    public static CoopSlotHash Of(int slot, ReadOnlySpan<byte> bytes) => new(slot, bytes.Length, SaveFileHash.HashOf(bytes));

    /// <summary>The same as a file entry named <see cref="FileName"/> (to compare slots as files).</summary>
    public SaveFileHash AsFile() => new(FileName, Bytes, Sha256);
}

/// <summary>How a save file differs between two moments.</summary>
public enum SaveFileChange
{
    /// <summary>Same bytes.</summary>
    Unchanged,

    /// <summary>Different bytes.</summary>
    Changed,

    /// <summary>Only there the second time.</summary>
    Added,

    /// <summary>Only there the first time.</summary>
    Removed,
}

/// <summary>One file's difference between two moments, with its size at each (0 where it wasn't there).</summary>
/// <param name="Name">The file's name.</param>
/// <param name="Change">What happened to it.</param>
/// <param name="BytesBefore">Its size the first time.</param>
/// <param name="BytesAfter">Its size the second time.</param>
public readonly record struct SaveFileDifference(string Name, SaveFileChange Change, long BytesBefore, long BytesAfter);

/// <summary>
/// What a save backup holds (D46): why and when it was taken, every save file in it with its hash, the co-op slots
/// copied with them, and the per-run counters kept per run file (<see cref="RunScopeRestore"/>), so
/// a restore can check the copies first and the session's end can tell which of the player's files changed. Stored as
/// <c>manifest.json</c> beside the copies (the slots and the counters in a <c>coop</c> folder). The slots and the counters
/// are optional fields, written only when there are some: a backup without them is the format 0.6.x wrote, and older
/// builds read one with them (they ignore the fields). Reading one never throws: a damaged manifest is just not a
/// manifest; damaged counters' field is read as none (the saves matter more than the counters).
/// </summary>
public sealed class SaveBackupManifest
{
    /// <summary>The manifest's file name in a backup folder.</summary>
    public const string FileName = "manifest.json";

    /// <summary>The format this build writes and reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>The folder in a backup that holds its co-op slots' copies.</summary>
    public const string CoopFolder = "coop";

    /// <summary>The highest co-op slot a manifest may name (the mod has 3; a few more are read, never written).</summary>
    public const int MaxCoopSlot = 9;

    private const int MaxFiles = 4096;

    private static readonly char[] PathCharacters = { '/', '\\', ':' };

    /// <summary>A manifest of <paramref name="files"/>, taken at <paramref name="takenAtUtc"/> by <paramref name="role"/> for <paramref name="reason"/>.</summary>
    public SaveBackupManifest(string role, string reason, DateTime takenAtUtc, IEnumerable<SaveFileHash> files)
        : this(role, reason, takenAtUtc, files, Array.Empty<CoopSlotHash>())
    {
    }

    /// <summary>
    /// A manifest of <paramref name="files"/> and the co-op slots <paramref name="coopSlots"/> (each slot 1 to
    /// <see cref="MaxCoopSlot"/>, once), taken at <paramref name="takenAtUtc"/> by <paramref name="role"/> for <paramref name="reason"/>.
    /// </summary>
    public SaveBackupManifest(string role, string reason, DateTime takenAtUtc, IEnumerable<SaveFileHash> files, IEnumerable<CoopSlotHash> coopSlots)
        : this(role, reason, takenAtUtc, files, coopSlots, null)
    {
    }

    /// <summary>
    /// A manifest of <paramref name="files"/>, the co-op slots <paramref name="coopSlots"/> and the per-run counters' copy
    /// <paramref name="runScopes"/> (named <see cref="RunScopeRestore.FileName"/>; null: none), taken at
    /// <paramref name="takenAtUtc"/> by <paramref name="role"/> for <paramref name="reason"/>.
    /// </summary>
    public SaveBackupManifest(string role, string reason, DateTime takenAtUtc, IEnumerable<SaveFileHash> files, IEnumerable<CoopSlotHash> coopSlots, SaveFileHash? runScopes)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(coopSlots);
        Role = role;
        Reason = reason;
        TakenAtUtc = DateTime.SpecifyKind(takenAtUtc, DateTimeKind.Utc);
        Files = files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        CoopSlots = coopSlots.OrderBy(s => s.Slot).ToList();
        RunScopes = runScopes;
        if (CoopSlots.Any(s => !IsCoopSlot(s.Slot)) || CoopSlots.Select(s => s.Slot).Distinct().Count() != CoopSlots.Count)
        {
            throw new ArgumentException($"Co-op slots are 1 to {MaxCoopSlot}, each once.", nameof(coopSlots));
        }
    }

    /// <summary>Who took it: <c>host</c>, <c>friend</c>, <c>manual</c>, <c>restore</c>.</summary>
    public string Role { get; }

    /// <summary>Why, in words.</summary>
    public string Reason { get; }

    /// <summary>When (UTC).</summary>
    public DateTime TakenAtUtc { get; }

    /// <summary>The files, by name.</summary>
    public IReadOnlyList<SaveFileHash> Files { get; }

    /// <summary>The co-op slots copied with them, by slot (none in a backup taken before 0.6.4 or on a PC that never hosted).</summary>
    public IReadOnlyList<CoopSlotHash> CoopSlots { get; }

    /// <summary>
    /// The per-run counters kept with them (<see cref="RunScopeRestore.FileName"/> in the <see cref="CoopFolder"/>), or null
    /// for a backup without (taken before per-run counters were kept, or nothing was known).
    /// </summary>
    public SaveFileHash? RunScopes { get; }

    /// <summary>All the files' and co-op slots' bytes together.</summary>
    public long TotalBytes => Files.Sum(f => f.Bytes) + CoopSlots.Sum(s => s.Bytes);

    /// <summary>A co-op slot's file name, <c>coop-&lt;slot&gt;.es3</c>.</summary>
    public static string CoopSlotFileName(int slot) => $"coop-{slot.ToString(CultureInfo.InvariantCulture)}.es3";

    /// <summary>True for a slot number a manifest may name (1 to <see cref="MaxCoopSlot"/>).</summary>
    public static bool IsCoopSlot(int slot) => slot is >= 1 and <= MaxCoopSlot;

    /// <summary>Co-op slot <paramref name="slot"/>'s entry, or null when the backup doesn't hold that slot.</summary>
    public CoopSlotHash? CoopSlot(int slot) => CoopSlots.FirstOrDefault(s => s.Slot == slot);

    /// <summary>The manifest as indented JSON.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteString("role", Role);
            writer.WriteString("reason", Reason);
            writer.WriteString("takenAtUtc", TakenAtUtc.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteStartArray("files");
            foreach (SaveFileHash file in Files)
            {
                writer.WriteStartObject();
                writer.WriteString("name", file.Name);
                writer.WriteNumber("bytes", file.Bytes);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (CoopSlots.Count > 0)
            {
                writer.WriteStartArray("coopSlots");
                foreach (CoopSlotHash slot in CoopSlots)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("slot", slot.Slot);
                    writer.WriteNumber("bytes", slot.Bytes);
                    writer.WriteString("sha256", slot.Sha256);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (RunScopes != null)
            {
                writer.WriteStartObject("runScopes");
                writer.WriteNumber("bytes", RunScopes.Bytes);
                writer.WriteString("sha256", RunScopes.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads a manifest. False for anything that isn't one this build understands: not JSON, another format version,
    /// missing fields, a file name with a folder in it, the same file twice, or co-op slots that aren't 1 to
    /// <see cref="MaxCoopSlot"/>, each once (the slots are optional). Never throws.
    /// </summary>
    public static bool TryParse(string json, out SaveBackupManifest? manifest)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int format) || format != FormatVersion ||
                !TryGetString(root, "role", out string role) || !TryGetString(root, "reason", out string reason) ||
                !TryGetString(root, "takenAtUtc", out string taken) ||
                !DateTime.TryParse(taken, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime takenAtUtc) ||
                !root.TryGetProperty("files", out JsonElement list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxFiles)
            {
                return false;
            }

            var files = new List<SaveFileHash>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryGetString(entry, "name", out string name) || !IsPlainFileName(name) || !names.Add(name) ||
                    !entry.TryGetProperty("bytes", out JsonElement bytes) || bytes.ValueKind != JsonValueKind.Number ||
                    !bytes.TryGetInt64(out long size) || size < 0 ||
                    !TryGetString(entry, "sha256", out string sha) || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                {
                    return false;
                }

                files.Add(new SaveFileHash(name, size, sha.ToUpperInvariant()));
            }

            var slots = new List<CoopSlotHash>();
            if (root.TryGetProperty("coopSlots", out JsonElement slotList))
            {
                if (slotList.ValueKind != JsonValueKind.Array || slotList.GetArrayLength() > MaxCoopSlot)
                {
                    return false;
                }

                foreach (JsonElement entry in slotList.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object ||
                        !entry.TryGetProperty("slot", out JsonElement number) || number.ValueKind != JsonValueKind.Number ||
                        !number.TryGetInt32(out int slot) || !IsCoopSlot(slot) || slots.Any(s => s.Slot == slot) ||
                        !entry.TryGetProperty("bytes", out JsonElement bytes) || bytes.ValueKind != JsonValueKind.Number ||
                        !bytes.TryGetInt64(out long size) || size < 0 ||
                        !TryGetString(entry, "sha256", out string sha) || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                    {
                        return false;
                    }

                    slots.Add(new CoopSlotHash(slot, size, sha.ToUpperInvariant()));
                }
            }

            manifest = new SaveBackupManifest(role, reason, takenAtUtc, files, slots, ReadRunScopes(root));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string JSON parsing let through that can't be read (an escaped lone surrogate).
            return false;
        }
    }

    /// <summary>
    /// How each file differs from <paramref name="before"/> to <paramref name="after"/> (names compared ignoring case, as
    /// Windows does), in name order.
    /// </summary>
    public static IReadOnlyList<SaveFileDifference> Compare(IEnumerable<SaveFileHash> before, IEnumerable<SaveFileHash> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var first = new Dictionary<string, SaveFileHash>(StringComparer.OrdinalIgnoreCase);
        foreach (SaveFileHash file in before)
        {
            first[file.Name] = file;
        }

        var second = new Dictionary<string, SaveFileHash>(StringComparer.OrdinalIgnoreCase);
        foreach (SaveFileHash file in after)
        {
            second[file.Name] = file;
        }

        var differences = new List<SaveFileDifference>();
        foreach (string name in first.Keys.Union(second.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            bool had = first.TryGetValue(name, out SaveFileHash? was);
            bool has = second.TryGetValue(name, out SaveFileHash? now);
            SaveFileChange change = !had ? SaveFileChange.Added
                : !has ? SaveFileChange.Removed
                : string.Equals(was!.Sha256, now!.Sha256, StringComparison.OrdinalIgnoreCase) && was.Bytes == now.Bytes ? SaveFileChange.Unchanged
                : SaveFileChange.Changed;
            differences.Add(new SaveFileDifference(now?.Name ?? was!.Name, change, was?.Bytes ?? 0, now?.Bytes ?? 0));
        }

        return differences;
    }

    /// <summary>
    /// True when <paramref name="files"/> are exactly this manifest's files: the same names (ignoring case), sizes and
    /// hashes, none more and none fewer. A backup of the same saves again adds nothing.
    /// </summary>
    public bool HoldsSameFiles(IEnumerable<SaveFileHash> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Compare(Files, files).All(d => d.Change == SaveFileChange.Unchanged);
    }

    /// <summary>
    /// True when <paramref name="files"/> and <paramref name="coopSlots"/> are exactly this manifest's files and co-op slots
    /// (<see cref="HoldsSameFiles"/>, and the same slots with the same sizes and hashes): a backup of the same saves and
    /// slots again adds nothing.
    /// </summary>
    public bool HoldsSame(IEnumerable<SaveFileHash> files, IEnumerable<CoopSlotHash> coopSlots)
    {
        ArgumentNullException.ThrowIfNull(coopSlots);
        return HoldsSameFiles(files) && Compare(CoopSlots.Select(s => s.AsFile()), coopSlots.Select(s => s.AsFile())).All(d => d.Change == SaveFileChange.Unchanged);
    }

    /// <summary>
    /// True when <paramref name="files"/>, <paramref name="coopSlots"/> and the per-run counters' copy
    /// <paramref name="runScopes"/> are exactly this manifest's (<see cref="HoldsSame(IEnumerable{SaveFileHash}, IEnumerable{CoopSlotHash})"/>,
    /// and the same counters, or none on both sides): only then is a new backup the same as this one.
    /// </summary>
    public bool HoldsSame(IEnumerable<SaveFileHash> files, IEnumerable<CoopSlotHash> coopSlots, SaveFileHash? runScopes) =>
        HoldsSame(files, coopSlots) && Equals(RunScopes, runScopes);

    /// <summary>
    /// True when <paramref name="files"/>, <paramref name="coopSlots"/> and <paramref name="runScopes"/> are exactly this
    /// manifest's but for the file named <paramref name="fileName"/>, which is in both with other bytes: the only change is
    /// inside that one file (the caller then reads both copies to say whether that change matters,
    /// <see cref="ProfilePlayTime.SameApartFromPlayTime"/>). False when nothing differs (that is <see cref="HoldsSame(IEnumerable{SaveFileHash}, IEnumerable{CoopSlotHash}, SaveFileHash?)"/>).
    /// </summary>
    public bool HoldsSameApartFrom(IEnumerable<SaveFileHash> files, IEnumerable<CoopSlotHash> coopSlots, SaveFileHash? runScopes, string fileName)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(coopSlots);
        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("A file name is needed.", nameof(fileName));
        }

        if (!Equals(RunScopes, runScopes) ||
            !Compare(CoopSlots.Select(s => s.AsFile()), coopSlots.Select(s => s.AsFile())).All(d => d.Change == SaveFileChange.Unchanged))
        {
            return false;
        }

        bool changedThere = false;
        foreach (SaveFileDifference difference in Compare(Files, files))
        {
            if (difference.Change == SaveFileChange.Unchanged)
            {
                continue;
            }

            if (difference.Change != SaveFileChange.Changed || !string.Equals(difference.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            changedThere = true;
        }

        return changedThere;
    }

    /// <summary>
    /// Whether this backup (the newest) can stand for the saves about to be backed up, hashed as <paramref name="now"/>, before
    /// any file is read: <see cref="BackupStandIn.Same"/> when the files, co-op slots and per-run counters are exactly its own
    /// (<see cref="HoldsSame(IEnumerable{SaveFileHash}, IEnumerable{CoopSlotHash}, SaveFileHash?)"/>);
    /// <see cref="BackupStandIn.IfPlayTimeOnly"/> when only <see cref="ProfilePlayTime.ProfileFile"/> differs
    /// (<see cref="HoldsSameApartFrom"/>), unless <paramref name="exact"/>; else <see cref="BackupStandIn.None"/>. A restore's
    /// backup of the saves it replaces is <paramref name="exact"/>: the recovery of a restore the game stopped in the middle of
    /// reads that backup's hashes as the replaced files' own (D137), and Steam Cloud's copy of the profile is the one the
    /// restore replaced only by its hash, so only a backup of those very bytes can stand for it.
    /// </summary>
    public BackupStandIn StandsFor(SaveBackupManifest now, bool exact)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (HoldsSame(now.Files, now.CoopSlots, now.RunScopes))
        {
            return BackupStandIn.Same;
        }

        return !exact && HoldsSameApartFrom(now.Files, now.CoopSlots, now.RunScopes, ProfilePlayTime.ProfileFile) ? BackupStandIn.IfPlayTimeOnly : BackupStandIn.None;
    }

    /// <summary>True for a bare file name: not empty, no folder, nothing a path could climb out with.</summary>
    public static bool IsPlainFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && name != "." && name != ".." &&
        name.IndexOfAny(PathCharacters) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>The optional per-run counters' entry; anything that doesn't read is none (never fails the manifest).</summary>
    private static SaveFileHash? ReadRunScopes(JsonElement root)
    {
        if (!root.TryGetProperty("runScopes", out JsonElement scopes) || scopes.ValueKind != JsonValueKind.Object ||
            !scopes.TryGetProperty("bytes", out JsonElement bytes) || bytes.ValueKind != JsonValueKind.Number ||
            !bytes.TryGetInt64(out long size) || size < 0 ||
            !TryGetString(scopes, "sha256", out string sha) || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            return null;
        }

        return new SaveFileHash(RunScopeRestore.FileName, size, sha.ToUpperInvariant());
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out JsonElement found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString() ?? string.Empty;
        return true;
    }
}

/// <summary>
/// How the time in a backup folder's name is read (D129). Names since 0.11.0 say UTC (<c>20261105T100000Z-host</c>) and are
/// exact. Older names (<c>20261105-190000-host</c>) hold the local time of the PC that took them, read here in
/// <see cref="Zone"/> at their own date (so a daylight-saving change between them reads right), unless
/// <see cref="TakenAtUtc"/> has the backup's own manifest time, which is exact wherever it was taken.
/// </summary>
public sealed class BackupClock
{
    private readonly Dictionary<string, DateTime> _takenAtUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A clock for names taken in <paramref name="zone"/>, with the manifests' UTC times by backup name (any case) when known.</summary>
    public BackupClock(TimeZoneInfo zone, IEnumerable<KeyValuePair<string, DateTime>>? takenAtUtc = null)
    {
        Zone = zone ?? throw new ArgumentNullException(nameof(zone));
        foreach (KeyValuePair<string, DateTime> taken in takenAtUtc ?? Array.Empty<KeyValuePair<string, DateTime>>())
        {
            _takenAtUtc[taken.Key] = DateTime.SpecifyKind(taken.Value, DateTimeKind.Utc);
        }
    }

    /// <summary>This PC's zone, no manifest times.</summary>
    public static BackupClock Local => new(TimeZoneInfo.Local);

    /// <summary>The zone old (local-time) names are read in.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>The manifests' UTC times, by backup name, used for old names.</summary>
    public IReadOnlyDictionary<string, DateTime> TakenAtUtc => _takenAtUtc;

    /// <summary>
    /// <paramref name="local"/> (an old name's time, no zone) as UTC: the manifest's time for <paramref name="name"/> when
    /// known, else the time less <see cref="Zone"/>'s offset at that moment. A time a clock change skipped (02:30 on the
    /// spring-forward night) or saw twice (01:30 on the fall-back night) takes the zone's standard offset. Never throws.
    /// </summary>
    internal DateTime ToUtc(string name, DateTime local)
    {
        if (_takenAtUtc.TryGetValue(name, out DateTime known))
        {
            return DateTime.SpecifyKind(known, DateTimeKind.Utc);
        }

        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset = Zone.IsInvalidTime(unspecified) || Zone.IsAmbiguousTime(unspecified) ? Zone.BaseUtcOffset : Zone.GetUtcOffset(unspecified);
        long ticks = unspecified.Ticks - offset.Ticks;
        return new DateTime(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
    }

    /// <summary>The local date (in <see cref="Zone"/>) of <paramref name="utc"/>: the day a player would say a backup was taken.</summary>
    internal DateTime LocalDate(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).Date;
}

/// <summary>
/// Names of save backup folders (D46, D129): <c>yyyyMMdd'T'HHmmss'Z'-role</c> in UTC (<c>20261105T100000Z-host</c>), with
/// <c>-2</c>, <c>-3</c>... when one already has that name, and which ones go when only the newest few are kept. Names
/// from before 0.11.0 hold local time (<c>20261105-190000-host</c>) and are still read, through a <see cref="BackupClock"/>,
/// so old and new sort together by the moment they were taken.
/// </summary>
public static class SaveBackupNames
{
    /// <summary>The time part's format: UTC, marked with <c>T</c> and <c>Z</c>.</summary>
    public const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The time part's format before 0.11.0: the local time of the PC that took it, unmarked.</summary>
    public const string LocalTimeFormat = "yyyyMMdd-HHmmss";

    private const int TimeLength = 16;
    private const int LocalTimeLength = 15;

    /// <summary>
    /// A new backup's name for <paramref name="takenAtUtc"/> (a local time is converted; an unspecified one is taken as
    /// UTC) and <paramref name="role"/> (lowercase letters and dashes) that isn't one of <paramref name="existing"/>.
    /// </summary>
    public static string Make(DateTime takenAtUtc, string role, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (!IsRole(role))
        {
            throw new ArgumentException($"A backup's role is lowercase letters and dashes, not '{role}'.", nameof(role));
        }

        DateTime utc = takenAtUtc.Kind == DateTimeKind.Local ? takenAtUtc.ToUniversalTime() : takenAtUtc;
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        string name = $"{utc.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{role}";
        string candidate = name;
        for (int suffix = 2; taken.Contains(candidate); suffix++)
        {
            candidate = $"{name}-{suffix.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    /// <summary>
    /// Reads a backup's name: when it was taken (UTC; an old local-time name read in this PC's zone) and by whom (any
    /// <c>-2</c> suffix dropped). False for other names.
    /// </summary>
    public static bool TryParse(string name, out DateTime takenAtUtc, out string role) => TryParse(name, null, out takenAtUtc, out role);

    /// <summary>As <see cref="TryParse(string, out DateTime, out string)"/>, with old names read by <paramref name="clock"/> (null: <see cref="BackupClock.Local"/>).</summary>
    public static bool TryParse(string name, BackupClock? clock, out DateTime takenAtUtc, out string role)
    {
        takenAtUtc = default;
        role = string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        int timeLength;
        if (name.Length >= TimeLength + 2 && name[TimeLength] == '-' &&
            DateTime.TryParseExact(name[..TimeLength], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc))
        {
            timeLength = TimeLength;
            takenAtUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }
        else if (name.Length >= LocalTimeLength + 2 && name[LocalTimeLength] == '-' &&
            DateTime.TryParseExact(name[..LocalTimeLength], LocalTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            timeLength = LocalTimeLength;
            takenAtUtc = (clock ?? BackupClock.Local).ToUtc(name, local);
        }
        else
        {
            return false;
        }

        string rest = name[(timeLength + 1)..];
        int dash = rest.LastIndexOf('-');
        if (dash > 0 && dash < rest.Length - 1 && rest[(dash + 1)..].All(c => c is >= '0' and <= '9'))
        {
            rest = rest[..dash];
        }

        if (!IsRole(rest))
        {
            takenAtUtc = default;
            return false;
        }

        role = rest;
        return true;
    }

    /// <summary>
    /// How a log names the backup <paramref name="name"/> (D127, D129): a UTC name as it is; an old local-time name as the
    /// UTC name it would have now, marked as an older name (<c>20261105T100000Z-host (named in local time on disk)</c>), so
    /// no log line holds a local time; anything else as <c>a folder T2T Online didn't name</c>. Old names are read by
    /// <paramref name="clock"/> (null: <see cref="BackupClock.Local"/>). Never throws.
    /// </summary>
    public static string ForLog(string? name, BackupClock? clock = null)
    {
        if (string.IsNullOrEmpty(name) || !TryParse(name, clock, out DateTime takenAtUtc, out _))
        {
            return "a folder T2T Online didn't name";
        }

        if (name[TimeLength - 1] == 'Z' && name[TimeLength] == '-')
        {
            return name;
        }

        return $"{takenAtUtc.ToString(TimeFormat, CultureInfo.InvariantCulture)}{name[LocalTimeLength..]} (named in local time on disk)";
    }

    /// <summary>
    /// How a log gives a backup's reason (its manifest's, or the one a backup is being taken for), D127 and D129: every
    /// backup name in it (a restore's <c>before restoring &lt;name&gt;</c>) as <see cref="ForLog"/> names it, so no local
    /// time; a reason from 0.10.0 or earlier that named the host a friend followed (<c>before following &lt;Steam
    /// name&gt;</c>) as <c>before following a host</c>, today's wording; one line of at most 160 characters. Old names are
    /// read by <paramref name="clock"/> (null: <see cref="BackupClock.Local"/>). Never throws.
    /// </summary>
    public static string ReasonForLog(string? reason, BackupClock? clock = null)
    {
        const string Following = "before following ";
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        try
        {
            if (reason.StartsWith(Following, StringComparison.Ordinal))
            {
                return Following + "a host";
            }

            string[] words = reason.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length >= LocalTimeLength && TryParse(words[i], clock, out _, out _))
                {
                    words[i] = ForLog(words[i], clock);
                }
            }

            return Diagnostics.SafeText.Clean(string.Join(' ', words), 160);
        }
        catch (Exception)
        {
            return "a reason that couldn't be read";
        }
    }

    /// <summary>True for a backup name in the UTC form (<see cref="TimeFormat"/>); false for an old local-time one or anything else.</summary>
    public static bool IsUtcName(string name) => TryParse(name, null, out _, out _) && name.Length > TimeLength && name[TimeLength - 1] == 'Z';

    /// <summary>
    /// The backups among <paramref name="names"/> beyond the newest <paramref name="keep"/> (by when they were taken, then
    /// by name), oldest last. Names that aren't backups are never in it. Old names are read by <paramref name="clock"/>
    /// (null: <see cref="BackupClock.Local"/>).
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<string> names, int keep, BackupClock? clock = null) => Expired(names, keep, keepOldest: false, clock);

    /// <summary>
    /// As <see cref="Expired(IEnumerable{string}, int, BackupClock?)"/>; with <paramref name="keepOldest"/>, the oldest backup
    /// never expires either (the saves from before the first online session, the one copy nothing later can replace).
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<string> names, int keep, bool keepOldest, BackupClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "Keep at least none.");
        }

        List<string> expired = NewestFirst(names, clock).Skip(keep).ToList();
        if (keepOldest && expired.Count > 0)
        {
            expired.RemoveAt(expired.Count - 1); // oldest last
        }

        return expired;
    }

    /// <summary>
    /// As <see cref="Expired(IEnumerable{string}, int, BackupClock?)"/>, but the backups named in <paramref name="keepAlways"/>
    /// never expire (ignoring case), whatever the time in their name says: the first backup ever taken, the one just taken,
    /// the one being restored. A clock that was wrong when a backup was named can't make it go.
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<string> names, int keep, IEnumerable<string?> keepAlways, BackupClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(keepAlways);
        var kept = new HashSet<string>(keepAlways.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!), StringComparer.OrdinalIgnoreCase);
        return Expired(names, keep, clock).Where(n => !kept.Contains(n)).ToList();
    }

    /// <summary>
    /// The first backup of each of the newest <paramref name="days"/> days that have any, by when they were taken, the days
    /// as this PC's calendar has them (<paramref name="clock"/>'s zone). A backup is taken per Start, so the newest few can
    /// all be from one evening's games: these still hold the saves from before them. Names that aren't backups are never in it.
    /// </summary>
    public static IReadOnlyList<string> FirstOfEachDay(IEnumerable<string> names, int days, BackupClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        BackupClock reader = clock ?? BackupClock.Local;
        return Read(names, reader)
            .GroupBy(b => reader.LocalDate(b.At))
            .OrderByDescending(day => day.Key)
            .Take(Math.Max(days, 0))
            .Select(day => day.OrderBy(b => b.At).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).First().Name)
            .ToList();
    }

    /// <summary>The oldest backup among <paramref name="names"/> by when it was taken, or null when there's none.</summary>
    public static string? Oldest(IEnumerable<string> names, BackupClock? clock = null)
    {
        IReadOnlyList<string> backups = NewestFirst(names, clock);
        return backups.Count > 0 ? backups[^1] : null;
    }

    /// <summary>
    /// The backups among <paramref name="names"/>, newest first (by when they were taken, then by name). Names that aren't
    /// backups are left out. Old names are read by <paramref name="clock"/> (null: <see cref="BackupClock.Local"/>).
    /// </summary>
    public static IReadOnlyList<string> NewestFirst(IEnumerable<string> names, BackupClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        return Read(names, clock ?? BackupClock.Local)
            .OrderByDescending(b => b.At)
            .ThenByDescending(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => b.Name)
            .ToList();
    }

    private static IEnumerable<(string Name, DateTime At)> Read(IEnumerable<string> names, BackupClock clock)
    {
        foreach (string name in names)
        {
            if (TryParse(name, clock, out DateTime at, out _))
            {
                yield return (name, at);
            }
        }
    }

    /// <summary>True for a file the game keeps a save in: <c>*.es3</c> (not Easy Save's own <c>.bac</c> and <c>.tmp</c> copies).</summary>
    public static bool IsSaveFile(string fileName) =>
        !string.IsNullOrEmpty(fileName) && fileName.EndsWith(".es3", StringComparison.OrdinalIgnoreCase) && fileName.Length > 4;

    /// <summary>
    /// True for a save file the game also keeps in Steam Cloud. Every save the game writes goes to the cloud except its
    /// display and audio settings, <c>config.es3</c> (Oct 29 build: <c>SystemSettings</c> loads and stores it with
    /// <c>isCloudSave: false</c>), and the one-time local copy of a profile from before a revert
    /// (<c>profileV2_before_revert.es3</c>, <c>TryBackUpProfileBeforeRevert</c>: the name with <c>_before_revert</c> before
    /// its extension), so a restore pushes every restored file but those.
    /// </summary>
    public static bool IsCloudSaved(string fileName) =>
        IsSaveFile(fileName) &&
        !string.Equals(fileName, "config.es3", StringComparison.OrdinalIgnoreCase) &&
        !fileName.EndsWith("_before_revert.es3", StringComparison.OrdinalIgnoreCase);

    private static bool IsRole(string role) =>
        !string.IsNullOrEmpty(role) && role.Length <= 32 && role[0] != '-' && role[^1] != '-' && role.All(c => c is (>= 'a' and <= 'z') or '-');
}

/// <summary>
/// A restore under way (D46): written beside the backups before the first restored file is moved
/// into place, and deleted once the restore is whole (or put back) and what follows it is safe (the shared-progress ledger,
/// the list of files Steam Cloud must get). One found at start-up means the game stopped in the middle of a restore (a
/// crash, a power cut): some files may be restored and some not, and the next start finishes or undoes it. Text, one
/// <c>key=value</c> per line: <c>backup</c>, <c>before</c> (the backup of the saves from just before the restore), then one
/// <c>file</c> per save file and one <c>slot</c> per co-op slot being restored, and <c>moved=1</c> once every one
/// of them is in place (the journal is written again then). Reading one never throws.
/// </summary>
public sealed class RestoreJournal
{
    /// <summary>The journal's file name in the backups folder.</summary>
    public const string FileName = "restore-journal.txt";

    private const int MaxFiles = 4096;

    /// <summary>A journal of restoring <paramref name="backup"/> (<paramref name="files"/>), with the saves from before in <paramref name="before"/>.</summary>
    public RestoreJournal(string backup, string before, IEnumerable<string> files)
        : this(backup, before, files, Array.Empty<int>())
    {
    }

    /// <summary>
    /// A journal of restoring <paramref name="backup"/>'s <paramref name="files"/> and co-op slots <paramref name="slots"/>,
    /// with the saves and slots from before in <paramref name="before"/>; <paramref name="moved"/>: every one is in place.
    /// </summary>
    public RestoreJournal(string backup, string before, IEnumerable<string> files, IEnumerable<int> slots, bool moved = false)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(slots);
        Backup = backup;
        Before = before;
        Files = files.ToList();
        Slots = slots.Distinct().OrderBy(s => s).ToList();
        Moved = moved;
    }

    /// <summary>True once every file and slot is in place: only what follows the moves (the ledger, Steam Cloud's list) may be missing.</summary>
    public bool Moved { get; }

    /// <summary>The same journal, with every file and slot in place.</summary>
    public RestoreJournal AllMoved() => new(Backup, Before, Files, Slots, moved: true);

    /// <summary>The backup being restored.</summary>
    public string Backup { get; }

    /// <summary>The backup of the saves from just before the restore.</summary>
    public string Before { get; }

    /// <summary>The save files being restored, by name.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>The co-op slots being restored.</summary>
    public IReadOnlyList<int> Slots { get; }

    /// <summary>The journal as text.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("backup=").Append(Backup).Append('\n');
        text.Append("before=").Append(Before).Append('\n');
        foreach (string file in Files)
        {
            text.Append("file=").Append(file).Append('\n');
        }

        foreach (int slot in Slots)
        {
            text.Append("slot=").Append(slot.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        if (Moved)
        {
            text.Append("moved=1\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads a journal. False for anything that isn't one: a backup name that isn't one, a file name with a folder in it or
    /// that isn't a save file, a co-op slot that isn't one or is there twice, an unknown or repeated key, too many files.
    /// Never throws.
    /// </summary>
    public static bool TryParse(string? text, out RestoreJournal? journal)
    {
        journal = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? backup = null;
        string? before = null;
        bool moved = false;
        var files = new List<string>();
        var slots = new List<int>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return false;
            }

            string key = line[..equals];
            string value = line[(equals + 1)..];
            switch (key)
            {
                case "backup" when backup == null && IsBackupName(value):
                    backup = value;
                    break;
                case "before" when before == null && IsBackupName(value):
                    before = value;
                    break;
                case "file" when files.Count < MaxFiles && SaveBackupManifest.IsPlainFileName(value) && SaveBackupNames.IsSaveFile(value):
                    files.Add(value);
                    break;
                case "slot" when value.Length == 1 && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int slot) &&
                    SaveBackupManifest.IsCoopSlot(slot) && !slots.Contains(slot):
                    slots.Add(slot);
                    break;
                case "moved" when !moved && value == "1":
                    moved = true;
                    break;
                default:
                    return false;
            }
        }

        if (backup == null || before == null)
        {
            return false;
        }

        journal = new RestoreJournal(backup, before, files, slots, moved);
        return true;
    }

    private static bool IsBackupName(string name) => SaveBackupManifest.IsPlainFileName(name) && SaveBackupNames.TryParse(name, out _, out _);
}
