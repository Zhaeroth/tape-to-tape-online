#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppUltimateReplay;
using T2TOnline.Core.Stream;

namespace T2TOnline.Plugin.Replay;

/// <summary>
/// A replay component's type as the stream names it (<see cref="ComponentAlignment.TypeHash"/> of its IL2CPP class's
/// "Namespace.Class"), read from the object's own class and cached per class: an object's components are read once, when
/// it joins the stream (host) or playback (friend).
/// </summary>
internal static class ReplayComponentTypes
{
    private static readonly Dictionary<IntPtr, (uint Hash, string Name)> ByClass = new();
    private static readonly Dictionary<uint, string> ByHash = new();

    /// <summary>The class name of a type hash read on this PC before, or the hash in hex (for a log line).</summary>
    public static string NameOfHash(uint hash) => ByHash.TryGetValue(hash, out string? name) ? name : $"type {hash:x8}";

    /// <summary>The component's type hash, or 0 when its class can't be read (a destroyed component).</summary>
    public static uint Of(ReplayBehaviour behaviour) => Read(behaviour).Hash;

    /// <summary>The component's class as the game names it ("Skaters.ReplayForward"), or "?" when it can't be read.</summary>
    public static string NameOf(ReplayBehaviour behaviour) => Read(behaviour).Name;

    private static (uint Hash, string Name) Read(ReplayBehaviour behaviour)
    {
        IntPtr klass = behaviour == null ? IntPtr.Zero : IL2CPP.il2cpp_object_get_class(behaviour.Pointer);
        if (klass == IntPtr.Zero)
        {
            return (0, "?");
        }

        if (!ByClass.TryGetValue(klass, out (uint Hash, string Name) type))
        {
            string? space = IL2CPP.il2cpp_class_get_namespace_(klass);
            string name = IL2CPP.il2cpp_class_get_name_(klass) ?? string.Empty;
            string full = string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
            type = (ComponentAlignment.TypeHash(full), full);
            ByClass[klass] = type;
            ByHash[type.Hash] = full;
        }

        return type;
    }

    /// <summary>The types of <paramref name="behaviours"/>, in their order.</summary>
    public static uint[] Of(IReadOnlyList<ReplayBehaviour> behaviours)
    {
        var types = new uint[behaviours.Count];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = Of(behaviours[i]);
        }

        return types;
    }
}
#endif
