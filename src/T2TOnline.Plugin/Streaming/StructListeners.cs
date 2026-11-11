#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using T2TOnline.Plugin.Diagnostics;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Listeners for events whose argument is one of the game's structs (<c>SkaterCheck</c>'s <c>SkaterCheckEventArgs</c>:
/// every bodycheck). Il2CppInterop can't turn a managed delegate into an IL2CPP one with such a parameter, so the IL2CPP
/// delegate is built here by hand, the way IL2CPP lays one out:
/// <list type="bullet">
/// <item>the event's exact <c>Action&lt;T&gt;</c> class (the game's raise checks it);</item>
/// <item><c>invoke_impl</c> and <c>method_ptr</c> point at one native-callable function, which IL2CPP's delegate invoke
/// calls as <c>invoke_impl(method_code, arg, method)</c> (from this build's code);</item>
/// <item><c>method_code</c> carries the listener's slot and that slot's generation, so the function knows which callback to
/// run. A listener taken off its event is released (<see cref="Release"/>): its slot is emptied for the next one, and a
/// stray call of the old delegate (one that stayed on its event) finds a different generation and does nothing, rather
/// than running a closure from an earlier match (the recorder's would record events twice);</item>
/// <item><c>method</c> is the class's own <c>Invoke</c>, so hashing and reflection on the delegate work;</item>
/// <item><c>m_target</c> is an object of its own. IL2CPP's delegate equality (which the game's <c>Unregister</c> and
/// <c>Register</c>'s duplicate check use) compares target and method, and every hand-built listener shares the method:
/// without a target of its own each would equal the others, and removing one (the mod's counter at the end of playback)
/// removed whichever came first (seen with GameStats' bodycheck listener's stand-in). The invoke passes
/// <c>method_code</c>, never the target, so it isn't used otherwise.</item>
/// </list>
/// A struct over 8 bytes arrives as a pointer to a copy (Windows x64); one of 1, 2, 4 or 8 bytes arrives in the register
/// itself. Either way it's boxed into a new IL2CPP object and wrapped as the interop type for the callback.
/// </summary>
internal static class StructListeners
{
    private static readonly List<Entry?> Entries = new();
    private static readonly Invoker Callback = OnInvoke;
    private static readonly IntPtr CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
    private static readonly IntPtr Scratch = Marshal.AllocHGlobal(16);
    private static int _failures;
    private static int _generation;
    private static int _strays;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Invoker(IntPtr methodCode, IntPtr argument, IntPtr method);

    /// <summary>True when an event needs a hand-built listener: an argument that's a game struct (an interop value-type class).</summary>
    public static bool Needed(Type[] argTypes) => argTypes.Any(IsGameStruct);

    /// <summary>A listener of <paramref name="actionType"/> (one struct argument) that calls <paramref name="callback"/>.</summary>
    public static Il2CppSystem.Delegate Create(Type actionType, Type[] argTypes, Action<object?[]> callback)
    {
        if (argTypes.Length != 1 || !IsGameStruct(argTypes[0]))
        {
            throw new NotSupportedException($"hand-built listeners take exactly one struct argument ({string.Join(", ", argTypes.Select(t => t.Name))})");
        }

        IntPtr argClass = Il2CppClassPointerStore.GetNativeClassPointer(argTypes[0]);
        IntPtr actionClass = Il2CppClassPointerStore.GetNativeClassPointer(actionType);
        IntPtr invoke = actionClass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(actionClass, "Invoke", 1);
        if (argClass == IntPtr.Zero || invoke == IntPtr.Zero)
        {
            throw new InvalidOperationException($"no IL2CPP class for {argTypes[0].Name} or {actionType.Name}");
        }

        uint align = 0;
        int size = IL2CPP.il2cpp_class_value_size(argClass, ref align);
        int slot = Entries.IndexOf(null);
        if (slot < 0)
        {
            slot = Entries.Count;
            Entries.Add(null);
        }

        int generation = ++_generation;
        Entries[slot] = new Entry(argTypes[0], argClass, size is 1 or 2 or 4 or 8, callback, generation);
        var listener = new Il2CppSystem.Delegate(IL2CPP.il2cpp_object_new(actionClass))
        {
            method_ptr = CallbackPointer,
            invoke_impl = CallbackPointer,
            method_code = Code(slot + 1, generation),
            method = invoke,
            m_target = new Il2CppSystem.Object(),
        };
        return listener;
    }

    /// <summary>
    /// A hand-built listener has been taken off its event: its slot is emptied (reused by the next one), and the delegate
    /// does nothing from now on. Anything else is left alone. Never throws.
    /// </summary>
    public static void Release(Il2CppSystem.Delegate listener)
    {
        try
        {
            if (listener.method_ptr != CallbackPointer)
            {
                return;
            }

            (int number, int generation) = Decode(listener.method_code);
            if (number >= 1 && number <= Entries.Count && Entries[number - 1]?.Generation == generation)
            {
                Entries[number - 1] = null;
            }
        }
        catch (Exception ex)
        {
            ModLog.Warn($"Struct listener: couldn't release one: {ModLog.SafeErrorText(ex)}");
        }
    }

    /// <summary>For the log: hand-built listeners in use, and calls of released ones that did nothing.</summary>
    public static string Describe() => $"{Entries.Count(e => e != null)} hand-built listeners in use, {_strays} calls of released ones ignored";

    /// <summary>The slot number (from 1) in the low 32 bits of <c>method_code</c>, its generation in the high 32.</summary>
    private static IntPtr Code(int number, int generation) => (IntPtr)(((long)generation << 32) | (uint)number);

    private static (int Number, int Generation) Decode(IntPtr code) => ((int)(code.ToInt64() & 0xFFFFFFFF), (int)(code.ToInt64() >> 32));

    private static bool IsGameStruct(Type type) => typeof(Il2CppSystem.ValueType).IsAssignableFrom(type) && !type.IsValueType && !type.IsEnum;

    private static void OnInvoke(IntPtr methodCode, IntPtr argument, IntPtr method)
    {
        // Called from inside the game's event raise: never throw.
        try
        {
            (int number, int generation) = Decode(methodCode);
            Entry? entry = number >= 1 && number <= Entries.Count ? Entries[number - 1] : null;
            if (entry == null || entry.Generation != generation)
            {
                // A released listener still on its event (its unregister failed): nothing to run.
                _strays++;
                return;
            }

            IntPtr data = argument;
            if (entry.InRegister)
            {
                Marshal.WriteInt64(Scratch, argument.ToInt64());
                data = Scratch;
            }

            IntPtr boxed = IL2CPP.il2cpp_value_box(entry.Class, data);
            entry.Callback(new[] { Activator.CreateInstance(entry.ArgType, boxed) });
        }
        catch (Exception ex)
        {
            if (_failures++ < 3)
            {
                ModLog.Error("Struct listener: a callback failed", ex);
            }
        }
    }

    private sealed record Entry(Type ArgType, IntPtr Class, bool InRegister, Action<object?[]> Callback, int Generation);
}

/// <summary>
/// Do-nothing stand-ins for detached listeners (<see cref="EventChannel"/>) that the game takes for the listeners
/// themselves. In this build, IL2CPP's delegate equality (<c>MulticastDelegate.Equals</c> → <c>Delegate.Equals</c>)
/// compares the class, the target, the method object (<c>GetMethodImpl</c>, from <c>method</c>,
/// <c>method_is_virtual</c> and the target, cached in <c>method_info</c>), <c>data</c> and
/// the invocation list; the hash is the method object's hash mixed with the target's address. So a delegate
/// of the listener's own class with its target, method, virtual flag and data is equal to it, with the same hash, while
/// its <c>invoke_impl</c> and <c>method_ptr</c> point at a function that does nothing: the game's own
/// <c>Unregister</c> (<c>Array.IndexOf</c>, then the persistent set's <c>Remove</c>) and <c>Register</c>'s duplicate
/// check find the stand-in, and the persistent set answers for it as for the listener. Each one is checked against its
/// listener with the game's own <c>Equals</c> and <c>GetHashCode</c> before it's used.
/// </summary>
internal static class StandIns
{
    private static readonly NoOp Callback = OnInvoke;
    private static readonly IntPtr CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
    private static int _refused;

    /// <summary>
    /// IL2CPP calls a delegate as <c>invoke_impl(method_code, args..., method)</c>; on Windows x64 a function that reads
    /// none of its arguments and returns nothing suits every event's signature (the caller owns its arguments).
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NoOp(IntPtr methodCode, IntPtr argument, IntPtr method);

    /// <summary>A stand-in the game takes for <paramref name="listener"/>, or null (logged for the first few) if one can't be made.</summary>
    public static Il2CppSystem.Delegate? EqualTo(Il2CppSystem.Delegate listener)
    {
        IntPtr klass = IL2CPP.il2cpp_object_get_class(listener.Pointer);
        if (klass == IntPtr.Zero || listener.method == IntPtr.Zero)
        {
            return Refuse("the listener has no method");
        }

        try
        {
            var standIn = new Il2CppSystem.Delegate(IL2CPP.il2cpp_object_new(klass))
            {
                method_ptr = CallbackPointer,
                invoke_impl = CallbackPointer,
                m_target = listener.m_target,
                method = listener.method,
                method_is_virtual = listener.method_is_virtual,
                data = listener.data,
            };

            return standIn.Equals(listener) && listener.Equals(standIn) && standIn.GetHashCode() == listener.GetHashCode()
                ? standIn
                : Refuse("the game doesn't take it for the listener");
        }
        catch (Exception ex)
        {
            return Refuse(ModLog.SafeErrorText(ex.InnerException ?? ex));
        }
    }

    private static Il2CppSystem.Delegate? Refuse(string why)
    {
        if (_refused++ < 3)
        {
            ModLog.Warn($"Event channel: no equal stand-in for a listener ({why}); it gets a plain one.");
        }

        return null;
    }

    private static void OnInvoke(IntPtr methodCode, IntPtr argument, IntPtr method)
    {
        // A detached listener's slot during playback: nothing to do, never anything to throw.
    }
}
#endif
