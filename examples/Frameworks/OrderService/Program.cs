using Confluent.Kafka;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderService;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(configuration.GetConnectionString("Orders")).Build());
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")!));
builder.Services.AddSingleton(_ => new ProducerBuilder<string, string>(new ProducerConfig
{
    BootstrapServers = configuration["Kafka:BootstrapServers"],
}).Build());

builder.Services.AddSingleton<OrderRepository>();
builder.Services.AddScoped<OrderWorkflow>();
builder.Services.AddHostedService<SchemaInitializer>();
builder.Services.AddHostedService<PaymentCompletedConsumer>();

builder.Services.AddHttpClient<InventoryClient>(c => c.BaseAddress = new Uri(configuration["Inventory:BaseUrl"]!));
builder.Services.AddHttpClient<PaymentsClient>(c => c.BaseAddress = new Uri(configuration["Payments:BaseUrl"]!));

// Standard OpenTelemetry setup. The OTLP exporter reads OTEL_EXPORTER_OTLP_* from configuration, which is how Stove
// points it at its receiver; in production these come from the environment.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("order-service"))
    .WithTracing(t => t
        .AddSource(Telemetry.ActivitySource.Name)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddNpgsql()
        .AddOtlpExporter())
    .WithLogging(l => l.AddOtlpExporter());

var app = builder.Build();

app.MapPost("/orders", async (CreateOrderRequest request, OrderWorkflow workflow, CancellationToken ct) =>
{
    var result = await workflow.Create(request, ct);
    return result switch
    {
        CreateOrderResult.Created created => Results.Created($"/orders/{created.Order.Id}", created.Order),
        CreateOrderResult.OutOfStock => Results.Conflict(new { error = "out of stock" }),
        CreateOrderResult.PaymentDeclined => Results.StatusCode(StatusCodes.Status402PaymentRequired),
        _ => Results.InternalServerError(),
    };
});

app.MapGet("/orders/{id:guid}", async (Guid id, OrderRepository repository, CancellationToken ct) =>
    await repository.Find(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());

app.Run();

public partial class Program;
