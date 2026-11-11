#if HAS_INTEROP
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using T2TOnline.Core.OnlineSettings;
using T2TOnline.Plugin.NativeUi;

namespace T2TOnline.Plugin.Campaign.Menus;

/// <summary>
/// Game arrays of references the mod fills with objects the array may be the only holder of: fresh strings, fresh objects,
/// or the game's own objects when the list that held them is dropped as the new one is set (the event and Nurse views, the
/// logo lists, a lobby button's text data, the lobby's words). The interop's array setters store an element in raw, with no
/// write barrier (decompiled: <c>Il2CppReferenceArray.StoreValue</c>, <c>Il2CppStringArray</c>'s indexer), and the game's
/// collector is incremental: an array it has already marked would not be scanned again, so a fresh string held only there
/// could be freed while a card still draws it. Each element is stored here through the runtime's barrier instead
/// (<see cref="NativeFields.Write"/>), the store the game's own code makes. An array of live components the scene holds
/// anyway needs none of this.
/// </summary>
internal static class GameArrays
{
    /// <summary>A new game string array holding <paramref name="texts"/> (null entries stay null).</summary>
    public static Il2CppStringArray Strings(IReadOnlyList<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var array = new Il2CppStringArray(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            if (texts[i] is { } text)
            {
                Store(array, i, IL2CPP.ManagedStringToIl2Cpp(text));
            }
        }

        return array;
    }

    /// <summary>Stores the game object <paramref name="value"/> as element <paramref name="index"/> of the game array of references <paramref name="array"/>, through the write barrier.</summary>
    public static void Store(Il2CppArrayBase array, int index, IntPtr value)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (index < 0 || index >= array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "outside the array");
        }

        if (IL2CPP.il2cpp_array_element_size(IL2CPP.il2cpp_object_get_class(array.Pointer)) != IntPtr.Size)
        {
            throw new ArgumentException("not an array of references", nameof(array));
        }

        // A reference element is a struct of one reference: pointer-sized, at the element's start.
        long address = ValueArrayLayout.ElementFieldAddress(array.Pointer.ToInt64(), index, IntPtr.Size, ValueArrayLayout.ObjectHeaderSize(IntPtr.Size), IntPtr.Size);
        NativeFields.Write(array.Pointer, new IntPtr(address), value);
    }
}
#endif
