using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connections = new Dictionary<string, string>
{
    ["Primary"] = builder.Configuration.GetConnectionString("Primary") ?? throw new InvalidOperationException("Primary connection is missing."),
    ["Archive"] = builder.Configuration.GetConnectionString("Archive") ?? throw new InvalidOperationException("Archive connection is missing."),
};
var startup = new Dictionary<string, string>();
foreach (var (name, connectionString) in connections)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "select value from records where id = 'startup'";
    startup[name] = (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Startup migration missing."));
}
var app = builder.Build();
app.MapGet("/startup", () => Results.Ok(new { Primary = startup["Primary"], Archive = startup["Archive"] }));
foreach (var (name, prefix) in new[] { ("Primary", ""), ("Archive", "/archive") })
{
    var connectionString = connections[name];
    app.MapPost(prefix + "/records", async (StoredRecord record, CancellationToken ct) =>
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into records values (@id, @value)";
        command.Parameters.Add(new NpgsqlParameter("id", record.Id));
        command.Parameters.Add(new NpgsqlParameter("value", record.Value));
        await command.ExecuteNonQueryAsync(ct);
        return Results.Created(prefix + "/records/" + record.Id, record);
    });
    app.MapGet(prefix + "/records/{id}", async (string id, CancellationToken ct) =>
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "select value from records where id = @id";
        command.Parameters.Add(new NpgsqlParameter("id", id));
        return await command.ExecuteScalarAsync(ct) is string value ? Results.Ok(new StoredRecord(id, value)) : Results.NotFound();
    });
}
app.Run();
public sealed record StoredRecord(string Id, string Value);
public partial class Program;
