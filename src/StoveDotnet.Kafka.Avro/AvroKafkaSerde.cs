using System.Collections.Concurrent;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using StoveDotnet.SchemaRegistry;

namespace StoveDotnet.Kafka.Avro;

/// <summary>Confluent Avro wire-format values, including generated Apache Avro ISpecificRecord types.</summary>
public sealed class AvroKafkaSerde(Func<ISchemaRegistryClient> client, AvroSerializerConfig? serializerConfig = null, AvroDeserializerConfig? deserializerConfig = null) : IStoveKafkaAsyncSerde
{
    private readonly ConcurrentDictionary<Type, object> _serializers = new();
    private readonly ConcurrentDictionary<Type, object> _deserializers = new();

    public async Task<byte[]> SerializeAsync<T>(T value, SerializationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serializer = (AvroSerializer<T>)_serializers.GetOrAdd(typeof(T), _ => new AvroSerializer<T>(client(), serializerConfig ?? new AvroSerializerConfig { AutoRegisterSchemas = false }));
        return await serializer.SerializeAsync(value, context).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> DeserializeAsync<T>(byte[] data, SerializationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length < 5 || data[0] != 0) throw new InvalidDataException("Expected Confluent Avro wire format: magic byte 0 followed by a four-byte schema ID.");
        var deserializer = (AvroDeserializer<T>)_deserializers.GetOrAdd(typeof(T), _ => new AvroDeserializer<T>(client(), deserializerConfig));
        return await deserializer.DeserializeAsync(data, false, context).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class AvroKafkaStoveExtensions
{
    /// <summary>Uses registry-backed Avro values for Publish and all Kafka observation assertions. Register Kafka and the registry first.</summary>
    public static StoveBuilder WithKafkaAvro(this StoveBuilder builder, Action<AvroSerializerConfig>? configureSerializer = null,
        string? kafkaName = null, string? schemaRegistryName = null)
    {
        var registry = builder.GetSystem<SchemaRegistrySystem>(schemaRegistryName);
        var config = new AvroSerializerConfig { AutoRegisterSchemas = false };
        configureSerializer?.Invoke(config);
        builder.GetSystem<KafkaSystem>(kafkaName).Options.AsyncSerde = new AvroKafkaSerde(() => registry.Client, config);
        return builder;
    }

    /// <summary>Registers the schema from a generated Avro type under an explicit subject during setup.</summary>
    public static MigrationCollection<SchemaRegistryMigrationContext> AddAvro<T>(this MigrationCollection<SchemaRegistryMigrationContext> migrations,
        string subject, int order = 0) where T : global::Avro.Specific.ISpecificRecord, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return migrations.Add(async (context, cancellationToken) =>
        {
            var schema = new Schema(new T().Schema.ToString(), SchemaType.Avro);
            await context.Client.RegisterSchemaAsync(subject, schema).WaitAsync(cancellationToken).ConfigureAwait(false);
        }, order);
    }
}
