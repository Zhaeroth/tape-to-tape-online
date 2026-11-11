#if HAS_INTEROP
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using T2TOnline.Core.Serialization;
using T2TOnline.Core.Stream;
using UnityEngine;

namespace T2TOnline.Plugin.Streaming;

/// <summary>
/// Event argument values made of several parts, packed into one <see cref="NetValue"/> blob (a list of values):
/// <list type="bullet">
/// <item>game lists (<c>Il2CppSystem.Collections.Generic.List&lt;T&gt;</c>), such as a shot's power-ups and relics. A
/// listener may walk the list, so the friend's PC always gets one, empty when its items can't be sent;</item>
/// <item>Unity's <c>Collision</c> (puck collisions, hits on the post): rebuilt on the friend's PC around the local colliders
/// and bodies, with the host's relative velocity, impulse and contact points.</item>
/// </list>
/// </summary>
internal static class CompoundValues
{
    /// <summary>Most items sent per list.</summary>
    public const int MaxItems = 32;

    /// <summary>Most contact points sent per collision.</summary>
    public const int MaxContacts = 4;

    private static readonly ConcurrentDictionary<Type, ListAccess> Lists = new();
    private static readonly List<CollisionRebuild> RebuildList = new();
    private static readonly List<NetValue> MissingList = new();

    /// <summary>
    /// True while <see cref="CollisionFromNet"/> looks up a collision's colliders and bodies: those lookups run at once,
    /// outside the lookup budget (<see cref="RefCache"/>): <c>CollisionVFX.Instantiate</c> needs the hit collider in
    /// the same call. Main thread only (event delivery).
    /// </summary>
    public static bool ResolvingCollision { get; private set; }

    /// <summary>
    /// How complete each collision <see cref="CollisionFromNet"/> rebuilt since <see cref="ClearRebuilds"/> is (the event
    /// channel reads it for each delivery: a collision missing a collider skips the listener that needs it).
    /// </summary>
    public static IReadOnlyList<CollisionRebuild> Rebuilds => RebuildList;

    /// <summary>The members the host's collisions had that weren't found here, since <see cref="ClearRebuilds"/> (for the log).</summary>
    public static IReadOnlyList<NetValue> MissingMembers => MissingList;

    /// <summary>Forgets the collisions rebuilt so far (before decoding the next event).</summary>
    public static void ClearRebuilds()
    {
        RebuildList.Clear();
        MissingList.Clear();
    }

    /// <summary>True for a game list type.</summary>
    public static bool IsList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.List<>);

    /// <summary>Packs a game list: each item as a value (null where an item can't be sent).</summary>
    public static NetValue ListToNet(Il2CppObjectBase list, Func<UnityEngine.Object, NetValue> refOf)
    {
        ListAccess access = Lists.GetOrAdd(list.GetType(), ListAccess.For);
        int count = Math.Min(access.Count(list), MaxItems);
        var items = new List<NetValue>(count);
        for (int i = 0; i < count; i++)
        {
            items.Add(EventValues.ToNet(access.Get(list, i), refOf));
        }

        return Pack(items);
    }

    /// <summary>A new game list of <paramref name="type"/> holding the items that resolve here (never null).</summary>
    public static object ListFromNet(NetValue value, Type type, Func<NetValue, Type, object?> resolveRef)
    {
        ListAccess access = Lists.GetOrAdd(type, ListAccess.For);
        object list = access.Create();
        var items = new List<NetValue>();
        if (Unpack(value, items, MaxItems))
        {
            foreach (NetValue item in items)
            {
                object? local = EventValues.FromNet(item, access.ItemType, resolveRef);
                if (local != null)
                {
                    access.Add(list, local);
                }
            }
        }

        return list;
    }

    /// <summary>Packs references to objects (a list of the game's objects that isn't itself a game list).</summary>
    public static NetValue RefsToNet(IEnumerable<UnityEngine.Object> objects, Func<UnityEngine.Object, NetValue> refOf)
    {
        var items = new List<NetValue>();
        foreach (UnityEngine.Object found in objects)
        {
            if (items.Count < MaxItems)
            {
                items.Add(RefOrNull(found, refOf));
            }
        }

        return Pack(items);
    }

    /// <summary>The objects a <see cref="RefsToNet"/> value names that exist here, as <typeparamref name="T"/>.</summary>
    public static List<T> RefsFromNet<T>(NetValue value, Func<NetValue, Type, object?> resolveRef)
        where T : class
    {
        var found = new List<T>();
        var items = new List<NetValue>();
        if (Unpack(value, items, MaxItems))
        {
            foreach (NetValue item in items)
            {
                if (resolveRef(item, typeof(T)) is T local)
                {
                    found.Add(local);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Packs a collision as the game sees it: the flag that says which side is "this", the raw relative velocity and
    /// impulse, the four objects involved (body, other body, collider, other collider) and up to four contact points.
    /// </summary>
    public static NetValue CollisionToNet(Collision collision, Func<UnityEngine.Object, NetValue> refOf)
    {
        ContactPairHeader header = collision.m_Header;
        ContactPair pair = collision.m_Pair;
        var items = new List<NetValue>
        {
            NetValue.FromBool(collision.m_Flipped),
            Vector(header.m_RelativeVelocity),
            Vector(pair.m_ImpulseSum),
            RefOrNull(header.body, refOf),
            RefOrNull(header.otherBody, refOf),
            RefOrNull(pair.collider, refOf),
            RefOrNull(pair.otherCollider, refOf),
        };

        int contacts = Math.Min(collision.contactCount, MaxContacts);
        items.Add(NetValue.FromInt(contacts));
        for (int i = 0; i < contacts; i++)
        {
            ContactPoint contact = collision.GetContact(i);
            items.Add(Vector(contact.m_Point));
            items.Add(Vector(contact.m_Normal));
            items.Add(Vector(contact.m_Impulse));
            items.Add(NetValue.FromFloat(contact.m_Separation));
            items.Add(NetValue.FromBool(contact.m_ThisColliderInstanceID == pair.m_ColliderID));
        }

        return Pack(items);
    }

    /// <summary>
    /// Rebuilds a collision on this PC: the same layout Unity builds from the physics engine's contact data, with the
    /// local objects' instance ids and the contact points held in the collision itself, so nothing reads physics memory
    /// (there is none behind it: a read there is a native crash no try/catch can stop). Null when the value isn't a
    /// collision.
    /// </summary>
    public static Collision? CollisionFromNet(NetValue value, Func<NetValue, Type, object?> resolveRef)
    {
        var items = new List<NetValue>();
        if (!Unpack(value, items, 8 + (MaxContacts * 5)) || items.Count < 8)
        {
            return null;
        }

        bool flipped = items[0].WholeValue != 0;
        int body, otherBody, collider, otherCollider;
        ResolvingCollision = true;
        try
        {
            body = InstanceId(resolveRef(items[3], typeof(Rigidbody)));
            otherBody = InstanceId(resolveRef(items[4], typeof(Rigidbody)));
            collider = InstanceId(resolveRef(items[5], typeof(Collider)));
            otherCollider = InstanceId(resolveRef(items[6], typeof(Collider)));
        }
        finally
        {
            ResolvingCollision = false;
        }

        NoteRebuild(items, body, otherBody, collider, otherCollider);
        int contacts = (int)Math.Clamp(items[7].WholeValue, 0, MaxContacts);
        if (items.Count < 8 + (contacts * 5))
        {
            return null;
        }

        // Unity's own structs (read-only fields): written byte by byte at the fields' offsets in this build (m_BodyID 0,
        // m_OtherBodyID 4, m_NbPairs 16, m_RelativeVelocity 24; m_ColliderID 0, m_OtherColliderID 4, m_NbPoints 16,
        // m_ImpulseSum 24), which the interop structs declare the same; hooks.txt lists each field.
        Span<byte> headerBytes = stackalloc byte[Unsafe.SizeOf<ContactPairHeader>()];
        headerBytes.Clear();
        MemoryMarshal.Write(headerBytes[0..], ref body);
        MemoryMarshal.Write(headerBytes[4..], ref otherBody);
        uint pairs = 1;
        MemoryMarshal.Write(headerBytes[16..], ref pairs);
        Vector3 relative = ToVector(items[1]);
        MemoryMarshal.Write(headerBytes[24..], ref relative);
        ContactPairHeader header = MemoryMarshal.Read<ContactPairHeader>(headerBytes);

        Span<byte> pairBytes = stackalloc byte[Unsafe.SizeOf<ContactPair>()];
        pairBytes.Clear();
        MemoryMarshal.Write(pairBytes[0..], ref collider);
        MemoryMarshal.Write(pairBytes[4..], ref otherCollider);
        uint points = (uint)contacts;
        MemoryMarshal.Write(pairBytes[16..], ref points);
        Vector3 impulse = ToVector(items[2]);
        MemoryMarshal.Write(pairBytes[24..], ref impulse);
        ContactPair contactPair = MemoryMarshal.Read<ContactPair>(pairBytes);

        var array = new Il2CppStructArray<ContactPoint>(contacts);
        for (int i = 0; i < contacts; i++)
        {
            int at = 8 + (i * 5);
            bool thisIsCollider = items[at + 4].WholeValue != 0;
            array[i] = new ContactPoint
            {
                m_Point = ToVector(items[at]),
                m_Normal = ToVector(items[at + 1]),
                m_Impulse = ToVector(items[at + 2]),
                m_Separation = (float)items[at + 3].Number,
                m_ThisColliderInstanceID = thisIsCollider ? collider : otherCollider,
                m_OtherColliderInstanceID = thisIsCollider ? otherCollider : collider,
            };
        }

        // Never Unity's (header, pair, flipped) constructor: it copies the contacts out of the physics engine's native buffer
        // straight away (ContactPair.GetContactPoint_Internal on m_StartPtr), which this collision doesn't have; that read
        // crashed the game (v0.3.1). The plain constructor only zeroes the fields. With m_LegacyContacts set, every member
        // of Collision the game's code can reach (contactCount, GetContact, contacts) reads the held contacts; GetContacts
        // isn't in this build.
        return new Collision
        {
            m_Header = header,
            m_Pair = contactPair,
            m_Flipped = flipped,
            m_LegacyContacts = array,
        };
    }

    /// <summary>How complete the rebuilt collision is (members the host sent as objects, found here or not), noted for the delivery.</summary>
    private static void NoteRebuild(List<NetValue> items, int body, int otherBody, int collider, int otherCollider)
    {
        Span<bool> sent = stackalloc bool[4];
        Span<bool> found = stackalloc bool[4];
        Span<int> ids = stackalloc int[] { body, otherBody, collider, otherCollider };
        for (int i = 0; i < 4; i++)
        {
            sent[i] = items[3 + i].Kind != NetValueKind.Null;
            found[i] = ids[i] != 0;
            if (sent[i] && !found[i] && MissingList.Count < 8)
            {
                MissingList.Add(items[3 + i]);
            }
        }

        if (RebuildList.Count < 16)
        {
            RebuildList.Add(CollisionCheck.Of(sent, found));
        }
    }

    private static NetValue Pack(List<NetValue> items)
    {
        var writer = new BitWriter(128);
        NetValue.WriteList(writer, items);
        return writer.ByteLength <= NetValue.MaxBytes ? NetValue.FromBytes(writer.AsSpan()) : NetValue.Null;
    }

    private static bool Unpack(NetValue value, List<NetValue> items, int max)
    {
        if (value.Kind != NetValueKind.Blob || value.Data == null)
        {
            return false;
        }

        var reader = new BitReader(value.Data);
        return NetValue.TryReadList(ref reader, items, max);
    }

    private static NetValue Vector(Vector3 v) => NetValue.FromVector3(v.x, v.y, v.z);

    private static Vector3 ToVector(NetValue value) => value.Kind == NetValueKind.Vector3 ? new Vector3(value.X, value.Y, value.Z) : Vector3.zero;

    private static NetValue RefOrNull(UnityEngine.Object? value, Func<UnityEngine.Object, NetValue> refOf) =>
        value == null ? NetValue.Null : refOf(value);

    private static int InstanceId(object? value) => value is UnityEngine.Object found && found != null ? found.GetInstanceID() : 0;

    /// <summary>Reflection access to one interop list type (Count, the indexer, Add, the constructor).</summary>
    private sealed class ListAccess
    {
        private readonly PropertyInfo _count;
        private readonly MethodInfo _get;
        private readonly MethodInfo _add;
        private readonly ConstructorInfo _create;

        private ListAccess(Type type)
        {
            ItemType = type.GetGenericArguments()[0];
            _count = type.GetProperty("Count") ?? throw new MissingMemberException(type.Name, "Count");
            _get = type.GetMethod("get_Item", new[] { typeof(int) }) ?? throw new MissingMethodException(type.Name, "get_Item");
            _add = type.GetMethod("Add", new[] { ItemType }) ?? throw new MissingMethodException(type.Name, "Add");
            _create = type.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException(type.Name, ".ctor");
        }

        public Type ItemType { get; }

        public static ListAccess For(Type type) => new(type);

        public int Count(object list) => (int)_count.GetValue(list)!;

        public object? Get(object list, int index) => _get.Invoke(list, new object[] { index });

        public void Add(object list, object item) => _add.Invoke(list, new[] { item });

        public object Create() => _create.Invoke(null);
    }
}
#endif
