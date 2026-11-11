using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// What a log may say about text a player wrote (a custom team's, skater's, goalie's or logo's name) instead of the text
/// (D127): its length and a short hash, <c>12 characters, #4be1</c>. The hash is keyed by a random key per launch, so one
/// log can tell two names apart and see the same one come back, but nobody can confirm a guessed name from it, and two
/// PCs' logs give the same text different hashes.
/// </summary>
public sealed class PlayerTextShape
{
    /// <summary>How many bytes the key has.</summary>
    public const int KeyBytes = 16;

    /// <summary>How many hex digits of the hash a line shows.</summary>
    public const int HashDigits = 4;

    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("t2t-log-text/1");

    private readonly byte[] _key;

    /// <summary>A shape writer under <paramref name="key"/> (<see cref="KeyBytes"/> bytes, copied).</summary>
    public PlayerTextShape(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"A text shape key is {KeyBytes} bytes, not {key.Length}.", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    /// <summary>A shape writer under a new random key: one per launch.</summary>
    public static PlayerTextShape ForLaunch() => new(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>
    /// <paramref name="text"/>'s shape: <c>12 characters, #4be1</c> (characters as a reader counts them: an emoji or a letter
    /// with its accents is one), <c>empty</c> for null or empty text. Never throws.
    /// </summary>
    public string Describe(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "empty";
        }

        try
        {
            int characters = new StringInfo(text).LengthInTextElements;
            return string.Create(CultureInfo.InvariantCulture, $"{characters} character{(characters == 1 ? string.Empty : "s")}, #{Hash(text)}");
        }
        catch (ArgumentException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{text.Length} UTF-16 units, not hashed");
        }
    }

    private string Hash(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] message = new byte[Domain.Length + utf8.Length];
        Domain.CopyTo(message, 0);
        utf8.CopyTo(message, Domain.Length);
        byte[] mac = HMACSHA256.HashData(_key, message);
        const string Digits = "0123456789abcdef";
        var hex = new StringBuilder(HashDigits);
        for (int i = 0; i < HashDigits / 2; i++)
        {
            hex.Append(Digits[mac[i] >> 4]).Append(Digits[mac[i] & 0xF]);
        }

        return hex.ToString();
    }
}
