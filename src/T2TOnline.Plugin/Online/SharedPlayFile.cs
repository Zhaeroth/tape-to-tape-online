#if HAS_INTEROP
using System;
using System.IO;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Session;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Campaign.Progress;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Online;

/// <summary>
/// A friend's record of the host's play it shared, per hosting session (<see cref="SharedPlayRecord"/>, D34, D135), in
/// <c>%LOCALAPPDATA%\T2T Online\progress\shared-play.txt</c>, and the witness the shared moments are judged by
/// (<see cref="SharedPlayGate"/>): the host's matches this PC played and whether it held the host's co-op run, live and,
/// after this PC's game restarts and rejoins the same hosting session, from the record. Read once at start-up (a file of
/// the old format, with host IDs, is written again at once without them), written each time a match or the run is first
/// seen in a hosting session. It names no host: each hosting session is kept under this PC's own key for it and its host
/// (<see cref="HostKeyFile.SessionKeyFor"/>). Never throws (a file that can't be read or written is logged once, and
/// the moments then count only what this game sees).
/// </summary>
internal sealed class SharedPlayFile
{
    /// <summary>The record's file name, in the progress folder.</summary>
    public const string FileName = "shared-play.txt";

    private readonly SharedPlayRecord _record;
    private readonly SharedPlayGate _gate;
    private bool _writeFailed;

    /// <summary>The record as the file holds it (empty when there's none, or it can't be read).</summary>
    public SharedPlayFile()
    {
        _record = Load();
        _gate = new SharedPlayGate(_record);
        if (_record.HadHostIds)
        {
            // Written by 0.10.0 or before, with each host's ID: written again now, without them.
            if (Save())
            {
                ModLog.Info($"Achievements: {FileName} held host IDs (the format before 0.11.0); written again without them.");
            }
        }
    }

    /// <summary>What this PC saw of the host's play in the hosting session it's in: what the shared moments are judged by.</summary>
    public IMomentWitness Witness => _gate;

    /// <summary>The hosting session this PC was welcomed into (0: none).</summary>
    public ulong Session => _gate.Session;

    private static string FilePath => Path.Combine(CampaignFiles.DataFolder, "progress", FileName);

    /// <summary>
    /// Welcomed by host <paramref name="host"/> into hosting session <paramref name="session"/> (a new F7 session or a
    /// rejoin). The record knows the pair by this PC's own key for it (<see cref="HostKeys.SessionTag"/>), so it names no host
    /// and another host naming the same session inherits nothing. When this PC shared that session's play in an earlier F7
    /// session (or before its game restarted), what it shared is said.
    /// </summary>
    public void Welcomed(ulong host, ulong session)
    {
        try
        {
            ulong key = HostKeyFile.SessionKeyFor(host, session);
            ulong before = _gate.Session;
            if (_gate.Welcomed(key) && key != before)
            {
                ModLog.Info($"Achievements: this PC shared the host's play in this hosting session earlier (before a rejoin, or before its game restarted: {_record.MatchCount(key)} of " +
                    $"their matches played here{(_record.HeldRun(key) ? ", their co-op run held" : string.Empty)}); moments owed for that play unlock here.");
            }
        }
        catch (Exception ex)
        {
            // Nothing of an earlier hosting session's play may count for this one: what's seen from now on only.
            _gate.Welcomed(0);
            ModLog.Warn($"Achievements: couldn't look up this hosting session in this PC's shared play ({ex.GetType().Name}); only the play seen from now on counts.");
        }
    }

    /// <summary>The F7 session ended or a new one starts.</summary>
    public void Clear() => _gate.Clear();

    /// <summary>
    /// This PC played the host's match <paramref name="match"/> (its number on the host), of the kind <paramref name="view"/>
    /// says (the setup it loaded): said once, and kept.
    /// </summary>
    public void NoteMatch(int match, MatchView view)
    {
        if (_gate.NoteMatch(match, view, DateTime.UtcNow, out bool noted))
        {
            ModLog.Info($"Achievements: the host's match {match} played here ({view.KindText()}); its shared moments count here as this PC sees it.");
            if (noted)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// What this PC's own stream showed of the host's match <paramref name="match"/> since (its score, a sign, its end): kept,
    /// and its end said once with what was seen (again when a sign raised with the end shows here later still).
    /// </summary>
    public void NoteView(int match, MatchView view)
    {
        MatchView before = _gate.ViewOf(match);
        if (!_gate.NoteView(match, view, DateTime.UtcNow, out bool noted))
        {
            return;
        }

        if (view.Ended)
        {
            MatchView now = _gate.ViewOf(match);
            if (!before.Ended)
            {
                ModLog.Info($"Achievements: the host's match {match} ended here: {now}.");
            }
            else if (now.Signs != before.Signs)
            {
                ModLog.Info($"Achievements: the host's match {match} ended here: {now} (with what its stream showed right after its end).");
            }
        }

        if (noted)
        {
            Save();
        }
    }

    /// <summary>This PC holds the host's co-op run: said once per hosting session, and kept. Cheap after that (every frame).</summary>
    public void NoteRun()
    {
        if (_gate.HeldRunLive)
        {
            return;
        }

        if (_gate.NoteRun(DateTime.UtcNow, out bool noted))
        {
            ModLog.Info("Achievements: the host's co-op run is held here; its run moments count here.");
            if (noted)
            {
                Save();
            }
        }
    }

    private static SharedPlayRecord Load()
    {
        try
        {
            return File.Exists(FilePath) ? SharedPlayRecord.Parse(File.ReadAllText(FilePath)) : new SharedPlayRecord();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Achievements: this PC's record of shared play couldn't be read ({CampaignFiles.ErrorText(ex)}); moments owed after a restart count only the play this game sees again.");
            return new SharedPlayRecord();
        }
    }

    private bool Save()
    {
        if (NativeLobby.TourHolds.HoldsFile("Achievements", "the record of shared play"))
        {
            return true; // a check tour's play stays in memory: the player's record says nothing of it
        }

        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + CampaignFiles.PartSuffix;
            File.WriteAllText(temporary, _record.Format());
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            if (!_writeFailed)
            {
                _writeFailed = true;
                ModLog.Warn($"Achievements: this PC's record of shared play couldn't be saved ({CampaignFiles.ErrorText(ex)}); after a restart, moments owed count only the play this game sees again.");
            }

            return false;
        }
    }
}
#endif
