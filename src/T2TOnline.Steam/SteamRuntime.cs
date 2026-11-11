using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>
/// Whether Steam can be called, and why not. Safe anywhere: in tests and other hosts without steam_api64 everything
/// here reports "unavailable" and never throws.
/// <para>
/// The first use probes once: load steam_api64 (by name, which resolves to the copy the game already loaded, or from
/// <see cref="SetLibraryPath"/>), then look up every <c>EntryPoint</c> the bindings declare. A missing required export
/// disables all bindings, so a native call can never fail half-way through a feature. A missing optional export
/// (<see cref="OptionalExports"/>: fallbacks and calls an online session can do without) only turns off the calls that
/// need it, which then return their failure value; <see cref="Status"/> names it. Interfaces whose current SDK version
/// only differs in calls we don't make are accepted in either version. If the library can't be loaded yet the probe is
/// retried at most every two seconds. Any exception from a native call afterwards (which the probe should make
/// impossible) also disables the bindings, and is kept in <see cref="Status"/>.
/// </para>
/// Everything is main-thread only, like the rest of this library.
/// </summary>
public static class SteamRuntime
{
    private const long RetryIntervalMs = 2000;

    private static ProbeState _state;
    private static long _nextRetryMs;
    private static string? _libraryPath;
    private static nint _library;
    private static bool _resolverInstalled;
    private static string _status = "not probed yet";
    private static string? _failure;
    private static IReadOnlyList<string> _missingExports = Array.Empty<string>();
    private static HashSet<string> _absentMethods = new(StringComparer.Ordinal);
    private static Func<string, bool>? _exportLookup;
    private static Binding[]? _bindings;
    private static IReadOnlyList<string>? _boundExports;
    private static IReadOnlyList<string>? _optionalExports;

    private enum ProbeState
    {
        NotProbed,
        LibraryNotLoaded,
        MissingExports,
        Ready,
    }

    /// <summary>
    /// True when steam_api64 is loaded and exports every required function, and no native call has failed.
    /// Says nothing about whether Steam itself is initialised; see <see cref="IsAvailable"/>.
    /// </summary>
    public static bool BindingsReady
    {
        get
        {
            EnsureProbed();
            return _state == ProbeState.Ready && _failure is null;
        }
    }

    /// <summary>True when the bindings are ready and the game has initialised Steam (there is a local Steam user).</summary>
    public static bool IsAvailable => LocalSteamId != 0;

    /// <summary>
    /// How an exception is written into <see cref="Status"/>: the plugin sets its log's cleaned form (type and message, folders
    /// labelled, one line: D127); without it, the type and the message. Set once, before first use; must not throw.
    /// </summary>
    public static Func<Exception, string>? ErrorText { get; set; }

    /// <summary>
    /// One line for logs: what the probe found, or the first native failure. It names no folder: the library is "by path"
    /// (<see cref="SetLibraryPath"/>) or "by name", never its path.
    /// </summary>
    public static string Status
    {
        get
        {
            EnsureProbed();
            return _failure is null ? _status : $"disabled after a native call failed: {_failure}";
        }
    }

    /// <summary>
    /// Exports the bindings declare that the loaded steam_api64 lacks, sorted: required ones (any of which disables every
    /// binding), optional ones (which only turn off the calls that need them), and every version of an interface
    /// accessor when it has none. An accessor version that isn't needed because another one is there isn't listed.
    /// </summary>
    public static IReadOnlyList<string> MissingExports
    {
        get
        {
            EnsureProbed();
            return _missingExports;
        }
    }

    /// <summary>Every flat-API export the bindings declare (required, optional and each accepted accessor version), sorted.</summary>
    public static IReadOnlyList<string> BoundExports => _boundExports ??= Bindings.Select(b => b.Export).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The bound exports whose absence only turns off the calls that need them, sorted.</summary>
    public static IReadOnlyList<string> OptionalExports => _optionalExports ??= Bindings
        .GroupBy(b => b.Export, StringComparer.Ordinal)
        .Where(group => group.All(b => b.Optional))
        .Select(group => group.Key)
        .ToArray();

    private static Binding[] Bindings => _bindings ??= FindBindings();

    /// <summary>The local user's Steam ID, or 0 if Steam isn't available.</summary>
    public static ulong LocalSteamId
    {
        get
        {
            nint user = SteamInterfaces.User();
            if (user == 0)
            {
                return 0;
            }

            try
            {
                return SteamNative.GetSteamId(user);
            }
            catch (Exception e)
            {
                ReportFailure(e);
                return 0;
            }
        }
    }

    /// <summary>The running app's Steam app ID, or 0 if Steam isn't available.</summary>
    public static uint AppId
    {
        get
        {
            nint utils = SteamInterfaces.Utils();
            if (utils == 0)
            {
                return 0;
            }

            try
            {
                return SteamNative.GetAppId(utils);
            }
            catch (Exception e)
            {
                ReportFailure(e);
                return 0;
            }
        }
    }

    /// <summary>
    /// Points the probe at a specific steam_api64.dll (for example the game's
    /// <c>Tape to Tape_Data\Plugins\x86_64\steam_api64.dll</c>). Optional: loading by name finds the copy the game has
    /// already loaded. Takes effect at the next probe, so call it before first use. Ignores null, empty or relative
    /// paths.
    /// </summary>
    public static void SetLibraryPath(string? fullPath)
    {
        if (!string.IsNullOrEmpty(fullPath) && Path.IsPathFullyQualified(fullPath))
        {
            _libraryPath = fullPath;
            if (_state == ProbeState.LibraryNotLoaded)
            {
                _nextRetryMs = 0;
            }
        }
    }

    /// <summary>
    /// Self-check of the identity layout against the real DLL: builds a Steam ID identity with the native
    /// <c>SetSteamID64</c> and with <see cref="SteamNetworkingIdentity.FromSteamId"/> and compares all 136 bytes, and
    /// reads one back with the native <c>GetSteamID64</c>. These helpers don't need Steam to be initialised.
    /// </summary>
    public static bool VerifyIdentityLayout(out string detail)
    {
        if (!BindingsReady)
        {
            detail = Status;
            return false;
        }

        const ulong probeId = 76561197960265729UL; // Individual account 1 in the public universe.
        try
        {
            var native = SteamNetworkingIdentity.FromSteamId(0xDEADBEEF);
            SteamNative.IdentityClear(ref native);
            bool cleared = IdentityBytes(native).All(b => b == 0);
            SteamNative.IdentitySetSteamId64(ref native, probeId);
            var managed = SteamNetworkingIdentity.FromSteamId(probeId);
            ulong readBack = SteamNative.IdentityGetSteamId64(in managed);
            bool same = IdentityBytes(native).SequenceEqual(IdentityBytes(managed));
            bool ok = cleared && same && readBack == probeId;
            // The probe's ID is a fixed test account, but the log names only whether it came back as set.
            detail = $"identity layout {(ok ? "matches" : "MISMATCH")}: native Clear zeroed={cleared}, " +
                $"native type={(int)native.Type} size={native.ByteCount} id {(native.SteamId64 == probeId ? "as set" : "differs")}, bytes equal={same}, " +
                $"native GetSteamID64 of managed {(readBack == probeId ? "as set" : "differs")}";
            return ok;
        }
        catch (Exception e)
        {
            ReportFailure(e);
            detail = Status;
            return false;
        }
    }

    /// <summary>Records the first exception from a native call and disables the bindings.</summary>
    internal static void ReportFailure(Exception exception) =>
        _failure ??= Describe(exception);

    /// <summary>An exception for <see cref="Status"/> (and a failed session close), through <see cref="ErrorText"/> when it's set.</summary>
    internal static string Describe(Exception exception)
    {
        try
        {
            if (ErrorText is { } text)
            {
                return text(exception);
            }
        }
        catch (Exception)
        {
            // The plugin's cleaner failed: the type alone says enough.
            return exception.GetType().Name;
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    /// <summary>
    /// True when the bindings are ready and the probe found the export behind the <see cref="SteamNative"/> method named
    /// <paramref name="nativeMethod"/> (pass <c>nameof(SteamNative.X)</c>). Callers of an optional export check this before
    /// calling it.
    /// </summary>
    internal static bool HasExport(string nativeMethod) => BindingsReady && !_absentMethods.Contains(nativeMethod);

    /// <summary>
    /// Forgets the probe and any native failure, so the next use probes again (tests). With
    /// <paramref name="exportLookup"/> the probe asks it which exports exist instead of loading steam_api64, so the
    /// required and optional rules can be exercised without Steam; native calls still go to the real library, if any.
    /// </summary>
    internal static void Reset(Func<string, bool>? exportLookup = null)
    {
        _exportLookup = exportLookup;
        _state = ProbeState.NotProbed;
        _nextRetryMs = 0;
        _status = "not probed yet";
        _failure = null;
        _missingExports = Array.Empty<string>();
        _absentMethods = new HashSet<string>(StringComparer.Ordinal);
    }

    private static byte[] IdentityBytes(in SteamNetworkingIdentity identity) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in identity), 1)).ToArray();

    private static void EnsureProbed()
    {
        if (_state == ProbeState.NotProbed ||
            (_state == ProbeState.LibraryNotLoaded && Environment.TickCount64 >= _nextRetryMs))
        {
            try
            {
                Probe();
            }
            catch (Exception e)
            {
                _state = ProbeState.LibraryNotLoaded;
                _nextRetryMs = Environment.TickCount64 + RetryIntervalMs;
                _status = $"probe failed: {Describe(e)}";
            }
        }
    }

    private static void Probe()
    {
        if (_exportLookup is not null)
        {
            Evaluate(_exportLookup, "export lookup");
            return;
        }

        if (!TryLoadLibrary(out nint library, out string from))
        {
            _state = ProbeState.LibraryNotLoaded;
            _nextRetryMs = Environment.TickCount64 + RetryIntervalMs;
            _status = $"{SteamNative.Library} can't be loaded (not running inside the game?)";
            return;
        }

        _library = library;
        InstallResolver();
        Evaluate(name => NativeLibrary.TryGetExport(library, name, out _), from);
    }

    /// <summary>Sorts the bindings into present and missing with <paramref name="hasExport"/>, and sets the state.</summary>
    private static void Evaluate(Func<string, bool> hasExport, string from)
    {
        var absentExports = new HashSet<string>(BoundExports.Where(name => !hasExport(name)), StringComparer.Ordinal);
        bool Absent(Binding binding) => absentExports.Contains(binding.Export);

        var missingRequired = Bindings.Where(b => !b.Optional && b.Interface is null && Absent(b)).Select(b => b.Export).Distinct().ToList();
        var requiredNames = missingRequired.ToList();
        var accessors = new List<string>();
        int interfaces = 0;
        foreach (IGrouping<string?, Binding> versions in Bindings.Where(b => b.Interface is not null).GroupBy(b => b.Interface))
        {
            interfaces++;
            string[] present = versions.Where(b => !Absent(b)).Select(b => b.Export).ToArray();
            if (present.Length > 0)
            {
                accessors.Add(present.Max(StringComparer.Ordinal)!);
                continue;
            }

            string[] all = versions.Select(b => b.Export).ToArray();
            missingRequired.AddRange(all);
            requiredNames.Add($"one of {string.Join("/", all)}");
        }

        string[] missingOptional = OptionalExports.Where(absentExports.Contains).ToArray();
        _absentMethods = new HashSet<string>(Bindings.Where(Absent).Select(b => b.Method), StringComparer.Ordinal);
        _missingExports = missingRequired.Concat(missingOptional).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();
        int required = Bindings.Count(b => !b.Optional && b.Interface is null) + interfaces;
        int optional = OptionalExports.Count;
        string optionalStatus = missingOptional.Length == 0
            ? $"all {optional} optional ones"
            : $"{optional - missingOptional.Length} of {optional} optional ones (missing, so only the calls that need them are off: {string.Join(", ", missingOptional)})";
        if (requiredNames.Count > 0)
        {
            _state = ProbeState.MissingExports;
            _status = $"{SteamNative.Library} ({from}) lacks {requiredNames.Count} of {required} required exports, so every Steam call is off: " +
                $"{string.Join(", ", requiredNames)}; it has {optionalStatus}";
            return;
        }

        _state = ProbeState.Ready;
        _status = $"{SteamNative.Library} ({from}) has all {required} required exports (with {string.Join(", ", accessors)}) and {optionalStatus}";
    }

    private static bool TryLoadLibrary(out nint library, out string from)
    {
        if (_libraryPath is not null && NativeLibrary.TryLoad(_libraryPath, out library))
        {
            from = "by path"; // never the path itself: it can hold the Windows user's name (D127)
            return true;
        }

        from = "by name";
        return NativeLibrary.TryLoad(SteamNative.Library, typeof(SteamNative).Assembly, null, out library);
    }

    private static void InstallResolver()
    {
        // Make every DllImport in this assembly bind to exactly the module the probe checked.
        if (_resolverInstalled)
        {
            return;
        }

        _resolverInstalled = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SteamNative).Assembly, static (name, _, _) =>
                name == SteamNative.Library ? _library : 0);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already installed for this assembly; DllImport keeps its default probing.
        }
    }

    private static Binding[] FindBindings() =>
        typeof(SteamNative).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(method => (Method: method, Export: method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint))
            .Where(import => import.Export is not null)
            .Select(import => new Binding(
                import.Method.Name,
                import.Export!,
                import.Method.IsDefined(typeof(OptionalExportAttribute)),
                import.Method.GetCustomAttribute<InterfaceAccessorAttribute>()?.InterfaceName))
            .OrderBy(binding => binding.Export, StringComparer.Ordinal)
            .ThenBy(binding => binding.Method, StringComparer.Ordinal)
            .ToArray();

    /// <summary>One <see cref="SteamNative"/> method and its export, as the probe sorts them.</summary>
    private sealed record Binding(string Method, string Export, bool Optional, string? Interface);
}
