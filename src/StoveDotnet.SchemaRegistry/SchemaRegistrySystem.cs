using Confluent.SchemaRegistry;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StoveDotnet.Kafka;

namespace StoveDotnet.SchemaRegistry;

/// <summary>A registry that starts after Kafka and registers schemas before any application starts.</summary>
public sealed class SchemaRegistrySystem : ExposingSystem<SchemaRegistryOptions, SchemaRegistryExposedConfiguration>, IRunAware, ISystemDependencies
{
    private readonly KafkaSystem _kafka;
    private IContainer? _container;
    private CachedSchemaRegistryClient? _client;
    private int _disposed;

    public SchemaRegistrySystem(string? name, SchemaRegistryOptions options, KafkaSystem kafka) : base(name, options)
    {
        ArgumentNullException.ThrowIfNull(kafka);
        _kafka = kafka;
        if (options.ExistingUrl is null) kafka.NeedsRegistryNetwork = true;
    }

    public IReadOnlyCollection<IPluggedSystem> Dependencies => [_kafka];
    public ISchemaRegistryClient Client => _client ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var url = Options.ExistingUrl;
        if (url is null)
        {
            if (!_kafka.IsManaged)
                throw new InvalidOperationException("A managed Schema Registry requires managed Kafka. UseExisting on the registry when connecting to an existing broker.");
            var builder = new ContainerBuilder(Options.Image)
                .WithNetwork(_kafka.RegistryNetwork)
                .WithPortBinding(8081, true)
                .WithEnvironment("SCHEMA_REGISTRY_HOST_NAME", "stove-schema-registry")
                .WithEnvironment("SCHEMA_REGISTRY_LISTENERS", "http://0.0.0.0:8081")
                .WithEnvironment("SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS", _kafka.RegistryBootstrapServers)
                .WithEnvironment("SCHEMA_REGISTRY_KAFKASTORE_SECURITY_PROTOCOL", "PLAINTEXT")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8081).ForPath("subjects")));
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            url = new UriBuilder("http", _container.Hostname, _container.GetMappedPublicPort(8081)).Uri.ToString();
        }
        Expose(new SchemaRegistryExposedConfiguration(url));
        var config = new SchemaRegistryConfig { Url = url };
        Options.ConfigureClient?.Invoke(config);
        _client = new CachedSchemaRegistryClient(config);
        // Check readiness for existing registries too, before starting the application.
        await _client.GetAllSubjectsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Options.ExistingUrl is null || Options.RunMigrationsOnExisting)
            await Options.Migrations.RunAsync(new SchemaRegistryMigrationContext(Client, ExposedConfiguration), cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;
        return DisposeResourcesAsync(
            () => _client is not null && Options.Cleanup is not null ? new ValueTask(Options.Cleanup(_client, CancellationToken.None)) : ValueTask.CompletedTask,
            () => { _client?.Dispose(); return ValueTask.CompletedTask; },
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask);
    }
}

public static class SchemaRegistryStoveExtensions
{
    public static StoveBuilder WithSchemaRegistry(this StoveBuilder builder, Action<SchemaRegistryOptions>? configure = null) =>
        builder.WithSchemaRegistry(null, configure);

    /// <summary>Registers a registry for a previously registered Kafka instance.</summary>
    public static StoveBuilder WithSchemaRegistry(this StoveBuilder builder, string? name, Action<SchemaRegistryOptions>? configure = null, string? kafkaName = null)
    {
        var options = new SchemaRegistryOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new SchemaRegistrySystem(name, options, builder.GetSystem<KafkaSystem>(kafkaName)));
    }

    public static SchemaRegistrySystem SchemaRegistry(this StoveTestContext test, string? name = null) => test.GetSystem<SchemaRegistrySystem>(name);
}
