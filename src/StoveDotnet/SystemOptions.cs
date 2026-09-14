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

    protected ExposingSystem(string? name, TOptions options)
    {
        Name = name;
        Options = options;
    }

    public string? Name { get; }

    public TOptions Options { get; }

    /// <summary>Connection details of the running system.</summary>
    public TExposed ExposedConfiguration =>
        _exposed ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    protected string DisplayName => Name is null ? GetType().Name : $"{GetType().Name}('{Name}')";

    protected void Expose(TExposed exposed) => _exposed = exposed;

    public IEnumerable<KeyValuePair<string, string?>> Configuration() =>
        Options.ConfigureExposedConfiguration(ExposedConfiguration);

    /// <summary>Attempts every cleanup/disposal step in order, then reports all failures together.</summary>
    protected static ValueTask DisposeResourcesAsync(params Func<ValueTask>[] actions) => SystemDisposal.RunAsync(actions);

    public abstract ValueTask DisposeAsync();
}
