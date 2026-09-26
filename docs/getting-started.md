# Getting started

StoveDotnet starts once for a test run and creates an isolated test scope for every call to `stove.Test`. Begin with
only the application and dependencies that the behavior under test actually uses.

## Requirements

- .NET 10 SDK
- Docker or Podman for container-backed modules
- An existing ASP.NET Core application or Generic Host worker

Your ASP.NET Core entry point must be visible to the test project:

```csharp
public partial class Program;
```

## Install the packages

Add the core package, an application host, and the systems your application uses. Packages are currently preview
releases, so include `--prerelease` when necessary.

```shell
dotnet add package StoveDotnet --prerelease
dotnet add package StoveDotnet.AspNetCore --prerelease
dotnet add package StoveDotnet.Http --prerelease
dotnet add package StoveDotnet.Postgres --prerelease
```

## Start Stove once

Create the fixture from your test framework's once-per-run hook. Map each exposed connection value to the same
configuration key the application reads in production.

```csharp
using StoveDotnet;

Stove stove = await StoveBuilder.Create()
    .WithTelemetry()
    .WithPostgres(options =>
    {
        options.ConfigureExposedConfiguration = postgres =>
            [new("ConnectionStrings:Orders", postgres.ConnectionString)];
    })
    .WithHttpClient()
    .WithAspNetCoreApplication<Program>()
    .StartAsync();
```

Register the application last by convention. Stove starts dependencies first, gathers their configuration, and then
starts the application with those values at the highest configuration precedence.

## Write a test

Every test body runs through `stove.Test`. The context exposes only systems registered by the fixture.

```csharp
[Fact]
public Task Creates_an_order() => stove.Test(async t =>
{
    var response = (await t.Http().Post<OrderDto>(
        "/orders",
        new { productId = "chair" }))
        .Expect(HttpStatusCode.Created);

    await t.Postgres().ShouldQuery(
        "select status from orders where id = @id",
        row => row.GetString(0),
        rows => Assert.Equal(["Created"], rows),
        new NpgsqlParameter("id", response.Body.Id));
});
```

Dispose the shared `Stove` instance from the matching once-per-run teardown hook.

## Add asynchronous behavior

Use the broker module that matches the application. Assertions wait only when the expected behavior is asynchronous;
direct database and broker-state APIs remain explicit.

```csharp
await t.Kafka().ShouldBePublished<OrderCreated>(
    message => message.Value.OrderId == response.Body.Id);
```

For scheduled messages and passwordless Azure namespaces, continue with [Azure Service Bus](azure-service-bus.md).
For correlation, cancellation and parallel-test tradeoffs, read [Practical testing](practical-testing.md).

## Pick your test framework

Stove ships no framework-specific attributes or runners. The [test-framework guide](test-frameworks.md) shows the
once-per-run lifecycle for xUnit, NUnit, MSTest and TUnit, plus isolated package-consumer examples.
