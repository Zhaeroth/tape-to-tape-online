using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using T2TOnline.Steam.Native;

namespace T2TOnline.Steam.Tests;

public class SteamCallResultTests
{
    private const ulong Handle = 0x1234_5678_9ABC_DEF0UL;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct TestResult
    {
        public int Code;
        public ulong Id;
    }

    /// <summary>A scripted Steam: the call completes after <see cref="CompleteAfterPolls"/> IsAPICallCompleted calls.</summary>
    private sealed class FakeSource : ISteamCallResultSource
    {
        private bool _fetched;
        private bool _reasonAskedWhenFinished;

        public SteamCallResultPath Path { get; set; } = SteamCallResultPath.SteamUtils;

        public bool Available { get; set; } = true;

        public int CompleteAfterPolls { get; set; } = 1;

        public bool FailWhilePending { get; set; }

        public bool CompletedButFailed { get; set; }

        /// <summary>How many fetches Steam refuses (the result isn't there yet) before <see cref="FetchOutcome"/>.</summary>
        public int RefusedFetches { get; set; }

        public SteamCallFetch FetchOutcome { get; set; } = SteamCallFetch.Fetched;

        public SteamApiCallFailure Reason { get; set; } = SteamApiCallFailure.None;

        /// <summary>Steam's reason once a fetch has been tried (it forgets a fetched call); null: unchanged.</summary>
        public SteamApiCallFailure? ReasonAfterFetch { get; set; }

        /// <summary>
        /// The real Steam client (seen in the game): asking for a finished call's failure reason before fetching it
        /// drops the result, and the fetch is refused. On by default, so every test runs against that behaviour.
        /// </summary>
        public bool ReasonQueryDropsFinishedCall { get; set; } = true;

        public int ReasonQueries { get; private set; }

        public int CompletionChecks { get; private set; }

        public int ResultFetches { get; private set; }

        public int LastCallbackId { get; private set; }

        public int LastResultSize { get; private set; }

        public bool TryIsCompleted(ulong call, out bool completed, out bool failed)
        {
            Assert.Equal(Handle, call);
            CompletionChecks++;
            completed = false;
            failed = false;
            if (!Available)
            {
                return false;
            }

            if (FailWhilePending)
            {
                failed = true;
                return true;
            }

            completed = CompletionChecks >= CompleteAfterPolls;
            failed = completed && CompletedButFailed;
            return true;
        }

        public SteamCallFetch Fetch<T>(ulong call, int callbackId, out T result)
            where T : unmanaged
        {
            Assert.Equal(Handle, call);
            ResultFetches++;
            LastCallbackId = callbackId;
            LastResultSize = Unsafe.SizeOf<T>();
            result = default;
            if (!Available)
            {
                return SteamCallFetch.Unavailable;
            }

            _fetched = true;
            if (ReasonQueryDropsFinishedCall && _reasonAskedWhenFinished)
            {
                return SteamCallFetch.Refused;
            }

            if (ResultFetches <= RefusedFetches)
            {
                return SteamCallFetch.Refused;
            }

            if (FetchOutcome == SteamCallFetch.Fetched)
            {
                Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1));
                BitConverter.TryWriteBytes(bytes, 1);
                BitConverter.TryWriteBytes(bytes[8..], 987654321UL);
            }

            return FetchOutcome;
        }

        public SteamApiCallFailure GetFailureReason(ulong call)
        {
            ReasonQueries++;
            _reasonAskedWhenFinished |= !_fetched && CompletionChecks >= CompleteAfterPolls && !CompletedButFailed && !FailWhilePending;
            return !Available ? SteamApiCallFailure.SteamGone : _fetched && ReasonAfterFetch is { } after ? after : Reason;
        }
    }

    [Fact]
    public void Pending_until_steam_says_done_then_completed_with_the_struct()
    {
        var source = new FakeSource { CompleteAfterPolls = 3 };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Pending, call.Poll());
        Assert.Equal(SteamCallResultState.Pending, call.Poll());
        Assert.False(call.IsDone);
        Assert.Equal(SteamCallResultState.Completed, call.Poll());

        Assert.True(call.IsDone);
        Assert.Equal(1, call.Result.Code);
        Assert.Equal(987654321UL, call.Result.Id);
        Assert.Equal(513, source.LastCallbackId);
        Assert.Equal(16, source.LastResultSize);
        Assert.Equal(3, call.PollCount);
        Assert.Equal(SteamApiCallFailure.None, call.FailureReason);
        Assert.Equal(SteamCallResultFailure.None, call.Failure);
        Assert.False(call.ShouldTryOtherPath);
    }

    [Fact]
    public void A_finished_call_is_fetched_without_asking_steam_for_a_reason_first()
    {
        // v0.5.1 asked for the reason first; the real Steam client then dropped the result and every lobby creation failed.
        var source = new FakeSource { CompleteAfterPolls = 2 };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        call.Poll();
        Assert.Equal(SteamCallResultState.Completed, call.Poll());
        Assert.Equal(0, source.ReasonQueries);
        Assert.Equal(987654321UL, call.Result.Id);
    }

    [Fact]
    public void Finished_calls_never_ask_steam_again()
    {
        var source = new FakeSource();
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);
        call.Poll();
        call.Poll();
        call.Poll();

        Assert.Equal(SteamCallResultState.Completed, call.State);
        Assert.Equal(1, source.CompletionChecks);
        Assert.Equal(1, source.ResultFetches);
    }

    [Fact]
    public void A_zero_handle_fails_at_once_without_asking_steam_and_without_a_made_up_reason()
    {
        var source = new FakeSource();
        var call = new SteamCallResult<TestResult>(0, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.State);
        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.NoHandle, call.Failure);
        Assert.Equal(SteamApiCallFailure.None, call.FailureReason);
        Assert.False(call.ShouldTryOtherPath);
        Assert.Equal(0, source.CompletionChecks);
    }

    [Theory]
    [InlineData(SteamApiCallFailure.NetworkFailure)]
    [InlineData(SteamApiCallFailure.SteamGone)]
    public void A_failure_steam_reports_for_the_call_is_not_a_reason_to_switch_paths(SteamApiCallFailure reason)
    {
        var source = new FakeSource { FailWhilePending = true, Reason = reason };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.CallFailed, call.Failure);
        Assert.Equal(reason, call.FailureReason);
        Assert.False(call.ShouldTryOtherPath);
        Assert.Equal(0, source.ResultFetches);
    }

    [Fact]
    public void Completed_but_failed_is_a_failure_of_the_call()
    {
        var source = new FakeSource { CompletedButFailed = true, Reason = SteamApiCallFailure.NetworkFailure };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.CallFailed, call.Failure);
        Assert.Equal(SteamApiCallFailure.NetworkFailure, call.FailureReason);
        Assert.False(call.ShouldTryOtherPath);
        Assert.Equal(default, call.Result);
    }

    [Fact]
    public void A_handle_the_completion_check_does_not_know_says_to_try_the_other_path()
    {
        var source = new FakeSource { FailWhilePending = true, Reason = SteamApiCallFailure.InvalidHandle };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.CompletionCheckFailed, call.Failure);
        Assert.Equal(SteamApiCallFailure.InvalidHandle, call.FailureReason);
        Assert.True(call.ShouldTryOtherPath);
        Assert.Equal(0, source.ResultFetches);
    }

    [Fact]
    public void A_refused_fetch_after_a_clean_completion_says_to_try_the_other_path_with_steams_reason()
    {
        var source = new FakeSource { FetchOutcome = SteamCallFetch.Refused, ReasonAfterFetch = SteamApiCallFailure.MismatchedCallback };
        var call = new SteamCallResult<TestResult>(Handle, 504, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.FetchFailed, call.Failure);
        Assert.Equal(SteamApiCallFailure.MismatchedCallback, call.FailureReason);
        Assert.True(call.ShouldTryOtherPath);
        Assert.Equal(default, call.Result);
    }

    [Fact]
    public void A_call_that_fails_on_fetch_reports_the_reason_steam_gives_after_it()
    {
        // The reason is asked for only after the fetch (asking first drops a finished call's result in the real client).
        var source = new FakeSource { FetchOutcome = SteamCallFetch.CallFailed, Reason = SteamApiCallFailure.NetworkFailure };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.CallFailed, call.Failure);
        Assert.Equal(SteamApiCallFailure.NetworkFailure, call.FailureReason);
        Assert.Equal(1, source.ReasonQueries);
        Assert.False(call.ShouldTryOtherPath);
    }

    [Fact]
    public void Manual_dispatch_keeps_fetching_a_call_the_completion_check_does_not_know()
    {
        var source = new FakeSource
        {
            Path = SteamCallResultPath.ManualDispatch,
            FailWhilePending = true,
            Reason = SteamApiCallFailure.InvalidHandle,
            RefusedFetches = 2,
        };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultPath.ManualDispatch, call.Path);
        Assert.Equal(SteamCallResultState.Pending, call.Poll());
        Assert.Equal(SteamCallResultState.Pending, call.Poll());
        Assert.Equal(SteamCallResultState.Completed, call.Poll());
        Assert.Equal(987654321UL, call.Result.Id);
        Assert.Equal(3, source.ResultFetches);
    }

    [Fact]
    public void Manual_dispatch_gives_up_on_a_call_steam_has_forgotten_instead_of_polling_it_for_good()
    {
        var source = new FakeSource
        {
            Path = SteamCallResultPath.ManualDispatch,
            FailWhilePending = true,
            Reason = SteamApiCallFailure.InvalidHandle,
            RefusedFetches = int.MaxValue,
        };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        for (int i = 1; i < SteamCallResult<TestResult>.MaxBlindRefusals; i++)
        {
            Assert.Equal(SteamCallResultState.Pending, call.Poll());
        }

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.FetchFailed, call.Failure);
        Assert.Equal(SteamApiCallFailure.None, call.FailureReason);
        Assert.Equal(SteamCallResult<TestResult>.MaxBlindRefusals, source.ResultFetches);
        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResult<TestResult>.MaxBlindRefusals, source.ResultFetches); // asked no more
    }

    [Fact]
    public void Manual_dispatch_reports_a_blind_fetch_failure_without_a_made_up_reason()
    {
        var source = new FakeSource
        {
            Path = SteamCallResultPath.ManualDispatch,
            FailWhilePending = true,
            Reason = SteamApiCallFailure.InvalidHandle,
            FetchOutcome = SteamCallFetch.CallFailed,
        };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.CallFailed, call.Failure);
        Assert.Equal(SteamApiCallFailure.None, call.FailureReason);
        Assert.False(call.ShouldTryOtherPath);
    }

    [Fact]
    public void Manual_dispatch_without_its_pipe_or_exports_says_to_try_the_other_path()
    {
        var source = new FakeSource { Path = SteamCallResultPath.ManualDispatch, FetchOutcome = SteamCallFetch.Unavailable };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.FetchFailed, call.Failure);
        Assert.True(call.ShouldTryOtherPath);
    }

    [Fact]
    public void Steam_going_away_fails_with_steam_unavailable()
    {
        var source = new FakeSource { Available = false };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, 0);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.SteamUnavailable, call.Failure);
        Assert.Equal(SteamApiCallFailure.SteamGone, call.FailureReason);
        Assert.False(call.ShouldTryOtherPath);
    }

    [Fact]
    public void Gives_up_after_the_poll_limit()
    {
        var source = new FakeSource { CompleteAfterPolls = 100 };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, maxPolls: 5);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(SteamCallResultState.Pending, call.Poll());
        }

        Assert.Equal(SteamCallResultState.TimedOut, call.Poll());
        Assert.Equal(SteamCallResultState.TimedOut, call.Poll());
        Assert.Equal(5, source.CompletionChecks);
        Assert.True(call.IsDone);
        Assert.False(call.ShouldTryOtherPath);
    }

    [Fact]
    public void A_timed_out_call_can_be_resumed_and_still_complete()
    {
        var source = new FakeSource { CompleteAfterPolls = 5 };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, maxPolls: 3);
        while (call.Poll() == SteamCallResultState.Pending)
        {
        }

        Assert.Equal(SteamCallResultState.TimedOut, call.State);
        Assert.True(call.Resume());
        Assert.Equal(0, call.MaxPolls);
        Assert.Equal(SteamCallResultState.Pending, call.Poll());
        Assert.Equal(SteamCallResultState.Completed, call.Poll());
        Assert.Equal(987654321UL, call.Result.Id);
        Assert.False(call.Resume());
    }

    [Fact]
    public void Resume_can_set_a_new_limit()
    {
        var source = new FakeSource { CompleteAfterPolls = 100 };
        var call = new SteamCallResult<TestResult>(Handle, 513, source, maxPolls: 2);
        call.Poll();
        call.Poll();

        Assert.True(call.Resume(morePolls: 3));
        Assert.Equal(5, call.MaxPolls);
        Assert.Equal(SteamCallResultState.Pending, call.Poll());
        Assert.Equal(SteamCallResultState.Pending, call.Poll());
        Assert.Equal(SteamCallResultState.TimedOut, call.Poll());
        Assert.Equal(5, source.CompletionChecks);
    }

    [Fact]
    public void Only_a_timed_out_call_can_be_resumed()
    {
        var pending = new SteamCallResult<TestResult>(Handle, 513, new FakeSource { CompleteAfterPolls = 10 }, 0);
        pending.Poll();
        var completed = new SteamCallResult<TestResult>(Handle, 513, new FakeSource(), 0);
        completed.Poll();
        var failed = new SteamCallResult<TestResult>(0, 513, new FakeSource(), 0);

        Assert.False(pending.Resume());
        Assert.False(completed.Resume());
        Assert.False(failed.Resume());
        Assert.Equal(SteamCallResultState.Pending, pending.State);
        Assert.Equal(SteamCallResultState.Completed, completed.State);
        Assert.Equal(SteamCallResultState.Failed, failed.State);
    }

    [Fact]
    public void A_call_that_failed_at_start_keeps_its_reason_and_path()
    {
        SteamCallResult<LobbyCreated> call = SteamCallResult<LobbyCreated>.FailedAtStart(
            LobbyCreated.CallbackId, SteamCallResultPath.ManualDispatch, SteamCallResultFailure.SteamUnavailable, SteamApiCallFailure.SteamGone);

        Assert.Equal(SteamCallResultState.Failed, call.Poll());
        Assert.Equal(SteamCallResultFailure.SteamUnavailable, call.Failure);
        Assert.Equal(SteamApiCallFailure.SteamGone, call.FailureReason);
        Assert.Equal(SteamCallResultPath.ManualDispatch, call.Path);
        Assert.Equal(LobbyCreated.CallbackId, call.CallbackId);
    }
}
