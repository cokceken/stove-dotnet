extern alias ClockApp;
using System.Diagnostics;
using System.Net;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Telemetry;
using Xunit;
using ClockAppMarker = ClockApp::ClockAppMarker;

namespace StoveDotnet.Hosting.AcceptanceTests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task Application_without_any_telemetry_setup_still_yields_the_trace_of_the_test()
    {
        // The clock app registers no OpenTelemetry SDK, instrumentation or exporter.
        await using var stove = await StoveBuilder.Create()
            .WithTelemetry()
            .WithHttpClient()
            .WithAspNetCoreApplication<ClockAppMarker>()
            .StartAsync(TestContext.Current.CancellationToken);

        await stove.Test(async t =>
        {
            (await t.Http().Get("/clock")).Expect(HttpStatusCode.OK);

            // No polling: the handler's span is still finishing when the response arrives, and Spans waits for it.
            var spans = await t.Telemetry().Spans();

            var call = Assert.Single(spans, s => s.Kind == "Client");
            Assert.EndsWith("/clock", Assert.IsType<string>(call.Attribute("url.full")), StringComparison.Ordinal);
            Assert.Equal(200, call.Attribute("http.response.status_code"));
            // The handler nests under the call that caused it, so the tree reads top-down without any setup.
            var handler = Assert.Single(spans, s => s.Kind == "Server" && s.Source == "Microsoft.AspNetCore");
            Assert.Equal(call.SpanId, handler.ParentSpanId);
            Assert.All(spans, s => Assert.Equal(t.TraceId, s.TraceId));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Only_activities_inside_a_test_trace_are_sampled_and_captured()
    {
        using var source = new ActivitySource("StoveDotnet.Tests.Sampling");
        await using var stove = await StoveBuilder.Create().WithTelemetry().StartAsync(TestContext.Current.CancellationToken);

        // Outside a test nothing asks for this source, so the application would not pay for the activity either.
        Assert.Null(source.StartActivity("outside any test"));

        await stove.Test(async t =>
        {
            using (var activity = source.StartActivity("inside the test"))
            {
                Assert.NotNull(activity);
                activity.SetTag("order.id", 42);
                activity.SetStatus(ActivityStatusCode.Error, "declined");
            }

            var span = Assert.Single(await t.Telemetry().Spans());
            Assert.Equal("inside the test", span.Name);
            Assert.Equal("StoveDotnet.Tests.Sampling", span.Source);
            Assert.Equal(42, span.Attribute("order.id"));
            Assert.True(span.IsError);
            Assert.Equal("declined", span.StatusMessage);
        }, TestContext.Current.CancellationToken);

        Assert.Null(source.StartActivity("after the test"));
    }

    [Fact]
    public async Task Failure_report_notes_spans_that_never_finished()
    {
        using var source = new ActivitySource("StoveDotnet.Tests.InFlight");
        await using var stove = await StoveBuilder.Create()
            .WithTelemetry(o => o.SettleTimeout = TimeSpan.FromMilliseconds(100))
            .StartAsync(TestContext.Current.CancellationToken);
        Activity? stuck = null;

        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(_ =>
        {
            using (source.StartActivity("finished")) { }
            stuck = source.StartActivity("stuck");
            throw new InvalidOperationException("gave up waiting");
        }, TestContext.Current.CancellationToken));
        stuck?.Dispose();

        Assert.Contains("[ok] finished", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 span(s) had started but not finished", error.Message, StringComparison.Ordinal);
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
