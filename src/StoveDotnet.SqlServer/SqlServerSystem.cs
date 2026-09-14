using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace StoveDotnet.SqlServer;

public sealed record SqlServerExposedConfiguration(string ConnectionString, string Server, string Database) : IExposedConfiguration;

/// <summary>Opens native connections for migrations and cleanup. The caller disposes each connection.</summary>
public sealed record SqlServerMigrationContext(
    Func<CancellationToken, Task<SqlConnection>> OpenConnectionAsync,
    SqlServerExposedConfiguration Configuration);

public sealed class SqlServerOptions : SystemOptions<SqlServerExposedConfiguration>
{
    public string Image { get; set; } = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>Database created in the managed container. Existing instances use their connection string's database.</summary>
    public string Database { get; set; } = "stove";

    public string Password { get; set; } = "Stove_password123!";

    public Func<MsSqlBuilder, MsSqlBuilder>? ConfigureContainer { get; set; }

    /// <summary>Configures each native connection before opening it, e.g. an access-token callback.</summary>
    public Action<SqlConnection>? ConfigureConnection { get; set; }

    /// <summary>Run before the application starts, in ascending order.</summary>
    public MigrationCollection<SqlServerMigrationContext> Migrations { get; } = new();

    /// <summary>Runs when Stove stops, including for existing instances. Connections opened by the callback must be disposed.</summary>
    public Func<SqlServerMigrationContext, CancellationToken, Task>? Cleanup { get; set; }

    internal string? ExistingConnectionString { get; private set; }

    internal bool RunMigrationsOnExisting { get; private set; } = true;

    /// <summary>Connects to an existing database without creating it or owning its server.</summary>
    public SqlServerOptions UseExisting(string connectionString, bool runMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ExistingConnectionString = connectionString;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}

/// <summary>A real SQL Server database, with parameterized operations and native SqlConnection access.</summary>
public sealed class SqlServerSystem : ExposingSystem<SqlServerOptions, SqlServerExposedConfiguration>, IRunAware
{
    private MsSqlContainer? _container;
    private SqlServerMigrationContext? _context;
    private int _disposed;

    public SqlServerSystem(string? name, SqlServerOptions options) : base(name, options)
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string connectionString;
        if (Options.ExistingConnectionString is { } existing)
        {
            connectionString = existing;
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Options.Database);
            if (Options.Database.Length > 128)
            {
                throw new InvalidOperationException("SQL Server database names must be at most 128 characters.");
            }

            var builder = new MsSqlBuilder(Options.Image).WithPassword(Options.Password);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            var csb = new SqlConnectionStringBuilder(_container.GetConnectionString());
            await using (var connection = new SqlConnection(csb.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC(@sql); END";
                command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = Options.Database });
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            csb.InitialCatalog = Options.Database;
            connectionString = csb.ConnectionString;
        }

        var configuration = new SqlConnectionStringBuilder(connectionString);
        var exposed = new SqlServerExposedConfiguration(connectionString, configuration.DataSource, configuration.InitialCatalog);
        // Validate authentication and the selected database before exposing configuration or starting the app.
        await using (var connection = await OpenConnection(connectionString, cancellationToken).ConfigureAwait(false))
        {
        }

        Expose(exposed);
        _context = new SqlServerMigrationContext(ct => OpenConnection(connectionString, ct), exposed);
        if (Options.ExistingConnectionString is null || Options.RunMigrationsOnExisting)
        {
            await Options.Migrations.RunAsync(_context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a native connection for transactions, bulk copy or other provider operations. The caller disposes it.</summary>
    public Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        return OpenConnection(ExposedConfiguration.ConnectionString, cancellationToken);
    }

    private async Task<SqlConnection> OpenConnection(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            Options.ConfigureConnection?.Invoke(connection);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> Execute(string sql, params SqlParameter[] parameters)
    {
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.sqlserver");
        await using var connection = await OpenConnectionAsync(test.CancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(test.CancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<T>> Query<T>(string sql, Func<SqlDataReader, T> map, params SqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(map);
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.sqlserver");
        await using var connection = await OpenConnectionAsync(test.CancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(test.CancellationToken).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(test.CancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>Queries once and passes the mapped rows to your assertion library.</summary>
    public async Task ShouldQuery<T>(string sql, Func<SqlDataReader, T> map, Action<IReadOnlyList<T>> assert, params SqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(await Query(sql, map, parameters).ConfigureAwait(false));
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await DisposeResourcesAsync(
            () => _context is not null && Options.Cleanup is not null
                ? new ValueTask(Options.Cleanup(_context, CancellationToken.None)) : ValueTask.CompletedTask,
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }
}

public static class SqlServerStoveExtensions
{
    public static StoveBuilder WithSqlServer(this StoveBuilder builder, Action<SqlServerOptions>? configure = null) =>
        builder.WithSqlServer(name: null, configure);

    public static StoveBuilder WithSqlServer(this StoveBuilder builder, string? name, Action<SqlServerOptions>? configure = null)
    {
        var options = new SqlServerOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new SqlServerSystem(name, options));
    }

    public static SqlServerSystem SqlServer(this StoveTestContext test, string? name = null) => test.GetSystem<SqlServerSystem>(name);
}
