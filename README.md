# StoveDotnet

Opinionated end-to-end testing for .NET, inspired by [Trendyol Stove](https://github.com/Trendyol/stove).

StoveDotnet starts your **real** dependencies in containers, injects their connection details into your **real**
application, runs the application in-process on a real Kestrel port, and gives every test one fluent DSL to arrange and
assert across HTTP, PostgreSQL, Kafka, Redis and third-party HTTP APIs. An OTLP receiver collects the application's
traces and logs so a failing test explains itself.

```csharp
[Fact]
public Task Creates_order_when_stock_is_available() => stove.Test(async t =>
{
    t.InventoryFake().StockAvailable("chair", available: 5);           // typed fakes generated from OpenAPI specs
    var payments = t.PaymentsFake().ChargeSucceeds(paymentId: "pay-1");

    var response = await t.Http().Post<Order>("/orders", new CreateOrderRequest("chair", 2, "customer-1"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

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

- **The real system runs.** Stove never replaces application services. If you want to swap something, that is your
  decision, made through the web host hook.
- **Every e2e test goes through `stove.Test`.** The test is named after its method. Systems are reachable only through
  the test context `t`. Stove owns the test's scope: correlation, per-test cleanup and failure reporting.
- **No test framework lock-in.** Stove ships no xUnit, NUnit, TUnit or MSTest extensions. You start Stove once per test
  run from your framework's hook (see [Test frameworks](#test-frameworks)).
- **No assertion library.** DSL methods return values or take callbacks; assert with whatever you already use.
- **Lightweight.** No dashboard and no agents. One OTLP/HTTP endpoint.

## Packages

| Package | Provides |
|---|---|
| `StoveDotnet` | Builder, lifecycle, `stove.Test`, correlation, `Eventually`, migrations |
| `StoveDotnet.AspNetCore` | `WithAspNetCoreApplication<Program>()` (WebApplicationFactory + real Kestrel), DI bridge `t.Using<T>()` |
| `StoveDotnet.Http` | `t.Http()`: typed JSON calls against the application |
| `StoveDotnet.Telemetry` | `WithTelemetry()`: OTLP/HTTP receiver for traces and logs, `t.Telemetry()` |
| `StoveDotnet.Postgres` | `WithPostgres()`: Testcontainers PostgreSQL, raw Npgsql DSL |
| `StoveDotnet.Kafka` | `WithKafka()`: Testcontainers Kafka, black-box publish/consume/fail assertions |
| `StoveDotnet.Redis` | `WithRedis()`: Testcontainers Redis, StackExchange.Redis client |
| `StoveDotnet.WireMock` | `WithWireMock()`: in-process WireMock.Net servers |

Requirements: .NET 10 and a Docker-compatible container runtime (Docker or Podman) for the container modules.

Packages are in preview on [nuget.org](https://www.nuget.org/packages?q=StoveDotnet). Install the ones you need into
your e2e test project:

```shell
dotnet add package StoveDotnet.AspNetCore --prerelease
dotnet add package StoveDotnet.Http --prerelease
dotnet add package StoveDotnet.Postgres --prerelease
```

## Setting up

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

Postgres, Kafka and Redis can use an already running service instead of a container. This is useful for CI services or
shared environments:

```csharp
.WithPostgres(o => o.UseExisting(Environment.GetEnvironmentVariable("ORDERS_DB")!, runMigrations: true))
```

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
  `DataSource` for raw Npgsql access. Options: `Migrations`, `Cleanup`, `Image`, `ConfigureContainer`.
- **Kafka** (black-box, needs no application changes):
  - `Publish<T>` / `PublishRaw` send a message as the running test.
  - `ShouldBePublished<T>` waits until a matching message appears on any topic. Stove tails every topic with its own
    consumer.
  - `ShouldBeConsumed<T>` waits until a consumer group has committed past the matching message.
  - `ShouldBeFailed<T>` waits until a matching message appears on an error topic (`.error` or `.DLT` by default).
  - `Peek<T>` returns the messages observed so far.
  - Serialization uses System.Text.Json by default. Plug in your own with `IStoveKafkaSerde`.
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
[`Fakes/`](https://github.com/cokceken/stove-dotnet/tree/main/examples/OrderService.E2ETests.XunitV3/Fakes) from
[`specs/`](https://github.com/cokceken/stove-dotnet/tree/main/examples/OrderService/specs). The
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

Data observed by Stove belongs to a test as follows:

- If it carries a test id or trace id, it belongs to the test with that id.
- If it carries neither, it matches every test.

Sequential tests need nothing more. Parallel tests need the application to propagate trace context:

- ASP.NET Core and HttpClient instrumentation do this automatically.
- Confluent.Kafka has no instrumentation. Copy `traceparent` into produced message headers yourself; the example app
  shows how.
- For WireMock stubs that must differ between parallel tests, set `ScopeStubsToTest = true`.

## Test frameworks

Stove only needs to be started once and disposed at the end. The example uses xUnit v3:

```csharp
[assembly: AssemblyFixture(typeof(StoveFixture))]

public sealed class StoveFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;
    public async ValueTask InitializeAsync() => Stove = await StoveSetup.Build().StartAsync();
    public async ValueTask DisposeAsync() => await Stove.DisposeAsync();
}

public sealed class OrderTests(StoveFixture fixture)
{
    [Fact]
    public Task Creates_order() => fixture.Stove.Test(async t => { /* ... */ });
}
```

<details>
<summary>NUnit, TUnit and MSTest</summary>

```csharp
// NUnit
[SetUpFixture]
public sealed class StoveSetUp
{
    public static Stove Stove { get; private set; } = null!;
    [OneTimeSetUp] public async Task Start() => Stove = await StoveSetup.Build().StartAsync();
    [OneTimeTearDown] public async Task Stop() => await Stove.DisposeAsync();
}

// TUnit
public static class StoveHooks
{
    public static Stove Stove { get; private set; } = null!;
    [Before(TestSession)] public static async Task Start() => Stove = await StoveSetup.Build().StartAsync();
    [After(TestSession)] public static async Task Stop() => await Stove.DisposeAsync();
}

// MSTest
[TestClass]
public static class StoveHooks
{
    public static Stove Stove { get; private set; } = null!;
    [AssemblyInitialize] public static async Task Start(TestContext _) => Stove = await StoveSetup.Build().StartAsync();
    [AssemblyCleanup] public static async Task Stop() => await Stove.DisposeAsync();
}
```

</details>

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

```
src/                         library packages
tests/                       StoveDotnet.UnitTests (no Docker: core, HTTP, WireMock, telemetry against a small test app)
                             StoveDotnet.IntegrationTests (Docker: Postgres, Redis, Kafka)
examples/                    OrderService, its OpenAPI specs, and OrderService.E2ETests.XunitV3 with typed fakes
.agents/skills/stove-dotnet  agent skill (canonical copy)
plugins/stove-dotnet         Claude Code plugin (copy of the skill; CI checks they match)
```

```shell
dotnet build -c Release
dotnet test --project tests/StoveDotnet.UnitTests
dotnet test --project tests/StoveDotnet.IntegrationTests
dotnet test --project examples/OrderService.E2ETests.XunitV3
# the deliberately failing demo test:
dotnet test --project examples/OrderService.E2ETests.XunitV3 -- --explicit only
```

Podman works as the container runtime through its Docker-compatible API. Give the machine enough memory for Kafka
(4 GB or more).

See [CONTRIBUTING.md](https://github.com/cokceken/stove-dotnet/blob/main/CONTRIBUTING.md) for the skill sync and the
release process.

## License

Apache-2.0
