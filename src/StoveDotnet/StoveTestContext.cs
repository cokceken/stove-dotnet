using System.Diagnostics;
using System.Security.Cryptography;

namespace StoveDotnet;

/// <summary>
/// The scope of a single e2e test. Created by <see cref="Stove.Test"/>; module packages add accessors such as
/// <c>t.Http()</c> or <c>t.Postgres()</c> as extension methods.
/// </summary>
public sealed class StoveTestContext
{
    private static readonly AsyncLocal<StoveTestContext?> CurrentTest = new();

    internal StoveTestContext(Stove stove, string testName, CancellationToken cancellationToken)
    {
        Stove = stove;
        TestName = testName;
        TestId = $"{testName}#{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        TraceId = TraceContext.NewTraceId();
        RootSpanId = TraceContext.NewSpanId();
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// The test currently running on this async flow, or <c>null</c>. Intended for module authors that need to
    /// correlate data with the running test; tests should use the context passed to their body.
    /// </summary>
    public static StoveTestContext? Current
    {
        get => CurrentTest.Value;
        internal set => CurrentTest.Value = value;
    }

    public Stove Stove { get; }

    /// <summary>Derived from the test file and method, e.g. <c>OrderTests.Creates_order</c>.</summary>
    public string TestName { get; }

    /// <summary>Unique per run of a test (theory rows get distinct ids). Sent as <see cref="StoveHeaders.TestId"/>.</summary>
    public string TestId { get; }

    /// <summary>W3C trace id (32 lowercase hex chars) that every Stove-originated call of this test continues.</summary>
    public string TraceId { get; }

    public string RootSpanId { get; }

    /// <summary>W3C <c>traceparent</c> header value for this test.</summary>
    public string Traceparent => TraceContext.FormatTraceparent(TraceId, RootSpanId);

    public CancellationToken CancellationToken { get; }

    /// <summary>The running application, if Stove started one.</summary>
    public IApplicationContext Application =>
        Stove.Application ?? throw new InvalidOperationException("Stove was started without an application under test.");

    /// <summary>Resolves a registered system. See <c>StoveBuilder</c> for name resolution rules.</summary>
    public T GetSystem<T>(string? name = null) where T : class, IPluggedSystem => Stove.Registry.Resolve<T>(name);

    /// <summary>
    /// Starts an <see cref="Activity"/> that continues this test's trace and makes it current. The application runs in
    /// the same process, so its instrumentation (e.g. HttpClient) observes Stove's own calls; with this activity current
    /// the spans it creates and the context it propagates stay in the test's trace. Dispose it when the call completes.
    /// </summary>
    public Activity StartCorrelatedActivity(string operationName)
    {
        var activity = new Activity(operationName);
        activity.SetParentId(
            ActivityTraceId.CreateFromString(TraceId),
            ActivitySpanId.CreateFromString(RootSpanId),
            ActivityTraceFlags.Recorded);
        activity.AddBaggage(StoveBaggage.TestId, TestId);
        return activity.Start();
    }

    /// <summary>Headers that tie a request or message to this test.</summary>
    public IEnumerable<KeyValuePair<string, string>> CorrelationHeaders()
    {
        yield return new(StoveHeaders.Traceparent, Traceparent);
        yield return new(StoveHeaders.TestId, TestId);
    }

    /// <summary>
    /// Whether data observed with the given headers belongs to this test. Data carrying another test's id or trace is
    /// excluded; data without correlation headers is included.
    /// </summary>
    public bool Owns(string? testIdHeader, string? traceparentHeader)
    {
        if (!string.IsNullOrEmpty(testIdHeader))
        {
            return string.Equals(testIdHeader, TestId, StringComparison.Ordinal);
        }

        return !TraceContext.TryParseTraceId(traceparentHeader, out var traceId)
            || string.Equals(traceId, TraceId, StringComparison.Ordinal);
    }

    /// <summary>Throws when a module DSL is used outside of <see cref="Stove.Test"/>.</summary>
    public static StoveTestContext Require() =>
        Current ?? throw new InvalidOperationException("This Stove operation must be called inside stove.Test(async t => ...).");
}

public static class StoveHeaders
{
    public const string Traceparent = "traceparent";
    public const string TestId = "X-Stove-Test-Id";
}

public static class StoveBaggage
{
    /// <summary>W3C baggage key carrying the test id through applications that propagate baggage.</summary>
    public const string TestId = "stove.test.id";
}
