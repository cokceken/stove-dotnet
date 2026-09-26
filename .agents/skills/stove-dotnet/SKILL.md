---
name: stove-dotnet
description: Use when adding StoveDotnet to a .NET project, writing or debugging StoveDotnet end-to-end tests (stove.Test, databases, Kafka, RabbitMQ, Azure Service Bus, Redis, WireMock, HTTP, telemetry), reading a StoveTestFailedException, or generating typed WireMock fakes for third-party APIs from OpenAPI/Swagger specs, SDKs or other API definitions.
---

# StoveDotnet skill router

StoveDotnet runs e2e tests against the real application. Dependencies run in Testcontainers or in-process WireMock.
Their connection details are injected into the app's configuration, and the app runs in-process on a real Kestrel port.
Every test runs inside `stove.Test(async t => ...)`.

Use this file as the entry point only. Open the guide that matches the task. When an API detail is uncertain, check the
package's XML docs or source (github.com/cokceken/stove-dotnet, `src/`) before writing code. Do not guess.

## First checks

1. **Test framework.** Stove ships no framework extensions. Find the once-per-run hook: xUnit v3 `AssemblyFixture`,
   NUnit `[SetUpFixture]`, TUnit `[Before(TestSession)]`, MSTest `[AssemblyInitialize]`.
2. **Application entry point.** The ASP.NET Core `Program` must be reachable: `public partial class Program;`.
3. **Dependencies.** Find the databases, brokers, caches and third-party HTTP APIs, plus the configuration keys the app
   reads for each (`appsettings.json`, `Program.cs`, options classes).
4. **Existing Stove setup.** Search for `StoveBuilder.Create()`. Extend the existing fixture instead of creating a
   second one.

## Route by task

| Task | Open |
|---|---|
| Add Stove to a project, configure systems, fixture per framework, app OpenTelemetry setup | `setup.md` |
| Write or change tests, module DSL signatures, correlation and parallel tests | `writing-tests.md` |
| Fake a third-party API from an OpenAPI/Swagger spec, SDK or docs (typed fake classes) | `openapi-fakes.md` |
| A test fails and the output contains `--- Stove: ... ---` sections, or tests hang or time out | `diagnosing-failures.md` |

## API anchors

```csharp
// Once per test run (fixture/hook), not per test.
Stove stove = await StoveBuilder.Create()
    .WithTelemetry()
    .WithPostgres(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)])
    .WithKafka(o => o.ConfigureExposedConfiguration = c => [new("Kafka:BootstrapServers", c.BootstrapServers)])
    .WithRedis(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Redis", c.ConnectionString)])
    .WithWireMock("payments", o => o.ConfigureExposedConfiguration = c => [new("Payments:BaseUrl", c.BaseUrl.ToString())])
    .WithHttpClient()
    .WithAspNetCoreApplication<Program>()   // registered last by convention
    .StartAsync();

// Every test.
[Fact]
public Task Creates_order() => stove.Test(async t =>
{
    var response = await t.Http().Post<OrderDto>("/orders", new { productId = "chair" });
    await t.Postgres().ShouldQuery("select status from orders where id = @id", r => r.GetString(0),
        rows => Assert.Equal(["Created"], rows), new NpgsqlParameter("id", response.Body.Id));
    await t.Kafka().ShouldBePublished<OrderCreated>(m => m.Value.OrderId == response.Body.Id);
});
```

Namespaces:

| Namespace | Provides |
|---|---|
| `StoveDotnet` | `StoveBuilder`, `Stove`, `StoveTestContext`, `Eventually`, exceptions |
| `StoveDotnet.AspNetCore` | `WithAspNetCoreApplication`, `t.Using<T>()` |
| `StoveDotnet.Http` | `WithHttpClient`, `t.Http()` |
| `StoveDotnet.Containers` | `WithContainer`, `t.Container(name?).Container` for user-supplied Testcontainers dependencies |
| `StoveDotnet.Time` | Opt-in `UseStoveTime(clock)`, `t.Clock(application?)` |
| `StoveDotnet.Oidc` | `WithOidc`, `t.Oidc(name?).IssueToken()` |
| `StoveDotnet.Postgres` | `WithPostgres`, `t.Postgres()` |
| `StoveDotnet.MongoDb` | `WithMongoDb`, `t.MongoDb()` |
| `StoveDotnet.MySql` | `WithMySql`, `t.MySql()` |
| `StoveDotnet.Kafka` | `WithKafka`, `t.Kafka()` |
| `StoveDotnet.RabbitMq` | `WithRabbitMq`, `t.RabbitMq()` |
| `StoveDotnet.Azure.ServiceBus` | `WithAzureServiceBus`, `t.AzureServiceBus()` |
| `StoveDotnet.Redis` | `WithRedis`, `t.Redis()` |
| `StoveDotnet.WireMock` | `WithWireMock`, `t.WireMock()`, `PathTemplate`, `RecordedRequest` |
| `StoveDotnet.Telemetry` | `WithTelemetry`, `t.Telemetry()` |

## Guardrails

- Every e2e test body goes through `stove.Test`. Do not call `t.Xxx()` DSL methods outside it, and do not nest
  `stove.Test` calls; both throw.
- Do not invent framework extensions or attributes (no `[StoveTest]`, no `UseStove()`). None exist.
- Do not add a test name parameter. The name comes from the calling method.
- Stove does not automatically replace application services. Do not register fakes into the app's DI container to stand in for a
  dependency Stove already provides. Point the app at the Stove system through configuration instead.
  `UseStoveTime` is an explicit exception: the fixture supplies a Microsoft `FakeTimeProvider` for application time.
- Exposed configuration keys must be unique across systems. Use the app's real keys.
- Named instances: `t.WireMock("payments")`. `t.WireMock()` resolves the unnamed instance, or the only instance of
  that kind. If several exist and none is unnamed, it throws.
- `ShouldHaveBeenCalled(method, path, times = 1, within = null)` checks an **exact** count. Pass `within` only when the
  app calls asynchronously (background work, consumers).
- WireMock stubs created in a test are removed when the test ends. Re-create them in each test.
- `ShouldBeConsumed` needs a consumer group that **commits** offsets. Pass the app's group id with `consumerGroup:`.
- Kafka publishes default to System.Text.Json web settings (camelCase). Messages that fail to deserialize to `T` never
  match.
- General assertions use the project's existing assertion library. HTTP responses also offer fluent `Expect(status)`.
- Keep setups minimal: register only the systems the app under test actually uses.

- Kafka and RabbitMQ observation is strict by default: propagate matching test id or valid `traceparent`; both must
  agree when present. Headerless data is excluded unless `Observation.UncorrelatedMessages = SingleActiveTest`.
- Observation is bounded per test (10,000 messages / 16 MiB by default). Overflow fails assertions explicitly;
  finished-test records are not replayed. Fallback uses observer arrival time and cannot identify delayed old messages.
- RabbitMQ observes dedicated exchange bindings on its own queue. Publisher confirms, routing and observed copies do
  not prove application processing; verify a business side effect instead.
- Azure Service Bus `Peek`/`ShouldBeScheduled` are non-destructive, but `Receive` locks the real queue/subscription.
  Never overlap it with the application or another test receiver on the same entity. Correlation cannot prevent a
  competing receiver from acquiring the message first; verify application consumption through a business side effect.
