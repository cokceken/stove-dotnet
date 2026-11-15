# Writing tests

```csharp
public Task Marks_order_paid_when_payment_completed_is_consumed() => stove.Test(async t =>
{
    var orderId = Guid.NewGuid();
    await t.Postgres().Execute("insert into orders (id, status) values (@id, 'Created')", new NpgsqlParameter("id", orderId));

    await t.Kafka().Publish("payments.completed", new PaymentCompleted(orderId, "pay-3"));
    await t.Kafka().ShouldBeConsumed<PaymentCompleted>(m => m.Value.OrderId == orderId, consumerGroup: "order-service");

    await t.Using<OrderRepository>(async repository =>
        Assert.Equal("Paid", (await repository.Find(orderId, t.CancellationToken))?.Status));
});
```

- `stove.Test(body, cancellationToken)` takes an optional token, for example xUnit's
  `TestContext.Current.CancellationToken`, so framework timeouts cancel Stove's waits.
- Framework control-flow exceptions (skip, inconclusive, pass) and cancellation pass through unchanged.
- `Eventually.AssertAsync(() => ..., timeout)` retries any assertion, from any assertion library, until it passes.
  `Eventually.UntilAsync` polls a value instead; an exception from its probe is an error and is not retried.

## Modules at a glance

- **Http:** `Get/Post/Put/Patch/Delete<T>(uri, body?, headers?)` returns `StoveHttpResponse<T>` with `StatusCode`,
  `Headers`, `RawBody` and a lazily deserialized `Body`. `SendRaw(HttpRequestMessage)` sends a hand-built request.
- **Postgres:** `Execute(sql, params)`, `Query<T>(sql, map, params)` and
  `DataSource` for raw Npgsql access. Options: `Migrations`, `Cleanup`, `Image`, `ConfigureContainer`, `ConfigureDataSource`.
- **SQL Server:** `Execute(sql, params)`, `Query<T>(sql, map, params)` and
  `OpenConnectionAsync(ct)` for native SqlClient access. Options: `Migrations`, `Cleanup`, `Image`, `ConfigureContainer`,
  `ConfigureConnection` and `UseExisting`.
- **MongoDB:** `Insert(collection, document)`, `Query<T>(collection, filter)`
  and native `Client`, `Database`, `Collection<T>()`. Ordered `Migrations` prepare collections, indexes and seed data.
- **MySQL:** `Execute` and `Query<T>` with `MySqlParameter` and native `DataSource` access.
  Options include `Migrations`, `Cleanup`, `ConfigureDataSource`, `ConfigureContainer` and `UseExisting`.
- **Kafka** (observes broker records and committed offsets; propagate correlation headers):
  - `Publish<T>` / `PublishRaw` send a message as the running test.
  - `ShouldBePublished<T>` waits until a matching message appears on any topic. Stove tails every topic with its own
    consumer.
  - `ShouldNotBePublished<T>(condition, within, topic)` observes for the full interval and fails on a matching message.
    This means no matching message was observed during that window; it cannot rule out later delivery or observer lag.
  - `ShouldBeConsumed<T>` waits until a consumer group has committed past the matching message.
  - `ShouldBeFailed<T>` waits until a matching message appears on an error topic (`.error` or `.DLT` by default).
  - `Peek<T>` returns retained messages of the current test observed so far.
  - `Observation` bounds evidence per test and excludes uncorrelated records by default. See [messaging guarantees](messaging.md).
  - Serialization uses System.Text.Json by default. Plug in your own with `IStoveKafkaSerde`, or use `WithKafkaAvro()`
    for generated Avro events. [Kafka security, Schema Registry and Avro](kafka.md) covers certificate configuration,
    schema migrations and subject naming.
- **RabbitMQ:** `Publish` / `PublishRaw` await broker confirms; `ShouldBePublished`, `ShouldNotBePublished` and `Peek`
  inspect copies on Stove's exclusive queue. Configure `Bindings` and ordered `Migrations`; native `Connection` is available.
  Observation does not prove application processing. See [setup and guarantees](messaging.md).
- **Azure Service Bus:** typed queues/topics/subscriptions, correlated `Publish`/`Schedule`, non-destructive `Peek` and
  `ShouldBeScheduled`, plus destructive `Receive` with explicit Complete/Abandon/DeadLetter/Defer settlement. Existing
  Azure namespaces accept any `TokenCredential`. See [authentication, scheduling and parallelism](azure-service-bus.md).
- **Redis:** `Multiplexer` and `Database(db)`, plus `Migrations` and `Cleanup` options.
- **WireMock:**
  - `MockGet/MockPost/MockPut/MockPatch/MockDelete(path, statusCode, responseBody, requestBody?, headers?, delay?)`.
  - `MockRaw(method, path, statusCode, string or byte[] body, contentType)` for XML, text or binary responses.
  - `Stub(request => ..., response => ...)` uses WireMock.Net's own builders; `request.WithPathTemplate(...)` is
    available there.
  - `Requests(method?, path?)` returns `RecordedRequest`s with `BodyAs<T>()`, `PathParameters`, `Query` and `Headers`.
  - `ShouldHaveBeenCalled(method, path, times, within?)` returns the matching requests; `ShouldNotHaveBeenCalled`.
  - `path` is an exact path or an OpenAPI template: `/stock/{productId}`, or `/{bucket}/{key+}` for the rest of the path.
  - `Server` gives raw access.
  - Stubs created in a test are removed when the test ends.
- **Telemetry:** `Spans()`, `Logs()`, `ShouldContainSpan(predicate)`, `ShouldNotHaveFailedSpans()`, `RenderTree()`.

## Faking third-party APIs

Wrap each third-party API in a small typed fake built from its API definition (OpenAPI/Swagger spec, SDK service
model or docs). The fake wraps a named WireMock instance, points the app's client at it through configuration, and
offers scenario and verification methods:

```csharp
// fixture
StoveBuilder.Create().WithInventoryFake().WithPaymentsFake() /* ... */;

// test
t.PaymentsFake().ChargeDeclined();
var charge = await t.PaymentsFake().ShouldHaveCharged(c => c.Amount == 20m);
```

The example project contains two fakes generated from OpenAPI specs:
[`Fakes/`](https://github.com/cokceken/stove-dotnet/tree/main/examples/Frameworks/OrderService.E2ETests.XunitV3/Fakes) from
[`specs/`](https://github.com/cokceken/stove-dotnet/tree/main/examples/Frameworks/OrderService/specs). The
[agent skill](https://github.com/cokceken/stove-dotnet#ai-agents) teaches coding agents to generate fakes like these, including for SDKs such as AWS S3.
