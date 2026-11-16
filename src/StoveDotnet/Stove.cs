using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace StoveDotnet;

/// <summary>A running e2e test environment. Every e2e test runs through <see cref="Test"/>.</summary>
public sealed class Stove : IAsyncDisposable
{
    private readonly SystemRegistry _registry;
    private readonly IReadOnlyList<ApplicationRegistration> _applications;
    private readonly Dictionary<string, IApplicationContext> _contexts = new(StringComparer.Ordinal);
    private readonly StoveOptions _options;
    private int _disposed;
    private IReadOnlyList<IPluggedSystem>? _startupOrder;

    internal Stove(SystemRegistry registry, IReadOnlyList<ApplicationRegistration> applications, StoveOptions options)
    {
        _registry = registry;
        _applications = applications;
        _options = options;
    }

    /// <summary>The running application, or <c>null</c> when Stove runs without one.</summary>
    public IApplicationContext? Application => _contexts.Count == 0 ? null : GetApplication();

    /// <summary>Resolves an exact name, the unnamed default, or the only application. Ambiguous defaults fail.</summary>
    public IApplicationContext GetApplication(string? name = null)
    {
        if (_contexts.TryGetValue(name ?? "", out var context)) return context;
        if (name is null && _contexts.Count == 1) return _contexts.Values.Single();
        throw new InvalidOperationException(name is null
            ? "No default application is available. Specify an application name when multiple named applications are registered."
            : $"Application '{name}' is not available. Registered applications: {string.Join(", ", _contexts.Keys)}.");
    }

    internal SystemRegistry Registry => _registry;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _startupOrder = SystemStartup.Order(_registry.All);
        var starts = new Dictionary<IPluggedSystem, Task>();
        foreach (var system in _startupOrder)
        {
            var dependencies = system is ISystemDependencies dependent
                ? dependent.Dependencies.Select(d => starts[d]).ToArray() : [];
            starts.Add(system, StartSystem(system, dependencies, cancellationToken));
        }
        try
        {
            await Task.WhenAll(starts.Values).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Systems start in parallel: report every one that failed, not whichever task happened to be first.
            var failures = starts.Values.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions)
                .OfType<StoveStartupException>().ToList();
            if (failures.Count == 0) throw;
            throw failures.Count == 1 ? failures[0] : new StoveStartupException(failures);
        }

        var configuration = CollectConfiguration();

        foreach (var registration in _applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = new Dictionary<string, string?>(configuration, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in registration.Options.Configuration) local[pair.Key] = pair.Value;
            try
            {
                var context = await registration.Application.StartAsync(local, cancellationToken).ConfigureAwait(false);
                if (registration.Options.ReadyAsync is { } ready) await ready(context, cancellationToken).ConfigureAwait(false);
                _contexts.Add(registration.Name ?? "", context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoveStartupException(
                    registration.Name is null ? "The application" : $"Application '{registration.Name}'", "failed to start or become ready", ex);
            }
        }
        await Task.WhenAll(_registry.OfType<IAfterApplicationsStarted>()
            .Select(async s => await s.OnApplicationsStartedAsync(this, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private static async Task StartSystem(IPluggedSystem system, Task[] dependencies, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(dependencies).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dependency reports its own failure; this system never started.
            throw new DependencyNotStartedException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (system is IRunAware runnable) await runnable.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoveStartupException(
                system.Name is null ? system.GetType().Name : $"{system.GetType().Name} '{system.Name}'", "failed to start", ex);
        }
    }

    private sealed class DependencyNotStartedException : Exception;

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

        // Skips and cancellation produce no failure report, so systems must not hold evidence for one.
        var failure = error is not null && !IsPassThrough(error, test.CancellationToken) ? error : null;
        Exception? endError = null;
        foreach (var aware in _registry.OfType<ITestScopeAware>())
        {
            try
            {
                await aware.OnTestEndedAsync(test, failure).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                endError ??= ex;
            }
        }

        if (error is not null)
        {
            if (failure is null)
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
        foreach (var registration in _applications)
        {
            if (registration.Application is not IFailureDetailsProvider provider) continue;
            var title = $"application:{registration.Name ?? "(default)"}";
            try
            {
                var detail = await provider.DescribeAsync(test, timeout.Token).ConfigureAwait(false);
                if (detail is not null) details.Add(new FailureDetails($"{title} / {detail.Title}", detail.Content));
            }
            catch (Exception ex) { details.Add(new FailureDetails(title, $"(failed to collect details: {ex.Message})")); }
        }
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

    /// <summary>
    /// Stops the applications in reverse start order, then the systems in reverse dependency order. Every step is
    /// attempted; failures are reported together in an <see cref="AggregateException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var actions = new List<Func<ValueTask>>();
        foreach (var registration in _applications.Reverse())
        {
            actions.Add(async () =>
            {
                try { await registration.Application.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (registration.Name is not null)
                {
                    throw new InvalidOperationException($"Application '{registration.Name}' failed to stop.", ex);
                }
            });
        }

        foreach (var system in (_startupOrder ?? _registry.All).Reverse())
        {
            actions.Add(system.DisposeAsync);
        }

        await SystemDisposal.RunAsync(actions).ConfigureAwait(false);
    }
}

/// <summary>
/// A system or application failed while the environment was starting. The message names what failed and repeats the
/// cause; the original exception is the <see cref="Exception.InnerException"/>. When several systems fail in the
/// same parallel start, <see cref="Failures"/> holds one exception per system.
/// </summary>
public sealed class StoveStartupException : Exception
{
    /// <summary>
    /// An application adapter may store text (e.g. the application's startup logs) under this key in the
    /// <see cref="Exception.Data"/> of the exception it throws; it is appended to the message.
    /// </summary>
    public const string DetailsKey = "StoveDotnet.StartupDetails";

    internal StoveStartupException(string subject, string outcome, Exception cause)
        : base(BuildMessage(subject, outcome, cause), cause)
    {
        Failures = [this];
    }

    internal StoveStartupException(IReadOnlyList<StoveStartupException> failures)
        : base($"{failures.Count} systems failed to start:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Select(f => "- " + f.Message))}",
            failures[0].InnerException)
    {
        Failures = failures;
    }

    /// <summary>One entry per system or application that failed. A single failure contains itself.</summary>
    public IReadOnlyList<StoveStartupException> Failures { get; }

    private static string BuildMessage(string subject, string outcome, Exception cause)
    {
        var message = $"{subject} {outcome}: {cause.GetType().Name}: {cause.Message}";
        return cause.Data[DetailsKey] is string details && details.Length > 0
            ? $"{message}{Environment.NewLine}{details}" : message;
    }
}

/// <summary>
/// A test body failed inside <see cref="Stove.Test"/>. The message starts with the original failure and continues
/// with what each system observed for the test; the original exception is the inner exception.
/// </summary>
public sealed class StoveTestFailedException : Exception
{
    internal StoveTestFailedException(StoveTestContext test, Exception error, IReadOnlyList<FailureDetails> details)
        : base(BuildMessage(test, error, details), error)
    {
        TestName = test.TestName;
        TraceId = test.TraceId;
        Details = details;
    }

    /// <summary>Name of the failed test, derived from its file and method.</summary>
    public string TestName { get; }

    /// <summary>W3C trace id of the failed test, for finding it in logs and traces.</summary>
    public string TraceId { get; }

    /// <summary>The sections of the report, one per system or application that had something to say.</summary>
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
