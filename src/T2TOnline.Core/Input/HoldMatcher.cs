using System.Collections.Generic;

namespace T2TOnline.Core.Input;

/// <summary>
/// Pairs the holds seen at the two ends of an input path on one PC (the real pad going in, the virtual pad coming out)
/// and gives each pair's lengths, so the whole path's effect on hold times can be measured in real time units.
/// </summary>
public sealed class HoldMatcher
{
    private const int MaxUnpaired = 64;
    private readonly HoldTracker _sent = new();
    private readonly HoldTracker _played = new();
    private readonly List<CompletedHold> _sentDone = new();
    private readonly List<CompletedHold> _playedDone = new();

    /// <summary>Pairs found so far, oldest first; the caller clears it.</summary>
    public List<MatchedHold> Matched { get; } = new();

    /// <summary>Records the buttons held on the input end at <paramref name="timestamp"/>.</summary>
    public void ObserveSent(long timestamp, PadButtons held) => _sent.Observe(timestamp, held, _sentDone);

    /// <summary>Records the buttons held on the output end at <paramref name="timestamp"/>, and pairs finished holds.</summary>
    public void ObservePlayed(long timestamp, PadButtons held)
    {
        _played.Observe(timestamp, held, _playedDone);
        foreach (CompletedHold played in _playedDone)
        {
            int match = _sentDone.FindIndex(sent => sent.Button == played.Button);
            if (match < 0)
            {
                continue;
            }

            Matched.Add(new MatchedHold(played.Button, _sentDone[match].Duration, played.Duration));
            _sentDone.RemoveAt(match);
        }

        _playedDone.Clear();
        if (_sentDone.Count > MaxUnpaired)
        {
            _sentDone.RemoveRange(0, _sentDone.Count - MaxUnpaired);
        }
    }
}

/// <summary>One hold, as it went in and as it came out.</summary>
/// <param name="Button">The button.</param>
/// <param name="SentDuration">Length on the input end.</param>
/// <param name="PlayedDuration">Length on the output end, in the same unit.</param>
public readonly record struct MatchedHold(PadButtons Button, long SentDuration, long PlayedDuration)
{
    /// <summary>Output minus input.</summary>
    public long Error => PlayedDuration - SentDuration;
}
