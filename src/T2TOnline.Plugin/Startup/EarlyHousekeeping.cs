using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using T2TOnline.Plugin.Native;

namespace T2TOnline.Plugin.Startup;

/// <summary>
/// Stops crash reporting and analytics from starting at all.
/// </summary>
/// <remarks>
/// Both start inside Unity's own start-up callbacks ([RuntimeInitializeOnLoadMethod]): Sentry at SubsystemRegistration,
/// its native crash handler and Unity Analytics at BeforeSceneLoad. MelonLoader sets up Il2CppInterop and Harmony only
/// on the first scene change, after all of those have run, so Harmony patches can't stop them. Instead, while plugins
/// pre-initialize (IL2CPP is up, but Unity hasn't called any game code yet), each start-up method's runtime entry
/// (its MethodInfo) is pointed at a function that does nothing. Unity calls start-up methods through the runtime's
/// invoke path, which reads that entry at call time. The method's machine code is left alone, so methods that share
/// it through IL2CPP folding are unaffected.
/// <para>
/// The UGS analytics callback (<c>Ua2CoreInitializeCallback.Register</c>, BeforeSceneLoad) gets a stand-in of its own that
/// first switches Unity's crash reporter off again (<see cref="CrashReporting"/>: the engine's session start, just before
/// these callbacks, turns it back on), then the engine's own analytics' initialize-on-startup (<see cref="EarlyAnalytics"/>):
/// that callback runs at the moment Unity documents for it, before the first scene and the game's first exception.
/// </para>
/// </remarks>
internal static class EarlyHousekeeping
{
    private static readonly StartupMethod[] Targets =
    {
        new("io.sentry.unity.runtime", "Sentry.Unity", "SentryInitialization", "Init", "Sentry crash reporting"),
        new("Sentry.Unity.Native", "Sentry.Unity.Native", "SentryNative", "ReinstallBackend", "Sentry native crash handler"),
        new("Unity.Services.Analytics", string.Empty, "Ua2CoreInitializeCallback", "Register", "Unity Analytics", HoldsEngineAnalytics: true),
    };

    private static readonly StaticVoidMethod SkipDelegate = SkipStaticVoid;
    private static readonly StaticVoidMethod SkipAndHoldDelegate = SkipAndHoldEngineAnalytics;

    /// <summary>Blocks every target. Returns the names of the ones that could not be blocked.</summary>
    public static IReadOnlyList<string> Apply()
    {
        var failed = new List<string>();
        Dictionary<string, nint> images;
        nint gameAssemblyBase;
        try
        {
            images = LoadedImages();
            gameAssemblyBase = NativeLibrary.TryLoad("GameAssembly", out nint handle) ? handle : 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            EarlyLog.Error("Housekeeping: the IL2CPP runtime API is unavailable; nothing could be blocked early", ex);
            foreach (StartupMethod startupMethod in Targets)
            {
                failed.Add(startupMethod.ToString());
            }

            return failed;
        }

        nint skip = SkipPointer(SkipDelegate);
        nint skipAndHold = SkipPointer(SkipAndHoldDelegate);
        foreach (StartupMethod startupMethod in Targets)
        {
            string? problem;
            try
            {
                problem = Block(startupMethod, images, startupMethod.HoldsEngineAnalytics ? skipAndHold : skip, gameAssemblyBase);
            }
            catch (Exception ex)
            {
                problem = EarlyLog.ErrorText(ex);
            }

            if (problem != null)
            {
                failed.Add(startupMethod.ToString());
                EarlyLog.Warn($"Housekeeping: {startupMethod.Purpose} not blocked ({startupMethod}): {problem}.");
            }
        }

        return failed;
    }

    private static string? Block(StartupMethod startupMethod, Dictionary<string, nint> images, nint skip, nint gameAssemblyBase)
    {
        if (!images.TryGetValue(startupMethod.Assembly + ".dll", out nint image))
        {
            return $"assembly {startupMethod.Assembly} is not loaded";
        }

        nint klass = Il2CppApi.ClassFromName(image, startupMethod.Namespace, startupMethod.Class);
        if (klass == 0)
        {
            return "class not found";
        }

        nint method = Il2CppApi.ClassGetMethodFromName(klass, startupMethod.Method, 0);
        if (method == 0)
        {
            return "method not found";
        }

        // Only a static, parameterless void method may be pointed at the do-nothing function: that's the signature
        // the runtime will call it with.
        bool isStatic = (Il2CppApi.MethodGetFlags(method, out _) & Il2CppApi.MethodAttributeStatic) != 0;
        if (!isStatic || Il2CppApi.MethodGetParamCount(method) != 0 || Il2CppApi.TypeGetType(Il2CppApi.MethodGetReturnType(method)) != Il2CppApi.TypeVoid)
        {
            return "not a static parameterless void method";
        }

        if (Marshal.ReadIntPtr(method, Il2CppApi.MethodInfoNameOffset) != Il2CppApi.MethodGetName(method))
        {
            return "unexpected MethodInfo layout";
        }

        if (!MemoryAccess.IsWritable(method, 2 * IntPtr.Size))
        {
            return "its MethodInfo is not writable";
        }

        nint original = Marshal.ReadIntPtr(method, 0);
        if (original == 0)
        {
            return "it has no code pointer";
        }

        Marshal.WriteIntPtr(method, 0, skip);
        Marshal.WriteIntPtr(method, IntPtr.Size, skip);
        if (Marshal.ReadIntPtr(method, 0) != skip || Marshal.ReadIntPtr(method, IntPtr.Size) != skip)
        {
            return "the write did not stick";
        }

        string where = gameAssemblyBase != 0 && original > gameAssemblyBase
            ? $"GameAssembly+0x{(long)(original - gameAssemblyBase):X}"
            : $"0x{(long)original:X}";
        EarlyLog.Info($"Housekeeping: {startupMethod.Purpose} blocked before start-up ({startupMethod}, code at {where})" +
            (startupMethod.HoldsEngineAnalytics ? "; when Unity calls it, Unity's crash reporting is switched off again and the engine's own analytics is held from starting too." : "."));
        return null;
    }

    /// <summary>Every loaded image by file name, such as "io.sentry.unity.runtime.dll".</summary>
    private static Dictionary<string, nint> LoadedImages()
    {
        var images = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        nint assemblies = Il2CppApi.DomainGetAssemblies(Il2CppApi.DomainGet(), out nuint count);
        for (nuint i = 0; i < count; i++)
        {
            nint image = Il2CppApi.AssemblyGetImage(Marshal.ReadIntPtr(assemblies, (int)i * IntPtr.Size));
            string? name = image == 0 ? null : Marshal.PtrToStringUTF8(Il2CppApi.ImageGetName(image));
            if (name != null)
            {
                images.TryAdd(name, image);
            }
        }

        return images;
    }

    // A delegate rather than [UnmanagedCallersOnly]: UnityEngine.CoreModule's interop assembly also defines that
    // attribute, which makes it ambiguous. The runtime calls the pointer for the rest of the session, so the delegate
    // lives in a static field and is never collected.
    private static nint SkipPointer(StaticVoidMethod stub) => Marshal.GetFunctionPointerForDelegate(stub);

    /// <summary>
    /// Stands in for a static parameterless void method. IL2CPP calls such a method with one argument, its own
    /// MethodInfo.
    /// </summary>
    private static void SkipStaticVoid(nint methodInfo)
    {
    }

    /// <summary>
    /// Stands in for <c>Ua2CoreInitializeCallback.Register</c> (Unity calls it at BeforeSceneLoad): skips it like
    /// <see cref="SkipStaticVoid"/>, switches Unity's crash reporter off again first (the engine's session start, just
    /// before these callbacks, turned it back on; <see cref="CrashReporting"/>), then holds the engine's own analytics from
    /// starting (<see cref="EarlyAnalytics"/>). Both never throw, and each has its own guard here too: nothing may get out
    /// into the engine.
    /// </summary>
    private static void SkipAndHoldEngineAnalytics(nint methodInfo)
    {
        try
        {
            CrashReporting.SwitchOffAtStartupCallbacks();
        }
        catch (Exception ex)
        {
            EarlyLog.Warn($"Housekeeping: Unity's crash reporting couldn't be switched off at Unity's start-up callbacks ({ex.GetType().Name}).");
        }

        try
        {
            EarlyAnalytics.HoldStartup();
        }
        catch (Exception ex)
        {
            EarlyLog.Warn($"Housekeeping: Unity Analytics' initialize-on-startup couldn't be held at start-up ({ex.GetType().Name}).");
        }
    }

    private delegate void StaticVoidMethod(nint methodInfo);

    /// <summary>A start-up method to block. <paramref name="HoldsEngineAnalytics"/>: its stand-in also holds the engine's own analytics.</summary>
    private sealed record StartupMethod(string Assembly, string Namespace, string Class, string Method, string Purpose, bool HoldsEngineAnalytics = false)
    {
        public override string ToString() =>
            Namespace.Length == 0 ? $"{Class}::{Method}" : $"{Namespace}.{Class}::{Method}";
    }
}
