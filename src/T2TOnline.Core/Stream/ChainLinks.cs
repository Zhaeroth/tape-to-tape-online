using System;
using System.Collections.Generic;
using System.Globalization;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// The HUD values (string-keyed, <c>HudStateMessage</c>) that carry the host's balls and chains to friends: the chain is
/// a rope the host's game makes with each ball (<c>BallAndChainController.SpawnBall</c>) and that is in no stream (no
/// replay object), so a friend's PC makes the game's own chain for each ball the host lists and lets the game's rope draw
/// it (the plugin's <c>Streaming/ChainMirror</c>). A peer that doesn't know a key ignores it (the HUD channel's rule).
/// </summary>
public static class ChainKeys
{
    /// <summary>What every chain key starts with.</summary>
    public const string Prefix = "chain.";

    /// <summary>Whole: how many chains the host lists (0 to <see cref="MaxChains"/>).</summary>
    public const string Count = "chain.count";

    /// <summary>The most chains carried (the prisoner boss has two; the talent one per ball, on skaters on the ice).</summary>
    public const int MaxChains = 8;

    private static readonly string[] Balls = Keys("ball");
    private static readonly string[] Skaters = Keys("skater");
    private static readonly string[] Rights = Keys("right");
    private static readonly string[] Humans = Keys("human");

    /// <summary>Reference: chain <paramref name="index"/>'s ball (<c>BallAndChainComponent</c>'s object).</summary>
    public static string Ball(int index) => Balls[index];

    /// <summary>Reference: the skater chain <paramref name="index"/> is fastened to (<c>BallAndChainComponent._victim</c>).</summary>
    public static string Skater(int index) => Skaters[index];

    /// <summary>Flag: chain <paramref name="index"/> is fastened to the right skate (a skater's second ball), else the left.</summary>
    public static string Right(int index) => Rights[index];

    /// <summary>Flag: chain <paramref name="index"/>'s skater plays for a team with players (the game's player length), else the AI's.</summary>
    public static string Human(int index) => Humans[index];

    /// <summary>True for a key this file names (the HUD channel hands those to the chain follower and writes them nowhere else).</summary>
    public static bool IsKey(string key) => key != null && key.StartsWith(Prefix, StringComparison.Ordinal);

    private static string[] Keys(string field)
    {
        var keys = new string[MaxChains];
        for (int i = 0; i < MaxChains; i++)
        {
            keys[i] = string.Create(CultureInfo.InvariantCulture, $"{Prefix}{i}.{field}");
        }

        return keys;
    }
}

/// <summary>One chain as the host lists it: its ball and skater (stream object ids), which skate, and whose length.</summary>
/// <param name="Ball">The ball's stream object id.</param>
/// <param name="Skater">The skater's stream object id.</param>
/// <param name="Right">Fastened to the right skate (the skater's second ball).</param>
/// <param name="Human">The game's player length (a team with players), else the AI's.</param>
public readonly record struct ChainLink(ushort Ball, ushort Skater, bool Right, bool Human);

/// <summary>
/// Friend side: the host's chains from its HUD values (<see cref="ChainKeys"/>). Feed it every chain value
/// (<see cref="Take"/>), then read the chains listed now (<see cref="Links"/>). Network input: a value of the wrong kind,
/// an index past <see cref="ChainKeys.MaxChains"/>, a count out of range or an unknown field is refused (counted), and
/// only complete chains count: a ball and a skater both set, each ball once. Not thread-safe; never throws.
/// </summary>
public sealed class ChainFollower
{
    private readonly ushort[] _balls = new ushort[ChainKeys.MaxChains];
    private readonly ushort[] _skaters = new ushort[ChainKeys.MaxChains];
    private readonly bool[] _rights = new bool[ChainKeys.MaxChains];
    private readonly bool[] _humans = new bool[ChainKeys.MaxChains];
    private readonly List<ChainLink> _links = new(ChainKeys.MaxChains);
    private int _count;

    /// <summary>True once a value changed what the host lists, until <see cref="EndMessage"/>.</summary>
    public bool Changed { get; private set; }

    /// <summary>Chain values refused (wrong kind, out of range, unknown key).</summary>
    public int Refused { get; private set; }

    /// <summary>Forgets the host's chains and the count of refusals (playback starting).</summary>
    public void Reset()
    {
        Array.Clear(_balls);
        Array.Clear(_skaters);
        Array.Clear(_rights);
        Array.Clear(_humans);
        _count = 0;
        Changed = false;
        Refused = 0;
    }

    /// <summary>A HUD message has been read: <see cref="Changed"/> starts again.</summary>
    public void EndMessage() => Changed = false;

    /// <summary>
    /// Takes one HUD value. True when <paramref name="key"/> is a chain key (whatever the value: the caller writes it nowhere
    /// else); a value that doesn't fit is refused and changes nothing.
    /// </summary>
    public bool Take(string key, NetValue value)
    {
        if (!ChainKeys.IsKey(key))
        {
            return false;
        }

        if (string.Equals(key, ChainKeys.Count, StringComparison.Ordinal))
        {
            if (value.Kind == NetValueKind.Whole && value.WholeValue is >= 0 and <= ChainKeys.MaxChains)
            {
                Set(ref _count, (int)value.WholeValue);
            }
            else
            {
                Refused++;
            }

            return true;
        }

        if (!TryParse(key.AsSpan(ChainKeys.Prefix.Length), out int index, out ReadOnlySpan<char> field))
        {
            Refused++;
            return true;
        }

        bool taken = field switch
        {
            "ball" => TryRef(value, ref _balls[index]),
            "skater" => TryRef(value, ref _skaters[index]),
            "right" => TryFlag(value, ref _rights[index]),
            "human" => TryFlag(value, ref _humans[index]),
            _ => false,
        };

        if (!taken)
        {
            Refused++;
        }

        return true;
    }

    /// <summary>
    /// The chains the host lists now, in its order: of the first <c>chain.count</c> places, those with a ball and a skater,
    /// each ball once (a ball named twice keeps its first place). The list is reused by the next call.
    /// </summary>
    public IReadOnlyList<ChainLink> Links()
    {
        _links.Clear();
        for (int i = 0; i < _count; i++)
        {
            if (_balls[i] == 0 || _skaters[i] == 0 || _links.Exists(link => link.Ball == _balls[i]))
            {
                continue;
            }

            _links.Add(new ChainLink(_balls[i], _skaters[i], _rights[i], _humans[i]));
        }

        return _links;
    }

    /// <summary>"3.ball" into 3 and "ball": one or two digits, below <see cref="ChainKeys.MaxChains"/>, then a dot and the field.</summary>
    private static bool TryParse(ReadOnlySpan<char> rest, out int index, out ReadOnlySpan<char> field)
    {
        index = -1;
        field = default;
        int dot = rest.IndexOf('.');
        if (dot is < 1 or > 2 || !int.TryParse(rest[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index >= ChainKeys.MaxChains)
        {
            return false;
        }

        field = rest[(dot + 1)..];
        return true;
    }

    /// <summary>A stream object reference (1 to 65535), or none (Null).</summary>
    private bool TryRef(NetValue value, ref ushort slot)
    {
        if (value.Kind == NetValueKind.Null)
        {
            Set(ref slot, (ushort)0);
            return true;
        }

        if (value.Kind != NetValueKind.Reference || value.WholeValue is < 1 or > ushort.MaxValue)
        {
            return false;
        }

        Set(ref slot, (ushort)value.WholeValue);
        return true;
    }

    private bool TryFlag(NetValue value, ref bool slot)
    {
        if (value.Kind != NetValueKind.Flag)
        {
            return false;
        }

        Set(ref slot, value.WholeValue != 0);
        return true;
    }

    private void Set<T>(ref T slot, T value)
        where T : IEquatable<T>
    {
        if (!slot.Equals(value))
        {
            slot = value;
            Changed = true;
        }
    }
}

/// <summary>
/// Friend side: which chains made here go and which of the host's are still to make (<see cref="Diff"/>), and how many may
/// be made a second (<see cref="TryTake"/>): a host can't fill a friend's game with chains, whatever it lists. Pure logic,
/// no clock of its own: the caller passes the time. Never throws.
/// </summary>
public sealed class ChainSync
{
    private readonly int _perSecond;
    private long _window = long.MinValue;
    private int _taken;

    /// <summary>A sync that lets <paramref name="perSecond"/> chains be made a second.</summary>
    public ChainSync(int perSecond) => _perSecond = Math.Max(1, perSecond);

    /// <summary>Chains made, all told (since <see cref="Reset"/>).</summary>
    public long Made { get; private set; }

    /// <summary>Times a chain waited for the next second's budget.</summary>
    public long Deferred { get; private set; }

    /// <summary>
    /// Fills <paramref name="gone"/> with the chains in <paramref name="made"/> the host no longer lists as they are (their
    /// ball, skater, skate or length changed: a new ball is a new chain), and <paramref name="missing"/> with the host's
    /// chains not made yet, in its order. Both lists are cleared first.
    /// </summary>
    public static void Diff(IReadOnlyCollection<ChainLink> made, IReadOnlyList<ChainLink> wanted, List<ChainLink> gone, List<ChainLink> missing)
    {
        ArgumentNullException.ThrowIfNull(made);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(gone);
        ArgumentNullException.ThrowIfNull(missing);
        gone.Clear();
        missing.Clear();
        foreach (ChainLink link in made)
        {
            if (!Contains(wanted, link))
            {
                gone.Add(link);
            }
        }

        foreach (ChainLink link in wanted)
        {
            if (!Contains(made, link))
            {
                missing.Add(link);
            }
        }
    }

    /// <summary>True when one more chain may be made at <paramref name="nowMicros"/> (counted as made); else counted as deferred.</summary>
    public bool TryTake(long nowMicros)
    {
        if (_window == long.MinValue || nowMicros - _window >= 1_000_000 || nowMicros < _window)
        {
            _window = nowMicros;
            _taken = 0;
        }

        if (_taken >= _perSecond)
        {
            Deferred++;
            return false;
        }

        _taken++;
        Made++;
        return true;
    }

    /// <summary>Forgets the window and the counts (playback starting).</summary>
    public void Reset()
    {
        _window = long.MinValue;
        _taken = 0;
        Made = Deferred = 0;
    }

    private static bool Contains(IEnumerable<ChainLink> links, ChainLink link)
    {
        foreach (ChainLink other in links)
        {
            if (other == link)
            {
                return true;
            }
        }

        return false;
    }
}
