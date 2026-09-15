using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Orders")
    ?? throw new InvalidOperationException("Orders connection string is missing.")));
builder.Services.AddHttpClient("catalog", client => client.BaseAddress = new Uri(builder.Configuration["Catalog:BaseUrl"]
    ?? throw new InvalidOperationException("Catalog URL is missing.")));
var app = builder.Build();
// Use the same startup path as production: fail if the schema was not prepared.
await using (var command = app.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("select count(*) from orders"))
    await command.ExecuteScalarAsync();
app.MapPost("/orders", async (CreateOrder request, NpgsqlDataSource database, IHttpClientFactory clients, CancellationToken ct) =>
{
    var quote = await clients.CreateClient("catalog").GetFromJsonAsync<Price>("/products/" + Uri.EscapeDataString(request.Product), ct);
    await using var command = database.CreateCommand("insert into orders (id, product, amount) values (@id, @product, @amount)");
    command.Parameters.AddWithValue("id", request.Id);
    command.Parameters.AddWithValue("product", request.Product);
    command.Parameters.AddWithValue("amount", quote!.Amount);
    await command.ExecuteNonQueryAsync(ct);
    return Results.Created("/orders/" + request.Id, new Order(request.Id, request.Product, quote.Amount));
});
app.MapGet("/orders/{id:guid}", async (Guid id, NpgsqlDataSource database, CancellationToken ct) =>
{
    await using var command = database.CreateCommand("select product, amount from orders where id = @id");
    command.Parameters.AddWithValue("id", id);
    await using var reader = await command.ExecuteReaderAsync(ct);
    return await reader.ReadAsync(ct) ? Results.Ok(new Order(id, reader.GetString(0), reader.GetDecimal(1))) : Results.NotFound();
});
app.Run();

public sealed record CreateOrder(Guid Id, string Product);
public sealed record Order(Guid Id, string Product, decimal Amount);
public sealed record Price(decimal Amount);
public partial class Program;
