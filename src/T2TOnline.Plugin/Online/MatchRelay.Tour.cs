#if HAS_INTEROP
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Online;

// The check tour's reach into the match half of the session (developer only): the one-PC test's friend told to report a
// failed load at the next match (the onepc tour's op_failload), and the host's barrier's time-scale re-zeroing counted
// for the campaign tour's summary. Apart from the relay itself (MatchRelay.cs); real friends never meet any of it.
internal sealed partial class MatchRelay
{
    /// <summary>The reason the one-PC test's friend gives the host when the tour has it fail a load.</summary>
    internal const string TourFailedLoadReason = "the check tour's stand-in reports that it couldn't load this match";

    private bool _tourFailLoad;
    private int _tourFailedSetup;

    /// <summary>
    /// Host: how many times the game set the time scale back while the loading barrier held a match, this session (each one
    /// re-zeroed by the barrier): expected while a game routine resets it, counted for the campaign tour's summary.
    /// </summary>
    internal int TourBarrierResets { get; private set; }

    /// <summary>
    /// The one-PC test's friend (measuring only) reports a failed load for the next match setup it gets, instead of ready:
    /// the host's stream friend then holds the reason, and the host's AI plays their skater at once (<c>AiStandIn</c>'s
    /// blind start). Once; a rejoin doesn't announce that match as ready afterwards. <paramref name="on"/> false takes the
    /// request back (the tour's step ended before a match came).
    /// </summary>
    internal void TourFailNextLoad(bool on) => _tourFailLoad = on;

    /// <summary>The setup just taken, when the tour asked for a failed load: the failure goes to the host, and true.</summary>
    private bool TourFailsThisLoad()
    {
        if (!_tourFailLoad || _client == null)
        {
            return false;
        }

        _tourFailLoad = false;
        _tourFailedSetup = _handledSetup;
        _client.SendReady(_client.SetupHash, false, TourFailedLoadReason);
        ModLog.Info($"Online one-PC test: your match's setup arrived over Steam ({Describe(_client.Setup)}); the check tour has this friend report that it couldn't load it.");
        return true;
    }
}
#endif
