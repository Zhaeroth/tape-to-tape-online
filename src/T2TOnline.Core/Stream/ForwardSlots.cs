using System;
using System.Globalization;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Stream;

/// <summary>
/// A host's per-skater state as HUD values (<c>&lt;prefix&gt;who.N</c>: the skater, a reference; <c>&lt;prefix&gt;is.N</c>: its
/// value), read on a friend's PC with each value checked: the frogged skaters with their frog time left
/// (<see cref="Frogs"/>), the zapped ones with the skater each was turned into (<see cref="Zaps"/>). A slot with no
/// skater is empty. Never throws; a bad value is refused and changes nothing.
/// </summary>
public sealed class ForwardSlots
{
    /// <summary>Most slots of one kind.</summary>
    public const int MaxSlots = 16;

    /// <summary>The longest skater asset id taken as a zap's form.</summary>
    public const int MaxIdLength = 64;

    /// <summary>The most frog seconds taken.</summary>
    public const int MaxFrogSeconds = 999;

    private readonly string[] _whoKeys = new string[MaxSlots];
    private readonly string[] _valueKeys = new string[MaxSlots];
    private readonly NetValue[] _whos = new NetValue[MaxSlots];
    private readonly NetValue[] _values = new NetValue[MaxSlots];
    private readonly Func<NetValue, bool> _valid;

    private ForwardSlots(string prefix, Func<NetValue, bool> valid)
    {
        Prefix = prefix;
        _valid = valid;
        for (int i = 0; i < MaxSlots; i++)
        {
            _whoKeys[i] = prefix + "who." + i.ToString(CultureInfo.InvariantCulture);
            _valueKeys[i] = prefix + "is." + i.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What every key of this kind starts with.</summary>
    public string Prefix { get; }

    /// <summary>True when the last message changed a slot.</summary>
    public bool Changed { get; private set; }

    /// <summary>Values refused for their kind or range.</summary>
    public int Refused { get; private set; }

    /// <summary>The frogged skaters (<c>frog.</c>): value whole, the seconds of frog left (0 to <see cref="MaxFrogSeconds"/>).</summary>
    public static ForwardSlots Frogs() => new("frog.", v => v.Kind == NetValueKind.Whole && v.WholeValue is >= 0 and <= MaxFrogSeconds);

    /// <summary>The zapped skaters (<c>zap.</c>): value text, the skater asset id each is now (an id's characters only).</summary>
    public static ForwardSlots Zaps() => new("zap.", v => v.Kind == NetValueKind.Text && IsId(v.Text));

    /// <summary>The key of slot <paramref name="index"/>'s skater.</summary>
    public string WhoKey(int index) => _whoKeys[index];

    /// <summary>The key of slot <paramref name="index"/>'s value.</summary>
    public string ValueKey(int index) => _valueKeys[index];

    /// <summary>Slot <paramref name="index"/>: its skater (a reference) and value, or false when it holds none.</summary>
    public bool TryGet(int index, out NetValue who, out NetValue value)
    {
        bool inRange = index >= 0 && index < MaxSlots;
        who = inRange ? _whos[index] : default;
        value = inRange ? _values[index] : default;
        return inRange && who.Kind == NetValueKind.Reference && value.Kind != NetValueKind.Null;
    }

    /// <summary>Reads one HUD value of this kind. True when the key is this kind's (taken or refused).</summary>
    public bool Take(string key, NetValue value)
    {
        if (key == null || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < MaxSlots; i++)
        {
            if (key == _whoKeys[i])
            {
                if (value.Kind is NetValueKind.Reference or NetValueKind.Null or NetValueKind.Text)
                {
                    Changed |= _whos[i] != value;
                    _whos[i] = value;
                }
                else
                {
                    Refused++;
                }

                return true;
            }

            if (key == _valueKeys[i])
            {
                if (value.Kind == NetValueKind.Null || _valid(value))
                {
                    Changed |= _values[i] != value;
                    _values[i] = value;
                }
                else
                {
                    Refused++;
                }

                return true;
            }
        }

        // An unknown key of this kind (a newer host): taken and ignored.
        return true;
    }

    /// <summary>After a message.</summary>
    public void EndMessage() => Changed = false;

    /// <summary>Forgets everything (a new playback).</summary>
    public void Reset()
    {
        Array.Clear(_whos);
        Array.Clear(_values);
        Changed = false;
        Refused = 0;
    }

    private static bool IsId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
