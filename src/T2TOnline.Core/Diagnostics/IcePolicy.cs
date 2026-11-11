using System;
using System.Collections.Generic;
using System.Globalization;

namespace T2TOnline.Core.Diagnostics;

/// <summary>
/// Relay only (D133): which ICE candidate types Steam may use for this PC's P2P sessions, and the start-up line that
/// says what Steam really took. ICE is how a P2P connection finds a direct route, and it does so by sharing addresses
/// with the peer; Steam's relays (SDR) carry the connection without it. Off (0) unless the player allows direct
/// connections, then every candidate type.
/// </summary>
public static class IcePolicy
{
    /// <summary><c>k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable</c>: no ICE, no address shared with the peer.</summary>
    public const int Disabled = 0;

    /// <summary><c>k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_All</c>: every candidate type.</summary>
    public const int AllCandidates = 0x7fffffff;

    /// <summary>The setting that allows direct connections, as it's named in the settings file.</summary>
    public const string SettingName = "AllowDirectConnections";

    /// <summary>The settings file, as a label (relative to the game's folder).</summary>
    public const string SettingsFile = "UserData/T2TOnline/settings.cfg";

    /// <summary>The value to set: <see cref="AllCandidates"/> when <paramref name="allowDirect"/>, else <see cref="Disabled"/>.</summary>
    public static int Wanted(bool allowDirect) => allowDirect ? AllCandidates : Disabled;

    /// <summary>
    /// The start-up line for what Steam took. In effect only when the value was read back and is the one wanted (Steam's
    /// own answer to the set is reported but the read-back decides). Not in effect is a warning; relay only not in effect
    /// turns online off for the session (<see cref="IceOutcome.TurnsOnlineOff"/>), as Steam's fallback would be the player's
    /// own Steam setting, which connects friends directly. The line says how to play anyway.
    /// </summary>
    /// <param name="allowDirect">The player's <c>AllowDirectConnections</c>.</param>
    /// <param name="setOk">Steam's answer to the set.</param>
    /// <param name="readOk">The value could be read back.</param>
    /// <param name="readBack">The value read back (meaningless unless <paramref name="readOk"/>).</param>
    public static IceOutcome Check(bool allowDirect, bool setOk, bool readOk, int readBack)
    {
        int wanted = Wanted(allowDirect);
        if (readOk && readBack == wanted)
        {
            string note = setOk ? string.Empty : " (Steam said the setting failed, but reads it back as set)";
            return new IceOutcome(
                true,
                !allowDirect,
                false,
                allowDirect
                    ? $"ICE: direct connections allowed (your address is shared with the players you connect to){note}."
                    : $"ICE: relay only (IP addresses are never shared){note}.");
        }

        string value = readBack.ToString(CultureInfo.InvariantCulture);
        string problem = !readOk ? (setOk ? "Steam's value can't be read back although it accepted the setting" : "Steam refused the setting and its value can't be read back")
            : setOk ? $"Steam reads back {value} although it accepted the setting"
            : $"Steam refused the setting (it reads back {value})";
        string wantedText = wanted.ToString(CultureInfo.InvariantCulture);
        return new IceOutcome(
            false,
            false,
            !allowDirect,
            allowDirect
                ? $"ICE: direct connections wanted ({wantedText}), but {problem}; connections may all go through Steam's relays."
                : $"ICE: relay only wanted ({wantedText}), but {problem}, so online is off this session: Steam could connect you straight to another player, " +
                  $"which would share your IP address with them. To play online anyway, set {SettingName} = true in {SettingsFile} and restart the game (your address is then shared with the players you connect to).");
    }

    /// <summary>
    /// True when a link is found direct while relay only is in effect (<paramref name="relayOnly"/>): Steam didn't keep the
    /// setting. A link with no route yet isn't direct.
    /// </summary>
    public static bool DirectDespiteRelayOnly(bool relayOnly, bool connected, bool relayed) => relayOnly && connected && !relayed;

    /// <summary>
    /// What a PC does about a link found direct while relay only is in effect: a friend's game leaves the host's
    /// (<paramref name="linkToHost"/>: the session's own end path, with a prompt, as when the host is lost); a host only
    /// warns, as it has no true reason to give one friend for ending their link (the friend's own check ends it on their
    /// side, and ending it from here would bring them straight back through their rejoin).
    /// </summary>
    public static DirectLinkAction OnRoute(bool relayOnly, bool connected, bool relayed, bool linkToHost) =>
        !DirectDespiteRelayOnly(relayOnly, connected, relayed) ? DirectLinkAction.None
        : linkToHost ? DirectLinkAction.Leave
        : DirectLinkAction.Warn;
}

/// <summary>
/// D133's guard over time: a link counts as direct only once every report has read it direct (connected and not relayed,
/// while relay only is in effect) for <see cref="ConfirmAfter"/>. Steam's P2P code can deselect a link's transport while
/// the link stays connected (its route lost, until a relay is found again or the link times out, 10 s by default), and a
/// link with no transport reads as not relayed: one such reading is a lost route, not a direct one. A direct route under
/// relay only means Steam ignored the setting, and stays. One instance per session; the game's thread only.
/// </summary>
public sealed class DirectLinkWatch
{
    /// <summary>How long a link must read direct before it's acted on: longer than Steam's connected timeout and a report.</summary>
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(20);

    /// <summary>Readings further apart than this start over (a report missed, a link that came back).</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(12);

    /// <summary>The most links followed at once (a host has three friends; a flood of peers starts over).</summary>
    public const int MaxLinks = 16;

    private readonly Dictionary<ulong, (TimeSpan First, TimeSpan Last)> _direct = new();

    /// <summary>
    /// One report's reading of the link to <paramref name="peer"/> at <paramref name="now"/>: what to do about it
    /// (<see cref="IcePolicy.OnRoute"/>) once it has read direct for <see cref="ConfirmAfter"/>, else
    /// <see cref="DirectLinkAction.None"/>. <paramref name="directFor"/> is how long it has read direct so far.
    /// </summary>
    public DirectLinkAction Observe(ulong peer, bool relayOnly, bool connected, bool relayed, bool linkToHost, TimeSpan now, out TimeSpan directFor)
    {
        directFor = TimeSpan.Zero;
        if (!IcePolicy.DirectDespiteRelayOnly(relayOnly, connected, relayed))
        {
            _direct.Remove(peer);
            return DirectLinkAction.None;
        }

        if (!_direct.TryGetValue(peer, out (TimeSpan First, TimeSpan Last) seen) || now < seen.Last || now - seen.Last > MaxGap)
        {
            if (!_direct.ContainsKey(peer) && _direct.Count >= MaxLinks)
            {
                _direct.Clear();
            }

            _direct[peer] = (now, now);
            return DirectLinkAction.None;
        }

        _direct[peer] = (seen.First, now);
        directFor = now - seen.First;
        return directFor >= ConfirmAfter ? IcePolicy.OnRoute(relayOnly, connected, relayed, linkToHost) : DirectLinkAction.None;
    }
}

/// <summary>What to do about a direct link while relay only is in effect (<see cref="IcePolicy.OnRoute"/>).</summary>
public enum DirectLinkAction
{
    /// <summary>Nothing: relayed, no route yet, or direct connections allowed.</summary>
    None,

    /// <summary>Warn (a host's link to a friend).</summary>
    Warn,

    /// <summary>Leave the host's game (a friend's link to its host).</summary>
    Leave,
}

/// <summary>What Steam took for ICE (<see cref="IcePolicy.Check"/>).</summary>
/// <param name="InEffect">The wanted value is the one Steam reads back: an INFO line; otherwise a WARN.</param>
/// <param name="RelayOnly">Relay only is in effect: a direct link afterwards means Steam didn't keep it.</param>
/// <param name="TurnsOnlineOff">Relay only was wanted and isn't in effect: online is off for the session.</param>
/// <param name="Line">The log line.</param>
public readonly record struct IceOutcome(bool InEffect, bool RelayOnly, bool TurnsOnlineOff, string Line);
