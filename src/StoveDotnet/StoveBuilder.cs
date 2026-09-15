namespace StoveDotnet;

/// <summary>Describes the systems and the application that make up an e2e test environment.</summary>
public sealed class StoveBuilder
{
    private readonly SystemRegistry _registry = new();
    private readonly List<ApplicationRegistration> _applications = [];

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
    public StoveBuilder WithApplication(IApplicationUnderTest application) => WithApplication(null, application);

    /// <summary>Registers an application. Applications start in registration order and stop in reverse order.</summary>
    public StoveBuilder WithApplication(string? name, IApplicationUnderTest application, ApplicationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (name is not null && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Application name must not be blank.", nameof(name));
        if (_applications.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"An application under test is already registered with name '{name ?? "(default)"}'.");
        }

        if (_applications.Any(a => ReferenceEquals(a.Application, application)))
            throw new InvalidOperationException("The same application instance cannot be registered twice.");
        _applications.Add(new ApplicationRegistration(name, application, options ?? new ApplicationOptions()));
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
        var stove = new Stove(_registry, _applications.ToArray(), Options);
        try
        {
            await stove.StartAsync(cancellationToken).ConfigureAwait(false);
            return stove;
        }
        catch (Exception startupError)
        {
            try
            {
                await stove.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Stove failed to start and rollback also failed. The first error is the startup failure.",
                    startupError, rollbackError);
            }

            throw;
        }
    }
}

public sealed class StoveOptions
{
    /// <summary>How long failure details providers (e.g. telemetry) may wait for late data when a test fails.</summary>
    public TimeSpan FailureDetailsTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
