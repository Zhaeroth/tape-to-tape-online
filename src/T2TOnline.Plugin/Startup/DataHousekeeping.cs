#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Diagnostics;
using T2TOnline.Plugin.Campaign;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// The mod's own folders, tidied once at start-up, before anything of the mod writes to them: the developer
/// output's move and prune (<see cref="DevOutput"/>), the temporaries a stopped write left (<see cref="ModTemporaries"/>),
/// and a <c>README.txt</c> in each data folder (<see cref="DataFolderReadme"/>). Each step on its own; none throws, and none
/// creates a folder that isn't there (the README of <c>%LOCALAPPDATA%\T2T Online</c> waits until the mod has made it).
/// </summary>
internal static class DataHousekeeping
{
    /// <summary>Runs every step. Call once, in <c>OnInitializeMelon</c>, after the settings and before the runtime starts.</summary>
    public static void Run()
    {
        Step("developer output", DevOutput.StartUp);
        Step("temporary files", SweepTemporaries);
        Step("README files", WriteReadmes);
    }

    private static void Step(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Start-up tidying: {what} failed ({ex.GetType().Name}); the game and the mod carry on.");
        }
    }

    /// <summary>
    /// Removes the mod's temporaries (<c>*.t2t-part</c>, the 0.10.x ledger's <c>.tmp</c>) from the mod's folders, and backup
    /// folders that were never finished (<c>backups\*.partial</c>). Nothing is being written yet, so none is in use by the mod.
    /// A temporary whose finished file is missing stays (<see cref="ModTemporaries.IsStale"/>): it may be the only copy, which
    /// its reader recovers (the shared-progress ledger).
    /// Never the game's save folder (its leftovers are the backups' own start-up step) or the game's own <c>.tmp</c>/<c>.bac</c>.
    /// </summary>
    private static void SweepTemporaries()
    {
        string mod = CampaignFiles.ModFolder;
        string data = CampaignFiles.DataFolder;
        var folders = new List<(string Path, string Label)>
        {
            (mod, "UserData/T2TOnline"),
            (Path.Combine(mod, "campaign"), "campaign/"),
            (Path.Combine(mod, "custom"), "custom/"),
            (Path.Combine(mod, "reports"), "reports/"),
            (DevOutput.Root, "dev/"),
            (data, CampaignFiles.DataFolderLabel),
            (Path.Combine(data, "coop"), "coop/"),
            (Path.Combine(data, "backups"), "backups/"),
            (Path.Combine(data, "progress"), "progress/"),
            (Path.Combine(data, "history-logos"), "history-logos/"),
        };

        int removed = 0;
        int left = 0;
        string? failure = null;
        var where = new List<string>();
        foreach ((string folder, string label) in folders.DistinctBy(f => Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            int before = removed;
            foreach (string file in Directory.GetFiles(folder).Where(f => ModTemporaries.IsStale(Path.GetFileName(f), name => File.Exists(Path.Combine(folder, name)))))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left++;
                    failure ??= CampaignFiles.ErrorText(ex);
                }
            }

            if (label == "backups/")
            {
                foreach (string partial in Directory.GetDirectories(folder, "*" + ModTemporaries.PartialFolderSuffix))
                {
                    string name = Path.GetFileName(partial);
                    if (!SaveBackupNames.TryParse(name[..^ModTemporaries.PartialFolderSuffix.Length], out _, out _))
                    {
                        continue; // only an unfinished backup of the mod's own goes
                    }

                    try
                    {
                        Directory.Delete(partial, recursive: true);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        left++;
                        failure ??= CampaignFiles.ErrorText(ex);
                    }
                }
            }

            if (removed > before)
            {
                where.Add(label);
            }
        }

        if (removed > 0)
        {
            ModLog.Info($"Temporary files: removed {removed} left by a game that stopped while writing ({string.Join(", ", where)}).");
        }

        if (left > 0)
        {
            ModLog.Warn($"Temporary files: {left} couldn't be removed ({failure}); tried again at the next start.");
        }
    }

    /// <summary>
    /// <c>README.txt</c> in <c>UserData\T2TOnline</c> (always there: the session log is in it) and in
    /// <c>%LOCALAPPDATA%\T2T Online</c> when it exists; one text when both are the same folder (a PC without a local
    /// application data folder). Written only when missing or different.
    /// </summary>
    private static void WriteReadmes()
    {
        string mod = CampaignFiles.ModFolder;
        string data = CampaignFiles.DataFolder;
        bool shared = string.Equals(Path.GetFullPath(mod).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        var written = new List<string>();
        if (Write(mod, shared ? DataFolderReadme.SharedFolderText : DataFolderReadme.GameFolderText))
        {
            written.Add("UserData/T2TOnline");
        }

        if (!shared && Write(data, DataFolderReadme.DataFolderText))
        {
            written.Add(CampaignFiles.DataFolderLabel);
        }

        if (written.Count > 0)
        {
            ModLog.Info($"Data folders: wrote {DataFolderReadme.FileName} in {string.Join(" and ", written)}.");
        }
    }

    /// <summary>True when the README in <paramref name="folder"/> was written now; false when it was current, the folder doesn't exist, or it failed (said once).</summary>
    private static bool Write(string folder, string text)
    {
        string path = Path.Combine(folder, DataFolderReadme.FileName);
        try
        {
            if (!Directory.Exists(folder) || (File.Exists(path) && !DataFolderReadme.NeedsWrite(File.ReadAllText(path), text)))
            {
                return false;
            }

            CampaignFiles.WriteAtomically(path, Encoding.ASCII.GetBytes(text));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ModLog.Warn($"Data folders: {DataFolderReadme.FileName} couldn't be written ({CampaignFiles.ErrorText(ex)}); the mod works without it.");
            return false;
        }
    }
}
#endif
