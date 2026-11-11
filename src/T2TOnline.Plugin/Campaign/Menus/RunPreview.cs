#if HAS_INTEROP
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Il2CppCysharp.Threading.Tasks;
using Il2CppData;
using Il2CppRogue.Relics.States;
using Il2CppSkaters.Injuries;
using Il2CppState;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Client;
using T2TOnline.Plugin.Diagnostics;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// Host: the run in the game's memory as its own run repository writes it, without saving the run. At a run-start menu the host's
/// new run exists only in memory (its first save is the world map's), so a friend can't get it as a checkpoint. A copy of the game's
/// run repositories (<c>RunDataRepositoryV2</c> and <c>RunStatsStateRepository</c>, with the game's own talent, relic and ability
/// lists) is pointed at the mod's <c>campaign/HostPreview.es3</c> and writes the run there: the same bytes the game's save
/// would, with the team's state, injuries and pacing set as <c>CampaignState.SaveRunData</c> sets them, and put back afterwards.
/// Nothing of the host's game changes: not its run file or co-op slot, not its run timer (the game's save restarts it), not the run
/// in memory. The mod patches neither repository method, so no checkpoint is published. A forced <c>CampaignState.SaveRunData</c>
/// was rejected (D67): it restarts the run timer and writes the co-op slot with a run that has no map yet.
/// </summary>
internal static class RunPreview
{
    /// <summary>The file the host's preview is written to (overwritten each time; deleted at start-up and when hosting stops).</summary>
    public static string PreviewPath => Path.Combine(CampaignFiles.CampaignFolder, "HostPreview.es3");

    /// <summary>The run-start self-test's second file: the preview loaded the way a friend's game would and written again.</summary>
    public static string CheckPath => Path.Combine(CampaignFiles.CampaignFolder, "HostPreviewCheck.es3");

    /// <summary>
    /// The run in <paramref name="state"/>'s memory as the game's run repository writes it, read back from the mod's file, with
    /// how long that took; null, with why, when it can't be made (no run, a repository this version doesn't know, this PC's
    /// save guard holding, the write not landing, a type a friend's game would refuse). Never throws for the game's state.
    /// </summary>
    public static byte[]? Make(CampaignState state, string path, out string problem, out double milliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        milliseconds = 0;
        if (!RunSync.CloudGuarded)
        {
            // The repository's save hands its file to Steam Cloud (StorageUtils.SaveToPlatform): only run sync's
            // patch keeps a mod file out (an untested build's offline tools run without it).
            problem = "Steam Cloud isn't kept out of the mod's files on this PC (run sync's patches aren't in), so nothing is written";
            return null;
        }

        if (SaveGuard.Refusing)
        {
            problem = "this PC's save guard holds (a restored backup waits for a restart, a host's match or campaign is here, or a check tour runs), so nothing can be written";
            return null;
        }

        RunData? run = state.runData;
        RunStatsState? stats = state.runStatsState;
        if (run == null || run.teamData == null || stats == null)
        {
            problem = "the game holds no run";
            return null;
        }

        RunDataRepositoryV2? game = state._runDataRepository?.TryCast<RunDataRepository>()?._runDataRepositoryV2;
        RunStatsStateRepository? gameStats = state._runStatsStateRepository?.TryCast<RunStatsStateRepository>();
        if (game == null || gameStats == null)
        {
            problem = "the game's run storage isn't the kind this version knows";
            return null;
        }

        try
        {
            Directory.CreateDirectory(CampaignFiles.CampaignFolder);
            CampaignFiles.DeleteWithCopies(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"the mod's campaign folder can't be written ({CampaignFiles.ErrorText(ex)})";
            return null;
        }

        RunDataRepositoryV2 repository = Repository(game, path);
        var statsRepository = new RunStatsStateRepository { _fileName = path, _es3SettingsLoader = gameStats._es3SettingsLoader };
        if (repository._fileName != path || statsRepository._fileName != path)
        {
            problem = "the copy of the run storage didn't take the mod's file";
            return null;
        }

        // As the game's save sets them: the team's state now (with each relic's "displayed" mark from the last), the injuries
        // and the pacing. Put back whatever happens: the run in memory must be the game's own afterwards.
        TeamState? teamState = run.teamState;
        ForwardInjuries? injuries = run.forwardInjuries;
        InjuryPacingData? pacing = run.injuryPacingData;
        bool written;
        try
        {
            TeamState now = run.teamData.SaveState();
            KeepDisplayed(teamState, now);
            run.teamState = now;
            InjurySystem? system = state._injurySystem;
            if (system != null)
            {
                run.forwardInjuries = system._forwardInjuries;
                run.injuryPacingData = system._pacingService?._pacingData!;
            }

            UniTask<bool>.Awaiter saving = repository.SaveRunData(run).GetAwaiter();
            statsRepository.SaveRunStatsState(stats);

            // The game's save runs straight through: it has finished here.
            written = saving.IsCompleted && saving.GetResult();
        }
        finally
        {
            run.teamState = teamState!;
            run.forwardInjuries = injuries!;
            run.injuryPacingData = pacing!;
        }

        if (!written)
        {
            problem = "the game's run repository didn't finish writing it";
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = CampaignFiles.ReadShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"it was written, but can't be read ({CampaignFiles.ErrorText(ex)})";
            return null;
        }

        milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (bytes.Length == 0 || bytes.Length > RunPreviewSet.MaxRunBytes)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"it's {bytes.Length / 1024.0:0.0} KB (1 byte to {RunPreviewSet.MaxRunBytes / 1024} KB go)");
            return null;
        }

        // Friends' games run the same check and would refuse it.
        if (!RunFileTypes.TryCheck(bytes, RunFileTypes.RunFile, out string types))
        {
            problem = $"a friend's game would refuse it ({SafeText.Clean(types, 160)})";
            return null;
        }

        problem = string.Empty;
        return bytes;
    }

    /// <summary>
    /// "squad vanilla, act 1, floor 0, run 0a1b2c3d-...", from a run file's bytes, for the log (each id only when it has an id's
    /// shape, as every id read from a file is: <see cref="HostText.KeyForLog(string?)"/>).
    /// </summary>
    public static string Summary(byte[] bytes) =>
        RunFileSummary.TryRead(bytes, out RunFileSummary? run) && run != null
            ? $"squad {HostText.KeyForLog(run.SquadId)}, act {run.Act}, floor {run.Floor}, run {HostText.KeyForLog(run.RunId)}"
            : "a run whose squad can't be read";

    /// <summary>
    /// A copy of the game's run repository with the game's own lists, pointed at <paramref name="path"/>: every repository
    /// field the game's own has (since the Nov 6 build, no logo repository: the game's run file no longer resolves logos
    /// through it).
    /// </summary>
    public static RunDataRepositoryV2 Repository(RunDataRepositoryV2 game, string path) => new()
    {
        _talentRepository = game._talentRepository,
        _relicRepository = game._relicRepository,
        _abilityRepository = game._abilityRepository,
        _fileName = path,
    };

    /// <summary>Deletes the preview files and Easy Save's copies of them (start-up, hosting stopped, the self-test's end). Never throws.</summary>
    public static void CleanUp(string why)
    {
        int deleted = 0;
        foreach (string path in new[] { PreviewPath, CheckPath })
        {
            try
            {
                deleted += CampaignFiles.DeleteWithCopies(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Run start: couldn't delete {Path.GetFileName(path)} ({CampaignFiles.ErrorText(ex)}).");
            }
        }

        if (deleted > 0)
        {
            ModLog.Trace($"Run start: deleted {deleted} run-start preview file(s) ({why}).");
        }
    }

    /// <summary>
    /// The game's save keeps each relic's "displayed" mark from the team's last state (<c>CampaignState.SaveRunData</c>: a relic state of the same id).
    /// </summary>
    private static void KeepDisplayed(TeamState? before, TeamState now)
    {
        Il2CppList.List<RelicState>? old = before?.relicStates;
        Il2CppList.List<RelicState>? fresh = now.relicStates;
        for (int i = 0; old != null && fresh != null && i < fresh.Count; i++)
        {
            RelicState? state = fresh[i];
            for (int j = 0; state != null && j < old.Count; j++)
            {
                if (old[j] != null && old[j].Id == state.Id)
                {
                    state.WasDisplayed = old[j].WasDisplayed;
                    break;
                }
            }
        }
    }
}
#endif
