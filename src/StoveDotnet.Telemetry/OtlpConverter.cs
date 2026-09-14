using Google.Protobuf;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;
using ProtoStatus = OpenTelemetry.Proto.Trace.V1.Status;

namespace StoveDotnet.Telemetry;

/// <summary>Converts OTLP protobuf payloads into Stove's records.</summary>
internal static partial class OtlpConverter
{
    public static IEnumerable<SpanRecord> ToSpans(ExportTraceServiceRequest request)
    {
        foreach (var resourceSpans in request.ResourceSpans)
        {
            var serviceName = ServiceName(resourceSpans.Resource?.Attributes);
            foreach (var span in resourceSpans.ScopeSpans.SelectMany(s => s.Spans))
            {
                yield return ToSpan(span, serviceName);
            }
        }
    }

    public static IEnumerable<LogRecord> ToLogs(ExportLogsServiceRequest request)
    {
        foreach (var resourceLogs in request.ResourceLogs)
        {
            var serviceName = ServiceName(resourceLogs.Resource?.Attributes);
            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                foreach (var log in scopeLogs.LogRecords)
                {
                    var timestamp = log.TimeUnixNano != 0 ? log.TimeUnixNano : log.ObservedTimeUnixNano;
                    var attributes = Attributes(log.Attributes);
                    yield return new LogRecord(
                        FromUnixNanos(timestamp),
                        string.IsNullOrEmpty(log.SeverityText) ? log.SeverityNumber.ToString() : log.SeverityText,
                        log.Body is null ? string.Empty : FormatTemplate(AnyValueToString(log.Body), attributes),
                        HexOrNull(log.TraceId),
                        HexOrNull(log.SpanId),
                        serviceName,
                        string.IsNullOrEmpty(scopeLogs.Scope?.Name) ? NullIfEmpty(log.EventName) : scopeLogs.Scope.Name,
                        attributes);
                }
            }
        }
    }

    private static SpanRecord ToSpan(ProtoSpan span, string serviceName) =>
        new(
            HexOrNull(span.TraceId) ?? string.Empty,
            HexOrNull(span.SpanId) ?? string.Empty,
            HexOrNull(span.ParentSpanId),
            span.Name,
            span.Kind.ToString().Replace("Unspecified", "Internal", StringComparison.Ordinal),
            serviceName,
            FromUnixNanos(span.StartTimeUnixNano),
            FromUnixNanos(span.EndTimeUnixNano),
            span.Status?.Code switch
            {
                ProtoStatus.Types.StatusCode.Ok => SpanStatus.Ok,
                ProtoStatus.Types.StatusCode.Error => SpanStatus.Error,
                _ => SpanStatus.Unset,
            },
            string.IsNullOrEmpty(span.Status?.Message) ? null : span.Status.Message,
            Attributes(span.Attributes),
            span.Events.Select(e => new SpanEventRecord(e.Name, FromUnixNanos(e.TimeUnixNano), Attributes(e.Attributes))).ToList());

    /// <summary>
    /// The .NET SDK exports structured logs as a message template (e.g. <c>Order {OrderId} created</c>) plus attributes;
    /// fill the placeholders back in for readable output.
    /// </summary>
    internal static string FormatTemplate(string template, IReadOnlyDictionary<string, object?> attributes) =>
        attributes.Count == 0 || !template.Contains('{', StringComparison.Ordinal)
            ? template
            : TemplatePlaceholder().Replace(template, match =>
                attributes.TryGetValue(match.Groups[1].Value, out var value)
                    ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
                    : match.Value);

    [System.Text.RegularExpressions.GeneratedRegex(@"\{@?([A-Za-z0-9_.]+)(?:[,:][^{}]*)?\}")]
    private static partial System.Text.RegularExpressions.Regex TemplatePlaceholder();

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string ServiceName(RepeatedField<KeyValue>? attributes) =>
        attributes?.FirstOrDefault(a => a.Key == "service.name")?.Value is { } value ? AnyValueToString(value) : "unknown";

    private static Dictionary<string, object?> Attributes(RepeatedField<KeyValue> attributes)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            result[attribute.Key] = AnyValueToObject(attribute.Value);
        }

        return result;
    }

    private static object? AnyValueToObject(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
        AnyValue.ValueOneofCase.IntValue => value.IntValue,
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        AnyValue.ValueOneofCase.BytesValue => Convert.ToHexStringLower(value.BytesValue.Span),
        AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(AnyValueToObject).ToList(),
        AnyValue.ValueOneofCase.KvlistValue => Attributes(value.KvlistValue.Values),
        _ => null,
    };

    private static string AnyValueToString(AnyValue value) => AnyValueToObject(value) switch
    {
        null => string.Empty,
        string s => s,
        IEnumerable<object?> list => $"[{string.Join(", ", list)}]",
        var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string? HexOrNull(ByteString bytes) =>
        bytes.IsEmpty || bytes.Span.IndexOfAnyExcept((byte)0) < 0 ? null : Convert.ToHexStringLower(bytes.Span);

    private static DateTimeOffset FromUnixNanos(ulong nanos) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(nanos / 100));
}
