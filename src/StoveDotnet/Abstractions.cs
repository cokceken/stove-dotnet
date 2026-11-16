namespace StoveDotnet;

/// <summary>A system (dependency or capability) plugged into Stove, e.g. a database, a message broker or an HTTP client.</summary>
public interface IPluggedSystem : IAsyncDisposable
{
    /// <summary>Optional instance name. <c>null</c> is the default (unnamed) instance.</summary>
    string? Name { get; }
}

/// <summary>Systems that start before the application. Independent systems run in parallel; declared dependencies start first.</summary>
public interface IRunAware
{
    /// <summary>
    /// Starts the system and returns once it is ready for the application: container up, connectivity checked,
    /// migrations run.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Systems that must finish starting before this system starts.</summary>
public interface ISystemDependencies
{
    /// <summary>The systems to wait for. Each must be registered on the same builder.</summary>
    IReadOnlyCollection<IPluggedSystem> Dependencies { get; }
}

/// <summary>Systems that contribute configuration to the application under test.</summary>
public interface IExposesConfiguration
{
    /// <summary>
    /// The configuration keys and values this system contributes. Called after every system has started; a key
    /// exposed by two systems is an error.
    /// </summary>
    IEnumerable<KeyValuePair<string, string?>> Configuration();
}

/// <summary>Systems that keep per-test state (stubs, collected telemetry, ...).</summary>
public interface ITestScopeAware
{
    /// <summary>A test scope is starting. Set up whatever the system keeps per test.</summary>
    Task OnTestStartedAsync(StoveTestContext test);

    /// <summary>
    /// <paramref name="failure"/> is set only when a failure report will be requested through
    /// <see cref="IFailureDetailsProvider"/>; a passed, skipped or cancelled test ends with <c>null</c>, and the system
    /// should release what it kept for the scope.
    /// </summary>
    Task OnTestEndedAsync(StoveTestContext test, Exception? failure);
}

/// <summary>Systems that can describe what they observed for a failed test (trace tree, logs, messages).</summary>
public interface IFailureDetailsProvider
{
    /// <summary>
    /// What the system observed for the failed test, or <c>null</c> when it has nothing to add. Called after the
    /// scope ended; honour the token, which bounds how long the report may wait.
    /// </summary>
    Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken);
}

/// <summary>One section of a failed test's report.</summary>
/// <param name="Title">Heading of the section, for example <c>kafka</c> or <c>wiremock 'payments'</c>.</param>
/// <param name="Content">What the system observed for the test, as plain text.</param>
public sealed record FailureDetails(string Title, string Content);

/// <summary>Starts and stops the real application under test.</summary>
public interface IApplicationUnderTest : IAsyncDisposable
{
    /// <summary>
    /// Starts the application with the configuration collected from every system plus the per-application
    /// overrides, and returns once it is ready to serve.
    /// </summary>
    Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken);
}

/// <summary>A running application: its services and, for HTTP applications, its address.</summary>
public interface IApplicationContext
{
    /// <summary>The application's root service provider. Create a scope before resolving scoped services.</summary>
    IServiceProvider Services { get; }

    /// <summary>The HTTP endpoint. Non-HTTP hosts throw when this is accessed; use their Services instead.</summary>
    Uri BaseAddress { get; }
}
