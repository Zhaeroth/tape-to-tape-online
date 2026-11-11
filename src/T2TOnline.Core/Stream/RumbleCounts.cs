using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using T2TOnline.Core.Protocol;

namespace T2TOnline.Core.Stream;

/// <summary>What became of one rumble record on a friend's PC.</summary>
public enum RumbleOutcome
{
    /// <summary>Played on the friend's pad.</summary>
    Played,

    /// <summary>For another player (another friend, or a Rewired player this PC isn't).</summary>
    ForOthers,

    /// <summary>Not played: this PC plays with the keyboard and mouse, or holds no pad (as a couch keyboard player).</summary>
    NoPad,

    /// <summary>Not played: the pad is unplugged.</summary>
    Unplugged,

    /// <summary>Not played: this PC's own Controller rumble setting is off (or the game's settings aren't loaded).</summary>
    SettingOff,

    /// <summary>Not played: the game's nudge of that type isn't on this PC.</summary>
    NoNudgeData,

    /// <summary>Not played: the pad can't vibrate.</summary>
    NoVibration,

    /// <summary>Limited: more than the pad takes at once or in a second (<see cref="RumbleVerdict.TooMany"/>).</summary>
    TooMany,

    /// <summary>Limited: the pad had buzzed without a break for too long (<see cref="RumbleVerdict.TooLong"/>).</summary>
    TooLong,

    /// <summary>Refused: the record didn't decode, or playing it failed.</summary>
    Failed,
}

/// <summary>A friend's rumble records by what became of them (per match, and summed over a session), for the log.</summary>
public sealed class RumbleCounts
{
    private static readonly RumbleOutcome[] Skips =
        { RumbleOutcome.SettingOff, RumbleOutcome.NoPad, RumbleOutcome.Unplugged, RumbleOutcome.NoVibration, RumbleOutcome.NoNudgeData };

    private readonly long[] _counts = new long[Enum.GetValues<RumbleOutcome>().Length];

    /// <summary>How many records had this outcome.</summary>
    public long this[RumbleOutcome outcome] => _counts[(int)outcome];

    /// <summary>Every record counted.</summary>
    public long Total => _counts.Sum();

    /// <summary>Records for this PC's player (everything but <see cref="RumbleOutcome.ForOthers"/> and <see cref="RumbleOutcome.Failed"/>).</summary>
    public long ForYou => Total - this[RumbleOutcome.ForOthers] - this[RumbleOutcome.Failed];

    /// <summary>Records the limiter refused.</summary>
    public long Limited => this[RumbleOutcome.TooMany] + this[RumbleOutcome.TooLong];

    /// <summary>Records for this PC's player that weren't played for another reason than the limiter.</summary>
    public long Skipped => Skips.Sum(o => this[o]);

    /// <summary>Counts one record.</summary>
    public void Add(RumbleOutcome outcome) => _counts[(int)outcome]++;

    /// <summary>Adds another's counts (a match's into a session's).</summary>
    public void AddFrom(RumbleCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (int i = 0; i < _counts.Length; i++)
        {
            _counts[i] += other._counts[i];
        }
    }

    /// <summary>Starts again.</summary>
    public void Clear() => Array.Clear(_counts);

    /// <summary>
    /// For the log: "41 for your skater (Rewired player 1): 38 played on your pad, 3 limited (2 over the rate, 1 after 3 s of
    /// buzzing), 0 skipped; 77 for the other players", naming each kind of skip that happened.
    /// </summary>
    public string Describe(int yourRewiredPlayer)
    {
        string who = yourRewiredPlayer >= 0 ? $" (Rewired player {yourRewiredPlayer})" : " (this PC isn't a friend in a session)";
        string limited = Limited == 0 ? "0 limited"
            : $"{Limited} limited ({this[RumbleOutcome.TooMany]} over {RumbleLimiter.PerSecond} a second, {this[RumbleOutcome.TooLong]} after " +
              string.Create(CultureInfo.InvariantCulture, $"{RumbleLimiter.MaxContinuousMicros / 1_000_000.0:0.#} s of buzzing)");
        var skips = Skips.Where(o => this[o] > 0).Select(o => $"{SkipName(o)} {this[o]}").ToList();
        string skipped = skips.Count == 0 ? "0 skipped" : $"{Skipped} skipped ({string.Join(", ", skips)})";
        string failed = this[RumbleOutcome.Failed] > 0 ? $"; {this[RumbleOutcome.Failed]} refused or failed" : string.Empty;
        return $"{ForYou} for your skater{who}: {this[RumbleOutcome.Played]} played on your pad, {limited}, {skipped}; " +
            $"{this[RumbleOutcome.ForOthers]} for the other players{failed}";
    }

    private static string SkipName(RumbleOutcome outcome) => outcome switch
    {
        RumbleOutcome.SettingOff => "your Controller rumble setting off",
        RumbleOutcome.NoPad => "no pad (keyboard and mouse)",
        RumbleOutcome.Unplugged => "pad unplugged",
        RumbleOutcome.NoVibration => "pad can't vibrate",
        RumbleOutcome.NoNudgeData => "the game's nudge not found here",
        _ => outcome.ToString(),
    };
}

/// <summary>The host's rumble records by the Rewired player they were for (per match, and summed over a session), for the log.</summary>
public sealed class RumbleTally
{
    private readonly long[] _byPlayer = new long[RumbleMessage.MaxRewiredPlayer + 1];

    /// <summary>Every rumble counted.</summary>
    public long Total { get; private set; }

    /// <summary>The rumbles counted for one Rewired player (0 for one out of range).</summary>
    public long Of(int rewiredPlayer) => rewiredPlayer >= 0 && rewiredPlayer < _byPlayer.Length ? _byPlayer[rewiredPlayer] : 0;

    /// <summary>Counts one for <paramref name="rewiredPlayer"/>; one out of range isn't counted.</summary>
    public void Add(int rewiredPlayer)
    {
        if (rewiredPlayer >= 0 && rewiredPlayer < _byPlayer.Length)
        {
            _byPlayer[rewiredPlayer]++;
            Total++;
        }
    }

    /// <summary>Adds another's counts (a match's into a session's).</summary>
    public void AddFrom(RumbleTally other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (int i = 0; i < _byPlayer.Length; i++)
        {
            _byPlayer[i] += other._byPlayer[i];
        }

        Total += other.Total;
    }

    /// <summary>Starts again.</summary>
    public void Clear()
    {
        Array.Clear(_byPlayer);
        Total = 0;
    }

    /// <summary>For the log: "Rewired player 1: 41, Rewired player 3: 2", or "none".</summary>
    public string Describe()
    {
        IEnumerable<string> players = Enumerable.Range(0, _byPlayer.Length).Where(p => _byPlayer[p] > 0).Select(p => $"Rewired player {p}: {_byPlayer[p]}");
        return Total == 0 ? "none" : string.Join(", ", players);
    }
}
