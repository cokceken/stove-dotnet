namespace StoveDotnet;

/// <summary>Prepares a system before the application starts, e.g. creating tables or topics.</summary>
public interface IStoveMigration<in TContext>
{
    /// <summary>Migrations run in ascending order.</summary>
    int Order => 0;

    Task ExecuteAsync(TContext context, CancellationToken cancellationToken);
}

public sealed class MigrationCollection<TContext>
{
    private readonly List<IStoveMigration<TContext>> _migrations = [];

    public int Count => _migrations.Count;

    public MigrationCollection<TContext> Add(IStoveMigration<TContext> migration)
    {
        _migrations.Add(migration);
        return this;
    }

    public MigrationCollection<TContext> Add(Func<TContext, CancellationToken, Task> migration, int order = 0) =>
        Add(new DelegateMigration(migration, order));

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
