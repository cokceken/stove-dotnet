namespace StoveDotnet.Telemetry;

public enum SpanStatus
{
    Unset,
    Ok,
    Error,
}

public sealed record SpanEventRecord(string Name, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>A finished activity of the application, captured in-process.</summary>
public sealed record SpanRecord(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    string Source,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    SpanStatus Status,
    string? StatusMessage,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<SpanEventRecord> Events)
{
    public TimeSpan Duration => EndTime - StartTime;

    public bool IsError => Status == SpanStatus.Error;

    public object? Attribute(string key) => Attributes.GetValueOrDefault(key);
}
