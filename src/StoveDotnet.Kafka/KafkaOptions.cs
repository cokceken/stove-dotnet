using System.Text.Json;
using Confluent.Kafka;
using Testcontainers.Kafka;

namespace StoveDotnet.Kafka;

public sealed record KafkaExposedConfiguration(string BootstrapServers) : IExposedConfiguration;

public sealed record KafkaMigrationContext(IAdminClient Admin, KafkaExposedConfiguration Configuration);

/// <summary>Converts message values for <see cref="KafkaSystem.Publish{T}"/> and the <c>ShouldBe*</c> assertions.</summary>
public interface IStoveKafkaSerde
{
    byte[] Serialize(object value, Type type);

    /// <summary>Returns the deserialized value; throwing means the record cannot be the requested type.</summary>
    object? Deserialize(byte[] data, Type type);
}

public sealed class SystemTextJsonKafkaSerde(JsonSerializerOptions options) : IStoveKafkaSerde
{
    public SystemTextJsonKafkaSerde()
        : this(new JsonSerializerOptions(JsonSerializerDefaults.Web))
    {
    }

    public byte[] Serialize(object value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type, options);

    public object? Deserialize(byte[] data, Type type) => JsonSerializer.Deserialize(data, type, options);
}

public sealed class KafkaOptions : SystemOptions<KafkaExposedConfiguration>
{
    public MessageObservationOptions Observation { get; } = new();

    public string Image { get; set; } = "apache/kafka:4.1.0";

    /// <summary>Further container customization.</summary>
    public Func<KafkaBuilder, KafkaBuilder>? ConfigureContainer { get; set; }

    public IStoveKafkaSerde Serde { get; set; } = new SystemTextJsonKafkaSerde();

    /// <summary>Default wait for <c>ShouldBe*</c> assertions.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);

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
