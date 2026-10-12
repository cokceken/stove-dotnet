namespace StoveDotnet;

internal static class SystemStartup
{
    public static IReadOnlyList<IPluggedSystem> Order(IReadOnlyList<IPluggedSystem> systems)
    {
        var ordered = new List<IPluggedSystem>();
        var visited = new HashSet<IPluggedSystem>();
        var visiting = new HashSet<IPluggedSystem>();
        foreach (var system in systems) Visit(system);
        return ordered;

        void Visit(IPluggedSystem system)
        {
            if (visited.Contains(system)) return;
            if (!systems.Contains(system)) throw new InvalidOperationException($"Dependency {system.GetType().Name} is not registered.");
            if (!visiting.Add(system)) throw new InvalidOperationException($"System dependency cycle at {system.GetType().Name}.");
            if (system is ISystemDependencies dependent)
                foreach (var dependency in dependent.Dependencies) Visit(dependency);
            visiting.Remove(system);
            visited.Add(system);
            ordered.Add(system);
        }
    }
}
