namespace StoveDotnet;

/// <summary>A system (dependency or capability) plugged into Stove, e.g. a database, a message broker or an HTTP client.</summary>
public interface IPluggedSystem : IAsyncDisposable
{
    /// <summary>Optional instance name. <c>null</c> is the default (unnamed) instance.</summary>
    string? Name { get; }
}

/// <summary>Systems that need to start before the application, e.g. containers. Run in parallel.</summary>
public interface IRunAware
{
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Systems that contribute configuration to the application under test.</summary>
public interface IExposesConfiguration
{
    IEnumerable<KeyValuePair<string, string?>> Configuration();
}

/// <summary>Systems that need access to the running application, e.g. its DI container or base address.</summary>
public interface IAfterApplicationStarted
{
    Task OnApplicationStartedAsync(IApplicationContext application, CancellationToken cancellationToken);
}

/// <summary>Systems that keep per-test state (stubs, collected telemetry, ...).</summary>
public interface ITestScopeAware
{
    Task OnTestStartedAsync(StoveTestContext test);

    Task OnTestEndedAsync(StoveTestContext test, Exception? failure);
}

/// <summary>Systems that can describe what they observed for a failed test (trace tree, logs, messages).</summary>
public interface IFailureDetailsProvider
{
    Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken);
}

public sealed record FailureDetails(string Title, string Content);

/// <summary>Starts and stops the real application under test.</summary>
public interface IApplicationUnderTest : IAsyncDisposable
{
    Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken);
}

public interface IApplicationContext
{
    IServiceProvider Services { get; }

    /// <summary>The HTTP endpoint. Non-HTTP hosts throw when this is accessed; use their Services instead.</summary>
    Uri BaseAddress { get; }
}
