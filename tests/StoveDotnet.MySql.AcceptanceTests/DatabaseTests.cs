using System.Data.Common;
using MySqlConnector;
using StoveDotnet.AspNetCore;
using StoveDotnet.MySql;
using StoveDotnet.Testing;
using Xunit;

namespace StoveDotnet.MySql.AcceptanceTests;

public sealed class MySqlFixture : RelationalFixture
{
    public override DatabaseModule CreateModule(string? name = null) => new MySqlModule(name);
    public override StoveBuilder WithApplication(StoveBuilder builder) => builder.WithAspNetCoreApplication<Program>();
}

[Collection("MySql")]
public sealed class ApplicationTests(MySqlFixture fixture) : RelationalApplicationTests(fixture);
[Collection("MySql")]
public sealed class ModuleTests(MySqlFixture fixture) : RelationalModuleTests(fixture);

[CollectionDefinition("MySql")]
public sealed class DatabaseCollection : ICollectionFixture<MySqlFixture>;

public sealed class MySqlModule(string? name) : DatabaseModule
{
    private MySqlSystem? _system;
    private MySqlSystem Native => _system ??= new MySqlSystem(name, CreateOptions());
    public override IPluggedSystem System => Native;
    public override string ConnectionString => Native.ExposedConfiguration.ConnectionString;
    public override string ApplicationNameQuery => "select @stove_application_name";
    private readonly List<(string Sql, int Order)> _migrations = [];
    private string? _existing;
    private bool _runMigrations = true;

    private MySqlOptions CreateOptions()
    {
        var options = new MySqlOptions();
        Configure(options);
        return options;
    }

    private void Configure(MySqlOptions options)
    {
        options.ConfigureDataSource = builder =>
        {
            builder.ConnectionStringBuilder.AllowUserVariables = true;
            builder.UseConnectionOpenedCallback(async (context, ct) =>
            {
                await using var command = context.Connection.CreateCommand();
                command.CommandText = "set @stove_application_name = 'stove-contract'";
                await command.ExecuteNonQueryAsync(ct);
            });
        };
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
    public override StoveBuilder RegisterPublic(StoveBuilder builder) => builder.WithMySql(name, Configure);
    public override void Bind(StoveTestContext test) => _system = test.MySql(name);
    public override void UseExisting(string connectionString, bool runMigrations = true)
    {
        _existing = connectionString;
        _runMigrations = runMigrations;
    }
    public override void Migrate(string sql, int order = 0) => _migrations.Add((sql, order));
    public override async Task<DbConnection> Open(CancellationToken ct) => await Native.DataSource.OpenConnectionAsync(ct);
    public override DbConnection CreateFreshConnection(string connectionString) => new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectionTimeout = 1 }.ConnectionString);
    public override string MissingDatabaseConnectionString(string connectionString) => new MySqlConnectionStringBuilder(connectionString) { Database = "missing_" + Guid.NewGuid().ToString("N"), ConnectionTimeout = 1 }.ConnectionString;
    public override IPluggedSystem Resolve(StoveTestContext t, bool unnamed = false) => t.MySql(unnamed ? null : name);
    public override Task<int> Insert(StoveTestContext t, string sql, string value) => t.MySql(name).Execute(sql, new MySqlParameter("id", 1), new MySqlParameter("value", value));
    public override Task<IReadOnlyList<string>> Query(StoveTestContext t, string sql) => t.MySql(name).Query(sql, r => r.GetString(0));
    public override Task ShouldQuery(StoveTestContext t, string sql, Action<IReadOnlyList<string>> assert) => t.MySql(name).ShouldQuery(sql, r => r.GetString(0), assert, new MySqlParameter("id", 1));
    public override async Task Seed(StoveTestContext t, string id, string value) => await t.MySql(name).Execute("insert into records values (@id, @value)", new MySqlParameter("id", id), new MySqlParameter("value", value));
    public override Task<IReadOnlyList<string>> Read(StoveTestContext t, string id) => t.MySql(name).Query("select value from records where id = @id", r => r.GetString(0), new MySqlParameter("id", id));
}
