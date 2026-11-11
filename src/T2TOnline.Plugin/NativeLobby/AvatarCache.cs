#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Steam;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>Which of Steam's avatar images the lobby wants: small and medium for lists, large for a seat.</summary>
internal enum AvatarSize
{
    /// <summary>Steam's small avatar (32 x 32 in practice).</summary>
    Small = 0,

    /// <summary>Steam's medium avatar (64 x 64 in practice): friends lists and the browser's rows.</summary>
    Medium = 1,

    /// <summary>Steam's large avatar (184 x 184 in practice): a seat in the lobby.</summary>
    Large = 2,
}

/// <summary>Where an avatar is on its way to the screen (<see cref="AvatarCache.Get(ulong, AvatarSize, out AvatarStatus)"/>).</summary>
internal enum AvatarStatus
{
    /// <summary>Steam hasn't given the image yet, or it is being read: show the placeholder and ask again later.</summary>
    Loading,

    /// <summary>There is a texture.</summary>
    Ready,

    /// <summary>There is no avatar to show: the player has none, Steam never gave one, or Steam can't be asked. Show the game's art.</summary>
    None,
}

/// <summary>
/// Steam avatars as Unity textures for the native lobby: a seat's picture, a browser row's, an invite list's. Steam says
/// nothing when an avatar is ready (this mod registers no callbacks, D20), so <see cref="SteamAvatarPoll"/> asks politely: a
/// player's image handle at 2 Hz while it isn't there, at most 4 asks a frame, and once loaded only for players who sit in
/// the lobby (<see cref="SetSeated"/>), every 10 s, in case they changed it. This class reads the pixels and makes the
/// texture, at most one image a frame, into one reused byte buffer.
/// <para>
/// A texture is RGBA32, bilinear, clamped, without mipmaps and kept out of Unity's unused-asset sweep at scene loads
/// (<c>hideFlags</c>), then no longer readable (<c>Apply(false, true)</c> frees the CPU copy). At most
/// <see cref="Capacity"/> are kept; the one asked for longest ago goes (a seated player's last), and goes through
/// <c>Object.Destroy</c> after <see cref="RetireFor"/>, so a card that still shows it has time to ask again. A card must ask
/// again (<see cref="Get(ulong, AvatarSize)"/> is a dictionary lookup) whenever it is built and whenever
/// <see cref="Version"/> changes, and must not keep a texture across a menu closing. Textures are checked with
/// <c>WasCollected</c> before they are handed out.
/// </para>
/// Main thread only. Nothing here throws: a failure is logged once and turns the cache off for the session, and every
/// <see cref="Get(ulong, AvatarSize)"/> then answers "no avatar". The log never says whose avatar a line is about (D127):
/// loads are counted per burst (<see cref="AvatarLoadTally"/>), problems name the size only.
/// </summary>
internal static class AvatarCache
{
    /// <summary>How many avatars (a player's size is one each) are kept.</summary>
    public const int Capacity = SteamAvatarPoll.MaxEntries;

    /// <summary>How long a replaced or evicted texture lives on before it is destroyed.</summary>
    public static readonly TimeSpan RetireFor = TimeSpan.FromSeconds(5);

    private const int LoadsLogged = 12;

    // A burst of loads (a list opening) is one line once no avatar has loaded for this long.
    private static readonly TimeSpan TallyQuiet = TimeSpan.FromSeconds(1);
    private const int ProblemsLogged = 5;
    private const int GivenUpLogged = 8;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly SteamAvatarPoll Poll = new();
    private static readonly Dictionary<SteamAvatarKey, Texture2D> Textures = new();
    private static readonly List<(Texture2D Texture, TimeSpan DestroyAt)> Retired = new();
    private static readonly AvatarLoadTally Tally = new();
    private static TimeSpan _lastLoad;
    private static byte[]? _buffer;
    private static bool _off;
    private static int _loads;
    private static int _problems;
    private static int _givenUp;
    private static int _evictions;
    private static bool _flipLogged;
    private static ulong _standIn = ulong.MaxValue;
    private static ulong _standInFor;

    /// <summary>
    /// Whether the rows are turned upside down before they go into the texture. Steam hands the top row first and Unity's
    /// textures start at the bottom row, so they must be (Steamworks' documentation doesn't say which order it is; the
    /// first load logs this, and the check tour's screenshot shows whether the avatar stands the right way up). The one
    /// place to change if it doesn't.
    /// </summary>
    public static bool FlipRows { get; set; } = true;

    /// <summary>Changes whenever a texture appears, is replaced or goes, so a card knows to ask again.</summary>
    public static int Version { get; private set; }

    /// <summary>How many avatars have a texture now.</summary>
    public static int Count => Textures.Count;

    /// <summary>
    /// The avatar's texture, or null while there is none (<paramref name="status"/> says whether it is still on its way).
    /// Asking is what makes the cache fetch it, and keeps it wanted.
    /// </summary>
    public static Texture2D? Get(ulong steamId, AvatarSize size, out AvatarStatus status)
    {
        steamId = steamId == _standIn ? _standInFor : steamId;
        if (_off || steamId == 0)
        {
            status = AvatarStatus.None;
            return null;
        }

        var key = new SteamAvatarKey(steamId, (SteamAvatarSize)size);
        SteamAvatarPhase phase = Poll.Want(key, Clock.Elapsed);
        DropEvicted();
        if (Textures.TryGetValue(key, out Texture2D? texture))
        {
            if (texture != null && !texture.WasCollected)
            {
                status = AvatarStatus.Ready;
                return texture;
            }

            // The engine took it behind our back (a scene change that ignored the unused-asset flag): start it over.
            Textures.Remove(key);
            Poll.Remove(key);
            Poll.Want(key, Clock.Elapsed);
            Version++;
            Problem($"Avatars: a {size} avatar's texture was destroyed by the engine; fetching it again.");
            status = AvatarStatus.Loading;
            return null;
        }

        status = phase == SteamAvatarPhase.None ? AvatarStatus.None : AvatarStatus.Loading;
        return null;
    }

    /// <summary>The avatar's texture, or null while there is none (see <see cref="Get(ulong, AvatarSize, out AvatarStatus)"/> to tell why).</summary>
    public static Texture2D? Get(ulong steamId, AvatarSize size) => Get(steamId, size, out _);

    /// <summary>An id that's asked for as another's: the one-PC test's friend on the board, whose picture is this PC's own.</summary>
    public static void SetStandIn(ulong standIn, ulong steamId)
    {
        _standIn = standIn;
        _standInFor = steamId;
    }

    /// <summary>Where an avatar is, without asking for it.</summary>
    public static AvatarStatus StatusOf(ulong steamId, AvatarSize size)
    {
        if (_off || steamId == 0)
        {
            return AvatarStatus.None;
        }

        var key = new SteamAvatarKey(steamId, (SteamAvatarSize)size);
        if (Textures.TryGetValue(key, out Texture2D? texture) && texture != null && !texture.WasCollected)
        {
            return AvatarStatus.Ready;
        }

        return Poll.PhaseOf(key) == SteamAvatarPhase.None ? AvatarStatus.None : AvatarStatus.Loading;
    }

    /// <summary>Who sits in the lobby now (every size of their avatar is kept up to date, every 10 s); empty when nobody does.</summary>
    public static void SetSeated(IEnumerable<ulong>? steamIds) => Poll.SetSeated(steamIds);

    /// <summary>
    /// Call every frame (the session's update does): asks Steam about the avatars that are due, reads at most one image, and
    /// destroys textures that were retired. Does nothing at all when nothing is wanted.
    /// </summary>
    public static void Update()
    {
        if (_off)
        {
            return;
        }

        try
        {
            TimeSpan now = Clock.Elapsed;
            if (Poll.Count > 0)
            {
                Poll.Poll(now);
                DropEvicted();
                LoadOne(now);
                while (Poll.TryTakeGivenUp(out SteamAvatarKey gone))
                {
                    GivenUp($"Avatars: no {(AvatarSize)gone.Size} avatar for one player: Steam has none to give (it said none for {SteamAvatarPoll.NoneGiveUp.TotalSeconds:0} s, " +
                        $"or couldn't be asked: {SteamRuntime.Status}); the game's art is shown, and Steam is asked again every {SteamAvatarPoll.NoneRetry.TotalSeconds:0} s.");
                }
            }

            if (Retired.Count > 0)
            {
                DestroyRetired(now);
            }

            if (Tally.Count > 0 && now - _lastLoad >= TallyQuiet)
            {
                string loaded = Tally.Take(Textures.Count);
                if (_loads++ < LoadsLogged)
                {
                    ModLog.Info($"Avatars: {loaded}");
                }
            }
        }
        catch (Exception ex)
        {
            _off = true;
            ModLog.Error("Avatars: the avatar cache failed; it is off until the game restarts (the lobby shows the game's art instead)", ex);
        }
    }

    /// <summary>Destroys every texture and forgets every avatar (the mod is stopping).</summary>
    public static void Clear()
    {
        foreach (Texture2D texture in Textures.Values)
        {
            Destroy(texture);
        }

        foreach ((Texture2D texture, _) in Retired)
        {
            Destroy(texture);
        }

        Textures.Clear();
        Retired.Clear();
        Poll.Clear();
        Version++;
    }

    private static void LoadOne(TimeSpan now)
    {
        if (!Poll.TryNextFetch(out SteamAvatarKey key, out int handle))
        {
            return;
        }

        string problem;
        Texture2D? texture = null;
        if (!SteamAvatars.TryRead(handle, ref _buffer, out int width, out int height))
        {
            problem = "Steam wouldn't give the image (a handle it doesn't know, or a size outside 8 to 512)";
        }
        else
        {
            texture = MakeTexture(width, height, out problem);
        }

        if (texture == null)
        {
            Poll.FetchFailed(key, handle, now);
            Problem($"Avatars: couldn't load a {(AvatarSize)key.Size} avatar (handle {handle}): {problem}.");
            return;
        }

        if (Textures.TryGetValue(key, out Texture2D? old))
        {
            Retire(old, now);
        }

        Textures[key] = texture;
        Version++;
        Poll.Fetched(key, handle, now);
        if (!_flipLogged)
        {
            _flipLogged = true;
            ModLog.Info($"Avatars: the first avatar read ({(AvatarSize)key.Size}, handle {handle}, {width}x{height}); " +
                (FlipRows
                    ? "rows are flipped before the upload (Steam sends the top row first, Unity's textures start at the bottom row); the check tour's screenshot shows whether the avatar stands upright."
                    : "rows are NOT flipped before the upload (FlipRows is off)."));
        }
        else
        {
            Tally.Add(((AvatarSize)key.Size).ToString(), width, height); // one line per burst (Update)
            _lastLoad = now;
        }
    }

    /// <summary>The texture for the image now in the buffer, or null with why.</summary>
    private static Texture2D? MakeTexture(int width, int height, out string problem)
    {
        problem = string.Empty;
        byte[]? pixels = _buffer;
        int size = width * height * SteamAvatars.BytesPerPixel;
        if (pixels == null || pixels.Length < size)
        {
            problem = "the buffer is shorter than the image";
            return null;
        }

        Texture2D? texture = null;
        GCHandle pin = default;
        try
        {
            if (FlipRows && !SteamAvatars.FlipRows(pixels.AsSpan(0, size), width, height))
            {
                problem = "the rows couldn't be flipped";
                return null;
            }

            texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            texture.LoadRawTextureData(pin.AddrOfPinnedObject(), size);
            texture.Apply(false, true);
            return texture;
        }
        catch (Exception ex)
        {
            problem = ModLog.SafeErrorText(ex);
            if (texture != null)
            {
                Destroy(texture);
            }

            return null;
        }
        finally
        {
            if (pin.IsAllocated)
            {
                pin.Free();
            }
        }
    }

    private static void DropEvicted()
    {
        while (Poll.TryTakeEvicted(out SteamAvatarKey key))
        {
            if (Textures.Remove(key, out Texture2D? texture))
            {
                Retire(texture, Clock.Elapsed);
                Version++;
            }

            if (_evictions++ < ProblemsLogged)
            {
                ModLog.Info($"Avatars: dropped a {(AvatarSize)key.Size} avatar to make room (the cache keeps {Capacity}; the one asked for longest ago goes).");
            }
        }
    }

    private static void Retire(Texture2D texture, TimeSpan now) => Retired.Add((texture, now + RetireFor));

    private static void DestroyRetired(TimeSpan now)
    {
        for (int i = Retired.Count - 1; i >= 0; i--)
        {
            if (now >= Retired[i].DestroyAt)
            {
                Destroy(Retired[i].Texture);
                Retired.RemoveAt(i);
            }
        }
    }

    private static void Destroy(Texture2D texture)
    {
        if (texture != null && !texture.WasCollected)
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static void Problem(string message)
    {
        if (_problems++ < ProblemsLogged)
        {
            ModLog.Warn(message);
        }
    }

    private static void GivenUp(string message)
    {
        if (_givenUp++ < GivenUpLogged)
        {
            ModLog.Info(message);
        }
    }
}
#endif
