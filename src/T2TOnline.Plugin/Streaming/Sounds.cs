#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppFMODUnity;
using Il2CppUltimateReplay;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Host side: sounds gameplay code plays directly rather than through an event. Most match sound
/// comes from SoundManager reacting to events, which a friend's PC re-delivers; the rest goes through
/// <c>RuntimeManager.PlayOneShot(string path, Vector3)</c>: referee whistles, and goalie saves picked from the goalie's
/// animation. Only <c>event:/SFX/</c> paths are forwarded (the rest are the host's own menus), as FMOD GUIDs.
/// </summary>
internal sealed class SoundRecorder
{
    private const string SfxPrefix = "event:/SFX/";

    private static readonly Dictionary<string, Guid> Guids = new(StringComparer.Ordinal);
    private static SoundRecorder? _active;
    private static bool _installed;

    private readonly StreamRecorder _recorder;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _skipped;
    private int _failures;

    public SoundRecorder(StreamRecorder recorder) => _recorder = recorder;

    /// <summary>Patches <c>RuntimeManager.PlayOneShot(string, Vector3)</c> (once). The patch does nothing unless a recording runs.</summary>
    public static bool Install(HarmonyLib.Harmony harmony)
    {
        if (_installed)
        {
            return true;
        }

        MethodInfo? target = AccessTools.Method(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), new[] { typeof(string), typeof(Vector3) });
        if (target == null)
        {
            ModLog.Warn("Sound recorder: RuntimeManager.PlayOneShot(string, Vector3) not found; whistles and goalie saves won't be recorded.");
            return false;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(typeof(SoundRecorder).GetMethod(nameof(PlayOneShotPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
        _installed = true;
        return true;
    }

    /// <summary>Starts recording direct sounds.</summary>
    public void Start()
    {
        _counts.Clear();
        _skipped = _failures = 0;
        _active = this;
    }

    /// <summary>Stops and logs what was recorded.</summary>
    public void Stop()
    {
        if (_active == this)
        {
            _active = null;
        }

        string sounds = _counts.Count == 0 ? "none" : string.Join(", ", _counts.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key[SfxPrefix.Length..]} ×{p.Value}"));
        ModLog.Info($"Sound recorder: {_counts.Values.Sum()} direct sounds ({sounds}); {_skipped} non-gameplay sounds left out.");
    }

    private static bool PlayOneShotPrefix(string path, Vector3 position)
    {
        // Runs inside the game's call: never throw, and skip the original only for a bubble's sounds while a late friend's
        // speech runs hidden (they'd be heard from lines nobody sees; SpeechMirror.BubblesQuiet is false the rest of the time).
        try
        {
            if (Campaign.Menus.SpeechMirror.BubblesQuiet && path != null && path.StartsWith(Campaign.Menus.SpeechMirror.BubbleSounds, StringComparison.Ordinal))
            {
                return false;
            }

            _active?.Record(path!, position);
        }
        catch (Exception ex)
        {
            if (_active != null && _active._failures++ < 3)
            {
                ModLog.Warn($"Sound recorder: couldn't record {path}: {ModLog.SafeErrorText(ex)}");
            }
        }

        return true;
    }

    private void Record(string path, Vector3 position)
    {
        if (!_recorder.Recording || path == null)
        {
            return;
        }

        if (!path.StartsWith(SfxPrefix, StringComparison.Ordinal))
        {
            _skipped++;
            return;
        }

        if (!Guids.TryGetValue(path, out Guid guid))
        {
            guid = FmodGuids.ToGuid(RuntimeManager.PathToGUID(path));
            Guids[path] = guid;
        }

        if (guid == Guid.Empty)
        {
            _skipped++;
            return;
        }

        var call = new SoundCall(0, false, guid, true, position.x, position.y, position.z, "PlayOneShot");
        _recorder.Write(MessageType.SoundCall, Channel.Events, call.Write);
        _counts[path] = _counts.GetValueOrDefault(path) + 1;
    }
}

/// <summary>
/// Friend side: plays the host's direct sounds at their recorded positions, at most so many at once and a second
/// (<see cref="OneShotLimiter"/>: a sound played this way can't be stopped from here, so a host can't play them without end).
/// </summary>
internal sealed class SoundChannel : IPresentationChannel
{
    private const int MaxTraced = 256;

    private static readonly MessageType[] HandledTypes = { MessageType.SoundCall };

    private readonly HashSet<Guid> _seen = new();
    private readonly OneShotLimiter _limiter = new();
    private int _played;
    private int _skipped;
    private int _failures;

    /// <inheritdoc />
    public IReadOnlyCollection<MessageType> Handles => HandledTypes;

    /// <inheritdoc />
    public void Begin(IStreamSource source, IReadOnlyDictionary<ushort, ReplayObject> objects)
    {
        _played = _skipped = _failures = 0;
        _seen.Clear();
        _limiter.Reset();
    }

    /// <inheritdoc />
    public void Deliver(in TimedMessage message)
    {
        var reader = new BitReader(message.Payload);
        if (!SoundCall.TryRead(ref reader, out SoundCall call) || call.Handle != 0 || call.EventGuid == Guid.Empty)
        {
            // Loops go as SoundLoop (SoundLoops.cs): a sound call with a handle isn't one this build sends.
            _skipped++;
            return;
        }

        // A host plays a few dozen distinct sounds; past that many (a hostile stream), they're no longer traced.
        if (_seen.Count < MaxTraced && _seen.Add(call.EventGuid))
        {
            ModLog.Trace($"Sound channel: first {call.EventGuid}");
        }

        if (!_limiter.TryTake(FrameClock.Micros))
        {
            if (_limiter.Refused == 1)
            {
                ModLog.Warn($"Sound channel: the host's stream asks for more direct sounds than a match plays (over {OneShotLimiter.Burst} at once, " +
                    $"or {OneShotLimiter.PerSecond} a second); the ones over it aren't played (counted).");
            }

            return;
        }

        try
        {
            RuntimeManager.PlayOneShot(FmodGuids.FromGuid(call.EventGuid), call.HasPosition ? new Vector3(call.X, call.Y, call.Z) : Vector3.zero);
            _played++;
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Warn($"Sound channel: couldn't play {call.EventGuid}: {ModLog.SafeErrorText(ex)}");
            }
        }
    }

    /// <inheritdoc />
    public void Finish()
    {
    }

    /// <inheritdoc />
    public void End(bool matchLoaded) =>
        ModLog.Info($"Sound channel: {_played} direct sounds played{(_skipped > 0 ? $", {_skipped} skipped" : string.Empty)}{(_failures > 0 ? $", {_failures} failed" : string.Empty)}" +
            $"{(_limiter.Refused > 0 ? $", {_limiter.Refused} not played (over {OneShotLimiter.Burst} at once, or {OneShotLimiter.PerSecond} a second)" : string.Empty)}.");
}

/// <summary>FMOD's GUID (four ints) to and from System.Guid, byte for byte.</summary>
internal static class FmodGuids
{
    /// <summary>The same 16 bytes as a System.Guid.</summary>
    public static Guid ToGuid(Il2CppFMOD.GUID guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, guid.Data1);
        BitConverter.TryWriteBytes(bytes[4..], guid.Data2);
        BitConverter.TryWriteBytes(bytes[8..], guid.Data3);
        BitConverter.TryWriteBytes(bytes[12..], guid.Data4);
        return new Guid(bytes);
    }

    /// <summary>Back to FMOD's GUID.</summary>
    public static Il2CppFMOD.GUID FromGuid(Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        return new Il2CppFMOD.GUID
        {
            Data1 = BitConverter.ToInt32(bytes),
            Data2 = BitConverter.ToInt32(bytes[4..]),
            Data3 = BitConverter.ToInt32(bytes[8..]),
            Data4 = BitConverter.ToInt32(bytes[12..]),
        };
    }
}
#endif
