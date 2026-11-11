#if HAS_INTEROP
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Online;

// How the session's log lines and status box name the ways in and out (D142): the developer's keys with the developer tools on, the
// game's own rows in the players' build (no key but Insert does anything there), so no line sends a player to a key they don't have.
internal sealed partial class OnlineSession
{
    /// <summary>The way out of this online game: F7 for a developer; Settings > Online's End Online Game (a host) or Leave Online Game for a player.</summary>
    private string LeaveWay => PlayersBuild.Leave(ModSettings.DevTools, _host != null);

    /// <summary>The way into a friend's game: F7 for a developer; the title's Online > Join Lobby for a player.</summary>
    private static string JoinWay => ModSettings.DevTools ? "F7" : PlayersBuild.JoinLobby;
}
#endif
