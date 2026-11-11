using System;
using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Timing;

namespace T2TOnline.Core.Campaign;

/// <summary>How a friend's game is doing at following the host. Values are part of the protocol: only add.</summary>
public enum FollowState : byte
{
    /// <summary>Not following anything yet.</summary>
    None = 0,

    /// <summary>Waiting for the run checkpoint the host's view needs.</summary>
    WaitingForRun = 1,

    /// <summary>Getting there (loading a scene, opening a screen).</summary>
    Moving = 2,

    /// <summary>Showing the host's view.</summary>
    Following = 3,

    /// <summary>Couldn't follow the host's view (with a reason); it tries again at the next view.</summary>
    Failed = 4,
}

/// <summary>
/// Friend to host (channel 1): where the friend's game is, for the host's log, status box and barriers. Sent when it
/// changes.
/// </summary>
public static class FollowReportMessage
{
    /// <summary>Longest reason, in UTF-8 bytes.</summary>
    public const int MaxReasonBytes = 160;

    /// <summary>Writes the payload (after the header).</summary>
    public static void Write(BitWriter writer, FollowReport report)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUInt32(report.Epoch);
        writer.WriteByte((byte)report.State);
        writer.WriteUInt32(report.Checkpoint);
        writer.WriteShortString(report.Reason ?? string.Empty, MaxReasonBytes);
    }

    /// <summary>Reads the payload. False for malformed data or an unknown state. Never throws.</summary>
    public static bool TryRead(ref BitReader reader, out FollowReport report)
    {
        uint epoch = reader.ReadUInt32();
        var state = (FollowState)reader.ReadByte();
        uint checkpoint = reader.ReadUInt32();
        string reason = SafeText.Clean(reader.ReadShortString(MaxReasonBytes), MaxReasonBytes);
        report = new FollowReport(epoch, state, checkpoint, reason);
        return !reader.IsOverrun && state <= FollowState.Failed;
    }
}

/// <summary>A friend's report: the host view (by epoch) it's on, how that's going, and the newest run checkpoint it applied.</summary>
public readonly record struct FollowReport(uint Epoch, FollowState State, uint Checkpoint, string Reason);

/// <summary>What the game glue should do next on a friend's PC (<see cref="CampaignFollower.Next"/>).</summary>
public enum FollowStep
{
    /// <summary>Nothing to do now.</summary>
    None,

    /// <summary>Go to <see cref="CampaignFollower.Target"/>, with the newest checkpoint; then call <see cref="CampaignFollower.Arrived"/> or <see cref="CampaignFollower.Missed"/>.</summary>
    GoToView,

    /// <summary>Stay on the view, but bring the run up to the newest checkpoint; then call <see cref="CampaignFollower.RunUpdated"/>.</summary>
    UpdateRun,
}

/// <summary>
/// A friend's side of following the host through the campaign: the newest host view and run checkpoint come in; it
/// says what to do next, waits for the run the view needs, retries a failed move a few times, and keeps what to report
/// to the host. Game-independent: the glue does the moving and tells it how that went. Not thread-safe (one game thread).
/// </summary>
public sealed class CampaignFollower
{
    /// <summary>Tries at one view before giving up on it until the next.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Wait after a failed try before the next.</summary>
    public const long RetryAfterMicros = 2_000_000;

    private int _attempts;
    private long _retryAt;
    private bool _busy;

    // While a move is out (GoToView handed out, not answered yet): the epoch it went to, and whether the host has since
    // moved to another place (not just its cursor). A move that then fails failed the old view, not the new one.
    private bool _moving;
    private uint _movingTo;
    private bool _movedOn;

    // The newest checkpoint a screen was asked to bring in (or a move applied), whatever it did with it: a screen may
    // take note of a checkpoint and leave it for the next scene change, and mustn't be asked again every frame.
    private uint _considered;
    private uint _asking;

    /// <summary>The host's newest view, or null before the first.</summary>
    public HostView? Target { get; private set; }

    /// <summary>The view the friend's game is on, or null.</summary>
    public HostView? Current { get; private set; }

    /// <summary>The run checkpoints from the host.</summary>
    public CheckpointStore Checkpoints { get; } = new();

    /// <summary>The newest checkpoint the friend's game applied (0: none).</summary>
    public uint AppliedCheckpoint { get; private set; }

    /// <summary>What to tell the host. Changes are picked up with <see cref="TryTakeReport"/>.</summary>
    public FollowReport Report { get; private set; } = new(0, FollowState.None, 0, string.Empty);

    /// <summary>
    /// True when the tries at the host's newest view are used up (<see cref="MaxAttempts"/>) and the friend's game still isn't there: it stays where it is until the host moves on.
    /// A first failed try isn't this: it's tried again (the player is told only about this one).
    /// </summary>
    public bool GaveUp => Target != null && !_busy && (Current == null || Current.Epoch != Target.Epoch) && _attempts >= MaxAttempts && Report.State == FollowState.Failed;

    private bool _reportChanged;

    /// <summary>
    /// A new view from the host: taken when newer than the one held. True when taken. A new place gets
    /// <see cref="MaxAttempts"/> fresh tries; the host's cursor moving (<see cref="SceneViewKeys.Transient"/>, the same run)
    /// doesn't, so a move that keeps failing isn't retried, and logged, at every cursor move.
    /// </summary>
    public bool OfferView(HostView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (Target != null && !SequenceMath.IsNewer(view.Epoch, Target.Epoch))
        {
            return false;
        }

        bool cursorOnly = Target != null && view.Checkpoint == Target.Checkpoint && view.SameExcept(Target, SceneViewKeys.Transient);
        Target = view;
        if (!cursorOnly)
        {
            _attempts = 0;
            _retryAt = 0;
            _movedOn |= _moving;
        }

        if (!_moving && Current != null && Current.ContentHash == view.ContentHash && Checkpoints.Has(view.Checkpoint))
        {
            // The same place under a new number (e.g. the host's game reopened the screen): nothing to do. Not while a move
            // is out: the game is leaving that place, and the move's answer says where it ends up.
            Current = view;
            SetReport(FollowState.Following, string.Empty);
        }

        return true;
    }

    /// <summary>A run checkpoint from the host. True when it's newer than the one held.</summary>
    public bool OfferCheckpoint(RunCheckpoint checkpoint) => Checkpoints.Offer(checkpoint);

    /// <summary>What to do now. Call every frame; acts on nothing while the glue is busy with the previous step.</summary>
    public FollowStep Next(long nowMicros)
    {
        if (_busy || Target == null)
        {
            return FollowStep.None;
        }

        if (Current == null || Current.Epoch != Target.Epoch)
        {
            if (_attempts >= MaxAttempts || nowMicros < _retryAt)
            {
                return FollowStep.None;
            }

            if (!Checkpoints.Has(Target.Checkpoint))
            {
                SetReport(FollowState.WaitingForRun, string.Empty);
                return FollowStep.None;
            }

            _busy = true;
            _attempts++;
            _moving = true;
            _movingTo = Target.Epoch;
            _movedOn = false;
            SetReport(FollowState.Moving, string.Empty);
            return FollowStep.GoToView;
        }

        if (Checkpoints.Latest != null && SequenceMath.IsNewer(Checkpoints.Latest.Sequence, _considered))
        {
            _busy = true;
            _asking = Checkpoints.Latest.Sequence;
            return FollowStep.UpdateRun;
        }

        return FollowStep.None;
    }

    /// <summary>The glue reached <paramref name="view"/> (the target it was given), with <paramref name="checkpoint"/> applied (0: none).</summary>
    public void Arrived(HostView view, uint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(view);
        _busy = false;
        _moving = _movedOn = false;
        _attempts = 0; // a move that works: the next one (a cursor move, say) has its tries again
        _retryAt = 0;
        Current = view;
        if (checkpoint != 0)
        {
            AppliedCheckpoint = checkpoint;
            if (SequenceMath.IsNewer(checkpoint, _considered))
            {
                _considered = checkpoint;
            }
        }

        if (Target != null && Target.Epoch == view.Epoch)
        {
            SetReport(FollowState.Following, string.Empty);
        }
    }

    /// <summary>
    /// The glue couldn't reach the target: it's tried again after <see cref="RetryAfterMicros"/>, at most
    /// <see cref="MaxAttempts"/> times. When the host moved to another place while the glue was on its way, the failure is
    /// reported against the view that failed, and the new view is tried at once with all its tries.
    /// </summary>
    public void Missed(string reason, long nowMicros)
    {
        bool movedOn = _movedOn;
        _busy = false;
        _moving = _movedOn = false;
        if (movedOn)
        {
            SetReport(FollowState.Failed, reason ?? string.Empty, _movingTo);
            return;
        }

        _retryAt = nowMicros + RetryAfterMicros;
        SetReport(FollowState.Failed, reason ?? string.Empty);
    }

    /// <summary>
    /// The glue answered the <see cref="FollowStep.UpdateRun"/> step: <paramref name="applied"/> is the checkpoint it loaded
    /// into the game (0: it loaded none, e.g. the screen leaves the run to the next scene change), <paramref name="ok"/>
    /// false when loading failed. Either way that checkpoint isn't asked about again; a newer one, or the next view, is.
    /// </summary>
    public void RunUpdated(uint applied, bool ok)
    {
        _busy = false;
        _moving = _movedOn = false;
        _considered = _asking;
        if (ok && applied != 0 && SequenceMath.IsNewer(applied, AppliedCheckpoint))
        {
            AppliedCheckpoint = applied;
        }

        SetReport(Report.State, ok ? Report.Reason : "couldn't update the run");
    }

    /// <summary>The friend's game left the campaign (session over, or the host went to the title): forget where it was.</summary>
    public void Reset()
    {
        Current = null;
        _busy = false;
        _moving = _movedOn = false;
        _attempts = 0;
        _retryAt = 0;
        SetReport(FollowState.None, string.Empty);
    }

    /// <summary>The report couldn't be sent: <see cref="TryTakeReport"/> gives it again.</summary>
    public void ReportAgain() => _reportChanged = true;

    /// <summary>The report, when it changed since last taken.</summary>
    public bool TryTakeReport(out FollowReport report)
    {
        report = Report;
        if (!_reportChanged)
        {
            return false;
        }

        _reportChanged = false;
        return true;
    }

    private void SetReport(FollowState state, string reason) => SetReport(state, reason, Target?.Epoch ?? 0);

    private void SetReport(FollowState state, string reason, uint epoch)
    {
        var report = new FollowReport(epoch, state, AppliedCheckpoint, reason);
        if (report != Report)
        {
            Report = report;
            _reportChanged = true;
        }
    }
}

/// <summary>The host's record of each friend's newest <see cref="FollowReport"/>, for the log, the status box and barriers.</summary>
public sealed class FollowTracker
{
    private readonly Dictionary<ulong, FollowReport> _reports = new();

    /// <summary>Records <paramref name="friend"/>'s report.</summary>
    public void Record(ulong friend, FollowReport report) => _reports[friend] = report;

    /// <summary><paramref name="friend"/> left.</summary>
    public void Forget(ulong friend) => _reports.Remove(friend);

    /// <summary><paramref name="friend"/>'s newest report, or false.</summary>
    public bool TryGet(ulong friend, out FollowReport report) => _reports.TryGetValue(friend, out report);

    /// <summary>True when every one of <paramref name="friends"/> is following view <paramref name="epoch"/> (or a later one).</summary>
    public bool AllFollowing(IEnumerable<ulong> friends, uint epoch)
    {
        ArgumentNullException.ThrowIfNull(friends);
        foreach (ulong friend in friends)
        {
            if (!_reports.TryGetValue(friend, out FollowReport report) ||
                SequenceMath.IsNewer(epoch, report.Epoch) ||
                (report.Epoch == epoch && report.State != FollowState.Following))
            {
                return false;
            }
        }

        return true;
    }
}
