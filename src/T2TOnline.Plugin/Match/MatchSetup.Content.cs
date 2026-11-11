#if HAS_INTEROP
using System;
using System.Globalization;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;

namespace T2TOnline.Plugin.Match;

/// <summary>
/// A match that names the host's custom content (<see cref="CustomContent"/>): a friend's load waits, a step a frame,
/// until every piece is here (it comes on the bulk channel, the setup on the events channel), then loads as any other; if
/// it doesn't all come, why goes to the match relay (<see cref="TakeLoadProblem"/>). The wait
/// (<see cref="ContentWait"/>) lasts 60 s, and after that as long as pieces keep arriving, up to 150 s: a slow link's 4 MB
/// logos no longer fail the match while they're still coming. A campaign match waits the same way, after the host's run
/// (<see cref="StepCampaignLoad"/>).
/// </summary>
internal static partial class MatchSetup
{
    private static ContentLoad? _contentLoad;

    /// <summary>
    /// True while a load waits for the host's custom content (an exhibition's, or a campaign match's after the host's run):
    /// the match relay's own load limit counts from when it's here.
    /// </summary>
    public static bool WaitingForContent => _contentLoad != null || _campaignLoad is { Content: { } };

    /// <summary>A load waiting for the host's custom content: loads once it's all here, or fails once the wait is over. Never throws.</summary>
    private static void UpdateContentLoad()
    {
        ContentLoad? load = _contentLoad;
        if (load == null)
        {
            return;
        }

        try
        {
            switch (StepContent(load.Wait, out string problem))
            {
                case ContentStep.Waiting:
                    return;
                case ContentStep.Failed:
                    _contentLoad = null;
                    FailContentLoad(problem);
                    return;
            }

            _contentLoad = null;
            if (!TryLoad(load.Values.Setup, out problem))
            {
                FailContentLoad(problem);
            }
        }
        catch (Exception ex)
        {
            _contentLoad = null;
            ModLog.Error("Match setup: loading a match with the host's custom content failed", ex);
            FailContentLoad($"setting it up failed ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>
    /// One frame of a wait for the host's custom content (<see cref="CustomContent.Waiting"/>): still waiting, here (logged:
    /// after how long, how much, how fast), or over without it (<paramref name="problem"/> says why). Logs once when the wait
    /// goes past its first 60 s because pieces still arrive. Allocates nothing while it waits.
    /// </summary>
    private static ContentStep StepContent(ContentWait wait, out string problem)
    {
        problem = string.Empty;
        double now = FrameClock.Seconds;
        CustomContent.AwaitedBytes(out long received, out long total);
        wait.Note(received, total, now);
        if (!CustomContent.Waiting())
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Match setup: the host's custom content is here, {now - wait.Since:0.0} s after the match's setup ({wait.Describe(now)}); loading the match."));
            return ContentStep.Here;
        }

        if (wait.Expired(now))
        {
            problem = $"the host's custom content {wait.Why(now)}: {CustomContent.WaitingFor()}";
            return ContentStep.Failed;
        }

        if (wait.ExtendsNow(now))
        {
            ModLog.Info($"Match setup: the host's custom content is still arriving ({wait.Describe(now)}); the match waits while it does " +
                $"(up to {ContentWait.MaxSeconds:0} s; {SafeText.Clean(CustomContent.WaitingFor(), 200)}).");
        }

        return ContentStep.Waiting;
    }

    /// <summary>Drops a load waiting for custom content (a newer setup, or the load was cancelled).</summary>
    private static void CancelContentLoad()
    {
        if (_contentLoad != null)
        {
            _contentLoad = null;
            ModLog.Info("Match setup: the load waiting for the host's custom content was cancelled.");
        }
    }

    /// <summary>A match waiting for custom content can't load after all: why goes in the log and to the match relay.</summary>
    private static void FailContentLoad(string problem)
    {
        _loadProblem = SafeText.Clean(problem, 240);
        ModLog.Warn($"Match setup: the match can't load here: {_loadProblem}.");
    }

    /// <summary>What a frame of a content wait found (<see cref="StepContent"/>).</summary>
    private enum ContentStep
    {
        Waiting,
        Here,
        Failed,
    }

    /// <summary>An exhibition's setup waiting for the host's custom content.</summary>
    private sealed record ContentLoad(Values Values, ContentWait Wait);
}
#endif
