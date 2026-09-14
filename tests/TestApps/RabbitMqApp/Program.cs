using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var builder = WebApplication.CreateBuilder(args);
var brokers = new Dictionary<string, BrokerClient>();
try
{
    foreach (var name in new[] { "Primary", "Archive" })
        brokers[name] = await BrokerClient.Open(builder.Configuration[$"Rabbit:{name}"] ?? throw new InvalidOperationException("Missing broker configuration."));
    var app = builder.Build();
    app.MapGet("/startup", () => Results.Ok(new { Ready = brokers.Values.All(b => b.Ready) }));
    foreach (var (name, prefix) in new[] { ("Primary", ""), ("Archive", "/archive") })
    {
        var broker = brokers[name];
        app.MapPost(prefix + "/publish", async (Work request, HttpContext context, CancellationToken ct) =>
        {
            var headers = new Dictionary<string, object?>();
            foreach (var header in new[] { "X-Stove-Test-Id", "traceparent" })
                if (context.Request.Headers.TryGetValue(header, out var value)) headers[header] = Encoding.UTF8.GetBytes(value.ToString());
            await broker.Publish(request, headers, ct);
            return Results.Accepted();
        });
        app.MapGet(prefix + "/processed/{id}", (string id) => broker.Processed.TryGetValue(id, out var value) ? Results.Ok(value) : Results.NotFound());
    }
    app.Run();
}
finally
{
    foreach (var broker in brokers.Values) await broker.DisposeAsync();
}

public sealed record Work(string Id, string Value);
public sealed class BrokerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _worker;
    private IChannel? _publisher;
    public ConcurrentDictionary<string, Work> Processed { get; } = new(StringComparer.Ordinal);
    public bool Ready { get; private set; }

    public static async Task<BrokerClient> Open(string connectionString)
    {
        var client = new BrokerClient();
        try
        {
            client._connection = await new ConnectionFactory { Uri = new Uri(connectionString), AutomaticRecoveryEnabled = false }.CreateConnectionAsync();
            client._worker = await client._connection.CreateChannelAsync(new CreateChannelOptions(true, true));
            client._publisher = await client._connection.CreateChannelAsync(new CreateChannelOptions(true, true));
            // The app requires topology setup to be complete before it starts.
            await client._worker.ExchangeDeclarePassiveAsync("orders");
            await client._worker.QueueDeclarePassiveAsync("orders.work");
            var consumer = new AsyncEventingBasicConsumer(client._worker);
            consumer.ReceivedAsync += async (_, args) =>
            {
                var request = JsonSerializer.Deserialize<Work>(args.Body.Span, Json)!;
                var headers = args.BasicProperties.Headers;
                var failed = request.Value == "fail";
                var result = request with { Value = failed ? "rejected" : request.Value.ToUpperInvariant() };
                if (!failed) client.Processed[request.Id] = result;
                await client._worker.BasicPublishAsync("orders", failed ? "orders.failed" : "orders.processed", true,
                    new BasicProperties { Headers = headers }, JsonSerializer.SerializeToUtf8Bytes(result, Json), args.CancellationToken);
                await client._worker.BasicAckAsync(args.DeliveryTag, false, args.CancellationToken);
            };
            await client._worker.BasicConsumeAsync("orders.work", false, consumer);
            client.Ready = true;
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task Publish(Work work, IDictionary<string, object?> headers, CancellationToken ct)
    {
        await _publishLock.WaitAsync(ct);
        try { await _publisher!.BasicPublishAsync("orders", "orders.created", true, new BasicProperties { Headers = headers }, JsonSerializer.SerializeToUtf8Bytes(work, Json), ct); }
        finally { _publishLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_worker is not null) await _worker.DisposeAsync();
        if (_publisher is not null) await _publisher.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _publishLock.Dispose();
    }
}
public partial class Program;
