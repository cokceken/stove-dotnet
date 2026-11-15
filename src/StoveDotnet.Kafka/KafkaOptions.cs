using System.Text.Json;
using Confluent.Kafka;
using Testcontainers.Kafka;

namespace StoveDotnet.Kafka;

/// <summary>Connection details of the running Kafka.</summary>
/// <param name="BootstrapServers">Bootstrap servers, ready to map into application configuration.</param>
public sealed record KafkaExposedConfiguration(string BootstrapServers) : IExposedConfiguration
{
    /// <summary>TLS settings the application needs to connect, when the broker is secured.</summary>
    public KafkaSecurityOptions Security { get; init; } = new();
}

/// <summary>What a Kafka migration receives.</summary>
/// <param name="Admin">Stove's admin client, for creating topics.</param>
/// <param name="Configuration">Connection details of the running broker.</param>
public sealed record KafkaMigrationContext(IAdminClient Admin, KafkaExposedConfiguration Configuration);

/// <summary>Converts message values for <see cref="KafkaSystem.Publish{T}"/> and the <c>ShouldBe*</c> assertions.</summary>
public interface IStoveKafkaSerde
{
    /// <summary>Serializes a message value for publishing.</summary>
    byte[] Serialize(object value, Type type);

    /// <summary>Returns the deserialized value; throwing means the record cannot be the requested type.</summary>
    object? Deserialize(byte[] data, Type type);
}

/// <summary>Topic-aware asynchronous serialization, for formats backed by a schema registry.</summary>
public interface IStoveKafkaAsyncSerde
{
    /// <summary>Serializes a message value for the given topic.</summary>
    Task<byte[]> SerializeAsync<T>(T value, SerializationContext context, CancellationToken cancellationToken);
    /// <summary>
    /// Deserializes a record value read from the given topic; throwing means the record is not a <typeparamref
    /// name="T"/>.
    /// </summary>
    Task<T> DeserializeAsync<T>(byte[] data, SerializationContext context, CancellationToken cancellationToken);
}

/// <summary>JSON serialization with System.Text.Json; the default serde.</summary>
/// <param name="options">Serializer options. Match the application's naming policy and converters.</param>
public sealed class SystemTextJsonKafkaSerde(JsonSerializerOptions options) : IStoveKafkaSerde
{
    /// <summary>Creates the serde with web defaults (camel-case property names).</summary>
    public SystemTextJsonKafkaSerde()
        : this(new JsonSerializerOptions(JsonSerializerDefaults.Web))
    {
    }

    /// <inheritdoc />
    public byte[] Serialize(object value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type, options);

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type) => JsonSerializer.Deserialize(data, type, options);
}

/// <summary>Options of the Kafka module.</summary>
public sealed class KafkaOptions : SystemOptions<KafkaExposedConfiguration>
{
    /// <summary>TLS settings for the broker and for Stove's clients.</summary>
    public KafkaSecurityOptions Security { get; } = new();

    /// <summary>When set, takes precedence over the synchronous Serde.</summary>
    public IStoveKafkaAsyncSerde? AsyncSerde { get; set; }
    /// <summary>Bounds the messages retained per test and decides how uncorrelated messages are treated.</summary>
    public MessageObservationOptions Observation { get; } = new();

    /// <summary>Container image. Defaults to <c>apache/kafka:4.1.0</c>.</summary>
    public string Image { get; set; } = "apache/kafka:4.1.0";

    /// <summary>Further container customization.</summary>
    public Func<KafkaBuilder, KafkaBuilder>? ConfigureContainer { get; set; }

    /// <summary>
    /// Serializes published values and deserializes observed ones. Defaults to System.Text.Json with web defaults.
    /// </summary>
    public IStoveKafkaSerde Serde { get; set; } = new SystemTextJsonKafkaSerde();

    /// <summary>Default wait for <c>ShouldBe*</c> assertions.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum wait for authenticated broker metadata during startup.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Topics with these suffixes hold failed messages (<see cref="KafkaSystem.ShouldBeFailed{T}"/>).</summary>
    public IList<string> ErrorTopicSuffixes { get; } = [".error", ".DLT"];

    /// <summary>
    /// Consumer groups inspected by <see cref="KafkaSystem.ShouldBeConsumed{T}"/>. When empty, every group on the
    /// broker except Stove's own is inspected.
    /// </summary>
    public IList<string> ConsumerGroups { get; } = [];

    /// <summary>Topics Stove observes, as a librdkafka regex subscription. Internal topics are excluded by default.</summary>
    public string ObservedTopicsPattern { get; set; } = "^[^_].*";

    /// <summary>Extra client settings (e.g. security) applied to Stove's producer, observer consumer and admin client.</summary>
    public Action<ClientConfig>? ConfigureClient { get; set; }

    /// <summary>Run before the application starts, in ascending order; typically creates topics.</summary>
    public MigrationCollection<KafkaMigrationContext> Migrations { get; } = new();

    /// <summary>Runs when Stove stops, before the container is removed.</summary>
    public Func<IAdminClient, CancellationToken, Task>? Cleanup { get; set; }

    internal string? ExistingBootstrapServers { get; private set; }

    internal bool RunMigrationsOnExisting { get; private set; } = true;

    /// <summary>Uses an already running Kafka instead of starting a container.</summary>
    public KafkaOptions UseExisting(string bootstrapServers, bool runMigrations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);
        ExistingBootstrapServers = bootstrapServers;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}
