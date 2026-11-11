#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Native;

namespace T2TOnline.Plugin.NativeUi;

/// <summary>
/// Reference writes into the game's objects where the interop can't make them safely, and the GC handles that keep the mod's objects alive.
/// A reference stored into a game object's field must go through the runtime's write barrier, or an incremental collection that already marked
/// the object would free what it now points to. Il2CppInterop's field setters do (they call <c>il2cpp_gc_wbarrier_set_field</c>), but its arrays
/// of structs copy an element in raw, and a struct field's setter copies the struct in raw: so those references are written here, one field at a
/// time, with the same export. On the Nov 6 build the export stores the value, then, when the collector is incremental, marks the 4 KB page of
/// the field in the collector's dirty-page bitmap: the very flag and bitmap the game's own inlined barriers use (verified in the game's code).
/// </summary>
internal static class NativeFields
{
    private static bool _modeSaid;

    /// <summary>The runtime's collector is incremental (read once; null when the export couldn't be called).</summary>
    public static bool? Incremental { get; private set; }

    /// <summary>The collector's mode, read once (the log's line says it).</summary>
    public static string Mode()
    {
        if (!_modeSaid)
        {
            _modeSaid = true;
            try
            {
                Incremental = Il2CppApi.GcIsIncremental();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Incremental = null;
            }
        }

        return Incremental switch { true => "yes", false => "no", _ => "unknown" };
    }

    /// <summary>The boxed offset of an instance field of the IL2CPP class <paramref name="klass"/> (its place after the object header). Throws when there's none.</summary>
    public static int FieldOffset(IntPtr klass, string name)
    {
        IntPtr field = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(klass, name);
        if (field == IntPtr.Zero)
        {
            throw new InvalidOperationException($"the game has no field {name}");
        }

        return checked((int)IL2CPP.il2cpp_field_get_offset(field));
    }

    /// <summary>The reference at <paramref name="address"/> (a field of a game object or array), read in place.</summary>
    public static IntPtr Read(IntPtr address) => Marshal.ReadIntPtr(address);

    /// <summary>Stores <paramref name="value"/> at <paramref name="address"/>, a reference field inside <paramref name="owner"/>, through the runtime's write barrier.</summary>
    public static void Write(IntPtr owner, IntPtr address, IntPtr value) => IL2CPP.il2cpp_gc_wbarrier_set_field(owner, address, value);

    /// <summary>
    /// Points a game object's <c>LocalizedString</c> field (<paramref name="field"/>, of <paramref name="owner"/>'s class) at <paramref name="term"/>: its
    /// <c>mTerm</c>, the struct's first field, written in place through the barrier (the interop's setter would copy the struct in raw).
    /// </summary>
    public static void WriteTerm(Il2CppObjectBase owner, IntPtr ownerClass, string field, string term)
    {
        ArgumentNullException.ThrowIfNull(owner);
        int header = Core.OnlineSettings.ValueArrayLayout.ObjectHeaderSize(IntPtr.Size);
        int fieldAt = FieldOffset(ownerClass, field);
        int termAt = FieldOffset(Il2CppClassPointerStore<Il2CppI2.Loc.LocalizedString>.NativeClassPtr, "mTerm") - header;
        if (termAt < 0)
        {
            throw new InvalidOperationException("the game's LocalizedString isn't laid out as expected");
        }

        IntPtr address = IntPtr.Add(owner.Pointer, fieldAt + termAt);
        Write(owner.Pointer, address, IL2CPP.ManagedStringToIl2Cpp(term));
    }

    /// <summary>A strong GC handle on a game object (nothing collects it until the handle is freed).</summary>
    public static nint Hold(Il2CppObjectBase thing)
    {
        ArgumentNullException.ThrowIfNull(thing);
        nint handle = IL2CPP.il2cpp_gchandle_new(thing.Pointer, false);
        return handle != 0 ? handle : throw new InvalidOperationException("the runtime gave no GC handle");
    }

    /// <summary>Frees handles from <see cref="Hold"/>. Never throws.</summary>
    public static void Free(List<nint> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        foreach (nint handle in handles)
        {
            try
            {
                IL2CPP.il2cpp_gchandle_free(handle);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"Settings menu: freeing a GC handle failed ({ModLog.SafeErrorText(ex)}).");
            }
        }

        handles.Clear();
    }
}
#endif
