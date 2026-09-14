using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace StoveDotnet.MongoDb;

public sealed record MongoDbExposedConfiguration(string ConnectionString, string Database) : IExposedConfiguration;

public sealed record MongoDbSetupContext(IMongoClient Client, IMongoDatabase Database, MongoDbExposedConfiguration Configuration);

public sealed class MongoDbOptions : SystemOptions<MongoDbExposedConfiguration>
{
    public string Image { get; set; } = "mongo:8.0";
    public string Database { get; set; } = "stove";

    /// <summary>Single-node replica set by default. Set null for standalone mode (no transactions).</summary>
    public string? ReplicaSet { get; set; } = "stove-rs";
    public Func<MongoDbBuilder, MongoDbBuilder>? ConfigureContainer { get; set; }
    public Action<MongoClientSettings>? ConfigureClient { get; set; }

    /// <summary>Ordered collection/index/seed setup before application startup; reruns on each environment start.</summary>
    public MigrationCollection<MongoDbSetupContext> Setup { get; } = new();
    public Func<MongoDbSetupContext, CancellationToken, Task>? Cleanup { get; set; }
    internal string? ExistingConnectionString { get; private set; }
    internal bool RunSetupOnExisting { get; private set; } = true;

    /// <summary>Uses an external endpoint and explicit database. Stove owns its client, never the external server.</summary>
    public MongoDbOptions UseExisting(string connectionString, string database, bool runSetup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ExistingConnectionString = connectionString;
        Database = database;
        RunSetupOnExisting = runSetup;
        return this;
    }
}

/// <summary>MongoDB with native filters and serialization. Documents are shared; tests must choose unique identifiers.</summary>
public sealed class MongoDbSystem(string? name, MongoDbOptions options)
    : ExposingSystem<MongoDbOptions, MongoDbExposedConfiguration>(name, options), IRunAware
{
    private MongoDbContainer? _container;
    private MongoClient? _client;
    private MongoDbSetupContext? _context;
    private int _disposed;

    public IMongoClient Client => _client ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");
    public IMongoDatabase Database => _context?.Database ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");
    public IMongoCollection<T> Collection<T>(string name, MongoCollectionSettings? settings = null) => Database.GetCollection<T>(name, settings);

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
        _context = new MongoDbSetupContext(_client, _client.GetDatabase(Options.Database), exposed);
        await Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken).ConfigureAwait(false);
        Expose(exposed);
        if (Options.ExistingConnectionString is null || Options.RunSetupOnExisting)
            await Options.Setup.RunAsync(_context, cancellationToken).ConfigureAwait(false);
    }

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

    public async Task ShouldQuery<T>(string collection, FilterDefinition<T> filter, Action<IReadOnlyList<T>> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        assert(await Query(collection, filter).ConfigureAwait(false));
    }

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

public static class MongoDbStoveExtensions
{
    public static StoveBuilder WithMongoDb(this StoveBuilder builder, Action<MongoDbOptions>? configure = null) => builder.WithMongoDb(null, configure);

    public static StoveBuilder WithMongoDb(this StoveBuilder builder, string? name, Action<MongoDbOptions>? configure = null)
    {
        var options = new MongoDbOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new MongoDbSystem(name, options));
    }

    public static MongoDbSystem MongoDb(this StoveTestContext test, string? name = null) => test.GetSystem<MongoDbSystem>(name);
}
