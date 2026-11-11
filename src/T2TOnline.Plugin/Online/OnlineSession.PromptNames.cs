#if HAS_INTEROP
using System;
using T2TOnline.Core.Input;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Input;

namespace T2TOnline.Plugin.Online;

// A friend's button prompts on the host's screen (Input/PromptNames.cs). The
// friend: its prompt names, made from its own device's maps when the game's prompt mapper is up, and again when the device
// or the controls change, go to the host after every welcome. The host: each friend's names answer the game's prompts for
// their Rewired player, and their game player gets their device family in the game's prompt mapper. Apart from what the
// session does (OnlineSession.cs).
internal sealed partial class OnlineSession
{
    /// <summary>How often a friend looks whether its names must be made again, and a host whether a friend's player shows their family.</summary>
    private static readonly TimeSpan PromptNamesInterval = TimeSpan.FromMilliseconds(500);

    private TimeSpan _nextPromptNames;
    private ushort _promptNamesGeneration;
    private PromptNameTable? _promptNames;
    private (int Family, int MapsPlayer, IntPtr Pad, byte Controls)? _promptNamesFor;
    private bool _promptNamesDirty;
    private int _promptNamesFailures;

    /// <summary>
    /// Friend: the one place to say this PC's controls changed mid-session (a remap from the panel, a pad picked up with
    /// its own maps): the prompt pin reads the maps again, and the prompt names are made again and sent to the host within
    /// half a second. New controls sent to the host with a new generation (message 20) are noticed by themselves too; this
    /// covers a change that keeps the generation. Never throws.
    /// </summary>
    /// <param name="why">What changed, for the log.</param>
    internal void NoteControlsChanged(string why)
    {
        PromptPin.ControlsChanged();
        _promptNamesDirty = true;
        _nextPromptNames = TimeSpan.Zero;
        ModLog.Info($"Prompts: your controls changed ({why}); your prompts here and your prompt names for the host follow them.");
    }

    /// <summary>Every frame: a friend's names go on its current session; twice a second, made again if the device or controls changed, and a host's friends' players get their families.</summary>
    private void UpdatePromptNames(TimeSpan elapsed)
    {
        if (_friend != null && _promptNames != null && !ReferenceEquals(_friend.PromptNames, _promptNames))
        {
            _friend.SetPromptNames(_promptNames); // a new session after a drop or a relink: sent after its welcome
        }

        if (elapsed < _nextPromptNames)
        {
            return;
        }

        _nextPromptNames = elapsed + PromptNamesInterval;
        if (_friend != null)
        {
            MakePromptNames();
        }

        if (_host != null)
        {
            foreach (RemotePad pad in _pads.Values)
            {
                PromptNames.FollowFamily(pad.Pad.RewiredPlayerId, pad.GamePlayer, pad.Pad.DeviceType, pad.Tag);
            }
        }
    }

    /// <summary>
    /// Friend: the prompt names of the device the prompt pin shows (the one-PC test's too, whose host side answers from
    /// them), made when there are none for it yet, or it or its controls changed. Waits for the game's prompt mapper. Three
    /// failures and they aren't made again this game (the host shows its own names, as before).
    /// </summary>
    private void MakePromptNames()
    {
        if (_promptNamesFailures >= 3)
        {
            return;
        }

        try
        {
            PromptDevice device = FriendPromptDevice();
            (int, int, IntPtr, byte) key = ((int)device.Family, device.MapsPlayerId, device.Pad?.Pointer ?? IntPtr.Zero, _controls?.Generation ?? 0);
            if (!_promptNamesDirty && _promptNamesFor == key)
            {
                return;
            }

            ushort generation = (ushort)((_promptNamesGeneration % ushort.MaxValue) + 1);
            PromptNameTable? table = PromptNames.Make(device, generation, out string line);
            if (table == null)
            {
                return; // the game's prompt mapper isn't up yet
            }

            _promptNamesGeneration = generation;
            _promptNamesFor = key;
            _promptNamesDirty = false;
            _promptNames = table;
            bool joined = _friend!.State == Core.Session.FriendSessionState.Joined;
            _friend.SetPromptNames(table);
            string hostTag = _solo ? "the one-PC test's host side" : HostTag();
            ModLog.Info($"Prompts: your prompt names for {hostTag}, {line}; {(joined ? "sent" : "sent after the welcome")} (its prompts for your player are named from them).");
        }
        catch (Exception ex)
        {
            _promptNamesFailures++;
            ModLog.Error(_promptNamesFailures >= 3
                ? "Prompts: making your prompt names for the host failed three times; not tried again this game (the host's prompts for you are its own)"
                : "Prompts: making your prompt names for the host failed; tried again in half a second", ex);
        }
    }

    /// <summary>The friend side ended: the next session makes its names afresh.</summary>
    private void ForgetPromptNames()
    {
        _promptNames = null;
        _promptNamesFor = null;
        _promptNamesDirty = false;
    }

    /// <summary>Host: a friend's prompt names arrived: the game's prompts for their Rewired player are named from them from now on.</summary>
    private void PromptNamesArrived(ulong steamId, string tag)
    {
        if (!_pads.TryGetValue(steamId, out RemotePad? pad) || _host?.Find(steamId)?.PromptNames is not { } table)
        {
            return;
        }

        PromptNames.Use(pad.Pad.RewiredPlayerId, pad.Tag, table);
        string family = PromptPinState.DeviceName(table.Family);
        string theirs = (int)pad.Pad.DeviceType == table.Family ? string.Empty
            : $"; their {pad.Pad.Kind.ToLowerInvariant()} here is {VirtualPad.FamilyName(pad.Pad.DeviceType)}, so they answer once it's {family}";
        ModLog.Info($"Prompts: {tag}'s button prompts on this PC now come from their own bindings (table {table.Generation}: {table.Entries.Count} names on {family}, " +
            $"{table.ModifierCount} modifier key(s), Rewired player {pad.Pad.RewiredPlayerId}){theirs}.");
    }
}
#endif
