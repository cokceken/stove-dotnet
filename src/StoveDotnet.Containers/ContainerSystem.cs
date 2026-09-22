using DotNet.Testcontainers.Containers;

namespace StoveDotnet.Containers;

/// <summary>Native access to resolved host/ports after container readiness and initialization.</summary>
public sealed record ContainerExposedConfiguration(IContainer Container) : IExposedConfiguration;

public sealed class ContainerOptions : SystemOptions<ContainerExposedConfiguration>
{
    /// <summary>Build a fresh, unstarted container. Stove starts and disposes the returned instance.
    /// Configure explicit readiness with the native builder's WithWaitStrategy.</summary>
    public Func<IContainer>? CreateContainer { get; set; }

    /// <summary>Optional service initialization after native readiness, before application configuration/startup.
    /// Own and dispose any temporary clients here. Forward the supplied startup token to I/O.</summary>
    public Func<IContainer, CancellationToken, Task>? InitializeAsync { get; set; }

    /// <summary>Opt-in container log excerpts on test failure. Logs are shared across tests, not correlated.</summary>
    public bool IncludeLogs { get; set; }
    public int MaxLogLength { get; set; } = 8192;
    /// <summary>Redacts combined stdout/stderr before truncation. Throwing redactors fail closed.
    /// This affects Stove's log excerpt only, not native exceptions, client logs or application logs.</summary>
    public Func<string, string>? RedactLogs { get; set; }
}

/// <summary>An owned dependency container. Does not infer protocols, credentials, client APIs or per-test isolation.</summary>
public sealed class ContainerSystem(string? name, ContainerOptions options)
    : ExposingSystem<ContainerOptions, ContainerExposedConfiguration>(name, options), IRunAware, IFailureDetailsProvider
{
    private IContainer? _container;
    private int _disposed;

    /// <summary>Native container owned by Stove. Callers must not stop/dispose it or enable reuse for shared infrastructure.</summary>
    public IContainer Container => _container ?? throw new InvalidOperationException($"{DisplayName} has not created a container yet.");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Options.MaxLogLength < 0) throw new InvalidOperationException("MaxLogLength must not be negative.");
        var create = Options.CreateContainer ?? throw new InvalidOperationException("CreateContainer must build a fresh, unstarted Testcontainers container.");
        var created = create() ?? throw new InvalidOperationException("CreateContainer returned null.");
        if (created.State != TestcontainersStates.Undefined)
            throw new InvalidOperationException("CreateContainer must return a fresh, unstarted container. Existing containers remain caller-owned.");
        _container = created;
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);
        if (Options.InitializeAsync is { } initialize)
            await initialize(_container, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Expose(new(_container));
    }

    public async Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        if (_container is null) return null;
        var content = $"State: {_container.State}";
        if (Options.IncludeLogs && Options.MaxLogLength > 0)
        {
            // Native retrieval buffers logs. Limit the emitted excerpt; never print env, commands or mapped secrets.
            string logs;
            try
            {
                var (stdout, stderr) = await _container.GetLogsAsync(ct: cancellationToken).ConfigureAwait(false);
                logs = stdout + Environment.NewLine + stderr;
            }
            catch (OperationCanceledException) { throw; }
            catch { logs = "[container logs unavailable]"; }
            try { logs = Options.RedactLogs is { } redact ? redact(logs) ?? "[redacted]" : logs; }
            catch { logs = "[redaction failed]"; }
            var limit = Math.Max(0, Options.MaxLogLength);
            if (logs.Length > limit) logs = "[truncated] …" + logs[^limit..];
            content += $"\nShared container logs (not test-correlated):\n{logs}";
        }
        return new FailureDetails($"container:{Name ?? "(default)"}", content);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_container is not null) await _container.DisposeAsync().ConfigureAwait(false);
    }
}

public static class ContainerStoveExtensions
{
    public static StoveBuilder WithContainer(this StoveBuilder builder, Action<ContainerOptions> configure) =>
        builder.WithContainer(null, configure);

    public static StoveBuilder WithContainer(this StoveBuilder builder, string? name, Action<ContainerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ContainerOptions();
        configure(options);
        ArgumentNullException.ThrowIfNull(options.CreateContainer);
        return builder.WithSystem(new ContainerSystem(name, options));
    }

    public static ContainerSystem Container(this StoveTestContext test, string? name = null) => test.GetSystem<ContainerSystem>(name);
}
