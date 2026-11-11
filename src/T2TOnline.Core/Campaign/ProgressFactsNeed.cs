namespace T2TOnline.Core.Campaign;

/// <summary>
/// Which of a friend's facts <see cref="ProgressApplyPolicy.Decide"/> needs at all. A batch can wait for many seconds
/// (the host's end-of-match screens keep its match loaded here) or a whole session (ShareProgress off), and the applier
/// asks for the facts every frame of that wait: the runs (<see cref="ApplyFacts.HeldRun"/>, <see cref="ApplyFacts.ViewRun"/>)
/// cost game calls and a new string each time, and the policy reads them only for a batch it lets through.
/// Game-independent.
/// </summary>
public static class ProgressFactsNeed
{
    /// <summary>
    /// True when every batch waits on these facts alone, whatever the runs are: sharing is off, or
    /// <paramref name="block"/> holds batches back now (<see cref="ProgressApplyPolicy.BlockHolds"/>, the rule the policy goes by).
    /// The runs then needn't be read.
    /// </summary>
    public static bool WaitsWhateverTheRuns(bool sharing, ApplyBlock block, bool ending, bool hostRunOver) =>
        !sharing || ProgressApplyPolicy.BlockHolds(block, ending, hostRunOver);
}
