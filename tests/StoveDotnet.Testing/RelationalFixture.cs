using System.Data.Common;
using StoveDotnet.Http;
using Xunit;

namespace StoveDotnet.Testing;

public abstract class DatabaseModule : IAsyncDisposable
{
    public abstract IPluggedSystem System { get; }
    public abstract string ConnectionString { get; }
    public abstract string ApplicationNameQuery { get; }
    public Func<DbConnection, CancellationToken, Task>? Migration { get; set; }
    public Func<DbConnection, CancellationToken, Task>? Cleanup { get; set; }
    public abstract StoveBuilder Register(StoveBuilder builder);
    public abstract StoveBuilder RegisterPublic(StoveBuilder builder);
    public abstract void Bind(StoveTestContext test);
    public abstract void UseExisting(string connectionString, bool runMigrations = true);
    public abstract void Migrate(string sql, int order = 0);
    public abstract Task<DbConnection> Open(CancellationToken ct);
    public abstract DbConnection CreateFreshConnection(string connectionString);
    public abstract string MissingDatabaseConnectionString(string connectionString);
    public abstract IPluggedSystem Resolve(StoveTestContext t, bool unnamed = false);
    public abstract Task<int> Insert(StoveTestContext t, string sql, string value);
    public abstract Task<IReadOnlyList<string>> Query(StoveTestContext t, string sql);
    public abstract Task ShouldQuery(StoveTestContext t, string sql, Action<IReadOnlyList<string>> assert);
    public abstract Task Seed(StoveTestContext t, string id, string value);
    public abstract Task<IReadOnlyList<string>> Read(StoveTestContext t, string id);
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return System.DisposeAsync();
    }
}

public abstract class RelationalFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;
    public DatabaseModule Module { get; private set; } = null!;
    public DatabaseModule Archive { get; private set; } = null!;
    public abstract DatabaseModule CreateModule(string? name = null);
    public abstract StoveBuilder WithApplication(StoveBuilder builder);

    public async ValueTask InitializeAsync()
    {
        Module = CreateModule("Primary");
        Archive = CreateModule("Archive");
        Prepare(Module);
        Prepare(Archive);
        Stove = await WithApplication(Archive.RegisterPublic(Module.RegisterPublic(StoveBuilder.Create())).WithHttpClient())
            .StartAsync(TestContext.Current.CancellationToken);
        await Stove.Test(t => { Module.Bind(t); Archive.Bind(t); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
    }

    private static void Prepare(DatabaseModule module)
    {
        // Register in reverse order to prove ordering; the real app reads this row during startup.
        module.Migrate("insert into records values ('startup', 'ready')", order: 2);
        module.Migrate("create table records (id varchar(36) primary key, value varchar(100) not null)", order: 1);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Stove is not null) await Stove.DisposeAsync();
    }
}
