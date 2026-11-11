using System;
using System.Runtime.InteropServices;
using System.Text;

namespace T2TOnline.Plugin.Native;

/// <summary>
/// The IL2CPP runtime's exported C API, called directly. Il2CppInterop wraps all of this once MelonLoader has set it
/// up, which happens only on the first scene change; this is for the code that must run before that.
/// </summary>
internal static class Il2CppApi
{
    /// <summary><c>IL2CPP_TYPE_VOID</c> (il2cpp-blob.h).</summary>
    public const int TypeVoid = 0x01;

    /// <summary><c>METHOD_ATTRIBUTE_STATIC</c> (il2cpp-tabledefs.h).</summary>
    public const uint MethodAttributeStatic = 0x0010;

    /// <summary>
    /// Offset of <c>MethodInfo.name</c>. From metadata v29 on (Unity 2021.2+; this game is v31) a MethodInfo starts
    /// with methodPointer, virtualMethodPointer, invoker_method, name.
    /// </summary>
    public static readonly int MethodInfoNameOffset = 3 * IntPtr.Size;

    private const string GameAssembly = "GameAssembly";

    /// <summary>The root application domain.</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_domain_get", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint DomainGet();

    /// <summary>Every loaded assembly: returns an array of <paramref name="count"/> Il2CppAssembly pointers.</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_domain_get_assemblies", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint DomainGetAssemblies(nint domain, out nuint count);

    /// <summary>The image (module) of an assembly.</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_assembly_get_image", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint AssemblyGetImage(nint assembly);

    /// <summary>An image's file name, such as "Assembly-CSharp.dll" (a UTF-8 C string).</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_image_get_name", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint ImageGetName(nint image);

    /// <summary>A class by namespace (empty for the global namespace) and name, or zero.</summary>
    public static nint ClassFromName(nint image, string namespaze, string name) =>
        ClassFromName(image, Utf8(namespaze), Utf8(name));

    /// <summary>A method of a class or its parents by name and parameter count, or zero.</summary>
    public static nint ClassGetMethodFromName(nint klass, string name, int argsCount) =>
        ClassGetMethodFromName(klass, Utf8(name), argsCount);

    /// <summary>A method's name (a UTF-8 C string owned by the runtime).</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_method_get_name", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint MethodGetName(nint method);

    /// <summary>A method's attribute flags; <paramref name="implementationFlags"/> receives its implementation flags.</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_method_get_flags", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint MethodGetFlags(nint method, out uint implementationFlags);

    /// <summary>A method's parameter count.</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_method_get_param_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint MethodGetParamCount(nint method);

    /// <summary>A method's return type (an Il2CppType pointer).</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_method_get_return_type", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint MethodGetReturnType(nint method);

    /// <summary>The element type code of an Il2CppType, such as <see cref="TypeVoid"/>.</summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_type_get_type", CallingConvention = CallingConvention.Cdecl)]
    public static extern int TypeGetType(nint type);

    /// <summary>
    /// The engine's native function behind an internal call, such as "UnityEngine.Analytics.Analytics::IsInitialized", or
    /// zero when the engine registered none by that name.
    /// </summary>
    public static nint ResolveIcall(string name) => ResolveIcall(Utf8(name));

    /// <summary>
    /// Calls a method through the runtime (<c>il2cpp_runtime_invoke</c>): <paramref name="args"/> holds one pointer per
    /// parameter (an object's pointer for a reference type, a pointer to the value for a value type, a <c>Nullable</c>'s
    /// included). Returns the result object (a value type's boxed), or zero; <paramref name="exception"/> receives the
    /// managed exception the method threw, or zero.
    /// </summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_runtime_invoke", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint RuntimeInvoke(nint method, nint obj, nint[] args, out nint exception);

    /// <summary>
    /// True when the runtime's garbage collector is incremental: its write barriers then mark each written field's page (the flag
    /// <c>il2cpp_gc_wbarrier_set_field</c> and the game's own inlined barriers read).
    /// </summary>
    [DllImport(GameAssembly, EntryPoint = "il2cpp_gc_is_incremental", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GcIsIncremental();

    // The runtime takes UTF-8 C strings; they're passed as null-terminated byte arrays.
    [DllImport(GameAssembly, EntryPoint = "il2cpp_class_from_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ClassFromName(nint image, byte[] namespaze, byte[] name);

    [DllImport(GameAssembly, EntryPoint = "il2cpp_resolve_icall", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ResolveIcall(byte[] name);

    [DllImport(GameAssembly, EntryPoint = "il2cpp_class_get_method_from_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ClassGetMethodFromName(nint klass, byte[] name, int argsCount);

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
}
