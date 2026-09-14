using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace StoveDotnet;

/// <summary>A running e2e test environment. Every e2e test runs through <see cref="Test"/>.</summary>
public sealed class Stove : IAsyncDisposable
{
    private readonly SystemRegistry _registry;
    private readonly IApplicationUnderTest? _applicationUnderTest;
    private readonly StoveOptions _options;
    private int _disposed;

    internal Stove(SystemRegistry registry, IApplicationUnderTest? applicationUnderTest, StoveOptions options)
    {
        _registry = registry;
        _applicationUnderTest = applicationUnderTest;
        _options = options;
    }

    /// <summary>The running application, or <c>null</c> when Stove runs without one.</summary>
    public IApplicationContext? Application { get; private set; }

    internal SystemRegistry Registry => _registry;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture synchronous throws too, so all in-flight starts finish before rollback disposes their resources.
        await Task.WhenAll(_registry.OfType<IRunAware>().Select(async s =>
            await s.RunAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);

        var configuration = CollectConfiguration();

        if (_applicationUnderTest is not null)
        {
            Application = await _applicationUnderTest.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(_registry.OfType<IAfterApplicationStarted>()
                .Select(async s => await s.OnApplicationStartedAsync(Application, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        }
    }

    private Dictionary<string, string?> CollectConfiguration()
    {
        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in _registry.OfType<IExposesConfiguration>())
        {
            foreach (var (key, value) in system.Configuration())
            {
                if (!configuration.TryAdd(key, value))
                {
                    throw new InvalidOperationException(
                        $"Configuration key '{key}' is exposed by more than one system. Map each system to distinct keys.");
                }
            }
        }

        return configuration;
    }

    /// <summary>
    /// Runs an e2e test. The test is named after the calling method; systems are reachable through the
    /// <see cref="StoveTestContext"/> passed to <paramref name="body"/>. When the body fails, the error is rethrown as a
    /// <see cref="StoveTestFailedException"/> enriched with what Stove observed (traces, logs, messages).
    /// </summary>
    public async Task Test(
        Func<StoveTestContext, Task> body,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string testMethod = "",
        [CallerFilePath] string testFile = "")
    {
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        if (StoveTestContext.Current is not null)
        {
            throw new InvalidOperationException("Stove tests cannot be nested.");
        }

        var test = new StoveTestContext(this, TestName(testFile, testMethod), cancellationToken);
        StoveTestContext.Current = test;
        try
        {
            await RunTest(test, body).ConfigureAwait(false);
        }
        finally
        {
            StoveTestContext.Current = null;
        }
    }

    private async Task RunTest(StoveTestContext test, Func<StoveTestContext, Task> body)
    {
        Exception? error = null;
        try
        {
            foreach (var aware in _registry.OfType<ITestScopeAware>())
            {
                await aware.OnTestStartedAsync(test).ConfigureAwait(false);
            }

            // Anything the body calls directly (a raw HttpClient, an SDK client, app services via Using<T>) is observed
            // by the in-process application's instrumentation; keep those calls inside the test's trace.
            using var activity = test.StartCorrelatedActivity("stove.test");
            await body(test).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Exception? endError = null;
        foreach (var aware in _registry.OfType<ITestScopeAware>())
        {
            try
            {
                await aware.OnTestEndedAsync(test, error).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                endError ??= ex;
            }
        }

        if (error is not null)
        {
            if (IsPassThrough(error, test.CancellationToken))
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            var details = await CollectFailureDetails(test).ConfigureAwait(false);
            throw new StoveTestFailedException(test, error, details);
        }

        if (endError is not null)
        {
            ExceptionDispatchInfo.Capture(endError).Throw();
        }
    }

    private async Task<IReadOnlyList<FailureDetails>> CollectFailureDetails(StoveTestContext test)
    {
        using var timeout = new CancellationTokenSource(_options.FailureDetailsTimeout);
        var details = new List<FailureDetails>();
        foreach (var provider in _registry.OfType<IFailureDetailsProvider>())
        {
            try
            {
                var detail = await provider.DescribeAsync(test, timeout.Token).ConfigureAwait(false);
                if (detail is not null)
                {
                    details.Add(detail);
                }
            }
            catch (Exception ex)
            {
                details.Add(new FailureDetails(provider.GetType().Name, $"(failed to collect details: {ex.Message})"));
            }
        }

        return details;
    }

    private static readonly HashSet<string> PassThroughExceptionNames =
    [
        "Xunit.Sdk.SkipException",
        "Xunit.SkipException",
        "NUnit.Framework.IgnoreException",
        "NUnit.Framework.InconclusiveException",
        "NUnit.Framework.SuccessException",
        "Microsoft.VisualStudio.TestTools.UnitTesting.AssertInconclusiveException",
        "TUnit.Core.Exceptions.SkipTestException",
    ];

    /// <summary>Framework control-flow exceptions (skip, inconclusive, pass) and cancellation are rethrown untouched.</summary>
    internal static bool IsPassThrough(Exception error, CancellationToken cancellationToken)
    {
        if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        for (var type = error.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName is { } name && PassThroughExceptionNames.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    internal static string TestName(string testFile, string testMethod)
    {
        var fileName = Path.GetFileNameWithoutExtension(testFile.Replace('\\', '/').Split('/')[^1]);
        return string.IsNullOrEmpty(fileName) ? testMethod : $"{fileName}.{testMethod}";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var actions = new List<Func<ValueTask>>();
        if (_applicationUnderTest is not null)
        {
            actions.Add(_applicationUnderTest.DisposeAsync);
        }

        foreach (var system in _registry.All.Reverse())
        {
            actions.Add(system.DisposeAsync);
        }

        await SystemDisposal.RunAsync(actions).ConfigureAwait(false);
    }
}

public sealed class StoveTestFailedException : Exception
{
    internal StoveTestFailedException(StoveTestContext test, Exception error, IReadOnlyList<FailureDetails> details)
        : base(BuildMessage(test, error, details), error)
    {
        TestName = test.TestName;
        TraceId = test.TraceId;
        Details = details;
    }

    public string TestName { get; }

    public string TraceId { get; }

    public IReadOnlyList<FailureDetails> Details { get; }

    private static string BuildMessage(StoveTestContext test, Exception error, IReadOnlyList<FailureDetails> details)
    {
        var message = new StringBuilder()
            .Append(error.GetType().Name).Append(": ").AppendLine(error.Message)
            .AppendLine()
            .Append("Stove test: ").AppendLine(test.TestName)
            .Append("Trace id:   ").AppendLine(test.TraceId);

        foreach (var detail in details)
        {
            message.AppendLine().Append("--- Stove: ").Append(detail.Title).AppendLine(" ---").AppendLine(detail.Content);
        }

        return message.ToString();
    }
}
