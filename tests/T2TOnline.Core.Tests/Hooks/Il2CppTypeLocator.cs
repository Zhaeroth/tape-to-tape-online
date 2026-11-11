using System;

namespace T2TOnline.Plugin.Hooks;

/// <summary>
/// Stands in for the plugin's type finder, which needs MelonLoader and the game's interop assemblies: <c>HookManifest.cs</c> is linked
/// into this project for its text parser only (<c>HookListsTests</c>), and nothing here looks a type up.
/// </summary>
internal static class Il2CppTypeLocator
{
    public static Type? Find(string gameTypeName) => null;
}
