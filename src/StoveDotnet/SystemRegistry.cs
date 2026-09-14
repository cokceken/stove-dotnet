namespace StoveDotnet;

internal sealed class SystemRegistry
{
    private readonly List<IPluggedSystem> _systems = [];

    public IReadOnlyList<IPluggedSystem> All => _systems;

    public void Add(IPluggedSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var duplicate = _systems.Any(s => s.GetType() == system.GetType() && s.Name == system.Name);
        if (duplicate)
        {
            throw new InvalidOperationException(system.Name is null
                ? $"A default (unnamed) {system.GetType().Name} is already registered."
                : $"A {system.GetType().Name} named '{system.Name}' is already registered.");
        }

        _systems.Add(system);
    }

    public IEnumerable<T> OfType<T>() => _systems.OfType<T>();

    /// <summary>
    /// Resolves a system. With a name, the named instance is returned. Without a name, the unnamed instance is
    /// returned, or the only registered instance when there is exactly one.
    /// </summary>
    public T Resolve<T>(string? name) where T : class, IPluggedSystem
    {
        var candidates = _systems.OfType<T>().ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No {typeof(T).Name} is registered. Register it on the StoveBuilder before starting Stove.");
        }

        if (name is not null)
        {
            return candidates.FirstOrDefault(s => s.Name == name)
                ?? throw new InvalidOperationException(
                    $"No {typeof(T).Name} named '{name}' is registered. Available: {Describe(candidates)}.");
        }

        var unnamed = candidates.FirstOrDefault(s => s.Name is null);
        if (unnamed is not null)
        {
            return unnamed;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        throw new InvalidOperationException(
            $"Multiple {typeof(T).Name} instances are registered and none is unnamed; specify a name. Available: {Describe(candidates)}.");
    }

    private static string Describe<T>(IEnumerable<T> systems) where T : IPluggedSystem =>
        string.Join(", ", systems.Select(s => s.Name is null ? "(default)" : $"'{s.Name}'"));
}
