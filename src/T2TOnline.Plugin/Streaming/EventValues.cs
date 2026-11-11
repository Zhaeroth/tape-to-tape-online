#if HAS_INTEROP
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using T2TOnline.Core.Serialization;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Converts event arguments to <see cref="NetValue"/>s on the host and back on a friend's PC.
/// <list type="bullet">
/// <item>Whole values: numbers, flags, enums, text, vectors, rotations, colours, references to game objects and assets
/// (see <see cref="ObjectRefs"/>), game lists and Unity collisions (see <see cref="CompoundValues"/>).</item>
/// <item>Argument classes (<c>Events.ShootingArg</c> and the like) are flattened into their sendable fields and rebuilt
/// field by field on the other side; fields that can't be sent (callbacks, interfaces) stay at their default.</item>
/// <item>Anything else is sent as null. A callback argument (<c>ScreenTransition</c>'s <c>Action</c>) arrives as a no-op:
/// what it would have done on the host comes from the host.</item>
/// </list>
/// </summary>
internal static class EventValues
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>?> FieldCache = new();
    private static readonly ConcurrentDictionary<Type, Func<object>?> Constructors = new();

    /// <summary>True for a type sent as one value.</summary>
    public static bool IsWhole(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Vector2) || type == typeof(Vector3) ||
        type == typeof(Quaternion) || type == typeof(Color) || typeof(UnityEngine.Object).IsAssignableFrom(type) ||
        type == typeof(Collision) || CompoundValues.IsList(type);

    /// <summary>
    /// The fields an argument class is flattened into, in declaration order (base class first), or null when the type is
    /// sent whole or can't be sent. Only the game's own plain classes are flattened, never engine or runtime types.
    /// </summary>
    public static IReadOnlyList<PropertyInfo>? FieldsOf(Type type) => FieldCache.GetOrAdd(type, FindFields);

    /// <summary>Converts one argument (or field) value for sending.</summary>
    public static NetValue ToNet(object? value, Func<UnityEngine.Object, NetValue> refOf) => value switch
    {
        null => NetValue.Null,
        bool b => NetValue.FromBool(b),
        float f => NetValue.FromFloat(f),
        double d => NetValue.FromDouble(d),
        string s => NetValue.FromString(s.Length > 120 ? s[..120] : s),
        Enum e => NetValue.FromInt(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture)),
        IntPtr => NetValue.Null,
        IConvertible c when value.GetType().IsPrimitive => NetValue.FromInt(c.ToInt64(System.Globalization.CultureInfo.InvariantCulture)),
        Vector2 v => NetValue.FromVector2(v.x, v.y),
        Vector3 v => NetValue.FromVector3(v.x, v.y, v.z),
        Quaternion q => NetValue.FromQuaternion(q.x, q.y, q.z, q.w),
        Color c => NetValue.FromColor(c.r, c.g, c.b, c.a),
        UnityEngine.Object o => refOf(o),
        Collision collision => CompoundValues.CollisionToNet(collision, refOf),
        Il2CppObjectBase list when CompoundValues.IsList(list.GetType()) => CompoundValues.ListToNet(list, refOf),
        _ => NetValue.Null,
    };

    /// <summary>
    /// Converts a received value to <paramref name="type"/>, or null (the type's default for value types) when it doesn't
    /// fit. <paramref name="resolveRef"/> finds referenced objects locally.
    /// </summary>
    public static object? FromNet(NetValue value, Type type, Func<NetValue, Type, object?> resolveRef)
    {
        if (type == typeof(bool))
        {
            return value.Kind == NetValueKind.Flag && value.WholeValue != 0;
        }

        if (type.IsEnum)
        {
            return Enum.ToObject(type, value.Kind == NetValueKind.Whole ? value.WholeValue : 0);
        }

        if (type == typeof(float))
        {
            return value.Kind is NetValueKind.Single32 or NetValueKind.Double64 ? (float)value.Number : 0f;
        }

        if (type == typeof(double))
        {
            return value.Kind is NetValueKind.Single32 or NetValueKind.Double64 ? value.Number : 0d;
        }

        if (type.IsPrimitive)
        {
            try
            {
                return Convert.ChangeType(value.Kind == NetValueKind.Whole ? value.WholeValue : 0, type, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is OverflowException or InvalidCastException)
            {
                // Out of the type's range, or a primitive Convert can't make (IntPtr, UIntPtr): its default.
                return Activator.CreateInstance(type);
            }
        }

        if (type == typeof(string))
        {
            return value.Kind == NetValueKind.Text ? value.Text : null;
        }

        if (type == typeof(Vector3))
        {
            return value.Kind == NetValueKind.Vector3 ? new Vector3(value.X, value.Y, value.Z) : Vector3.zero;
        }

        if (type == typeof(Vector2))
        {
            return value.Kind == NetValueKind.Vector2 ? new Vector2(value.X, value.Y) : Vector2.zero;
        }

        if (type == typeof(Quaternion))
        {
            return value.Kind == NetValueKind.Rotation ? new Quaternion(value.X, value.Y, value.Z, value.W) : Quaternion.identity;
        }

        if (type == typeof(Color))
        {
            return value.Kind == NetValueKind.Colour ? new Color(value.X, value.Y, value.Z, value.W) : Color.clear;
        }

        if (typeof(UnityEngine.Object).IsAssignableFrom(type))
        {
            return resolveRef(value, type);
        }

        if (type == typeof(Collision))
        {
            return CompoundValues.CollisionFromNet(value, resolveRef);
        }

        if (CompoundValues.IsList(type))
        {
            return CompoundValues.ListFromNet(value, type, resolveRef);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    /// <summary>A new, empty instance of an argument class, or null if it can't be made.</summary>
    public static object? NewInstance(Type type)
    {
        Func<object>? make = Constructors.GetOrAdd(type, FindConstructor);
        return make?.Invoke();
    }

    /// <summary>An IL2CPP <c>System.Action</c> that does nothing, for callback arguments.</summary>
    public static Il2CppSystem.Action NoOpAction() => DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => { }))!;

    /// <summary>Casts an interop object to the interop type <paramref name="type"/>, or null if it isn't one.</summary>
    public static object? CastTo(Il2CppObjectBase value, Type type)
    {
        if (type.IsInstanceOfType(value))
        {
            return value;
        }

        IntPtr wanted = Il2CppClassPointerStore.GetNativeClassPointer(type);
        IntPtr actual = IL2CPP.il2cpp_object_get_class(value.Pointer);
        if (wanted == IntPtr.Zero || actual == IntPtr.Zero || !IL2CPP.il2cpp_class_is_assignable_from(wanted, actual))
        {
            return null;
        }

        return Activator.CreateInstance(type, value.Pointer);
    }

    private static IReadOnlyList<PropertyInfo>? FindFields(Type type)
    {
        if (IsWhole(type) || type.IsValueType || type.IsInterface || !typeof(Il2CppObjectBase).IsAssignableFrom(type) ||
            typeof(Il2CppSystem.Delegate).IsAssignableFrom(type) || typeof(Il2CppSystem.Type).IsAssignableFrom(type) ||
            !IsGameType(type) || type.IsGenericType)
        {
            return null;
        }

        var chain = new List<Type>();
        for (Type? current = type; current != null && IsGameType(current); current = current.BaseType)
        {
            chain.Insert(0, current);
        }

        var fields = new List<PropertyInfo>();
        foreach (Type declaring in chain)
        {
            fields.AddRange(declaring.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && IsWhole(p.PropertyType) &&
                    declaring.GetField($"NativeFieldInfoPtr_{p.Name}", BindingFlags.NonPublic | BindingFlags.Static) != null)
                .OrderBy(p => p.MetadataToken));
        }

        return fields;
    }

    /// <summary>The game's own types (not the runtime's or the engine's).</summary>
    private static bool IsGameType(Type type)
    {
        string assembly = type.Assembly.GetName().Name ?? string.Empty;
        return assembly.StartsWith("Il2Cpp", StringComparison.Ordinal) && assembly != "Il2Cppmscorlib" &&
            !assembly.StartsWith("Il2CppSystem", StringComparison.Ordinal) && !assembly.StartsWith("Il2CppInterop", StringComparison.Ordinal);
    }

    private static Func<object>? FindConstructor(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is ConstructorInfo plain)
        {
            return () => plain.Invoke(null);
        }

        // No parameterless constructor (ShootingArg): allocate the object and wrap it; every field is set right after.
        ConstructorInfo? wrap = type.GetConstructor(new[] { typeof(IntPtr) });
        IntPtr klass = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (wrap == null || klass == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(klass))
        {
            return null;
        }

        return () => wrap.Invoke(new object[] { IL2CPP.il2cpp_object_new(klass) });
    }
}
#endif
