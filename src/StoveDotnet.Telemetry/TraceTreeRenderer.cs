using System.Globalization;
using System.Text;

namespace StoveDotnet.Telemetry;

internal static class TraceTreeRenderer
{
    public static string Render(IReadOnlyList<SpanRecord> spans)
    {
        if (spans.Count == 0)
        {
            return "(no spans received)";
        }

        var ids = spans.Select(s => s.SpanId).ToHashSet(StringComparer.Ordinal);
        var children = spans
            .Where(s => s.ParentSpanId is not null && ids.Contains(s.ParentSpanId))
            .ToLookup(s => s.ParentSpanId!, StringComparer.Ordinal);
        var roots = spans.Where(s => s.ParentSpanId is null || !ids.Contains(s.ParentSpanId)).OrderBy(s => s.StartTime);

        var output = new StringBuilder();
        foreach (var root in roots)
        {
            Append(output, root, children, prefix: string.Empty, childPrefix: string.Empty);
        }

        return output.ToString().TrimEnd();
    }

    private static void Append(StringBuilder output, SpanRecord span, ILookup<string, SpanRecord> children, string prefix, string childPrefix)
    {
        output.Append(prefix)
            .Append(span.IsError ? "[x] " : "[ok] ")
            .Append(span.Name);

        // Client spans are named after the method only (HTTP semantic conventions); the URL is what tells them apart.
        if (span.Kind == "Client" && span.Attribute("url.full") is string url && !span.Name.Contains(url, StringComparison.Ordinal))
        {
            output.Append(' ').Append(url);
        }

        output.Append(" (").Append(span.ServiceName).Append(", ").Append(span.Kind).Append(", ")
            .Append(span.Duration.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)).Append("ms)");

        if (span.IsError)
        {
            var exception = span.Events.FirstOrDefault(e => e.Name == "exception");
            var message = exception?.Attributes.GetValueOrDefault("exception.message") ?? span.StatusMessage;
            var type = exception?.Attributes.GetValueOrDefault("exception.type");
            if (message is not null || type is not null)
            {
                output.Append(" error: ").Append(type is null ? string.Empty : $"{type}: ").Append(message);
            }
        }

        output.AppendLine();

        var ordered = children[span.SpanId].OrderBy(s => s.StartTime).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var last = i == ordered.Count - 1;
            Append(output, ordered[i], children, childPrefix + (last ? "`-- " : "|-- "), childPrefix + (last ? "    " : "|   "));
        }
    }
}
