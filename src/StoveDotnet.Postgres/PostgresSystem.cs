using Npgsql;
using Testcontainers.PostgreSql;

namespace StoveDotnet.Postgres;

public sealed record PostgresExposedConfiguration(
    string ConnectionString,
    string Host,
    int Port,
    string Database,
    string Username,
    string Password) : IExposedConfiguration;

public sealed record PostgresMigrationContext(NpgsqlDataSource DataSource, PostgresExposedConfiguration Configuration);

public sealed class PostgresOptions : SystemOptions<PostgresExposedConfiguration>
{
    public string Image { get; set; } = "postgres:17-alpine";

    public string Database { get; set; } = "stove";

    public string Username { get; set; } = "stove";

    public string Password { get; set; } = "stove";

    /// <summary>Further container customization, e.g. <c>b => b.WithCommand("-c", "max_connections=200")</c>.</summary>
    public Func<PostgreSqlBuilder, PostgreSqlBuilder>? ConfigureContainer { get; set; }

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

    public PostgresSystem(string? name, PostgresOptions options)
        : base(name, options)
    {
    }

    /// <summary>Raw Npgsql access for anything the DSL does not cover.</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

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
        _dataSource = NpgsqlDataSource.Create(connectionString);
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

    /// <summary>Runs a query, maps every row and hands the rows to <paramref name="assert"/>.</summary>
    public async Task ShouldQuery<T>(string sql, Func<NpgsqlDataReader, T> map, Action<IReadOnlyList<T>> assert, params NpgsqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(await Query(sql, map, parameters).ConfigureAwait(false));
    }

    private NpgsqlCommand CreateCommand(string sql, NpgsqlParameter[] parameters)
    {
        var command = DataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return command;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            if (Options.Cleanup is not null)
            {
                await Options.Cleanup(_dataSource, CancellationToken.None).ConfigureAwait(false);
            }

            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public static class PostgresStoveExtensions
{
    public static StoveBuilder WithPostgres(this StoveBuilder builder, Action<PostgresOptions>? configure = null) =>
        builder.WithPostgres(name: null, configure);

    public static StoveBuilder WithPostgres(this StoveBuilder builder, string? name, Action<PostgresOptions>? configure = null)
    {
        var options = new PostgresOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new PostgresSystem(name, options));
    }

    public static PostgresSystem Postgres(this StoveTestContext test, string? name = null) => test.GetSystem<PostgresSystem>(name);
}
