using System.Buffers.Binary;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using StoveDotnet.Kafka.Avro;
using StoveDotnet.SchemaRegistry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace StoveDotnet.Kafka.UnitTests;

public sealed class AvroTests
{
    [Fact]
    public async Task Missing_setup_schema_fails_without_registering_on_publish()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/subjects/orders-value").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("{\"error_code\":40403,\"message\":\"Schema not found\"}"));
        using var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = server.Url });
        var serde = new AvroKafkaSerde(() => client);
        await Assert.ThrowsAsync<SchemaRegistryException>(() => serde.SerializeAsync(new GeneratedEvent(),
            new SerializationContext(MessageComponentType.Value, "orders"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(server.LogEntries, e => e.RequestMessage?.Path == "/subjects/orders-value/versions");
    }

    [Fact]
    public async Task Generated_events_use_registry_schema_id_and_interoperate_with_native_confluent_clients()
    {
        using var server = Registry();
        using var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = server.Url });
        var serde = new AvroKafkaSerde(() => client);
        var context = new SerializationContext(MessageComponentType.Value, "orders");
        var token = TestContext.Current.CancellationToken;
        var bytes = await serde.SerializeAsync(new GeneratedEvent { Id = "typed" }, context, token);
        Assert.Equal(0, bytes[0]);
        Assert.Equal(42, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)));
        var nativeReader = new AvroDeserializer<GeneratedEvent>(client);
        Assert.Equal("typed", (await nativeReader.DeserializeAsync(bytes, false, context)).Id);
        var nativeWriter = new AvroSerializer<GeneratedEvent>(client, new AvroSerializerConfig { AutoRegisterSchemas = false });
        var appBytes = await nativeWriter.SerializeAsync(new GeneratedEvent { Id = "application" }, context);
        Assert.Equal("application", (await serde.DeserializeAsync<GeneratedEvent>(appBytes, context, token)).Id);
        Assert.Contains(server.LogEntries, e => e.RequestMessage?.Path == "/subjects/orders-value");
        Assert.DoesNotContain(server.LogEntries, e => e.RequestMessage?.Path == "/subjects/orders-value/versions");
    }

    [Fact]
    public async Task Setup_migration_registers_generated_schema_under_explicit_subject()
    {
        using var server = Registry();
        using var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = server.Url });
        var migrations = new MigrationCollection<SchemaRegistryMigrationContext>();
        migrations.AddAvro<GeneratedEvent>("orders-value");
        await migrations.RunAsync(new SchemaRegistryMigrationContext(client, new SchemaRegistryExposedConfiguration(server.Url!)), TestContext.Current.CancellationToken);
        var request = Assert.Single(server.LogEntries, e => e.RequestMessage?.Path == "/subjects/orders-value/versions");
        Assert.Contains("GeneratedEvent", request.RequestMessage!.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_and_malformed_wire_format_are_rejected()
    {
        using var server = Registry();
        using var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = server.Url });
        var serde = new AvroKafkaSerde(() => client);
        var context = new SerializationContext(MessageComponentType.Value, "orders");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serde.SerializeAsync(new GeneratedEvent(), context, cancelled.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => serde.DeserializeAsync<GeneratedEvent>([1, 2], context, TestContext.Current.CancellationToken));
        Assert.Empty(server.LogEntries);
    }

    [Fact]
    public async Task Subject_naming_strategy_is_forwarded_to_confluent()
    {
        using var server = Registry();
        server.Given(Request.Create().WithPath("/subjects/StoveDotnet.Kafka.UnitTests.GeneratedEvent").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = 42, version = 1, subject = "StoveDotnet.Kafka.UnitTests.GeneratedEvent", schema = new GeneratedEvent().Schema.ToString() }));
        using var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = server.Url });
        var serde = new AvroKafkaSerde(() => client, new AvroSerializerConfig { AutoRegisterSchemas = false, SubjectNameStrategy = SubjectNameStrategy.Record });
        await serde.SerializeAsync(new GeneratedEvent { Id = "record" }, new SerializationContext(MessageComponentType.Value, "orders"), TestContext.Current.CancellationToken);
        Assert.Contains(server.LogEntries, e => e.RequestMessage?.Path == "/subjects/StoveDotnet.Kafka.UnitTests.GeneratedEvent");
    }

    private static WireMockServer Registry()
    {
        var server = WireMockServer.Start();
        var schema = new GeneratedEvent().Schema.ToString();
        server.Given(Request.Create().WithPath("/subjects/orders-value").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = 42, version = 1, subject = "orders-value", schema }));
        server.Given(Request.Create().WithPath("/subjects/orders-value/versions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"id\":42}"));
        server.Given(Request.Create().WithPath("/schemas/ids/42").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(JsonSerializer.Serialize(new { schema })));
        return server;
    }
}
