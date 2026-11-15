namespace StoveDotnet.Telemetry;

/// <summary>Outcome a span reported.</summary>
public enum SpanStatus
{
    /// <summary>The span did not set a status.</summary>
    Unset,
    /// <summary>The span reported success.</summary>
    Ok,
    /// <summary>The span reported an error.</summary>
    Error,
}

/// <summary>An event recorded on a span, for example an exception.</summary>
/// <param name="Name">Event name, for example <c>exception</c>.</param>
/// <param name="Timestamp">When the event was recorded.</param>
/// <param name="Attributes">The event's tags.</param>
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
    /// <summary>How long the span took.</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Whether the span reported an error.</summary>
    public bool IsError => Status == SpanStatus.Error;

    /// <summary>The value of a tag, or <c>null</c> when the span does not have it.</summary>
    public object? Attribute(string key) => Attributes.GetValueOrDefault(key);
}
