# Kafka security, Schema Registry and Avro

Kafka uses plaintext and System.Text.Json by default. Transport security is configured on `KafkaOptions.Security`.
This configuration applies to Stove's admin client, producer and observer. Exposed configuration includes `Security`
so the application can receive the certificate paths and selected mode through its own configuration keys.

```csharp
.WithKafka(o =>
{
    o.Security.Mode = KafkaSecurityMode.MutualTls; // Plaintext, Tls or MutualTls
    o.Security.CaCertificateLocation = "/certificates/ca.pem";
    o.Security.ClientCertificateLocation = "/certificates/client.pem";
    o.Security.ClientKeyLocation = "/certificates/client-key.pem";
    // Required only when Stove starts the broker:
    o.Security.BrokerCertificateLocation = "/certificates/broker.pem";
    o.Security.BrokerKeyLocation = "/certificates/broker-key.pem";
    o.ConfigureExposedConfiguration = c =>
    [
        new("Kafka:BootstrapServers", c.BootstrapServers),
        new("Kafka:SecurityMode", c.Security.Mode.ToString()),
        new("Kafka:CaCertificateLocation", c.Security.CaCertificateLocation),
        new("Kafka:ClientCertificateLocation", c.Security.ClientCertificateLocation),
        new("Kafka:ClientKeyLocation", c.Security.ClientKeyLocation)
    ];
})
```

`Tls` requires the CA certificate; `MutualTls` also requires the client's certificate and private key. For an existing
broker, call `o.UseExisting("broker.example:9093")` and omit the broker certificate/key. The managed broker accepts
PEM certificates and PKCS#8 PEM keys; optional encrypted-key passwords are `ClientKeyPassword` and `BrokerKeyPassword`.
The broker certificate's SAN must cover the host advertised to clients (usually `localhost` for local Docker).
Stove keeps CA and hostname verification enabled. `Security.ApplyTo(ClientConfig)` is also available to native
application/test clients. Map settings to the application's actual keys; Stove does not change application DI.

Managed security configures the external listener. Broker/controller traffic and the managed registry's connection
use plaintext inside the private Docker network. This is a local test topology, with certificate enforcement on the
application/Stove connection. `ConfigureContainer` can customize the topology; `ConfigureClient` customizes native
client options, with explicit `Security` settings applied afterward. Selecting `Plaintext` overrides a transport
previously set only through `ConfigureClient`.

## Schema Registry

Add `StoveDotnet.SchemaRegistry` and use `StoveDotnet.SchemaRegistry`. Register Kafka first, then its registry:

```csharp
.WithKafka()
.WithSchemaRegistry(o =>
{
    o.ConfigureExposedConfiguration = c => [new("SchemaRegistry:Url", c.Url)];
    o.Migrations.Add(async (context, ct) =>
    {
        await context.Client.RegisterSchemaAsync("orders-value",
            new Confluent.SchemaRegistry.Schema(schemaJson, Confluent.SchemaRegistry.SchemaType.Avro))
            .WaitAsync(ct);
    });
})
```

Kafka readiness and topic migrations finish first. Then the registry starts, checks readiness, and runs its migrations
in ascending order. All finish before the application starts. `t.SchemaRegistry().Client` exposes the native client;
Stove owns and disposes it. Registry cleanup runs before the client and container are disposed, including in existing mode.
Dependent systems stop before Kafka and its private network.

Use `o.UseExisting("https://registry.example", runMigrations: false)` for an existing registry. `ConfigureClient` supports
Confluent's HTTP authentication and TLS settings. A managed registry currently requires managed Kafka; existing Kafka
must be paired with an existing registry. Each registry has its own `Image` and `ConfigureContainer` hooks.
Named bindings are explicit:

```csharp
.WithKafka("events")
.WithSchemaRegistry("schemas", o => { /* migrations */ }, kafkaName: "events")
```

## Generated Avro events

Add `StoveDotnet.Kafka.Avro` and use `StoveDotnet.Kafka.Avro`. Apache Avro generated `ISpecificRecord` types can be
published and observed without a custom serde:

```csharp
var stove = await StoveBuilder.Create()
    .WithKafka()
    .WithSchemaRegistry(o =>
    {
        o.Migrations.AddAvro<OrderCreated>("orders.created-value");
        o.ConfigureExposedConfiguration = c => [new("SchemaRegistry:Url", c.Url)];
    })
    .WithKafkaAvro()
    .WithAspNetCoreApplication<Program>()
    .StartAsync();

await stove.Test(async t =>
{
    await t.Kafka().Publish("orders.created", new OrderCreated { OrderId = "order-1" });
    await t.Kafka().ShouldBePublished<OrderCreated>(m => m.Value.OrderId == "order-1");
    var observed = await t.Kafka().PeekAsync<OrderCreated>("orders.created");
});
```

`AddAvro<T>` registers the generated type's schema under an explicit subject. Publishing resolves that schema's ID
through Confluent's client and emits its standard format: magic byte `0`, four-byte big-endian schema ID, Avro payload.
Observation retrieves the writer schema by ID and uses Confluent's generated-type deserializer, including schema
evolution supported by that client. Keys remain UTF-8 strings; Avro applies to values. Correlation, retention limits,
consumed/failed assertions and cancellation have the same behavior as JSON.

The default subject strategy is `Topic`: `topic-value`. Auto-registration defaults to **false**, so missing schemas
fail publishing. Enable it explicitly or select the application's subject naming strategy:

```csharp
.WithKafkaAvro(c =>
{
    c.SubjectNameStrategy = Confluent.SchemaRegistry.SubjectNameStrategy.Record;
    c.AutoRegisterSchemas = false;
}, kafkaName: "events", schemaRegistryName: "schemas")
```

For `Record`, register the full Avro record name; for `TopicRecord`, register `topic-full.record.Name`.
`AvroKafkaSerde` accepts Confluent serializer/deserializer configuration directly and can use a caller-owned registry
client. The serde does not dispose that client. Its `IStoveKafkaAsyncSerde` interface carries topic context and a
cancellation token. `KafkaOptions.AsyncSerde`, when set, takes precedence over the synchronous `Serde`.
Prefer `PeekAsync<T>` for registry-backed observation; the existing synchronous `Peek<T>` blocks until decoding finishes.
The built-in Avro path uses the schema-ID-prefixed wire format, rather than header-based schema identification.
