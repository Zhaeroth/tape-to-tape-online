using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace T2TOnline.Steam.Native;

/// <summary>UTF-8 C strings to and from Steam. Never throws: bad input gives null or an empty string.</summary>
internal static class NativeStrings
{
    /// <summary>
    /// <paramref name="value"/> as a null-terminated UTF-8 byte array, or null if it is null or contains a NUL
    /// (which would silently truncate it on the native side).
    /// </summary>
    public static byte[]? ToUtf8(string? value)
    {
        if (value is null || value.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }

    /// <summary>The UTF-8 C string at <paramref name="pointer"/>, or an empty string for a null pointer.</summary>
    public static string FromPointer(nint pointer) => pointer == 0 ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;

    /// <summary>The string in a fixed-size char buffer: up to the first NUL, or the whole buffer if there is none.</summary>
    public static string FromBuffer(ReadOnlySpan<byte> buffer)
    {
        int end = buffer.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? buffer : buffer[..end]);
    }

    /// <summary>The string in the fixed-size char array at <paramref name="offset"/> inside an interop struct.</summary>
    public static string FromStructBuffer<T>(in T value, int offset, int length)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1));
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
        {
            return string.Empty;
        }

        return FromBuffer(bytes.Slice(offset, length));
    }
}
