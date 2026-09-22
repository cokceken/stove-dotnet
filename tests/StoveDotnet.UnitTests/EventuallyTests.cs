using Xunit;

namespace StoveDotnet.UnitTests;

public sealed class EventuallyTests
{
    [Fact]
    public async Task Legacy_zero_timeout_checks_once_and_legacy_assertion_retries_nested_timeouts()
    {
        var calls = 0;
        await Eventually.UntilAsync(_ => { calls++; return ValueTask.FromResult(true); }, TimeSpan.Zero, () => "immediate");
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<StoveTimeoutException>(() => Eventually.UntilAsync(_ => ValueTask.FromResult(false), TimeSpan.Zero, () => "immediate"));
        calls = 0;
        await Eventually.AssertAsync(() =>
        {
            if (++calls == 1) throw new StoveTimeoutException("inner timeout");
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task Effective_timeout_cancels_an_inflight_assertion_and_reports_attempts()
    {
        var error = await Assert.ThrowsAsync<StoveTimeoutException>(() => Eventually.AssertAsync(
            ct => Task.Delay(Timeout.InfiniteTimeSpan, ct), TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("attempts: 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("Timed out after", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_timeout()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Eventually.AssertAsync(async ct => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }, TimeSpan.FromSeconds(5), cancellationToken: cts.Token);
        await started.Task;
        await cts.CancelAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cts.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Programming_errors_fail_immediately_but_legacy_assertions_retry()
    {
        var calls = 0;
        Task Fail() { calls++; throw new InvalidOperationException("bug"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Eventually.AssertAsync(_ => Fail(), TimeSpan.FromSeconds(1)));
        Assert.Equal(1, calls);
        var legacy = await Assert.ThrowsAsync<StoveTimeoutException>(() => Eventually.AssertAsync(Fail, TimeSpan.FromMilliseconds(180)));
        Assert.True(calls > 2);
        Assert.IsType<InvalidOperationException>(legacy.InnerException);
        Assert.Contains("bug", legacy.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configured_retry_and_value_probe_return_matching_value()
    {
        var count = 0;
        var result = await Eventually.UntilAsync<int>(_ =>
        {
            if (++count == 1) throw new InvalidOperationException("transient");
            return ValueTask.FromResult(count);
        }, value => value == 3, TimeSpan.FromSeconds(2), new EventuallyOptions { RetryOn = e => e is InvalidOperationException });
        Assert.Equal(3, result);
        var timeout = await Assert.ThrowsAsync<StoveTimeoutException>(() => Eventually.UntilAsync<string>(_ => ValueTask.FromResult("waiting"),
            value => value == "done", TimeSpan.FromMilliseconds(100)));
        Assert.Contains("last observed: waiting", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Noncooperative_probe_is_awaited_without_overlap_or_late_success()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var task = Eventually.AssertAsync(async _ => { calls++; entered.SetResult(); await release.Task; }, TimeSpan.FromMilliseconds(60));
        await entered.Task;
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var completedBeforeRelease = task.IsCompleted;
        release.SetResult();
        await Assert.ThrowsAsync<StoveTimeoutException>(() => task);
        Assert.False(completedBeforeRelease);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Throughout_samples_until_duration_and_never_retries_a_failure()
    {
        var calls = 0;
        await Eventually.ThroughoutAsync(_ => { calls++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(180), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(calls > 1);
        calls = 0;
        await Assert.ThrowsAsync<StoveAssertionException>(() => Eventually.ThroughoutAsync(_ =>
        {
            if (++calls == 2) throw new StoveAssertionException("changed");
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Throughout_does_not_start_another_probe_in_the_final_sampling_interval()
    {
        var calls = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Eventually.ThroughoutAsync(_ => { calls++; return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(140));
    }

    [Fact]
    public async Task Throughout_does_not_treat_an_unfinished_probe_as_success()
    {
        await Assert.ThrowsAsync<StoveTimeoutException>(() => Eventually.ThroughoutAsync(
            ct => Task.Delay(Timeout.InfiniteTimeSpan, ct), TimeSpan.FromMilliseconds(60)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Eventually.ThroughoutAsync(_ => Task.CompletedTask,
            TimeSpan.FromSeconds(1), cancellationToken: cts.Token));
    }
}
