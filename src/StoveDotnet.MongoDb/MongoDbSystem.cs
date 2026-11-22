using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace StoveDotnet.MongoDb;

/// <summary>Connection details of the running MongoDB.</summary>
/// <param name="ConnectionString">Connection string of the running server, ready to map into application
/// configuration.</param>
/// <param name="Database">The database Stove uses and migrations run against.</param>
public sealed record MongoDbExposedConfiguration(string ConnectionString, string Database) : IExposedConfiguration;

/// <summary>What a MongoDB migration or cleanup callback receives.</summary>
/// <param name="Client">Stove's client.</param>
/// <param name="Database">The configured database.</param>
/// <param name="Configuration">Connection details of the running server.</param>
public sealed record MongoDbMigrationContext(IMongoClient Client, IMongoDatabase Database, MongoDbExposedConfiguration Configuration);

/// <summary>Options of the MongoDB module.</summary>
public sealed class MongoDbOptions : SystemOptions<MongoDbExposedConfiguration>
{
    /// <summary>Container image. Defaults to <c>mongo:8.0</c>.</summary>
    public string Image { get; set; } = "mongo:8.0";
    /// <summary>Database name. Defaults to <c>stove</c>; MongoDB creates it on first write.</summary>
    public string Database { get; set; } = "stove";

    /// <summary>Single-node replica set by default. Set null for standalone mode (no transactions).</summary>
    public string? ReplicaSet { get; set; } = "stove-rs";
    /// <summary>Further container customization.</summary>
    public Func<MongoDbBuilder, MongoDbBuilder>? ConfigureContainer { get; set; }
    /// <summary>
    /// Customizes Stove's own client before it connects. It does not configure the application's client.
    /// </summary>
    public Action<MongoClientSettings>? ConfigureClient { get; set; }

    /// <summary>Ordered collection/index/seed setup before application startup; reruns on each environment start.</summary>
    public MigrationCollection<MongoDbMigrationContext> Migrations { get; } = new();
    /// <summary>Runs when Stove stops, before the container is removed; also against an existing server.</summary>
    public Func<MongoDbMigrationContext, CancellationToken, Task>? Cleanup { get; set; }
    internal string? ExistingConnectionString { get; private set; }
    internal bool RunMigrationsOnExisting { get; private set; } = true;

    /// <summary>Uses an external endpoint and explicit database. Stove owns its client, never the external server.</summary>
    public MongoDbOptions UseExisting(string connectionString, string database, bool runMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ExistingConnectionString = connectionString;
        Database = database;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}

/// <summary>MongoDB with native filters and serialization. Documents are shared; tests must choose unique identifiers.</summary>
public sealed class MongoDbSystem(string? name, MongoDbOptions options)
    : ExposingSystem<MongoDbOptions, MongoDbExposedConfiguration>(name, options), IRunAware
{
    private MongoDbContainer? _container;
    private MongoClient? _client;
    private MongoDbMigrationContext? _context;
    private int _disposed;

    /// <summary>Stove's native client, for anything the DSL does not cover.</summary>
    public IMongoClient Client => _client ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");
    /// <summary>The configured database.</summary>
    public IMongoDatabase Database => _context?.Database ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");
    /// <summary>A typed collection of the configured database.</summary>
    public IMongoCollection<T> Collection<T>(string name, MongoCollectionSettings? settings = null) => Database.GetCollection<T>(name, settings);

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Database);
        var connectionString = Options.ExistingConnectionString;
        if (connectionString is null)
        {
            var builder = new MongoDbBuilder(Options.Image);
            if (Options.ReplicaSet is { } replicaSet) builder = builder.WithReplicaSet(replicaSet);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            connectionString = _container.GetConnectionString();
        }

        var settings = MongoClientSettings.FromConnectionString(connectionString);
        Options.ConfigureClient?.Invoke(settings);
        _client = new MongoClient(settings);
        var exposed = new MongoDbExposedConfiguration(connectionString, Options.Database);
        _context = new MongoDbMigrationContext(_client, _client.GetDatabase(Options.Database), exposed);
        await Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken).ConfigureAwait(false);
        Expose(exposed);
        if (Options.ExistingConnectionString is null || Options.RunMigrationsOnExisting)
            await Options.Migrations.RunAsync(_context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts one document as the running test.</summary>
    public async Task Insert<T>(string collection, T document)
    {
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.mongodb");
        await Collection<T>(collection).InsertOneAsync(document, cancellationToken: test.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the current matching documents once. Use native APIs for paging, projections and transactions.</summary>
    public async Task<IReadOnlyList<T>> Query<T>(string collection, FilterDefinition<T> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.mongodb");
        return await Collection<T>(collection).Find(filter).ToListAsync(test.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await DisposeResourcesAsync(
            () => _context is not null && Options.Cleanup is not null
                ? new ValueTask(Options.Cleanup(_context, CancellationToken.None)) : ValueTask.CompletedTask,
            () => { _client?.Dispose(); return ValueTask.CompletedTask; },
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }
}

/// <summary>Registers the module on a <see cref="StoveBuilder"/> and resolves it from a test context.</summary>
public static class MongoDbStoveExtensions
{
    /// <summary>Registers the default (unnamed) instance.</summary>
    public static StoveBuilder WithMongoDb(this StoveBuilder builder, Action<MongoDbOptions>? configure = null) => builder.WithMongoDb(null, configure);

    /// <summary>
    /// Registers a named instance, so several of the same kind can run side by side. Resolve it in a test by
    /// passing the same name to the accessor.
    /// </summary>
    public static StoveBuilder WithMongoDb(this StoveBuilder builder, string? name, Action<MongoDbOptions>? configure = null)
    {
        var options = new MongoDbOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new MongoDbSystem(name, options));
    }

    /// <summary>
    /// The system registered on the running environment: the instance with the given name; without a name, the
    /// unnamed instance or the only one registered.
    /// </summary>
    public static MongoDbSystem MongoDb(this StoveTestContext test, string? name = null) => test.GetSystem<MongoDbSystem>(name);
}
