using System.Collections.Concurrent;

namespace StoveDotnet.Telemetry;

internal sealed class TelemetryStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SpanRecord>> _spans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<LogRecord>> _logs = new(StringComparer.Ordinal);

    public AsyncChangeSignal Changed { get; } = new();

    public void Add(IEnumerable<SpanRecord> spans)
    {
        foreach (var span in spans)
        {
            _spans.GetOrAdd(span.TraceId, _ => new ConcurrentQueue<SpanRecord>()).Enqueue(span);
        }

        Changed.Notify();
    }

    public void Add(IEnumerable<LogRecord> logs)
    {
        // Logs outside of a trace cannot be attributed to a test.
        foreach (var log in logs.Where(l => l.TraceId is not null))
        {
            _logs.GetOrAdd(log.TraceId!, _ => new ConcurrentQueue<LogRecord>()).Enqueue(log);
        }

        Changed.Notify();
    }

    public IReadOnlyList<SpanRecord> Spans(string traceId) =>
        _spans.TryGetValue(traceId, out var spans) ? spans.OrderBy(s => s.StartTime).ToList() : [];

    public IReadOnlyList<LogRecord> Logs(string traceId) =>
        _logs.TryGetValue(traceId, out var logs) ? logs.OrderBy(l => l.Timestamp).ToList() : [];

    public void Evict(string traceId)
    {
        _spans.TryRemove(traceId, out _);
        _logs.TryRemove(traceId, out _);
    }
}
