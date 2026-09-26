using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["ServiceBus:ConnectionString"]
    ?? throw new InvalidOperationException("ServiceBus:ConnectionString is required.");
builder.Services.AddSingleton(new ProcessedMessages());
builder.Services.AddSingleton(new ServiceBusClient(connectionString));
builder.Services.AddHostedService<InboxConsumer>();

var app = builder.Build();
app.MapGet("/processed/{id}", (string id, ProcessedMessages messages) =>
    messages.Values.TryGetValue(id, out var value) ? Results.Ok(new Work(id, value)) : Results.NotFound());
app.Run();

public sealed record Work(string Id, string Value);
public sealed class ProcessedMessages
{
    public ConcurrentDictionary<string, string> Values { get; } = new();
}

public sealed class InboxConsumer(ServiceBusClient client, ProcessedMessages messages, ILogger<InboxConsumer> logger) : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusProcessor _processor = client.CreateProcessor("inbox", new ServiceBusProcessorOptions
    {
        AutoCompleteMessages = false,
        MaxConcurrentCalls = 4
    });

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _processor.ProcessMessageAsync += async args =>
        {
            var work = JsonSerializer.Deserialize<Work>(args.Message.Body.ToMemory().Span, JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException("Inbox message body is required.");
            messages.Values[work.Id] = work.Value;
            await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);
        };
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus inbox processor failed at {ErrorSource}.", args.ErrorSource);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _processor.StopProcessingAsync(cancellationToken);
    public ValueTask DisposeAsync() => _processor.DisposeAsync();
}

public partial class Program;
