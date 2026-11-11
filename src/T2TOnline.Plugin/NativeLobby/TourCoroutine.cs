#if HAS_INTEROP
using System;
using T2TOnline.Core.Lobby;
using UnityEngine;

namespace T2TOnline.Plugin.NativeLobby;

/// <summary>
/// A game coroutine (an IL2CPP <c>IEnumerator</c>, such as the game's own campaign walker returns) for the check tour's
/// <see cref="CoroutinePump"/>: its next stretch, and what it yielded read as the pump's kinds, in Unity's terms
/// (verified in the interop: <c>WaitForSeconds.m_Seconds</c>, <c>WaitForSecondsRealtime.waitTime</c> on a
/// <c>CustomYieldInstruction</c>, <c>CustomYieldInstruction.keepWaiting</c>, <c>AsyncOperation.isDone</c>). The wrapper
/// holds the coroutine and every object it waits on, so none is collected while the pump needs it. Developer only: the
/// check tour drives it; nothing is injected into the game.
/// </summary>
internal sealed class TourCoroutine : ICoroutineFrame
{
    private readonly Il2CppSystem.Collections.IEnumerator _routine;

    /// <summary>Wraps <paramref name="routine"/>, named <paramref name="name"/> in the log.</summary>
    public TourCoroutine(Il2CppSystem.Collections.IEnumerator routine, string name)
    {
        _routine = routine ?? throw new ArgumentNullException(nameof(routine));
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public CoroutineYield Current { get; private set; } = CoroutineYield.NextFrame();

    /// <inheritdoc />
    public bool MoveNext()
    {
        bool more = _routine.MoveNext();
        Current = more ? Read(_routine.Current, Name) : CoroutineYield.NextFrame();
        return more;
    }

    /// <summary>
    /// What a coroutine yielded, as the pump's kind. The order matters: <c>WaitForSecondsRealtime</c> is a
    /// <c>CustomYieldInstruction</c>, and a <c>CustomYieldInstruction</c> is an <c>IEnumerator</c>.
    /// </summary>
    public static CoroutineYield Read(Il2CppSystem.Object? value, string within)
    {
        if (value == null)
        {
            return CoroutineYield.NextFrame();
        }

        if (value.TryCast<WaitForSeconds>() is { } seconds)
        {
            return CoroutineYield.Wait(seconds.m_Seconds);
        }

        if (value.TryCast<WaitForSecondsRealtime>() is { } realtime)
        {
            return CoroutineYield.WaitRealtime(realtime.waitTime);
        }

        if (value.TryCast<CustomYieldInstruction>() is { } custom)
        {
            string what = TypeName(value);
            return CoroutineYield.While(() => custom.keepWaiting, what);
        }

        if (value.TryCast<Il2CppSystem.Collections.IEnumerator>() is { } nested)
        {
            return CoroutineYield.Into(new TourCoroutine(nested, $"{within} > {TypeName(value)}"));
        }

        if (value.TryCast<AsyncOperation>() is { } operation)
        {
            return CoroutineYield.While(() => !operation.isDone, "AsyncOperation");
        }

        return value.TryCast<WaitForEndOfFrame>() != null ? CoroutineYield.NextFrame("WaitForEndOfFrame") : CoroutineYield.Unknown(TypeName(value));
    }

    private static string TypeName(Il2CppSystem.Object value)
    {
        try
        {
            return value.GetIl2CppType()?.Name ?? "?";
        }
        catch (Exception)
        {
            return "?"; // a name for the log only
        }
    }

    /// <summary>
    /// The tour's start-up check of the names above, on Unity's own yield objects: what each reads as, and one stretch of a
    /// <c>WaitForSecondsRealtime</c> through the <c>IEnumerator</c> wrapper (its first <c>MoveNext</c> says it waits, its
    /// <c>Current</c> is null). One line; a failure is said, never thrown.
    /// </summary>
    public static string SelfCheck()
    {
        try
        {
            CoroutineYield seconds = Read(new WaitForSeconds(0.5f), "the check");
            var realtime = new WaitForSecondsRealtime(0.25f);
            CoroutineYield real = Read(realtime, "the check");
            CoroutineYield frame = Read(new WaitForEndOfFrame(), "the check");
            Il2CppSystem.Collections.IEnumerator asEnumerator = realtime.Cast<Il2CppSystem.Collections.IEnumerator>();
            bool waits = asEnumerator.MoveNext();
            bool currentNull = asEnumerator.Current == null;
            bool right = seconds.Kind == CoroutineYieldKind.Seconds && Math.Abs(seconds.Seconds - 0.5) < 1e-6
                && real.Kind == CoroutineYieldKind.RealtimeSeconds && Math.Abs(real.Seconds - 0.25) < 1e-6
                && frame.Kind == CoroutineYieldKind.NextFrame && waits && currentNull;
            return $"the coroutine pump reads WaitForSeconds as {Said(seconds)}, WaitForSecondsRealtime as {Said(real)}, WaitForEndOfFrame as {Said(frame)}; " +
                $"IEnumerator MoveNext {waits}, Current {(currentNull ? "null" : "not null")}{(right ? string.Empty : " (NOT as expected: the pump can't be trusted on this build)")}.";
        }
        catch (Exception ex)
        {
            return $"the coroutine pump's names couldn't be read ({Diagnostics.ModLog.SafeErrorText(ex)}): the pump can't be trusted on this build.";
        }
    }

    private static string Said(CoroutineYield yielded) => yielded.Kind switch
    {
        CoroutineYieldKind.Seconds => FormattableString.Invariant($"{yielded.Seconds:0.##} s"),
        CoroutineYieldKind.RealtimeSeconds => FormattableString.Invariant($"{yielded.Seconds:0.##} s of real time"),
        CoroutineYieldKind.NextFrame => "one frame",
        _ => yielded.Kind.ToString(),
    };
}
#endif
