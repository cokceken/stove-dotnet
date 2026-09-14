# Writing StoveDotnet tests

## Shape of a test

```csharp
[Fact]
public Task Rejects_order_when_out_of_stock() => stove.Test(async t =>
{
    // Arrange: seed data and stub third parties through t
    // Act: call the app like a client would (HTTP, Kafka message)
    // Assert: observable outcomes (response, rows, published messages, outgoing calls, spans)
});
```

Rules for `stove.Test(Func<StoveTestContext, Task> body, CancellationToken cancellationToken = default)`:

- **Naming.** The test is named `{FileName}.{MethodName}` automatically. Keep method names descriptive.
- **Access.** Systems are reachable only through `t`, via extension methods such as `t.Http()`, `t.Postgres()`,
  `t.Kafka()`, `t.Redis()`, `t.WireMock(name)`, `t.Telemetry()` and `t.Using<T>()`.
- **No nesting.** One `stove.Test` per test method.
- **Cancellation.** Pass the framework token when there is one (xUnit: `TestContext.Current.CancellationToken`). Inside
  the body, use `t.CancellationToken`.
- **Failures.** A failure is rethrown as `StoveTestFailedException` with the original as `InnerException`. Skip,
  inconclusive and pass exceptions from test frameworks pass through unchanged.
- **Test context.** `t.TestId`, `t.TraceId`, `t.Traceparent` and `t.Application` (`Services`, `BaseAddress`) are
  available when needed.

Assert with the project's assertion library. Stove's own helpers throw `StoveAssertionException` or
`StoveTimeoutException`.

## Module DSL

### HTTP (`t.Http(name?)`)

```csharp
StoveHttpResponse<T> r = await t.Http().Get<T>("/orders/1", headers: null);
StoveHttpResponse    r = await t.Http().Post("/orders", body);          // also Put, Patch, Delete; generic <T> variants
r.StatusCode; r.Headers; r.RawBody; r.IsSuccessStatusCode;
T body = r.Body;            // generic only; deserialized lazily, throws with the raw body if it is not T
HttpResponseMessage raw = await t.Http().SendRaw(new HttpRequestMessage(...));
```

- Bodies are serialized with System.Text.Json web defaults (camelCase).
- Redirects are not followed.
- For auth, add headers per call or use `HttpClientOptions.DefaultHeaders`.

### Postgres (`t.Postgres(name?)`)

```csharp
int rows = await t.Postgres().Execute("insert into orders (id, status) values (@id, @status)",
    new NpgsqlParameter("id", id), new NpgsqlParameter("status", "Created"));
IReadOnlyList<T> list = await t.Postgres().Query("select ...", reader => reader.GetString(0), params NpgsqlParameter[]);
await t.Postgres().ShouldQuery("select ...", reader => (reader.GetGuid(0), reader.GetString(1)),
    rows => Assert.Equal([(id, "Created")], rows), new NpgsqlParameter("id", id));
NpgsqlDataSource ds = t.Postgres().DataSource;
```

- Raw Npgsql only; there is no Dapper or EF.
- The mapper receives `NpgsqlDataReader`.
- `ShouldQuery` runs once. Wrap it in `Eventually.AssertAsync` when the app writes asynchronously.

### Kafka (`t.Kafka(name?)`): broker observation with propagated correlation

```csharp
await t.Kafka().Publish("payments.completed", new PaymentCompleted(orderId, "pay-1"), key: orderId.ToString(), headers: null);
await t.Kafka().PublishRaw(topic, bytes, key: null, headers: null);

ObservedMessage<T> m = await t.Kafka().ShouldBePublished<T>(m => m.Value.OrderId == id, timeout: null, topic: null);
ObservedMessage<T> m = await t.Kafka().ShouldBeConsumed<T>(m => m.Value.OrderId == id, timeout: null, topic: null, consumerGroup: "order-service");
ObservedMessage<T> m = await t.Kafka().ShouldBeFailed<T>(m => ..., timeout: null);   // on topics ending with .error / .DLT
IReadOnlyList<ObservedMessage<T>> seen = t.Kafka().Peek<T>(topic: "orders.created"); // no waiting
m.Value; m.Key; m.Topic; m.Headers; m.Record.Partition; m.Record.Offset;
```

How it works:

- Stove tails all topics with its own consumer.
- `ShouldBePublished` matches any message the test can see, including ones Stove itself published.
- `ShouldBeConsumed` first finds the message, then waits until a consumer group (other than Stove's) has committed an
  offset past it. Pass the app's `consumerGroup`; otherwise all groups are checked.
- The default timeout is `KafkaOptions.DefaultTimeout` (10s).
- Use `ShouldNotBePublished<T>(condition, within, topic)` for a bounded absence observation; `Peek` is only a snapshot.
- Strict matching requires a valid matching traceparent or test id; when both are present they must agree.
- Records are retained only for active scopes within `Observation` limits. Overflow fails observation explicitly.
- `ShouldBeConsumed` proves a committed offset, not successful business processing.

### Redis (`t.Redis(name?)`)

`t.Redis().Database()` returns a StackExchange.Redis `IDatabase`, and `t.Redis().Multiplexer` returns an
`IConnectionMultiplexer`. There are no assertion helpers; use the client directly.

### WireMock (`t.WireMock(name)`)

Prefer a typed fake per third-party API (see `openapi-fakes.md`). The raw API:

```csharp
wm.MockGet(path, statusCode: 200, responseBody: obj, responseHeaders: null, delay: null);
wm.MockPost(path, statusCode: 200, responseBody: obj, requestBody: obj /* JSON equality match */, responseHeaders: null, delay: null);
wm.MockPut / MockPatch (same as MockPost) / MockDelete (same as MockGet);
wm.MockRaw(method, path, statusCode, "<xml/>" or byte[], contentType, responseHeaders: null, delay: null);
wm.Stub(req => req.WithPathTemplate("/items/{id}").UsingGet().WithHeader("Authorization", "*"), res => res.WithStatusCode(204));
IReadOnlyList<RecordedRequest> reqs = wm.Requests(method: "POST", path: "/charges");
IReadOnlyList<RecordedRequest> reqs = await wm.ShouldHaveBeenCalled("POST", "/charges", times: 1, within: null);
await wm.ShouldNotHaveBeenCalled("POST", "/charges");
reqs[0].BodyAs<T>(); reqs[0].PathParameters["id"]; reqs[0].QueryValue("page"); reqs[0].Header("Authorization"); reqs[0].Body;
```

- **Paths.** `path` is an exact path or an OpenAPI template: `{name}` matches one segment and `{name+}` matches the rest,
  including slashes. Matching ignores case.
- **Response bodies.** A `string` body is sent as-is. Other objects are serialized to JSON with the
  `application/json` content type.
- **Stub lifetime.** Stubs registered in a test are deleted when it ends. The newest matching stub wins in WireMock.Net
  when several match.

### Telemetry (`t.Telemetry()`)

```csharp
SpanRecord span = await t.Telemetry().ShouldContainSpan(s => s.Kind == "Server" && s.Attribute("http.route") as string == "/orders");
IReadOnlyList<SpanRecord> spans = await t.Telemetry().Spans();      // waits for export to go quiet
IReadOnlyList<LogRecord> logs = await t.Telemetry().Logs();
await t.Telemetry().ShouldNotHaveFailedSpans();
string tree = t.Telemetry().RenderTree();
```

Span `Kind` values are `"Server"`, `"Client"`, `"Internal"`, `"Producer"` and `"Consumer"`. Attributes follow
OpenTelemetry semantic conventions (`http.route`, `url.full`, `db.statement`...).

### App services (`t.Using<T>`)

```csharp
await t.Using<OrderRepository>(async repo => Assert.Equal("Paid", (await repo.Find(id, t.CancellationToken))?.Status));
var count = await t.Using<IOrderQueries, int>(q => q.Count());
await t.Using<IServiceA, IServiceB>(async (a, b) => { ... });   // up to 3 services
```

- Each call gets a new DI scope.
- Use it to arrange or assert through the app's own code when there is no external interface.
- Prefer external observation (HTTP, database, messages) when one exists.

### Waiting for eventual outcomes

```csharp
await Eventually.AssertAsync(async () =>
    await t.Postgres().ShouldQuery("select status from orders where id = @id", r => r.GetString(0),
        rows => Assert.Equal(["Paid"], rows), new NpgsqlParameter("id", id)),
    TimeSpan.FromSeconds(10), t.CancellationToken);
```

Never use `Task.Delay` to wait for the app. Use Kafka `ShouldBe*` waits, WireMock `within:`, telemetry
`ShouldContainSpan`, or `Eventually`.

## Correlation and parallel tests

- Each test has its own W3C trace. Stove's HTTP calls and broker publishes carry `traceparent` and `X-Stove-Test-Id`.
  Calls made directly in the body (a raw `HttpClient`, SDK clients) also join the trace through the current `Activity`.
- Kafka/RabbitMQ messages need matching correlation by default; both headers must agree when present. Headerless
  messages are excluded unless the explicit `SingleActiveTest` fallback applies. That fallback cannot distinguish
  delayed previous work from a new single scope. Other modules retain their existing permissive headerless behavior.
- **Parallel tests** need the app to propagate trace context. ASP.NET Core and HttpClient instrumentation do this;
  Kafka/RabbitMQ producers must copy `traceparent`. If they overlap on the same third-party endpoint, set
  `WireMockOptions.ScopeStubsToTest = true`.
- Prefer unique data per test (new ids, distinct product names) so tests stay independent of each other's rows.

## Test design guidance

- Name tests after behavior: `Rejects_order_when_out_of_stock_without_charging`.
- Assert outcomes at the boundaries: the response, the stored state, published messages, outgoing third-party calls.
  Do not assert internal method calls.
- Cover the failure paths the third-party spec defines (declined, not found, unavailable) through typed fake scenario
  methods.
- Keep one environment per run. Do not start Stove in constructors or per test class unless isolation truly needs a
  separate application.

## MongoDB (`t.MongoDb(name?)`)

Use namespace `StoveDotnet.MongoDb` with `MongoDB.Driver`/`MongoDB.Bson`.

```csharp
var id = ObjectId.GenerateNewId();
await t.MongoDb().Insert("records", new BsonDocument { ["_id"] = id, ["value"] = "ready" });
IReadOnlyList<BsonDocument> documents = await t.MongoDb().Query<BsonDocument>(
    "records", Builders<BsonDocument>.Filter.Eq("_id", id));
await t.MongoDb().ShouldQuery<BsonDocument>("records", Builders<BsonDocument>.Filter.Eq("_id", id),
    rows => Assert.Single(rows));
IMongoCollection<BsonDocument> collection = t.MongoDb().Collection<BsonDocument>("records");
IMongoDatabase database = t.MongoDb().Database;
IMongoClient client = t.MongoDb().Client;
```

Queries read current matches once with native filters; no implied ordering or retry. Use collection APIs for
projections, pagination, serializers and sessions. Pass `t.CancellationToken` to native calls; dispose sessions/cursors
you create. The default replica set supports native transactions, but they do not include the app's separate session.
Stove does not install global BSON conventions or reset documents between tests.

## MySQL (`t.MySql(name?)`)

Use namespace `StoveDotnet.MySql` with `MySqlConnector`.

```csharp
await t.MySql().Execute("insert into records values (@id, @value)",
    new MySqlParameter("id", id), new MySqlParameter("value", "ready"));
await t.MySql().ShouldQuery("select value from records where id = @id", r => r.GetString(0),
    rows => Assert.Equal(["ready"], rows), new MySqlParameter("id", id));
IReadOnlyList<string> values = await t.MySql().Query("select value from records", r => r.GetString(0));
MySqlDataSource dataSource = t.MySql().DataSource;
```

The DSL owns its connection/command/reader and uses test cancellation. Native connections obtained from `DataSource`
are caller-owned. Queries read once, and test correlation does not isolate rows. MariaDB compatibility is unverified.

## RabbitMQ (`t.RabbitMq(name?)`)

Use namespace `StoveDotnet.RabbitMq`. Configure named-exchange `Bindings` before asserting.

```csharp
await t.RabbitMq().Publish("orders", "orders.submit", new { id, value = "hello" });
RabbitMqMessage<JsonElement> message = await t.RabbitMq().ShouldBePublished<JsonElement>(
    m => m.Value.GetProperty("id").GetString() == id, routingKey: "orders.processed");
await t.RabbitMq().ShouldNotBePublished<JsonElement>(_ => true, TimeSpan.FromMilliseconds(200), routingKey: "orders.failed");
IReadOnlyList<RabbitMqMessage<JsonElement>> records = t.RabbitMq().Peek<JsonElement>();
```

`Publish` waits for publisher confirmation with mandatory routing enabled by default. An unroutable message raises
native `PublishReturnException`. `PublishRaw(exchange, routingKey, ReadOnlyMemory<byte>, headers?, mandatory?, contentType?)`
supports bytes; the default content type is application/octet-stream. JSON uses web serializer defaults.
`RabbitMqMessage<T>` contains `Value` and `Record` (Exchange, RoutingKey, Body, text Headers, MessageId, ObservedAt).
Assertion exchange/routing-key filters are exact strings, not binding patterns.

Stove observes copies on its own queue; it never consumes the work queue. A mandatory publish may route only to Stove,
so even a confirmed observed message does not prove application processing. Verify a business side effect. There is no
RabbitMQ `ShouldBeConsumed` API. Native `Connection` supports caller-owned channels; pass cancellation and serialize
concurrent native channel access. Observer connection/channel loss or consumer cancellation invalidates assertions.
