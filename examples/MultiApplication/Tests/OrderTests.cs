using System.Net;
using MultiApplication.Worker;
using RabbitMQ.Client;
using StoveDotnet;
using StoveDotnet.AspNetCore;
using StoveDotnet.Hosting;
using StoveDotnet.Http;
using StoveDotnet.Postgres;
using StoveDotnet.RabbitMq;
using Xunit;

[assembly: AssemblyFixture(typeof(MultiApplication.Tests.EnvironmentFixture))]
namespace MultiApplication.Tests;

public sealed class EnvironmentFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Stove = await Build().StartAsync(TestContext.Current.CancellationToken);

    internal static StoveBuilder Build() => StoveBuilder.Create()
        .WithPostgres(o =>
        {
            o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)];
            o.Migrations.Add(async (ctx, ct) =>
            {
                await using var command = ctx.DataSource.CreateCommand("create table processed_orders (id uuid primary key, product text not null)");
                await command.ExecuteNonQueryAsync(ct);
            });
        })
        .WithRabbitMq(o =>
        {
            o.ConfigureExposedConfiguration = c => [new("Rabbit:Uri", c.ConnectionString)];
            o.Setup.Add(async (ctx, ct) =>
            {
                await ctx.Channel.ExchangeDeclareAsync("orders", ExchangeType.Direct, cancellationToken: ct);
                await ctx.Channel.QueueDeclareAsync("orders.work", false, false, false, cancellationToken: ct);
                await ctx.Channel.QueueBindAsync("orders.work", "orders", "created", cancellationToken: ct);
            });
            o.Bindings.Add(new("orders", "created"));
        })
        .WithHostApplication("worker", WorkerApplication.Build)
        .WithAspNetCoreApplication<Program>("api")
        .WithHttpClient("api", o => o.ApplicationName = "api");

    public async ValueTask DisposeAsync() { GC.SuppressFinalize(this); await Stove.DisposeAsync(); }
}

public sealed class OrderTests(EnvironmentFixture fixture)
{
    [Fact]
    public Task Api_publishes_worker_persists_and_api_reads() => ProcessOrder("chair");

    [Fact]
    public Task Concurrent_requests_remain_independent() => Task.WhenAll(Enumerable.Range(0, 4).Select(i => ProcessOrder("product-" + i)));

    private Task ProcessOrder(string product) => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid();
        (await t.Http("api").Post("/orders", new { id, product })).Expect(HttpStatusCode.Accepted);
        await t.RabbitMq().ShouldBePublished<OrderResponse>(m => m.Value.Id == id);
        // An observed message is insufficient: only the worker can create this persisted result.
        await Eventually.AssertAsync(async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{id}");
            var result = (await t.Http("api").Send<OrderResponse>(request, ct)).Expect(HttpStatusCode.OK);
            Assert.Equal(product, result.Body.Product);
        }, TimeSpan.FromSeconds(10), cancellationToken: t.CancellationToken);
    }, TestContext.Current.CancellationToken);

    private sealed record OrderResponse(Guid Id, string Product);
}
