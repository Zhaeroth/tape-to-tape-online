#if HAS_INTEROP
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

namespace T2TOnline.Plugin.Native;

/// <summary>Reads IL2CPP collection interfaces, which Il2CppInterop exposes without .NET's enumeration support.</summary>
internal static class Il2CppLists
{
    /// <summary>The items of an IL2CPP <c>IList&lt;T&gt;</c>, in order. An empty sequence for null.</summary>
    public static IEnumerable<T> Items<T>(Il2CppSystem.Collections.Generic.IList<T>? list)
        where T : Il2CppObjectBase
    {
        if (list == null)
        {
            yield break;
        }

        // A concrete List<T> or any ICollection<T> gives a count to index up to; otherwise enumerate.
        int count = list.TryCast<Il2CppSystem.Collections.Generic.List<T>>()?.Count
            ?? list.TryCast<Il2CppSystem.Collections.Generic.ICollection<T>>()?.Count
            ?? -1;
        if (count >= 0)
        {
            for (int i = 0; i < count; i++)
            {
                yield return list[i];
            }

            yield break;
        }

        foreach (T item in Enumerate(list.Cast<Il2CppSystem.Collections.Generic.IEnumerable<T>>()))
        {
            yield return item;
        }
    }

    /// <summary>The items of an IL2CPP <c>IReadOnlyList&lt;T&gt;</c>, in order. An empty sequence for null.</summary>
    public static IEnumerable<T> Items<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T>? list)
        where T : Il2CppObjectBase
    {
        if (list == null)
        {
            yield break;
        }

        int count = list.TryCast<Il2CppSystem.Collections.Generic.List<T>>()?.Count
            ?? list.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>()?.Count
            ?? -1;
        if (count >= 0)
        {
            for (int i = 0; i < count; i++)
            {
                yield return list[i];
            }

            yield break;
        }

        foreach (T item in Enumerate(list.Cast<Il2CppSystem.Collections.Generic.IEnumerable<T>>()))
        {
            yield return item;
        }
    }

    /// <summary>The items of an IL2CPP <c>List&lt;T&gt;</c>, in order. An empty sequence for null.</summary>
    public static IEnumerable<T> Items<T>(Il2CppSystem.Collections.Generic.List<T>? list)
        where T : Il2CppObjectBase
    {
        int count = list?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            yield return list![i];
        }
    }

    /// <summary>The items of an IL2CPP <c>IEnumerable&lt;T&gt;</c> (a game method's iterator), in order. An empty sequence for null.</summary>
    public static IEnumerable<T> Items<T>(Il2CppSystem.Collections.Generic.IEnumerable<T>? sequence)
        where T : Il2CppObjectBase =>
        sequence == null ? System.Linq.Enumerable.Empty<T>() : Enumerate(sequence);

    private static IEnumerable<T> Enumerate<T>(Il2CppSystem.Collections.Generic.IEnumerable<T> sequence)
        where T : Il2CppObjectBase
    {
        Il2CppSystem.Collections.Generic.IEnumerator<T> items = sequence.GetEnumerator();
        Il2CppSystem.Collections.IEnumerator cursor = items.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (cursor.MoveNext())
        {
            yield return items.Current;
        }
    }
}
#endif
