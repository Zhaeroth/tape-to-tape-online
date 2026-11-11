using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>What a <see cref="CustomItem"/> holds. Values are part of the protocol: only add.</summary>
public enum CustomContentKind : byte
{
    /// <summary>Not a valid kind.</summary>
    None = 0,

    /// <summary>A custom team: its <c>TeamDataModel</c> as the game's own JSON (keyed by the team's id).</summary>
    Team = 1,

    /// <summary>A custom skater: its <c>ForwardDataModel</c> as the game's own JSON (keyed by the skater's id).</summary>
    Skater = 2,

    /// <summary>A custom goalie: its <c>GoalieDataModel</c> as the game's own JSON (keyed by the goalie's id).</summary>
    Goalie = 3,

    /// <summary>A custom logo: the PNG file's bytes (keyed by the game's name for its sprite, <c>custom_&lt;file&gt;.png</c>).</summary>
    Logo = 4,

    /// <summary>
    /// A custom logo the host's campaign run team wears (as <see cref="Logo"/>): a friend also puts it on the run team it
    /// holds, should that run have loaded before the logo arrived.
    /// </summary>
    RunLogo = 5,

    /// <summary>
    /// The look of the host's campaign run team that its run file doesn't keep (<see cref="RunLookData"/>: the away and away
    /// goalie kits, the transition colours, the goalie's colours), keyed by <see cref="RunLookData.Key"/>: a friend puts it on
    /// its copy of the host's run after each load (its own game would make them from its own squad or custom team).
    /// </summary>
    RunLook = 6,
}

/// <summary>
/// One piece of custom content that exists only on the host's PC (a custom team, skater, goalie or logo): its kind, its key (the id
/// or sprite name the game knows it by) and its bytes, as the host's game keeps them. Identified by <see cref="Hash"/>, so the same
/// content is sent to a friend only once a session.
/// </summary>
public sealed class CustomItem
{
    /// <summary>Longest key, in UTF-8 bytes.</summary>
    public const int MaxKeyBytes = 128;

    /// <summary>Largest logo (a PNG file).</summary>
    public const int MaxLogoBytes = 4 * 1024 * 1024;

    /// <summary>Largest team, skater or goalie (the game's JSON for one is a few KB).</summary>
    public const int MaxModelBytes = 256 * 1024;

    /// <summary>Deepest nesting of a model's JSON (<c>JsonUtility</c> writes a team or a skater about three levels deep).</summary>
    public const int MaxModelDepth = 16;

    /// <summary>The prefix the game gives a custom logo's sprite name (<c>CustomLogoLoader.LoadSprites</c>).</summary>
    public const string LogoPrefix = "custom_";

    /// <summary>An item. Throws when the kind, key or size isn't one a friend accepts (<see cref="Check"/>).</summary>
    public CustomItem(CustomContentKind kind, string key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(bytes);
        if (Check(kind, key, bytes.Length) is string problem)
        {
            throw new ArgumentException(problem, nameof(key));
        }

        Kind = kind;
        Key = key;
        Bytes = bytes;
        Hash = ComputeHash(kind, key, bytes);
    }

    /// <summary>What it is.</summary>
    public CustomContentKind Kind { get; }

    /// <summary>The id (team, skater, goalie) or sprite name (logo) the game knows it by.</summary>
    public string Key { get; }

    /// <summary>The content: the game's JSON for a model, the PNG file for a logo.</summary>
    public byte[] Bytes { get; }

    /// <summary>FNV-1a over kind, key and bytes: equal hashes, equal content.</summary>
    public ulong Hash { get; }

    /// <summary>This item's reference, as a match setup names it.</summary>
    public CustomRef Ref => new(Kind, Hash, Key);

    /// <summary>True for a logo (<see cref="CustomContentKind.Logo"/> or <see cref="CustomContentKind.RunLogo"/>).</summary>
    public bool IsLogo => Kind is CustomContentKind.Logo or CustomContentKind.RunLogo;

    /// <summary>True for a kind this build knows.</summary>
    public static bool IsKnown(CustomContentKind kind) => kind is >= CustomContentKind.Team and <= CustomContentKind.RunLook;

    /// <summary>The largest item of <paramref name="kind"/>.</summary>
    public static int MaxBytesOf(CustomContentKind kind) => kind switch
    {
        CustomContentKind.Logo or CustomContentKind.RunLogo => MaxLogoBytes,
        CustomContentKind.RunLook => RunLookData.MaxBytes,
        _ => MaxModelBytes,
    };

    /// <summary>
    /// Why an item of <paramref name="kind"/>, <paramref name="key"/> and <paramref name="length"/> bytes isn't acceptable,
    /// or null when it is. A key is a file name on a friend's PC (a logo is loaded from a file there), so it's held to
    /// what's safe as one: no path, no control or reserved characters. A logo's is the game's sprite name for a PNG in its
    /// custom logo folder.
    /// </summary>
    public static string? Check(CustomContentKind kind, string key, int length)
    {
        if (!IsKnown(kind))
        {
            return $"unknown kind {(byte)kind}";
        }

        if (length <= 0 || length > MaxBytesOf(kind))
        {
            return $"{length} bytes (1 to {MaxBytesOf(kind)})";
        }

        if (string.IsNullOrEmpty(key) || System.Text.Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
        {
            return $"a key of {key?.Length ?? 0} characters (1 to {MaxKeyBytes} bytes)";
        }

        foreach (char c in key)
        {
            if (char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return "a key with a character a file name can't hold";
            }
        }

        if (key.Trim() != key || key.EndsWith('.') || key.Contains("..", StringComparison.Ordinal))
        {
            return "a key a file name can't be";
        }

        bool logo = kind is CustomContentKind.Logo or CustomContentKind.RunLogo;
        if (logo && (!key.StartsWith(LogoPrefix, StringComparison.Ordinal) || key.Length <= LogoPrefix.Length + 4 ||
            !key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            return "a logo key that isn't the game's name for a custom PNG (custom_<file>.png)";
        }

        if (logo && IsDeviceName(LogoFileName(key)))
        {
            // A friend writes the logo to this file name: Windows would open a device instead.
            return "a logo key whose file name Windows keeps for a device (CON, NUL, COM1...)";
        }

        return null;
    }

    /// <summary>
    /// True for a file name Windows opens as a device rather than a file: CON, PRN, AUX, NUL, COM0-9, LPT0-9 (also with
    /// a superscript 1, 2 or 3), CONIN$ and CONOUT$, in any case, with or without an extension and with spaces before it
    /// (<c>NUL.png</c> and <c>COM1 .png</c> are devices too on Windows 10).
    /// </summary>
    public static bool IsDeviceName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        int dot = fileName.IndexOf('.', StringComparison.Ordinal);
        ReadOnlySpan<char> stem = (dot < 0 ? fileName.AsSpan() : fileName.AsSpan(0, dot)).TrimEnd(' ');
        if (stem.Length == 3)
        {
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        if (stem.Length == 4 && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return stem[3] is (>= '0' and <= '9') or '¹' or '²' or '³';
        }

        return stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The hash of an item (see <see cref="Hash"/>).</summary>
    public static ulong ComputeHash(CustomContentKind kind, string key, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ulong hash = Fnv.Add(Fnv.OffsetBasis, (byte)kind);
        Span<byte> length = stackalloc byte[4];
        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        BinaryPrimitives.WriteInt32LittleEndian(length, keyBytes.Length);
        hash = Fnv.Add(hash, length);
        hash = Fnv.Add(hash, keyBytes);
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash = Fnv.Add(hash, length);
        return Fnv.Add(hash, bytes);
    }

    /// <summary>
    /// True when <paramref name="json"/> nests no deeper than <see cref="MaxModelDepth"/>. Checked before a model reaches Unity's
    /// <c>JsonUtility</c>, a native parser nobody here can read: 256 KB of <c>[</c> would be 256,000 levels for it, and a stack
    /// overflow is the one failure the mod can't catch. Only the nesting is looked at; what isn't JSON is left for Unity to refuse.
    /// </summary>
    public static bool IsShallowJson(ReadOnlySpan<byte> json)
    {
        int depth = 0;
        bool inText = false;
        for (int i = 0; i < json.Length; i++)
        {
            byte b = json[i];
            if (inText)
            {
                if (b == (byte)'\\')
                {
                    i++;
                }
                else if (b == (byte)'"')
                {
                    inText = false;
                }
            }
            else if (b == (byte)'"')
            {
                inText = true;
            }
            else if (b is (byte)'{' or (byte)'[')
            {
                if (++depth > MaxModelDepth)
                {
                    return false;
                }
            }
            else if (b is (byte)'}' or (byte)']')
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        return true;
    }

    /// <summary>The file name a logo key stands for (the key without the game's <see cref="LogoPrefix"/>).</summary>
    public static string LogoFileName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith(LogoPrefix, StringComparison.Ordinal) ? key[LogoPrefix.Length..] : key;
    }
}

/// <summary>A match setup's reference to one <see cref="CustomItem"/>: its kind, hash and key.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Hash">The item's <see cref="CustomItem.Hash"/>.</param>
/// <param name="Key">The id or sprite name the game knows it by.</param>
public readonly record struct CustomRef(CustomContentKind Kind, ulong Hash, string Key)
{
    /// <summary>"team:0123456789abcdef:&lt;id&gt;", as a setup value carries it.</summary>
    public string Format() => string.Create(CultureInfo.InvariantCulture, $"{Name(Kind)}:{Hash:x16}:{Key}");

    /// <summary>
    /// For logs: "team &lt;id&gt; (0123456789abcdef)". A logo's key is its file name, which its player chose, so a logo is
    /// only "logo (0123456789abcdef)": the hash of its bytes, the same on every PC (D127). Any other key is a host's id, written
    /// only when it has an identifier's shape (<see cref="Diagnostics.HostText.KeyForLog(string?)"/>, D146).
    /// </summary>
    public override string ToString() => Kind is CustomContentKind.Logo or CustomContentKind.RunLogo
        ? string.Create(CultureInfo.InvariantCulture, $"{Name(Kind)} ({Hash:x16})")
        : string.Create(CultureInfo.InvariantCulture, $"{Name(Kind)} {Diagnostics.HostText.KeyForLog(Key)} ({Hash:x16})");

    /// <summary>Reads <see cref="Format"/>'s text. False for anything else (an unknown kind, a bad hash, an unsafe key).</summary>
    public static bool TryParse(string? text, out CustomRef value)
    {
        value = default;
        if (text == null)
        {
            return false;
        }

        int first = text.IndexOf(':', StringComparison.Ordinal);
        int second = first < 0 ? -1 : text.IndexOf(':', first + 1);
        if (first <= 0 || second != first + 17)
        {
            return false;
        }

        CustomContentKind kind = KindOf(text[..first]);
        string key = text[(second + 1)..];
        if (kind == CustomContentKind.None ||
            !ulong.TryParse(text.AsSpan(first + 1, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hash) ||
            CustomItem.Check(kind, key, 1) != null)
        {
            return false;
        }

        value = new CustomRef(kind, hash, key);
        return true;
    }

    /// <summary>A kind's word in a reference.</summary>
    public static string Name(CustomContentKind kind) => kind switch
    {
        CustomContentKind.Team => "team",
        CustomContentKind.Skater => "skater",
        CustomContentKind.Goalie => "goalie",
        CustomContentKind.Logo => "logo",
        CustomContentKind.RunLogo => "runlogo",
        CustomContentKind.RunLook => "runlook",
        _ => "none",
    };

    private static CustomContentKind KindOf(string name) => name switch
    {
        "team" => CustomContentKind.Team,
        "skater" => CustomContentKind.Skater,
        "goalie" => CustomContentKind.Goalie,
        "logo" => CustomContentKind.Logo,
        "runlogo" => CustomContentKind.RunLogo,
        "runlook" => CustomContentKind.RunLook,
        _ => CustomContentKind.None,
    };
}

/// <summary>
/// The custom content a match setup needs (<see cref="Protocol.MatchSetupMessage"/>): <c>custom.count</c>, then each
/// reference as <c>custom.0</c>, <c>custom.1</c>, ... (<see cref="CustomRef.Format"/>). A friend's game loads the match once
/// it holds every one of them.
/// </summary>
public static class CustomContentValues
{
    /// <summary>The key of the reference count.</summary>
    public const string CountKey = "custom.count";

    /// <summary>Most references in one setup (two teams of five with their goalies and logos, and room for a lineup's own picks).</summary>
    public const int MaxRefs = 24;

    /// <summary>Adds <paramref name="refs"/> to a setup through <paramref name="add"/> (nothing when there are none).</summary>
    public static void Add(Action<string, NetValue> add, IReadOnlyList<CustomRef> refs)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(refs);
        if (refs.Count == 0)
        {
            return;
        }

        if (refs.Count > MaxRefs)
        {
            throw new ArgumentException($"At most {MaxRefs} references.", nameof(refs));
        }

        add(CountKey, NetValue.FromInt(refs.Count));
        for (int i = 0; i < refs.Count; i++)
        {
            add(Key(i), NetValue.FromString(refs[i].Format()));
        }
    }

    /// <summary>
    /// The references in a setup (none when it has no <see cref="CountKey"/>). False, with why, for a count out of range or
    /// a reference that doesn't read; never throws.
    /// </summary>
    public static bool TryRead(IReadOnlyList<KeyValuePair<string, NetValue>> setup, out List<CustomRef> refs, out string problem)
    {
        ArgumentNullException.ThrowIfNull(setup);
        refs = new List<CustomRef>();
        problem = string.Empty;
        NetValue count = MatchSetupValues.Get(setup, CountKey);
        if (count.Kind == NetValueKind.Null)
        {
            return true;
        }

        if (count.Kind != NetValueKind.Whole || count.WholeValue is < 1 or > MaxRefs)
        {
            problem = $"{CountKey} is {count} (1 to {MaxRefs})";
            return false;
        }

        for (int i = 0; i < count.WholeValue; i++)
        {
            NetValue value = MatchSetupValues.Get(setup, Key(i));
            if (value.Kind != NetValueKind.Text || !CustomRef.TryParse(value.Text, out CustomRef found))
            {
                problem = $"{Key(i)} isn't a custom content reference";
                refs.Clear();
                return false;
            }

            if (!refs.Contains(found))
            {
                refs.Add(found);
            }
        }

        return true;
    }

    /// <summary>The setup key of reference <paramref name="index"/>.</summary>
    public static string Key(int index) => string.Create(CultureInfo.InvariantCulture, $"custom.{index}");
}

/// <summary>
/// A PNG's size from its header (the signature, then the IHDR chunk: width and height, big-endian), read before the bytes
/// go anywhere near an image decoder: a friend refuses a logo too large to decode (a crafted file can claim a size that
/// would take gigabytes).
/// </summary>
public static class PngHeader
{
    /// <summary>The largest side a friend decodes (the game's own logos are a few hundred pixels).</summary>
    public const int MaxSide = 4096;

    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static ReadOnlySpan<byte> Ihdr => "IHDR"u8;

    /// <summary>The image's width and height. False when the bytes aren't a PNG (a signature and an IHDR chunk first).</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(Signature) || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 ||
            !bytes.Slice(12, 4).SequenceEqual(Ihdr))
        {
            return false;
        }

        uint w = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
        {
            return false;
        }

        width = (int)w;
        height = (int)h;
        return true;
    }

    /// <summary>Why <paramref name="bytes"/> aren't a logo a friend decodes, or null when they are.</summary>
    public static string? CheckLogo(ReadOnlySpan<byte> bytes)
    {
        if (!TryRead(bytes, out int width, out int height))
        {
            return "it isn't a PNG";
        }

        return width > MaxSide || height > MaxSide
            ? string.Create(CultureInfo.InvariantCulture, $"it's {width}x{height} pixels (at most {MaxSide} a side)")
            : null;
    }
}

/// <summary>
/// How many of the host's logos a friend decodes in one session, and how large. The receiver caps what it
/// keeps, but each logo is decoded into a texture as it arrives and stays in the game's logo list for the session: a host
/// sending logo after logo (each up to <see cref="PngHeader.MaxSide"/> a side, a few KB when blank) would otherwise grow
/// the friend's memory and stall its frames without end. A real session needs a handful (two teams a match, the run team).
/// </summary>
public sealed class CustomLogoBudget
{
    /// <summary>Most logos decoded in one session.</summary>
    public const int MaxLogos = 32;

    /// <summary>Most pixels decoded in one session: four logos of the largest size (about 360 MB of textures with mipmaps).</summary>
    public const long MaxPixels = 4L * PngHeader.MaxSide * PngHeader.MaxSide;

    /// <summary>Logos taken this session.</summary>
    public int Logos { get; private set; }

    /// <summary>Pixels taken this session.</summary>
    public long Pixels { get; private set; }

    /// <summary>Logos refused this session (over the budget).</summary>
    public int Refused { get; private set; }

    /// <summary>
    /// Takes a logo of <paramref name="width"/> by <paramref name="height"/> pixels from the budget. False, with why, when it
    /// doesn't fit (counted in <see cref="Refused"/>; nothing is taken).
    /// </summary>
    public bool TryTake(int width, int height, out string problem)
    {
        long pixels = (long)Math.Max(width, 0) * Math.Max(height, 0);
        if (Logos >= MaxLogos || Pixels + pixels > MaxPixels)
        {
            Refused++;
            problem = string.Create(CultureInfo.InvariantCulture,
                $"this session has decoded {Logos} of the host's logos ({Pixels / 1_000_000.0:0.#} Mpx); the most it takes is {MaxLogos} ({MaxPixels / 1_000_000.0:0.#} Mpx)");
            return false;
        }

        Logos++;
        Pixels += pixels;
        problem = string.Empty;
        return true;
    }

    /// <summary>A new session: nothing taken.</summary>
    public void Reset()
    {
        Logos = 0;
        Pixels = 0;
        Refused = 0;
    }
}
