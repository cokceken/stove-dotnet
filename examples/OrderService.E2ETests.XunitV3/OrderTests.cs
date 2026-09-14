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
using StoveDotnet.WireMock;
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
        var orderId = Guid.NewGuid();
        var inventory = t.InventoryFake().StockAvailable("table", available: 0);

        var response = await t.Http().Post("/orders", new CreateOrderRequest("table", 1, "customer-2", orderId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await inventory.ShouldHaveCheckedStock("table");
        await t.PaymentsFake().ShouldNotHaveCharged();
        await AssertNoOrderSideEffects(t, orderId);
    });

    [Fact]
    public Task Returns_payment_required_when_the_charge_is_declined() => _stove.Test(async t =>
    {
        var orderId = Guid.NewGuid();
        t.InventoryFake().StockAvailable("desk", available: 1);
        var payments = t.PaymentsFake().ChargeDeclined();

        var response = await t.Http().Post("/orders", new CreateOrderRequest("desk", 1, "customer-5", orderId));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        await payments.ShouldHaveCharged(c => c.CustomerId == "customer-5" && c.Amount == 10m);
        await AssertNoOrderSideEffects(t, orderId);
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
        var response = await t.Http().Get<Order>($"/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Paid", response.Body.Status);
        Assert.Equal("pay-3", response.Body.PaymentId);
        Assert.False(await t.Redis().Database().KeyExistsAsync($"order:{orderId}"));
    });

    [Fact]
    public Task Retrieves_a_created_order_through_http() => _stove.Test(async t =>
    {
        t.InventoryFake().StockAvailable("book", available: 1);
        t.PaymentsFake().ChargeSucceeds("pay-book");
        var created = await t.Http().Post<Order>("/orders", new CreateOrderRequest("book", 1, Guid.NewGuid().ToString()));
        var retrieved = await t.Http().Get<Order>($"/orders/{created.Body.Id}");
        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.Equal(created.Body, retrieved.Body);
    });

    [Fact]
    public Task Missing_order_returns_not_found() => _stove.Test(async t =>
        Assert.Equal(HttpStatusCode.NotFound, (await t.Http().Get($"/orders/{Guid.NewGuid()}")).StatusCode));

    [Fact]
    public async Task Concurrent_orders_use_their_own_payment_stubs_and_observations()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => _stove.Test(async t =>
        {
            var orderId = Guid.NewGuid();
            var paymentId = $"parallel-payment-{index}";
            // Every scope deliberately stubs the same URLs differently.
            t.InventoryFake().StockAvailable("shared-product", available: index + 1);
            var payments = t.PaymentsFake().ChargeSucceeds(paymentId);
            if (Interlocked.Increment(ref count) == 4) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), t.CancellationToken);
            var response = await t.Http().Post<Order>("/orders", new CreateOrderRequest("shared-product", index + 1, orderId.ToString(), orderId));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(paymentId, response.Body.PaymentId);
            await payments.ShouldHaveCharged(c => c.OrderId == orderId && c.Amount == (index + 1) * 10m);
            await t.Postgres().ShouldQuery("select payment_id from orders where id = @id", r => r.GetString(0),
                rows => Assert.Equal([paymentId], rows), new NpgsqlParameter("id", orderId));
            await t.Kafka().ShouldBePublished<OrderCreated>(m => m.Value.OrderId == orderId);
            Assert.All(t.Kafka().Peek<OrderCreated>("orders.created"), m => Assert.Equal(orderId, m.Value.OrderId));
        }, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Failed_workflow_assertion_includes_dependency_diagnostics()
    {
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => _stove.Test(async t =>
        {
            t.InventoryFake().StockAvailable("diagnostics", available: 1);
            t.PaymentsFake().PaymentsUnavailable();
            var response = await t.Http().Post("/orders", new CreateOrderRequest("diagnostics", 1, "diagnostics"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }, TestContext.Current.CancellationToken));
        Assert.IsType<Xunit.Sdk.EqualException>(error.InnerException);
        Assert.Contains(error.Details, d => d.Title.Contains("telemetry", StringComparison.Ordinal));
        Assert.Contains(error.Details, d => d.Title.Contains("payments", StringComparison.Ordinal));
    }

    private static async Task AssertNoOrderSideEffects(StoveTestContext t, Guid orderId)
    {
        await t.Postgres().ShouldQuery("select id from orders where id = @id", r => r.GetGuid(0),
            rows => Assert.Empty(rows), new NpgsqlParameter("id", orderId));
        Assert.False(await t.Redis().Database().KeyExistsAsync($"order:{orderId}"));
        await t.Kafka().ShouldNotBePublished<OrderCreated>(m => m.Value.OrderId == orderId,
            within: TimeSpan.FromSeconds(1), topic: "orders.created");
    }

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
