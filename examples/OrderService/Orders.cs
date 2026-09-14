using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;
using StackExchange.Redis;

namespace OrderService;

public sealed record CreateOrderRequest(string ProductId, int Quantity, string CustomerId);

public sealed record Order(Guid Id, string ProductId, int Quantity, string CustomerId, string Status, string? PaymentId);

public sealed record OrderCreated(Guid OrderId, string ProductId, int Quantity, decimal Amount);

public sealed record PaymentCompleted(Guid OrderId, string PaymentId);

public abstract record CreateOrderResult
{
    public sealed record Created(Order Order) : CreateOrderResult;

    public sealed record OutOfStock : CreateOrderResult;

    public sealed record PaymentDeclined : CreateOrderResult;
}

public static class Telemetry
{
    public static readonly ActivitySource ActivitySource = new("OrderService");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class OrderWorkflow(
    InventoryClient inventory,
    PaymentsClient payments,
    OrderRepository repository,
    IConnectionMultiplexer redis,
    IProducer<string, string> producer,
    ILogger<OrderWorkflow> logger)
{
    private const decimal UnitPrice = 10m;

    public async Task<CreateOrderResult> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var available = await inventory.AvailableStock(request.ProductId, ct);
        if (available < request.Quantity)
        {
            logger.LogWarning("Product {ProductId} has {Available} in stock, {Requested} requested", request.ProductId, available, request.Quantity);
            return new CreateOrderResult.OutOfStock();
        }

        var orderId = Guid.NewGuid();
        var amount = request.Quantity * UnitPrice;
        var paymentId = await payments.Charge(orderId, request.CustomerId, amount, ct);
        if (paymentId is null)
        {
            logger.LogWarning("Payment declined for order {OrderId} of customer {CustomerId}", orderId, request.CustomerId);
            return new CreateOrderResult.PaymentDeclined();
        }

        var order = new Order(orderId, request.ProductId, request.Quantity, request.CustomerId, "Created", paymentId);
        await repository.Insert(order, ct);
        await redis.GetDatabase().StringSetAsync($"order:{orderId}", JsonSerializer.Serialize(order, Telemetry.Json));

        var headers = new Headers();
        if (Activity.Current?.Id is { } traceparent)
        {
            // Confluent.Kafka has no built-in instrumentation; propagate the W3C trace context explicitly.
            headers.Add("traceparent", Encoding.UTF8.GetBytes(traceparent));
        }

        await producer.ProduceAsync("orders.created", new Message<string, string>
        {
            Key = orderId.ToString(),
            Value = JsonSerializer.Serialize(new OrderCreated(orderId, request.ProductId, request.Quantity, amount), Telemetry.Json),
            Headers = headers,
        }, ct);

        logger.LogInformation("Order {OrderId} created", orderId);
        return new CreateOrderResult.Created(order);
    }
}

public sealed class InventoryClient(HttpClient http)
{
    private sealed record Stock(int Available);

    public async Task<int> AvailableStock(string productId, CancellationToken ct)
    {
        var stock = await http.GetFromJsonAsync<Stock>($"/stock/{productId}", Telemetry.Json, ct);
        return stock?.Available ?? 0;
    }
}

public sealed class PaymentsClient(HttpClient http)
{
    private sealed record ChargeRequest(Guid OrderId, string CustomerId, decimal Amount);

    private sealed record ChargeResult(string PaymentId);

    public async Task<string?> Charge(Guid orderId, string customerId, decimal amount, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/charges", new ChargeRequest(orderId, customerId, amount), Telemetry.Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return (await response.Content.ReadFromJsonAsync<ChargeResult>(Telemetry.Json, ct))?.PaymentId;
    }
}

public sealed class OrderRepository(NpgsqlDataSource dataSource, IConnectionMultiplexer redis)
{
    public async Task Insert(Order order, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "insert into orders (id, product_id, quantity, customer_id, status, payment_id) values ($1, $2, $3, $4, $5, $6)");
        command.Parameters.Add(new() { Value = order.Id });
        command.Parameters.Add(new() { Value = order.ProductId });
        command.Parameters.Add(new() { Value = order.Quantity });
        command.Parameters.Add(new() { Value = order.CustomerId });
        command.Parameters.Add(new() { Value = order.Status });
        command.Parameters.Add(new() { Value = (object?)order.PaymentId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkPaid(Guid orderId, string paymentId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("update orders set status = 'Paid', payment_id = $2 where id = $1");
        command.Parameters.Add(new() { Value = orderId });
        command.Parameters.Add(new() { Value = paymentId });
        await command.ExecuteNonQueryAsync(ct);
        await redis.GetDatabase().KeyDeleteAsync($"order:{orderId}");
    }

    public async Task<Order?> Find(Guid id, CancellationToken ct)
    {
        var cached = await redis.GetDatabase().StringGetAsync($"order:{id}");
        if (cached.HasValue)
        {
            return JsonSerializer.Deserialize<Order>(cached.ToString(), Telemetry.Json);
        }

        await using var command = dataSource.CreateCommand(
            "select id, product_id, quantity, customer_id, status, payment_id from orders where id = $1");
        command.Parameters.Add(new() { Value = id });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Order(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }
}

public sealed class SchemaInitializer(NpgsqlDataSource dataSource) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            create table if not exists orders (
                id uuid primary key,
                product_id text not null,
                quantity int not null,
                customer_id text not null,
                status text not null,
                payment_id text null)
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PaymentCompletedConsumer(IConfiguration configuration, OrderRepository repository, ILogger<PaymentCompletedConsumer> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"],
            GroupId = "order-service",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe("payments.completed");

        while (!stoppingToken.IsCancellationRequested)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(200));
            if (result is null)
            {
                continue;
            }

            using var activity = StartConsumerActivity(result.Message.Headers);
            var payment = JsonSerializer.Deserialize<PaymentCompleted>(result.Message.Value, Telemetry.Json)!;
            await repository.MarkPaid(payment.OrderId, payment.PaymentId, stoppingToken);
            logger.LogInformation("Order {OrderId} paid with {PaymentId}", payment.OrderId, payment.PaymentId);
            consumer.Commit(result);
        }

        consumer.Close();
    }, stoppingToken);

    private static Activity? StartConsumerActivity(Headers? headers)
    {
        var traceparent = headers is not null && headers.TryGetLastBytes("traceparent", out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        return ActivityContext.TryParse(traceparent, null, out var parent)
            ? Telemetry.ActivitySource.StartActivity("payments.completed process", ActivityKind.Consumer, parent)
            : Telemetry.ActivitySource.StartActivity("payments.completed process", ActivityKind.Consumer);
    }
}
