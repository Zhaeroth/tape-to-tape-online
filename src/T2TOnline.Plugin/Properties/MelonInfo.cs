using MelonLoader;
using T2TOnline.Plugin;

// The version is the build's (Directory.Build.props, written as BuildVersion.Release by the project), never typed here.
[assembly: MelonInfo(typeof(T2TOnlinePlugin), "T2T Online", BuildVersion.Release, "Zhaeroth")]
[assembly: MelonGame("Excellent Rectangle", "Tape to Tape")]
// No Harmony attribute is used (every patch is made by hand, from the hook lists), so MelonLoader's PatchAll, which scans every
// type here before its generator runs, has nothing to do. D108's fix is the constructor's (Startup/StaleInterop).
[assembly: HarmonyDontPatchAll]
[assembly: MelonPlatform(MelonPlatformAttribute.CompatiblePlatforms.WINDOWS_X64)]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
// MelonOptionalDependencies, naming every interop assembly, is written by the build from the project's interop references
// (T2TOnline.Plugin.csproj, WriteOptionalDependencies): no "missing dependencies" warning on a first launch or a regeneration.
