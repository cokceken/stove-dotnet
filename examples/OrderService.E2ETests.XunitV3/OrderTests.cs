using System.Net;
using Npgsql;
using OrderService.E2ETests.Fakes;
using StoveDotnet;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Kafka;
using StoveDotnet.Postgres;
using StoveDotnet.Redis;
using StoveDotnet.Telemetry;
using Xunit;

namespace OrderService.E2ETests;

public sealed class OrderTests(StoveFixture fixture)
{
    private readonly Stove _stove = fixture.Stove;

    [Fact]
    public Task Creates_order_when_stock_is_available_and_payment_succeeds() => _stove.Test(async t =>
    {
        t.InventoryFake().StockAvailable("chair", available: 5);
        var payments = t.PaymentsFake().ChargeSucceeds(paymentId: "pay-1");

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
        var charge = await payments.ShouldHaveCharged(c => c.OrderId == order.Id);
        Assert.Equal(20m, charge.Amount);
        await t.Telemetry().ShouldContainSpan(s => s.Kind == "Server" && s.Attribute("http.route") as string == "/orders");
    });

    [Fact]
    public Task Rejects_order_when_out_of_stock_without_charging() => _stove.Test(async t =>
    {
        var inventory = t.InventoryFake().StockAvailable("table", available: 0);

        var response = await t.Http().Post("/orders", new CreateOrderRequest("table", 1, "customer-2"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await inventory.ShouldHaveCheckedStock("table");
        await t.PaymentsFake().ShouldNotHaveCharged();
        Assert.Empty(t.Kafka().Peek<OrderCreated>("orders.created"));
    });

    [Fact]
    public Task Returns_payment_required_when_the_charge_is_declined() => _stove.Test(async t =>
    {
        t.InventoryFake().StockAvailable("desk", available: 1);
        var payments = t.PaymentsFake().ChargeDeclined();

        var response = await t.Http().Post("/orders", new CreateOrderRequest("desk", 1, "customer-5"));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        await payments.ShouldHaveCharged(c => c.CustomerId == "customer-5" && c.Amount == 10m);
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
        t.InventoryFake().StockAvailable("sofa", available: 3);
        t.PaymentsFake().PaymentsUnavailable();

        var response = await t.Http().Post("/orders", new CreateOrderRequest("sofa", 1, "customer-4"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    });
}
