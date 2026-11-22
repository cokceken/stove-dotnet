namespace StoveDotnet;

/// <summary>Marker for the connection details a system exposes once it is running.</summary>
public interface IExposedConfiguration;

/// <summary>Base options shared by every system that exposes configuration to the application.</summary>
public abstract class SystemOptions<TExposed> where TExposed : IExposedConfiguration
{
    /// <summary>
    /// Maps the running system's connection details to application configuration keys, e.g.
    /// <c>c => [new("ConnectionStrings:Orders", c.ConnectionString)]</c>.
    /// </summary>
    public Func<TExposed, IEnumerable<KeyValuePair<string, string?>>> ConfigureExposedConfiguration { get; set; } = _ => [];
}

/// <summary>Convenience base for systems that expose configuration after <see cref="IRunAware.RunAsync"/>.</summary>
public abstract class ExposingSystem<TOptions, TExposed> : IPluggedSystem, IExposesConfiguration
    where TOptions : SystemOptions<TExposed>
    where TExposed : IExposedConfiguration
{
    private TExposed? _exposed;

    /// <summary>
    /// Stores the instance name and the options. Call <see cref="Expose"/> once the system is running.
    /// </summary>
    protected ExposingSystem(string? name, TOptions options)
    {
        Name = name;
        Options = options;
    }

    /// <inheritdoc />
    public string? Name { get; }

    /// <summary>The options this system was registered with.</summary>
    public TOptions Options { get; }

    /// <summary>Connection details of the running system.</summary>
    public TExposed ExposedConfiguration =>
        _exposed ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    /// <summary>Type name and instance name, for error messages.</summary>
    protected string DisplayName => Name is null ? GetType().Name : $"{GetType().Name}('{Name}')";

    /// <summary>
    /// Publishes the connection details once the system is running, so they can be mapped into application
    /// configuration.
    /// </summary>
    protected void Expose(TExposed exposed) => _exposed = exposed;

    /// <inheritdoc />
    public IEnumerable<KeyValuePair<string, string?>> Configuration() =>
        Options.ConfigureExposedConfiguration(ExposedConfiguration);

    /// <summary>Attempts every cleanup/disposal step in order, then reports all failures together.</summary>
    protected static ValueTask DisposeResourcesAsync(params Func<ValueTask>[] actions) => SystemDisposal.RunAsync(actions);

    /// <summary>
    /// Releases clients and removes containers the system owns. Must be safe after a partial start and when called
    /// twice.
    /// </summary>
    public abstract ValueTask DisposeAsync();
}
