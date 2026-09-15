using System.Text.Json;
using Npgsql;
using StoveDotnet;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Postgres;
using StoveDotnet.WireMock;

namespace FrameworkExamples;

// No test framework dependency. Each example supplies its own lifecycle hooks and assertion library.
public sealed class ExampleEnvironment : IAsyncDisposable
{
    private int _starts;
    private int _stops;
    private PostgresSystem? _postgres;
    public Stove Stove { get; private set; } = null!;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _starts) != 1) throw new InvalidOperationException("Environment started more than once.");
        Stove = await StoveBuilder.Create()
            .WithPostgres(o =>
            {
                o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)];
                o.Migrations.Add(async (ctx, token) =>
                {
                    await using var command = ctx.DataSource.CreateCommand("create table orders (id uuid primary key, product text not null, amount numeric not null)");
                    await command.ExecuteNonQueryAsync(token);
                });
            })
            .WithWireMock("catalog", o =>
            {
                o.ScopeStubsToTest = true;
                o.ConfigureExposedConfiguration = c => [new("Catalog:BaseUrl", c.BaseUrl.ToString())];
            })
            .WithHttpClient().WithAspNetCoreApplication<Program>().StartAsync(ct);
        await Stove.Test(t => { _postgres = t.Postgres(); return Task.CompletedTask; }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Interlocked.Increment(ref _stops) != 1) throw new InvalidOperationException("Environment stopped more than once.");
        if (Stove is null) return;
        await Stove.DisposeAsync();
        // Teardown verification belongs to this example harness, not adopters' ordinary fixtures.
        var clientDisposed = false;
        try { await using var unexpected = await _postgres!.DataSource.OpenConnectionAsync(); }
        catch (ObjectDisposedException) { clientDisposed = true; }
        if (!clientDisposed) throw new InvalidOperationException("Stove did not dispose its native client.");
        var endpointRemoved = false;
        var settings = new NpgsqlConnectionStringBuilder(_postgres!.ExposedConfiguration.ConnectionString) { Pooling = false, Timeout = 1 };
        try { await using var unexpected = new NpgsqlConnection(settings.ConnectionString); await unexpected.OpenAsync(); }
        catch (NpgsqlException) { endpointRemoved = true; }
        if (!endpointRemoved) throw new InvalidOperationException("Stove left its managed database reachable.");
        if (Environment.GetEnvironmentVariable("STOVE_FRAMEWORK_LIFECYCLE_FILE") is { } path)
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Starts = _starts, Stops = _stops, ClientDisposed = clientDisposed, EndpointRemoved = endpointRemoved }));
    }
}

// HTTP contracts deliberately separate from the application's CLR types.
public sealed record OrderRequest(Guid Id, string Product);
public sealed record OrderResponse(Guid Id, string Product, decimal Amount);
