#if HAS_INTEROP
using System;
using System.Collections.Generic;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Campaign;

/// <summary>
/// The screens a host's game can be on in the campaign, as <see cref="HostView.Screen"/> names. A
/// friend's game follows each with the <see cref="IScreenFollower"/> of the same name.
/// </summary>
internal static class Screens
{
    /// <summary>The title screen (start_screen), or anywhere outside the campaign. Friends return to their own title screen.</summary>
    public const string Title = "title";

    /// <summary>The world map (world_map), after the host's game saved the run there. Details: <see cref="Details.Node"/>, <see cref="Details.Act"/>.</summary>
    public const string Map = "map";

    /// <summary>A match node's preview on the world map. Details: the node, and the opponent as the host's game shows it.</summary>
    public const string Preview = "preview";

    /// <summary>A campaign match (standard_game): the match relay loads and plays it.</summary>
    public const string Match = "match";

    /// <summary>A menu (rewards, events, squad select, run end, ...). Details: <see cref="Details.Menu"/>.</summary>
    public const string Menu = "menu";
}

/// <summary>Detail names in <see cref="HostView.Values"/>, shared by the host's capture and the friend's glue.</summary>
internal static class Details
{
    /// <summary>Text: the map node, "x,y" (<c>STS.Map.Point</c>).</summary>
    public const string Node = "node";

    /// <summary>Whole: the act (1-3).</summary>
    public const string Act = "act";

    /// <summary>Text: the run's id (<c>RunData.runID</c>), so a friend can tell a new run from the one it holds.</summary>
    public const string Run = "run";

    /// <summary>Text: a menu's id (its <c>MenuDefinition</c> name).</summary>
    public const string Menu = "menu";

    /// <summary>Text: the scene the host's game is in.</summary>
    public const string Scene = "scene";
}

/// <summary>
/// One part of the campaign (run sync, scenes, menus, matches, progress) plugged into the <see cref="CampaignRelay"/>.
/// Every method is called on the game thread, inside the relay's guard: an exception is logged and switches the module's
/// part off for the session; it never reaches the game. A module switched off still gets the session's
/// <see cref="StopHosting"/> or <see cref="StopFollowing"/>, which must put back whatever it changed. Defaults do nothing.
/// </summary>
internal interface ICampaignModule
{
    /// <summary>For logs.</summary>
    string Name { get; }

    /// <summary>Host to friend message types this module reads on a friend's PC (from the 80-127 range).</summary>
    IReadOnlyCollection<MessageType> Owns => Array.Empty<MessageType>();

    /// <summary>This PC started hosting.</summary>
    void StartHosting(CampaignRelay relay)
    {
    }

    /// <summary>This PC stopped hosting.</summary>
    void StopHosting(string reason)
    {
    }

    /// <summary>A friend joined or rejoined (resend them what they missed).</summary>
    void FriendJoined(ulong friend)
    {
    }

    /// <summary>A friend left.</summary>
    void FriendLeft(ulong friend)
    {
    }

    /// <summary>This PC joined a host as a friend.</summary>
    void StartFollowing(CampaignRelay relay)
    {
    }

    /// <summary>This PC left the host. <paramref name="gameClosing"/>: no scene loads, just put things back.</summary>
    void StopFollowing(string reason, bool gameClosing)
    {
    }

    /// <summary>A friend's PC got a message of a type in <see cref="Owns"/> (header read). False when it's malformed.</summary>
    bool Receive(MessageType type, ref BitReader reader) => false;

    /// <summary>Friend to host message types this module reads on the host's PC (a progress resend request).</summary>
    IReadOnlyCollection<MessageType> OwnsFromFriends => Array.Empty<MessageType>();

    /// <summary>The host's PC got a message of a type in <see cref="OwnsFromFriends"/> from <paramref name="friend"/> (header read). False when it's malformed.</summary>
    bool ReceiveFromFriend(ulong friend, MessageType type, ref BitReader reader) => false;

    /// <summary>Every frame while hosting or following.</summary>
    void Update()
    {
    }

    /// <summary>Lines for the status box (keep them short), or none.</summary>
    IEnumerable<string> Status() => Array.Empty<string>();
}

/// <summary>
/// A friend's way to one host screen (by <see cref="Screen"/>). The relay calls <see cref="Go"/> when the host's view
/// changes to this screen (and the run it needs is here); the follower gets there, over as many frames as it takes, and
/// says how it went through the callback (exactly once). <see cref="Leave"/> comes when another screen takes over, the
/// session ends or the module is switched off; a callback not called by then is answered as failed by the relay, and later
/// calls are ignored.
/// </summary>
internal interface IScreenFollower
{
    /// <summary>The <see cref="Screens"/> name this follows.</summary>
    string Screen { get; }

    /// <summary>
    /// Go to <paramref name="view"/>. <paramref name="run"/> is the newest checkpoint (null when the view needs none).
    /// Call <paramref name="done"/> once: (true, the checkpoint applied or 0) when there; (false, reason) when it can't.
    /// </summary>
    void Go(HostView view, RunCheckpoint? run, Action<FollowResult> done);

    /// <summary>
    /// The host saved a newer run while this screen is showing: bring it in when the screen needs it (else ignore it and
    /// report true; the next screen change applies it). Call <paramref name="done"/> once.
    /// </summary>
    void UpdateRun(RunCheckpoint run, Action<FollowResult> done) => done(new FollowResult(true, run.Sequence, string.Empty, Applied: false));

    /// <summary>Every frame while this screen is the friend's.</summary>
    void Update()
    {
    }

    /// <summary>Another screen takes over (or the session ends): stop, put back what only this screen needed.</summary>
    void Leave()
    {
    }
}

/// <summary>
/// How many lines of one kind go in the log: at most a set number a minute, then only counted, and when the next minute's
/// first line comes, one line says how many were left out. A long session keeps its context to the end, and a busy stretch
/// can't fill the log. Game thread only.
/// </summary>
internal sealed class LineBudget
{
    private readonly string _what;
    private readonly int _perMinute;
    private double _since = double.NegativeInfinity;
    private int _lines;
    private int _left;

    /// <summary>A budget of <paramref name="perMinute"/> lines a minute for <paramref name="what"/> ("Menus: open and close lines").</summary>
    public LineBudget(string what, int perMinute)
    {
        _what = what;
        _perMinute = perMinute;
    }

    /// <summary>Lines left out of the log since <see cref="TakeLeftOut"/> was last asked.</summary>
    public int LeftOut { get; private set; }

    /// <summary>
    /// The lines left out so far, for a line at the end of a session (the last minute's would otherwise never be said), and
    /// counting starts again.
    /// </summary>
    public int TakeLeftOut()
    {
        int leftOut = LeftOut;
        LeftOut = 0;
        _left = 0;
        return leftOut;
    }

    /// <summary>True when one more line may go in the log now (it's counted against this minute).</summary>
    public bool TrySpend()
    {
        double now = FrameClock.Seconds;
        if (now - _since >= 60)
        {
            if (_left > 0)
            {
                ModLog.Info($"{_what}: {_left} line(s) were only counted in the last minute ({_perMinute} a minute go in the log).");
            }

            _since = now;
            _lines = 0;
            _left = 0;
        }

        if (_lines < _perMinute)
        {
            _lines++;
            return true;
        }

        _left++;
        LeftOut++;
        return false;
    }
}

/// <summary>How a follower's move went: whether it got there, the run checkpoint it applied (0: none) and why not.</summary>
/// <param name="Ok">Got there (or brought the run in).</param>
/// <param name="Checkpoint">The checkpoint now applied on this PC, or 0 when the step didn't apply one.</param>
/// <param name="Reason">Why not, for the host's log and the status box.</param>
/// <param name="Applied">The checkpoint was actually loaded into the game (not just acknowledged).</param>
internal readonly record struct FollowResult(bool Ok, uint Checkpoint, string Reason, bool Applied = true);
#endif
