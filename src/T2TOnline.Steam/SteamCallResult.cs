using System;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam;

/// <summary>Where a <see cref="SteamCallResult{T}"/> stands.</summary>
public enum SteamCallResultState
{
    /// <summary>Not finished; keep polling.</summary>
    Pending,

    /// <summary>Finished; <see cref="SteamCallResult{T}.Result"/> holds Steam's struct (check its own result field too).</summary>
    Completed,

    /// <summary>The call failed, or Steam couldn't be asked; <see cref="SteamCallResult{T}.Failure"/> says how.</summary>
    Failed,

    /// <summary>
    /// Still pending after the poll limit. The call itself may still finish: <see cref="SteamCallResult{T}.Resume"/>
    /// lets polling carry on.
    /// </summary>
    TimedOut,
}

/// <summary>
/// How a <see cref="SteamCallResult{T}"/> failed, as this library saw it; Steam's own reason, when it gave one, is
/// <see cref="SteamCallResult{T}.FailureReason"/>. Only <see cref="CompletionCheckFailed"/> and <see cref="FetchFailed"/>
/// are about the <see cref="SteamCallResultPath"/>, so only they make <see cref="SteamCallResult{T}.ShouldTryOtherPath"/>
/// true.
/// </summary>
public enum SteamCallResultFailure
{
    /// <summary>Not failed.</summary>
    None,

    /// <summary>
    /// There was no call to poll: its handle is 0 (<c>k_uAPICallInvalid</c>), because Steam refused to start it or this
    /// library rejected its arguments (such as lobby 0). Steam gave no reason.
    /// </summary>
    NoHandle,

    /// <summary>
    /// Steam couldn't be asked: the bindings are off, or Steam isn't initialised or has shut down
    /// (<see cref="SteamCallResult{T}.FailureReason"/> is <see cref="SteamApiCallFailure.SteamGone"/>).
    /// </summary>
    SteamUnavailable,

    /// <summary>
    /// Steam reports the call itself failed, and <see cref="SteamCallResult{T}.FailureReason"/> says why
    /// (<see cref="SteamApiCallFailure.NetworkFailure"/>: the Steam client lost the Steam servers, usually for a moment).
    /// Nothing to do with the path: try the call again later.
    /// </summary>
    CallFailed,

    /// <summary>
    /// <c>ISteamUtils::IsAPICallCompleted</c> doesn't know a handle Steam has just issued
    /// (<see cref="SteamApiCallFailure.InvalidHandle"/>), so the <see cref="SteamCallResultPath.SteamUtils"/> path can't see
    /// the call. The <see cref="SteamCallResultPath.ManualDispatch"/> path keeps trying to fetch such a call instead.
    /// </summary>
    CompletionCheckFailed,

    /// <summary>
    /// Steam reported the call finished and not failed, but fetching its result on this path didn't work
    /// (<see cref="SteamCallResult{T}.FailureReason"/>: <see cref="SteamApiCallFailure.InvalidHandle"/> if something else
    /// fetched it first, <see cref="SteamApiCallFailure.MismatchedCallback"/> for a wrong callback id, or
    /// <see cref="SteamApiCallFailure.None"/> when the manual dispatch exports or pipe are missing). Also a
    /// <see cref="SteamCallResultPath.ManualDispatch"/> call that neither <c>ISteamUtils</c> nor the fetch has known for
    /// <see cref="SteamCallResult{T}.MaxBlindRefusals"/> polls (<see cref="SteamApiCallFailure.None"/>).
    /// </summary>
    FetchFailed,
}

/// <summary>
/// Which API fetches a finished call's result struct.
/// <para>
/// Try <see cref="SteamUtils"/> first. It is what Facepunch.Steamworks' own <c>CallResult&lt;T&gt;</c> uses in this very
/// process under manual dispatch (<c>IsAPICallCompleted</c> then <c>GetAPICallResult</c> on ISteamUtils), so it is known
/// to work with the game's dispatch mode, and it needs no pipe handle. Facepunch's dispatcher only fetches results for
/// handles it registered itself (it ignores a <c>SteamAPICallCompleted_t</c> for anyone else's), so it won't consume
/// ours; a result can be fetched only once, by whoever asks first.
/// </para>
/// <para>
/// <see cref="ManualDispatch"/> fetches with <c>SteamAPI_ManualDispatch_GetAPICallResult</c> on the pipe from
/// <c>SteamAPI_GetHSteamPipe</c>. It also asks <c>ISteamUtils::IsAPICallCompleted</c> first, but when that doesn't know
/// the handle (<see cref="SteamApiCallFailure.InvalidHandle"/>, the case this path exists for) it keeps trying the fetch
/// itself, which returns nothing until the result is there, up to <see cref="SteamCallResult{T}.MaxBlindRefusals"/> polls.
/// steam_api.h says this fetch
/// is meant for a <c>SteamAPICallCompleted_t</c> handler, which we don't have. Switch paths only when
/// <see cref="SteamCallResult{T}.ShouldTryOtherPath"/> says so.
/// </para>
/// </summary>
public enum SteamCallResultPath
{
    /// <summary><c>SteamAPI_ISteamUtils_GetAPICallResult</c>.</summary>
    SteamUtils,

    /// <summary><c>SteamAPI_ManualDispatch_GetAPICallResult</c>.</summary>
    ManualDispatch,
}

/// <summary>
/// Polls one <c>SteamAPICall_t</c> until it finishes, without callbacks: call <see cref="Poll"/> once a frame.
/// <typeparamref name="T"/> must be the call's result struct with its exact SDK layout (for example
/// <see cref="LobbyCreated"/>), and <see cref="CallbackId"/> its <c>k_iCallback</c>. Poll never throws. Main thread only.
/// </summary>
/// <typeparam name="T">The result struct.</typeparam>
public sealed class SteamCallResult<T>
    where T : unmanaged
{
    /// <summary>
    /// Polls on the <see cref="SteamCallResultPath.ManualDispatch"/> path that found neither <c>ISteamUtils</c> nor the fetch
    /// knowing the call before it fails as <see cref="SteamCallResultFailure.FetchFailed"/> (about ten minutes of frames at
    /// 60 a second): a call Steam has forgotten would otherwise stay pending for good, polled every frame.
    /// </summary>
    public const int MaxBlindRefusals = 36_000;

    private readonly ISteamCallResultSource _source;
    private T _result;
    private int _blindRefusals;

    /// <summary>
    /// Starts polling <paramref name="handle"/> for a result of type <typeparamref name="T"/> with
    /// <paramref name="callbackId"/>. A zero handle (<c>k_uAPICallInvalid</c>) fails at once with
    /// <see cref="SteamCallResultFailure.NoHandle"/>. <paramref name="maxPolls"/> &gt; 0 gives up after that many polls.
    /// </summary>
    public SteamCallResult(ulong handle, int callbackId, SteamCallResultPath path = SteamCallResultPath.SteamUtils, int maxPolls = 0)
        : this(handle, callbackId, SteamCallResultSources.For(path), maxPolls)
    {
    }

    internal SteamCallResult(ulong handle, int callbackId, ISteamCallResultSource source, int maxPolls)
    {
        _source = source;
        Handle = handle;
        CallbackId = callbackId;
        MaxPolls = maxPolls;
        if (handle == 0)
        {
            State = SteamCallResultState.Failed;
            Failure = SteamCallResultFailure.NoHandle;
        }
    }

    /// <summary>The <c>SteamAPICall_t</c> being polled.</summary>
    public ulong Handle { get; }

    /// <summary>The expected <c>k_iCallback</c> of <typeparamref name="T"/>.</summary>
    public int CallbackId { get; }

    /// <summary>The API this call's result is fetched through.</summary>
    public SteamCallResultPath Path => _source.Path;

    /// <summary>Polls before giving up, or 0 for no limit (<see cref="Resume"/> can change it).</summary>
    public int MaxPolls { get; private set; }

    /// <summary>How many times <see cref="Poll"/> has asked Steam.</summary>
    public int PollCount { get; private set; }

    /// <summary>Where the call stands.</summary>
    public SteamCallResultState State { get; private set; }

    /// <summary>True once the call is no longer <see cref="SteamCallResultState.Pending"/>.</summary>
    public bool IsDone => State != SteamCallResultState.Pending;

    /// <summary>How the call failed, when <see cref="State"/> is <see cref="SteamCallResultState.Failed"/>; otherwise <see cref="SteamCallResultFailure.None"/>.</summary>
    public SteamCallResultFailure Failure { get; private set; }

    /// <summary>
    /// Steam's own reason (<c>ESteamAPICallFailure</c>), when <see cref="State"/> is <see cref="SteamCallResultState.Failed"/>:
    /// <see cref="SteamApiCallFailure.None"/> when Steam gave none, <see cref="SteamApiCallFailure.SteamGone"/> when it
    /// couldn't be asked. <see cref="Failure"/> says which step failed.
    /// </summary>
    public SteamApiCallFailure FailureReason { get; private set; } = SteamApiCallFailure.None;

    /// <summary>
    /// True when the call failed because of this path's way of reading results
    /// (<see cref="SteamCallResultFailure.CompletionCheckFailed"/> or <see cref="SteamCallResultFailure.FetchFailed"/>),
    /// so the next call should use the other <see cref="SteamCallResultPath"/>. Never true for a zero handle, Steam being
    /// unavailable, a failure Steam reports for the call itself (a network failure is transient), or a timeout.
    /// </summary>
    public bool ShouldTryOtherPath =>
        State == SteamCallResultState.Failed && Failure is SteamCallResultFailure.CompletionCheckFailed or SteamCallResultFailure.FetchFailed;

    /// <summary>The result struct, once <see cref="State"/> is <see cref="SteamCallResultState.Completed"/>; default before.</summary>
    public T Result => _result;

    /// <summary>A call result that has already failed before Steam was asked to start the call.</summary>
    internal static SteamCallResult<T> FailedAtStart(int callbackId, SteamCallResultPath path, SteamCallResultFailure failure, SteamApiCallFailure reason)
    {
        var call = new SteamCallResult<T>(0, callbackId, SteamCallResultSources.For(path), 0);
        call.Failure = failure;
        call.FailureReason = reason;
        return call;
    }

    /// <summary>Asks Steam once whether the call is done and, if so, fetches the result. Returns the new state.</summary>
    public SteamCallResultState Poll()
    {
        if (State != SteamCallResultState.Pending)
        {
            return State;
        }

        PollCount++;
        Step();
        if (State == SteamCallResultState.Pending && MaxPolls > 0 && PollCount >= MaxPolls)
        {
            State = SteamCallResultState.TimedOut;
        }

        return State;
    }

    /// <summary>
    /// Makes a <see cref="SteamCallResultState.TimedOut"/> call pending again, for up to <paramref name="morePolls"/>
    /// more polls (0 or less: no limit). A call that timed out can still finish, and a lobby created or joined after the
    /// wait is still ours: keep polling it in the background and leave that lobby when it completes. False, changing
    /// nothing, unless the call had timed out.
    /// </summary>
    public bool Resume(int morePolls = 0)
    {
        if (State != SteamCallResultState.TimedOut)
        {
            return false;
        }

        MaxPolls = morePolls > 0 ? (int)Math.Min((long)PollCount + morePolls, int.MaxValue) : 0;
        State = SteamCallResultState.Pending;
        return true;
    }

    private void Step()
    {
        if (!_source.TryIsCompleted(Handle, out bool completed, out bool failed))
        {
            Fail(SteamCallResultFailure.SteamUnavailable, SteamApiCallFailure.SteamGone);
            return;
        }

        if (failed)
        {
            SteamApiCallFailure reason = _source.GetFailureReason(Handle);
            if (reason != SteamApiCallFailure.InvalidHandle)
            {
                Fail(SteamCallResultFailure.CallFailed, reason);
            }
            else if (Path == SteamCallResultPath.ManualDispatch)
            {
                // ISteamUtils doesn't know the handle, the case this path is for: only the fetch can tell.
                Fetch(blind: true);
            }
            else
            {
                Fail(SteamCallResultFailure.CompletionCheckFailed, reason);
            }
        }
        else if (completed)
        {
            Fetch(blind: false);
        }
    }

    /// <summary>
    /// Fetches the result. <paramref name="blind"/>: ISteamUtils can't see the call, so a refusal means "not there yet"
    /// and Steam has no reason to give.
    /// </summary>
    private void Fetch(bool blind)
    {
        // Never ask Steam for a finished call's failure reason before fetching it: the Steam client then drops the
        // result, and the fetch fails with InvalidHandle (v0.5.1 asked first, and every lobby creation failed in game).
        // The reason is read only once a fetch has been refused or the call reports failure.
        switch (_source.Fetch(Handle, CallbackId, out T value))
        {
            case SteamCallFetch.Fetched:
                _result = value;
                State = SteamCallResultState.Completed;
                break;
            case SteamCallFetch.CallFailed:
                // Blind: ISteamUtils doesn't know this call, so its "reason" would be made up.
                Fail(SteamCallResultFailure.CallFailed, blind ? SteamApiCallFailure.None : _source.GetFailureReason(Handle));
                break;
            case SteamCallFetch.Refused when blind:
                // Neither ISteamUtils nor the fetch knows the call: one Steam has forgotten would be polled forever.
                if (++_blindRefusals >= MaxBlindRefusals)
                {
                    Fail(SteamCallResultFailure.FetchFailed, SteamApiCallFailure.None);
                }

                break;
            case SteamCallFetch.Refused:
                // Steam's reason may already be gone with the call (then None); a mismatched callback id shows here.
                Fail(SteamCallResultFailure.FetchFailed, _source.GetFailureReason(Handle));
                break;
            default:
                // ISteamUtils answered a moment ago, so on the manual dispatch path its own exports or pipe are missing.
                if (Path == SteamCallResultPath.ManualDispatch)
                {
                    Fail(SteamCallResultFailure.FetchFailed, SteamApiCallFailure.None);
                }
                else
                {
                    Fail(SteamCallResultFailure.SteamUnavailable, SteamApiCallFailure.SteamGone);
                }

                break;
        }
    }

    private void Fail(SteamCallResultFailure failure, SteamApiCallFailure reason)
    {
        Failure = failure;
        FailureReason = reason;
        State = SteamCallResultState.Failed;
    }
}
