using System;
using System.Collections.Generic;
using System.Text;
using T2TOnline.Core.Serialization;

namespace T2TOnline.Core.Campaign;

/// <summary>
/// The host's first-time campaign tip as a menu-board item of its own (type <see cref="Type"/>): the tip's id (what a friend's game
/// finds its own copy by), its speech sequence's name (a check) and line count, and whether it shows the dark overlay. Its values come
/// from the host: <see cref="TryRead"/> checks each and returns false on anything malformed; it never throws.
/// </summary>
/// <param name="Id">The tip's id ("campaignMap").</param>
/// <param name="Sequence">Its speech sequence's name ("" when it has none).</param>
/// <param name="Lines">How many lines its sequence has.</param>
/// <param name="Dark">It shows the dark overlay (the game's tips always do).</param>
public sealed record CampaignTipData(string Id, string Sequence, int Lines, bool Dark)
{
    /// <summary>The board item's id and type.</summary>
    public const string Type = "CampaignTip";

    /// <summary>Longest id or sequence name, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 64;

    /// <summary>Most lines said.</summary>
    public const int MaxLines = 999;

    private const string IdKey = "tip.id";
    private const string SequenceKey = "tip.seq";
    private const string LinesKey = "tip.lines";
    private const string DarkKey = "tip.dark";

    /// <summary>The item's data. Throws <see cref="ArgumentException"/> for an id or name out of range (the host's game made them: never expected).</summary>
    public MenuData ToData()
    {
        if (Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(Id));
        }

        return new MenuData(
            new List<KeyValuePair<string, NetValue>>
            {
                new(IdKey, NetValue.FromString(Id)),
                new(SequenceKey, NetValue.FromString(Sequence)),
                new(LinesKey, NetValue.FromInt(Lines)),
                new(DarkKey, NetValue.FromBool(Dark)),
            },
            Array.Empty<MenuItem>());
    }

    /// <summary>The tip in an item's data; false for anything missing, malformed or out of range. Never throws.</summary>
    public static bool TryRead(MenuData data, out CampaignTipData? tip)
    {
        tip = null;
        if (data == null || !data.TryGet(IdKey, out NetValue id) || id.Kind != NetValueKind.Text || !data.TryGet(SequenceKey, out NetValue sequence) ||
            sequence.Kind != NetValueKind.Text || !data.TryGet(LinesKey, out NetValue lines) || lines.Kind != NetValueKind.Whole ||
            !data.TryGet(DarkKey, out NetValue dark) || dark.Kind != NetValueKind.Flag || lines.WholeValue is < 0 or > MaxLines)
        {
            return false;
        }

        var read = new CampaignTipData(id.Text ?? string.Empty, sequence.Text ?? string.Empty, (int)lines.WholeValue, dark.WholeValue != 0);
        if (read.Problem() != null)
        {
            return false;
        }

        tip = read;
        return true;
    }

    /// <summary>Why the tip can't go (an empty or over-long id, an over-long name, a control character, a line count out of range), or null.</summary>
    public string? Problem() =>
        string.IsNullOrEmpty(Id) ? "the tip has no id"
        : Name(Id, "the tip's id") ?? Name(Sequence, "the tip's sequence") ?? (Lines is < 0 or > MaxLines ? $"the tip's {Lines} lines are out of range" : null);

    private static string? Name(string? text, string what)
    {
        if (text == null)
        {
            return $"{what} is missing";
        }

        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                return $"{what} has a control character";
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetByteCount(text) > MaxNameBytes ? $"{what} is over {MaxNameBytes} bytes" : null;
        }
        catch (ArgumentException)
        {
            return $"{what} isn't valid text";
        }
    }
}

/// <summary>What the host's side does with its campaign tip this frame (<see cref="CampaignTipTimeline.Host"/>).</summary>
public enum HostTipStep
{
    /// <summary>Nothing (none armed or up, or one up and still showing, or one armed not showing yet).</summary>
    Wait,

    /// <summary>The tip armed shows: it's up (and goes to friends).</summary>
    Show,

    /// <summary>The tip armed didn't show in time (the game's own rules kept it back): forgotten.</summary>
    Disarm,

    /// <summary>The tip up has ended (its overlay went, or it's been up implausibly long): its board item closes.</summary>
    End,
}

/// <summary>What a friend's side does with the host's campaign tip this frame (<see cref="CampaignTipTimeline.Friend"/>).</summary>
public enum FriendTipStep
{
    /// <summary>Nothing yet (not on its screen, or playing while the host's still shows).</summary>
    Wait,

    /// <summary>Play it here now.</summary>
    Play,

    /// <summary>It waited too long for this PC to be on its screen: left out.</summary>
    LeftOut,

    /// <summary>The host's ended before this PC played it: not shown here.</summary>
    NeverShown,

    /// <summary>The host's ended and this PC's still shows: skip it on (the game's own per-anchor skip).</summary>
    RunOut,

    /// <summary>This PC's has ended too.</summary>
    Done,

    /// <summary>This PC's still shows long after the host's ended: taken down.</summary>
    TakeDown,
}

/// <summary>
/// The timing of the host's campaign tip on both PCs: pure decisions over what the game
/// glue sees each frame, so they're tested here.
/// </summary>
public static class CampaignTipTimeline
{
    /// <summary>How long after the game looked a tip up its overlay may take to show (it waits three frames, then fades in).</summary>
    public const double ConfirmSeconds = 1.5;

    /// <summary>How many frames that takes at least: the game's wait is in frames, and a scene's loading frames can be long.</summary>
    public const int ConfirmFrames = 30;

    /// <summary>Longest a tip is taken to be up (past it, its board item closes regardless).</summary>
    public const double MaxTipSeconds = 600;

    /// <summary>How long a friend waits to be on the tip's screen (with a speech presenter) before leaving it out.</summary>
    public const double StartWaitSeconds = 15;

    /// <summary>How long a friend's tip may take to run out after the host's ended before it's taken down.</summary>
    public const double RunOutSeconds = 5;

    /// <summary>
    /// The host's step: <paramref name="armedAt"/> when a tip is armed (null: none), <paramref name="upSince"/> when one is up
    /// (null: none), <paramref name="overlayUp"/> whether the presenter's dark overlay shows now, and
    /// <paramref name="armedFrames"/> how many frames the armed tip has waited (a tip is given up only past both
    /// <see cref="ConfirmSeconds"/> and <see cref="ConfirmFrames"/>).
    /// </summary>
    public static HostTipStep Host(double? armedAt, double? upSince, bool overlayUp, double now, int armedFrames = int.MaxValue)
    {
        if (upSince is double since)
        {
            return !overlayUp || now - since > MaxTipSeconds ? HostTipStep.End : HostTipStep.Wait;
        }

        if (armedAt is not double armed)
        {
            return HostTipStep.Wait;
        }

        return now - armed > ConfirmSeconds && armedFrames > ConfirmFrames ? HostTipStep.Disarm : overlayUp ? HostTipStep.Show : HostTipStep.Wait;
    }

    /// <summary>
    /// A friend's step: whether it has played the tip, whether the host's has ended, whether this PC is on the tip's screen
    /// (its scene, with a speech presenter), whether its overlay shows now, when the tip came and when the host's ended.
    /// </summary>
    public static FriendTipStep Friend(bool started, bool hostEnded, bool onItsScreen, bool overlayUp, double arrivedAt, double endedAt, double now)
    {
        if (!hostEnded)
        {
            if (started)
            {
                return FriendTipStep.Wait;
            }

            return now - arrivedAt > StartWaitSeconds ? FriendTipStep.LeftOut : onItsScreen ? FriendTipStep.Play : FriendTipStep.Wait;
        }

        if (!started)
        {
            return FriendTipStep.NeverShown;
        }

        return !overlayUp ? FriendTipStep.Done : now - endedAt > RunOutSeconds ? FriendTipStep.TakeDown : FriendTipStep.RunOut;
    }
}
