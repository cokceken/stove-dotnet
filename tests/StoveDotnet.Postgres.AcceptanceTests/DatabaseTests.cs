using System.Data.Common;
using Npgsql;
using StoveDotnet.AspNetCore;
using StoveDotnet.Postgres;
using StoveDotnet.Testing;
using Xunit;

namespace StoveDotnet.Postgres.AcceptanceTests;

public sealed class PostgresFixture : RelationalFixture
{
    public override DatabaseModule CreateModule(string? name = null) => new PostgresModule(name);
    public override StoveBuilder WithApplication(StoveBuilder builder) => builder.WithAspNetCoreApplication<Program>();
}

[Collection("Postgres")]
public sealed class ApplicationTests(PostgresFixture fixture) : RelationalApplicationTests(fixture);
[Collection("Postgres")]
public sealed class ModuleTests(PostgresFixture fixture) : RelationalModuleTests(fixture);

[CollectionDefinition("Postgres")]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresModule(string? name) : DatabaseModule
{
    private PostgresSystem? _system;
    private PostgresSystem Native => _system ??= new PostgresSystem(name, CreateOptions());
    public override IPluggedSystem System => Native;
    public override string ConnectionString => Native.ExposedConfiguration.ConnectionString;
    public override string ApplicationNameQuery => "select current_setting('application_name')";
    private readonly List<(string Sql, int Order)> _migrations = [];
    private string? _existing;
    private bool _runMigrations = true;

    private PostgresOptions CreateOptions()
    {
        var options = new PostgresOptions();
        Configure(options);
        return options;
    }

    private void Configure(PostgresOptions options)
    {
        options.ConfigureDataSource = builder => builder.ConnectionStringBuilder.ApplicationName = "stove-contract";
        options.ConfigureExposedConfiguration = c => [new("ConnectionStrings:" + (name ?? "Orders"), c.ConnectionString)];
        if (_existing is not null) options.UseExisting(_existing, _runMigrations);
        options.Migrations.Add(async (ctx, ct) =>
        {
            if (Migration is null) return;
            await using var connection = await ctx.DataSource.OpenConnectionAsync(ct);
            await Migration(connection, ct);
        });
        foreach (var (sql, order) in _migrations)
        {
            options.Migrations.Add(async (ctx, ct) =>
            {
                await using var connection = await ctx.DataSource.OpenConnectionAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(ct);
            }, order);
        }
        options.Cleanup = async (ds, ct) =>
        {
            if (Cleanup is null) return;
            await using var connection = await ds.OpenConnectionAsync(ct);
            await Cleanup(connection, ct);
        };
    }

    public override StoveBuilder Register(StoveBuilder builder) => builder.WithSystem(Native);
    public override StoveBuilder RegisterPublic(StoveBuilder builder) => builder.WithPostgres(name, Configure);
    public override void Bind(StoveTestContext test) => _system = test.Postgres(name);
    public override void UseExisting(string connectionString, bool runMigrations = true)
    {
        _existing = connectionString;
        _runMigrations = runMigrations;
    }
    public override void Migrate(string sql, int order = 0) => _migrations.Add((sql, order));
    public override async Task<DbConnection> Open(CancellationToken ct) => await Native.DataSource.OpenConnectionAsync(ct);
    public override DbConnection CreateFreshConnection(string connectionString) => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 1 }.ConnectionString);
    public override string MissingDatabaseConnectionString(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString) { Database = "missing_" + Guid.NewGuid().ToString("N"), Timeout = 1 }.ConnectionString;
    public override IPluggedSystem Resolve(StoveTestContext t, bool unnamed = false) => t.Postgres(unnamed ? null : name);
    public override Task<int> Insert(StoveTestContext t, string sql, string value) => t.Postgres(name).Execute(sql, new NpgsqlParameter("id", 1), new NpgsqlParameter("value", value));
    public override Task<IReadOnlyList<string>> Query(StoveTestContext t, string sql) => t.Postgres(name).Query(sql, r => r.GetString(0));
    public override Task ShouldQuery(StoveTestContext t, string sql, Action<IReadOnlyList<string>> assert) => t.Postgres(name).ShouldQuery(sql, r => r.GetString(0), assert, new NpgsqlParameter("id", 1));
    public override async Task Seed(StoveTestContext t, string id, string value) => await t.Postgres(name).Execute("insert into records values (@id, @value)", new NpgsqlParameter("id", id), new NpgsqlParameter("value", value));
    public override Task<IReadOnlyList<string>> Read(StoveTestContext t, string id) => t.Postgres(name).Query("select value from records where id = @id", r => r.GetString(0), new NpgsqlParameter("id", id));
}
