using StackExchange.Redis;
using Testcontainers.Redis;

namespace StoveDotnet.Redis;

public sealed record RedisExposedConfiguration(string ConnectionString, string Host, int Port) : IExposedConfiguration;

public sealed record RedisMigrationContext(IConnectionMultiplexer Multiplexer, RedisExposedConfiguration Configuration);

public sealed class RedisOptions : SystemOptions<RedisExposedConfiguration>
{
    public string Image { get; set; } = "redis:7-alpine";

    /// <summary>Further container customization.</summary>
    public Func<RedisBuilder, RedisBuilder>? ConfigureContainer { get; set; }

    /// <summary>Run before the application starts, in ascending order.</summary>
    public MigrationCollection<RedisMigrationContext> Migrations { get; } = new();

    /// <summary>Runs when Stove stops, before the container is removed.</summary>
    public Func<IConnectionMultiplexer, CancellationToken, Task>? Cleanup { get; set; }

    internal string? ExistingConnectionString { get; private set; }

    internal bool RunMigrationsOnExisting { get; private set; } = true;

    /// <summary>Uses an already running Redis instead of starting a container.</summary>
    public RedisOptions UseExisting(string connectionString, bool runMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ExistingConnectionString = connectionString;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}

/// <summary>A real Redis for the application under test. Tests use the StackExchange.Redis client directly.</summary>
public sealed class RedisSystem : ExposingSystem<RedisOptions, RedisExposedConfiguration>, IRunAware
{
    private RedisContainer? _container;
    private ConnectionMultiplexer? _multiplexer;
    private int _disposed;

    public RedisSystem(string? name, RedisOptions options)
        : base(name, options)
    {
    }

    public IConnectionMultiplexer Multiplexer => _multiplexer ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    public IDatabase Database(int db = -1) => Multiplexer.GetDatabase(db);

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
            var builder = new RedisBuilder(Options.Image);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            connectionString = _container.GetConnectionString();
        }

        _multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
        var endpoint = ConfigurationOptions.Parse(connectionString).EndPoints.FirstOrDefault();
        var (host, port) = endpoint switch
        {
            System.Net.DnsEndPoint dns => (dns.Host, dns.Port),
            System.Net.IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            _ => ("localhost", 6379),
        };

        var exposed = new RedisExposedConfiguration(connectionString, host, port);
        Expose(exposed);

        if (runMigrations)
        {
            await Options.Migrations.RunAsync(new RedisMigrationContext(_multiplexer, exposed), cancellationToken).ConfigureAwait(false);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await DisposeResourcesAsync(
            () => _multiplexer is not null && Options.Cleanup is not null
                ? new ValueTask(Options.Cleanup(_multiplexer, CancellationToken.None)) : ValueTask.CompletedTask,
            () => _multiplexer?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }
}

public static class RedisStoveExtensions
{
    public static StoveBuilder WithRedis(this StoveBuilder builder, Action<RedisOptions>? configure = null) =>
        builder.WithRedis(name: null, configure);

    public static StoveBuilder WithRedis(this StoveBuilder builder, string? name, Action<RedisOptions>? configure = null)
    {
        var options = new RedisOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new RedisSystem(name, options));
    }

    public static RedisSystem Redis(this StoveTestContext test, string? name = null) => test.GetSystem<RedisSystem>(name);
}
