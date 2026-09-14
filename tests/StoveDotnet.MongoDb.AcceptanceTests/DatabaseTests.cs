using System.Net;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.MongoDb;
using Xunit;

namespace StoveDotnet.MongoDb.AcceptanceTests;

public sealed record TestDocument([property: BsonId] ObjectId Id, [property: BsonElement("value")] string Value);
public sealed record Request(string Id, string Value);
public sealed record Startup(string Primary, string Archive);

public sealed class MongoFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;
    public MongoDbSystem Primary { get; private set; } = null!;
    public MongoDbSystem Archive { get; private set; } = null!;

    public static void Configure(MongoDbOptions options, string name)
    {
        options.ConfigureClient = settings => settings.ApplicationName = "stove-contract";
        options.ConfigureExposedConfiguration = c => [new($"Mongo:{name}:ConnectionString", c.ConnectionString), new($"Mongo:{name}:Database", c.Database)];
        options.Setup.Add((c, ct) => c.Database.GetCollection<TestDocument>("records").InsertOneAsync(new(ObjectId.GenerateNewId(), "ready"), cancellationToken: ct), order: 2);
        options.Setup.Add(async (c, ct) =>
        {
            await c.Database.CreateCollectionAsync("records", cancellationToken: ct);
            await c.Database.GetCollection<TestDocument>("records").Indexes.CreateOneAsync(
                new CreateIndexModel<TestDocument>(Builders<TestDocument>.IndexKeys.Ascending(x => x.Value)), cancellationToken: ct);
        }, order: 1);
    }

    public async ValueTask InitializeAsync()
    {
        Stove = await StoveBuilder.Create().WithMongoDb("Primary", o => Configure(o, "Primary"))
            .WithMongoDb("Archive", o => Configure(o, "Archive")).WithHttpClient().WithAspNetCoreApplication<Program>()
            .StartAsync(TestContext.Current.CancellationToken);
        await Stove.Test(t => { Primary = t.MongoDb("Primary"); Archive = t.MongoDb("Archive"); return Task.CompletedTask; });
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Stove is not null) await Stove.DisposeAsync();
    }
}

public sealed class DatabaseTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    [Fact]
    public Task Application_writes_use_bson_ids_and_serialized_field_names() => fixture.Stove.Test(async t =>
    {
        var id = ObjectId.GenerateNewId();
        Assert.Equal(HttpStatusCode.Created, (await t.Http().Post("/records", new Request(id.ToString(), "İstanbul 日本語 🐘"))).StatusCode);
        await t.MongoDb("Primary").ShouldQuery<TestDocument>("records", Builders<TestDocument>.Filter.Eq(x => x.Id, id), rows => Assert.Equal("İstanbul 日本語 🐘", Assert.Single(rows).Value));
        var raw = await t.MongoDb("Primary").Collection<BsonDocument>("records").Find(new BsonDocument("_id", id)).SingleAsync(t.CancellationToken);
        Assert.True(raw["_id"].IsObjectId);
        Assert.True(raw.Contains("value"));
        Assert.False(raw.Contains("Value"));
    });

    [Fact]
    public Task Native_filters_and_module_seeds_are_read_by_application() => fixture.Stove.Test(async t =>
    {
        var id = ObjectId.GenerateNewId();
        await t.MongoDb("Primary").Insert("records", new TestDocument(id, "seed"));
        Assert.Equal("seed", (await t.Http().Get<Request>($"/records/{id}")).Body.Value);
        Assert.Equal("stove-contract", t.MongoDb("Primary").Client.Settings.ApplicationName);
    });

    [Fact]
    public Task Ordered_setup_and_indexes_are_ready_before_application_start() => fixture.Stove.Test(async t =>
    {
        Assert.Equal(new Startup("ready", "ready"), (await t.Http().Get<Startup>("/startup")).Body);
        using var indexes = await t.MongoDb("Primary").Collection<TestDocument>("records").Indexes.ListAsync(t.CancellationToken);
        Assert.Contains(await indexes.ToListAsync(t.CancellationToken), x => x["key"].AsBsonDocument.Contains("value"));
    });

    [Fact]
    public Task Named_instances_keep_identical_ids_separate() => fixture.Stove.Test(async t =>
    {
        var id = ObjectId.GenerateNewId();
        await t.MongoDb("Primary").Insert("records", new TestDocument(id, "primary"));
        await t.MongoDb("Archive").Insert("records", new TestDocument(id, "archive"));
        Assert.Equal("primary", (await t.Http().Get<Request>($"/records/{id}")).Body.Value);
        Assert.Equal("archive", (await t.Http().Get<Request>($"/archive/records/{id}")).Body.Value);
        Assert.Throws<InvalidOperationException>(() => t.MongoDb());
    });

    [Fact]
    public async Task Concurrent_scopes_use_explicit_document_isolation()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => fixture.Stove.Test(async t =>
        {
            var id = ObjectId.GenerateNewId();
            await t.Http().Post("/records", new Request(id.ToString(), $"scope-{i}"));
            if (Interlocked.Increment(ref count) == 4) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), t.CancellationToken);
            Assert.Equal($"scope-{i}", Assert.Single(await t.MongoDb("Primary").Query<TestDocument>("records", Builders<TestDocument>.Filter.Eq(x => x.Id, id))).Value);
        })));
    }

    [Fact]
    public Task Replica_set_supports_commit_and_abort() => fixture.Stove.Test(async t =>
    {
        var system = t.MongoDb("Primary");
        var committed = ObjectId.GenerateNewId();
        var aborted = ObjectId.GenerateNewId();
        using var session = await system.Client.StartSessionAsync(cancellationToken: t.CancellationToken);
        session.StartTransaction();
        await system.Collection<TestDocument>("records").InsertOneAsync(session, new(committed, "commit"), cancellationToken: t.CancellationToken);
        await session.CommitTransactionAsync(t.CancellationToken);
        session.StartTransaction();
        await system.Collection<TestDocument>("records").InsertOneAsync(session, new(aborted, "abort"), cancellationToken: t.CancellationToken);
        await session.AbortTransactionAsync(t.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await t.Http().Get($"/records/{committed}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Http().Get($"/records/{aborted}")).StatusCode);
    });

    [Fact]
    public async Task Existing_endpoint_skips_setup_and_survives_client_disposal()
    {
        var id = ObjectId.GenerateNewId();
        var builder = StoveBuilder.Create();
        foreach (var (name, system) in new[] { ("Primary", fixture.Primary), ("Archive", fixture.Archive) })
            builder.WithMongoDb(name, o =>
            {
                MongoFixture.Configure(o, name);
                o.UseExisting(system.ExposedConfiguration.ConnectionString, system.ExposedConfiguration.Database, runSetup: false);
                o.Setup.Add((_, _) => throw new InvalidOperationException("must not run"));
            });
        await using (var stove = await builder.WithHttpClient().WithAspNetCoreApplication<Program>().StartAsync(TestContext.Current.CancellationToken))
            await stove.Test(async t =>
            {
                await t.Http().Post("/records", new Request(id.ToString(), "external"));
                Assert.Single(await t.MongoDb("Primary").Query<TestDocument>("records", Builders<TestDocument>.Filter.Eq(x => x.Id, id)));
            });
        await fixture.Stove.Test(async t => Assert.Equal("external", (await t.Http().Get<Request>($"/records/{id}")).Body.Value));
    }

    [Fact]
    public async Task Cancellation_reaches_document_operations()
    {
        using var cts = new CancellationTokenSource();
        await fixture.Stove.Test(async t =>
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.MongoDb("Primary").Query<TestDocument>("records", Builders<TestDocument>.Filter.Empty));
        }, cts.Token);
    }

    [Fact]
    public async Task Readiness_runs_without_setup()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => StoveBuilder.Create().WithMongoDb(o =>
        {
            o.UseExisting("mongodb://127.0.0.1:1", "missing", runSetup: false);
            o.ConfigureClient = s => s.ServerSelectionTimeout = TimeSpan.FromMilliseconds(250);
        }).StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Standalone_mode_supports_documents_without_claiming_transactions()
    {
        await using var stove = await StoveBuilder.Create().WithMongoDb(o => o.ReplicaSet = null)
            .StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            var hello = await t.MongoDb().Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: t.CancellationToken);
            Assert.False(hello.Contains("setName"));
            var id = ObjectId.GenerateNewId();
            await t.MongoDb().Insert("records", new TestDocument(id, "standalone"));
            Assert.Single(await t.MongoDb().Query<TestDocument>("records", Builders<TestDocument>.Filter.Eq(x => x.Id, id)));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cleanup_failure_still_disposes_client_and_is_idempotent()
    {
        var calls = 0;
        MongoDbSystem system = null!;
        var stove = await StoveBuilder.Create().WithMongoDb(o =>
        {
            o.UseExisting(fixture.Primary.ExposedConfiguration.ConnectionString, "cleanup");
            o.Setup.Add((c, ct) => c.Database.GetCollection<BsonDocument>("seed").InsertOneAsync(new BsonDocument("value", "ready"), cancellationToken: ct));
            o.Cleanup = (_, _) => { calls++; throw new InvalidOperationException("cleanup failed"); };
        }).StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(t => { system = t.MongoDb(); return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<AggregateException>(() => stove.DisposeAsync().AsTask());
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "cleanup failed");
        await system.DisposeAsync();
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => system.Client.StartSessionAsync(cancellationToken: TestContext.Current.CancellationToken));
        using var client = new MongoClient(fixture.Primary.ExposedConfiguration.ConnectionString);
        Assert.Single(await client.GetDatabase("cleanup").GetCollection<BsonDocument>("seed").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_setup_keeps_original_error_and_removes_owned_container()
    {
        var original = new InvalidOperationException("setup failed");
        string connectionString = "";
        var error = await Assert.ThrowsAsync<AggregateException>(() => StoveBuilder.Create().WithMongoDb(o =>
        {
            o.Setup.Add((c, _) => { connectionString = c.Configuration.ConnectionString; throw original; });
            o.Cleanup = (_, _) => throw new InvalidOperationException("rollback failed");
        }).StartAsync(TestContext.Current.CancellationToken));
        Assert.Same(original, error.InnerExceptions[0]);
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "rollback failed");
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(500);
        using var client = new MongoClient(settings);
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetDatabase("stove").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_document_assertion_preserves_cause_and_identity()
    {
        var original = new InvalidOperationException("unexpected document");
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => fixture.Stove.Test(t =>
            t.MongoDb("Primary").ShouldQuery<TestDocument>("records", Builders<TestDocument>.Filter.Eq(x => x.Id, ObjectId.GenerateNewId()), _ => throw original)));
        Assert.Same(original, error.InnerException);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }
}
