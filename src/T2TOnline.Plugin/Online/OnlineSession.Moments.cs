#if HAS_INTEROP
using System.Linq;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Lobby;
using T2TOnline.Core.Protocol;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Startup;

namespace T2TOnline.Plugin.Online;

// A friend's side of the shared moments (D34, D135): each one the host sends unlocks only for play this PC's own game saw,
// a match it played under the number the moment names, or the host's co-op run held here; one whose play may still come
// waits, one it can't have seen is refused, each with one line. What this PC saw is noted as it happens (SharedPlayFile).
internal sealed partial class OnlineSession
{
    // Refused moments said in full; after that they're counted (a host sending many can't fill the log).
    private const int RefusalsSaid = 8;

    private int _playsNoted;
    private int _momentRefusals;
    private int _momentsOff;
    private bool _momentsFullSaid;

    // The host match playing here (its number on the host) and what was last noted of it (D135): compared every frame,
    // noted when it changes; forgotten in another hosting session, whose match numbers are its own.
    private readonly MatchViewTracker _view = new();

    /// <summary>
    /// A moment as the achievement lines name it: "MERCY in match 4", "WIN_ACT3 from the run (match 9)". A name that isn't a
    /// shared moment's is the host game's own text: never logged (<see cref="MomentCheck.NameForLog"/>).
    /// </summary>
    private static string MomentWords(AchievementMoment moment) =>
        $"{MomentCheck.NameForLog(moment.Name)} {(moment.FromRun ? $"from the run (match {moment.Match})" : $"in match {moment.Match}")}";

    /// <summary>
    /// Friend: welcomed by <paramref name="host"/> into hosting session <paramref name="session"/> (a new F7 session or a
    /// rejoin).
    /// </summary>
    private void MomentsWelcomed(ulong host, ulong session)
    {
        _sharedPlay.Welcomed(host, session);
        _momentInbox.Welcomed(session);
    }

    /// <summary>
    /// Friend: what this PC sees of the host's play, noted: each host match played here (its number on the host, and its
    /// kind from the setup loaded), what this PC's own stream showed of it (its score, the mercy rule on an elite, a
    /// headshot goal, its end), and the host's co-op run held (following, a checkpoint applied). Every frame: a few ints
    /// and flags compared.
    /// </summary>
    private void NoteHostPlay()
    {
        // A match number is its host game's own: nothing noted in another hosting session carries over.
        _view.InSession(_sharedPlay.Session);
        int plays = _relay.HostMatchesPlayed;
        if (plays != _playsNoted)
        {
            _playsNoted = plays;
            int serial = _relay.PlayedHostSerial;
            _view.Played(serial);
            if (serial > 0)
            {
                _sharedPlay.NoteMatch(serial, MatchView.FromSetup(_relay.FromHost?.Setup));
            }
            else
            {
                ModLog.Warn("Achievements: the host's match played here came without its number on the host; its shared moments can't count here.");
            }
        }

        // What this PC's own stream showed of it since the last frame (its score, a sign, its end: the game's end-of-game
        // event in this stream, or else the host ending the match here; enough of it played here), noted when something
        // changed.
        bool ended = HostMatchWitness.EndSeen || _relay.HostEndedHere;
        if (_view.Match > 0 && _view.TryNote(HostMatchWitness.HomeGoals, HostMatchWitness.VisitorGoals, HostMatchWitness.Signs,
            ended, _relay.PlayingHostMatch, Streaming.FrameClock.Micros, out MatchView seen))
        {
            _sharedPlay.NoteView(_view.Match, seen);
        }

        // The game's own rule for a campaign match's winner, from this PC's run and teams: read while the match is loaded
        // here, noted once as its end shows (the newest read: the game decides the winner at the end).
        if (HostWinRule.TryRead(_sharedPlay.Session, _view.Match, _sharedPlay.Witness, _relay.HostMatchLoadedHere, ended,
            Streaming.FrameClock.Seconds, _relay.FromHost?.Setup, _relay.MeasuresOnly, out MatchView ruled))
        {
            _sharedPlay.NoteView(_view.Match, ruled);
        }

        if (_campaign.Following && _campaign.AppliedRun > 0)
        {
            _sharedPlay.NoteRun();
        }
    }

    /// <summary>
    /// Friend: the host earned a shared moment with us: unlocked here if this PC's game saw the play it came from, kept
    /// waiting while that play may still come here, refused (one line) when it can't have.
    /// </summary>
    private void ReceiveAchievement(in AchievementMoment moment)
    {
        string words = MomentWords(moment);
        if (!ModSettings.ShareAchievements)
        {
            // Said for the first few (an honest host sends a handful a session); a host sending more can't fill the log.
            if (++_momentsOff <= RefusalsSaid)
            {
                ModLog.Info($"Achievements: the host earned {words} with you; not unlocked here (ShareAchievements is off" +
                    $"{(_momentsOff == RefusalsSaid ? "; more aren't said" : string.Empty)}).");
            }

            return;
        }

        NoteHostPlay();
        switch (_momentInbox.Arrived(moment, _sharedPlay.Witness, out MomentReason reason))
        {
            case MomentArrival.UnlockNow:
                UnlockShared(moment);
                break;
            case MomentArrival.Waiting:
                ModLog.Info($"Achievements: {words} from the host waits: {MomentCheck.Words(reason)}{SeenWords(moment)}; it's judged again once that play is seen here.");
                break;
            case MomentArrival.Refused:
                RefuseMoment(moment, reason);
                break;
            case MomentArrival.Full when !_momentsFullSaid:
                _momentsFullSaid = true;
                ModLog.Warn($"Achievements: {words} from the host isn't kept: {MomentInbox.MaxWaiting} moments already wait here (said once).");
                break;
        }
    }

    /// <summary>Friend, every frame: what this PC sees of the host's play is noted, and moments that waited for it are decided.</summary>
    private void UnlockWaitingAchievements()
    {
        if (_friend == null)
        {
            return;
        }

        NoteHostPlay();
        while (_momentInbox.Waiting > 0 && _momentInbox.TryTakeDecided(_sharedPlay.Witness, out AchievementMoment moment, out MomentReason reason))
        {
            if (reason == MomentReason.None)
            {
                UnlockShared(moment);
            }
            else
            {
                RefuseMoment(moment, reason);
            }
        }
    }

    /// <summary>
    /// Friend: a moment the host shared with us, whose play this PC saw, is unlocked on this PC's account (the line names the
    /// game's winner rule that let it through when it isn't the lead: <see cref="MomentCheck.WinRuleNote"/>).
    /// </summary>
    private void UnlockShared(in AchievementMoment moment)
    {
        string outcome = AchievementMirror.UnlockHere(moment.Name);
        string rule = moment.Match > 0 ? MomentCheck.WinRuleNote(moment.Name, _sharedPlay.Witness.ViewOf(moment.Match)) : string.Empty;
        ModLog.Info($"Achievements: {MomentWords(moment)} from the host: {outcome}{rule}.");
        if (outcome == "unlocked")
        {
            Notify(NoticeId.AchievementShared, $"Achievement unlocked: {SafeText.Clean(moment.Name, 64)}");
        }
    }

    /// <summary>Friend: a moment this PC's game didn't see the play of: not unlocked, said once (the first few in full).</summary>
    private void RefuseMoment(in AchievementMoment moment, MomentReason reason)
    {
        if (++_momentRefusals <= RefusalsSaid)
        {
            ModLog.Info($"Achievements: {MomentWords(moment)} from the host isn't unlocked here: {MomentCheck.Words(reason)}{SeenWords(moment)}" +
                $"{(_momentRefusals == RefusalsSaid ? " (more are counted at the session's end, not said)" : string.Empty)}.");
        }
    }

    /// <summary>" (an Elite match of act 2, seen to end 1-3 here)": what this PC saw of the moment's match, when it played it.</summary>
    private string SeenWords(in AchievementMoment moment) =>
        moment.Match > 0 && _sharedPlay.Witness.Played(moment.Match) ? $" ({_sharedPlay.Witness.ViewOf(moment.Match)})" : string.Empty;

    /// <summary>Friend, as the session ends: the moments that still waited (they won't unlock now), and the refusals not said.</summary>
    private void SayMomentsLeft()
    {
        var left = _momentInbox.TakeLeft(_sharedPlay.Witness);
        if (left.Count > 0)
        {
            ModLog.Info($"Achievements: {left.Count} moment(s) from the host never unlocked here: " +
                $"{string.Join(", ", left.Select(l => $"{MomentWords(l.Moment)} ({MomentCheck.Words(l.Reason)})"))}.");
        }

        if (_momentRefusals > RefusalsSaid)
        {
            ModLog.Info($"Achievements: {_momentRefusals} moment(s) from the host were refused here in all this session ({_momentRefusals - RefusalsSaid} not said one by one).");
        }

        _momentRefusals = 0;
        _momentsOff = 0;
        _momentsFullSaid = false;
    }
}
#endif
