#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppState.RunHistory;
using Il2CppTape2Tape.Customization;
using T2TOnline.Core.Campaign;
using T2TOnline.Core.Stream;
using T2TOnline.Plugin.Diagnostics;
using UnityEngine;

namespace T2TOnline.Plugin.Campaign.Progress;

/// <summary>
/// A host's custom team logo in a friend's run history (v1; D55 as amended). On the couch the logo
/// exists on that PC; on a friend's it's in the game's logo list only while the session lasts (<see cref="Match.CustomContent"/>),
/// and the history page's only lookup (<c>RunHistoryDeserializer.ResolveLogo</c>: the squad's own logo, else
/// <c>ILogoRepository.GetLogo</c>, the default logo on a miss) would show the default after it. So:
/// <list type="bullet">
/// <item>Each logo of the host's that arrives in a session is noted (<see cref="Offer"/>; the newest 8, 16 MB).</item>
/// <item>When the shared run's history entry is saved (<see cref="ForEntry"/>) and its team wears one of them, its PNG goes
/// into <c>%LOCALAPPDATA%\T2T Online\history-logos\&lt;key&gt;.png</c> (checked, written whole; the newest 32 and 48 MB
/// kept, <see cref="HistoryLogoNames"/>) and the entry names <c>t2t_hostlogo_&lt;key&gt;.png</c>.</item>
/// <item>A prefix on <c>ResolveLogo</c> answers those names with the stored logo, loaded once a game with the game's own
/// loader (<c>CustomLogoLoader.LoadSprites</c>) after the header and a decode budget of its own are
/// checked, and kept from Unity's unused-asset sweeps; any other name runs the game's own lookup. A file that's gone
/// leaves the game's default logo, as without the mod.</item>
/// </list>
/// Nothing goes into the game's folders, logo list or editors. Nothing here throws into the game.
/// </summary>
internal static class HistoryLogos
{
    private const int MaxArrived = 8;
    private const long MaxArrivedBytes = 16L * 1024 * 1024;
    private const int MaxLoggedFailures = 5;

    private static readonly List<CustomItem> Arrived = new();
    private static readonly Dictionary<string, Sprite> Loaded = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);
    private static readonly CustomLogoBudget Budget = new();
    private static string _host = string.Empty;
    private static int _failures;

    /// <summary>Where the logos are kept.</summary>
    public static string Folder => Path.Combine(CampaignFiles.DataFolder, HistoryLogoNames.FolderName);

    /// <summary>A friend session with <paramref name="host"/> starts: another host's logos noted before are let go.</summary>
    public static void StartFriend(string host)
    {
        if (!string.Equals(host, _host, StringComparison.Ordinal))
        {
            Arrived.Clear();
            _host = host ?? string.Empty;
        }
    }

    /// <summary>A logo of the host's arrived (<see cref="CustomContentKind.Logo"/> or <see cref="CustomContentKind.RunLogo"/>): kept in memory for the history entry.</summary>
    public static void Offer(CustomItem item)
    {
        if (item == null || !item.IsLogo)
        {
            return;
        }

        Arrived.RemoveAll(i => i.Kind == item.Kind && i.Key == item.Key);
        Arrived.Add(item);
        while (Arrived.Count > MaxArrived || Arrived.Sum(i => (long)i.Bytes.Length) > MaxArrivedBytes)
        {
            Arrived.RemoveAt(0);
        }
    }

    /// <summary>
    /// The shared run's history entry, about to be saved on this PC: when its team wears a logo of the host's that arrived
    /// here, the PNG is kept in the mod's data and the entry names it by its content name. Returns the entry. Never throws:
    /// on any problem the entry keeps the logo name it had (the game's default logo shows), and the log says why.
    /// </summary>
    public static RunHistoryEntryModel ForEntry(RunHistoryEntryModel entry)
    {
        try
        {
            RunHistoryTeamModel? team = entry?.teamSnapshot;
            string? name = team?.logoName;
            if (team == null || name == null || !name.StartsWith(CustomItem.LogoPrefix, StringComparison.Ordinal))
            {
                return entry!;
            }

            CustomItem? item = Arrived.LastOrDefault(i => i.Kind == CustomContentKind.RunLogo && i.Key == name) ?? Arrived.LastOrDefault(i => i.Key == name);
            if (item == null)
            {
                ModLog.Info($"Progress: the shared run's history entry names {LogNames.HostTag()}'s team logo ({LogNames.TextShape(name)}), which didn't reach this PC this session; its run history page shows the game's default logo.");
                return entry!;
            }

            if (PngHeader.CheckLogo(item.Bytes) is string bad)
            {
                ModLog.Warn($"Progress: {LogNames.HostTag()}'s team logo ({LogNames.TextShape(name)}) isn't kept for the run history ({bad}); its run history page shows the game's default logo.");
                return entry!;
            }

            if (NativeLobby.TourHolds.HoldsFile("Progress", "the run history's team logo"))
            {
                return entry!; // a check tour's run end: its history entry isn't saved either
            }

            // Written whole each time (a run's end, at most a few MB): a copy kept before becomes the newest again.
            string key = HistoryLogoNames.Key(item.Bytes);
            CampaignFiles.WriteAtomically(Path.Combine(Folder, HistoryLogoNames.FileName(key)), item.Bytes);
            Prune(key);
            string content = HistoryLogoNames.ContentName(key);
            team.logoName = content;
            ModLog.Info($"Progress: the shared run's history entry keeps {LogNames.HostTag()}'s team logo ({LogNames.TextShape(name)}, {item.Bytes.Length / 1024} KB) in T2T Online's data as {content}; " +
                "only its run history page reads it.");
            return entry!;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Progress: {LogNames.HostTag()}'s team logo couldn't be kept for the run history ({ex.GetType().Name}: {CampaignFiles.ErrorText(ex)}); its run history page shows the game's default logo.");
            return entry!;
        }
    }

    /// <summary>Patches the history page's logo lookup (at start-up, D38). Adds a line to <paramref name="failed"/> when it can't.</summary>
    public static void Install(HarmonyLib.Harmony harmony, List<string> failed)
    {
        try
        {
            harmony.Patch(AccessTools.Method(typeof(RunHistoryDeserializer), nameof(RunHistoryDeserializer.ResolveLogo)),
                prefix: new HarmonyMethod(typeof(HistoryLogos), nameof(ResolveLogoPrefix)));
        }
        catch (Exception ex)
        {
            failed.Add($"progress: RunHistoryDeserializer.ResolveLogo ({ModLog.SafeErrorText(ex)})");
        }
    }

    /// <summary>The history page looks a logo up: a stored host logo's content name is answered here; anything else by the game.</summary>
    private static bool ResolveLogoPrefix(string logoName, ref Sprite __result)
    {
        try
        {
            if (!HistoryLogoNames.TryParseContentName(logoName, out string key))
            {
                return true;
            }

            Sprite? sprite = Load(logoName, key);
            if (sprite == null)
            {
                return true; // the game's own lookup: its default logo
            }

            __result = sprite;
            return false;
        }
        catch (Exception ex)
        {
            if (_failures++ < MaxLoggedFailures)
            {
                ModLog.Error("Run history: showing a host's team logo failed (the game shows its default logo)", ex);
            }

            return true;
        }
    }

    /// <summary>The stored logo <paramref name="key"/> as a sprite, loaded once a game; null (logged once) when it can't be.</summary>
    private static Sprite? Load(string name, string key)
    {
        if (Loaded.TryGetValue(name, out Sprite? known) && known != null)
        {
            return known;
        }

        if (Missing.Contains(name))
        {
            return null;
        }

        string path = Path.Combine(Folder, HistoryLogoNames.FileName(key));
        string? problem = null;
        byte[] bytes = Array.Empty<byte>();
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            problem = "it isn't kept any more (only the newest 32 are)";
        }
        else if (file.Length > CustomItem.MaxLogoBytes)
        {
            problem = $"its file is {file.Length / 1024} KB";
        }
        else
        {
            try
            {
                bytes = CampaignFiles.ReadShared(path);
                problem = PngHeader.CheckLogo(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"its file can't be read ({CampaignFiles.ErrorText(ex)})";
            }

            if (problem == null && PngHeader.TryRead(bytes, out int width, out int height) && !Budget.TryTake(width, height, out string overLimit))
            {
                problem = overLimit.Replace("this session", "this game", StringComparison.Ordinal);
            }
        }

        Sprite? sprite = null;
        if (problem == null)
        {
            // The path's string is fresh and held only by this fresh array until the loader reads it, after an allocation of
            // its own: stored through the write barrier (GameArrays), so the incremental collector can't miss it meanwhile.
            Il2CppStringArray paths = Menus.GameArrays.Strings(new[] { path });
            Il2CppSystem.Collections.Generic.List<Sprite>? sprites = CustomLogoLoader.LoadSprites(paths);
            GC.KeepAlive(paths);
            sprite = sprites != null && sprites.Count > 0 ? sprites[0] : null;
            problem = sprite == null ? "the game's loader couldn't load it" : null;
        }

        if (sprite == null)
        {
            Missing.Add(name);
            ModLog.Warn($"Run history: the host's team logo ({LogNames.TextShape(name)}) can't be shown ({problem}); the game shows its default logo.");
            return null;
        }

        // Kept from the unused-asset sweep at scene loads: the history page is the only thing holding it.
        Texture2D? texture = sprite.texture;
        sprite.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        if (texture != null)
        {
            texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }

        Loaded[name] = sprite;
        ModLog.Info($"Run history: showed a host's team logo from T2T Online's data ({LogNames.TextShape(name)}, {(texture != null ? $"{texture.width}x{texture.height}" : "no texture")}) with the game's own loader.");
        return sprite;
    }

    /// <summary>Keeps the store within its caps (never the logo just stored), each removal logged.</summary>
    private static void Prune(string keep)
    {
        var files = new List<HistoryLogoFile>();
        foreach (string path in Directory.GetFiles(Folder, "*.png", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            files.Add(new HistoryLogoFile(info.Name, info.Length, info.LastWriteTimeUtc));
        }

        foreach (string old in HistoryLogoNames.Expired(files, keep))
        {
            try
            {
                File.Delete(Path.Combine(Folder, old));
                ModLog.Info($"Progress: removed the oldest kept host logo ({old}) from T2T Online's data (the newest {HistoryLogoNames.MaxFiles}, {HistoryLogoNames.MaxTotalBytes / (1024 * 1024)} MB, are kept); " +
                    "a run history entry that names it shows the game's default logo.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ModLog.Warn($"Progress: couldn't remove the old host logo {old} ({CampaignFiles.ErrorText(ex)}).");
            }
        }
    }
}
#endif
