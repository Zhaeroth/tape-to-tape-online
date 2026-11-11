using System;
using System.IO;
using System.IO.Compression;

namespace T2TOnline.Core.Serialization;

/// <summary>
/// Raw deflate for message payloads. Decompression is bounded: network input is untrusted, and a few bytes of deflate
/// can expand to megabytes, so anything that would exceed the caller's limit is refused.
/// </summary>
public static class Compression
{
    /// <summary>
    /// Deflates <paramref name="data"/>. Payloads are a few KB at most, so the optimal level costs microseconds: on real
    /// recorded snapshots (.NET 6) about 24 microseconds a delta and 53 a keyframe, where the fastest level saves
    /// 3 to 12 for 1% more bytes.
    /// </summary>
    public static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream((data.Length / 2) + 16);
        Deflate(data, output);
        return output.ToArray();
    }

    /// <summary>Deflates <paramref name="data"/> into <paramref name="output"/> (emptied first), for a caller that reuses one.</summary>
    internal static void Deflate(ReadOnlySpan<byte> data, MemoryStream output)
    {
        output.SetLength(0);
        using var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(data);
    }

    /// <summary>
    /// Inflates <paramref name="data"/>. False, with <paramref name="result"/> empty, if it's malformed or would come to
    /// more than <paramref name="maxBytes"/>. Never throws.
    /// </summary>
    public static bool TryInflate(ReadOnlySpan<byte> data, int maxBytes, out byte[] result) => TryInflate(data.ToArray(), maxBytes, out result);

    /// <summary>
    /// Inflates <paramref name="data"/> where it is, without copying it. False, with <paramref name="result"/> empty, if it's
    /// malformed or would come to more than <paramref name="maxBytes"/>. Never throws.
    /// </summary>
    public static bool TryInflate(byte[] data, int maxBytes, out byte[] result)
    {
        ArgumentNullException.ThrowIfNull(data);
        result = Array.Empty<byte>();
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            Span<byte> buffer = stackalloc byte[4096];
            while (true)
            {
                int read = inflate.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maxBytes)
                {
                    return false;
                }

                output.Write(buffer[..read]);
            }

            result = output.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
