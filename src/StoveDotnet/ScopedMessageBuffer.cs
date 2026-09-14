using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace StoveDotnet;

public enum UncorrelatedMessagePolicy
{
    Exclude,
    /// <summary>Attribute headerless records received while exactly one test is active to that test. Observer time is not publication time.</summary>
    SingleActiveTest,
}

/// <summary>Bounds retained message evidence per active test. Limits are captured when the system is constructed.</summary>
public sealed class MessageObservationOptions
{
    public int MaxMessagesPerTest { get; set; } = 10_000;
    /// <summary>Maximum retained payload/header bytes per test, excluding object overhead.</summary>
    public long MaxBytesPerTest { get; set; } = 16 * 1024 * 1024;
    public UncorrelatedMessagePolicy UncorrelatedMessages { get; set; } = UncorrelatedMessagePolicy.Exclude;
}

// Shared by broker observers. Only active contexts are strongly held; failed scopes keep diagnostics through a weak key.
internal sealed class ScopedMessageBuffer<T>
{
    private sealed class Scope(StoveTestContext test)
    {
        public string TestId { get; } = test.TestId;
        public string TraceId { get; } = test.TraceId;
        public List<T> Records { get; } = [];
        public long Bytes { get; set; }
        public string? Error { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Scope> _active = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<StoveTestContext, Scope> _scopes = new();
    private readonly int _maxMessages;
    private readonly long _maxBytes;
    private readonly UncorrelatedMessagePolicy _policy;
    private string? _observerError;

    public ScopedMessageBuffer(MessageObservationOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMessagesPerTest);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxBytesPerTest);
        if (!Enum.IsDefined(options.UncorrelatedMessages)) throw new ArgumentOutOfRangeException(nameof(options));
        _maxMessages = options.MaxMessagesPerTest;
        _maxBytes = options.MaxBytesPerTest;
        _policy = options.UncorrelatedMessages;
    }

    public AsyncChangeSignal Changed { get; } = new();

    public void Start(StoveTestContext test)
    {
        lock (_gate)
        {
            var scope = new Scope(test);
            _scopes.Add(test, scope);
            _active.Add(test.TestId, scope);
        }
    }

    public void End(StoveTestContext test, Exception? failure)
    {
        lock (_gate)
        {
            _active.Remove(test.TestId);
            if (failure is null) _scopes.Remove(test);
        }
    }

    public void Add(T record, long bytes, string? testId, string? traceparent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var changed = false;
        lock (_gate)
        {
            if (_active.Count == 0) return;
            // Invalid or conflicting headers never qualify for the uncorrelated fallback.
            string? traceId = null;
            if (traceparent is not null)
            {
                if (!ActivityContext.TryParse(traceparent, null, out var context)) return;
                traceId = context.TraceId.ToHexString();
            }
            if (testId is not null && string.IsNullOrWhiteSpace(testId)) return;
            foreach (var scope in _active.Values)
            {
                var correlated = testId is not null || traceId is not null;
                if (correlated)
                {
                    if ((testId is not null && testId != scope.TestId) || (traceId is not null && traceId != scope.TraceId)) continue;
                }
                else if (_policy != UncorrelatedMessagePolicy.SingleActiveTest || _active.Count != 1) continue;

                if (scope.Error is not null) continue;
                if (scope.Records.Count >= _maxMessages || bytes > _maxBytes - scope.Bytes)
                {
                    scope.Error = $"Message observation incomplete: retention limit exceeded ({_maxMessages} messages / {_maxBytes} bytes per test). Increase Observation limits or narrow observed routes/topics.";
                    changed = true;
                    continue;
                }
                scope.Records.Add(record);
                scope.Bytes += bytes;
                changed = true;
            }
        }
        if (changed) Changed.Notify();
    }

    public void Fail(string error)
    {
        lock (_gate) _observerError ??= "Message observation incomplete: " + error;
        Changed.Notify();
    }

    public (IReadOnlyList<T> Records, string? Error) Inspect(StoveTestContext test)
    {
        lock (_gate)
        {
            return _scopes.TryGetValue(test, out var scope)
                ? (scope.Records.ToArray(), _observerError ?? scope.Error)
                : (Array.Empty<T>(), _observerError);
        }
    }

    public IReadOnlyList<T> Snapshot(StoveTestContext test)
    {
        var snapshot = Inspect(test);
        if (snapshot.Error is not null) throw new StoveAssertionException(snapshot.Error);
        return snapshot.Records;
    }
}
