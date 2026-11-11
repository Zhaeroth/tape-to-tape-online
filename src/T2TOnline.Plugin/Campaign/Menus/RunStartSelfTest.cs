#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2CppCysharp.Threading.Tasks;
using Il2CppData;
using Il2CppState;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Online;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// The run-start carrier's self-test (Ctrl+F11, only with the DevTools setting): on one PC, what a friend's game would get at a
/// run-start menu is made and taken back apart, and the log says whether every field survived. Run at the title screen (after a
/// squad is picked) or on the world map; never while this PC follows a host (its run is the host's then).
/// <list type="number">
/// <item>The run: this PC's in-memory run written as the preview would be (<see cref="RunPreview.Make"/>, the mod's
/// <c>campaign/HostPreview.es3</c>): its size, squad, act and floor, and the time it took.</item>
/// <item>The skaters: the run team's forwards and the game's free agent templates (up to 16), each read as the host reads an
/// offered skater (<see cref="SkaterModels.Read"/>), sent through the run preview attachment's own encoding and decoding,
/// built the way a friend's game builds it (<see cref="SkaterModels.Builder"/>) and read back with the game's own serializer:
/// "all fields equal", or the first field that isn't for each that differs.</item>
/// <item>The run again: the preview file loaded the way a friend's game loads a run, into a scratch run that never touches
/// the game (a copy of the run repositories over a fresh starting team), written once more with the game's own serializer
/// (<c>campaign/HostPreviewCheck.es3</c>), and the two files compared value by value (<see cref="RunFileDiff"/>).</item>
/// </list>
/// Nothing here saves the run, changes the run in memory or loads one into the game; both files are deleted at the end.
/// </summary>
internal static class RunStartSelfTest
{
    private const double LoadTimeoutSeconds = 15;
    private const int MaxSkaters = RunPreviewSet.MaxSkaters * 2;
    private const int LoggedDifferences = 8;

    private static Check? _check;

    /// <summary>Ctrl+F11: runs the self-test, or says why not. Its last part takes a few frames (<see cref="Update"/>).</summary>
    public static void Start()
    {
        if (_check != null)
        {
            ModLog.Warn("Run start: the carrier self-test (Ctrl+F11) is still running; wait for its result.");
            return;
        }

        if (CampaignRelay.Current is { Following: true } || RunSync.Instance?.HoldingHostRun == true)
        {
            ModLog.Warn("Run start: carrier self-test (Ctrl+F11): not while this PC follows a host (the run here is the host's).");
            return;
        }

        CampaignState? state = CampaignGame.State();
        RunData? run = state?.runData;
        if (state == null || run?.teamData == null)
        {
            ModLog.Warn("Run start: carrier self-test (Ctrl+F11): the game holds no run yet (pick a squad, or go to the world map, first).");
            return;
        }

        ModLog.Info($"Run start: carrier self-test (Ctrl+F11) on this PC's run (squad {SafeText.Clean(run.squadId, 32)}): making what a friend's game would get at a run-start menu...");
        byte[]? preview = RunPreview.Make(state, RunPreview.PreviewPath, out string problem, out double milliseconds);
        if (preview == null)
        {
            ModLog.Warn($"Run start: carrier self-test: the run preview couldn't be made: {problem}.");
            RunPreview.CleanUp("the self-test stopped");
            return;
        }

        ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Run start: carrier self-test: the run preview is {preview.Length / 1024.0:0.0} KB, made in {milliseconds:0.0} ms with a copy of the game's run repository ") +
            $"({RunPreview.Summary(preview)}; its types pass a friend's check; the run in memory, its timer and its run file are untouched).");
        Skaters(state, preview);
        BeginReload(state, preview);
    }

    /// <summary>Every frame: the scratch load's end, when the self-test is running. Never throws.</summary>
    public static void Update()
    {
        Check? check = _check;
        if (check == null)
        {
            return;
        }

        try
        {
            if (!check.Run.IsCompleted || !check.Stats.IsCompleted)
            {
                if (Seconds(check.Since) > LoadTimeoutSeconds)
                {
                    Finish($"the scratch load didn't finish within {LoadTimeoutSeconds:0} s (it's left to finish by itself)");
                }

                return;
            }

            RunData? loaded = check.Run.GetResult();
            RunStatsState? stats = check.Stats.GetResult();
            Compare(check, loaded, stats);
        }
        catch (Exception ex)
        {
            Finish($"an error ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>
    /// The skater carrier: each skater read as an offered skater is, through the attachment's encoding, built as a friend's game
    /// builds it and read back. Logged.
    /// </summary>
    private static void Skaters(CampaignState state, byte[] preview)
    {
        var forwards = new List<ForwardData>();
        Il2CppList.List<ForwardData>? team = state.runData.teamData.GetForwards(false);
        for (int i = 0; team != null && i < team.Count && forwards.Count < MaxSkaters; i++)
        {
            if (team[i] != null)
            {
                forwards.Add(team[i]);
            }
        }

        int fromTeam = forwards.Count;
        Il2CppList.List<ForwardData>? agents = state.freeAgents;
        for (int i = 0; agents != null && i < agents.Count && forwards.Count < MaxSkaters; i++)
        {
            if (agents[i] != null)
            {
                forwards.Add(agents[i]);
            }
        }

        var read = new List<RunStartSkater>();
        var failures = new List<string>();
        foreach (ForwardData forward in forwards)
        {
            RunStartSkater? skater = SkaterModels.Read(forward, out string problem);
            if (skater == null)
            {
                failures.Add($"{Name(forward)} can't be read ({problem})");
            }
            else
            {
                read.Add(skater);
            }
        }

        var set = new RunPreviewSet
        {
            Menu = RunStartMenu.OldSquad,
            Mode = RunPreviewSet.FullSequence,
            RunFile = preview,
            SuperStars = read.Take(RunPreviewSet.MaxSkaters).ToList(),
            RandomSkaters = read.Skip(RunPreviewSet.MaxSkaters).Take(RunPreviewSet.MaxSkaters).ToList(),
        };
        MenuAttachment attachment;
        RunPreviewSet? sent;
        try
        {
            attachment = MenuAttachment.RunPreview(1, set);
            if (!attachment.TryGetRunPreview(out sent) || sent == null)
            {
                ModLog.Warn("Run start: carrier self-test: the run preview attachment didn't read back.");
                return;
            }
        }
        catch (ArgumentException ex)
        {
            ModLog.Warn($"Run start: carrier self-test: the run preview attachment can't be made ({ModLog.SafeErrorText(ex)}).");
            return;
        }

        var builder = new SkaterModels.Builder();
        int equal = 0;
        try
        {
            foreach (RunStartSkater skater in sent.SuperStars.Concat(sent.RandomSkaters))
            {
                ForwardData? built = builder.Build(skater, out string problem);
                if (built == null)
                {
                    failures.Add($"{skater}: {problem}");
                }
                else
                {
                    equal++;
                }
            }
        }
        finally
        {
            // Only checked, never shown or kept: the skaters and their talents and abilities made here go.
            SkaterModels.Destroy(builder.Made, "the self-test's skaters");
        }

        string size = string.Create(CultureInfo.InvariantCulture, $"the attachment with them is {attachment.BodyLength / 1024.0:0.0} KB, {attachment.DeflatedLength / 1024.0:0.0} KB deflated");
        if (failures.Count == 0)
        {
            ModLog.Info($"Run start: carrier self-test: {equal} skater(s) ({fromTeam} of the run's team, {forwards.Count - fromTeam} free agent template(s)) read, sent through " +
                $"the run preview attachment, built with the game's deserializer and read back with its serializer: all fields equal ({size}).");
            return;
        }

        ModLog.Warn($"Run start: carrier self-test: {equal} of {forwards.Count} skater(s) rebuilt with all fields equal; {failures.Count} not ({size}): " +
            string.Join("; ", failures.Take(5).Select(f => SafeText.Clean(f, 200))) + (failures.Count > 5 ? "; ..." : string.Empty) + ".");
    }

    /// <summary>Starts loading the preview file into a scratch run, as a friend's game would load it (never into the game's own state).</summary>
    private static void BeginReload(CampaignState state, byte[] preview)
    {
        RunDataRepositoryV2? game = state._runDataRepository?.TryCast<RunDataRepository>()?._runDataRepositoryV2;
        RunStatsStateRepository? gameStats = state._runStatsStateRepository?.TryCast<RunStatsStateRepository>();
        RunSquadScriptableObject? squad = state.GetCurrentSquad();
        if (game == null || gameStats == null || squad == null)
        {
            Finish("the game's run storage or squad isn't the kind this version knows");
            return;
        }

        // A fresh starting team, as the game's own load makes one (no pickups are triggered).
        TeamData team = state.InstantiateStartingTeam(squad);
        RunDataRepositoryV2 loader = RunPreview.Repository(game, RunPreview.PreviewPath);
        var stats = new RunStatsStateRepository { _fileName = RunPreview.PreviewPath, _es3SettingsLoader = gameStats._es3SettingsLoader };
        _check = new Check(preview, game, gameStats, loader.LoadRunData(team).GetAwaiter(), stats.LoadRunStatsState().GetAwaiter(), Stopwatch.GetTimestamp());
        Update();
    }

    /// <summary>The scratch run written again and compared with the preview, value by value.</summary>
    private static void Compare(Check check, RunData? loaded, RunStatsState? stats)
    {
        if (loaded == null || loaded.isUnableToLoad || stats == null)
        {
            Finish("the game's loader made no run of the preview file");
            return;
        }

        RunDataRepositoryV2 writer = RunPreview.Repository(check.Game, RunPreview.CheckPath);
        var statsWriter = new RunStatsStateRepository { _fileName = RunPreview.CheckPath, _es3SettingsLoader = check.GameStats._es3SettingsLoader };
        CampaignFiles.DeleteWithCopies(RunPreview.CheckPath);
        UniTask<bool>.Awaiter saving = writer.SaveRunData(loaded).GetAwaiter();
        statsWriter.SaveRunStatsState(stats);
        if (!saving.IsCompleted || !saving.GetResult() || !File.Exists(RunPreview.CheckPath))
        {
            Finish("the scratch run couldn't be written again");
            return;
        }

        byte[] again = CampaignFiles.ReadShared(RunPreview.CheckPath);
        double seconds = Seconds(check.Since);
        if (!RunFileDiff.TryCompare(check.Preview, again, LoggedDifferences, LogNames.TextShape, out List<string> differences, out int total, out int values))
        {
            Finish("one of the two files isn't JSON");
            return;
        }

        int forwards = loaded.teamData?.GetForwards(false)?.Count ?? 0;
        if (total == 0)
        {
            ModLog.Info(string.Create(CultureInfo.InvariantCulture, $"Run start: carrier self-test: the run preview loaded the way a friend's game loads it (in {seconds:0.00} s, a team of {forwards} skaters, ") +
                $"into a scratch run) and written again is the same run: all {values} values equal.");
        }
        else
        {
            ModLog.Warn($"Run start: carrier self-test: the run preview loaded the way a friend's game loads it and written again differs in {total} of {values} value(s): " +
                string.Join("; ", differences) + (total > differences.Count ? "; ..." : string.Empty) + ".");
        }

        Finish(string.Empty);
    }

    private static void Finish(string problem)
    {
        _check = null;
        if (problem.Length > 0)
        {
            ModLog.Warn($"Run start: carrier self-test: {problem}.");
        }

        RunPreview.CleanUp("the self-test ended");
        ModLog.Info("Run start: carrier self-test done (nothing was saved; the preview files are deleted).");
    }

    // By its asset id, never its names: a custom skater's are typed by its player.
    private static string Name(ForwardData forward) => ObjectEntryLogText.Skater(forward.id);

    private static double Seconds(long since) => (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;

    /// <summary>A running self-test's scratch load.</summary>
    private sealed record Check(byte[] Preview, RunDataRepositoryV2 Game, RunStatsStateRepository GameStats, UniTask<RunData>.Awaiter Run,
        UniTask<RunStatsState>.Awaiter Stats, long Since);
}
#endif
