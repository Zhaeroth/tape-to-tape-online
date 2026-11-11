#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppData;
using Il2CppState;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// A friend's copies of the shared runs its game held: the newest <c>RunData</c> of each, by run id (the game
/// makes a new one at every load and reset, so the one kept is never changed by them). A batch whose run-bound parts are for
/// a run this game no longer holds (the host went on to the next run first, the session ended, or a rejoin brought it)
/// applies on the kept copy: put in the campaign state just for the batch (<see cref="Swap"/>), then the run held now goes
/// back, with the squad the game has cached for it (<c>CampaignState.GetCurrentSquad</c> answers from that cache, which only
/// a run's reset clears). Only shared runs are kept (the host's view named them); they last until the game closes. A run
/// this PC held before its game restarted can be kept again from a copy the host sends (v1,
/// <see cref="RunCopyLoader"/>). A kept <c>RunData</c> no longer in the campaign state is referenced by nothing of the game's,
/// so every one is marked to survive Unity's sweep of unused assets while kept, and let go when it's dropped. Game thread only.
/// </summary>
internal sealed class ProgressKeptRuns
{
    /// <summary>Most runs kept (the one held now, the one before, and one more).</summary>
    public const int MaxKept = 3;

    private readonly List<(string Run, RunData Data)> _runs = new();
    private readonly List<string> _ids = new();

    /// <summary>The ids of the runs kept.</summary>
    public IReadOnlyCollection<string> Ids => _ids;

    /// <summary>
    /// Keeps <paramref name="data"/> as shared run <paramref name="run"/>'s newest, safe from Unity's sweep (the oldest kept
    /// goes beyond <see cref="MaxKept"/>).
    /// </summary>
    public void Keep(string run, RunData data)
    {
        int at = _runs.FindIndex(r => r.Run == run);
        if (at >= 0)
        {
            if (_runs[at].Data.Pointer == data.Pointer)
            {
                return;
            }

            LetGo(_runs[at]);
            _runs.RemoveAt(at);
        }

        data.hideFlags |= UnityEngine.HideFlags.DontUnloadUnusedAsset;
        _runs.Add((run, data));
        if (_runs.Count > MaxKept)
        {
            LetGo(_runs[0]);
            _runs.RemoveAt(0);
        }

        _ids.Clear();
        _ids.AddRange(_runs.Select(r => r.Run));
    }

    /// <summary>True when shared run <paramref name="run"/> is kept.</summary>
    public bool Has(string run) => _runs.Exists(r => r.Run == run);

    /// <summary>
    /// Puts kept run <paramref name="run"/> in <paramref name="campaign"/> until the result is disposed (then the run held
    /// before goes back). Null when it isn't kept, or Unity destroyed it (then it isn't kept any more).
    /// </summary>
    public IDisposable? Swap(CampaignState campaign, string run)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        int at = _runs.FindIndex(r => r.Run == run);
        if (at < 0)
        {
            return null;
        }

        if (_runs[at].Data == null || _runs[at].Data.WasCollected)
        {
            // Unity destroyed it (a sweep of unused assets): it's gone, and the batch says so.
            _runs.RemoveAt(at);
            _ids.Clear();
            _ids.AddRange(_runs.Select(r => r.Run));
            return null;
        }

        // The squad cached for the run held now isn't this run's, and what the game caches during the batch isn't the held
        // run's: looked up again from the kept run for the batch, and put back after it (bench snapshots and quests are off
        // on some squads, and the game asks by that cache).
        RunData? held = campaign.runData;
        RunSquadScriptableObject? heldSquad = campaign._currentSquadCached;
        campaign.runData = _runs[at].Data;
        campaign._currentSquadCached = null;
        return new PutBack(campaign, held, heldSquad);
    }

    /// <summary>A run no longer kept: Unity may sweep it again (when the game doesn't hold it).</summary>
    private static void LetGo((string Run, RunData Data) kept)
    {
        try
        {
            if (kept.Data != null && !kept.Data.WasCollected)
            {
                kept.Data.hideFlags &= ~UnityEngine.HideFlags.DontUnloadUnusedAsset;
            }
        }
        catch (Exception)
        {
            // already gone: nothing to let go
        }
    }

    private sealed class PutBack : IDisposable
    {
        private readonly CampaignState _campaign;
        private readonly RunData? _held;
        private readonly RunSquadScriptableObject? _heldSquad;
        private bool _done;

        public PutBack(CampaignState campaign, RunData? held, RunSquadScriptableObject? heldSquad)
        {
            _campaign = campaign;
            _held = held;
            _heldSquad = heldSquad;
        }

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _campaign.runData = _held;
            _campaign._currentSquadCached = _heldSquad;
        }
    }
}
#endif
