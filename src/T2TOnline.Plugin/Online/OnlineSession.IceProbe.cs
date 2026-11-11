#if HAS_INTEROP
using System;
using System.Threading;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;
using T2TOnline.Steam;
using T2TOnline.Steam.Native;

namespace T2TOnline.Plugin.Online;

// D133's proof per connection, for developers (DevTools on; nothing at all with it off): the start-up line proves Steam
// took relay only at global scope; this has Steam itself say, for the session's first connection, whether that connection
// uses ICE. As a session starts (a host or a join), Steam's networking debug output goes to a handler and its P2P
// rendezvous log level is raised to Verbose (the level of the line that names a connection's ICE setting; the line for
// ICE switched off is at Msg); the first ICE line is logged once, rebuilt from what was parsed (SteamIceLine: nothing of
// the connection's description, which names the peer), and the debug output goes off again, as at the session's end.
internal sealed partial class OnlineSession
{
    /// <summary>Lines looked at, at most, before the handler only counts: Steam's verbose output can be long.</summary>
    private const int IceProbeMaxLines = 20_000;

    // Steam's thread writes these, the game's thread reads them: the first reading, and how many lines came.
    private static SteamIceReading? _iceReading;
    private static int _iceLines;

    // One handler for the process (no allocation per session).
    private static readonly Action<SteamNetworkingDebugOutputType, string> IceProbeHandler = OnSteamDebugLine;

    /// <summary>Steam's debug output is on for this session's first connection.</summary>
    private bool _iceProbeOn;

    /// <summary>The P2P rendezvous log level this changed (to put back), or -1 when it was left as it was.</summary>
    private int _rendezvousLevelBefore = -1;

    /// <summary>As a session starts, with DevTools on: Steam's debug output on until the first connection's ICE line. Never throws.</summary>
    private void ArmIceProbe()
    {
        if (_iceProbeOn || !ModSettings.DevTools || _self == 0)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _iceReading, null);
            Interlocked.Exchange(ref _iceLines, 0);
            const int verbose = (int)SteamNetworkingDebugOutputType.Verbose;
            if (!SteamNetworking.TryGetGlobalConfig(SteamNetworkingConfigValue.LogLevelP2PRendezvous, out int before))
            {
                ModLog.Info("ICE: DevTools: Steam won't say its P2P rendezvous log level, so its debug output stays off (no per-connection ICE line this session).");
                return;
            }

            _rendezvousLevelBefore = -1;
            if (before < verbose)
            {
                if (!SteamNetworking.SetGlobalConfig(SteamNetworkingConfigValue.LogLevelP2PRendezvous, verbose))
                {
                    ModLog.Info("ICE: DevTools: Steam refused a Verbose P2P rendezvous log level, so its debug output stays off (no per-connection ICE line this session).");
                    return;
                }

                _rendezvousLevelBefore = before;
            }

            if (!SteamDebugOutput.Start(SteamNetworkingDebugOutputType.Verbose, IceProbeHandler))
            {
                PutRendezvousLevelBack();
                ModLog.Info("ICE: DevTools: Steam's networking debug output couldn't be turned on (Steam, or its library's SetDebugOutputFunction, isn't there: see the Steam: lines); no per-connection ICE line this session.");
                return;
            }

            _iceProbeOn = true;
            ModLog.Info("ICE: DevTools: Steam's networking debug output is on (P2P rendezvous at Verbose) until this session's first connection says how it uses ICE; only that line is logged.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"ICE: DevTools: turning Steam's networking debug output on failed ({ModLog.SafeErrorText(ex)}).");
            DisarmIceProbe("it failed to start");
        }
    }

    /// <summary>Every frame (a field read while nothing is armed): the first connection's ICE line, logged once.</summary>
    private void PollIceProbe()
    {
        if (!_iceProbeOn || Volatile.Read(ref _iceReading) is not { } reading)
        {
            return;
        }

        string line = SteamIceLine.LogLine(reading, _relayOnly, out bool warn);
        if (warn)
        {
            ModLog.Warn(line);
        }
        else
        {
            ModLog.Info(line);
        }

        DisarmIceProbe(null);
    }

    /// <summary>
    /// Steam's debug output off again and its log level back (after the line, or at the session's end with
    /// <paramref name="unseen"/> saying why no line came). Never throws.
    /// </summary>
    private void DisarmIceProbe(string? unseen)
    {
        bool wasOn = _iceProbeOn;
        _iceProbeOn = false;
        try
        {
            bool stopped = !wasOn || SteamDebugOutput.Stop();
            PutRendezvousLevelBack();
            if (!wasOn)
            {
                return;
            }

            int lines = Volatile.Read(ref _iceLines);
            string off = stopped ? "Steam's networking debug output is off again" : "Steam's networking debug output couldn't be turned off (no line of it is used from now on)";
            ModLog.Info(unseen == null
                ? $"ICE: DevTools: {off}."
                : $"ICE: DevTools: Steam gave no ICE line for this session's connections before {unseen} ({lines} debug line(s) seen); {off}.");
        }
        catch (Exception ex)
        {
            ModLog.Warn($"ICE: DevTools: turning Steam's networking debug output off failed ({ModLog.SafeErrorText(ex)}).");
        }
    }

    /// <summary>At the session's end: a line that came this very frame is still logged, then the debug output goes off.</summary>
    private void EndIceProbe()
    {
        PollIceProbe();
        DisarmIceProbe("the session ended");
    }

    /// <summary>Steam's P2P rendezvous log level back to what it was before <see cref="ArmIceProbe"/> raised it.</summary>
    private void PutRendezvousLevelBack()
    {
        if (_rendezvousLevelBefore < 0)
        {
            return;
        }

        int before = _rendezvousLevelBefore;
        _rendezvousLevelBefore = -1;
        if (!SteamNetworking.SetGlobalConfig(SteamNetworkingConfigValue.LogLevelP2PRendezvous, before))
        {
            ModLog.Info($"ICE: DevTools: Steam's P2P rendezvous log level couldn't be put back to {before}; it stays at Verbose until the game restarts.");
        }
    }

    /// <summary>
    /// Steam's handler, on whichever thread Steam prints from (its own, under its lock): no Steam, no Unity, no log. The first
    /// ICE reading is kept for <see cref="PollIceProbe"/>; past <see cref="IceProbeMaxLines"/> lines only the count goes on.
    /// </summary>
    private static void OnSteamDebugLine(SteamNetworkingDebugOutputType type, string text)
    {
        if (Interlocked.Increment(ref _iceLines) > IceProbeMaxLines || Volatile.Read(ref _iceReading) != null)
        {
            return;
        }

        if (SteamIceLine.Read(text) is { } reading)
        {
            Interlocked.CompareExchange(ref _iceReading, reading, null);
        }
    }
}
#endif
