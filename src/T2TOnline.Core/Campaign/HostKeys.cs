using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The keys a friend's progress ledger knows hosts by (D135): an HMAC-SHA256 of the host's SteamID64 under a secret only
/// this PC holds (<see cref="SecretBytes"/> random bytes, kept in a file of its own), cut to <see cref="TagBytes"/> bytes
/// as lowercase hex. The same host always gets the same key here, so its 24-hour budget holds across its hosting
/// sessions, yet the ledger names nobody: without the secret a key can't be matched to an ID, not even by trying every
/// account. Game-independent.
/// </summary>
public static class HostKeys
{
    /// <summary>The secret's length, in bytes.</summary>
    public const int SecretBytes = 32;

    /// <summary>A key's length, in bytes (twice that in hex digits).</summary>
    public const int TagBytes = 16;

    /// <summary>A new random secret.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>
    /// The secret a key file holds (<paramref name="file"/>: its bytes; null when there's none). True when it holds exactly
    /// <see cref="SecretBytes"/> bytes that aren't all zero; otherwise false, with <paramref name="secret"/> a new one to keep
    /// in its place and <paramref name="why"/> saying what was wrong ("none yet" for a missing file).
    /// </summary>
    public static bool TryRead(byte[]? file, out byte[] secret, out string why)
    {
        if (file != null && file.Length == SecretBytes && Array.Exists(file, b => b != 0))
        {
            secret = (byte[])file.Clone();
            why = string.Empty;
            return true;
        }

        secret = NewSecret();
        why = file == null ? "none yet" : file.Length != SecretBytes ? $"{file.Length} bytes, not {SecretBytes}" : "all zero";
        return false;
    }

    /// <summary>The key of host <paramref name="steamId"/> under <paramref name="secret"/>.</summary>
    public static string Tag(byte[] secret, ulong steamId)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException($"A secret of {SecretBytes} bytes.", nameof(secret));
        }

        Span<byte> id = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(id, steamId);
        Span<byte> hash = stackalloc byte[32]; // SHA-256's size
        HMACSHA256.HashData(secret, id, hash);
        return Convert.ToHexString(hash[..TagBytes]).ToLowerInvariant();
    }

    /// <summary>
    /// The id this PC keeps hosting session <paramref name="session"/> of host <paramref name="steamId"/> under in its own
    /// records (the shared play, D135), under <paramref name="secret"/>: the same pair always gets the same id here, the
    /// record names no host, and another host naming the same session (a modified host can name any) gets another id, so it
    /// inherits nothing of what this PC shared with the first. Never 0, but 0 for session 0 (none).
    /// </summary>
    public static ulong SessionTag(byte[] secret, ulong steamId, ulong session)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException($"A secret of {SecretBytes} bytes.", nameof(secret));
        }

        if (session == 0)
        {
            return 0;
        }

        // A tag of its own: 1 + 8 + 8 bytes, so it never equals a host key's input (8 bytes).
        Span<byte> pair = stackalloc byte[1 + (2 * sizeof(ulong))];
        pair[0] = (byte)'S';
        BinaryPrimitives.WriteUInt64LittleEndian(pair[1..], steamId);
        BinaryPrimitives.WriteUInt64LittleEndian(pair[(1 + sizeof(ulong))..], session);
        Span<byte> hash = stackalloc byte[32]; // SHA-256's size
        HMACSHA256.HashData(secret, pair, hash);
        ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        return tag != 0 ? tag : 1;
    }
}
