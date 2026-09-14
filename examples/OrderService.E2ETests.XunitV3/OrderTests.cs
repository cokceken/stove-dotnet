using System.Net;
using Npgsql;
using StoveDotnet;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Kafka;
using StoveDotnet.Postgres;
using StoveDotnet.Redis;
using StoveDotnet.Telemetry;
using StoveDotnet.WireMock;
using Xunit;

namespace OrderService.E2ETests;

public sealed class OrderTests(StoveFixture fixture)
{
    private readonly Stove _stove = fixture.Stove;

    [Fact]
    public Task Creates_order_when_stock_is_available_and_payment_succeeds() => _stove.Test(async t =>
    {
        t.WireMock("inventory").MockGet("/stock/chair", responseBody: new { available = 5 });
        t.WireMock("payments").MockPost("/charges", responseBody: new { paymentId = "pay-1" });

        var response = await t.Http().Post<Order>("/orders", new CreateOrderRequest("chair", 2, "customer-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = response.Body;

        await t.Postgres().ShouldQuery(
            "select status, payment_id from orders where id = @id",
            r => (Status: r.GetString(0), PaymentId: r.GetString(1)),
            rows => Assert.Equal([("Created", "pay-1")], rows),
            new NpgsqlParameter("id", order.Id));

        Assert.True(await t.Redis().Database().KeyExistsAsync($"order:{order.Id}"));

        await t.Kafka().ShouldBePublished<OrderCreated>(m => m.Value.OrderId == order.Id && m.Value.Amount == 20m);
        await t.WireMock("payments").ShouldHaveBeenCalled("POST", "/charges");
        await t.Telemetry().ShouldContainSpan(s => s.Kind == "Server" && s.Attribute("http.route") as string == "/orders");
    });

    [Fact]
    public Task Rejects_order_when_out_of_stock_without_charging() => _stove.Test(async t =>
    {
        t.WireMock("inventory").MockGet("/stock/table", responseBody: new { available = 0 });

        var response = await t.Http().Post("/orders", new CreateOrderRequest("table", 1, "customer-2"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await t.WireMock("payments").ShouldNotHaveBeenCalled("POST", "/charges");
        Assert.Empty(t.Kafka().Peek<OrderCreated>("orders.created"));
    });

    [Fact]
    public Task Marks_order_paid_when_payment_completed_is_consumed() => _stove.Test(async t =>
    {
        var orderId = Guid.NewGuid();
        await t.Postgres().Execute(
            "insert into orders (id, product_id, quantity, customer_id, status) values (@id, 'lamp', 1, 'customer-3', 'Created')",
            new NpgsqlParameter("id", orderId));

        await t.Kafka().Publish("payments.completed", new PaymentCompleted(orderId, "pay-3"), key: orderId.ToString());

        await t.Kafka().ShouldBeConsumed<PaymentCompleted>(m => m.Value.OrderId == orderId, consumerGroup: "order-service");
        await t.Using<OrderRepository>(async repository =>
        {
            var order = await repository.Find(orderId, t.CancellationToken);
            Assert.Equal("Paid", order?.Status);
        });
    });

    // Fails on purpose to show Stove's failure output (trace tree, logs, observed calls and messages).
    // Run it explicitly: dotnet test --project examples/OrderService.E2ETests.XunitV3 -- --explicit only
    [Fact(Explicit = true)]
    public Task Failure_output_demo() => _stove.Test(async t =>
    {
        t.WireMock("inventory").MockGet("/stock/sofa", responseBody: new { available = 3 });
        t.WireMock("payments").MockPost("/charges", statusCode: 500);

        var response = await t.Http().Post("/orders", new CreateOrderRequest("sofa", 1, "customer-4"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    });
}
