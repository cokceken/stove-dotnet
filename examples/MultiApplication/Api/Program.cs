using System.Text;
using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Orders")!));
var app = builder.Build();
app.MapPost("/orders", async (CreateOrder order, HttpContext context, CancellationToken ct) =>
{
    // Short-lived native connections keep this example focused on the cross-application contract.
    await using var connection = await new ConnectionFactory { Uri = new Uri(builder.Configuration["Rabbit:Uri"]!) }.CreateConnectionAsync(ct);
    await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), ct);
    var headers = new Dictionary<string, object?>();
    foreach (var key in new[] { "traceparent", "X-Stove-Test-Id" })
        if (context.Request.Headers.TryGetValue(key, out var value)) headers[key] = Encoding.UTF8.GetBytes(value.ToString());
    await channel.BasicPublishAsync("orders", "created", true, new BasicProperties { Headers = headers },
        JsonSerializer.SerializeToUtf8Bytes(order, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);
    return Results.Accepted($"/orders/{order.Id}");
});
app.MapGet("/orders/{id:guid}", async (Guid id, NpgsqlDataSource database, CancellationToken ct) =>
{
    await using var command = database.CreateCommand("select product from processed_orders where id = @id");
    command.Parameters.AddWithValue("id", id);
    var product = await command.ExecuteScalarAsync(ct);
    return product is string value ? Results.Ok(new CreateOrder(id, value)) : Results.NotFound();
});
app.Run();

public sealed record CreateOrder(Guid Id, string Product);
public partial class Program;
