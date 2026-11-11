#if HAS_INTEROP
namespace T2TOnline.Plugin.Runtime;

/// <summary>
/// The developer keys F3, F4 and F2 (record a match, play it back in place, load its match fresh and play it) as the replay
/// check tour calls them: each method is what the key itself calls (<see cref="ModRuntime"/>), so the tour drives exactly
/// the keys' code without sending a key press. Every call is guarded as the keys' are; none throws.
/// </summary>
internal interface IStreamKeys
{
    /// <summary>What the stream tools hold now.</summary>
    StreamKeysState State { get; }

    /// <summary>F3: starts recording the match in play, or stops.</summary>
    void PressF3();

    /// <summary>F4: plays the last recording back in client mode, or stops.</summary>
    void PressF4();

    /// <summary>F2: loads the newest recording's match fresh and plays it, or cancels and stops.</summary>
    void PressF2();

    /// <summary>F2 on the recording at <paramref name="path"/> instead of the newest (nothing when something else has the match).</summary>
    void PressF2On(string path);
}

/// <summary>What the stream tools hold, for the replay tour's checks.</summary>
/// <param name="Recording">True while F3 records.</param>
/// <param name="Playing">True while a stream plays (F4's or F2's).</param>
/// <param name="RehearsalWaiting">True while F2's match is loading, before it plays.</param>
/// <param name="RecordingPath">The file F3 is recording (or last recorded), or null.</param>
/// <param name="PlayedFrames">Frames the current (or last) playback has shown.</param>
/// <param name="FailedStates">Object states the current (or last) playback failed to apply.</param>
/// <param name="StopReason">Why the last playback stopped; empty before any.</param>
/// <param name="RehearsalOutcome">What the last F2 came to, as its log line says it ("the loaded match has exactly the recording's setup", or why it didn't load); null before any.</param>
internal readonly record struct StreamKeysState(bool Recording, bool Playing, bool RehearsalWaiting, string? RecordingPath, long PlayedFrames, long FailedStates, string StopReason, string? RehearsalOutcome);
#endif
