# Adding StoveDotnet to a project

## 1. Test project

Create a separate e2e test project next to the app (for example `tests/MyService.E2ETests`) that references the app
project. Add only the packages you need:

| Package | When |
|---|---|
| `StoveDotnet` | always |
| `StoveDotnet.AspNetCore` | the app is ASP.NET Core; also provides `t.Using<T>()` |
| `StoveDotnet.Http` | tests call the app's HTTP API |
| `StoveDotnet.Postgres` / `.SqlServer` / `.MongoDb` / `.MySql` / `.Kafka` / `.RabbitMq` / `.Redis` | the app uses them |
| `StoveDotnet.WireMock` | the app calls third-party HTTP APIs (see `openapi-fakes.md`) |
| `StoveDotnet.Telemetry` | the app has (or can get) OpenTelemetry; strongly recommended |

Requirements: .NET 10, and Docker or Podman for database, Kafka, RabbitMQ and Redis container modules.

The app must expose its entry point to `WebApplicationFactory`. Add this to the end of the app's `Program.cs` if it is
missing:

```csharp
public partial class Program;
```

## 2. Map exposed configuration to the app's real keys

Each system exposes typed connection details after it starts. Map them to the keys the app **already reads**. Find
those keys in `appsettings.json`, `builder.Configuration[...]`, `GetConnectionString(...)`, and options classes bound
with `Configure<T>(section)`.

| System | Exposed record | Members |
|---|---|---|
| MongoDB | `MongoDbExposedConfiguration` | `ConnectionString, Database` |
| MySQL | `MySqlExposedConfiguration` | `ConnectionString, Host, Port, Database, Username, Password` |
| Postgres | `PostgresExposedConfiguration` | `ConnectionString, Host, Port, Database, Username, Password` |
| Kafka | `KafkaExposedConfiguration` | `BootstrapServers` |
| Redis | `RedisExposedConfiguration` | `ConnectionString, Host, Port` |
| WireMock | `WireMockExposedConfiguration` | `BaseUrl (Uri), Host, Port` |
| Telemetry | `TelemetryExposedConfiguration` | `Endpoint (Uri)` |

```csharp
.WithPostgres(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)])
.WithWireMock("payments", o => o.ConfigureExposedConfiguration = c => [new("Payments:BaseUrl", c.BaseUrl.ToString())])
```

- Values are applied with `IWebHostBuilder.UseSetting`, which takes the highest precedence. They are visible to code in
  `Program.cs` that runs before `Build()`.
- Nested option keys use `:` (for example `Payments:BaseUrl`).
- Stove fails at startup if two systems expose the same key.

## 3. The fixture: one Stove per test run

```csharp
public static class StoveSetup
{
    public static StoveBuilder Build() => StoveBuilder.Create()
        .WithTelemetry()
        .WithPostgres(o =>
        {
            o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)];
            o.Migrations.Add((ctx, ct) => RunSql(ctx.DataSource, "create table ...", ct));   // only if the app does not migrate itself
        })
        .WithKafka(o =>
        {
            o.ConfigureExposedConfiguration = c => [new("Kafka:BootstrapServers", c.BootstrapServers)];
            o.Migrations.Add((ctx, _) => ctx.Admin.CreateTopicsAsync([new TopicSpecification { Name = "orders.created" }]));
        })
        .WithHttpClient()
        .WithAspNetCoreApplication<Program>();
}
```

**xUnit v3:**

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

**NUnit:** `[SetUpFixture]` with `[OneTimeSetUp]`/`[OneTimeTearDown]` storing a static `Stove`.
**TUnit:** `[Before(TestSession)]`/`[After(TestSession)]` static hooks.
**MSTest:** `[AssemblyInitialize]`/`[AssemblyCleanup]`.
The body is the same in all of them: `StoveSetup.Build().StartAsync()` and `DisposeAsync()`.

xUnit v3 projects on the .NET 10 SDK must opt into Microsoft.Testing.Platform in `global.json`
(`"test": { "runner": "Microsoft.Testing.Platform" }`) and run with `dotnet test --project <path>`.

## 4. Options worth knowing

| Option | Where | Purpose |
|---|---|---|
| `Image` | Postgres/Kafka/Redis options | Container image (defaults `postgres:17-alpine`, `apache/kafka:4.1.0`, `redis:7-alpine`) |
| `ConfigureContainer` | same | `Func<XxxBuilder, XxxBuilder>` for Testcontainers customization |
| `Migrations.Add((ctx, ct) => ..., order)` | same | Runs before the app starts. Postgres ctx: `DataSource`; Kafka ctx: `Admin`; Redis ctx: `Multiplexer` |
| `Cleanup` | same | Runs on dispose |
| `UseExisting(connectionString or bootstrapServers, runMigrations)` | same | Use a running instance instead of a container |
| `ConsumerGroups`, `ErrorTopicSuffixes`, `Serde`, `DefaultTimeout`, `ConfigureClient` | `KafkaOptions` | Consume checks, DLT topics, serialization, waits, security |
| `ScopeStubsToTest`, `KeepStubsAfterTest`, `JsonSerializerOptions`, `Port` | `WireMockOptions` | Parallel-safe stubs, stub lifetime, JSON |
| `BaseAddress`, `DefaultHeaders`, `JsonSerializerOptions`, `Timeout` | `HttpClientOptions` | Test HTTP client |
| `Environment`, `ConfigureWebHost`, `Port` | `AspNetCoreApplicationOptions` | Hosting environment, test-only host customization |
| `FailureFlushWait`, `QuietPeriod`, `ConfigureExposedConfiguration` | `TelemetryOptions` | Telemetry waits; defaults map `OTEL_*` keys |

**Named instances.** Every `WithXxx` has an overload that takes a name first: `.WithRedis("cache", o => ...)`. Use one
WireMock per third-party API.

## 5. Application OpenTelemetry, needed for trace and log attachment

`WithTelemetry()` exposes `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` and short batch
delays as configuration. The app needs a standard OTLP exporter setup; nothing Stove-specific:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("order-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithLogging(l => l.AddOtlpExporter());
```

- If the app has no OpenTelemetry, suggest adding it, since it is production-useful anyway. Otherwise leave out
  `WithTelemetry()`.
- Do not hard-code an exporter endpoint in the app; that overrides Stove's configuration.
- Apps using Confluent.Kafka must copy `traceparent` into produced message headers (`Activity.Current?.Id`) and start a
  consumer activity from it. Confluent.Kafka has no instrumentation.

## MongoDB and MySQL setup

`WithMongoDb(name?, configure)` defaults to `mongo:8.0`, database `stove`, single-node replica set `stove-rs`.
Set `ReplicaSet = null` for standalone (no transactions). `ConfigureClient` receives `MongoClientSettings` and
`ConfigureContainer` receives a `MongoDbBuilder`. Map both `c.ConnectionString` and `c.Database` into app config.
`UseExisting(connectionString, database, runSetup: true)` selects an explicit database without changing the server.
`Setup.Add((ctx, ct) => ..., order)` runs ordered collection/index/seed callbacks with `Client`, `Database`, and
`Configuration`; this is repeated setup, not persisted migration history. `Cleanup` receives the same context.

`WithMySql(name?, configure)` defaults to `mysql:8.4`, database/user/password `stove`, using MySqlConnector.
`ConfigureDataSource` receives `MySqlDataSourceBuilder`; `ConfigureContainer` receives `MySqlBuilder`.
`Migrations` receive `DataSource` and `Configuration`; `Cleanup` receives the data source.
`UseExisting(connectionString, runMigrations: true)` does not provision the external database.
Map `c.ConnectionString` to the application's actual connection-string key.

Both validate connectivity before application startup, even with setup/migrations disabled. Client hooks configure
Stove's client only. Cleanup also runs against external endpoints when configured; Stove disposes its own client
but leaves the external server running. Setup/migrations rerun on every environment start. Use unique test identifiers;
neither module automatically clears data between scopes. MySQL/MongoDB additions are repository implementations;
check package availability before recommending a particular published version.

## Messaging observation and RabbitMQ setup

`KafkaOptions.Observation` and `RabbitMqOptions.Observation` expose `MaxMessagesPerTest` (10,000), `MaxBytesPerTest`
(16 MiB) and `UncorrelatedMessages` (default `UncorrelatedMessagePolicy.Exclude`). Configure these before the system
is constructed. No TTL evicts active evidence: overflow marks it incomplete and fails observation assertions.
`SingleActiveTest` is an explicit fallback for headerless arrivals while only one test is active; it cannot tell a delayed
old message from new work. Matching headers are required for reliable parallel isolation.

`WithRabbitMq(name?, configure)` uses `RabbitMQ.Client` with default `rabbitmq:4.1-alpine`, user/password `stove`.
`ConfigureClient` receives `ConnectionFactory`; automatic recovery is disabled after that hook to avoid hidden gaps.
`ConfigureContainer` receives `RabbitMqBuilder`. `Setup.Add((ctx, ct) => ..., order)` receives `Connection`, temporary
`Channel` and `Configuration`; declare exchanges/work queues/bindings there. `Bindings.Add(new("orders", "orders.#"))`
binds Stove's exclusive queue to a named exchange. Wildcards apply only according to the exchange type.
Map `RabbitMqExposedConfiguration.ConnectionString` to the app's actual configuration key.

`UseExisting(connectionString, runSetup: true)` retains external infrastructure ownership. Cleanup runs with a fresh
channel in either mode; later disposal still runs if it fails. Stove removes its exclusive observer queue but does not
remove user topology unless cleanup explicitly does so. `Connection` is native; callers own channels created from it.
There are no observer bindings by default: publishing/native access works, but assertions require explicit bindings.

### Runnable framework references

In the StoveDotnet repository, `examples/Frameworks` provides independent xUnit v3, NUnit, MSTest and TUnit
examples, and `docs/test-frameworks.md` documents adoption on .NET 10 / Microsoft.Testing.Platform.
MSTest assembly hooks are static methods in a nonstatic `[TestClass]`. Current TUnit uses
`TestContext.Current!.Execution.CancellationToken`; NUnit uses `TestContext.CurrentContext.CancellationToken`,
and MSTest uses the injected `TestContext.CancellationToken`. Do not copy xUnit's token accessor to every framework.

## Multiple applications

Use `WithAspNetCoreApplication<Program>("api", o => o.Configuration["Key"] = "value")` and
`WithHostApplication("worker", WorkerApplication.Build)` (namespace/package `StoveDotnet.Hosting`). The worker
factory receives shared configuration and must apply it before reading options and building the real, unstarted host.
Applications start in registration order after dependencies; `ReadyAsync` finishes before the next app starts.
Stove stops hosts in reverse order before dependencies. Do not use process environment variables for per-host settings.

Bind clients explicitly: `WithHttpClient("api", o => o.ApplicationName = "api")`, then `t.Http("api")`.
`ApplicationName` is independent from the HTTP client name. An explicit BaseAddress overrides application binding.
`t.GetApplication("worker").Services` selects the worker; `t.Using<MyService>("api", action)` selects an API service scope.
Unnamed access resolves the unnamed default or the only application; multiple named apps without a default are ambiguous.
A Generic Host has no HTTP BaseAddress. Existing single-application setup remains valid.

`examples/MultiApplication` verifies HTTP -> RabbitMQ -> worker -> PostgreSQL -> HTTP. See
`docs/multiple-applications.md` for readiness, named failure logs, package setup and in-process isolation limitations.

## Custom dependency containers

`StoveDotnet.Containers` provides `WithContainer(name?, configure)`. Set `CreateContainer` to a factory returning a
fresh, unstarted Testcontainers `IContainer`; use the native builder for image, environment, commands, files and
random ports. Always configure a service-specific `WithWaitStrategy`. `InitializeAsync(container, ct)` optionally
creates buckets/indexes or seeds data after readiness and before application startup. Dispose temporary SDK clients.
`ConfigureExposedConfiguration` receives `c.Container`; resolve `Hostname` and `GetMappedPublicPort` there.
Stove owns the returned container and rollback; do not enable reuse, disable cleanup or return a shared instance.
Already-running containers are rejected without taking ownership. Separate networks/volumes remain caller-owned.
Repository `examples/CustomContainer` demonstrates a MinIO container behind a real API; see `docs/custom-containers.md`.

## Optional test time and OIDC

Add `StoveDotnet.Time` only when application time needs control. Create a fixture-owned Microsoft `FakeTimeProvider`;
call `services.UseStoveTime(clock)` in the worker factory or ASP.NET Core `ConfigureTestServices`. Share that exact
instance only where intentional, and serialize shared-clock mutation. There is no automatic clock or reset.

Add `StoveDotnet.Oidc` for generic signed JWT scenarios. Register `WithOidc(o => ...)` and map its exposed `Issuer`,
`MetadataAddress` and `Audience` through `ConfigureExposedConfiguration` to the application's actual auth keys.
The real bearer handler must use the local metadata address and permit HTTP metadata in tests. Token issuance is
`t.Oidc().IssueToken(o => o.Claims["sub"] = "alice")`; keep provider-specific roles outside the module.
See repository `docs/practical-testing.md` and the Time/Oidc acceptance suites for executable setup.
