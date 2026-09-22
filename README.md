# StoveDotnet

Opinionated end-to-end testing for .NET, inspired by [Trendyol Stove](https://github.com/Trendyol/stove).

StoveDotnet starts your **real** dependencies in containers, injects their connection details into your **real**
application, runs the application in-process on a real Kestrel port, and gives every test one fluent DSL to arrange and
assert across HTTP, PostgreSQL, SQL Server, MongoDB, MySQL, Kafka, RabbitMQ, Redis and third-party HTTP APIs. An OTLP receiver collects the application's
traces and logs so a failing test explains itself.

```csharp
[Fact]
public Task Creates_order_when_stock_is_available() => stove.Test(async t =>
{
    t.InventoryFake().StockAvailable("chair", available: 5);           // typed fakes generated from OpenAPI specs
    var payments = t.PaymentsFake().ChargeSucceeds(paymentId: "pay-1");

    var response = (await t.Http().Post<Order>("/orders", new CreateOrderRequest("chair", 2, "customer-1")))
        .Expect(HttpStatusCode.Created);

    await t.Postgres().ShouldQuery(
        "select status from orders where id = @id", r => r.GetString(0),
        rows => Assert.Equal(["Created"], rows),
        new NpgsqlParameter("id", response.Body.Id));

    await t.Kafka().ShouldBePublished<OrderCreated>(m => m.Value.OrderId == response.Body.Id);
    var charge = await payments.ShouldHaveCharged(c => c.OrderId == response.Body.Id);
    Assert.Equal(20m, charge.Amount);
});
```

## Principles

- **The real system runs.** Stove does not automatically replace application services. Overrides are explicit through
  the host hook; optional `UseStoveTime(clock)` registers a fixture-owned test clock.
- **Every e2e test goes through `stove.Test`.** The test is named after its method. Systems are reachable only through
  the test context `t`. Stove owns the test's scope: correlation, per-test cleanup and failure reporting.
- **No test framework lock-in.** Stove ships no xUnit, NUnit, TUnit or MSTest extensions. You start Stove once per test
  run from your framework's hook (see [Test frameworks](#test-frameworks)).
- **Use your assertion library.** DSL methods return values or take callbacks; HTTP also offers fluent status checks.
- **Lightweight.** No dashboard and no agents. One OTLP/HTTP endpoint.

## Packages

| Package | Provides |
|---|---|
| `StoveDotnet` | Builder, lifecycle, `stove.Test`, correlation, `Eventually`, migrations |
| `StoveDotnet.AspNetCore` | `WithAspNetCoreApplication<Program>()` (WebApplicationFactory + real Kestrel), DI bridge `t.Using<T>()` |
| `StoveDotnet.Hosting` | `WithHostApplication("worker", factory)`: named Generic Host workers with configuration and managed shutdown |
| `StoveDotnet.Http` | `t.Http()`: typed JSON calls against the application |
| `StoveDotnet.Time` | Explicit `UseStoveTime(clock)` injection and `t.Clock(application)` using Microsoft's FakeTimeProvider |
| `StoveDotnet.Oidc` | `WithOidc()` / `t.Oidc()`: local discovery, JWKS and signed test tokens for real bearer validation |
| `StoveDotnet.Telemetry` | `WithTelemetry()`: OTLP/HTTP receiver for traces and logs, `t.Telemetry()` |
| `StoveDotnet.Postgres` | `WithPostgres()`: Testcontainers PostgreSQL, raw Npgsql DSL |
| `StoveDotnet.SqlServer` | `WithSqlServer()`: Testcontainers SQL Server, native Microsoft.Data.SqlClient DSL |
| `StoveDotnet.MongoDb` | `WithMongoDb()`: MongoDB replica set, native filters, collections and ordered setup |
| `StoveDotnet.MySql` | `WithMySql()`: MySQL, native MySqlConnector DSL |
| `StoveDotnet.Kafka` | `WithKafka()`: Testcontainers Kafka, black-box publish/consume/fail assertions |
| `StoveDotnet.RabbitMq` | `WithRabbitMq()`: confirmed publishing, dedicated observation queues, native RabbitMQ.Client |
| `StoveDotnet.Redis` | `WithRedis()`: Testcontainers Redis, StackExchange.Redis client |
| `StoveDotnet.WireMock` | `WithWireMock()`: in-process WireMock.Net servers |

Requirements: .NET 10 and a Docker-compatible container runtime (Docker or Podman) for the container modules.

Published packages are in preview on [nuget.org](https://www.nuget.org/packages?q=StoveDotnet). Install the ones you need into
your e2e test project. MongoDB, MySQL and RabbitMQ are implemented in this checkout; their publication is not implied by this table:

```shell
dotnet add package StoveDotnet.AspNetCore --prerelease
dotnet add package StoveDotnet.Http --prerelease
dotnet add package StoveDotnet.Postgres --prerelease
```

## Setting up

See [practical testing](docs/practical-testing.md) for fluent HTTP status assertions, safe diagnostics, custom requests,
cancellation-aware waits, controllable time and OIDC. The [isolation examples](examples/Isolation/README.md) cover shared
hosts, independent databases/hosts and background workers. New modules must be published before consuming them from NuGet;
their presence in this checkout does not imply a release.

For separate APIs and workers, see [named applications and HTTP clients](docs/multiple-applications.md) and the
[API/worker example](examples/MultiApplication/README.md). Existing single-application setup remains unchanged.

```csharp
Stove = await StoveBuilder.Create()
    .WithTelemetry()
    .WithPostgres(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)])
    .WithRedis(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Redis", c.ConnectionString)])
    .WithKafka(o =>
    {
        o.ConfigureExposedConfiguration = c => [new("Kafka:BootstrapServers", c.BootstrapServers)];
        o.Migrations.Add((ctx, _) => ctx.Admin.CreateTopicsAsync([new TopicSpecification { Name = "orders.created" }]));
    })
    .WithWireMock("inventory", o => o.ConfigureExposedConfiguration = c => [new("Inventory:BaseUrl", c.BaseUrl.ToString())])
    .WithWireMock("payments", o => o.ConfigureExposedConfiguration = c => [new("Payments:BaseUrl", c.BaseUrl.ToString())])
    .WithHttpClient()
    .WithAspNetCoreApplication<Program>()
    .StartAsync();
```

`StartAsync` does the following, in order:

1. Starts all systems in parallel and runs their migrations.
2. Collects every system's exposed configuration.
3. Starts the application with that configuration.
4. Wires up systems that need the running application.

`DisposeAsync` stops the application, runs cleanup callbacks and removes containers.
Every resource disposal is attempted even when a cleanup callback fails. Shutdown failures are collected in an
`AggregateException`. Startup failures are rethrown unchanged when rollback succeeds; if rollback also fails, an
`AggregateException` contains the startup failure first and the rollback failure second.

### Configuration injection

Each system exposes typed connection details (for example `PostgresExposedConfiguration.ConnectionString`). You map
them to your application's configuration keys with `ConfigureExposedConfiguration`. Stove passes the values as
in-memory configuration with the highest precedence (`IWebHostBuilder.UseSetting`), so they are visible even to code in
`Program.cs` that reads configuration before `Build()`. Two systems exposing the same key is an error.

### Named instances

Every `WithXxx` has an overload that takes a name. Use it when you need several instances of one kind, such as one
WireMock per third-party API or two Redis instances:

- `t.WireMock("payments")` resolves by name.
- `t.WireMock()` resolves the unnamed instance. If there is none, it resolves the single registered instance of that
  kind. If that is ambiguous, it throws and lists the available names.

### Existing instances

Postgres, SQL Server, MongoDB, MySQL, Kafka, RabbitMQ and Redis can use an already running service instead of a container. This is useful for CI services or
shared environments:

```csharp
.WithPostgres(o => o.UseExisting(Environment.GetEnvironmentVariable("ORDERS_DB")!, runMigrations: true))
```

PostgreSQL, SQL Server and MySQL open a connection before starting the application, even when migrations are disabled.
Stove does not stop an existing server. Configured cleanup callbacks still run; `runMigrations: false` only disables
migrations, not cleanup.

### SQL Server

Reference `StoveDotnet.SqlServer` and use `StoveDotnet.SqlServer` and `Microsoft.Data.SqlClient` namespaces:

```csharp
var stove = await StoveBuilder.Create()
    .WithSqlServer("orders", o =>
    {
        o.Database = "orders";
        o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)];
        o.Migrations.Add(async (ctx, ct) =>
        {
            await using var connection = await ctx.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "create table orders (id int primary key, status nvarchar(50))";
            await command.ExecuteNonQueryAsync(ct);
        });
    })
    .WithAspNetCoreApplication<Program>()
    .StartAsync();

await stove.Test(async t =>
{
    await t.SqlServer("orders").Execute("insert into orders values (@id, @status)",
        new SqlParameter("id", 1), new SqlParameter("status", "Created"));
    await t.SqlServer("orders").ShouldQuery("select status from orders where id = @id",
        r => r.GetString(0), rows => Assert.Equal(["Created"], rows), new SqlParameter("id", 1));
});
await stove.DisposeAsync();
```

Managed instances create the requested database before migrations. `UseExisting(connectionString, runMigrations: true)`
uses the database in that connection string and does not create it. `Image`, `Password` and `ConfigureContainer` apply
to managed instances. The default image follows the SQL Server 2022 tag; set `Image` to a fixed tag or digest for a
reproducible environment. Running the SQL Server Testcontainers image accepts its EULA; use an existing instance when
your environment cannot run that image.

`OpenConnectionAsync(ct)` returns a native `SqlConnection` for transactions, bulk copy and other provider operations.
The caller disposes it. `ConfigureConnection` customizes each client connection before opening (for example, an
access-token callback); migrations and cleanup receive the same connection factory. It does not configure the
application's own SQL client. For PostgreSQL, `ConfigureDataSource` customizes the `NpgsqlDataSourceBuilder` used by Stove.

Both database DSLs query once. Use `Eventually.AssertAsync` for assertions against asynchronous application writes.

### MongoDB and MySQL

Each provider has an independent package, native-client test application and acceptance suite. MongoDB defaults to
`mongo:8.0` with a single-node replica set; MySQL defaults to `mysql:8.4` with MySqlConnector. See the
[database module guide](docs/database-modules.md) for runtime modes, ownership, transactions and complete setup details.

```csharp
// using StoveDotnet.MongoDb; using MongoDB.Bson; using MongoDB.Driver;
var builder = StoveBuilder.Create().WithMongoDb("documents", o =>
{
    o.ConfigureExposedConfiguration = c =>
        [new("Mongo:ConnectionString", c.ConnectionString), new("Mongo:Database", c.Database)];
    o.Setup.Add((ctx, ct) => ctx.Database.CreateCollectionAsync("records", cancellationToken: ct));
});
// After adding your application and starting the environment:
await stove.Test(async t =>
{
    var id = ObjectId.GenerateNewId();
    await t.MongoDb("documents").Insert("records", new BsonDocument { ["_id"] = id, ["value"] = "ready" });
    await t.MongoDb("documents").ShouldQuery<BsonDocument>("records", Builders<BsonDocument>.Filter.Eq("_id", id),
        documents => Assert.Single(documents));
});
```

```csharp
// using StoveDotnet.MySql; using MySqlConnector;
var builder = StoveBuilder.Create().WithMySql(o =>
{
    o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)];
    o.Migrations.Add(async (ctx, ct) =>
    {
        await using var connection = await ctx.DataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "create table orders (id int primary key, status varchar(100))";
        await command.ExecuteNonQueryAsync(ct);
    });
});
// Inside stove.Test:
await t.MySql().Execute("insert into orders values (@id, @status)",
    new MySqlParameter("id", 1), new MySqlParameter("status", "Created"));
```

### Database test isolation

A database belongs to a running Stove environment, not an individual `stove.Test`. Migrations run at environment startup
and cleanup at shutdown. Named managed instances have separate containers; two named existing connections may still
point at the same database. Correlation does not isolate rows or roll back application writes.

Use unique identifiers per test for parallel tests. For tests that require an empty database, use a dedicated environment
or explicitly reset data between sequential tests. A transaction opened by the test does not include writes made by the
application through its own connections.

## Writing tests

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
- `Eventually.AssertAsync(() => ..., timeout)` retries any assertion until it passes.

### Modules at a glance

- **Http:** `Get/Post/Put/Patch/Delete<T>(uri, body?, headers?)` returns `StoveHttpResponse<T>` with `StatusCode`,
  `Headers`, `RawBody` and a lazily deserialized `Body`. `SendRaw(HttpRequestMessage)` sends a hand-built request.
- **Postgres:** `Execute(sql, params)`, `Query<T>(sql, map, params)`, `ShouldQuery<T>(sql, map, assert, params)` and
  `DataSource` for raw Npgsql access. Options: `Migrations`, `Cleanup`, `Image`, `ConfigureContainer`, `ConfigureDataSource`.
- **SQL Server:** `Execute(sql, params)`, `Query<T>(sql, map, params)`, `ShouldQuery<T>(sql, map, assert, params)` and
  `OpenConnectionAsync(ct)` for native SqlClient access. Options: `Migrations`, `Cleanup`, `Image`, `ConfigureContainer`,
  `ConfigureConnection` and `UseExisting`.
- **MongoDB:** `Insert(collection, document)`, `Query<T>(collection, filter)`, `ShouldQuery<T>(collection, filter, assert)`
  and native `Client`, `Database`, `Collection<T>()`. Ordered `Setup` prepares collections, indexes and seed data.
- **MySQL:** `Execute`, `Query<T>`, `ShouldQuery<T>` with `MySqlParameter` and native `DataSource` access.
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
  - `Observation` bounds evidence per test and excludes uncorrelated records by default. See [messaging guarantees](docs/messaging.md).
  - Serialization uses System.Text.Json by default. Plug in your own with `IStoveKafkaSerde`.
- **RabbitMQ:** `Publish` / `PublishRaw` await broker confirms; `ShouldBePublished`, `ShouldNotBePublished` and `Peek`
  inspect copies on Stove's exclusive queue. Configure `Bindings` and ordered `Setup`; native `Connection` is available.
  Observation does not prove application processing. See [setup and guarantees](docs/messaging.md).
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

### Faking third-party APIs

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
[agent skill](#ai-agents) teaches coding agents to generate fakes like these, including for SDKs such as AWS S3.

## Telemetry and failure reports

`WithTelemetry()` starts an OTLP/HTTP (protobuf) receiver and exposes the standard SDK settings as configuration:
`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, and short batch delays. An application with
a standard OpenTelemetry setup exports to it without changes:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("order-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql().AddOtlpExporter())
    .WithLogging(l => l.AddOtlpExporter());
```

Each test gets its own W3C trace. Stove's HTTP calls and Kafka messages carry `traceparent` and `X-Stove-Test-Id`, so
the application's spans and logs join the test's trace. When a test fails, Stove rethrows a `StoveTestFailedException`.
The original exception is kept as `InnerException`, and the message contains what each system observed:

```text
StoveDotnet.StoveTestFailedException : EqualException: Assert.Equal() Failure: Values differ
Expected: Created
Actual:   PaymentRequired

Stove test: OrderTests.Failure_output_demo
Trace id:   b23be8492d7e283bae0f7b389e58a5fc

--- Stove: telemetry ---
[x] POST http://127.0.0.1:58643/orders (order-service, Client, 356.8ms)
`-- [ok] POST /orders (order-service, Server, 347.8ms)
    |-- [ok] GET http://localhost:58630/stock/sofa (order-service, Client, 260.8ms)
    |   `-- [ok] GET (order-service, Server, 246.7ms)
    `-- [x] POST http://localhost:58631/charges (order-service, Client, 29.2ms)
        `-- [x] POST (order-service, Server, 25.8ms)

Logs (1):
  09:42:49.016 [Warning] OrderService.OrderWorkflow: Payment declined for order 6de533c8-... of customer customer-4

--- Stove: wiremock 'payments' ---
Requests received (1):
  POST http://localhost:58631/charges -> 500 (matched) {"orderId":"6de533c8-...","customerId":"customer-4","amount":10}
```

The application runs in the test process, so instrumentation sees everything in that process. The server spans under
the WireMock calls above come from the in-process WireMock servers.

### Correlation and parallel tests

Kafka and RabbitMQ accept messages carrying the active test's id or a valid matching `traceparent`. If both headers
are present, both must agree. Malformed or conflicting headers are excluded. Headerless messages are excluded by
default; the explicit `SingleActiveTest` fallback uses observer arrival time and cannot distinguish a delayed old
message from current work. Use strict correlation for parallel tests and existing brokers.

Both observers retain at most 10,000 messages / 16 MiB of payload/header evidence per active test by default. Retention
overflow makes assertions fail explicitly. Completed scopes release their records, with bounded failure evidence kept
long enough for diagnostics. These defaults intentionally change Kafka's previous unbounded, permissive behavior.
See [migration guidance and exact guarantees](docs/messaging.md).

Other modules keep their existing correlation behavior; telemetry may include data without correlation headers.
Application code must propagate trace context across boundaries:

- ASP.NET Core and HttpClient instrumentation propagate W3C context.
- Kafka and RabbitMQ producers must forward `traceparent` (or the test id) into message headers. The native-client test
  apps demonstrate this without referencing Stove.
- For WireMock stubs that differ between parallel tests, set `ScopeStubsToTest = true`.

## Test frameworks

Runnable examples demonstrate **xUnit v3, NUnit, MSTest and TUnit**, each with its own lifecycle hooks and
native assertions against the same real ASP.NET Core application, PostgreSQL container and WireMock catalog.

See the [adopter guide](docs/test-frameworks.md) for package setup, cancellation, parallelism and single-test
commands, and the [example projects](examples/Frameworks/README.md) for code you can run and adapt.
The examples target .NET 10 with Microsoft.Testing.Platform. CI checks normal, filtered, failed and skipped
runs, verifies teardown, and repeats the checks against isolated NuGet consumers before publishing.

## AI agents

StoveDotnet ships an agent skill that teaches coding agents to:

- set up Stove in a project;
- write tests with the correct APIs;
- read failure output;
- generate typed WireMock fakes from OpenAPI specs or SDK definitions.

The skill uses the open `SKILL.md` format.

- **Claude Code:** install it as a plugin:
  ```text
  /plugin marketplace add cokceken/stove-dotnet
  /plugin install stove-dotnet@stove-dotnet
  ```
- **Other agents** (Codex, GitHub Copilot, Cursor and others that read `.agents/skills`): copy
  [`.agents/skills/stove-dotnet`](https://github.com/cokceken/stove-dotnet/tree/main/.agents/skills/stove-dotnet) into
  your repository's `.agents/skills/` (or `.claude/skills/`).

Then ask, for example: *"Add Stove e2e tests for the order endpoint"* or *"Create a WireMock fake for the payments API
from specs/payments.yaml"*.

## Repository layout and development

See the [project roadmap](ROADMAP.md) for implemented work, remaining gaps and proposed next modules, and
[architecture decisions](docs/decisions.md) for the agreed design direction.

```
src/                         library packages
tests/StoveDotnet.UnitTests   core only; no application host or containers
tests/*.AcceptanceTests      separate Hosting, database, Redis, Kafka and RabbitMq suites
tests/StoveDotnet.Testing     provider-neutral database contracts; no database drivers
tests/TestApps/               small real applications using native clients, with no Stove references
examples/Frameworks/         Framework examples, shared SampleApi, and OrderService composition tests with typed fakes
.agents/skills/stove-dotnet  agent skill (canonical copy)
plugins/stove-dotnet         Claude Code plugin (copy of the skill; CI checks they match)
```

```shell
dotnet build -c Release
dotnet test --project tests/StoveDotnet.UnitTests
dotnet test --project tests/StoveDotnet.Hosting.AcceptanceTests
dotnet test --project tests/StoveDotnet.Postgres.AcceptanceTests
dotnet test --project tests/StoveDotnet.SqlServer.AcceptanceTests
dotnet test --project tests/StoveDotnet.MongoDb.AcceptanceTests
dotnet test --project tests/StoveDotnet.MySql.AcceptanceTests
dotnet test --project tests/StoveDotnet.Redis.AcceptanceTests
dotnet test --project tests/StoveDotnet.Kafka.AcceptanceTests
dotnet test --project tests/StoveDotnet.RabbitMq.AcceptanceTests
dotnet test --project examples/Frameworks/OrderService.E2ETests.XunitV3
# the deliberately failing demo test:
dotnet test --project examples/Frameworks/OrderService.E2ETests.XunitV3 -- --explicit only
```

Podman works as the container runtime through its Docker-compatible API. Run suites individually on machines with
limited memory; the database suites each start two named database containers. CI runs the suites in separate jobs,
and a provider failure does not prevent other suites from reporting their results.

The database application contracts cover HTTP writes observed by Stove, Stove seeding read by the application,
startup migrations, named database routing, existing endpoints, concurrency and failure handling. Module contracts
cover native clients and lifecycle edge cases. See [module conventions](docs/modules.md).

OrderService remains a focused composition example using PostgreSQL, Redis, Kafka and third-party HTTP APIs. Its tests
cover retrieval, rejected-order side effects, payment consumption, concurrent requests and failure diagnostics. The
optional `OrderId` on creation lets a caller identify an attempted order even when it is rejected; it is not an
idempotency guarantee. Concurrent tests use distinct identifiers and trace-scoped WireMock stubs.

See [CONTRIBUTING.md](https://github.com/cokceken/stove-dotnet/blob/main/CONTRIBUTING.md) for the skill sync and the
release process.

## License

Apache-2.0
