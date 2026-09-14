using System.Globalization;
using System.Text;

namespace StoveDotnet.Telemetry;

public sealed record TelemetryExposedConfiguration(Uri Endpoint) : IExposedConfiguration;

public sealed class TelemetryOptions : SystemOptions<TelemetryExposedConfiguration>
{
    public TelemetryOptions()
    {
        ConfigureExposedConfiguration = DefaultConfiguration;
    }

    /// <summary>Receiver port; 0 picks a free port.</summary>
    public int Port { get; set; }

    /// <summary>How long to wait for late spans and logs when a test fails.</summary>
    public TimeSpan FailureFlushWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>No new data for this long means the application has finished exporting.</summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Maximum number of log lines attached to a failed test.</summary>
    public int MaxLogsInFailureDetails { get; set; } = 200;

    /// <summary>
    /// Standard OpenTelemetry SDK settings pointing the application at the receiver. The .NET SDK reads these from
    /// <c>IConfiguration</c>; combine with your own keys via <c>[.. TelemetryOptions.DefaultConfiguration(c), ...]</c>.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string?>> DefaultConfiguration(TelemetryExposedConfiguration exposed)
    {
        ArgumentNullException.ThrowIfNull(exposed);
        yield return new("OTEL_EXPORTER_OTLP_ENDPOINT", exposed.Endpoint.ToString().TrimEnd('/'));
        yield return new("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        yield return new("OTEL_BSP_SCHEDULE_DELAY", "100");
        yield return new("OTEL_BLRP_SCHEDULE_DELAY", "100");
    }
}

/// <summary>Receives the application's traces and logs over OTLP and attaches them to the test that caused them.</summary>
public sealed class TelemetrySystem
    : ExposingSystem<TelemetryOptions, TelemetryExposedConfiguration>, IRunAware, ITestScopeAware, IFailureDetailsProvider
{
    private readonly TelemetryStore _store = new();
    private OtlpReceiver? _receiver;

    public TelemetrySystem(string? name, TelemetryOptions options)
        : base(name, options)
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _receiver = new OtlpReceiver(_store, Options.Port);
        await _receiver.StartAsync(cancellationToken).ConfigureAwait(false);
        Expose(new TelemetryExposedConfiguration(_receiver.Endpoint));
    }

    /// <summary>Spans of the current test, once the application has stopped exporting new ones (or <paramref name="waitFor"/> elapsed).</summary>
    public async Task<IReadOnlyList<SpanRecord>> Spans(TimeSpan? waitFor = null)
    {
        var test = StoveTestContext.Require();
        await WaitForQuiet(test, waitFor ?? Options.FailureFlushWait, test.CancellationToken).ConfigureAwait(false);
        return _store.Spans(test.TraceId);
    }

    /// <summary>Logs emitted within the current test's trace.</summary>
    public async Task<IReadOnlyList<LogRecord>> Logs(TimeSpan? waitFor = null)
    {
        var test = StoveTestContext.Require();
        await WaitForQuiet(test, waitFor ?? Options.FailureFlushWait, test.CancellationToken).ConfigureAwait(false);
        return _store.Logs(test.TraceId);
    }

    /// <summary>Waits until a span of the current test matches <paramref name="predicate"/>.</summary>
    public async Task<SpanRecord> ShouldContainSpan(Func<SpanRecord, bool> predicate, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var test = StoveTestContext.Require();
        SpanRecord? match = null;
        await Eventually.UntilAsync(
            _ => ValueTask.FromResult((match = _store.Spans(test.TraceId).FirstOrDefault(predicate)) is not null),
            timeout ?? TimeSpan.FromSeconds(5),
            () => $"no span matched. Received spans:{Environment.NewLine}{TraceTreeRenderer.Render(_store.Spans(test.TraceId))}",
            _store.Changed,
            test.CancellationToken).ConfigureAwait(false);
        return match!;
    }

    /// <summary>Fails when any span of the current test has error status.</summary>
    public async Task ShouldNotHaveFailedSpans(TimeSpan? waitFor = null)
    {
        var spans = await Spans(waitFor).ConfigureAwait(false);
        if (spans.Any(s => s.IsError))
        {
            throw new StoveAssertionException(
                $"Expected no failed spans but found {spans.Count(s => s.IsError)}:{Environment.NewLine}{TraceTreeRenderer.Render(spans)}");
        }
    }

    /// <summary>Renders the spans received so far for the current test as a tree.</summary>
    public string RenderTree() => TraceTreeRenderer.Render(_store.Spans(StoveTestContext.Require().TraceId));

    public Task OnTestStartedAsync(StoveTestContext test) => Task.CompletedTask;

    public Task OnTestEndedAsync(StoveTestContext test, Exception? failure)
    {
        // Failure details are collected after the scope ends; keep the data of failed tests until then.
        if (failure is null)
        {
            _store.Evict(test.TraceId);
        }

        return Task.CompletedTask;
    }

    public async Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        try
        {
            await WaitForQuiet(test, Options.FailureFlushWait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Describe whatever arrived in time.
        }

        var spans = _store.Spans(test.TraceId);
        var logs = _store.Logs(test.TraceId);
        _store.Evict(test.TraceId);

        var content = new StringBuilder()
            .AppendLine(TraceTreeRenderer.Render(spans))
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Logs ({logs.Count}):");

        foreach (var log in logs.TakeLast(Options.MaxLogsInFailureDetails))
        {
            content.AppendLine(CultureInfo.InvariantCulture,
                $"  {log.Timestamp:HH:mm:ss.fff} [{log.Severity}] {log.Category}: {log.Body}");
        }

        if (logs.Count == 0)
        {
            content.AppendLine("  (no logs received)");
        }

        return new FailureDetails(Name is null ? "telemetry" : $"telemetry '{Name}'", content.ToString().TrimEnd());
    }

    /// <summary>Waits until data for the test has arrived and nothing new came in for <see cref="TelemetryOptions.QuietPeriod"/>.</summary>
    private async Task WaitForQuiet(StoveTestContext test, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maxWait);
        var lastCount = 0;
        var lastGrowth = TimeProvider.System.GetTimestamp();
        try
        {
            while (true)
            {
                var changed = _store.Changed.NextChange();
                var count = _store.Spans(test.TraceId).Count + _store.Logs(test.TraceId).Count;
                if (count != lastCount)
                {
                    lastCount = count;
                    lastGrowth = TimeProvider.System.GetTimestamp();
                }

                var quietFor = TimeProvider.System.GetElapsedTime(lastGrowth);
                if (count > 0 && quietFor >= Options.QuietPeriod)
                {
                    return;
                }

                var remaining = count > 0 ? Options.QuietPeriod - quietFor : Options.QuietPeriod;
                // Changes from other tests wake this loop too; the quiet period is measured on this test's data only.
                await Task.WhenAny(changed, Task.Delay(remaining, timeout.Token)).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Max wait elapsed; callers use whatever arrived.
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_receiver is not null)
        {
            await _receiver.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public static class TelemetryStoveExtensions
{
    public static StoveBuilder WithTelemetry(this StoveBuilder builder, Action<TelemetryOptions>? configure = null)
    {
        var options = new TelemetryOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new TelemetrySystem(name: null, options));
    }

    public static TelemetrySystem Telemetry(this StoveTestContext test) => test.GetSystem<TelemetrySystem>();
}
