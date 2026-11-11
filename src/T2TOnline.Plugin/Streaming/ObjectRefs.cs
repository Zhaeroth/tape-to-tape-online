#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppUltimateReplay;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using UnityEngine;
using UnityEngine.SceneManagement;
using Player = Il2CppTape2Tape.Player;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// References to game objects and assets in event arguments, sent so the other PC can find its own copy:
/// <list type="bullet">
/// <item>a component on a replay object's own GameObject (skaters, goalies, the puck): the replay object's network id;</item>
/// <item>a player (<c>Tape2Tape.Player</c>): <c>player:</c> and its id; a friend's game has no players of its own, so it
/// resolves to the stand-in for that host player (<see cref="PlayerProxies"/>), or to nothing before the stream has
/// listed the players;</item>
/// <item>any other scene object (teams, arena props): <c>go:</c> scene name and hierarchy path, each step
/// <c>name#n</c> for the n-th sibling of that name;</item>
/// <item>an asset (team data, move configs, shop items): <c>asset:</c> and its name.</item>
/// </list>
/// The receiving side looks the object up and returns a component of the type the event declares.
/// </summary>
internal static class ObjectRefs
{
    /// <summary>What every scene-path key and reference starts with.</summary>
    public const string ScenePrefix = "go:";
    private const string AssetPrefix = "asset:";
    private const string PlayerPrefix = "player:";
    private const double RootsCacheSeconds = 1;

    private static (string Scene, double At, List<GameObject> Roots)? _unlistedRoots;

    /// <summary>
    /// Host side: the reference for <paramref name="value"/>. <paramref name="netIdOf"/> gives a replay object's network id (0
    /// if none). <paramref name="paths"/> remembers scene paths (a recording's: see <see cref="PathMemo"/>).
    /// </summary>
    public static NetValue ToNet(UnityEngine.Object value, Func<ReplayObject, ushort> netIdOf, PathMemo? paths = null)
    {
        if (value == null)
        {
            return NetValue.Null; // destroyed on the native side
        }

        GameObject? gameObject = value.TryCast<Component>()?.gameObject ?? value.TryCast<GameObject>();
        if (gameObject == null)
        {
            return NetValue.FromString(AssetPrefix + value.name);
        }

        ReplayObject? replayObject = gameObject.GetComponent<ReplayObject>();
        if (replayObject != null)
        {
            ushort netId = netIdOf(replayObject);
            if (netId != 0)
            {
                return NetValue.FromRef(netId);
            }
        }

        if (value.TryCast<Player>() is Player player)
        {
            return NetValue.FromString(PlayerPrefix + player._PlayerId_k__BackingField.ToString(CultureInfo.InvariantCulture));
        }

        string path = paths != null ? paths.PathOf(gameObject) : PathOf(gameObject.transform);
        return path.Length <= NetValue.MaxStringBytes ? NetValue.FromString(path) : NetValue.Null;
    }

    /// <summary>
    /// The scene-and-path key of a transform. <paramref name="roots"/> answers root ordinals for a pass over many objects
    /// (one look at each scene's roots instead of one per object).
    /// </summary>
    public static string PathOf(Transform transform, RootOrdinals? roots = null)
    {
        var steps = new List<string>();
        for (Transform? current = transform; current != null; current = current.parent)
        {
            int ordinal = roots != null && current.parent == null ? roots.Of(current) : Ordinal(current);
            steps.Add($"{current.name}#{ordinal}");
        }

        steps.Reverse();
        return new StringBuilder(ScenePrefix).Append(transform.gameObject.scene.name).Append('/').AppendJoin('/', steps).ToString();
    }

    /// <summary>
    /// Friend side: finds the local object a reference names, as <paramref name="type"/> (a component type, GameObject, or
    /// asset type), or null.
    /// </summary>
    public static object? Resolve(NetValue value, Type type, IReadOnlyDictionary<ushort, ReplayObject> objects, RefCache cache)
    {
        GameObject? gameObject = null;
        if (value.Kind == NetValueKind.Reference)
        {
            // Unity's null: destroyed here since it was mapped (its object is pruned within a second).
            gameObject = objects.TryGetValue((ushort)value.WholeValue, out ReplayObject? replayObject) && replayObject != null ? replayObject.gameObject : null;
        }
        else if (value.Kind == NetValueKind.Text && value.Text is string playerRef && playerRef.StartsWith(PlayerPrefix, StringComparison.Ordinal))
        {
            gameObject = int.TryParse(playerRef.AsSpan(PlayerPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int id) &&
                PlayerProxies.ProxyOf(id) is Player proxy ? proxy.gameObject : null;
        }
        else if (value.Kind == NetValueKind.Text && value.Text is string text)
        {
            // A collision's colliders and bodies are looked up at once, outside the budget (CompoundValues.ResolvingCollision).
            bool urgent = CompoundValues.ResolvingCollision;
            UnityEngine.Object? found =
                text.StartsWith(AssetPrefix, StringComparison.Ordinal) ? cache.Find(text, type, urgent, () => FindAsset(text[AssetPrefix.Length..], type))
                : text.StartsWith(ScenePrefix, StringComparison.Ordinal) ? cache.Find(text, typeof(GameObject), urgent, () => FindByPath(text[ScenePrefix.Length..]))
                : null;
            if (found == null)
            {
                return null;
            }

            gameObject = found.TryCast<GameObject>();
            if (gameObject == null)
            {
                return EventValues.CastTo(found, type);
            }
        }

        if (gameObject == null)
        {
            return null;
        }

        if (type == typeof(GameObject))
        {
            return gameObject;
        }

        if (!typeof(Component).IsAssignableFrom(type))
        {
            return null;
        }

        Component? component = gameObject.GetComponent(Il2CppType.From(type));
        return component == null ? null : EventValues.CastTo(component, type);
    }

    /// <summary>True when the scene manager lists a loaded scene named <paramref name="sceneName"/> (a path there is a cheap lookup).</summary>
    internal static bool SceneListed(string sceneName)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneManager.GetSceneAt(i).name == sceneName)
            {
                return true;
            }
        }

        return false;
    }

    private static int Ordinal(Transform transform)
    {
        string name = transform.name;
        int ordinal = 0;
        Transform? parent = transform.parent;
        if (parent != null)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform sibling = parent.GetChild(i);
                if (sibling.Pointer == transform.Pointer)
                {
                    return ordinal;
                }

                ordinal += sibling.name == name ? 1 : 0;
            }

            return ordinal;
        }

        return RootOrdinal(transform, transform.gameObject.scene.GetRootGameObjects());
    }

    /// <summary>A root's ordinal among same-named roots in <paramref name="roots"/> (its scene's roots, in order).</summary>
    internal static int RootOrdinal(Transform root, IEnumerable<GameObject> roots)
    {
        string name = root.name;
        int ordinal = 0;
        foreach (GameObject other in roots)
        {
            if (other.transform.Pointer == root.Pointer)
            {
                return ordinal;
            }

            ordinal += other.name == name ? 1 : 0;
        }

        return ordinal;
    }

    private static GameObject? FindByPath(string path)
    {
        int slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return null;
        }

        string sceneName = path[..slash];
        string[] steps = path[(slash + 1)..].Split('/');
        foreach (GameObject root in RootsOf(sceneName))
        {
            if (root == null)
            {
                continue; // destroyed since the roots were listed
            }

            Transform? found = Walk(root.transform, steps);
            if (found != null)
            {
                return found.gameObject;
            }
        }

        return null;
    }

    /// <summary>
    /// The root objects of the named scene. "DontDestroyOnLoad" isn't listed by the scene manager, so it's found through
    /// its objects, a scan of every transform: kept for a second, so lookups of several references there share one.
    /// </summary>
    private static IEnumerable<GameObject> RootsOf(string sceneName)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.name == sceneName)
            {
                return scene.GetRootGameObjects();
            }
        }

        double now = FrameClock.Seconds;
        if (_unlistedRoots is { } cached && cached.Scene == sceneName && now - cached.At < RootsCacheSeconds)
        {
            return cached.Roots;
        }

        var roots = new List<GameObject>();
        foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Transform>()))
        {
            Transform? transform = found?.TryCast<Transform>();
            if (transform != null && transform.parent == null && transform.gameObject.scene.name == sceneName)
            {
                roots.Add(transform.gameObject);
            }
        }

        _unlistedRoots = (sceneName, now, roots);
        return roots;
    }

    /// <summary>Matches the steps from a root; the first step names the root itself. Ordinals are counted among same-named siblings.</summary>
    private static Transform? Walk(Transform root, string[] steps)
    {
        if (!Matches(root, steps[0], out _))
        {
            return null;
        }

        Transform current = root;
        for (int s = 1; s < steps.Length; s++)
        {
            if (!Split(steps[s], out string name, out int ordinal))
            {
                return null;
            }

            Transform? next = null;
            int seen = 0;
            for (int i = 0; i < current.childCount && next == null; i++)
            {
                Transform child = current.GetChild(i);
                if (child.name == name && seen++ == ordinal)
                {
                    next = child;
                }
            }

            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static bool Matches(Transform root, string step, out int ordinal) =>
        Split(step, out string name, out ordinal) && root.name == name && Ordinal(root) == ordinal;

    private static bool Split(string step, out string name, out int ordinal)
    {
        int hash = step.LastIndexOf('#');
        name = hash < 0 ? step : step[..hash];
        ordinal = 0;
        return hash >= 0 && int.TryParse(step.AsSpan(hash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ordinal);
    }

    private static UnityEngine.Object? FindAsset(string name, Type type)
    {
        if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
        {
            return null;
        }

        foreach (UnityEngine.Object asset in Resources.FindObjectsOfTypeAll(Il2CppType.From(type)))
        {
            if (asset != null && asset.name == name)
            {
                return asset;
            }
        }

        return null;
    }
}

/// <summary>
/// Friend side: references already looked up, by text and the type asked for. A found object is kept while it lives (a
/// destroyed one is looked up again). A miss is retried after a second, then after twice as long each time up to 30 s,
/// as the object may spawn later. The texts come from the network, so new lookups run within a budget by cost
/// (<see cref="LookupBudget"/>: scene paths, cheap, 64 a second and 16 a frame; scans of every loaded object, 8 a second;
/// an urgent one, a collision's colliders and bodies, at once up to 32 a frame), the rest wait as misses, and only so many
/// texts are remembered.
/// </summary>
internal sealed class RefCache
{
    private const double FirstRetrySeconds = 1.0;
    private const double LongestRetrySeconds = 30.0;
    private const int MaxRemembered = 4096;

    private readonly Dictionary<(string Text, Type Type), UnityEngine.Object> _found = new();
    private readonly Dictionary<(string Text, Type Type), (double RetryAt, double Wait)> _missed = new();

    /// <summary>The lookups run and held back, by kind.</summary>
    public LookupBudget Budget { get; } = new();

    /// <summary>Lookups held back by the budget.</summary>
    public long Deferred => Budget.HeldBack;

    /// <summary>
    /// The cached object for <paramref name="text"/> as <paramref name="type"/>, or what <paramref name="find"/> finds now
    /// (within the budget; <paramref name="urgent"/>: a listener needs it at once).
    /// </summary>
    public UnityEngine.Object? Find(string text, Type type, bool urgent, Func<UnityEngine.Object?> find)
    {
        var key = (text, type);
        if (_found.TryGetValue(key, out UnityEngine.Object? hit))
        {
            if (hit != null)
            {
                return hit;
            }

            // Unity's null: destroyed since.
            _found.Remove(key);
        }

        double now = FrameClock.Seconds;
        bool missedBefore = _missed.TryGetValue(key, out (double RetryAt, double Wait) miss);
        if (missedBefore && now < miss.RetryAt)
        {
            return null;
        }

        if (!Budget.TryTake(LookupBudget.KindOf(text, ObjectRefs.SceneListed), now, FrameClock.Frame, urgent))
        {
            return null;
        }

        UnityEngine.Object? found = find();
        if (found != null)
        {
            if (_found.Count < MaxRemembered)
            {
                _found[key] = found;
            }

            _missed.Remove(key);
        }
        else if (missedBefore || _missed.Count < MaxRemembered)
        {
            double wait = missedBefore ? Math.Min(miss.Wait * 2, LongestRetrySeconds) : FirstRetrySeconds;
            _missed[key] = (now + wait, wait);
        }

        return found;
    }

    /// <summary>Forgets everything (playback over).</summary>
    public void Clear()
    {
        _found.Clear();
        _missed.Clear();
        Budget.Clear();
    }
}

/// <summary>
/// Host side: scene paths already worked out in one recording, by object. A path walks every ancestor's siblings by name and
/// lists its scene's roots (<see cref="ObjectRefs.PathOf"/>), all interop calls and new strings: about a millisecond each,
/// inside the game's event raise (the host's "event" cost, p95 over a millisecond), and teams and rink colliders are named
/// again and again (a team in the HUD sample 20 times a second while its offside line glows, the boards at every puck
/// collision). What a friend looks up is the scene as it loaded, so the first path stays right for an object. Each object is
/// kept with its path, so its address can't be another object's while it's here; capped, and emptied with the recording.
/// </summary>
internal sealed class PathMemo
{
    private const int MaxRemembered = 1024;

    private readonly Dictionary<IntPtr, (GameObject Object, string Path)> _paths = new();

    /// <summary>The object's scene path (<see cref="ObjectRefs.PathOf"/>), worked out once.</summary>
    public string PathOf(GameObject gameObject)
    {
        if (_paths.TryGetValue(gameObject.Pointer, out (GameObject Object, string Path) known))
        {
            return known.Path;
        }

        string path = ObjectRefs.PathOf(gameObject.transform);
        if (_paths.Count < MaxRemembered)
        {
            _paths[gameObject.Pointer] = (gameObject, path);
        }

        return path;
    }

    /// <summary>Forgets everything (a recording starting or stopping).</summary>
    public void Clear() => _paths.Clear();
}

/// <summary>
/// Root objects' ordinals (the n-th root of that name in its scene) for one pass over many objects: each scene's roots
/// are listed once (<c>Scene.GetRootGameObjects</c> makes a new array every call) instead of once per object.
/// </summary>
internal sealed class RootOrdinals
{
    private readonly Dictionary<int, Dictionary<IntPtr, int>> _byScene = new();

    /// <summary>The ordinal of <paramref name="root"/>, a transform without a parent.</summary>
    public int Of(Transform root)
    {
        Scene scene = root.gameObject.scene;
        if (!_byScene.TryGetValue(scene.handle, out Dictionary<IntPtr, int>? ordinals))
        {
            ordinals = new Dictionary<IntPtr, int>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (GameObject other in scene.GetRootGameObjects())
            {
                string name = other.name;
                int ordinal = seen.GetValueOrDefault(name);
                seen[name] = ordinal + 1;
                ordinals[other.transform.Pointer] = ordinal;
            }

            _byScene[scene.handle] = ordinals;
        }

        // Not among them (it became a root since): counted the slow way.
        return ordinals.TryGetValue(root.Pointer, out int found) ? found : ObjectRefs.RootOrdinal(root, scene.GetRootGameObjects());
    }
}
#endif
