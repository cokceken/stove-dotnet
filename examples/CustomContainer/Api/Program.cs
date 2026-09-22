using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

var builder = WebApplication.CreateBuilder(args);
var endpoint = new Uri(builder.Configuration["Storage:Endpoint"]!);
var bucket = builder.Configuration["Storage:Bucket"]!;
builder.Services.AddSingleton<IMinioClient>(_ => new MinioClient()
    .WithEndpoint(endpoint.Host, endpoint.Port)
    .WithCredentials(builder.Configuration["Storage:AccessKey"], builder.Configuration["Storage:SecretKey"])
    .WithSSL(endpoint.Scheme == "https").Build());
var app = builder.Build();
// The bucket must be initialized before the application starts listening.
if (!await app.Services.GetRequiredService<IMinioClient>().BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket)))
    throw new InvalidOperationException("Storage bucket has not been initialized.");
app.MapPut("/objects/{key}", async (string key, Upload upload, IMinioClient client, CancellationToken ct) =>
{
    using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(upload.Content));
    await client.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(key)
        .WithStreamData(body).WithObjectSize(body.Length).WithContentType("text/plain"), ct);
    return Results.Created($"/objects/{key}", new { key });
});
app.MapGet("/objects/{key}", async (string key, IMinioClient client, CancellationToken ct) =>
{
    try
    {
        using var body = new MemoryStream();
        await client.GetObjectAsync(new GetObjectArgs().WithBucket(bucket).WithObject(key)
            .WithCallbackStream((stream, token) => stream.CopyToAsync(body, token)), ct);
        return Results.Text(System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }
    catch (ObjectNotFoundException) { return Results.NotFound(); }
});
app.Run();

public sealed record Upload(string Content);
public partial class Program;
