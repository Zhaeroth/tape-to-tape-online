using System.Collections.Generic;
using T2TOnline.Core.Diagnostics;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// A friend's reasons for not following the host's view (<see cref="FollowReport.Reason"/>), as the host's log may write
/// them (<see cref="ForLog"/>): a reason comes from the friend's game, and a modified one could send any text. One that is
/// always the same words (<see cref="Fixed"/>) is written as it is; one whose first words are fixed and whose rest varies
/// (<see cref="LeadIns"/>) as those words and the length of the rest, so the host's log still says what kind of failure it
/// was; any other by its length. The friend's own log has every reason in full.
/// </summary>
public static class FollowReasons
{
    /// <summary>The host's match didn't load on the friend's PC (its own log says why).</summary>
    public const string MatchNotLoading = "the host's match isn't loading here (this PC's log says why)";

    /// <summary>A scene the friend's game loaded for the host didn't finish loading.</summary>
    public const string SceneLoadUnfinished = "a scene load didn't finish";

    /// <summary>The host's map view named no run.</summary>
    public const string MapWithoutRun = "the host's map view came without a run";

    /// <summary>The host's preview view named no run.</summary>
    public const string PreviewWithoutRun = "the host's preview came without a run";

    /// <summary>The friend's world map had no match preview to show the host's in.</summary>
    public const string NoMatchPreview = "this world map has no match preview";

    /// <summary>The host's preview named another run than the one it came with.</summary>
    public const string PreviewNotItsRun = "the host's preview isn't the run it says it is";

    /// <summary>The friend's run sync wasn't running when the trip to the host's map started.</summary>
    public const string RunSyncNotRunning = "run sync isn't running";

    /// <summary>The move to the host's screen failed with an error the friend's log holds.</summary>
    public const string ErrorSeeLog = "an error (see the log)";

    /// <summary>The friend's save guard wasn't on, so its game didn't load the host's run.</summary>
    public const string SaveGuardOff = "this PC's save guard isn't on (see its log), so the host's run isn't loaded here";

    /// <summary>The friend's game was asked for the host's run while it followed no host.</summary>
    public const string NotFollowing = "this PC isn't following a host";

    /// <summary>Every fixed reason.</summary>
    public static IReadOnlyCollection<string> Fixed { get; } = new HashSet<string>
    {
        MatchNotLoading, SceneLoadUnfinished, MapWithoutRun, PreviewWithoutRun, NoMatchPreview, PreviewNotItsRun,
        RunSyncNotRunning, ErrorSeeLog, SaveGuardOff, NotFollowing,
    };

    /// <summary>
    /// The first words of the reasons whose rest varies (a time limit, a run's number, an error's text, what the game said),
    /// as the friend's followers write them. A reason whose words change falls back to its length: nothing else depends on
    /// this list.
    /// </summary>
    public static IReadOnlyList<string> LeadIns { get; } = new[]
    {
        "an error (",
        "the title screen didn't load within ",
        "couldn't load the title screen: ",
        "the host's map view is malformed (",
        "the host's preview view is malformed (",
        "couldn't load the host's run",
        "this map has no ",
        "the host's run ",
        "the host's run moved on (",
        "the host's run was put away (",
        "the host's preview can't be held (",
        "couldn't load the world map: ",
        "the world map didn't load within ",
        "the world map loaded, but not the host's (",
        "this version can't show the host's ",
    };

    /// <summary>
    /// A friend's reason for the host's log: one of <see cref="Fixed"/> as it is, one that begins with one of
    /// <see cref="LeadIns"/> as those words and the length of the rest, any other by its length (<see cref="SentText.Reason(string?, IReadOnlyCollection{string}, IReadOnlyList{string})"/>).
    /// </summary>
    public static string ForLog(string? sent) => SentText.Reason(sent, Fixed, LeadIns);
}
