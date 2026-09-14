using System.Net;
using StoveDotnet.Http;
using Xunit;

namespace StoveDotnet.Testing;

// These models intentionally do not come from the app: HTTP is the contract.
public sealed record RecordRequest(string Id, string Value);
public sealed record StartupResponse(string Primary, string Archive);

public abstract class RelationalApplicationTests(RelationalFixture fixture)
{
    [Fact]
    public Task Application_writes_are_visible_through_the_module() => fixture.Stove.Test(async t =>
    {
        var record = new RecordRequest(Guid.NewGuid().ToString(), "application write");
        var response = await t.Http().Post<RecordRequest>("/records", record);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(record, response.Body);
        Assert.Equal([record.Value], await fixture.Module.Read(t, record.Id));
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Module_seeding_is_visible_through_http() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        await fixture.Module.Seed(t, id, "seeded by Stove");
        var response = await t.Http().Get<RecordRequest>($"/records/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new RecordRequest(id, "seeded by Stove"), response.Body);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Real_application_reads_both_migrated_databases_at_startup() => fixture.Stove.Test(async t =>
    {
        var response = await t.Http().Get<StartupResponse>("/startup");
        Assert.Equal(new StartupResponse("ready", "ready"), response.Body);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Named_databases_route_writes_to_the_correct_instance() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await t.Http().Post("/records", new RecordRequest(id, "primary"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await t.Http().Post("/archive/records", new RecordRequest(id, "archive"))).StatusCode);
        Assert.Equal(["primary"], await fixture.Module.Read(t, id));
        Assert.Equal(["archive"], await fixture.Archive.Read(t, id));
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Existing_services_support_an_application_round_trip_and_survive_disposal()
    {
        var primary = fixture.CreateModule("Primary");
        var archive = fixture.CreateModule("Archive");
        primary.UseExisting(fixture.Module.ConnectionString, runMigrations: false);
        archive.UseExisting(fixture.Archive.ConnectionString, runMigrations: false);
        primary.Migrate("this must never run");
        var id = Guid.NewGuid().ToString();
        await using (var stove = await fixture.WithApplication(archive.RegisterPublic(primary.RegisterPublic(StoveBuilder.Create())).WithHttpClient())
            .StartAsync(TestContext.Current.CancellationToken))
        {
            await stove.Test(async t =>
            {
                Assert.Equal(HttpStatusCode.Created, (await t.Http().Post("/records", new RecordRequest(id, "external"))).StatusCode);
                Assert.Equal(["external"], await primary.Read(t, id));
            }, TestContext.Current.CancellationToken);
        }
        await fixture.Stove.Test(async t =>
            Assert.Equal("external", (await t.Http().Get<RecordRequest>($"/records/{id}")).Body.Value), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Concurrent_test_scopes_keep_their_records_separate()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => fixture.Stove.Test(async t =>
        {
            var id = Guid.NewGuid().ToString();
            var value = $"scope-{index}";
            Assert.Equal(HttpStatusCode.Created, (await t.Http().Post("/records", new RecordRequest(id, value))).StatusCode);
            if (Interlocked.Increment(ref count) == 4) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), t.CancellationToken);
            Assert.Equal([value], await fixture.Module.Read(t, id));
            Assert.Equal(value, (await t.Http().Get<RecordRequest>($"/records/{id}")).Body.Value);
        }, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Unavailable_database_prevents_the_real_application_from_starting()
    {
        var primary = fixture.CreateModule("Primary");
        var archive = fixture.CreateModule("Archive");
        primary.UseExisting(primary.MissingDatabaseConnectionString(fixture.Module.ConnectionString), runMigrations: false);
        archive.UseExisting(fixture.Archive.ConnectionString, runMigrations: false);
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() =>
            fixture.WithApplication(archive.RegisterPublic(primary.RegisterPublic(StoveBuilder.Create())).WithHttpClient())
                .StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_http_assertion_preserves_the_cause_and_test_identity()
    {
        var original = new InvalidOperationException("expected a different record");
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => fixture.Stove.Test(async t =>
        {
            Assert.Equal(HttpStatusCode.NotFound, (await t.Http().Get($"/records/{Guid.NewGuid()}")).StatusCode);
            throw original;
        }, TestContext.Current.CancellationToken));
        Assert.Same(original, error.InnerException);
        Assert.Contains(nameof(Failed_http_assertion_preserves_the_cause_and_test_identity), error.TestName, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }
}
