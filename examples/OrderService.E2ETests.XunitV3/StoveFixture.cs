using Confluent.Kafka.Admin;
using StoveDotnet;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Kafka;
using StoveDotnet.Postgres;
using StoveDotnet.Redis;
using StoveDotnet.Telemetry;
using StoveDotnet.WireMock;
using Xunit;

[assembly: AssemblyFixture(typeof(OrderService.E2ETests.StoveFixture))]

namespace OrderService.E2ETests;

/// <summary>
/// One Stove environment for the whole test assembly. This is the only xUnit-specific piece: other frameworks start
/// and dispose the same builder from their own once-per-run hooks.
/// </summary>
public sealed class StoveFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Stove = await StoveBuilder.Create()
            .WithTelemetry()
            .WithPostgres(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)])
            .WithRedis(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Redis", c.ConnectionString)])
            .WithKafka(o =>
            {
                o.ConfigureExposedConfiguration = c => [new("Kafka:BootstrapServers", c.BootstrapServers)];
                o.Migrations.Add((ctx, _) => ctx.Admin.CreateTopicsAsync(
                [
                    new TopicSpecification { Name = "orders.created", NumPartitions = 1, ReplicationFactor = 1 },
                    new TopicSpecification { Name = "payments.completed", NumPartitions = 1, ReplicationFactor = 1 },
                ]));
            })
            .WithWireMock("inventory", o => o.ConfigureExposedConfiguration = c => [new("Inventory:BaseUrl", c.BaseUrl.ToString())])
            .WithWireMock("payments", o => o.ConfigureExposedConfiguration = c => [new("Payments:BaseUrl", c.BaseUrl.ToString())])
            .WithHttpClient()
            .WithAspNetCoreApplication<Program>()
            .StartAsync();
    }

    public async ValueTask DisposeAsync() => await Stove.DisposeAsync();
}
