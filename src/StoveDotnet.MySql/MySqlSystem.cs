using MySqlConnector;
using Testcontainers.MySql;

namespace StoveDotnet.MySql;

public sealed record MySqlExposedConfiguration(
    string ConnectionString,
    string Host,
    int Port,
    string Database,
    string Username,
    string Password) : IExposedConfiguration;

public sealed record MySqlMigrationContext(MySqlDataSource DataSource, MySqlExposedConfiguration Configuration);

public sealed class MySqlOptions : SystemOptions<MySqlExposedConfiguration>
{
    public string Image { get; set; } = "mysql:8.4";

    public string Database { get; set; } = "stove";

    public string Username { get; set; } = "stove";

    public string Password { get; set; } = "stove";

    /// <summary>Further container customization, e.g. <c>b => b.WithCommand("--max-connections=200")</c>.</summary>
    public Func<MySqlBuilder, MySqlBuilder>? ConfigureContainer { get; set; }

    /// <summary>Configures Stove's native client (e.g. authentication or connection callbacks) before it is built.</summary>
    public Action<MySqlDataSourceBuilder>? ConfigureDataSource { get; set; }

    /// <summary>Run before the application starts, in ascending order.</summary>
    public MigrationCollection<MySqlMigrationContext> Migrations { get; } = new();

    /// <summary>Runs when Stove stops, before the container is removed.</summary>
    public Func<MySqlDataSource, CancellationToken, Task>? Cleanup { get; set; }

    internal string? ExistingConnectionString { get; private set; }

    internal bool RunMigrationsOnExisting { get; private set; } = true;

    /// <summary>Uses an already running MySQL (e.g. a CI service) instead of starting a container.</summary>
    public MySqlOptions UseExisting(string connectionString, bool runMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ExistingConnectionString = connectionString;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}

/// <summary>A real MySQL database for the application under test, with native MySqlConnector access for tests.</summary>
public sealed class MySqlSystem : ExposingSystem<MySqlOptions, MySqlExposedConfiguration>, IRunAware
{
    private MySqlContainer? _container;
    private MySqlDataSource? _dataSource;
    private int _disposed;

    public MySqlSystem(string? name, MySqlOptions options)
        : base(name, options)
    {
    }

    /// <summary>Raw MySqlConnector access for anything the DSL does not cover.</summary>
    public MySqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string connectionString;
        var runMigrations = true;
        if (Options.ExistingConnectionString is { } existing)
        {
            connectionString = existing;
            runMigrations = Options.RunMigrationsOnExisting;
        }
        else
        {
            var builder = new MySqlBuilder(Options.Image)
                .WithDatabase(Options.Database)
                .WithUsername(Options.Username)
                .WithPassword(Options.Password);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            connectionString = _container.GetConnectionString();
        }

        var csb = new MySqlConnectionStringBuilder(connectionString);
        var exposed = new MySqlExposedConfiguration(
            connectionString, csb.Server, checked((int)csb.Port), csb.Database ?? string.Empty, csb.UserID ?? string.Empty, csb.Password ?? string.Empty);
        var dataSourceBuilder = new MySqlDataSourceBuilder(connectionString);
        Options.ConfigureDataSource?.Invoke(dataSourceBuilder);
        _dataSource = dataSourceBuilder.Build();
        // Creating a data source is lazy. Verify connectivity even when no migrations were supplied.
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        Expose(exposed);

        if (runMigrations)
        {
            await Options.Migrations.RunAsync(new MySqlMigrationContext(_dataSource, exposed), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Executes a statement (e.g. seeding test data) and returns the number of affected rows.</summary>
    public async Task<int> Execute(string sql, params MySqlParameter[] parameters)
    {
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.mysql");
        await using var connection = await DataSource.OpenConnectionAsync(test.CancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(test.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a query and maps every row.</summary>
    public async Task<IReadOnlyList<T>> Query<T>(string sql, Func<MySqlDataReader, T> map, params MySqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(map);
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.mysql");
        await using var connection = await DataSource.OpenConnectionAsync(test.CancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(test.CancellationToken).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(test.CancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>Runs a query, maps every row and hands the rows to <paramref name="assert"/>.</summary>
    public async Task ShouldQuery<T>(string sql, Func<MySqlDataReader, T> map, Action<IReadOnlyList<T>> assert, params MySqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(await Query(sql, map, parameters).ConfigureAwait(false));
    }

    private static MySqlCommand CreateCommand(MySqlConnection connection, string sql, MySqlParameter[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return command;
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await DisposeResourcesAsync(
            () => _dataSource is not null && Options.Cleanup is not null
                ? new ValueTask(Options.Cleanup(_dataSource, CancellationToken.None)) : ValueTask.CompletedTask,
            () => _dataSource?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }
}

public static class MySqlStoveExtensions
{
    public static StoveBuilder WithMySql(this StoveBuilder builder, Action<MySqlOptions>? configure = null) =>
        builder.WithMySql(name: null, configure);

    public static StoveBuilder WithMySql(this StoveBuilder builder, string? name, Action<MySqlOptions>? configure = null)
    {
        var options = new MySqlOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new MySqlSystem(name, options));
    }

    public static MySqlSystem MySql(this StoveTestContext test, string? name = null) => test.GetSystem<MySqlSystem>(name);
}
