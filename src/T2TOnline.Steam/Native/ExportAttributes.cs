using System;

namespace T2TOnline.Steam.Native;

/// <summary>
/// Marks a <see cref="SteamNative"/> binding whose export may be missing without turning the other bindings off: the
/// probe in <see cref="SteamRuntime"/> reports it, and the calls that need it return their failure value without
/// touching native code. Every unmarked binding is required.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class OptionalExportAttribute : Attribute
{
}

/// <summary>
/// Marks one version of a Steam interface accessor (<c>SteamAPI_SteamUtils_v010</c>, <c>SteamAPI_SteamUtils_v011</c>):
/// versions whose flat methods the bindings call with the same signatures. The probe needs at least one version of each
/// interface, and <see cref="SteamInterfaces"/> uses the newest one the DLL exports (a DLL's flat methods are built
/// against its own interface version).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class InterfaceAccessorAttribute : Attribute
{
    public InterfaceAccessorAttribute(string interfaceName) => InterfaceName = interfaceName;

    /// <summary>The interface every version of this accessor returns, such as <c>ISteamUtils</c>.</summary>
    public string InterfaceName { get; }
}
