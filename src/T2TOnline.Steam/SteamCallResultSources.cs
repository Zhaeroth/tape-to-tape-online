using System;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>What one attempt to fetch a call's result found.</summary>
internal enum SteamCallFetch
{
    /// <summary>Steam couldn't be asked (or this path's exports or pipe are missing).</summary>
    Unavailable,

    /// <summary>
    /// Steam returned false: it has no result for this handle and callback id (not finished, unknown to this path,
    /// already fetched, or a mismatched callback id).
    /// </summary>
    Refused,

    /// <summary>Steam returned the call flagged as failed (an I/O failure), which consumes it; the struct means nothing.</summary>
    CallFailed,

    /// <summary>Steam returned the result, which consumes the call.</summary>
    Fetched,
}

/// <summary>What <see cref="SteamCallResult{T}"/> polls: Steam in the game, a fake in tests. Implementations never throw.</summary>
internal interface ISteamCallResultSource
{
    /// <summary>The path results are fetched through.</summary>
    SteamCallResultPath Path { get; }

    /// <summary>False if Steam couldn't be asked; otherwise <c>IsAPICallCompleted</c>'s answer and failed flag.</summary>
    bool TryIsCompleted(ulong call, out bool completed, out bool failed);

    /// <summary>Fetches a call's result struct; <paramref name="result"/> is only meaningful when this returns <see cref="SteamCallFetch.Fetched"/>.</summary>
    SteamCallFetch Fetch<T>(ulong call, int callbackId, out T result)
        where T : unmanaged;

    /// <summary>Why a call failed; <see cref="SteamApiCallFailure.SteamGone"/> if Steam couldn't be asked.</summary>
    SteamApiCallFailure GetFailureReason(ulong call);
}

/// <summary>The two native ways of fetching a call result (see <see cref="SteamCallResultPath"/>).</summary>
internal static class SteamCallResultSources
{
    private static readonly SteamUtilsSource UtilsSource = new();
    private static readonly ManualDispatchSource DispatchSource = new();

    public static ISteamCallResultSource For(SteamCallResultPath path) =>
        path == SteamCallResultPath.ManualDispatch ? DispatchSource : UtilsSource;

    /// <summary>Steam's two answers to a fetch, as one outcome.</summary>
    private static SteamCallFetch Outcome(bool returned, bool failed) =>
        !returned ? SteamCallFetch.Refused : failed ? SteamCallFetch.CallFailed : SteamCallFetch.Fetched;

    private class SteamUtilsSource : ISteamCallResultSource
    {
        public virtual SteamCallResultPath Path => SteamCallResultPath.SteamUtils;

        public bool TryIsCompleted(ulong call, out bool completed, out bool failed)
        {
            completed = false;
            failed = false;
            nint utils = SteamInterfaces.Utils();
            if (utils == 0)
            {
                return false;
            }

            try
            {
                completed = SteamNative.IsApiCallCompleted(utils, call, out failed);
                return true;
            }
            catch (Exception e)
            {
                SteamRuntime.ReportFailure(e);
                completed = false;
                failed = false;
                return false;
            }
        }

        public virtual SteamCallFetch Fetch<T>(ulong call, int callbackId, out T result)
            where T : unmanaged
        {
            result = default;
            nint utils = SteamInterfaces.Utils();
            if (utils == 0)
            {
                return SteamCallFetch.Unavailable;
            }

            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1));
            try
            {
                bool returned = SteamNative.GetApiCallResult(utils, call, ref MemoryMarshal.GetReference(bytes), bytes.Length, callbackId, out bool failed);
                return Outcome(returned, failed);
            }
            catch (Exception e)
            {
                SteamRuntime.ReportFailure(e);
                return SteamCallFetch.Unavailable;
            }
        }

        public SteamApiCallFailure GetFailureReason(ulong call)
        {
            nint utils = SteamInterfaces.Utils();
            if (utils == 0)
            {
                return SteamApiCallFailure.SteamGone;
            }

            try
            {
                return SteamNative.GetApiCallFailureReason(utils, call);
            }
            catch (Exception e)
            {
                SteamRuntime.ReportFailure(e);
                return SteamApiCallFailure.SteamGone;
            }
        }
    }

    private sealed class ManualDispatchSource : SteamUtilsSource
    {
        public override SteamCallResultPath Path => SteamCallResultPath.ManualDispatch;

        public override SteamCallFetch Fetch<T>(ulong call, int callbackId, out T result)
        {
            result = default;
            int pipe = SteamInterfaces.Pipe();
            if (pipe == 0 || !SteamRuntime.HasExport(nameof(SteamNative.ManualDispatchGetApiCallResult)))
            {
                return SteamCallFetch.Unavailable;
            }

            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1));
            try
            {
                bool returned = SteamNative.ManualDispatchGetApiCallResult(pipe, call, ref MemoryMarshal.GetReference(bytes), bytes.Length, callbackId, out bool failed);
                return Outcome(returned, failed);
            }
            catch (Exception e)
            {
                SteamRuntime.ReportFailure(e);
                return SteamCallFetch.Unavailable;
            }
        }
    }
}
