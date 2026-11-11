#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppBishopGames.UI;
using Il2CppInterop.Runtime;
using T2TOnline.Core.Lobby;
using T2TOnline.Plugin.Campaign.Menus;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Streaming;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// The prefabs the native lobby clones from, found through the game's own menu catalogue
/// (<c>UIModule.GetMenuDefinitionFromUID(name).PrefabRef</c>) and held by a private Addressables load for as long as the title is up:
/// Addressables unloads a bundle's assets with its last reference (<c>ReleaseUnusedMenuPrefabs</c>, <c>ReleaseTitleMenus</c>), and a live clone
/// would lose its sprites. Loads are polled, never waited on, with a timeout. When a source loads, every part <c>ui_parts.txt</c> lists for it is
/// looked for: a part that's gone turns off only the native lobby, and the log names it.
/// </summary>
/// <remarks>
/// <c>AssetReference.LoadAssetAsync&lt;GameObject&gt;</c> is a generic virtual call, as <c>UIModule.ShowMenuAsync&lt;T&gt;</c> is, which the mod has
/// made for 20+ types; the game makes this same instance in <c>LoadMenuPrefab</c>. Its by-value handle comes back boxed and is ignored: done and
/// result are read from the reference itself (<c>IsDone</c>, <c>Asset</c>), and one <c>ReleaseAsset</c> gives it back.
/// </remarks>
internal static class UiAssets
{
    /// <summary>
    /// How long a load may take before its source counts as failed, which turns the native lobby off for this game: long, as what's waiting (a screen) waits anyway. These take about 0.2 s
    /// on a fast PC, and one asked for at a title load competes with the title's warm-ups on a slow disk.
    /// </summary>
    public const double TimeoutSeconds = 30;

    private static readonly Dictionary<string, Source> Sources = new(StringComparer.Ordinal);
    private static readonly List<AssetReferenceGameObject> LateReleases = new();
    private static readonly HashSet<string> Optional = new(StringComparer.Ordinal);
    private static UiPartsList? _parts;
    private static string? _partsProblem;
    private static bool _anyLoading;

    /// <summary>Why a source or a part is missing this game (null while nothing is): the native lobby is off then.</summary>
    public static string? Problem { get; private set; }

    /// <summary>A loaded source's definition, for the shell's own: its prefab's address.</summary>
    public static string? Guid(string name) => Sources.TryGetValue(name, out Source? source) ? source.Guid : null;

    /// <summary>Starts a private load of each source not asked for yet. Never throws; never waits.</summary>
    public static void Request(params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                RequestOne(name);
            }
            catch (Exception ex)
            {
                Fail(name, $"its load couldn't start ({ModLog.SafeErrorText(ex)})");
            }
        }
    }

    /// <summary>Starts a private load of each source only an icon needs: its failure turns nothing off.</summary>
    public static void RequestOptional(params string[] names)
    {
        foreach (string name in names)
        {
            Optional.Add(name);
        }

        Request(names);
    }

    /// <summary>True when the source has loaded, and its parts are all there.</summary>
    public static bool Ready(string name) => Prefab(name) != null;

    /// <summary>The source's prefab, or null: not loaded (yet), failed, or unloaded under us.</summary>
    public static GameObject? Prefab(string name)
    {
        if (!Sources.TryGetValue(name, out Source? source) || source.Prefab == null || source.Prefab.WasCollected)
        {
            return null;
        }

        return source.Prefab;
    }

    /// <summary>A part of a loaded source (a child of its prefab), or null.</summary>
    public static GameObject? Part(string name, string path)
    {
        GameObject? prefab = Prefab(name);
        Transform? found = prefab == null ? null : UiClone.Find(prefab.transform, path);
        return found == null ? null : found.gameObject;
    }

    /// <summary>Every frame: finished loads are read (checked against <c>ui_parts.txt</c>), slow ones given up on. Cheap while nothing loads.</summary>
    public static void Tick()
    {
        if (!_anyLoading && LateReleases.Count == 0)
        {
            return;
        }

        double now = FrameClock.Seconds;
        _anyLoading = false;
        foreach (Source source in Sources.Values)
        {
            if (source.State != SourceState.Loading)
            {
                continue;
            }

            try
            {
                if (source.Reference != null && source.Reference.IsDone)
                {
                    Finish(source, now);
                }
                else if (now - source.Since > TimeoutSeconds)
                {
                    // Not released now: its handle comes back once it's done, so the bundle isn't left held for good.
                    if (source.Reference != null)
                    {
                        LateReleases.Add(source.Reference);
                        source.Reference = null;
                    }

                    Fail(source.Name, $"its prefab didn't load within {TimeoutSeconds:0} s");
                }
                else
                {
                    _anyLoading = true;
                }
            }
            catch (Exception ex)
            {
                Fail(source.Name, $"reading its load failed ({ModLog.SafeErrorText(ex)})");
            }
        }

        for (int i = LateReleases.Count - 1; i >= 0; i--)
        {
            AssetReferenceGameObject late = LateReleases[i];
            if (late.WasCollected || !late.IsValid())
            {
                LateReleases.RemoveAt(i);
            }
            else if (late.IsDone)
            {
                late.ReleaseAsset();
                LateReleases.RemoveAt(i);
            }
        }
    }

    /// <summary>Gives every held source back (the title has gone, so no clone lives). A later request loads them again.</summary>
    public static void ReleaseAll(string why)
    {
        int released = 0;
        foreach (Source source in Sources.Values)
        {
            try
            {
                if (source.Reference != null && !source.Reference.WasCollected)
                {
                    if (source.State == SourceState.Loading)
                    {
                        LateReleases.Add(source.Reference);
                    }
                    else if (source.Reference.IsValid())
                    {
                        source.Reference.ReleaseAsset();
                        released++;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Lobby: giving back the {source.Name} prefab failed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        Sources.Clear();
        _anyLoading = false;
        if (released > 0)
        {
            ModLog.Info($"Lobby: gave back {released} held prefab(s) ({why}).");
        }
    }

    private static void RequestOne(string name)
    {
        if (Sources.TryGetValue(name, out Source? known) && known.State != SourceState.Failed)
        {
            return;
        }

        UIModule? ui = MenuGame.Module();
        if (ui == null)
        {
            return; // the game isn't up yet: asked again later
        }

        UiPartsList? parts = PartsList();
        if (parts == null)
        {
            Fail(name, $"ui_parts.txt can't be read ({_partsProblem})");
            return;
        }

        MenuDefinition? definition = ui.GetMenuDefinitionFromUID(name);
        string? guid = definition?.PrefabRef?.AssetGUID;
        if (string.IsNullOrEmpty(guid))
        {
            Fail(name, "the game's catalogue has no such menu");
            return;
        }

        var reference = new AssetReferenceGameObject(guid);
        _ = reference.LoadAssetAsync<GameObject>();
        Sources[name] = new Source(name, guid) { Reference = reference, Since = FrameClock.Seconds, State = SourceState.Loading };
        _anyLoading = true;
    }

    private static void Finish(Source source, double now)
    {
        GameObject? prefab = source.Reference?.Asset?.TryCast<GameObject>();
        if (prefab == null)
        {
            Fail(source.Name, "its load finished with no prefab");
            return;
        }

        string? missing = MissingPart(source.Name, prefab);
        if (missing != null)
        {
            Fail(source.Name, missing);
            return;
        }

        source.Prefab = prefab;
        source.State = SourceState.Ready;
        ModLog.Info($"Lobby: the {source.Name} prefab is held ({(now - source.Since) * 1000:0} ms; {PartsList()?.PartsOf(source.Name).Count() ?? 0} part(s) checked).");
        if (Optional.Contains(source.Name))
        {
            NativeArt.SourceArrived();
        }
    }

    /// <summary>What's wrong with a live copy of a menu (<paramref name="root"/>, its prefab's root) against <c>ui_parts.txt</c>'s rows for <paramref name="name"/>, or null.</summary>
    public static string? CheckParts(string name, GameObject root) => MissingPart(name, root);

    /// <summary>What's wrong with a loaded prefab against <c>ui_parts.txt</c>, or null.</summary>
    private static string? MissingPart(string name, GameObject prefab)
    {
        UiPartsList? parts = PartsList();
        if (parts == null)
        {
            return $"ui_parts.txt can't be read ({_partsProblem})";
        }

        foreach (UiPart part in parts.PartsOf(name))
        {
            Transform? found = UiClone.Find(prefab.transform, part.Path);
            if (found == null)
            {
                return $"the part {name}/{part.Path} is gone";
            }

            if (part.Components.Count == 0)
            {
                continue;
            }

            var carried = new HashSet<string>(StringComparer.Ordinal);
            foreach (Component? component in found.gameObject.GetComponents(Il2CppType.Of<Component>()))
            {
                if (component != null)
                {
                    carried.Add(component.GetIl2CppType().Name);
                }
            }

            string? lacking = part.Components.FirstOrDefault(c => !carried.Contains(c));
            if (lacking != null)
            {
                return $"the part {name}/{part.Path} has no {lacking}";
            }
        }

        return null;
    }

    private static UiPartsList? PartsList()
    {
        if (_parts != null || _partsProblem != null)
        {
            return _parts;
        }

        try
        {
            using Stream stream = typeof(UiAssets).Assembly.GetManifestResourceStream("T2TOnline.ui_parts.txt")
                ?? throw new InvalidOperationException("the embedded resource is missing");
            using var reader = new StreamReader(stream);
            _parts = UiPartsList.Parse(reader.ReadToEnd(), out string problem);
            _partsProblem = _parts == null ? problem : null;
        }
        catch (Exception ex)
        {
            _partsProblem = ModLog.SafeErrorText(ex);
        }

        return _parts;
    }

    private static void Fail(string prefab, string why)
    {
        if (!Sources.TryGetValue(prefab, out Source? source))
        {
            Sources[prefab] = source = new Source(prefab, string.Empty);
        }

        source.State = SourceState.Failed;
        source.Prefab = null;
        if (source.Reference != null)
        {
            try
            {
                if (source.Reference.IsValid())
                {
                    source.Reference.ReleaseAsset();
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Lobby: giving back the {prefab} prefab failed ({ModLog.SafeErrorText(ex)}).");
            }

            source.Reference = null;
        }

        if (Optional.Contains(prefab))
        {
            ModLog.Warn($"Lobby: the {prefab} prefab can't be used: {why}. Only an icon goes with it.");
            return;
        }

        Problem ??= $"{prefab}: {why}";
        ModLog.Warn($"Lobby: the {prefab} prefab can't be used: {why}. The native lobby is off for this game; {Core.OnlineSettings.PlayersBuild.WithoutLobby(Startup.ModSettings.DevTools)}.");
    }

    private enum SourceState
    {
        Loading,
        Ready,
        Failed,
    }

    private sealed class Source
    {
        public Source(string name, string guid)
        {
            Name = name;
            Guid = guid;
        }

        public string Name { get; }

        public string Guid { get; }

        public AssetReferenceGameObject? Reference { get; set; }

        public GameObject? Prefab { get; set; }

        public double Since { get; set; }

        public SourceState State { get; set; }
    }
}
#endif
