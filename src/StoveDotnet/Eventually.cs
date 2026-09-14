namespace StoveDotnet;

/// <summary>Polling helpers for eventually-consistent assertions.</summary>
public static class Eventually
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Re-evaluates <paramref name="probe"/> until it returns true or <paramref name="timeout"/> elapses. When a
    /// <paramref name="signal"/> is given, waits wake up as soon as it fires instead of on the next poll interval.
    /// </summary>
    public static async Task UntilAsync(
        Func<CancellationToken, ValueTask<bool>> probe,
        TimeSpan timeout,
        Func<string> describeTimeout,
        AsyncChangeSignal? signal = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var delay = InitialDelay;

        while (true)
        {
            var changed = signal?.NextChange();
            try
            {
                if (await probe(timeoutSource.Token).ConfigureAwait(false))
                {
                    return;
                }

                var wait = Task.Delay(delay, timeoutSource.Token);
                await (changed is null ? wait : Task.WhenAny(changed, wait).Unwrap()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new StoveTimeoutException($"Timed out after {timeout.TotalSeconds:0.###}s: {describeTimeout()}");
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }
    }

    /// <summary>Retries <paramref name="assertion"/> until it stops throwing; on timeout rethrows the last failure.</summary>
    public static async Task AssertAsync(Func<Task> assertion, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        try
        {
            await UntilAsync(async _ =>
            {
                try
                {
                    await assertion().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    return false;
                }
            }, timeout, () => last?.Message ?? "assertion did not pass", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (StoveTimeoutException timeoutException) when (last is not null)
        {
            throw new StoveTimeoutException(timeoutException.Message, last);
        }
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
