using System.Diagnostics;

namespace StoveDotnet.Telemetry;

public sealed class TelemetryOptions
{
    /// <summary>
    /// How long to wait for spans of the test that have started but not finished, for example a request handler that
    /// completes just after its response was sent. Spans that finish later are missing from the result.
    /// </summary>
    public TimeSpan SettleTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Which activity sources to capture. By default every named source except the runtime sources marked
    /// experimental, which report connection-level detail.
    /// </summary>
    public Func<ActivitySource, bool> ListenTo { get; set; } =
        source => !source.Name.StartsWith("Experimental.", StringComparison.Ordinal);
}

/// <summary>
/// Captures the activities (spans) the application creates for each test, in-process, and attaches the trace tree to
/// the test when it fails. The application needs no exporter or collector. What a span contains is decided by whoever
/// creates it: HttpClient spans carry method, URL and status on their own; ASP.NET Core, database and broker spans are
/// as detailed as the instrumentation the application has registered.
/// </summary>
public sealed class TelemetrySystem : IPluggedSystem, IRunAware, ITestScopeAware, IFailureDetailsProvider
{
    private readonly TelemetryStore _store = new();
    private ActivityListener? _listener;

    public TelemetrySystem(string? name, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Name = name;
        Options = options;
    }

    public string? Name { get; }

    public TelemetryOptions Options { get; }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (Options.SettleTimeout <= TimeSpan.Zero) throw new InvalidOperationException("SettleTimeout must be positive.");
        ArgumentNullException.ThrowIfNull(Options.ListenTo);
        _listener = new ActivityListener
        {
            // Activities created without a source (including the ones Stove starts to carry the test trace) are plumbing.
            ShouldListenTo = source => source.Name.Length > 0 && Options.ListenTo(source),
            // Ask for data only inside a test trace, so the application samples everything else exactly as it would
            // without Stove.
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                _store.IsTracked(options.Parent.TraceId.ToHexString())
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
            ActivityStarted = activity => _store.Started(activity.TraceId.ToHexString()),
            ActivityStopped = activity =>
            {
                if (_store.IsTracked(activity.TraceId.ToHexString())) _store.Stopped(ToSpan(activity));
            },
        };
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Spans of the current test that have finished. Waits for spans that are still in flight, up to
    /// <paramref name="waitFor"/> (default <see cref="TelemetryOptions.SettleTimeout"/>). Work the application has not
    /// started yet cannot be waited for here; use <see cref="ShouldContainSpan"/> for asynchronous outcomes.
    /// </summary>
    public async Task<IReadOnlyList<SpanRecord>> Spans(TimeSpan? waitFor = null)
    {
        var test = StoveTestContext.Require();
        await WaitUntilSettled(test, waitFor ?? Options.SettleTimeout, test.CancellationToken).ConfigureAwait(false);
        return _store.Spans(test.TraceId);
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
            () => $"no span matched. Captured spans:{Environment.NewLine}{TraceTreeRenderer.Render(_store.Spans(test.TraceId))}",
            _store.Changed,
            cancellationToken: test.CancellationToken).ConfigureAwait(false);
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

    /// <summary>Renders the spans captured so far for the current test as a tree.</summary>
    public string RenderTree() => TraceTreeRenderer.Render(_store.Spans(StoveTestContext.Require().TraceId));

    public Task OnTestStartedAsync(StoveTestContext test)
    {
        ArgumentNullException.ThrowIfNull(test);
        _store.Track(test.TraceId);
        return Task.CompletedTask;
    }

    public Task OnTestEndedAsync(StoveTestContext test, Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(test);
        // Failure details are collected after the scope ends; keep the spans of a failed test until then.
        if (failure is null) _store.Evict(test.TraceId);
        return Task.CompletedTask;
    }

    public async Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        try
        {
            await WaitUntilSettled(test, Options.SettleTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Describe whatever finished in time.
        }

        var spans = _store.Spans(test.TraceId);
        var inFlight = _store.InFlight(test.TraceId);
        _store.Evict(test.TraceId);

        var content = TraceTreeRenderer.Render(spans);
        if (inFlight > 0) content += $"{Environment.NewLine}({inFlight} span(s) had started but not finished)";
        return new FailureDetails(Name is null ? "telemetry" : $"telemetry '{Name}'", content);
    }

    private async Task WaitUntilSettled(StoveTestContext test, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changed = _store.Changed.NextChange();
            if (_store.InFlight(test.TraceId) == 0) return;
            var remaining = maxWait - TimeProvider.System.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return;
            try
            {
                await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    private static SpanRecord ToSpan(Activity activity)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects) attributes[tag.Key] = tag.Value;

        var events = new List<SpanEventRecord>();
        foreach (var activityEvent in activity.Events)
        {
            var eventAttributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in activityEvent.Tags) eventAttributes[tag.Key] = tag.Value;
            events.Add(new SpanEventRecord(activityEvent.Name, activityEvent.Timestamp, eventAttributes));
        }

        var start = new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero);
        return new SpanRecord(
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            activity.DisplayName,
            activity.Kind.ToString(),
            activity.Source.Name,
            start,
            start + activity.Duration,
            activity.Status switch
            {
                ActivityStatusCode.Ok => SpanStatus.Ok,
                ActivityStatusCode.Error => SpanStatus.Error,
                _ => SpanStatus.Unset,
            },
            string.IsNullOrEmpty(activity.StatusDescription) ? null : activity.StatusDescription,
            attributes,
            events);
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        return ValueTask.CompletedTask;
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
