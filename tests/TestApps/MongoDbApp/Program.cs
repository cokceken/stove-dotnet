using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
var clients = new List<MongoClient>();
var collections = new Dictionary<string, IMongoCollection<StoredDocument>>();
var startup = new Dictionary<string, string>();
try
{
    foreach (var name in new[] { "Primary", "Archive" })
    {
        var client = new MongoClient(builder.Configuration[$"Mongo:{name}:ConnectionString"]);
        clients.Add(client);
        var collection = client.GetDatabase(builder.Configuration[$"Mongo:{name}:Database"]).GetCollection<StoredDocument>("records");
        collections[name] = collection;
        startup[name] = (await collection.Find(x => x.Value == "ready").SingleAsync()).Value;
    }
    var app = builder.Build();
    app.MapGet("/startup", () => Results.Ok(new { Primary = startup["Primary"], Archive = startup["Archive"] }));
    foreach (var (name, prefix) in new[] { ("Primary", ""), ("Archive", "/archive") })
    {
        var collection = collections[name];
        app.MapPost(prefix + "/records", async (DocumentRequest request, CancellationToken ct) =>
        {
            await collection.InsertOneAsync(new StoredDocument(ObjectId.Parse(request.Id), request.Value), cancellationToken: ct);
            return Results.Created(prefix + "/records/" + request.Id, request);
        });
        app.MapGet(prefix + "/records/{id}", async (string id, CancellationToken ct) =>
        {
            var document = await collection.Find(x => x.Id == ObjectId.Parse(id)).SingleOrDefaultAsync(ct);
            return document is null ? Results.NotFound() : Results.Ok(new DocumentRequest(document.Id.ToString(), document.Value));
        });
    }
    app.Run();
}
finally
{
    foreach (var client in clients) client.Dispose();
}

public sealed record DocumentRequest(string Id, string Value);
public sealed record StoredDocument([property: BsonId] ObjectId Id, [property: BsonElement("value")] string Value);
public partial class Program;
