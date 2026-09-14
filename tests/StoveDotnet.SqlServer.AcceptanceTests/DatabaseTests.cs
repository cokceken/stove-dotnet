using System.Data.Common;
using Microsoft.Data.SqlClient;
using StoveDotnet.AspNetCore;
using StoveDotnet.SqlServer;
using StoveDotnet.Testing;
using Xunit;

namespace StoveDotnet.SqlServer.AcceptanceTests;

public sealed class SqlServerFixture : RelationalFixture
{
    public override DatabaseModule CreateModule(string? name = null) => new SqlServerModule(name);
    public override StoveBuilder WithApplication(StoveBuilder builder) => builder.WithAspNetCoreApplication<Program>();
}

[Collection("SqlServer")]
public sealed class ApplicationTests(SqlServerFixture fixture) : RelationalApplicationTests(fixture);
[Collection("SqlServer")]
public sealed class ModuleTests(SqlServerFixture fixture) : RelationalModuleTests(fixture);

[CollectionDefinition("SqlServer")]
public sealed class DatabaseCollection : ICollectionFixture<SqlServerFixture>;

public sealed class SqlServerModule(string? name) : DatabaseModule
{
    private SqlServerSystem? _system;
    private SqlServerSystem Native => _system ??= new SqlServerSystem(name, CreateOptions());
    public override IPluggedSystem System => Native;
    public override string ConnectionString => Native.ExposedConfiguration.ConnectionString;
    public override string ApplicationNameQuery => "select APP_NAME()";
    private readonly List<(string Sql, int Order)> _migrations = [];
    private string? _existing;
    private bool _runMigrations = true;

    private SqlServerOptions CreateOptions()
    {
        var options = new SqlServerOptions();
        Configure(options);
        return options;
    }

    private void Configure(SqlServerOptions options)
    {
        options.ConfigureConnection = connection => connection.ConnectionString = new SqlConnectionStringBuilder(connection.ConnectionString) { ApplicationName = "stove-contract" }.ConnectionString;
        options.ConfigureExposedConfiguration = c => [new("ConnectionStrings:" + (name ?? "Orders"), c.ConnectionString)];
        if (_existing is not null) options.UseExisting(_existing, _runMigrations);
        options.Migrations.Add(async (ctx, ct) =>
        {
            if (Migration is null) return;
            await using var connection = await ctx.OpenConnectionAsync(ct);
            await Migration(connection, ct);
        });
        foreach (var (sql, order) in _migrations)
        {
            options.Migrations.Add(async (ctx, ct) =>
            {
                await using var connection = await ctx.OpenConnectionAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(ct);
            }, order);
        }
        options.Cleanup = async (ctx, ct) =>
        {
            if (Cleanup is null) return;
            await using var connection = await ctx.OpenConnectionAsync(ct);
            await Cleanup(connection, ct);
        };
    }

    public override StoveBuilder Register(StoveBuilder builder) => builder.WithSystem(Native);
    public override StoveBuilder RegisterPublic(StoveBuilder builder) => builder.WithSqlServer(name, Configure);
    public override void Bind(StoveTestContext test) => _system = test.SqlServer(name);
    public override void UseExisting(string connectionString, bool runMigrations = true)
    {
        _existing = connectionString;
        _runMigrations = runMigrations;
    }
    public override void Migrate(string sql, int order = 0) => _migrations.Add((sql, order));
    public override async Task<DbConnection> Open(CancellationToken ct) => await Native.OpenConnectionAsync(ct);
    public override DbConnection CreateFreshConnection(string connectionString) => new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectTimeout = 1 }.ConnectionString);
    public override string MissingDatabaseConnectionString(string connectionString) => new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "missing_" + Guid.NewGuid().ToString("N"), ConnectTimeout = 1 }.ConnectionString;
    public override IPluggedSystem Resolve(StoveTestContext t, bool unnamed = false) => t.SqlServer(unnamed ? null : name);
    public override Task<int> Insert(StoveTestContext t, string sql, string value) => t.SqlServer(name).Execute(sql, new SqlParameter("id", 1), new SqlParameter("value", value));
    public override Task<IReadOnlyList<string>> Query(StoveTestContext t, string sql) => t.SqlServer(name).Query(sql, r => r.GetString(0));
    public override Task ShouldQuery(StoveTestContext t, string sql, Action<IReadOnlyList<string>> assert) => t.SqlServer(name).ShouldQuery(sql, r => r.GetString(0), assert, new SqlParameter("id", 1));
    public override async Task Seed(StoveTestContext t, string id, string value) => await t.SqlServer(name).Execute("insert into records values (@id, @value)", new SqlParameter("id", id), new SqlParameter("value", value));
    public override Task<IReadOnlyList<string>> Read(StoveTestContext t, string id) => t.SqlServer(name).Query("select value from records where id = @id", r => r.GetString(0), new SqlParameter("id", id));
}
