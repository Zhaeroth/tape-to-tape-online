#if HAS_INTEROP
using T2TOnline.Core.Lobby;

namespace T2TOnline.Plugin.Online;

// Leave Online Game / End Online Game on the game's Settings menu (NativeUi/SettingsTabRows.cs, D139): F7's own leave, from a row.
internal sealed partial class OnlineSession
{
    /// <summary>This PC is in an online session (hosting it or in a host's).</summary>
    public bool InOnlineGame => InLobby;

    /// <summary>This PC hosts the online session it's in.</summary>
    public bool HostsOnlineGame => InLobby && _host != null;

    /// <summary>
    /// How many others are in the session this PC hosts: the lobby's members but this PC, or friends' players seated or kept for a rejoin,
    /// whichever is more; 0 when it hosts none. Settings > Online's Direct Connections asks nothing while there is anyone (D133: the
    /// question is the host's own consent, and a friend's press reaches the host's menus). A few field reads: asked at a press.
    /// </summary>
    public int OthersHosted
    {
        get
        {
            if (!InLobby || _host == null)
            {
                return 0;
            }

            int members = 0;
            foreach (ulong member in _link.Members)
            {
                if (member != _self)
                {
                    members++;
                }
            }

            return System.Math.Max(members, _pads.Count + _parked.Count);
        }
    }

    /// <summary>
    /// Settings > Online's Leave or End, once the player said yes: exactly what F7 does in a session (<c>LeaveAll</c>, the same notice, the pad
    /// given back as after any session). Nothing when no session is on any more. Guarded as a key's action.
    /// </summary>
    public void LeaveFromSettings() => Guarded("Settings > Online", () =>
    {
        if (!InLobby)
        {
            Diagnostics.ModLog.Info("Online: Leave from Settings > Online: the session had ended already.");
            return;
        }

        bool hosting = _host != null;
        LeaveAll("left from Settings > Online");
        Notify(NoticeId.SessionLeft, hosting ? "Session ended." : $"Left the online session. {PadBackNotice()}");
        NotifyPadBack();
        Diagnostics.ModLog.Info($"Online: left from Settings > Online ({(hosting ? "host" : "friend")}).");
    });
}
#endif
