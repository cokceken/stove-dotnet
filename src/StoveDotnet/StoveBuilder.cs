namespace StoveDotnet;

/// <summary>Describes the systems and the application that make up an e2e test environment.</summary>
public sealed class StoveBuilder
{
    private readonly SystemRegistry _registry = new();
    private IApplicationUnderTest? _application;

    private StoveBuilder()
    {
    }

    public static StoveBuilder Create() => new();

    public StoveOptions Options { get; } = new();

    /// <summary>Registers a system. Module packages expose friendlier <c>WithXxx</c> extension methods on top of this.</summary>
    public StoveBuilder WithSystem(IPluggedSystem system)
    {
        _registry.Add(system);
        return this;
    }

    /// <summary>Sets the application under test. Module packages expose friendlier extension methods on top of this.</summary>
    public StoveBuilder WithApplication(IApplicationUnderTest application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (_application is not null)
        {
            throw new InvalidOperationException("An application under test is already registered.");
        }

        _application = application;
        return this;
    }

    public StoveBuilder Configure(Action<StoveOptions> configure)
    {
        configure(Options);
        return this;
    }

    /// <summary>Starts every system, then the application, and returns the running <see cref="Stove"/>.</summary>
    public async Task<Stove> StartAsync(CancellationToken cancellationToken = default)
    {
        var stove = new Stove(_registry, _application, Options);
        try
        {
            await stove.StartAsync(cancellationToken).ConfigureAwait(false);
            return stove;
        }
        catch
        {
            await stove.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class StoveOptions
{
    /// <summary>How long failure details providers (e.g. telemetry) may wait for late data when a test fails.</summary>
    public TimeSpan FailureDetailsTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
