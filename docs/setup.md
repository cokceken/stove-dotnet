# Environment setup and lifecycle

One `StoveBuilder` describes the dependencies and applications of a test run. Start it once from your test framework's
once-per-run hook (see [test frameworks](test-frameworks.md)) and dispose it from the matching teardown hook.

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
`AggregateException`.

A startup failure is thrown as a `StoveStartupException` whose message names the system or application that failed
and repeats the cause, for example `PostgresSystem 'orders' failed to start: ...`; the original exception is its
`InnerException`. Systems start in parallel, so when several fail, all of them are listed (`Failures`). An application
that fails to start also reports the warnings and errors it logged before giving up. Cancellation is not wrapped. If
rollback also fails, an `AggregateException` contains the startup failure first and the rollback failure second.

## Configuration injection

Each system exposes typed connection details (for example `PostgresExposedConfiguration.ConnectionString`). You map
them to your application's configuration keys with `ConfigureExposedConfiguration`. Stove passes the values as
in-memory configuration with the highest precedence (`IWebHostBuilder.UseSetting`), so they are visible even to code in
`Program.cs` that reads configuration before `Build()`. Two systems exposing the same key is an error.

## Named instances

Every `WithXxx` has an overload that takes a name. Use it when you need several instances of one kind, such as one
WireMock per third-party API or two Redis instances:

- `t.WireMock("payments")` resolves by name.
- `t.WireMock()` resolves the unnamed instance. If there is none, it resolves the single registered instance of that
  kind. If that is ambiguous, it throws and lists the available names.

## Existing instances

Postgres, SQL Server, MongoDB, MySQL, Kafka, RabbitMQ, Azure Service Bus and Redis can use an already running service instead of a container. This is useful for CI services or
shared environments:

```csharp
.WithPostgres(o => o.UseExisting(Environment.GetEnvironmentVariable("ORDERS_DB")!, runMigrations: true))
```

PostgreSQL, SQL Server and MySQL open a connection before starting the application, even when migrations are disabled.
Stove does not stop an existing server. Configured cleanup callbacks still run; `runMigrations: false` only disables
migrations, not cleanup.
