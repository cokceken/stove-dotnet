using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Read before Build() on purpose: proves Stove's configuration is visible to top-level startup code.
var greeting = builder.Configuration["TestApp:Greeting"]
    ?? throw new InvalidOperationException("TestApp:Greeting is not configured.");

builder.Services.AddSingleton(new GreetingService(greeting));
builder.Services.AddHttpClient("downstream", client =>
    client.BaseAddress = new Uri(builder.Configuration["Downstream:BaseUrl"] ?? "http://localhost:1"));

if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("test-app"))
        .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
        .WithLogging(l => l.AddOtlpExporter());
}

var app = builder.Build();

app.MapGet("/greeting", (GreetingService service) => Results.Ok(new Greeting(service.Message)));

app.MapPost("/echo", (Greeting body) => Results.Created("/echo", body));

app.MapGet("/downstream/{id}", async (string id, IHttpClientFactory factory, ILogger<GreetingService> logger) =>
{
    logger.LogInformation("Calling downstream for {Id}", id);
    using var response = await factory.CreateClient("downstream").GetAsync($"/items/{id}");
    return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
});

app.MapGet("/boom", (ILogger<GreetingService> logger) =>
{
    logger.LogError("About to fail");
    throw new InvalidOperationException("boom");
});

app.Run();

public sealed record Greeting(string Message);

public sealed class GreetingService(string message)
{
    public string Message { get; } = message;
}

public partial class Program;
