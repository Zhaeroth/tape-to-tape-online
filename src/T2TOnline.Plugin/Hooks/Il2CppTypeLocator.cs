using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader.Utils;

namespace T2TOnline.Plugin.Hooks;

/// <summary>
/// Finds the interop type MelonLoader generated for a game type, by the game's own name.
/// MelonLoader prefixes generated namespaces with "Il2Cpp" ("Sentry.Unity.X" becomes "Il2CppSentry.Unity.X",
/// and global-namespace types land in "Il2Cpp"), so each name is tried in its plain and prefixed forms.
/// Nested types use '+' or '/' separators interchangeably. Interop assemblies load lazily, so on the first miss
/// every assembly in MelonLoader's Il2CppAssemblies folder is loaded and the index rebuilt.
/// </summary>
internal static class Il2CppTypeLocator
{
    private static Dictionary<string, Type>? _byName;
    private static bool _loadedAll;

    /// <summary>Returns the interop type for <paramref name="gameTypeName"/>, or null if the game has no such type.</summary>
    public static Type? Find(string gameTypeName)
    {
        string name = gameTypeName.Replace('/', '+');
        Type? found = Lookup(_byName ??= BuildIndex(), name);
        if (found == null && !_loadedAll)
        {
            LoadAllInteropAssemblies();
            _byName = BuildIndex();
            found = Lookup(_byName, name);
        }

        return found;
    }

    /// <summary>Forgets the index, for when more assemblies have loaded.</summary>
    public static void Invalidate() => _byName = null;

    private static Type? Lookup(Dictionary<string, Type> index, string name)
    {
        foreach (string candidate in Candidates(name))
        {
            foreach (string form in NestedForms(candidate))
            {
                if (index.TryGetValue(form, out Type? type))
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The decompiled listing writes nested types with dots ("Rewired.ReInput.ControllerHelper"); reflection uses '+'
    /// ("Rewired.ReInput+ControllerHelper"). Yields the name as given, then with its last one, two and three dots
    /// turned into '+'.
    /// </summary>
    private static IEnumerable<string> NestedForms(string name)
    {
        yield return name;
        string nested = name;
        for (int depth = 0; depth < 3; depth++)
        {
            int dot = nested.LastIndexOf('.');
            if (dot <= 0)
            {
                yield break;
            }

            nested = string.Concat(nested.AsSpan(0, dot), "+", nested.AsSpan(dot + 1));
            yield return nested;
        }
    }

    private static void LoadAllInteropAssemblies()
    {
        _loadedAll = true;
        string directory = MelonEnvironment.Il2CppAssembliesDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                // Load by name so MelonLoader's resolver hands back the copy it manages (or the one already loaded).
                Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(file)));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // Not every file in the folder is meant to be loaded on its own; skip it.
            }
        }
    }

    private static IEnumerable<string> Candidates(string name)
    {
        yield return "Il2Cpp" + name;
        yield return "Il2Cpp." + name;
        yield return name;
    }

    private static Dictionary<string, Type> BuildIndex()
    {
        var index = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(IsInteropAssembly))
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (Type type in types)
            {
                if (type.FullName != null)
                {
                    index.TryAdd(type.FullName, type);
                }
            }
        }

        return index;
    }

    private static bool IsInteropAssembly(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            return false;
        }

        string location;
        try
        {
            location = assembly.Location;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        return location.Contains("Il2CppAssemblies", StringComparison.OrdinalIgnoreCase);
    }
}
