using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Stream;

/// <summary>What a <c>.t2tstream</c> file says about itself.</summary>
/// <param name="ProtocolVersion">The <see cref="ProtocolInfo.Version"/> that wrote it.</param>
/// <param name="ModVersion">The mod version that wrote it.</param>
/// <param name="GameBuild">The first 8 bytes of the recording game's GameAssembly.dll SHA-256.</param>
/// <param name="CreatedUtcTicks">When recording started (UTC, .NET ticks).</param>
/// <param name="Description">Free text: what was recorded.</param>
public sealed record MatchStreamHeader(byte ProtocolVersion, string ModVersion, ulong GameBuild, long CreatedUtcTicks, string Description);

/// <summary>One record of a stream: a protocol message exactly as the host would send it, and when.</summary>
/// <param name="TimeMicros">The host's clock when it was sent, in microseconds since recording started.</param>
/// <param name="Channel">The channel it would go on.</param>
/// <param name="Message">The message bytes (header and payload).</param>
public readonly record struct MatchStreamRecord(long TimeMicros, Channel Channel, byte[] Message);

/// <summary>
/// Writes a <c>.t2tstream</c>: everything a host sends a friend (snapshots, events, sounds, HUD, match setup), with
/// timestamps, so one PC can later play it through client mode exactly as a friend's PC would receive it.
/// </summary>
/// <remarks>
/// Layout, little-endian: the 8 bytes <c>T2TSTRM1</c>; the header (protocol version byte, mod version and description
/// as length-prefixed UTF-8, game build u64, created ticks i64); then records, each a u32 length of what follows, an i64
/// time in microseconds, a channel byte, and the message. A file cut off mid-record (the game crashed) reads cleanly up
/// to the last whole record.
/// </remarks>
public sealed class MatchStreamWriter : IDisposable
{
    /// <summary>The file's first 8 bytes.</summary>
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("T2TSTRM1");

    /// <summary>What a record's length counts besides the message: the time (8 bytes) and the channel (1).</summary>
    internal const int RecordOverhead = 8 + 1;

    private readonly BinaryWriter _writer;

    /// <summary>Starts a stream on <paramref name="output"/>, which the writer then owns.</summary>
    public MatchStreamWriter(System.IO.Stream output, MatchStreamHeader header)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(header);
        _writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: false);
        _writer.Write(Magic);
        _writer.Write(header.ProtocolVersion);
        WriteString(_writer, header.ModVersion);
        _writer.Write(header.GameBuild);
        _writer.Write(header.CreatedUtcTicks);
        WriteString(_writer, header.Description);
    }

    /// <summary>Records written.</summary>
    public long Records { get; private set; }

    /// <summary>Messages refused for being too long for a record (<see cref="MatchStreamReader.MaxRecordBytes"/>).</summary>
    public long Refused { get; private set; }

    /// <summary>Bytes written, header included.</summary>
    public long Bytes => _writer.BaseStream.Position;

    /// <summary>
    /// Appends one message. False, with nothing written, for one too long for a record
    /// (<see cref="MatchStreamReader.MaxRecordBytes"/>): the reader would stop there and lose the rest of the file.
    /// </summary>
    public bool Write(long timeMicros, Channel channel, ReadOnlySpan<byte> message)
    {
        if (message.Length > MatchStreamReader.MaxRecordBytes - RecordOverhead)
        {
            Refused++;
            return false;
        }

        _writer.Write((uint)(RecordOverhead + message.Length));
        _writer.Write(timeMicros);
        _writer.Write((byte)channel);
        _writer.Write(message);
        Records++;
        return true;
    }

    /// <summary>Pushes buffered records to the file.</summary>
    public void Flush() => _writer.Flush();

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ushort)Math.Min(bytes.Length, ushort.MaxValue));
        writer.Write(bytes, 0, Math.Min(bytes.Length, ushort.MaxValue));
    }
}

/// <summary>Reads a <c>.t2tstream</c> written by <see cref="MatchStreamWriter"/>. Never throws on bad data.</summary>
public sealed class MatchStreamReader : IDisposable
{
    /// <summary>Largest record accepted, in bytes.</summary>
    public const int MaxRecordBytes = 4 * 1024 * 1024;

    private readonly BinaryReader _reader;

    private MatchStreamReader(BinaryReader reader, MatchStreamHeader header)
    {
        _reader = reader;
        Header = header;
    }

    /// <summary>The stream's header.</summary>
    public MatchStreamHeader Header { get; }

    /// <summary>
    /// True when reading stopped before the end of the file: it ended partway through a record (the recording was cut off),
    /// a record's length was corrupt, or the file couldn't be read (<see cref="ReadError"/>).
    /// </summary>
    public bool Truncated { get; private set; }

    /// <summary>Why the file couldn't be read to the end (an I/O error, as <see cref="SafeError.Text(Exception?)"/> words it), or empty.</summary>
    public string ReadError { get; private set; } = string.Empty;

    /// <summary>Opens a stream. False, with <paramref name="problem"/> saying why, if it isn't a readable stream file.</summary>
    public static bool TryOpen(System.IO.Stream input, [NotNullWhen(true)] out MatchStreamReader? reader, out string problem)
    {
        ArgumentNullException.ThrowIfNull(input);
        reader = null;
        var binary = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
        try
        {
            byte[] magic = binary.ReadBytes(MatchStreamWriter.Magic.Length);
            if (!magic.AsSpan().SequenceEqual(MatchStreamWriter.Magic))
            {
                problem = "not a T2T Online stream (wrong magic)";
                binary.Dispose();
                return false;
            }

            byte protocol = binary.ReadByte();
            string mod = ReadString(binary);
            ulong build = binary.ReadUInt64();
            long created = binary.ReadInt64();
            string description = ReadString(binary);
            reader = new MatchStreamReader(binary, new MatchStreamHeader(protocol, mod, build, created, description));
            problem = string.Empty;
            return true;
        }
        catch (EndOfStreamException)
        {
            problem = "the header is cut off";
            binary.Dispose();
            return false;
        }
        catch (IOException ex)
        {
            // The message of a file's error holds its full path: only the folder's label and the file name go on.
            problem = $"it couldn't be read ({SafeError.Text(ex)})";
            binary.Dispose();
            return false;
        }
    }

    /// <summary>Reads the next record. False at the end of the file, or at a cut-off, corrupt or unreadable one (<see cref="Truncated"/>).</summary>
    public bool TryRead(out MatchStreamRecord record)
    {
        record = default;
        try
        {
            if (_reader.BaseStream.Position >= _reader.BaseStream.Length)
            {
                return false;
            }

            uint length = _reader.ReadUInt32();
            if (length < MatchStreamWriter.RecordOverhead || length > MaxRecordBytes || _reader.BaseStream.Length - _reader.BaseStream.Position < length)
            {
                Truncated = true;
                return false;
            }

            long time = _reader.ReadInt64();
            var channel = (Channel)_reader.ReadByte();
            byte[] message = _reader.ReadBytes((int)length - MatchStreamWriter.RecordOverhead);
            record = new MatchStreamRecord(time, channel, message);
            return true;
        }
        catch (EndOfStreamException)
        {
            Truncated = true;
            return false;
        }
        catch (IOException ex)
        {
            Truncated = true;
            ReadError = SafeError.Text(ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _reader.Dispose();

    private static string ReadString(BinaryReader reader)
    {
        ushort length = reader.ReadUInt16();
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
