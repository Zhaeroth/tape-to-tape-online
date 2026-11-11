#if HAS_INTEROP
using System.Collections.Generic;
using T2TOnline.Core.Stream;

namespace T2TOnline.Plugin.Online;

// The host's run history page browsed at the title: the custom logo the page wears goes to friends through the custom
// content carrier (v1; Campaign/Menus/RunHistoryPages.cs decides which logo and when, Core
// HistoryLogoShare). Apart from the match half of the session (MatchRelay.cs).
internal sealed partial class MatchRelay
{
    /// <summary>
    /// Host: the logo a run history page wears goes to every friend in the session who hasn't had it (the carrier sends each
    /// item to each friend once a session, in pieces as their link takes them, and again to a friend who rejoins or whose store
    /// dropped it to make room). Called
    /// again and again while the page is up, so a friend who joined meanwhile gets it too; a friend who has it costs nothing.
    /// </summary>
    public void ShareHistoryLogo(CustomItem item) => Guarded("sending a run history page's logo", () =>
    {
        if (_content == null || _host == null)
        {
            return;
        }

        var items = new List<CustomItem>(1) { item };
        foreach (StreamFriend friend in _host.Friends)
        {
            _content.Send(friend.SteamId, items);
        }
    });
}
#endif
