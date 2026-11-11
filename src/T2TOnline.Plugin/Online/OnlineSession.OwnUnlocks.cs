#if HAS_INTEROP
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Online;

// A friend's own new-unlocks screen at a run's end the host's game shows none for (Campaign/Menus/OwnUnlocks.cs): while it's
// up, the friend's device is lent back to their own game with the lend their own settings use (OnlineSession.Remap.cs), and
// taken back when it closes; the session's end leaves it where it was lent, as for those menus. The host's game is past its
// run's end then (its end-of-game steps, its title, its lobby): nothing of the friend's plays there, so it isn't told.
// Their own settings and remapper wait meanwhile, and the screen doesn't open while one of those has the device.
internal sealed partial class OnlineSession
{
    private bool _unlocksLent;

    /// <summary>
    /// True while the friend's device is lent to their own game: their own settings or remapper, their new-unlocks screen, or the
    /// question about a match that couldn't load here (OnlineSession.FollowQuestion.cs).
    /// </summary>
    private bool DeviceLent => _ownMenu.HoldsDevice || _unlocksLent || _questionLent;

    /// <summary>The own new-unlocks screen's hooks, once, from the constructor.</summary>
    private void InstallOwnUnlocks()
    {
        OwnUnlocks.LendDevice = LendForOwnUnlocks;
        OwnUnlocks.GiveDeviceBack = GiveBackFromOwnUnlocks;
        OwnUnlocks.HostPlaying = () => !_broken && _friend != null && _flow.Friend == LobbyFriendState.Playing;
        OwnUnlocks.HostAtLobby = () => !_broken && _friend != null && _flow.Friend == LobbyFriendState.Returning;
    }

    /// <summary>
    /// The friend's device for their own new-unlocks screen: lent back to their game (a pad to its player with only the UI's
    /// maps on, a keyboard and mouse to their players), or already theirs. False, with why, when it can't be.
    /// </summary>
    private (bool Lent, string Device) LendForOwnUnlocks()
    {
        if (_friend is not { State: FriendSessionState.Joined } || _broken)
        {
            return (false, "this PC isn't joined to the session now");
        }

        if (!DeviceWithHost || (_localPad == null && _localKeyboard == null))
        {
            return (true, "your device is this game's own");
        }

        if (_ownMenu.HoldsDevice || _unlocksLent || _questionLent)
        {
            return (false, _questionLent ? "the question about the host's match has your device" : "your own settings or remapper has your device");
        }

        if (_localPad is { IsConnected: false })
        {
            return (false, "your pad is unplugged");
        }

        if (!LendDevice(out string device))
        {
            Step("taking your device back", TakeDeviceBack);
            return (false, device);
        }

        _unlocksLent = true;
        return (true, device);
    }

    /// <summary>The friend's new-unlocks screen closed (<paramref name="why"/>): their device goes back to the session while its play period lasts.</summary>
    private void GiveBackFromOwnUnlocks(string why)
    {
        if (!_unlocksLent)
        {
            return;
        }

        if (_friend is { State: FriendSessionState.Joined } && DeviceWithHost && !_broken)
        {
            _unlocksLent = false;
            Step("taking your device back", TakeDeviceBack);
            ModLog.Info($"Your {(_localPad != null ? "pad" : "keyboard")} is taken again after your new-unlocks screen ({why}): only the online session reads it.");
            return;
        }

        EndOwnUnlocksLend(why);
    }

    /// <summary>
    /// The play period or the session ended with the friend's new-unlocks screen holding their device: it stays where it was
    /// lent, set as a give-back sets it (as for their own settings), and nothing more is taken. Never throws.
    /// </summary>
    private void EndOwnUnlocksLend(string reason)
    {
        if (!_unlocksLent)
        {
            return;
        }

        _unlocksLent = false;
        if (_localPad is { LentTo: >= 0 } pad)
        {
            Step("leaving your pad where it was lent", () => _padBack = pad.EndLend());
        }

        _keyboardLent = false;
        _lendPlayer = -1;
        ModLog.Info($"Your device stays with your own game ({reason}); your new-unlocks screen had it.");
    }
}
#endif
