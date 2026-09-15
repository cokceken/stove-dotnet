using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MultiApplication.Worker;

public static class WorkerApplication
{
    // This is also the standalone worker's production startup path. No Stove dependency or replacement services.
    public static IHost Build(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder();
        if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Orders")!));
        builder.Services.AddHostedService<OrderWorker>();
        return builder.Build();
    }
}

public sealed class OrderWorker(IConfiguration configuration, NpgsqlDataSource database, ILogger<OrderWorker> logger) : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IConnection? _connection;
    private IChannel? _channel;
    private string? _consumer;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A completed StartAsync means the schema and consumer are ready, not just a background task scheduled.
        await using (var command = database.CreateCommand("select count(*) from processed_orders"))
            await command.ExecuteScalarAsync(cancellationToken);
        _connection = await new ConnectionFactory { Uri = new Uri(configuration["Rabbit:Uri"]!), AutomaticRecoveryEnabled = false }
            .CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await _channel.QueueDeclarePassiveAsync("orders.work", cancellationToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            using var activity = new Activity("worker.process");
            if (args.BasicProperties.Headers?.TryGetValue("traceparent", out var parent) == true && parent is byte[] bytes)
                activity.SetParentId(Encoding.UTF8.GetString(bytes));
            activity.Start();
            var order = JsonSerializer.Deserialize<Order>(args.Body.Span, Json)!;
            await using var command = database.CreateCommand("insert into processed_orders values (@id, @product) on conflict (id) do nothing");
            command.Parameters.AddWithValue("id", order.Id);
            command.Parameters.AddWithValue("product", order.Product);
            await command.ExecuteNonQueryAsync(args.CancellationToken);
            logger.LogInformation("Processed order {OrderId}", order.Id);
            await _channel.BasicAckAsync(args.DeliveryTag, false, args.CancellationToken);
        };
        _consumer = await _channel.BasicConsumeAsync("orders.work", false, consumer, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null && _consumer is not null)
            await _channel.BasicCancelAsync(_consumer, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        try { if (_channel is not null) await _channel.DisposeAsync(); }
        finally { if (_connection is not null) await _connection.DisposeAsync(); }
    }

    private sealed record Order(Guid Id, string Product);
}
