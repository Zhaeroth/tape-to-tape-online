using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// Counts the interop assemblies loaded before MelonLoader's generator runs (D108). MelonLoader's sort of the plugins by their
/// references loads every one this plugin names, so on a launch where the generator doesn't run, a count is normal; on one where
/// it does, <see cref="StaleInterop"/> has cleared them first and nothing should load, and a load there means the generator will
/// fail on that file. Started from the plugin's constructor, said once at <c>OnApplicationEarlyStart</c>, right before the
/// generator's turn, with what asked for the first ones.
/// <para>
/// Nothing here may touch an interop type: it only reads an assembly's location and a stack trace's frames.
/// </para>
/// </summary>
internal static class EarlyInteropLoads
{
    private const int MaxNamed = 4;

    private static readonly object Gate = new();
    private static readonly List<string> Loads = new();
    private static bool _watching;
    private static int _count;

    /// <summary>Starts watching, and notes what an earlier load already brought in. Never throws.</summary>
    internal static void Start()
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
            _watching = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Note(assembly, "loaded before the plugin");
            }
        }
        catch (Exception)
        {
            // A watch that can't start only means the early-start line says less.
        }
    }

    /// <summary>
    /// One line for the log, or null when nothing was loaded: <paramref name="generatorExpected"/> says whether the generator runs
    /// now (a load is then a failure to come), else the loads are the sort's, and normal. Never throws.
    /// </summary>
    internal static string? Summary(bool generatorExpected)
    {
        try
        {
            lock (Gate)
            {
                if (_count == 0)
                {
                    return null;
                }

                string named = $"{string.Join("; ", Loads)}{(_count > Loads.Count ? $"; {_count - Loads.Count} more" : string.Empty)}";
                return generatorExpected
                    ? $"{_count} of MelonLoader's interop assemblies are loaded although its generator remakes them now, so it can't delete them and may stop the game (\"Failed to start MelonLoader\"): {named}."
                    : $"MelonLoader's sort of the plugins loaded {_count} interop assemblies before its generator's turn, as on any launch where they're current: {named}.";
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Stops watching (the generator has had its turn). Never throws.</summary>
    internal static void Stop()
    {
        try
        {
            if (_watching)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnLoad;
                _watching = false;
            }
        }
        catch (Exception)
        {
            // Nothing to undo.
        }
    }

    private static void OnLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            Note(args.LoadedAssembly, AskedBy());
        }
        catch (Exception)
        {
            // Never into the runtime's loader.
        }
    }

    private static void Note(Assembly assembly, string askedBy)
    {
        string location = assembly.IsDynamic ? string.Empty : assembly.Location;
        if (location.IndexOf("Il2CppAssemblies", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        lock (Gate)
        {
            _count++;
            if (Loads.Count < MaxNamed)
            {
                Loads.Add($"{assembly.GetName().Name} ({askedBy})");
            }
        }
    }

    // Whose call brought the assembly in: the first frame of ours if there is one, else the first frames outside the runtime's
    // own loading code (MelonLoader's DependencyGraph, on a normal launch).
    private static string AskedBy()
    {
        var trace = new StackTrace(2, false);
        var seen = new List<string>();
        foreach (StackFrame frame in trace.GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            if (method == null || type == null || type == typeof(EarlyInteropLoads))
            {
                continue;
            }

            string ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("T2TOnline", StringComparison.Ordinal))
            {
                return $"from {type.FullName}.{method.Name}";
            }

            if (!ns.StartsWith("System", StringComparison.Ordinal) && !ns.StartsWith("Internal", StringComparison.Ordinal) &&
                !ns.StartsWith("MelonLoader.Resolver", StringComparison.Ordinal) && seen.Count < 2)
            {
                seen.Add($"{type.Name}.{method.Name}");
            }
        }

        return seen.Count == 0 ? "the runtime" : "from " + string.Join(" < ", seen);
    }
}
