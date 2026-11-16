using Npgsql;
using Testcontainers.PostgreSql;

namespace StoveDotnet.Postgres;

/// <summary>Connection details of the running PostgreSQL.</summary>
/// <param name="ConnectionString">Connection string of the running server, ready to map into application
/// configuration.</param>
/// <param name="Host">Host name of the server.</param>
/// <param name="Port">Port of the server.</param>
/// <param name="Database">Database name.</param>
/// <param name="Username">User name.</param>
/// <param name="Password">Password.</param>
public sealed record PostgresExposedConfiguration(
    string ConnectionString,
    string Host,
    int Port,
    string Database,
    string Username,
    string Password) : IExposedConfiguration;

/// <summary>What a PostgreSQL migration receives.</summary>
/// <param name="DataSource">Stove's data source; open connections or create commands from it.</param>
/// <param name="Configuration">Connection details of the running server.</param>
public sealed record PostgresMigrationContext(NpgsqlDataSource DataSource, PostgresExposedConfiguration Configuration);

/// <summary>Options of the PostgreSQL module.</summary>
public sealed class PostgresOptions : SystemOptions<PostgresExposedConfiguration>
{
    /// <summary>Container image. Defaults to <c>postgres:17-alpine</c>.</summary>
    public string Image { get; set; } = "postgres:17-alpine";

    /// <summary>Database created in the container. Defaults to <c>stove</c>.</summary>
    public string Database { get; set; } = "stove";

    /// <summary>User created in the container. Defaults to <c>stove</c>.</summary>
    public string Username { get; set; } = "stove";

    /// <summary>Password of the container user. Defaults to <c>stove</c>.</summary>
    public string Password { get; set; } = "stove";

    /// <summary>Further container customization, e.g. <c>b => b.WithCommand("-c", "max_connections=200")</c>.</summary>
    public Func<PostgreSqlBuilder, PostgreSqlBuilder>? ConfigureContainer { get; set; }

    /// <summary>Configures Stove's native client (e.g. enum mappings, JSON or authentication) before it is built.</summary>
    public Action<NpgsqlDataSourceBuilder>? ConfigureDataSource { get; set; }

    /// <summary>Run before the application starts, in ascending order.</summary>
    public MigrationCollection<PostgresMigrationContext> Migrations { get; } = new();

    /// <summary>Runs when Stove stops, before the container is removed.</summary>
    public Func<NpgsqlDataSource, CancellationToken, Task>? Cleanup { get; set; }

    internal string? ExistingConnectionString { get; private set; }

    internal bool RunMigrationsOnExisting { get; private set; } = true;

    /// <summary>Uses an already running PostgreSQL (e.g. a CI service) instead of starting a container.</summary>
    public PostgresOptions UseExisting(string connectionString, bool runMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ExistingConnectionString = connectionString;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}

/// <summary>A real PostgreSQL database for the application under test, with raw Npgsql access for tests.</summary>
public sealed class PostgresSystem : ExposingSystem<PostgresOptions, PostgresExposedConfiguration>, IRunAware
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;
    private int _disposed;

    /// <summary>
    /// Creates the system. Tests normally register it through the builder extension, which also applies the options
    /// callback.
    /// </summary>
    public PostgresSystem(string? name, PostgresOptions options)
        : base(name, options)
    {
    }

    /// <summary>Raw Npgsql access for anything the DSL does not cover.</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    /// <inheritdoc />
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
            var builder = new PostgreSqlBuilder(Options.Image)
                .WithDatabase(Options.Database)
                .WithUsername(Options.Username)
                .WithPassword(Options.Password);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            connectionString = _container.GetConnectionString();
        }

        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        var exposed = new PostgresExposedConfiguration(
            connectionString, csb.Host ?? "localhost", csb.Port, csb.Database ?? string.Empty, csb.Username ?? string.Empty, csb.Password ?? string.Empty);
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        Options.ConfigureDataSource?.Invoke(dataSourceBuilder);
        _dataSource = dataSourceBuilder.Build();
        // Creating a data source is lazy. Verify connectivity even when no migrations were supplied.
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        Expose(exposed);

        if (runMigrations)
        {
            await Options.Migrations.RunAsync(new PostgresMigrationContext(_dataSource, exposed), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Executes a statement (e.g. seeding test data) and returns the number of affected rows.</summary>
    public async Task<int> Execute(string sql, params NpgsqlParameter[] parameters)
    {
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.postgres");
        await using var command = CreateCommand(sql, parameters);
        return await command.ExecuteNonQueryAsync(test.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a query and maps every row.</summary>
    public async Task<IReadOnlyList<T>> Query<T>(string sql, Func<NpgsqlDataReader, T> map, params NpgsqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(map);
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.postgres");
        await using var command = CreateCommand(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(test.CancellationToken).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(test.CancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private NpgsqlCommand CreateCommand(string sql, NpgsqlParameter[] parameters)
    {
        var command = DataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return command;
    }

    /// <inheritdoc />
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

/// <summary>Registers the module on a <see cref="StoveBuilder"/> and resolves it from a test context.</summary>
public static class PostgresStoveExtensions
{
    /// <summary>Registers the default (unnamed) instance.</summary>
    public static StoveBuilder WithPostgres(this StoveBuilder builder, Action<PostgresOptions>? configure = null) =>
        builder.WithPostgres(name: null, configure);

    /// <summary>
    /// Registers a named instance, so several of the same kind can run side by side. Resolve it in a test by
    /// passing the same name to the accessor.
    /// </summary>
    public static StoveBuilder WithPostgres(this StoveBuilder builder, string? name, Action<PostgresOptions>? configure = null)
    {
        var options = new PostgresOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new PostgresSystem(name, options));
    }

    /// <summary>
    /// The system registered on the running environment: the instance with the given name; without a name, the
    /// unnamed instance or the only one registered.
    /// </summary>
    public static PostgresSystem Postgres(this StoveTestContext test, string? name = null) => test.GetSystem<PostgresSystem>(name);
}
