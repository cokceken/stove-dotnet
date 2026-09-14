using StoveDotnet.Telemetry;
using Xunit;

namespace StoveDotnet.Hosting.AcceptanceTests;

public sealed class TelemetryTests
{
    [Theory]
    [InlineData("Order {OrderId} created", "Order 42 created")]
    [InlineData("Took {Elapsed:0.00}ms for {@Order}", "Took 1.5ms for o-1")]
    [InlineData("Unknown {Missing} stays", "Unknown {Missing} stays")]
    [InlineData("No placeholders", "No placeholders")]
    public void Formats_log_message_templates(string template, string expected)
    {
        var attributes = new Dictionary<string, object?> { ["OrderId"] = 42L, ["Elapsed"] = 1.5, ["Order"] = "o-1" };

        Assert.Equal(expected, OtlpConverter.FormatTemplate(template, attributes));
    }

    [Fact]
    public void Renders_spans_as_a_tree_with_errors_and_client_urls()
    {
        var start = DateTimeOffset.UnixEpoch;
        SpanRecord Span(string id, string? parent, string name, string kind, SpanStatus status = SpanStatus.Unset, Dictionary<string, object?>? attributes = null) =>
            new("t", id, parent, name, kind, "svc", start, start.AddMilliseconds(5), status, null, attributes ?? [], []);

        var tree = TraceTreeRenderer.Render(
        [
            Span("a", "stove-root", "POST /orders", "Server"),
            Span("b", "a", "POST", "Client", SpanStatus.Error, new() { ["url.full"] = "http://payments/charges" }),
        ]);

        Assert.Equal(
            """
            [ok] POST /orders (svc, Server, 5ms)
            `-- [x] POST http://payments/charges (svc, Client, 5ms)
            """.ReplaceLineEndings(),
            tree);
    }
}
