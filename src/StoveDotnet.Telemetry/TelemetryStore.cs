using System.Collections.Concurrent;

namespace StoveDotnet.Telemetry;

internal sealed class TelemetryStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SpanRecord>> _spans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<LogRecord>> _logs = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _tracked = new(StringComparer.Ordinal);

    public AsyncChangeSignal Changed { get; } = new();

    /// <summary>
    /// Starts retaining data for a test's trace. Everything is read by trace id, so data of any other trace (late
    /// exports of finished tests, startup, background work) could never be returned and is not stored.
    /// </summary>
    public void Track(string traceId) => _tracked[traceId] = 0;

    public void Add(IEnumerable<SpanRecord> spans)
    {
        foreach (var span in spans)
        {
            if (!_tracked.ContainsKey(span.TraceId)) continue;
            _spans.GetOrAdd(span.TraceId, _ => new ConcurrentQueue<SpanRecord>()).Enqueue(span);
            // The test may have ended between the check and the enqueue.
            if (!_tracked.ContainsKey(span.TraceId)) _spans.TryRemove(span.TraceId, out _);
        }

        Changed.Notify();
    }

    public void Add(IEnumerable<LogRecord> logs)
    {
        // Logs outside of a trace cannot be attributed to a test.
        foreach (var log in logs.Where(l => l.TraceId is not null))
        {
            if (!_tracked.ContainsKey(log.TraceId!)) continue;
            _logs.GetOrAdd(log.TraceId!, _ => new ConcurrentQueue<LogRecord>()).Enqueue(log);
            if (!_tracked.ContainsKey(log.TraceId!)) _logs.TryRemove(log.TraceId!, out _);
        }

        Changed.Notify();
    }

    public IReadOnlyList<SpanRecord> Spans(string traceId) =>
        _spans.TryGetValue(traceId, out var spans) ? spans.OrderBy(s => s.StartTime).ToList() : [];

    public IReadOnlyList<LogRecord> Logs(string traceId) =>
        _logs.TryGetValue(traceId, out var logs) ? logs.OrderBy(l => l.Timestamp).ToList() : [];

    public void Evict(string traceId)
    {
        _tracked.TryRemove(traceId, out _);
        _spans.TryRemove(traceId, out _);
        _logs.TryRemove(traceId, out _);
    }
}
