using System.Diagnostics;

namespace StoveDotnet;

/// <summary>How <see cref="Eventually"/> polls and which exceptions it treats as "not yet".</summary>
public sealed class EventuallyOptions
{
    /// <summary>
    /// Which exceptions mean "not yet". <c>null</c> selects the method's default: <c>AssertAsync</c> retries every
    /// exception, <c>UntilAsync</c> retries none. Cancellation is never retried.
    /// </summary>
    public Func<Exception, bool>? RetryOn { get; set; }
    /// <summary>Pause between attempts. Defaults to 50 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>Sequential, cooperative polling using wall-clock deadlines, independent of application test clocks.</summary>
public static class Eventually
{
    /// <summary>
    /// Polls until the probe returns true. An exception from the probe is an error and propagates, unless
    /// <see cref="EventuallyOptions.RetryOn"/> selects it. A signal wakes the next poll early.
    /// </summary>
    public static Task UntilAsync(Func<CancellationToken, ValueTask<bool>> probe, TimeSpan timeout,
        Func<string> describeTimeout, AsyncChangeSignal? signal = null, EventuallyOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Poll(probe, value => value, timeout, describeTimeout, options, _ => false, value => value.ToString(), signal, cancellationToken);

    /// <summary>
    /// Polls until the value matches and returns it; exceptions propagate as for the bool probe. A timeout includes
    /// the last observed value (customize <paramref name="describeValue"/> to redact sensitive data).
    /// </summary>
    public static Task<T> UntilAsync<T>(Func<CancellationToken, ValueTask<T>> probe, Func<T, bool> matches,
        TimeSpan timeout, EventuallyOptions? options = null, Func<T, string>? describeValue = null,
        CancellationToken cancellationToken = default) =>
        Poll(probe, matches, timeout, () => "probe did not match", options, _ => false,
            describeValue ?? (value => value?.ToString() ?? "<null>"), null, cancellationToken);

    /// <summary>Retries the assertion until it stops throwing. Use the token overload to cancel in-flight I/O at the deadline.</summary>
    public static Task AssertAsync(Func<Task> assertion, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        return AssertAsync(_ => assertion(), timeout, options: null, cancellationToken);
    }

    /// <summary>
    /// Retries the assertion until it stops throwing: any exception, from any assertion library, means "not yet"
    /// unless <see cref="EventuallyOptions.RetryOn"/> narrows it. The assertion receives the linked caller/deadline token.
    /// </summary>
    public static async Task AssertAsync(Func<CancellationToken, Task> assertion, TimeSpan timeout,
        EventuallyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        await Poll(async ct => { await assertion(ct).ConfigureAwait(false); return true; }, value => value, timeout,
            () => "assertion did not pass", options, _ => true, _ => "assertion failed", null, cancellationToken).ConfigureAwait(false);
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
        TimeSpan timeout, Func<string> description, EventuallyOptions? options, Func<Exception, bool> defaultRetryOn,
        Func<T, string> describeValue, AsyncChangeSignal? signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(matches);
        options ??= new EventuallyOptions();
        Validate(timeout, options);
        var retryOn = options.RetryOn ?? defaultRetryOn;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        var attempts = 0;
        var observed = "<no value>";
        Exception? last = null;
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
            catch (Exception ex) when (ex is not OperationCanceledException && retryOn(ex))
            {
                last = ex;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Expired()) throw Failure();
            if (matched) return value;
            try
            {
                // Cancel the delay when signalled too; no abandoned polling tasks accumulate.
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var wait = Task.Delay(options.PollInterval, delayCancellation.Token);
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
        }
        bool Expired() => deadline.IsCancellationRequested || watch.Elapsed >= timeout;
        StoveTimeoutException Failure() => Timeout(timeout, watch, attempts,
            $"{description()}; last observed: {Bound(observed)}; last failure: {Bound(last?.Message ?? "<none>")}", last);
    }

    private static void Validate(TimeSpan timeout, EventuallyOptions options)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(timeout));
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
/// <summary>
/// A wait in <see cref="Eventually"/> or a module's <c>Should*</c> method ran out of time. The message reports
/// elapsed time, attempts and what was last observed; the last retried exception is the inner exception.
/// </summary>
public sealed class StoveTimeoutException : TimeoutException
{
    /// <summary>Creates the exception with a description of what was waited for and what was observed.</summary>
    public StoveTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception, keeping the last retried failure as the inner exception.</summary>
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

    /// <summary>
    /// Completes the task handed out by <see cref="NextChange"/> and arms a new one. Call it after the store
    /// changed.
    /// </summary>
    public void Notify() =>
        Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}
