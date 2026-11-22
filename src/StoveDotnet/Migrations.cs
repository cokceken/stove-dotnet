namespace StoveDotnet;

/// <summary>Prepares a system before the application starts, e.g. creating tables or topics.</summary>
public interface IStoveMigration<in TContext>
{
    /// <summary>Migrations run in ascending order.</summary>
    int Order => 0;

    /// <summary>
    /// Prepares the system. Runs once per environment start, before any application starts; there is no migration
    /// history.
    /// </summary>
    Task ExecuteAsync(TContext context, CancellationToken cancellationToken);
}

/// <summary>The ordered callbacks a module runs against its system before the application starts.</summary>
public sealed class MigrationCollection<TContext>
{
    private readonly List<IStoveMigration<TContext>> _migrations = [];

    /// <summary>Number of registered migrations.</summary>
    public int Count => _migrations.Count;

    /// <summary>
    /// Adds a migration object; its <see cref="IStoveMigration{TContext}.Order"/> decides when it runs.
    /// </summary>
    public MigrationCollection<TContext> Add(IStoveMigration<TContext> migration)
    {
        _migrations.Add(migration);
        return this;
    }

    /// <summary>
    /// Adds a callback. Lower <paramref name="order"/> runs first; callbacks with the same order run in the order
    /// they were added.
    /// </summary>
    public MigrationCollection<TContext> Add(Func<TContext, CancellationToken, Task> migration, int order = 0) =>
        Add(new DelegateMigration(migration, order));

    /// <summary>Runs the migrations in ascending order, stopping at the first failure.</summary>
    public async Task RunAsync(TContext context, CancellationToken cancellationToken)
    {
        foreach (var migration in _migrations.OrderBy(m => m.Order))
        {
            await migration.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class DelegateMigration(Func<TContext, CancellationToken, Task> migration, int order) : IStoveMigration<TContext>
    {
        public int Order => order;

        public Task ExecuteAsync(TContext context, CancellationToken cancellationToken) => migration(context, cancellationToken);
    }
}
