# End-to-end tests that run the real system

<div class="hero">
  <p class="hero__eyebrow">StoveDotnet for .NET 10</p>
  <p class="hero__lead">Start your real dependencies, inject their connection details into your real application, and test the complete behavior through one focused DSL.</p>
  <div class="hero__actions">
    <a class="button button--primary" href="getting-started/">Build your first test</a>
    <a class="button" href="https://www.nuget.org/packages?q=StoveDotnet">Explore NuGet packages</a>
  </div>
</div>

[![CI](https://github.com/cokceken/stove-dotnet/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/cokceken/stove-dotnet/actions/workflows/ci.yml?query=branch%3Amain)
[![NuGet](https://img.shields.io/nuget/v/StoveDotnet.svg)](https://www.nuget.org/packages/StoveDotnet)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/cokceken/stove-dotnet/badge)](https://scorecard.dev/viewer/?uri=github.com/cokceken/stove-dotnet)

```csharp
[Fact]
public Task Creates_order_when_stock_is_available() => stove.Test(async t =>
{
    var response = (await t.Http().Post<Order>("/orders", new { productId = "chair" }))
        .Expect(HttpStatusCode.Created);

    await t.Postgres().ShouldQuery(
        "select status from orders where id = @id",
        row => row.GetString(0),
        rows => Assert.Equal(["Created"], rows),
        new NpgsqlParameter("id", response.Body.Id));

    await t.Kafka().ShouldBePublished<OrderCreated>(
        message => message.Value.OrderId == response.Body.Id);
});
```

## Why StoveDotnet?

<div class="feature-grid">
  <article class="feature-card">
    <h3>Real application</h3>
    <p>Your ASP.NET Core API or Generic Host runs in-process on a real port. Stove changes configuration, not application behavior.</p>
  </article>
  <article class="feature-card">
    <h3>Real dependencies</h3>
    <p>Databases, brokers and caches run through Testcontainers. WireMock handles only the third-party APIs you do not own.</p>
  </article>
  <article class="feature-card">
    <h3>Failure evidence</h3>
    <p>Correlated traces, logs and broker observations are attached to the test that caused them, so failures explain themselves.</p>
  </article>
  <article class="feature-card">
    <h3>Framework neutral</h3>
    <p>Use xUnit, NUnit, MSTest or TUnit and keep your existing assertion library. Stove owns no custom test runner.</p>
  </article>
</div>

## Supported systems

| Area | Modules |
|---|---|
| Applications | ASP.NET Core, Generic Host workers, HTTP clients |
| Databases | PostgreSQL, SQL Server, MongoDB, MySQL |
| Messaging | Kafka, RabbitMQ, Azure Service Bus |
| Infrastructure | Redis, OIDC, controllable time, telemetry |
| Third parties | WireMock and user-supplied dependency containers |

Each module exposes its native client and preserves the behavior that matters for production. Broker observation is
correlated and bounded; database assertions use the real provider; Azure namespaces support `TokenCredential` for
managed-identity-compatible tests.

## Choose a path

- [Build your first Stove test](getting-started.md)
- [Review verified compatibility and security evidence](compatibility.md)
- [Understand correlation, waiting and isolation](practical-testing.md)
- [Test an API and worker together](multiple-applications.md)
- [Use Kafka and RabbitMQ safely in parallel tests](messaging.md)
- [Test queues, topics and scheduled Azure Service Bus messages](azure-service-bus.md)

!!! note "Preview software"
    StoveDotnet is evolving toward its first stable API. Pin package versions in application repositories and read the
    GitHub release notes before upgrading.
