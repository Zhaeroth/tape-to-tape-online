#if HAS_INTEROP
using System;
using System.IO;
using System.Runtime.CompilerServices;
using MelonLoader.Utils;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Streaming;
using T2TOnline.Steam;

namespace T2TOnline.Plugin.Diagnostics;

/// <summary>
/// This PC's own Steam name for the log's cleaner (D127): read once Steam is signed in, again every
/// <see cref="OwnNameWatch.LookSeconds"/> while online is on (a rename), and before every problem report, with online on or off,
/// so a report made before any lobby cleans it too (MelonLoader's credit line can be the same text). Registered with
/// <see cref="ModLog.RegisterName"/>; never written anywhere. Without Steam (not signed in, or the mod's Steam library missing
/// from a part install) the cleaner can't know it, and the log says so once. The game's thread only; never throws.
/// </summary>
internal static class OwnName
{
    private static readonly OwnNameWatch Watch = new();
    private static bool _failed;
    private static bool _pathSet;

    /// <summary>Every frame from the online session: a read when one is due (at once, then every 30 s). One comparison otherwise.</summary>
    public static void Tick()
    {
        if (_failed || !Watch.Due(FrameClock.Seconds))
        {
            return;
        }

        Learn(Watch.Known ? "a look while online is on" : "Steam signed in");
    }

    /// <summary>A read now, <paramref name="when"/> saying what asked ("a report"): the name registered, a first name, a change or its absence said once.</summary>
    public static void Learn(string when)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            ulong id;
            string name;
            try
            {
                (id, name) = ReadSteam();
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException or System.IO.FileLoadException or TypeLoadException or DllNotFoundException or EntryPointNotFoundException)
            {
                // The mod's Steam library isn't there (a part install): nothing to read this launch.
                _failed = true;
                ModLog.Info($"Log: this PC's own Steam name can't be known to the log's cleaner ({when}: the mod's Steam library couldn't be used, {ex.GetType().Name}).");
                return;
            }

            OwnNameLook look = Watch.Seen(id, name, FrameClock.Seconds);
            if (look is OwnNameLook.First or OwnNameLook.Same or OwnNameLook.Changed)
            {
                ModLog.RegisterName(id, name); // already known costs a dictionary read
            }

            if (Watch.Line(look, when) is { } line)
            {
                ModLog.Info(line);
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            ModLog.Warn($"Log: reading this PC's own Steam name for the log's cleaner failed ({ModLog.SafeErrorText(ex)}); it isn't read again this launch.");
        }
    }

    /// <summary>The Steam calls, apart: a missing Steam library fails as this method is compiled, inside the caller's catch.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ulong Id, string Name) ReadSteam()
    {
        if (!_pathSet)
        {
            // The game's own copy, as the online session points it (an offline shell from start-up has no session to).
            _pathSet = true;
            SteamRuntime.SetLibraryPath(Path.Combine(MelonEnvironment.GameRootDirectory, "Tape to Tape_Data", "Plugins", "x86_64", "steam_api64.dll"));
        }

        ulong id = SteamRuntime.LocalSteamId;
        return id == 0 ? (0, string.Empty) : (id, SteamFriends.GetPersonaName());
    }
}
#endif
