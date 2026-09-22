using System.Diagnostics;

namespace StoveDotnet;

public sealed class EventuallyOptions
{
    /// <summary>New overloads retry Stove assertions by default. Include your test framework's assertion exception explicitly.</summary>
    public Func<Exception, bool> RetryOn { get; set; } = error => error is StoveAssertionException;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>Sequential, cooperative polling using wall-clock deadlines, independent of application test clocks.</summary>
public static class Eventually
{
    /// <summary>Legacy probe: false is retried; exceptions propagate immediately.</summary>
    public static Task UntilAsync(Func<CancellationToken, ValueTask<bool>> probe, TimeSpan timeout,
        Func<string> describeTimeout, AsyncChangeSignal? signal = null, CancellationToken cancellationToken = default) =>
        Poll(probe, value => value, timeout, describeTimeout, new EventuallyOptions { RetryOn = _ => false },
            value => value.ToString(), signal, cancellationToken, backoff: true);

    public static Task UntilAsync(Func<CancellationToken, ValueTask<bool>> probe, TimeSpan timeout,
        EventuallyOptions options, Func<string> describeTimeout, AsyncChangeSignal? signal = null,
        CancellationToken cancellationToken = default) =>
        Poll(probe, value => value, timeout, describeTimeout, options, value => value.ToString(), signal, cancellationToken);

    /// <summary>Returns the matching value; timeout includes the last observed value (customize to redact sensitive data).</summary>
    public static Task<T> UntilAsync<T>(Func<CancellationToken, ValueTask<T>> probe, Func<T, bool> matches,
        TimeSpan timeout, EventuallyOptions? options = null, Func<T, string>? describeValue = null,
        CancellationToken cancellationToken = default) =>
        Poll(probe, matches, timeout, () => "probe did not match", options ?? new EventuallyOptions(),
            describeValue ?? (value => value?.ToString() ?? "<null>"), null, cancellationToken);

    /// <summary>Compatibility overload: retries every exception except cancellation; the delegate receives no timeout token.</summary>
    public static Task AssertAsync(Func<Task> assertion, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Poll(async _ => { await assertion().ConfigureAwait(false); return true; }, value => value, timeout,
            () => "assertion did not pass", new EventuallyOptions { RetryOn = _ => true }, _ => "assertion failed", null, cancellationToken, backoff: true);

    /// <summary>Retries selected exceptions. The assertion receives the linked caller/deadline token.</summary>
    public static async Task AssertAsync(Func<CancellationToken, Task> assertion, TimeSpan timeout,
        EventuallyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        await Poll(async ct => { await assertion(ct).ConfigureAwait(false); return true; }, value => value, timeout,
            () => "assertion did not pass", options ?? new EventuallyOptions(), _ => "assertion failed", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Samples an assertion throughout a bounded interval. Every failure propagates immediately; no retries.
    /// This cannot prove absence between samples. The assertion must honor its effective deadline token.</summary>
    public static async Task ThroughoutAsync(Func<CancellationToken, Task> assertion, TimeSpan duration,
        TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var options = new EventuallyOptions { PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50) };
        Validate(duration, options);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var watch = Stopwatch.StartNew();
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Leave one sampling interval for a new probe. Starting work at the deadline
            // makes fast I/O randomly fail solely because of timer scheduling jitter.
            if (attempts > 0 && duration - watch.Elapsed <= options.PollInterval)
            {
                var remaining = duration - watch.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            attempts++;
            try
            {
                await assertion(deadline.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // An assertion that ignored cancellation and completed late did not establish a bounded observation.
                if (deadline.IsCancellationRequested || watch.Elapsed >= duration) throw Timeout(duration, watch, attempts, "assertion did not finish before the observation deadline", null);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Timeout(duration, watch, attempts, "assertion did not finish before the observation deadline", null);
            }
            try { await Task.Delay(options.PollInterval, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return; // Duration elapsed between successful samples, not while a probe was unfinished.
            }
        }
    }

    private static async Task<T> Poll<T>(Func<CancellationToken, ValueTask<T>> probe, Func<T, bool> matches,
        TimeSpan timeout, Func<string> description, EventuallyOptions options, Func<T, string> describeValue,
        AsyncChangeSignal? signal, CancellationToken cancellationToken, bool backoff = false)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(matches);
        Validate(timeout, options, allowLegacyTimeouts: backoff);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != TimeSpan.Zero) deadline.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        var attempts = 0;
        var observed = "<no value>";
        Exception? last = null;
        var delay = options.PollInterval;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Expired()) throw Failure();
            var changed = signal?.NextChange();
            attempts++;
            T value = default!;
            var matched = false;
            try
            {
                value = await probe(deadline.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                observed = describeValue(value);
                matched = matches(value);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Failure();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && options.RetryOn(ex))
            {
                last = ex;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Expired()) throw Failure();
            if (matched) return value;
            if (timeout == TimeSpan.Zero) throw Failure();
            try
            {
                // Cancel the delay when signalled too; no abandoned polling tasks accumulate.
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var wait = Task.Delay(delay, delayCancellation.Token);
                if (changed is null) await wait.ConfigureAwait(false);
                else
                {
                    await Task.WhenAny(changed, wait).ConfigureAwait(false);
                    await delayCancellation.CancelAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Failure();
            }
            if (backoff) delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 500));
        }
        bool Expired() => deadline.IsCancellationRequested ||
            (timeout > TimeSpan.Zero && watch.Elapsed >= timeout);
        StoveTimeoutException Failure() => Timeout(timeout, watch, attempts,
            $"{description()}; last observed: {Bound(observed)}; last failure: {Bound(last?.Message ?? "<none>")}", last);
    }

    private static void Validate(TimeSpan timeout, EventuallyOptions options, bool allowLegacyTimeouts = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.RetryOn);
        if ((!allowLegacyTimeouts || (timeout != TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan))
            && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (options.PollInterval <= TimeSpan.Zero || options.PollInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must be a positive supported delay.");
    }

    private static string Bound(string value) => value.Length <= 1024 ? value : value[..1024] + "… [truncated]";
    private static StoveTimeoutException Timeout(TimeSpan timeout, Stopwatch watch, int attempts, string description, Exception? last)
    {
        var message = $"Timed out after {watch.Elapsed.TotalSeconds:0.###}s (limit {timeout.TotalSeconds:0.###}s, attempts: {attempts}): {description}";
        return last is null ? new StoveTimeoutException(message) : new StoveTimeoutException(message, last);
    }
}
public sealed class StoveTimeoutException : TimeoutException
{
    public StoveTimeoutException(string message)
        : base(message)
    {
    }

    public StoveTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Lets waiters observe that a store changed without polling.</summary>
public sealed class AsyncChangeSignal
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the next <see cref="Notify"/>. Obtain it before inspecting the store to avoid missing a change.</summary>
    public Task NextChange() => Volatile.Read(ref _next).Task;

    public void Notify() =>
        Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}
