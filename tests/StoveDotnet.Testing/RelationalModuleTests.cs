using System.Data.Common;
using Xunit;

namespace StoveDotnet.Testing;

/// <summary>The same behavioral contract runs against both database providers.</summary>
public abstract class RelationalModuleTests(RelationalFixture fixture)
{
    [Fact]
    public async Task Migrations_finish_in_order_before_configuration_reaches_the_app()
    {
        var module = fixture.CreateModule("Orders");
        module.UseExisting(fixture.Module.ConnectionString);
        var table = "migration_" + Guid.NewGuid().ToString("N");
        module.Migrate($"insert into {table} values (1, 'seeded')", order: 2);
        module.Migrate($"create table {table} (id int primary key, value varchar(100))", order: 1);
        module.Cleanup = (connection, ct) => Execute(connection, $"drop table {table}", ct);
        var app = new ConfigurationProbe(async (configuration, ct) =>
        {
            Assert.Equal(module.ConnectionString, configuration["ConnectionStrings:Orders"]);
            await using var connection = await module.Open(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select value from {table}";
            Assert.Equal("seeded", await command.ExecuteScalarAsync(ct));
        });
        await using var stove = await module.Register(StoveBuilder.Create()).WithApplication(app)
            .StartAsync(TestContext.Current.CancellationToken);

        await stove.Test(async t =>
        {
            Assert.Equal(["seeded"], await module.Query(t, $"select value from {table}"));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Named_existing_connections_skip_migrations_and_leave_the_server_running_after_cleanup_failure()
    {
        var source = fixture.Module;
        var first = fixture.CreateModule("first");
        var second = fixture.CreateModule("second");
        first.UseExisting(source.ConnectionString, runMigrations: false);
        second.UseExisting(source.ConnectionString, runMigrations: false);
        first.Migrate("this migration must never execute");
        second.Migrate("this migration must never execute");
        var cleanupError = new InvalidOperationException("cleanup failed");
        var cleaned = 0;
        first.Cleanup = (_, _) => { cleaned++; throw cleanupError; };
        second.Cleanup = (_, _) => { cleaned++; return Task.CompletedTask; };
        var stove = await second.Register(first.Register(StoveBuilder.Create()))
            .StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await stove.Test(t =>
            {
                Assert.Same(first.System, first.Resolve(t));
                Assert.Same(second.System, second.Resolve(t));
                Assert.Throws<InvalidOperationException>(() => first.Resolve(t, unnamed: true));
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => stove.DisposeAsync().AsTask());
            Assert.Contains(cleanupError, error.Flatten().InnerExceptions);
        }

        await stove.DisposeAsync();
        Assert.Equal(2, cleaned);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.Open(TestContext.Current.CancellationToken));
        await using var connection = await source.Open(TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Native_clients_and_parameterized_dsl_work_on_managed_databases()
    {
        var module = fixture.Module;
        var table = "parameters_" + Guid.NewGuid().ToString("N");
        await using var connection = await module.Open(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = module.ApplicationNameQuery;
            Assert.Equal("stove-contract", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        await Execute(connection, $"create table {table} (id int primary key, value varchar(100))", TestContext.Current.CancellationToken);
        try
        {
            await fixture.Stove.Test(async t =>
            {
                const string value = "chair'; DROP TABLE products; --";
                Assert.Equal(1, await module.Insert(t, $"insert into {table} values (@id, @value)", value));
                await module.ShouldQuery(t, $"select value from {table} where id = @id", rows => Assert.Equal([value], rows));
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            await Execute(connection, $"drop table {table}", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Migration_failure_and_cleanup_failure_still_remove_the_managed_container()
    {
        var module = fixture.CreateModule();
        var startupError = new InvalidOperationException("migration failed");
        var cleanupError = new InvalidOperationException("cleanup failed");
        module.Migration = (_, _) => throw startupError;
        module.Cleanup = (_, _) => throw cleanupError;
        var error = await Assert.ThrowsAsync<AggregateException>(() => module.Register(StoveBuilder.Create())
            .StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(startupError, error.InnerExceptions[0]);
        Assert.Contains(cleanupError, error.Flatten().InnerExceptions);
        await module.System.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            // A fresh, unpooled client proves that the server is no longer reachable.
            await using var connection = module.CreateFreshConnection(module.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task Existing_database_is_validated_even_without_migrations()
    {
        var module = fixture.CreateModule();
        var existing = fixture.Module.ConnectionString;
        module.UseExisting(module.MissingDatabaseConnectionString(existing), runMigrations: false);
        var appStarted = false;
        var app = new ConfigurationProbe((_, _) => { appStarted = true; return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<DbException>(() => module.Register(StoveBuilder.Create()).WithApplication(app)
            .StartAsync(TestContext.Current.CancellationToken));
        Assert.False(appStarted);
    }

    [Fact]
    public async Task Cancellation_reaches_database_operations()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Stove.Test(async t =>
        {
            await fixture.Module.Query(t, "select 'cancelled'");
        }, cancellation.Token));
    }

    private static async Task Execute(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class ConfigurationProbe(Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task> start)
        : IApplicationUnderTest, IApplicationContext, IServiceProvider
    {
        public IServiceProvider Services => this;
        public Uri BaseAddress { get; } = new("http://localhost");
        public object? GetService(Type serviceType) => null;
        public async Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
        {
            await start(configuration, cancellationToken);
            return this;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

