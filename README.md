# StoveDotnet

## TL;DR
> StoveDotnet removes the infrastructure-orchestration boilerplate from realistic .NET end-to-end tests.

It starts containerized databases, brokers, caches, and other dependencies; injects their connection details into your real ASP.NET Core or worker applications; waits for the system to become ready; and owns cleanup.
Tests use a focused DSL to call applications, prepare state, publish or observe messages, query databases, and inspect telemetry. Correlation, bounded waits, validation helpers, and failure evidence make tests easier to write and diagnose. Without locking you into a test framework or assertion library.

[![CI](https://github.com/cokceken/stove-dotnet/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/cokceken/stove-dotnet/actions/workflows/ci.yml?query=branch%3Amain)
[![Documentation](https://github.com/cokceken/stove-dotnet/actions/workflows/pages.yml/badge.svg?branch=main&event=push)](https://github.com/cokceken/stove-dotnet/actions/workflows/pages.yml?query=branch%3Amain)
[![NuGet](https://img.shields.io/nuget/v/StoveDotnet.svg)](https://www.nuget.org/packages/StoveDotnet)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/cokceken/stove-dotnet/badge)](https://scorecard.dev/viewer/?uri=github.com/cokceken/stove-dotnet)

Opinionated end-to-end testing for .NET, inspired by [Trendyol Stove](https://github.com/Trendyol/stove).

[Documentation](https://cokceken.github.io/stove-dotnet/) ·
[Compatibility and trust](https://cokceken.github.io/stove-dotnet/compatibility/) ·
[NuGet packages](https://www.nuget.org/packages?q=StoveDotnet) ·
[Roadmap](ROADMAP.md) ·
[Security](SECURITY.md)

StoveDotnet starts your **real** dependencies in containers, injects their connection details into your **real**
application, runs the application in-process on a real Kestrel port, and gives every test one fluent DSL to arrange and
assert across HTTP, PostgreSQL, SQL Server, MongoDB, MySQL, Kafka, RabbitMQ, Azure Service Bus, Redis and third-party HTTP APIs. An OTLP receiver collects the application's
traces and logs so a failing test explains itself.

```csharp
[Fact]
public Task Creates_order_when_stock_is_available() => stove.Test(async t =>
{
    t.InventoryFake().StockAvailable("chair", available: 5);           // typed fakes generated from OpenAPI specs
    var payments = t.PaymentsFake().ChargeSucceeds(paymentId: "pay-1");

    var response = (await t.Http().Post<Order>("/orders", new CreateOrderRequest("chair", 2, "customer-1")))
        .Expect(HttpStatusCode.Created);

    var statuses = await t.Postgres().Query(
        "select status from orders where id = @id", r => r.GetString(0),
        new NpgsqlParameter("id", response.Body.Id));
    Assert.Equal(["Created"], statuses);

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
| `StoveDotnet.AspNetCore` | `WithAspNetCoreApplication<Program>()` (WebApplicationFactory + real Kestrel) |
| `StoveDotnet.Hosting` | `WithHostApplication("worker", factory)`: named Generic Host workers with configuration and managed shutdown; DI bridge `t.Using<T>()` and opt-in test time (`UseStoveTime(clock)`, `t.Clock(application)`) for any application |
| `StoveDotnet.Containers` | `WithContainer("storage", configure)`: bring your own Testcontainers image, readiness, initialization and application configuration |
| `StoveDotnet.Http` | `t.Http()`: typed JSON calls against the application |
| `StoveDotnet.Oidc` | `WithOidc()` / `t.Oidc()`: local discovery, JWKS and signed test tokens for real bearer validation |
| `StoveDotnet.Telemetry` | `WithTelemetry()`: OTLP/HTTP receiver for traces and logs, `t.Telemetry()` |
| `StoveDotnet.Postgres` | `WithPostgres()`: Testcontainers PostgreSQL, raw Npgsql DSL |
| `StoveDotnet.SqlServer` | `WithSqlServer()`: Testcontainers SQL Server, native Microsoft.Data.SqlClient DSL |
| `StoveDotnet.MongoDb` | `WithMongoDb()`: MongoDB replica set, native filters, collections and ordered setup |
| `StoveDotnet.MySql` | `WithMySql()`: MySQL, native MySqlConnector DSL |
| `StoveDotnet.Kafka` | `WithKafka()`: Testcontainers Kafka, black-box publish/consume/fail assertions |
| `StoveDotnet.SchemaRegistry` | `WithSchemaRegistry()`: Confluent registry with ordered schema setup migrations |
| `StoveDotnet.Kafka.Avro` | `WithKafkaAvro()`: generated Avro event publishing and observation using registry schema IDs |
| `StoveDotnet.RabbitMq` | `WithRabbitMq()`: confirmed publishing, dedicated observation queues, native RabbitMQ.Client |
| `StoveDotnet.Azure.ServiceBus` | `WithAzureServiceBus()`: official emulator or Azure namespace, typed topology, scheduling and explicit settlement |
| `StoveDotnet.Redis` | `WithRedis()`: Testcontainers Redis, StackExchange.Redis client |
| `StoveDotnet.WireMock` | `WithWireMock()`: in-process WireMock.Net servers |

Requirements: .NET 10 and a Docker-compatible container runtime (Docker or Podman) for the container modules.

Published packages are in preview on [nuget.org](https://www.nuget.org/packages?q=StoveDotnet). Install the ones you need into
your e2e test project. MongoDB, MySQL, RabbitMQ and Azure Service Bus are implemented in this checkout; their publication is not implied by this table:

```shell
dotnet add package StoveDotnet.AspNetCore --prerelease
dotnet add package StoveDotnet.Http --prerelease
dotnet add package StoveDotnet.Postgres --prerelease
```

## Setting up

Describe the environment once and start it from your test framework's once-per-run hook:

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

Each system exposes typed connection details; `ConfigureExposedConfiguration` maps them to the configuration keys your
application already reads. Stove starts the dependencies in parallel, runs their migrations, starts the application
with that configuration, and owns cleanup.

- [Getting started](docs/getting-started.md): the first test, step by step.
- [Environment setup and lifecycle](docs/setup.md): configuration injection, named and existing instances, startup failures.
- [Writing tests](docs/writing-tests.md): the DSL of every module, waiting, and typed fakes for third-party APIs.
- [Databases](docs/database-modules.md), [Kafka and RabbitMQ](docs/messaging.md),
  [Azure Service Bus](docs/azure-service-bus.md), [custom containers](docs/custom-containers.md).
- [APIs and workers together](docs/multiple-applications.md) and [practical testing](docs/practical-testing.md):
  HTTP assertions, cancellation-aware waits, controllable time and OIDC.

## When a test fails

Stove rethrows the failure as a `StoveTestFailedException`. The original exception is kept as `InnerException`, and
the message contains what each system observed for that test:

```text
StoveDotnet.StoveTestFailedException : EqualException: Assert.Equal() Failure: Values differ
Expected: Created
Actual:   PaymentRequired

Stove test: OrderTests.Failure_output_demo
Trace id:   b23be8492d7e283bae0f7b389e58a5fc

--- Stove: application:(default) / application logs ---
09:42:49.016 [Warning] OrderService.OrderWorkflow: Payment declined for order 6de533c8-... of customer customer-4

--- Stove: telemetry ---
[x] POST http://127.0.0.1:58643/orders (order-service, Client, 356.8ms)
`-- [ok] POST /orders (order-service, Server, 347.8ms)
    |-- [ok] GET http://localhost:58630/stock/sofa (order-service, Client, 260.8ms)
    |   `-- [ok] GET (order-service, Server, 246.7ms)
    `-- [x] POST http://localhost:58631/charges (order-service, Client, 29.2ms)
        `-- [x] POST (order-service, Server, 25.8ms)

--- Stove: wiremock 'payments' ---
Requests received (1):
  POST http://localhost:58631/charges -> 500 (matched) {"orderId":"6de533c8-...","customerId":"customer-4","amount":10}

--- Stove: http ---
Requests sent (1):
  POST http://127.0.0.1:58643/orders → 402 PaymentRequired (356.8ms)
    Response body: (empty)
```

The report does not depend on how the test asserted or on which assertion library you use. Application logs and the
requests the test sent need no setup; the trace tree comes from `WithTelemetry()`. See
[telemetry and failure reports](docs/failure-reports.md) for what each section contains and how tests are correlated.

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
  [`plugins/stove-dotnet/skills/stove-dotnet`](https://github.com/cokceken/stove-dotnet/tree/main/plugins/stove-dotnet/skills/stove-dotnet)
  into your repository's `.agents/skills/` (or `.claude/skills/`).

Then ask, for example: *"Add Stove e2e tests for the order endpoint"* or *"Create a WireMock fake for the payments API
from specs/payments.yaml"*.

## Contributing

See [CONTRIBUTING.md](https://github.com/cokceken/stove-dotnet/blob/main/CONTRIBUTING.md) for the repository layout,
building, the test suites and the release process, and the [roadmap](ROADMAP.md) for what is planned.

## License

Apache-2.0
