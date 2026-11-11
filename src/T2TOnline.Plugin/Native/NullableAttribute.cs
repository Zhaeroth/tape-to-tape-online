// MelonLoader's interop assembly UnityEngine.CoreModule.dll exposes a public System.Runtime.CompilerServices.NullableAttribute
// (a wrapper for the IL2CPP type of that name). Once the interop assemblies are referenced it hides the attribute the
// compiler would embed, and it lacks the constructors the compiler needs (error CS0656). A definition in this assembly
// takes precedence over referenced ones, so the compiler uses this one. Same shape as the compiler's own.
namespace System.Runtime.CompilerServices;

/// <summary>Nullability annotations the compiler writes into metadata.</summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Event | AttributeTargets.Field | AttributeTargets.GenericParameter |
    AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue,
    AllowMultiple = false,
    Inherited = false)]
internal sealed class NullableAttribute : Attribute
{
    /// <summary>One flag for the whole type.</summary>
    public NullableAttribute(byte flag) => NullableFlags = new[] { flag };

    /// <summary>One flag per type component.</summary>
    public NullableAttribute(byte[] flags) => NullableFlags = flags;

    /// <summary>The flags: 0 oblivious, 1 not annotated, 2 annotated.</summary>
    public byte[] NullableFlags { get; }
}
