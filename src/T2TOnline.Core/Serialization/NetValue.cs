using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Serialization;

/// <summary>What a <see cref="NetValue"/> holds.</summary>
public enum NetValueKind : byte
{
    /// <summary>Nothing (a null reference or an unsupported value).</summary>
    Null = 0,

    /// <summary>A bool.</summary>
    Flag = 1,

    /// <summary>A whole number (any integer type or enum).</summary>
    Whole = 2,

    /// <summary>A float.</summary>
    Single32 = 3,

    /// <summary>A string.</summary>
    Text = 4,

    /// <summary>Two floats.</summary>
    Vector2 = 5,

    /// <summary>Three floats.</summary>
    Vector3 = 6,

    /// <summary>Four floats: a rotation.</summary>
    Rotation = 7,

    /// <summary>A reference to a game object by network id.</summary>
    Reference = 8,

    /// <summary>Raw bytes (an FMOD GUID, say).</summary>
    Blob = 9,

    /// <summary>Four floats: a colour.</summary>
    Colour = 10,

    /// <summary>A double.</summary>
    Double64 = 11,
}

/// <summary>
/// One value of a game event argument or a HUD field, tagged with its kind, so events and HUD state can be carried
/// generically: the mod reads which fields an event's argument has from the running build, not from code compiled
/// into it. Decoding never throws.
/// </summary>
public readonly struct NetValue : IEquatable<NetValue>
{
    /// <summary>Longest string carried, in UTF-8 bytes.</summary>
    public const int MaxStringBytes = 255;

    /// <summary>Longest byte value carried.</summary>
    public const int MaxBytes = 1024;

    private NetValue(NetValueKind kind, long integer = 0, double number = 0, float x = 0, float y = 0, float z = 0, float w = 0, string? text = null, byte[]? data = null)
    {
        Kind = kind;
        WholeValue = integer;
        Number = number;
        X = x;
        Y = y;
        Z = z;
        W = w;
        Text = text;
        Data = data;
    }

    /// <summary>A null value.</summary>
    public static NetValue Null => default;

    /// <summary>What this holds.</summary>
    public NetValueKind Kind { get; }

    /// <summary>The value for <see cref="NetValueKind.Whole"/>, <see cref="NetValueKind.Flag"/> (0 or 1) and <see cref="NetValueKind.Reference"/>.</summary>
    public long WholeValue { get; }

    /// <summary>The value for <see cref="NetValueKind.Single32"/> and <see cref="NetValueKind.Double64"/>.</summary>
    public double Number { get; }

    /// <summary>First component of a vector, quaternion or colour.</summary>
    public float X { get; }

    /// <summary>Second component.</summary>
    public float Y { get; }

    /// <summary>Third component.</summary>
    public float Z { get; }

    /// <summary>Fourth component.</summary>
    public float W { get; }

    /// <summary>The value for <see cref="NetValueKind.Text"/>.</summary>
    public string? Text { get; }

    /// <summary>The value for <see cref="NetValueKind.Blob"/>.</summary>
    public byte[]? Data { get; }

    /// <summary>A bool.</summary>
    public static NetValue FromBool(bool value) => new(NetValueKind.Flag, value ? 1 : 0);

    /// <summary>A whole number.</summary>
    public static NetValue FromInt(long value) => new(NetValueKind.Whole, value);

    /// <summary>A float.</summary>
    public static NetValue FromFloat(float value) => new(NetValueKind.Single32, number: value);

    /// <summary>A double.</summary>
    public static NetValue FromDouble(double value) => new(NetValueKind.Double64, number: value);

    /// <summary>A string; null becomes <see cref="Null"/>.</summary>
    public static NetValue FromString(string? value) => value == null ? Null : new(NetValueKind.Text, text: value);

    /// <summary>A 2D vector.</summary>
    public static NetValue FromVector2(float x, float y) => new(NetValueKind.Vector2, x: x, y: y);

    /// <summary>A 3D vector.</summary>
    public static NetValue FromVector3(float x, float y, float z) => new(NetValueKind.Vector3, x: x, y: y, z: z);

    /// <summary>A rotation.</summary>
    public static NetValue FromQuaternion(float x, float y, float z, float w) => new(NetValueKind.Rotation, x: x, y: y, z: z, w: w);

    /// <summary>A colour.</summary>
    public static NetValue FromColor(float r, float g, float b, float a) => new(NetValueKind.Colour, x: r, y: g, z: b, w: a);

    /// <summary>A reference to the game object with network id <paramref name="netId"/>.</summary>
    public static NetValue FromRef(ushort netId) => new(NetValueKind.Reference, netId);

    /// <summary>Raw bytes (copied).</summary>
    public static NetValue FromBytes(ReadOnlySpan<byte> value) => new(NetValueKind.Blob, data: value.ToArray());

    /// <summary>Writes the value.</summary>
    public void Write(BitWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBits((ulong)Kind, 4);
        switch (Kind)
        {
            case NetValueKind.Flag:
                writer.WriteBool(WholeValue != 0);
                break;
            case NetValueKind.Whole:
                writer.WriteVarInt(WholeValue);
                break;
            case NetValueKind.Reference:
                writer.WriteVarUInt((ulong)WholeValue);
                break;
            case NetValueKind.Single32:
                writer.WriteSingle((float)Number);
                break;
            case NetValueKind.Double64:
                writer.WriteBits(unchecked((ulong)BitConverter.DoubleToInt64Bits(Number)), 64);
                break;
            case NetValueKind.Text:
                writer.WriteShortString(Text ?? string.Empty, MaxStringBytes);
                break;
            case NetValueKind.Blob:
                byte[] data = Data ?? Array.Empty<byte>();
                int length = Math.Min(data.Length, MaxBytes);
                writer.WriteVarUInt((ulong)length);
                writer.WriteBytes(data.AsSpan(0, length));
                break;
            case NetValueKind.Vector2:
                writer.WriteSingle(X);
                writer.WriteSingle(Y);
                break;
            case NetValueKind.Vector3:
                writer.WriteSingle(X);
                writer.WriteSingle(Y);
                writer.WriteSingle(Z);
                break;
            case NetValueKind.Rotation:
            case NetValueKind.Colour:
                writer.WriteSingle(X);
                writer.WriteSingle(Y);
                writer.WriteSingle(Z);
                writer.WriteSingle(W);
                break;
        }
    }

    /// <summary>Reads a value. False for unknown kinds or truncated data.</summary>
    public static bool TryRead(ref BitReader reader, out NetValue value)
    {
        var kind = (NetValueKind)reader.ReadBits(4);
        switch (kind)
        {
            case NetValueKind.Null:
                value = Null;
                break;
            case NetValueKind.Flag:
                value = FromBool(reader.ReadBool());
                break;
            case NetValueKind.Whole:
                value = FromInt(reader.ReadVarInt());
                break;
            case NetValueKind.Reference:
                ulong id = reader.ReadVarUInt();
                value = id <= ushort.MaxValue ? FromRef((ushort)id) : Null;
                if (id > ushort.MaxValue)
                {
                    return false;
                }

                break;
            case NetValueKind.Single32:
                value = FromFloat(reader.ReadSingle());
                break;
            case NetValueKind.Double64:
                value = FromDouble(BitConverter.Int64BitsToDouble(unchecked((long)reader.ReadBits(64))));
                break;
            case NetValueKind.Text:
                value = FromString(reader.ReadShortString(MaxStringBytes));
                break;
            case NetValueKind.Blob:
                ulong length = reader.ReadVarUInt();
                if (length > MaxBytes || (long)length * 8 > reader.RemainingBits)
                {
                    value = Null;
                    return false;
                }

                byte[] data = new byte[(int)length];
                reader.ReadBytes(data);
                value = new NetValue(NetValueKind.Blob, data: data);
                break;
            case NetValueKind.Vector2:
                value = FromVector2(reader.ReadSingle(), reader.ReadSingle());
                break;
            case NetValueKind.Vector3:
                value = FromVector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                break;
            case NetValueKind.Rotation:
                value = FromQuaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                break;
            case NetValueKind.Colour:
                value = FromColor(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                break;
            default:
                value = Null;
                return false;
        }

        return !reader.IsOverrun;
    }

    /// <summary>Writes a list of values, count first.</summary>
    public static void WriteList(BitWriter writer, IReadOnlyList<NetValue> values)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(values);
        writer.WriteVarUInt((ulong)values.Count);
        foreach (NetValue value in values)
        {
            value.Write(writer);
        }
    }

    /// <summary>Reads a list written by <see cref="WriteList"/>, at most <paramref name="maxCount"/> long.</summary>
    public static bool TryReadList(ref BitReader reader, List<NetValue> values, int maxCount = 256)
    {
        ArgumentNullException.ThrowIfNull(values);
        ulong count = reader.ReadVarUInt();
        if (count > (ulong)maxCount)
        {
            return false;
        }

        for (ulong i = 0; i < count; i++)
        {
            if (!TryRead(ref reader, out NetValue value))
            {
                return false;
            }

            values.Add(value);
        }

        return !reader.IsOverrun;
    }

    /// <inheritdoc />
    public bool Equals(NetValue other) =>
        Kind == other.Kind && WholeValue == other.WholeValue && Number.Equals(other.Number) && X.Equals(other.X) && Y.Equals(other.Y) &&
        Z.Equals(other.Z) && W.Equals(other.W) && Text == other.Text &&
        (Data == null ? other.Data == null : other.Data != null && Data.AsSpan().SequenceEqual(other.Data));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NetValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, WholeValue, Number, X, Y, Z, W, Text);

    /// <summary>Value equality.</summary>
    public static bool operator ==(NetValue left, NetValue right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(NetValue left, NetValue right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        NetValueKind.Null => "null",
        NetValueKind.Flag => WholeValue != 0 ? "true" : "false",
        NetValueKind.Whole => WholeValue.ToString(CultureInfo.InvariantCulture),
        NetValueKind.Reference => $"#{WholeValue}",
        NetValueKind.Single32 or NetValueKind.Double64 => Number.ToString("0.###", CultureInfo.InvariantCulture),
        NetValueKind.Text => $"\"{Text}\"",
        NetValueKind.Blob => $"{Data?.Length ?? 0} bytes",
        NetValueKind.Vector2 => string.Create(CultureInfo.InvariantCulture, $"({X:0.##}, {Y:0.##})"),
        NetValueKind.Vector3 => string.Create(CultureInfo.InvariantCulture, $"({X:0.##}, {Y:0.##}, {Z:0.##})"),
        _ => string.Create(CultureInfo.InvariantCulture, $"({X:0.##}, {Y:0.##}, {Z:0.##}, {W:0.##})"),
    };
}
