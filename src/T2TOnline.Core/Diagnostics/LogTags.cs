using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// The names logs give players and lobbies instead of their Steam IDs (D127): <c>P#a0c1d2</c> for a player and
/// <c>L#7d20e1</c> for a lobby, the first hex digits of an HMAC-SHA256 of the ID under a key. Every PC in a lobby uses the
/// key its host publishes for the hosting session (<see cref="TryParseKey"/>), so the same player has the same tag in every
/// PC's log of that lobby; outside a lobby each launch has its own random key. Without the key a tag can't be turned back
/// into an ID, nor checked against a known one (a plain hash could: a SteamID64 holds a 32-bit account number, and lobby
/// IDs advance a few million an hour). The key itself is never logged. Thread-safe.
/// </summary>
public sealed class LogTags
{
    /// <summary>The key's length in bytes.</summary>
    public const int KeyBytes = 16;

    /// <summary>The key's length as lobby data: two lowercase hex digits a byte.</summary>
    public const int KeyHexLength = KeyBytes * 2;

    /// <summary>A player tag's hex digits.</summary>
    public const int ShortDigits = 6;

    /// <summary>A player tag's hex digits in a lobby where two members' short tags are the same.</summary>
    public const int LongDigits = 8;

    /// <summary>What stands for a player when a line can't say which one (a name two players share).</summary>
    public const string UnknownPlayer = "P#??????";

    // Domain prefixes, so a player's tag and a lobby's are never the same function of the same number.
    private static readonly byte[] PlayerPrefix = "t2t-log-tag/1"u8.ToArray();
    private static readonly byte[] LobbyPrefix = "t2t-log-lobby/1"u8.ToArray();

    private readonly byte[] _key;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, string> _hex = new();
    private readonly Dictionary<ulong, string> _lobbies = new();
    private readonly HashSet<ulong> _members = new();
    private int _digits = ShortDigits;

    /// <summary>Tags under <paramref name="key"/> (<see cref="KeyBytes"/> bytes).</summary>
    /// <param name="key">The key: the host's for its lobby, or a launch's own.</param>
    /// <param name="shared">True when the key is the host's (the tags match the other PCs' logs of the lobby).</param>
    public LogTags(byte[] key, bool shared)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"A log tag key is {KeyBytes} bytes.", nameof(key));
        }

        _key = (byte[])key.Clone();
        Shared = shared;
    }

    /// <summary>True when the key came from the lobby's host, so the other PCs' logs use the same tags.</summary>
    public bool Shared { get; }

    /// <summary>The player tags' hex digits now: <see cref="ShortDigits"/>, or <see cref="LongDigits"/> once two members' collided.</summary>
    public int Digits
    {
        get
        {
            lock (_gate)
            {
                return _digits;
            }
        }
    }

    /// <summary>Tags under a new random key, for a launch outside any lobby (or a host that published no key).</summary>
    public static LogTags ForLaunch() => new(NewKey(), shared: false);

    /// <summary>A new random key (<see cref="RandomNumberGenerator"/>), made once per hosting session.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary><paramref name="key"/> as lobby data: <see cref="KeyHexLength"/> lowercase hex digits.</summary>
    public static string ToHex(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Hex(key, key.Length * 2);
    }

    /// <summary>
    /// Reads a key a host published: exactly <see cref="KeyHexLength"/> hex digits (either case), nothing else. Lobby data
    /// is untrusted: anything else is refused, and the caller falls back to a launch's own key.
    /// </summary>
    public static bool TryParseKey(string? text, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (text == null || text.Length != KeyHexLength)
        {
            return false;
        }

        var bytes = new byte[KeyBytes];
        for (int i = 0; i < KeyBytes; i++)
        {
            int high = HexValue(text[2 * i]);
            int low = HexValue(text[(2 * i) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            bytes[i] = (byte)((high << 4) | low);
        }

        key = bytes;
        return true;
    }

    /// <summary>True when <paramref name="hex"/> is this key written in hex (either case): a log line must never hold it.</summary>
    public bool IsKey(ReadOnlySpan<char> hex)
    {
        if (hex.Length != KeyHexLength)
        {
            return false;
        }

        for (int i = 0; i < KeyBytes; i++)
        {
            int high = HexValue(hex[2 * i]);
            int low = HexValue(hex[(2 * i) + 1]);
            if (high < 0 || low < 0 || (byte)((high << 4) | low) != _key[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The player tag of <paramref name="steamId"/>: <c>P#</c> and <see cref="Digits"/> hex digits.</summary>
    public string Player(ulong steamId)
    {
        lock (_gate)
        {
            return string.Concat("P#", PlayerHex(steamId).AsSpan(0, _digits));
        }
    }

    /// <summary>The lobby tag of <paramref name="lobbyId"/>: <c>L#</c> and <see cref="ShortDigits"/> hex digits.</summary>
    public string Lobby(ulong lobbyId)
    {
        lock (_gate)
        {
            if (!_lobbies.TryGetValue(lobbyId, out string? tag))
            {
                tag = "L#" + Hex(Mac(LobbyPrefix, lobbyId), ShortDigits);
                Remember(_lobbies, lobbyId, tag);
            }

            return tag;
        }
    }

    /// <summary>
    /// Adds the lobby's members as both PCs see them (the Steam lobby's member list). When two members' short tags are the
    /// same, every player tag gets <see cref="LongDigits"/> digits for the rest of the lobby; true when that happened now.
    /// </summary>
    public bool AddMembers(IEnumerable<ulong> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        bool widened = false;
        lock (_gate)
        {
            foreach (ulong member in members)
            {
                if (member == 0 || _members.Contains(member))
                {
                    continue;
                }

                string hex = PlayerHex(member);
                if (_digits == ShortDigits)
                {
                    foreach (ulong other in _members)
                    {
                        if (string.CompareOrdinal(PlayerHex(other), 0, hex, 0, ShortDigits) == 0)
                        {
                            _digits = LongDigits;
                            widened = true;
                            break;
                        }
                    }
                }

                _members.Add(member);
            }
        }

        return widened;
    }

    private static void Remember(Dictionary<ulong, string> cache, ulong id, string value)
    {
        if (cache.Count >= 4096)
        {
            cache.Clear(); // IDs a log names are few; this only bounds a flood
        }

        cache[id] = value;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static string Hex(byte[] bytes, int digits)
    {
        const string HexDigits = "0123456789abcdef";
        return string.Create(digits, bytes, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                byte b = source[i / 2];
                span[i] = HexDigits[(i % 2 == 0 ? b >> 4 : b) & 0xF];
            }
        });
    }

    private string PlayerHex(ulong steamId)
    {
        lock (_gate)
        {
            if (_hex.TryGetValue(steamId, out string? known))
            {
                return known;
            }

            string hex = Hex(Mac(PlayerPrefix, steamId), LongDigits);
            if (_hex.Count >= 4096)
            {
                // Members' entries are needed for collisions; everyone else's can go.
                foreach (ulong id in new List<ulong>(_hex.Keys))
                {
                    if (!_members.Contains(id))
                    {
                        _hex.Remove(id);
                    }
                }
            }

            _hex[steamId] = hex;
            return hex;
        }
    }

    private byte[] Mac(byte[] prefix, ulong id)
    {
        var message = new byte[prefix.Length + sizeof(ulong)];
        prefix.CopyTo(message, 0);
        for (int i = 0; i < sizeof(ulong); i++)
        {
            message[prefix.Length + i] = (byte)(id >> (8 * i)); // little-endian on every PC
        }

        return HMACSHA256.HashData(_key, message);
    }
}
