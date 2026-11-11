#if HAS_INTEROP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppBishopGames;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using T2TOnline.Core.Serialization;
using T2TOnline.Plugin.Diagnostics;
using T2TOnline.Plugin.Hooks;

namespace T2TOnline.Plugin.Streaming;

/// <summary>One event on the game's buses, as read from the running build.</summary>
internal sealed class BusEvent
{
    private MethodInfo? _invoke;

    public BusEvent(ushort id, string name, AbstractSingleEvent @event, Type[] argTypes, Type? actionType)
    {
        Id = id;
        Name = name;
        Event = @event;
        ArgTypes = argTypes;
        ActionType = actionType;
        Fields = argTypes.Select(EventValues.FieldsOf).ToArray();
        ValueNames = NamesOf(argTypes, Fields);
    }

    /// <summary>Position in the catalog: the id the stream uses.</summary>
    public ushort Id { get; }

    /// <summary>Bus and field, for example "GameplayEvents.Goal".</summary>
    public string Name { get; }

    /// <summary>The event object.</summary>
    public AbstractSingleEvent Event { get; }

    /// <summary>The event's argument types (interop types), 0 to 4.</summary>
    public Type[] ArgTypes { get; }

    /// <summary>The exact IL2CPP delegate type its listeners have (Il2CppSystem.Action or Action&lt;..&gt;); null if unsupported.</summary>
    public Type? ActionType { get; }

    /// <summary>Per argument: the fields an argument class is flattened into, or null for a value sent whole.</summary>
    public IReadOnlyList<PropertyInfo>?[] Fields { get; }

    /// <summary>The names values are sent under, in order ("arg0", or "arg0.field" for an argument class's fields).</summary>
    public IReadOnlyList<string> ValueNames { get; }

    /// <summary>Host side: the values for one raise, in <see cref="ValueNames"/> order. Read inside the callback: arguments are mutable.</summary>
    public void Encode(object?[] args, Func<UnityEngine.Object, NetValue> refOf, List<NetValue> values)
    {
        for (int i = 0; i < ArgTypes.Length; i++)
        {
            object? arg = i < args.Length ? args[i] : null;
            if (Fields[i] is not IReadOnlyList<PropertyInfo> fields)
            {
                values.Add(EventValues.ToNet(arg, refOf));
                continue;
            }

            foreach (PropertyInfo field in fields)
            {
                values.Add(arg == null ? NetValue.Null : EventValues.ToNet(field.GetValue(arg), refOf));
            }
        }
    }

    /// <summary>
    /// Friend side: rebuilds the arguments from received values. False when the values don't fit this event (a different
    /// build); a field that can't be set is left at its default.
    /// </summary>
    public bool TryDecode(IReadOnlyList<NetValue> values, Func<NetValue, Type, object?> resolveRef, out object?[] args)
    {
        args = new object?[ArgTypes.Length];
        if (values.Count != ValueNames.Count)
        {
            return false;
        }

        int next = 0;
        for (int i = 0; i < ArgTypes.Length; i++)
        {
            Type type = ArgTypes[i];
            if (Fields[i] is IReadOnlyList<PropertyInfo> fields)
            {
                object? instance = EventValues.NewInstance(type);
                foreach (PropertyInfo field in fields)
                {
                    NetValue value = values[next++];
                    if (instance != null && value.Kind != NetValueKind.Null)
                    {
                        field.SetValue(instance, EventValues.FromNet(value, field.PropertyType, resolveRef));
                    }
                }

                args[i] = instance;
            }
            else
            {
                NetValue value = values[next++];
                args[i] = type == typeof(Il2CppSystem.Action) ? EventValues.NoOpAction() : EventValues.FromNet(value, type, resolveRef);
            }
        }

        return true;
    }

    /// <summary>
    /// Friend side, after <see cref="TryDecode"/>: for each argument sent whole (not flattened into fields), whether the host
    /// sent it as an object and whether it was found here (<see cref="Core.Stream.MissingObjects.SkipsEvent"/>).
    /// </summary>
    public (bool SentAsObject, bool FoundHere)[] WholeArguments(IReadOnlyList<NetValue> values, object?[] args)
    {
        var whole = new List<(bool SentAsObject, bool FoundHere)>(ArgTypes.Length);
        int next = 0;
        for (int i = 0; i < ArgTypes.Length && next < values.Count; i++)
        {
            if (Fields[i] is IReadOnlyList<PropertyInfo> fields)
            {
                next += fields.Count;
                continue;
            }

            object? arg = i < args.Length ? args[i] : null;
            bool found = arg is UnityEngine.Object unityObject ? unityObject != null : arg != null;
            whole.Add((values[next++].Kind == NetValueKind.Reference, found));
        }

        return whole.ToArray();
    }

    /// <summary>Calls one listener directly with <paramref name="args"/>, as the event's own safe invocation would.</summary>
    public void Call(Il2CppSystem.Delegate listener, object?[] args)
    {
        if (ActionType == null)
        {
            return;
        }

        _invoke ??= ActionType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(ActionType.Name, "Invoke");
        object typed = EventValues.CastTo(listener, ActionType)
            ?? throw new InvalidCastException($"a listener of {Name} isn't an {ActionType.Name}");
        _invoke.Invoke(typed, args);
    }

    private static List<string> NamesOf(Type[] argTypes, IReadOnlyList<PropertyInfo>?[] fieldsOf)
    {
        var names = new List<string>();
        for (int i = 0; i < argTypes.Length; i++)
        {
            if (fieldsOf[i] is IReadOnlyList<PropertyInfo> fields)
            {
                names.AddRange(fields.Select(f => $"arg{i}.{f.Name}"));
            }
            else
            {
                names.Add($"arg{i}");
            }
        }

        return names;
    }
}

/// <summary>
/// The game's event buses (<c>Events.GameplayEvents</c>, <c>RogueEvents</c>, <c>PowerupEvents</c>, <c>ShopEvents</c>), read
/// from the running build by reflection, as the game itself does in <c>GameplayEvents.UnregisterAllEvents</c>. Each event
/// is a static field holding a <c>SingleEvent</c> / <c>SingleEvent&lt;T..&gt;</c> (up to four arguments) whose listeners are
/// a plain list of delegates of the exact <c>Action&lt;T..&gt;</c> type.
/// <c>PlayNowMenuEvents</c> is local controller-assignment traffic and is left out.
/// </summary>
internal static class EventCatalog
{
    private static readonly string[] Buses = { "GameplayEvents", "RogueEvents", "PowerupEvents", "ShopEvents" };
    private static List<BusEvent>? _events;

    /// <summary>Every event, in a stable order (bus, then field name). Built once; events whose object is missing are skipped.</summary>
    public static IReadOnlyList<BusEvent> All => _events ??= Build();

    /// <summary>Events by name.</summary>
    public static Dictionary<string, BusEvent> ByName() => All.ToDictionary(e => e.Name, StringComparer.Ordinal);

    private static List<BusEvent> Build()
    {
        var events = new List<BusEvent>();
        int unsupported = 0;
        foreach (string bus in Buses)
        {
            Type? busType = Il2CppTypeLocator.Find($"Events.{bus}");
            if (busType == null)
            {
                ModLog.Warn($"Event catalog: bus {bus} not found.");
                continue;
            }

            foreach (PropertyInfo property in busType.GetProperties(BindingFlags.Public | BindingFlags.Static).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (!typeof(Il2CppObjectBase).IsAssignableFrom(property.PropertyType) || !IsSingleEvent(property.PropertyType))
                {
                    continue;
                }

                AbstractSingleEvent? @event;
                try
                {
                    @event = (property.GetValue(null) as Il2CppObjectBase)?.TryCast<AbstractSingleEvent>();
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"Event catalog: {bus}.{property.Name} couldn't be read: {ModLog.SafeErrorText(ex)}");
                    continue;
                }

                if (@event == null)
                {
                    continue;
                }

                Type[] args = property.PropertyType.IsGenericType ? property.PropertyType.GetGenericArguments() : Type.EmptyTypes;
                Type? action = ActionTypeFor(args);
                unsupported += action == null ? 1 : 0;
                events.Add(new BusEvent((ushort)events.Count, $"{bus}.{property.Name}", @event, args, action));
            }
        }

        ModLog.Info($"Event catalog: {events.Count} events on {Buses.Length} buses{(unsupported > 0 ? $" ({unsupported} with argument types the mod can't listen to)" : string.Empty)}.");
        return events;
    }

    private static bool IsSingleEvent(Type type)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            if (current == typeof(AbstractSingleEvent))
            {
                return true;
            }
        }

        return false;
    }

    private static Type? ActionTypeFor(Type[] args) => args.Length switch
    {
        0 => typeof(Il2CppSystem.Action),
        1 => typeof(Il2CppSystem.Action<>).MakeGenericType(args),
        2 => typeof(Il2CppSystem.Action<,>).MakeGenericType(args),
        3 => typeof(Il2CppSystem.Action<,,>).MakeGenericType(args),
        4 => typeof(Il2CppSystem.Action<,,,>).MakeGenericType(args),
        _ => null,
    };
}

/// <summary>Builds IL2CPP listener delegates of an event's exact type from a managed callback.</summary>
internal static class ListenerFactory
{
    // Reflection is looked up once per delegate class: client mode makes a stand-in for every detached listener (about
    // 400) when playback starts.
    private static readonly Dictionary<Type, MethodInfo> Converters = new();
    private static readonly Dictionary<string, MethodInfo> Makers = new(StringComparer.Ordinal);
    private static MethodInfo? _convertDefinition;

    /// <summary>An IL2CPP delegate of <paramref name="actionType"/> that calls <paramref name="callback"/> with the arguments.</summary>
    public static Il2CppSystem.Delegate Create(Type actionType, Type[] argTypes, Action<object?[]> callback)
    {
        if (StructListeners.Needed(argTypes))
        {
            return StructListeners.Create(actionType, argTypes, callback);
        }

        Delegate managed = argTypes.Length switch
        {
            0 => new Action(() => callback(Array.Empty<object?>())),
            1 => Make(nameof(Make1), argTypes, callback),
            2 => Make(nameof(Make2), argTypes, callback),
            3 => Make(nameof(Make3), argTypes, callback),
            4 => Make(nameof(Make4), argTypes, callback),
            _ => throw new NotSupportedException($"{argTypes.Length} arguments"),
        };

        if (!Converters.TryGetValue(actionType, out MethodInfo? convert))
        {
            _convertDefinition ??= typeof(DelegateSupport).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(DelegateSupport.ConvertDelegate) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            convert = _convertDefinition.MakeGenericMethod(actionType);
            Converters[actionType] = convert;
        }

        object converted = convert.Invoke(null, new object[] { managed })
            ?? throw new InvalidOperationException($"DelegateSupport couldn't convert to {actionType.Name}");
        return ((Il2CppObjectBase)converted).Cast<Il2CppSystem.Delegate>();
    }

    /// <summary>
    /// A listener from <see cref="Create"/> has been taken off its event: a hand-built one (a struct argument) gives up its
    /// callback (<see cref="StructListeners.Release"/>); a converted one is the interop's and needs nothing. Never throws.
    /// </summary>
    public static void Release(Il2CppSystem.Delegate listener) => StructListeners.Release(listener);

    private static Delegate Make(string factory, Type[] argTypes, Action<object?[]> callback)
    {
        string key = factory + ":" + string.Join(",", argTypes.Select(t => t.AssemblyQualifiedName));
        if (!Makers.TryGetValue(key, out MethodInfo? maker))
        {
            maker = typeof(ListenerFactory).GetMethod(factory, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(argTypes);
            Makers[key] = maker;
        }

        return (Delegate)maker.Invoke(null, new object[] { callback })!;
    }

    private static Action<T1> Make1<T1>(Action<object?[]> callback) => a => callback(new object?[] { a });

    private static Action<T1, T2> Make2<T1, T2>(Action<object?[]> callback) => (a, b) => callback(new object?[] { a, b });

    private static Action<T1, T2, T3> Make3<T1, T2, T3>(Action<object?[]> callback) => (a, b, c) => callback(new object?[] { a, b, c });

    private static Action<T1, T2, T3, T4> Make4<T1, T2, T3, T4>(Action<object?[]> callback) => (a, b, c, d) => callback(new object?[] { a, b, c, d });
}
#endif
