using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StoveDotnet.Hosting;

/// <summary>
/// Options of a Generic Host application under test, including per-application configuration and readiness from
/// <see cref="ApplicationOptions"/>.
/// </summary>
public sealed class HostApplicationOptions : ApplicationOptions
{
    /// <summary>
    /// How long the host may take to stop when the environment is disposed. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Runs the application's real Generic Host factory, without changing its services or process environment.</summary>
public sealed class HostApplicationUnderTest(
    Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<IHost>> factory,
    HostApplicationOptions options) : IApplicationUnderTest, IFailureDetailsProvider
{
    private readonly ApplicationLogCollector _logs = new();
    private IHost? _host;
    private int _disposed;

    /// <inheritdoc />
    public async Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
    {
        _host = await factory(configuration, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The application factory returned no host.");
        _host.Services.GetRequiredService<ILoggerFactory>().AddProvider(_logs);
        try
        {
            await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logs.AttachStartupLogs(ex);
            throw;
        }
        return new HostContext(_host.Services);
    }

    /// <inheritdoc />
    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken) => _logs.DescribeAsync(test, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _host is null) return;
        var errors = new List<Exception>();
        try
        {
            using var timeout = new CancellationTokenSource(options.ShutdownTimeout);
            await _host.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { errors.Add(ex); }
        try
        {
            if (_host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else _host.Dispose();
        }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("The Generic Host failed to stop or dispose.", errors);
    }

    private sealed record HostContext(IServiceProvider Services) : IApplicationContext
    {
        public Uri BaseAddress => throw new InvalidOperationException("A Generic Host application has no HTTP endpoint. Bind the HTTP client to an ASP.NET Core application.");
    }
}

/// <summary>Registers the module on a <see cref="StoveBuilder"/> and resolves it from a test context.</summary>
public static class HostStoveExtensions
{
    /// <summary>The factory must apply the supplied configuration before building the application's real host.</summary>
    public static StoveBuilder WithHostApplication(this StoveBuilder builder, string? name,
        Func<IReadOnlyDictionary<string, string?>, IHost> factory, Action<HostApplicationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return builder.WithHostApplication(name, (configuration, _) => Task.FromResult(factory(configuration)), configure);
    }

    /// <summary>
    /// Registers a named instance, so several of the same kind can run side by side. Resolve it in a test by
    /// passing the same name to the accessor.
    /// </summary>
    public static StoveBuilder WithHostApplication(this StoveBuilder builder, string? name,
        Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task<IHost>> factory, Action<HostApplicationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var options = new HostApplicationOptions();
        configure?.Invoke(options);
        if (options.ShutdownTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(configure), "Shutdown timeout must be positive.");
        return builder.WithApplication(name, new HostApplicationUnderTest(factory, options), options);
    }
}
