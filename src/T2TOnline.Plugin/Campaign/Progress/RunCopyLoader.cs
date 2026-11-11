#if HAS_INTEROP
using System;
using System.Globalization;
using System.IO;
using Il2CppCysharp.Threading.Tasks;
using Il2CppData;
using Il2CppState;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// A friend's load of a copy of one of the host's runs (v1), for shared progress only: the run's end of a run
/// this PC held before its game restarted applies on it (<see cref="ProgressKeptRuns"/>). It's loaded with the game's own
/// code, as the game's <c>CampaignState.LoadDataInternal</c> loads a run: the squad's starting team
/// (<c>CampaignState.InstantiateStartingTeam</c>), then a run repository's <c>LoadRunData(team)</c>, which reads the file,
/// makes a new <c>RunData</c> and fills it. The repository is one of its own (a copy of the game's with the game's own lists,
/// pointed at <c>UserData\T2TOnline\campaign\KeptRun.es3</c>, as the run-start self-test's scratch load makes it:
/// <see cref="Menus.RunPreview.Repository"/>): the campaign state's own repositories and run, and so the game's own loads,
/// are never touched. The run is never put in the campaign state (except by the kept-run swap, for one batch), followed or
/// saved; the file is deleted as soon as the load is over. The run's load sets FMOD's "act" to its act
/// (<c>RunData.set_CurrentAct</c>): it's put back right after. One load at a time, polled from the friend's update; nothing
/// here throws.
/// </summary>
internal sealed class RunCopyLoader
{
    /// <summary>The copy's file name, in the mod's campaign folder (a session file, like HostRun.es3).</summary>
    public const string FileName = "KeptRun.es3";

    private const double TimeoutSeconds = 20;
    private const string ActParameter = "act";

    private Job? _job;

    /// <summary>Where the copy is written for the game's loader.</summary>
    public static string FilePath => Path.Combine(CampaignFiles.CampaignFolder, FileName);

    /// <summary>True while a copy is loading.</summary>
    public bool Busy => _job != null;

    /// <summary>
    /// Starts loading <paramref name="checkpoint"/>'s run (run <paramref name="run"/> of squad <paramref name="squad"/>);
    /// <paramref name="done"/> is called once, on the game thread: the loaded run, or null and why not. Null when it started;
    /// else why it can't (nothing was started, <paramref name="done"/> isn't called).
    /// </summary>
    public string? Start(string run, string squad, RunCheckpoint checkpoint, Action<RunData?, string> done)
    {
        if (_job != null)
        {
            return "another copy is loading";
        }

        if (!RunSync.CloudGuarded)
        {
            // Without the guard, a file of the mod's would reach Steam Cloud under its absolute path (RunSync.CloudCall).
            return "Steam Cloud's calls aren't guarded on this PC, so no copy of a run is written here";
        }

        byte[]? bytes = checkpoint.Get(RunCheckpointPart.Run);
        CampaignState? state = CampaignGame.State();
        RunSquadScriptableObject? squadAsset = state == null ? null : Squad(state, squad);
        RunDataRepositoryV2? game = state?._runDataRepository?.TryCast<RunDataRepository>()?._runDataRepositoryV2;
        if (bytes == null || state == null || squadAsset == null || game == null)
        {
            return bytes == null ? "the copy holds no run file" : state == null ? "the game's campaign isn't ready"
                : squadAsset == null ? $"this game has no squad {HostText.KeyForLog(squad)}" : "the game's run storage isn't the kind this version knows";
        }

        float act = ReadAct(out bool actRead);
        var job = new Job(run, checkpoint, done, act, actRead);
        try
        {
            CampaignFiles.WriteAtomically(FilePath, bytes);
            TeamData team = state.InstantiateStartingTeam(squadAsset); // a fresh starting team, as the game's own load makes one
            RunDataRepositoryV2 repository = Menus.RunPreview.Repository(game, FilePath);
            job.Started = Seconds;
            _job = job;
            job.Awaiter = repository.LoadRunData(team).GetAwaiter();
        }
        catch (Exception ex)
        {
            _job = null;
            Cleanup(job);
            return $"its load couldn't start ({ex.GetType().Name}: {CampaignFiles.ErrorText(ex)})";
        }

        Update(); // a load that finished inside the call is answered now
        return null;
    }

    /// <summary>Every frame: a load that finished is answered; one that takes too long is given up on.</summary>
    public void Update()
    {
        Job? job = _job;
        if (job?.Awaiter == null)
        {
            return;
        }

        RunData? loaded = null;
        string problem;
        try
        {
            if (!job.Awaiter.IsCompleted)
            {
                if (Seconds - job.Started < TimeoutSeconds)
                {
                    return;
                }

                problem = $"the game's loader didn't finish within {TimeoutSeconds:0} s";
            }
            else
            {
                loaded = job.Awaiter.GetResult();
                problem = loaded == null || loaded.isUnableToLoad ? "the game's loader made no run of it (its own warning is in the log above)"
                    : loaded.runID != job.Run ? $"the file held run {HostText.KeyForLog(loaded.runID)}, not {HostText.KeyForLog(job.Run)}"
                    : string.Empty;
            }
        }
        catch (Exception ex)
        {
            problem = $"the game's loader failed ({ModLog.SafeErrorText(ex)})";
        }

        _job = null;
        Cleanup(job);
        double seconds = Seconds - job.Started;
        Answer(job, problem.Length == 0 ? loaded : null, problem, seconds);
    }

    /// <summary>The session ended: a load still running is given up on (it ends on its own; nothing waits for it).</summary>
    public void Stop()
    {
        Job? job = _job;
        _job = null;
        if (job != null)
        {
            Cleanup(job);
            ModLog.Info($"Run sync: the copy of the host's run {job.Run} was still loading as the session ended; it's given up on (its batch comes again on a rejoin).");
        }

        DeleteFile();
    }

    private static double Seconds => UnityEngine.Time.realtimeSinceStartupAsDouble;

    private static void Answer(Job job, RunData? loaded, string problem, double seconds)
    {
        try
        {
            if (loaded != null)
            {
                ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Run sync: loaded a copy of the host's run {job.Run} (checkpoint {job.Checkpoint.Sequence}, {job.Checkpoint.RawBytes / 1024.0:0.0} KB) ") +
                    string.Create(CultureInfo.InvariantCulture, $"with the game's own run loader in {seconds:0.00} s, for shared progress only: it isn't followed, held or saved ") +
                    $"(act {loaded.CurrentAct}, floor {loaded.currentFloor}, a team of {loaded.teamData?.forwards?.Count ?? 0} skaters).");
            }

            job.Done(loaded, problem);
        }
        catch (Exception ex)
        {
            ModLog.Error($"Run sync: the copy of the host's run {job.Run} loaded, but taking it failed", ex);
        }
    }

    /// <summary>After a load (or its start failed): FMOD's "act" back as it was, the file deleted.</summary>
    private static void Cleanup(Job job)
    {
        try
        {
            if (job.ActRead)
            {
                float now = ReadAct(out bool read);
                if (read && Math.Abs(now - job.Act) > 0.001f)
                {
                    Il2CppFMODUnity.RuntimeManager.StudioSystem.setParameterByName(ActParameter, job.Act, false);
                    ModLog.Info($"Run sync: the copy's load set the music's act to {now:0}; it's back to {job.Act:0}.");
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Run sync: couldn't put the music's act back after loading a copy of the host's run ({ModLog.SafeErrorText(ex)}).");
        }

        DeleteFile();
    }

    private static void DeleteFile()
    {
        try
        {
            CampaignFiles.DeleteWithCopies(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModLog.Warn($"Run sync: couldn't delete {FileName} ({CampaignFiles.ErrorText(ex)}).");
        }
    }

    /// <summary>FMOD's global "act" now (the music follows it), or false when it can't be read.</summary>
    private static float ReadAct(out bool read)
    {
        try
        {
            read = Il2CppFMODUnity.RuntimeManager.StudioSystem.getParameterByName(ActParameter, out float value) == Il2CppFMOD.RESULT.OK;
            return value;
        }
        catch (Exception)
        {
            read = false;
            return 0f;
        }
    }

    private static RunSquadScriptableObject? Squad(CampaignState state, string id)
    {
        Il2CppSystem.Collections.Generic.List<RunSquadScriptableObject>? squads = state.squads;
        for (int i = 0; squads != null && i < squads.Count; i++)
        {
            RunSquadScriptableObject squad = squads[i];
            if (squad != null && squad.id == id)
            {
                return squad;
            }
        }

        return null;
    }

    /// <summary>One copy being loaded.</summary>
    private sealed class Job
    {
        public Job(string run, RunCheckpoint checkpoint, Action<RunData?, string> done, float act, bool actRead)
        {
            Run = run;
            Checkpoint = checkpoint;
            Done = done;
            Act = act;
            ActRead = actRead;
        }

        public string Run { get; }

        public RunCheckpoint Checkpoint { get; }

        public Action<RunData?, string> Done { get; }

        /// <summary>FMOD's "act" before the load.</summary>
        public float Act { get; }

        public bool ActRead { get; }

        public double Started { get; set; }

        public UniTask<RunData>.Awaiter? Awaiter { get; set; }
    }
}
#endif
