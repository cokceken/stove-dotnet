using System.Collections.Concurrent;

namespace StoveDotnet.Telemetry;

/// <summary>Spans of the traces that belong to running or failed tests. Nothing else is retained.</summary>
internal sealed class TelemetryStore
{
    private sealed class Trace
    {
        public ConcurrentQueue<SpanRecord> Spans { get; } = new();
        public int InFlight;
    }

    private readonly ConcurrentDictionary<string, Trace> _traces = new(StringComparer.Ordinal);

    public AsyncChangeSignal Changed { get; } = new();

    public void Track(string traceId) => _traces[traceId] = new Trace();

    public bool IsTracked(string traceId) => _traces.ContainsKey(traceId);

    public void Started(string traceId)
    {
        if (_traces.TryGetValue(traceId, out var trace)) Interlocked.Increment(ref trace.InFlight);
    }

    public void Stopped(SpanRecord span)
    {
        if (!_traces.TryGetValue(span.TraceId, out var trace)) return;
        trace.Spans.Enqueue(span);
        Interlocked.Decrement(ref trace.InFlight);
        Changed.Notify();
    }

    public IReadOnlyList<SpanRecord> Spans(string traceId) =>
        _traces.TryGetValue(traceId, out var trace) ? trace.Spans.OrderBy(s => s.StartTime).ToList() : [];

    /// <summary>Spans of the trace that started and have not finished yet.</summary>
    public int InFlight(string traceId) =>
        _traces.TryGetValue(traceId, out var trace) ? Math.Max(0, Volatile.Read(ref trace.InFlight)) : 0;

    public void Evict(string traceId) => _traces.TryRemove(traceId, out _);
}
